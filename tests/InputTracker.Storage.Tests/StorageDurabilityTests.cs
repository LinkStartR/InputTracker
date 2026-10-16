using Microsoft.Data.Sqlite;

namespace InputTracker.Storage.Tests;

public sealed class StorageDurabilityTests
{
    [Fact]
    public void DisposedUncommittedBatchRollsBackWhileCommittedBatchSurvivesReopen()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid device = Guid.NewGuid();
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock()))
        {
            database.RegisterDevice(device, kind: 1, identityConfidence: 0, createdUtcMicroseconds: 1);
        }

        WriteAggregateInTransaction(layout.DatabasePath, device, commit: false);
        Assert.Equal(0, ReadPressCount(layout.DatabasePath));

        WriteAggregateInTransaction(layout.DatabasePath, device, commit: true);
        Assert.Equal(1, ReadPressCount(layout.DatabasePath));
    }

    [Fact]
    public void BoundaryFlushPrecedesSqliteDurableWatermarkCommit()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid device = Guid.NewGuid();
        using StorageDatabase database = StorageDatabase.Open(layout, new FakeClock());
        database.RegisterDevice(device, kind: 0, identityConfidence: 0, createdUtcMicroseconds: 1);
        using var writer = new MouseSpoolWriter(System.IO.Path.Combine(layout.SpoolDirectory, "mouse.open"));
        writer.Append(new MouseSpoolRecord(1, 10, 1, -1, 0, false, 0, 0, 0));

        SpoolDurableWatermark watermark = database.CommitSpoolBoundary(device, writer, "mouse", 2);

        Assert.Equal(1, watermark.PowerDurable);
        using SqliteCommand command = database.Connection.CreateCommand();
        command.CommandText = "SELECT power_durable_count FROM mouse_spool_commits WHERE spool_id = 'mouse';";
        Assert.Equal(1L, (long)command.ExecuteScalar()!);
    }

    [Fact]
    public void FileFlushBeforeDirectoryFailureDoesNotPublishFinalPowerDurability()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        using StorageDatabase database = StorageDatabase.Open(layout, new FakeClock());
        string spoolPath = System.IO.Path.Combine(layout.SpoolDirectory, "mouse.open");
        using var writer = new MouseSpoolWriter(spoolPath);
        writer.Append(new MouseSpoolRecord(1, 10, 1, -1, 0, false, 0, 0, 0));
        SpoolDurableWatermark? published = null;

        Assert.Throws<SqliteException>(() =>
            published = database.CommitSpoolBoundary(Guid.NewGuid(), writer, "uncommitted", 2));

        Assert.Null(published);
        writer.Dispose();
        SpoolRecoveryResult recovery = MouseSpoolRecovery.Recover(spoolPath, truncateIncompleteTail: false);
        Assert.Equal(1, recovery.Recovered);
        using SqliteCommand command = database.Connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM mouse_spool_commits WHERE spool_id = 'uncommitted';";
        Assert.Equal(0L, (long)command.ExecuteScalar()!);
    }

    private static void WriteAggregateInTransaction(string path, Guid device, bool commit)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO keyboard_daily(
                device_id, physical_key, capture_local_date, time_zone_id, press_count, hold_duration_us)
            VALUES ($device, 30, '2026-08-05', 'UTC', 1, 1000);
            """;
        command.Parameters.AddWithValue("$device", device.ToString("N"));
        command.ExecuteNonQuery();
        if (commit) transaction.Commit();
    }

    private static long ReadPressCount(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(SUM(press_count), 0) FROM keyboard_daily;";
        return (long)command.ExecuteScalar()!;
    }
}
