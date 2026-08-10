using Microsoft.Data.Sqlite;

namespace InputTracker.Storage;

public sealed class StorageDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly FileStream? _writerGuard;

    private StorageDatabase(
        StorageLayout layout,
        SqliteConnection connection,
        string sqliteVersion,
        FileStream? writerGuard)
    {
        Layout = layout;
        _connection = connection;
        SqliteVersion = sqliteVersion;
        _writerGuard = writerGuard;
    }

    public StorageLayout Layout { get; }
    public string SqliteVersion { get; }
    public int SchemaVersion => ExecuteScalar<int>("PRAGMA user_version;");
    public string JournalMode => ExecuteScalar<string>("PRAGMA journal_mode;");
    public int Synchronous => ExecuteScalar<int>("PRAGMA synchronous;");
    public bool ForeignKeysEnabled => ExecuteScalar<int>("PRAGMA foreign_keys;") == 1;
    public bool SecureDeleteEnabled => ExecuteScalar<int>("PRAGMA secure_delete;") == 1;
    public int BusyTimeoutMilliseconds => ExecuteScalar<int>("PRAGMA busy_timeout;");

    public static StorageDatabase Open(StorageLayout layout, IStorageClock? clock = null) =>
        OpenCore(layout, clock, ownerLock: null);

    internal static StorageDatabase OpenOwned(
        StorageLayout layout,
        IStorageClock clock,
        FileStream ownerLock)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(ownerLock);
        string expectedLockPath = Path.GetFullPath(Path.Combine(
            layout.StorageDirectory,
            "storage-service.writer.lock"));
        if (!ownerLock.CanWrite
            || !string.Equals(Path.GetFullPath(ownerLock.Name), expectedLockPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The supplied Storage owner lock does not belong to this store.");
        }

        return OpenCore(layout, clock, ownerLock);
    }

    private static StorageDatabase OpenCore(
        StorageLayout layout,
        IStorageClock? clock,
        FileStream? ownerLock)
    {
        ArgumentNullException.ThrowIfNull(layout);
        clock ??= SystemStorageClock.Instance;
        FileStream? writerGuard = null;
        if (ownerLock is null)
        {
            string lockPath = Path.Combine(layout.StorageDirectory, "storage-service.writer.lock");
            writerGuard = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.Read,
                1,
                FileOptions.WriteThrough);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = layout.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };
        SqliteConnection? connection = null;
        try
        {
            connection = new SqliteConnection(builder.ToString());
            connection.Open();
            Configure(connection);
            ApplyMigrations(connection, clock);
            string sqliteVersion = Scalar<string>(connection, "SELECT sqlite_version();");
            return new StorageDatabase(layout, connection, sqliteVersion, writerGuard);
        }
        catch
        {
            connection?.Dispose();
            writerGuard?.Dispose();
            throw;
        }
    }

    public long RecordCalibration(TimeCalibrationSegment segment)
    {
        using SqliteTransaction transaction = _connection.BeginTransaction();
        InsertCalibration(_connection, transaction, segment);

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

    public StorageDeviceResolution ObserveDevice(
        StorageDeviceObservation observation,
        long observedUtcMicroseconds) =>
        StorageDeviceCatalog.Observe(this, observation, observedUtcMicroseconds);

    public IReadOnlyList<StorageDeviceInfo> ListDevices() => StorageDeviceCatalog.ReadAll(this);

    public StorageDeviceInfo SetDeviceAlias(Guid deviceId, string? alias) =>
        StorageDeviceCatalog.SetAlias(this, deviceId, alias);

    public void UpsertKeyboardDaily(
        Guid deviceId,
        ushort makeCode,
        bool isE0,
        bool isE1,
        DateOnly captureLocalDate,
        string timeZoneId,
        long pressCount,
        long holdDurationMicroseconds)
    {
        if (isE0 && isE1)
        {
            throw new ArgumentException("A physical key cannot use both E0 and E1 prefixes.");
        }

        int physicalKey = makeCode | (isE0 ? 1 << 16 : 0) | (isE1 ? 1 << 17 : 0);
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

    internal StorageServiceOrdinals ReadStorageServiceOrdinals() =>
        ReadStorageServiceOrdinals(_connection, transaction: null);

    internal StorageServiceOrdinals CommitStorageServiceBatch(
        IReadOnlyList<StorageKeyboardAggregateDelta> deltas,
        TimeCalibrationSegment? calibration,
        StorageLifecycleBoundary? boundary,
        IStorageServiceFaultInjector faultInjector)
    {
        ArgumentNullException.ThrowIfNull(deltas);
        ArgumentNullException.ThrowIfNull(faultInjector);
        using SqliteTransaction transaction = _connection.BeginTransaction();
        if (calibration is { } segment)
        {
            InsertCalibration(_connection, transaction, segment);
        }

        foreach (StorageKeyboardAggregateDelta delta in deltas)
        {
            if (delta.DeviceId == Guid.Empty
                || delta.IsE0 && delta.IsE1
                || delta.PressCount < 0
                || delta.HoldDurationMicroseconds < 0
                || delta.PressCount == 0 && delta.HoldDurationMicroseconds == 0)
            {
                throw new InvalidDataException("A keyboard aggregate delta is invalid.");
            }

            using SqliteCommand command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO keyboard_daily(
                    device_id, physical_key, capture_local_date, time_zone_id, press_count, hold_duration_us)
                VALUES ($device, $key, $date, $zone, $count, $duration)
                ON CONFLICT(device_id, physical_key, capture_local_date, time_zone_id)
                DO UPDATE SET
                    press_count = press_count + excluded.press_count,
                    hold_duration_us = hold_duration_us + excluded.hold_duration_us;
                """;
            command.Parameters.AddWithValue("$device", delta.DeviceId.ToString("N"));
            command.Parameters.AddWithValue("$key", PackPhysicalKey(delta.MakeCode, delta.IsE0, delta.IsE1));
            command.Parameters.AddWithValue("$date", delta.CaptureLocalDate.ToString(
                "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$zone", delta.TimeZoneId);
            command.Parameters.AddWithValue("$count", delta.PressCount);
            command.Parameters.AddWithValue("$duration", delta.HoldDurationMicroseconds);
            command.ExecuteNonQuery();
        }

        StorageServiceOrdinals current = ReadStorageServiceOrdinals(_connection, transaction);
        StorageServiceOrdinals next = current;
        if (deltas.Count > 0 || calibration is not null)
        {
            next = new StorageServiceOrdinals(
                checked(current.CommitOrdinal + 1),
                deltas.Count > 0
                    ? checked(current.KeyboardAggregateCommitOrdinal + 1)
                    : current.KeyboardAggregateCommitOrdinal);
            WriteStorageServiceOrdinals(_connection, transaction, next);
        }

        faultInjector.BeforeStorageCommit(boundary, deltas.Count);
        transaction.Commit();
        return next;
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

    public void Dispose()
    {
        try
        {
            _connection.Dispose();
        }
        finally
        {
            _writerGuard?.Dispose();
        }
    }

    private T ExecuteScalar<T>(string commandText) => Scalar<T>(_connection, commandText);

    private static int PackPhysicalKey(ushort makeCode, bool isE0, bool isE1) =>
        makeCode | (isE0 ? 1 << 16 : 0) | (isE1 ? 1 << 17 : 0);

    private static void InsertCalibration(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TimeCalibrationSegment segment)
    {
        using (SqliteCommand session = connection.CreateCommand())
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

        using (SqliteCommand persistedSession = connection.CreateCommand())
        {
            persistedSession.Transaction = transaction;
            persistedSession.CommandText = "SELECT frequency FROM clock_sessions WHERE session_id = $session;";
            persistedSession.Parameters.AddWithValue("$session", segment.SessionId.ToString("N"));
            object? stored = persistedSession.ExecuteScalar();
            if (stored is not long storedFrequency || storedFrequency != segment.Frequency)
            {
                throw new InvalidDataException("A clock session identifier has a conflicting frequency.");
            }
        }

        using SqliteCommand calibration = connection.CreateCommand();
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

    private static StorageServiceOrdinals ReadStorageServiceOrdinals(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        const string commitKey = "storage.service.commit-ordinal";
        const string keyboardKey = "storage.service.keyboard-aggregate-commit-ordinal";
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT key, value
            FROM store_metadata
            WHERE key IN ('storage.service.commit-ordinal', 'storage.service.keyboard-aggregate-commit-ordinal')
            ORDER BY key;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            values.Add(reader.GetString(0), reader.GetString(1));
        }

        if (values.Count == 0)
        {
            return new StorageServiceOrdinals(0, 0);
        }

        if (values.Count != 2
            || !values.TryGetValue(commitKey, out string? commitText)
            || !values.TryGetValue(keyboardKey, out string? keyboardText)
            || !long.TryParse(commitText, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out long commit)
            || !long.TryParse(keyboardText, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out long keyboard)
            || commit < 0
            || keyboard < 0
            || keyboard > commit)
        {
            throw new InvalidDataException("Storage service watermark metadata is missing or invalid.");
        }

        return new StorageServiceOrdinals(commit, keyboard);
    }

    private static void WriteStorageServiceOrdinals(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StorageServiceOrdinals ordinals)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO store_metadata(key, value)
            VALUES
                ('storage.service.commit-ordinal', $commit),
                ('storage.service.keyboard-aggregate-commit-ordinal', $keyboard)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$commit", ordinals.CommitOrdinal.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$keyboard", ordinals.KeyboardAggregateCommitOrdinal.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

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
        if (version == 2)
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            Execute(connection, StorageSchema.Version3Sql, transaction);
            using SqliteCommand record = connection.CreateCommand();
            record.Transaction = transaction;
            record.CommandText = """
                INSERT INTO schema_migrations(version, name, checksum, applied_utc_us)
                VALUES (3, 'storage-service-contracts', $checksum, $applied);
                """;
            record.Parameters.AddWithValue("$checksum", StorageSchema.Version3Checksum);
            record.Parameters.AddWithValue("$applied", ToUnixMicroseconds(clock.GetUtcNow()));
            record.ExecuteNonQuery();
            transaction.Commit();
            version = 3;
        }

        VerifyMigrationChecksum(connection, 3, StorageSchema.Version3Checksum);
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
