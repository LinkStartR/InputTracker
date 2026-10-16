using Microsoft.Data.Sqlite;

namespace InputTracker.Storage.Tests;

public sealed class StorageDatabaseTests
{
    [Fact]
    public void OpenCreatesProgramLocalWalDatabaseAndCurrentSchema()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);

        using StorageDatabase database = StorageDatabase.Open(layout, new FakeClock());

        Assert.StartsWith(directory.Path, database.Layout.DatabasePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(StorageSchema.CurrentVersion, database.SchemaVersion);
        Assert.Equal("wal", database.JournalMode, ignoreCase: true);
        Assert.Equal(2, database.Synchronous);
        Assert.True(database.ForeignKeysEnabled);
        Assert.True(database.SecureDeleteEnabled);
        Assert.Equal(5000, database.BusyTimeoutMilliseconds);
        Assert.NotEmpty(database.SqliteVersion);
    }

    [Fact]
    public void FutureSchemaVersionFailsClosed()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        using (var connection = new SqliteConnection($"Data Source={layout.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 8;";
            command.ExecuteNonQuery();
        }

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => StorageDatabase.Open(layout, new FakeClock()));
        Assert.Contains("newer", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MigrationChecksumMismatchFailsClosed()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock())) { }

        using (var connection = new SqliteConnection($"Data Source={layout.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE schema_migrations SET checksum = 'tampered' WHERE version = 1;";
            command.ExecuteNonQuery();
        }

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => StorageDatabase.Open(layout, new FakeClock()));
        Assert.Contains("checksum", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VersionSixChecksumMismatchFailsClosed()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock())) { }

        using (var connection = new SqliteConnection($"Data Source={layout.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE schema_migrations SET checksum = 'tampered' WHERE version = 6;";
            command.ExecuteNonQuery();
        }

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => StorageDatabase.Open(layout, new FakeClock()));
        Assert.Contains("migration 6", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("checksum", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VersionOneDatabaseMigratesToCurrentVersionWithRecordedChecksums()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        using (var connection = new SqliteConnection($"Data Source={layout.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = StorageSchema.Version1Sql;
            command.ExecuteNonQuery();
            command.CommandText = """
                INSERT INTO schema_migrations(version, name, checksum, applied_utc_us)
                VALUES (1, 'initial-storage-schema', $checksum, 1);
                """;
            command.Parameters.AddWithValue("$checksum", StorageSchema.Version1Checksum);
            command.ExecuteNonQuery();
        }

        using StorageDatabase database = StorageDatabase.Open(layout, new FakeClock());

        Assert.Equal(StorageSchema.CurrentVersion, database.SchemaVersion);
        using SqliteCommand checksum = database.Connection.CreateCommand();
        checksum.CommandText = "SELECT checksum FROM schema_migrations WHERE version = 2;";
        Assert.Equal(StorageSchema.Version2Checksum, (string)checksum.ExecuteScalar()!);
        checksum.CommandText = "SELECT checksum FROM schema_migrations WHERE version = 3;";
        Assert.Equal(StorageSchema.Version3Checksum, (string)checksum.ExecuteScalar()!);
        checksum.CommandText = "SELECT checksum FROM schema_migrations WHERE version = 4;";
        Assert.Equal(StorageSchema.Version4Checksum, (string)checksum.ExecuteScalar()!);
        checksum.CommandText = "SELECT checksum FROM schema_migrations WHERE version = 5;";
        Assert.Equal(StorageSchema.Version5Checksum, (string)checksum.ExecuteScalar()!);
        checksum.CommandText = "SELECT checksum FROM schema_migrations WHERE version = 6;";
        Assert.Equal(StorageSchema.Version6Checksum, (string)checksum.ExecuteScalar()!);
        checksum.CommandText = "SELECT checksum FROM schema_migrations WHERE version = 7;";
        Assert.Equal(StorageSchema.Version7Checksum, (string)checksum.ExecuteScalar()!);
        Assert.Contains("mouse_chunks", database.ReadSchemaColumns().Keys);
        Assert.Contains("device_identity_mappings", database.ReadSchemaColumns().Keys);
        Assert.Contains("mouse_spool_segments", database.ReadSchemaColumns().Keys);
        Assert.Contains("mouse_device_watermarks", database.ReadSchemaColumns().Keys);
        Assert.Contains("mouse_button_daily", database.ReadSchemaColumns().Keys);
        Assert.Contains("mouse_button_aggregated_segments", database.ReadSchemaColumns().Keys);
        Assert.Contains("gamepad_configuration_revisions", database.ReadSchemaColumns().Keys);
    }

    [Fact]
    public void VersionTwoDatabaseMigratesToVersionThreeWithoutReplacingExistingTables()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        using (var connection = new SqliteConnection($"Data Source={layout.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = StorageSchema.Version1Sql;
            command.ExecuteNonQuery();
            command.CommandText = """
                INSERT INTO schema_migrations(version, name, checksum, applied_utc_us)
                VALUES (1, 'initial-storage-schema', $checksum, 1);
                """;
            command.Parameters.AddWithValue("$checksum", StorageSchema.Version1Checksum);
            command.ExecuteNonQuery();
            command.Parameters.Clear();
            command.CommandText = StorageSchema.Version2Sql;
            command.ExecuteNonQuery();
            command.CommandText = """
                INSERT INTO schema_migrations(version, name, checksum, applied_utc_us)
                VALUES (2, 'mouse-chunk-catalog', $checksum, 2);
                """;
            command.Parameters.AddWithValue("$checksum", StorageSchema.Version2Checksum);
            command.ExecuteNonQuery();
        }

        using StorageDatabase database = StorageDatabase.Open(layout, new FakeClock());

        Assert.Equal(StorageSchema.CurrentVersion, database.SchemaVersion);
        Assert.Contains("device_identity_mappings", database.ReadSchemaColumns().Keys);
        using SqliteCommand checksum = database.Connection.CreateCommand();
        checksum.CommandText = "SELECT checksum FROM schema_migrations WHERE version = 3;";
        Assert.Equal(StorageSchema.Version3Checksum, (string)checksum.ExecuteScalar()!);
    }

    [Fact]
    public void VersionThreeDatabaseMigratesToVersionFourWithCompleteObjectsAndChecksum()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock())) { }

        using var connection = new SqliteConnection($"Data Source={layout.DatabasePath};Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT checksum FROM schema_migrations WHERE version = 4;";
        Assert.Equal(StorageSchema.Version4Checksum, (string)command.ExecuteScalar()!);
        command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name IN ('mouse_spool_segments', 'mouse_device_watermarks');";
        Assert.Equal(2L, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public void VersionFourChecksumMismatchFailsClosed()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock())) { }
        using (var connection = new SqliteConnection($"Data Source={layout.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE schema_migrations SET checksum = 'tampered' WHERE version = 4;";
            command.ExecuteNonQuery();
        }

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => StorageDatabase.Open(layout, new FakeClock()));
        Assert.Contains("migration 4", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("checksum", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("DELETE FROM store_metadata WHERE key = 'mouse.v3-adoption-complete';")]
    [InlineData("UPDATE store_metadata SET value = 'invalid' WHERE key = 'mouse.v3-adoption-complete';")]
    public void VersionFourMissingOrInvalidAdoptionMarkerFailsClosed(string mutation)
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock())) { }
        using (var connection = new SqliteConnection($"Data Source={layout.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = mutation;
            command.ExecuteNonQuery();
        }

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            StorageDatabase.Open(layout, new FakeClock()));
        Assert.Contains("shape", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VersionFourWrongIndexShapeFailsClosedWithValidVersionAndChecksum()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock())) { }
        using (var connection = new SqliteConnection($"Data Source={layout.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                DROP INDEX mouse_spool_segments_device_sequence;
                CREATE INDEX mouse_spool_segments_device_sequence
                    ON mouse_spool_segments(device_id, last_sequence, first_sequence);
                """;
            command.ExecuteNonQuery();
        }

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            StorageDatabase.Open(layout, new FakeClock()));
        Assert.Contains("shape", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PartialVersionFourObjectsFailClosedWithoutChangingVersionThree()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock())) { }
        using (var connection = new SqliteConnection($"Data Source={layout.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DROP TABLE mouse_device_watermarks; DELETE FROM store_metadata WHERE key = 'mouse.v3-adoption-complete'; PRAGMA user_version = 3; DELETE FROM schema_migrations WHERE version = 4;";
            command.ExecuteNonQuery();
        }

        Assert.ThrowsAny<Exception>(() => StorageDatabase.Open(layout, new FakeClock()));
        using var verify = new SqliteConnection($"Data Source={layout.DatabasePath};Pooling=False");
        verify.Open();
        using SqliteCommand version = verify.CreateCommand();
        version.CommandText = "PRAGMA user_version;";
        Assert.Equal(3L, (long)version.ExecuteScalar()!);
    }

    [Fact]
    public void MouseSpoolPrefixTransactionAdvancesDeviceAndGlobalWatermarksAcrossCalibrations()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        var clock = new FakeClock();
        using StorageDatabase database = StorageDatabase.Open(layout, clock);
        Guid deviceId = Guid.NewGuid();
        database.RegisterDevice(deviceId, kind: 0, identityConfidence: 0, createdUtcMicroseconds: 1);
        Guid firstSession = Guid.NewGuid();
        long firstCalibration = database.RecordCalibration(TimeCalibrationSegment.Capture(firstSession, clock));
        MouseSpoolSegment first = CreateSpoolSegment(layout, deviceId, firstSession, firstCalibration, 1, 2);

        StorageMousePrefixCommit firstCommit = database.CommitMouseSpoolSegment(first, 10);

        Guid secondSession = Guid.NewGuid();
        long secondCalibration = database.RecordCalibration(TimeCalibrationSegment.Capture(secondSession, clock));
        MouseSpoolSegment second = CreateSpoolSegment(layout, deviceId, secondSession, secondCalibration, 3, 2);
        StorageMousePrefixCommit secondCommit = database.CommitMouseSpoolSegment(second, 11);

        Assert.Equal(2, firstCommit.DeviceSequence);
        Assert.Equal(4, secondCommit.DeviceSequence);
        Assert.Equal(1, firstCommit.CommitOrdinal);
        Assert.Equal(2, secondCommit.CommitOrdinal);
        Assert.Equal(0, secondCommit.KeyboardAggregateCommitOrdinal);
        Assert.Equal(4, database.ReadMouseDeviceWatermarks()[deviceId]);
        using SqliteCommand rows = database.Connection.CreateCommand();
        rows.CommandText = "SELECT COUNT(*), MIN(first_sequence), MAX(last_sequence) FROM mouse_spool_segments WHERE device_id = $device;";
        rows.Parameters.AddWithValue("$device", deviceId.ToString("N"));
        using SqliteDataReader reader = rows.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal(2, reader.GetInt64(0));
        Assert.Equal(1, reader.GetInt64(1));
        Assert.Equal(4, reader.GetInt64(2));
    }

    [Fact]
    public void MouseSpoolDirectoryFailurePublishesNoWatermarkOrGlobalOrdinal()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        var clock = new FakeClock();
        using StorageDatabase database = StorageDatabase.Open(layout, clock);
        Guid deviceId = Guid.NewGuid();
        database.RegisterDevice(deviceId, kind: 0, identityConfidence: 0, createdUtcMicroseconds: 1);
        Guid session = Guid.NewGuid();
        long calibration = database.RecordCalibration(TimeCalibrationSegment.Capture(session, clock));
        MouseSpoolSegment segment = CreateSpoolSegment(layout, deviceId, session, calibration, 1, 1);
        using (SqliteCommand trigger = database.Connection.CreateCommand())
        {
            trigger.CommandText = """
                CREATE TRIGGER fail_spool_directory_insert
                BEFORE INSERT ON mouse_spool_segments
                BEGIN SELECT RAISE(ABORT, 'test spool directory failure'); END;
                """;
            trigger.ExecuteNonQuery();
        }

        Assert.Throws<SqliteException>(() => database.CommitMouseSpoolSegment(segment, 10));

        Assert.Empty(database.ReadMouseDeviceWatermarks());
        Assert.Equal(new StorageServiceOrdinals(0, 0), database.ReadStorageServiceOrdinals());
        using SqliteCommand rows = database.Connection.CreateCommand();
        rows.CommandText = "SELECT COUNT(*) FROM mouse_spool_segments;";
        Assert.Equal(0L, (long)rows.ExecuteScalar()!);
        Assert.True(File.Exists(segment.Path));
    }

    [Fact]
    public void VersionTwoLegacyKeyboardAggregateMigratesAndUpsertsWithoutRowSplit()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid deviceId = Guid.NewGuid();
        const int physicalKey = 30 | (1 << 16);
        using (var connection = new SqliteConnection($"Data Source={layout.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = StorageSchema.Version1Sql;
            command.ExecuteNonQuery();
            command.CommandText = """
                INSERT INTO schema_migrations(version, name, checksum, applied_utc_us)
                VALUES (1, 'initial-storage-schema', $checksum, 1);
                """;
            command.Parameters.AddWithValue("$checksum", StorageSchema.Version1Checksum);
            command.ExecuteNonQuery();
            command.Parameters.Clear();
            command.CommandText = StorageSchema.Version2Sql;
            command.ExecuteNonQuery();
            command.CommandText = """
                INSERT INTO schema_migrations(version, name, checksum, applied_utc_us)
                VALUES (2, 'mouse-chunk-catalog', $checksum, 2);
                """;
            command.Parameters.AddWithValue("$checksum", StorageSchema.Version2Checksum);
            command.ExecuteNonQuery();
            command.Parameters.Clear();
            command.CommandText = """
                INSERT INTO devices(device_id, kind, identity_confidence, created_utc_us)
                VALUES ($device, 1, 0, 3);
                INSERT INTO keyboard_daily(
                    device_id, physical_key, capture_local_date, time_zone_id, press_count, hold_duration_us)
                VALUES ($device, $key, '2026-11-01', 'Pacific Standard Time', 4, 5000);
                """;
            command.Parameters.AddWithValue("$device", deviceId.ToString("N"));
            command.Parameters.AddWithValue("$key", physicalKey);
            command.ExecuteNonQuery();
        }

        using StorageDatabase database = StorageDatabase.Open(layout, new FakeClock());
        Assert.Equal(StorageSchema.CurrentVersion, database.SchemaVersion);
        using (SqliteCommand migrated = database.Connection.CreateCommand())
        {
            migrated.CommandText = """
                SELECT make_code, is_e0, is_e1, press_count, hold_duration_us
                FROM keyboard_daily
                WHERE device_id = $device;
                """;
            migrated.Parameters.AddWithValue("$device", deviceId.ToString("N"));
            using SqliteDataReader reader = migrated.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(30, reader.GetInt32(0));
            Assert.Equal(1, reader.GetInt32(1));
            Assert.Equal(0, reader.GetInt32(2));
            Assert.Equal(4, reader.GetInt64(3));
            Assert.Equal(5000, reader.GetInt64(4));
            Assert.False(reader.Read());
        }

        database.UpsertKeyboardDaily(
            deviceId,
            makeCode: 30,
            isE0: true,
            isE1: false,
            captureLocalDate: new DateOnly(2026, 11, 1),
            timeZoneId: "Pacific Standard Time",
            pressCount: 2,
            holdDurationMicroseconds: 3000);

        using SqliteCommand accumulated = database.Connection.CreateCommand();
        accumulated.CommandText = """
            SELECT COUNT(*), SUM(press_count), SUM(hold_duration_us)
            FROM keyboard_daily
            WHERE device_id = $device AND make_code = 30 AND is_e0 = 1 AND is_e1 = 0;
            """;
        accumulated.Parameters.AddWithValue("$device", deviceId.ToString("N"));
        using SqliteDataReader accumulatedReader = accumulated.ExecuteReader();
        Assert.True(accumulatedReader.Read());
        Assert.Equal(1, accumulatedReader.GetInt64(0));
        Assert.Equal(6, accumulatedReader.GetInt64(1));
        Assert.Equal(8000, accumulatedReader.GetInt64(2));
    }

    [Fact]
    public void VersionTwoChecksumMismatchFailsClosed()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock())) { }
        using (var connection = new SqliteConnection($"Data Source={layout.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE schema_migrations SET checksum = 'tampered' WHERE version = 2;";
            command.ExecuteNonQuery();
        }

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => StorageDatabase.Open(layout, new FakeClock()));
        Assert.Contains("migration 2", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("checksum", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VersionThreeChecksumMismatchFailsClosed()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock())) { }
        using (var connection = new SqliteConnection($"Data Source={layout.DatabasePath};Pooling=False"))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE schema_migrations SET checksum = 'tampered' WHERE version = 3;";
            command.ExecuteNonQuery();
        }

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => StorageDatabase.Open(layout, new FakeClock()));
        Assert.Contains("migration 3", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("checksum", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MouseChunkSchemaContainsOnlyStorageIdentityRangeAndIntegrityFields()
    {
        using var directory = new TemporaryDirectory();
        using StorageDatabase database = StorageDatabase.Open(StorageLayout.Create(directory.Path), new FakeClock());

        IReadOnlyList<string> columns = database.ReadSchemaColumns()["mouse_chunks"];

        Assert.Equal(
            [
                "chunk_id", "device_id", "calibration_segment_id", "relative_path",
                "first_sequence", "last_sequence", "first_monotonic_timestamp", "last_monotonic_timestamp",
                "event_count", "canonical_length", "compressed_length", "file_length",
                "compressed_crc32c", "canonical_sha256", "created_utc_us", "recovered"
            ],
            columns);
        string joined = string.Join('|', columns);
        foreach (string forbidden in new[] { "interface", "alias", "window", "process", "keyboard", "physical_key", "time_zone_id" })
        {
            Assert.DoesNotContain(forbidden, joined, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void KeyboardSchemaContainsOnlyAggregateFields()
    {
        using var directory = new TemporaryDirectory();
        using StorageDatabase database = StorageDatabase.Open(StorageLayout.Create(directory.Path), new FakeClock());

        IReadOnlyList<string> columns = database.ReadSchemaColumns()["keyboard_daily"];

        Assert.Equal(
            ["device_id", "physical_key", "capture_local_date", "time_zone_id", "press_count", "hold_duration_us"],
            columns);
        string schema = string.Join('|', columns);
        foreach (string forbidden in new[] { "text", "character", "vkey", "window", "process", "focus", "sequence", "key_down", "key_up", "event_timestamp" })
        {
            Assert.DoesNotContain(forbidden, schema, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void CalibrationMapsMonotonicTicksWithoutTreatingThemAsUtcTicks()
    {
        var clock = new FakeClock
        {
            Frequency = 10_000_000,
            Timestamp = 9_000_000_000,
            UtcNow = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero)
        };

        TimeCalibrationSegment segment = TimeCalibrationSegment.Capture(Guid.NewGuid(), clock);
        long oneSecondLater = segment.ToUtcMicroseconds(segment.MonotonicAnchor + clock.Frequency);

        Assert.Equal(1_785_931_201_000_000L, oneSecondLater);
        Assert.NotEqual(segment.MonotonicAnchor, segment.UtcAnchorMicroseconds);
    }

    private static MouseSpoolSegment CreateSpoolSegment(
        StorageLayout layout,
        Guid deviceId,
        Guid calibrationSessionId,
        long calibrationSegmentId,
        long firstSequence,
        int count)
    {
        Guid segmentId = Guid.NewGuid();
        string path = System.IO.Path.Combine(layout.SpoolDirectory, segmentId.ToString("N") + ".mspool");
        var header = new MouseSpoolHeader(
            segmentId, deviceId, calibrationSessionId, calibrationSegmentId, firstSequence);
        using (var writer = new MouseSpoolWriter(path, header))
        {
            for (int index = 0; index < count; index++)
            {
                long sequence = firstSequence + index;
                writer.Append(new MouseSpoolRecord(sequence, sequence, index, -index, 0, false, 0, 0, 0));
            }

            writer.FlushFile();
        }

        return MouseSpoolSegmentReader.Read(path);
    }
}

internal sealed class FakeClock : IStorageClock
{
    public long Frequency { get; set; } = 10_000_000;
    public long Timestamp { get; set; } = 123_456_789;
    public DateTimeOffset UtcNow { get; set; } = new(2026, 8, 5, 0, 0, 0, TimeSpan.Zero);
    public string TimeZoneId { get; set; } = "Test/Zone";

    public long GetTimestamp() => Timestamp++;
    public DateTimeOffset GetUtcNow() => UtcNow;
    public string GetTimeZoneId() => TimeZoneId;
}

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "InputTracker.Storage.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }
}
