using Microsoft.Data.Sqlite;

namespace InputTracker.Storage.Tests;

public sealed class StorageDatabaseTests
{
    [Fact]
    public void OpenCreatesProgramLocalWalDatabaseAndVersionOneSchema()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);

        using StorageDatabase database = StorageDatabase.Open(layout, new FakeClock());

        Assert.StartsWith(directory.Path, database.Layout.DatabasePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, database.SchemaVersion);
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
            command.CommandText = "PRAGMA user_version = 2;";
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
    public void KeyboardSchemaContainsOnlyAggregateFields()
    {
        using var directory = new TemporaryDirectory();
        using StorageDatabase database = StorageDatabase.Open(StorageLayout.Create(directory.Path), new FakeClock());

        IReadOnlyList<string> columns = database.ReadSchemaColumns()["keyboard_daily"];

        Assert.Equal(
            ["device_id", "physical_key", "capture_local_date", "time_zone_id", "press_count", "hold_duration_us"],
            columns);
        string schema = string.Join('|', database.ReadSchemaColumns().SelectMany(item => item.Value));
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
