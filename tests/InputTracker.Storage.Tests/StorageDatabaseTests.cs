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
        Assert.Equal(2, database.SchemaVersion);
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
            command.CommandText = "PRAGMA user_version = 3;";
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
    public void VersionOneDatabaseMigratesToVersionTwoWithRecordedChecksum()
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

        Assert.Equal(2, database.SchemaVersion);
        using SqliteCommand checksum = database.Connection.CreateCommand();
        checksum.CommandText = "SELECT checksum FROM schema_migrations WHERE version = 2;";
        Assert.Equal(StorageSchema.Version2Checksum, (string)checksum.ExecuteScalar()!);
        Assert.Contains("mouse_chunks", database.ReadSchemaColumns().Keys);
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
