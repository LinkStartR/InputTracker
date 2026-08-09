using Microsoft.Data.Sqlite;

namespace InputTracker.Storage;

public sealed class StorageDatabase : IDisposable
{
    private readonly SqliteConnection _connection;

    private StorageDatabase(StorageLayout layout, SqliteConnection connection, string sqliteVersion)
    {
        Layout = layout;
        _connection = connection;
        SqliteVersion = sqliteVersion;
    }

    public StorageLayout Layout { get; }
    public string SqliteVersion { get; }
    public int SchemaVersion => ExecuteScalar<int>("PRAGMA user_version;");
    public string JournalMode => ExecuteScalar<string>("PRAGMA journal_mode;");
    public int Synchronous => ExecuteScalar<int>("PRAGMA synchronous;");
    public bool ForeignKeysEnabled => ExecuteScalar<int>("PRAGMA foreign_keys;") == 1;
    public bool SecureDeleteEnabled => ExecuteScalar<int>("PRAGMA secure_delete;") == 1;
    public int BusyTimeoutMilliseconds => ExecuteScalar<int>("PRAGMA busy_timeout;");

    public static StorageDatabase Open(StorageLayout layout, IStorageClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        clock ??= SystemStorageClock.Instance;
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = layout.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        try
        {
            Configure(connection);
            ApplyMigrations(connection, clock);
            string sqliteVersion = Scalar<string>(connection, "SELECT sqlite_version();");
            return new StorageDatabase(layout, connection, sqliteVersion);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public long RecordCalibration(TimeCalibrationSegment segment)
    {
        using SqliteTransaction transaction = _connection.BeginTransaction();
        using (SqliteCommand session = _connection.CreateCommand())
        {
            session.Transaction = transaction;
            session.CommandText = """
                INSERT OR IGNORE INTO clock_sessions(session_id, frequency, created_utc_us)
                VALUES ($session, $frequency, $created);
                """;
            session.Parameters.AddWithValue("$session", segment.SessionId.ToString("N"));
            session.Parameters.AddWithValue("$frequency", segment.Frequency);
            session.Parameters.AddWithValue("$created", segment.UtcAnchorMicroseconds);
            session.ExecuteNonQuery();
        }

        using (SqliteCommand calibration = _connection.CreateCommand())
        {
            calibration.Transaction = transaction;
            calibration.CommandText = """
            INSERT INTO time_calibration_segments(
                session_id, monotonic_anchor, utc_anchor_us, uncertainty_ticks, time_zone_id)
            VALUES ($session, $mono, $utc, $uncertainty, $zone);
            """;
            calibration.Parameters.AddWithValue("$session", segment.SessionId.ToString("N"));
            calibration.Parameters.AddWithValue("$mono", segment.MonotonicAnchor);
            calibration.Parameters.AddWithValue("$utc", segment.UtcAnchorMicroseconds);
            calibration.Parameters.AddWithValue("$uncertainty", segment.SamplingUncertaintyTicks);
            calibration.Parameters.AddWithValue("$zone", segment.TimeZoneId);
            calibration.ExecuteNonQuery();
        }

        long id = Scalar<long>(_connection, "SELECT last_insert_rowid();", transaction);
        transaction.Commit();
        return id;
    }

    public void RegisterDevice(Guid deviceId, int kind, int identityConfidence, long createdUtcMicroseconds)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO devices(device_id, kind, identity_confidence, created_utc_us)
            VALUES ($device, $kind, $confidence, $created);
            """;
        command.Parameters.AddWithValue("$device", deviceId.ToString("N"));
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$confidence", identityConfidence);
        command.Parameters.AddWithValue("$created", createdUtcMicroseconds);
        command.ExecuteNonQuery();
    }

    public void UpsertKeyboardDaily(
        Guid deviceId,
        int physicalKey,
        DateOnly captureLocalDate,
        string timeZoneId,
        long pressCount,
        long holdDurationMicroseconds)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO keyboard_daily(
                device_id, physical_key, capture_local_date, time_zone_id, press_count, hold_duration_us)
            VALUES ($device, $key, $date, $zone, $count, $duration)
            ON CONFLICT(device_id, physical_key, capture_local_date, time_zone_id)
            DO UPDATE SET
                press_count = press_count + excluded.press_count,
                hold_duration_us = hold_duration_us + excluded.hold_duration_us;
            """;
        command.Parameters.AddWithValue("$device", deviceId.ToString("N"));
        command.Parameters.AddWithValue("$key", physicalKey);
        command.Parameters.AddWithValue("$date", captureLocalDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$zone", timeZoneId);
        command.Parameters.AddWithValue("$count", pressCount);
        command.Parameters.AddWithValue("$duration", holdDurationMicroseconds);
        command.ExecuteNonQuery();
    }

    public SpoolDurableWatermark CommitSpoolBoundary(
        Guid deviceId,
        MouseSpoolWriter writer,
        string spoolId,
        long updatedUtcMicroseconds)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentException.ThrowIfNullOrWhiteSpace(spoolId);
        SpoolFlushResult flush = writer.FlushFile();
        using SqliteTransaction transaction = _connection.BeginTransaction();
        using SqliteCommand command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO mouse_spool_commits(
                spool_id, device_id, committed_length, record_count, power_durable_count, updated_utc_us)
            VALUES ($spool, $device, $length, $records, $durable, $updated)
            ON CONFLICT(spool_id) DO UPDATE SET
                committed_length = excluded.committed_length,
                record_count = excluded.record_count,
                power_durable_count = excluded.power_durable_count,
                updated_utc_us = excluded.updated_utc_us;
            """;
        command.Parameters.AddWithValue("$spool", spoolId);
        command.Parameters.AddWithValue("$device", deviceId.ToString("N"));
        command.Parameters.AddWithValue("$length", flush.FlushedLength);
        command.Parameters.AddWithValue("$records", flush.FileFlushedCount);
        command.Parameters.AddWithValue("$durable", flush.FileFlushedCount);
        command.Parameters.AddWithValue("$updated", updatedUtcMicroseconds);
        command.ExecuteNonQuery();
        transaction.Commit();
        return new SpoolDurableWatermark(
            spoolId,
            flush.FileFlushedCount,
            flush.FileFlushedCount,
            flush.FlushedLength);
    }

    public MouseChunkDurableWatermark CommitMouseChunk(
        Guid chunkId,
        Guid deviceId,
        long calibrationSegmentId,
        IReadOnlyList<MouseSpoolRecord> records,
        long createdUtcMicroseconds) => MouseChunkCatalog.Commit(
            this,
            chunkId,
            deviceId,
            calibrationSegmentId,
            records,
            createdUtcMicroseconds);

    public IReadOnlyList<MouseChunkCatalogEntry> ReadMouseChunkCatalog() => MouseChunkCatalog.ReadAll(this);

    public MouseChunkRecoveryReport RecoverMouseChunks(long recoveredUtcMicroseconds) =>
        MouseChunkCatalog.Recover(this, recoveredUtcMicroseconds);

    internal SqliteConnection Connection => _connection;

    public IReadOnlyDictionary<string, IReadOnlyList<string>> ReadSchemaColumns()
    {
        var result = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        using SqliteCommand tables = _connection.CreateCommand();
        tables.CommandText = "SELECT name FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        using SqliteDataReader reader = tables.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        reader.Close();

        foreach (string name in names)
        {
            using SqliteCommand columns = _connection.CreateCommand();
            columns.CommandText = $"PRAGMA table_info([{name.Replace("]", "]]", StringComparison.Ordinal)}]);";
            using SqliteDataReader columnReader = columns.ExecuteReader();
            var values = new List<string>();
            while (columnReader.Read()) values.Add(columnReader.GetString(1));
            result.Add(name, values);
        }

        return result;
    }

    public void Dispose() => _connection.Dispose();

    private T ExecuteScalar<T>(string commandText) => Scalar<T>(_connection, commandText);

    private static void Configure(SqliteConnection connection)
    {
        string journal = Scalar<string>(connection, "PRAGMA journal_mode = WAL;");
        if (!string.Equals(journal, "wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"SQLite refused WAL mode and returned '{journal}'.");
        }

        Execute(connection, """
            PRAGMA synchronous = FULL;
            PRAGMA foreign_keys = ON;
            PRAGMA secure_delete = ON;
            PRAGMA busy_timeout = 5000;
            """);
    }

    private static void ApplyMigrations(SqliteConnection connection, IStorageClock clock)
    {
        int version = Scalar<int>(connection, "PRAGMA user_version;");
        if (version > StorageSchema.CurrentVersion)
        {
            throw new InvalidDataException($"Storage schema version {version} is newer than supported version {StorageSchema.CurrentVersion}.");
        }

        if (version == 0)
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            Execute(connection, StorageSchema.Version1Sql, transaction);
            using SqliteCommand record = connection.CreateCommand();
            record.Transaction = transaction;
            record.CommandText = """
                INSERT INTO schema_migrations(version, name, checksum, applied_utc_us)
                VALUES (1, 'initial-storage-schema', $checksum, $applied);
                """;
            record.Parameters.AddWithValue("$checksum", StorageSchema.Version1Checksum);
            record.Parameters.AddWithValue("$applied", ToUnixMicroseconds(clock.GetUtcNow()));
            record.ExecuteNonQuery();
            transaction.Commit();
            version = 1;
        }

        VerifyMigrationChecksum(connection, 1, StorageSchema.Version1Checksum);
        if (version == 1)
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            Execute(connection, StorageSchema.Version2Sql, transaction);
            using SqliteCommand record = connection.CreateCommand();
            record.Transaction = transaction;
            record.CommandText = """
                INSERT INTO schema_migrations(version, name, checksum, applied_utc_us)
                VALUES (2, 'mouse-chunk-catalog', $checksum, $applied);
                """;
            record.Parameters.AddWithValue("$checksum", StorageSchema.Version2Checksum);
            record.Parameters.AddWithValue("$applied", ToUnixMicroseconds(clock.GetUtcNow()));
            record.ExecuteNonQuery();
            transaction.Commit();
            version = 2;
        }

        VerifyMigrationChecksum(connection, 2, StorageSchema.Version2Checksum);
    }

    private static void VerifyMigrationChecksum(SqliteConnection connection, int version, string expected)
    {
        string actual = Scalar<string>(connection, $"SELECT checksum FROM schema_migrations WHERE version = {version};");
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Storage schema migration {version} checksum does not match the compiled migration.");
        }
    }

    private static long ToUnixMicroseconds(DateTimeOffset value) =>
        checked((value.UtcDateTime.Ticks - 621355968000000000L) / 10);

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static T Scalar<T>(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        object? value = command.ExecuteScalar();
        if (value is null or DBNull)
        {
            throw new InvalidDataException($"SQLite command returned no scalar value: {sql}");
        }

        return (T)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }
}
