using Microsoft.Data.Sqlite;

namespace InputTracker.Storage.Tests;

public sealed class StorageServiceLifecycleTests
{
    [Fact]
    public void SameStoreHasOneProcessAndFileOwnerAndRetainsLockFile()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        var clock = new ControllableStorageClock();
        string lockPath;
        using (StorageService service = StorageService.OpenForTests(layout, clock))
        {
            lockPath = service.OwnerLockPath;
            Assert.Equal(StorageServiceState.Created, service.Health.State);
            Assert.False(service.Health.AcceptingWrites);
            Assert.Equal(StorageDurabilityScope.SQLiteCommitted, service.Health.DurableWatermark.Scope);
            Assert.Throws<InvalidOperationException>(() => StorageService.OpenForTests(layout, clock));
            Assert.Throws<IOException>(() => new FileStream(
                lockPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.Read));
            service.Dispose();
            Assert.Equal(StorageServiceState.Disposed, service.Health.State);
            Assert.False(service.Health.AcceptingWrites);
        }

        Assert.True(File.Exists(lockPath));
        using StorageService reopened = StorageService.OpenForTests(layout, clock);
        Assert.Equal(StorageServiceState.Created, reopened.Health.State);
    }

    [Fact]
    public void LegacyStorageDatabaseCannotBypassLiveServiceOwner()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        var clock = new ControllableStorageClock();
        using (StorageService service = StorageService.OpenForTests(layout, clock))
        {
            Assert.Throws<IOException>(() => StorageDatabase.Open(layout, clock));
        }

        Guid deviceId = Guid.NewGuid();
        using (StorageDatabase database = StorageDatabase.Open(layout, clock))
        {
            database.RegisterDevice(deviceId, kind: 1, identityConfidence: 0, createdUtcMicroseconds: 1);
        }

        using StorageDatabase reopened = StorageDatabase.Open(layout, clock);
        Assert.Contains(reopened.ListDevices(), device => device.DeviceId == deviceId);
    }

    [Fact]
    public void CalibrationOnlyCommitAdvancesGlobalOrdinalAndSurvivesReopen()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        var clock = new ControllableStorageClock();
        TimeCalibrationSegment calibration = StorageServiceTestSupport.Calibration(
            "UTC",
            new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero));
        using (StorageService service = StorageService.OpenForTests(layout, clock))
        {
            service.Start(calibration);
            Assert.Equal(1, service.Health.DurableWatermark.CommitSequence);
            Assert.Equal(0, service.Health.DurableWatermark.KeyboardAggregateCommitSequence);

            TimeCalibrationSegment replacement = StorageServiceTestSupport.Calibration(
                "UTC",
                new DateTimeOffset(2026, 8, 9, 0, 0, 1, TimeSpan.Zero),
                monotonicAnchor: 1_000_000);
            service.ChangeCalibration(1_000_000, replacement);
            Assert.Equal(2, service.Health.DurableWatermark.CommitSequence);
            Assert.Equal(0, service.Health.DurableWatermark.KeyboardAggregateCommitSequence);
        }

        using StorageService reopened = StorageService.OpenForTests(layout, clock);
        Assert.Equal(2, reopened.Health.DurableWatermark.CommitSequence);
        Assert.Equal(0, reopened.Health.DurableWatermark.KeyboardAggregateCommitSequence);
    }

    [Fact]
    public void PauseReasonsStackAndOnlyLastRemovalResumes()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid deviceId = StorageServiceTestSupport.RegisterKeyboard(layout);
        var clock = new ControllableStorageClock();
        TimeCalibrationSegment calibration = StorageServiceTestSupport.Calibration(
            "UTC",
            new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero));
        using StorageService service = StorageService.OpenForTests(layout, clock);
        service.Start(calibration);
        service.WriteKeyboardTransition(StorageServiceTestSupport.Down(deviceId, calibration, 0));

        service.Pause(StoragePauseReason.User, 1_000_000);
        long pausedOrdinal = service.Health.DurableWatermark.CommitSequence;
        Assert.Equal(StorageServiceState.Paused, service.Health.State);
        Assert.False(service.Health.AcceptingWrites);
        service.Pause(StoragePauseReason.SessionLock, 1_000_000);
        Assert.Equal(pausedOrdinal, service.Health.DurableWatermark.CommitSequence);

        service.Resume(StoragePauseReason.User);
        Assert.Equal(StorageServiceState.Paused, service.Health.State);
        service.Resume(StoragePauseReason.SessionLock);
        Assert.Equal(StorageServiceState.Running, service.Health.State);
        Assert.True(service.Health.AcceptingWrites);
    }

    [Fact]
    public void DeviceAndCalibrationBoundariesPreserveRunningOrPausedState()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid firstDevice = StorageServiceTestSupport.RegisterKeyboard(layout);
        Guid secondDevice = StorageServiceTestSupport.RegisterKeyboard(layout);
        var clock = new ControllableStorageClock();
        TimeCalibrationSegment first = StorageServiceTestSupport.Calibration(
            "UTC",
            new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero));
        using StorageService service = StorageService.OpenForTests(layout, clock);
        service.Start(first);
        service.WriteKeyboardTransition(StorageServiceTestSupport.Down(firstDevice, first, 0, makeCode: 30));
        service.WriteKeyboardTransition(StorageServiceTestSupport.Down(secondDevice, first, 10, makeCode: 31));

        service.RemoveDevice(firstDevice, 1_000_000);
        Assert.Equal(StorageServiceState.Running, service.Health.State);
        Assert.True(service.Health.AcceptingWrites);
        TimeCalibrationSegment second = StorageServiceTestSupport.Calibration(
            "UTC",
            new DateTimeOffset(2026, 8, 9, 0, 0, 2, TimeSpan.Zero),
            monotonicAnchor: 2_000_000);
        service.ChangeCalibration(2_000_000, second);
        Assert.Equal(StorageServiceState.Running, service.Health.State);

        service.Pause(StoragePauseReason.Suspend, 2_000_000);
        TimeCalibrationSegment third = StorageServiceTestSupport.Calibration(
            "UTC",
            new DateTimeOffset(2026, 8, 9, 0, 0, 3, TimeSpan.Zero),
            monotonicAnchor: 3_000_000);
        service.ChangeCalibration(3_000_000, third);
        Assert.Equal(StorageServiceState.Paused, service.Health.State);
        Assert.False(service.Health.AcceptingWrites);
    }

    [Fact]
    public void CommitFailureEntersFaultWithoutAdvancingWatermarkOrRetrying()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid deviceId = StorageServiceTestSupport.RegisterKeyboard(layout);
        var clock = new ControllableStorageClock();
        var faults = new ControllableStorageFaultInjector();
        TimeCalibrationSegment calibration = StorageServiceTestSupport.Calibration(
            "UTC",
            new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero));
        using StorageService service = StorageService.OpenForTests(layout, clock, faults);
        service.Start(calibration);
        service.WriteKeyboardTransition(StorageServiceTestSupport.Down(deviceId, calibration, 0));
        service.WriteKeyboardTransition(StorageServiceTestSupport.Up(deviceId, calibration, 100_000));
        StorageDurableWatermark before = service.Health.DurableWatermark;
        faults.FailNextCommit = true;

        Assert.Throws<IOException>(() => service.Checkpoint(100_000));

        Assert.Equal(StorageServiceState.IntegrityFault, service.Health.State);
        Assert.False(service.Health.AcceptingWrites);
        Assert.NotNull(service.Health.IntegrityFault);
        Assert.Equal(before, service.Health.DurableWatermark);
        Assert.Throws<InvalidOperationException>(() =>
            service.WriteKeyboardTransition(StorageServiceTestSupport.Down(deviceId, calibration, 2_000_000)));
        Assert.Empty(StorageServiceTestSupport.ReadKeyboardRows(layout));
    }

    [Fact]
    public void EmptyDeviceBoundaryDoesNotAdvanceEitherOrdinal()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        var clock = new ControllableStorageClock();
        TimeCalibrationSegment calibration = StorageServiceTestSupport.Calibration(
            "UTC",
            new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero));
        using StorageService service = StorageService.OpenForTests(layout, clock);
        service.Start(calibration);
        StorageDurableWatermark before = service.Health.DurableWatermark;

        service.RemoveDevice(Guid.NewGuid(), 1_000_000);

        Assert.Equal(before, service.Health.DurableWatermark);
        Assert.Equal(StorageServiceState.Running, service.Health.State);
        Assert.True(service.Health.AcceptingWrites);
    }

    [Fact]
    public void StopCommitsOpenHoldAndPreventsFurtherAcceptance()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid deviceId = StorageServiceTestSupport.RegisterKeyboard(layout);
        var clock = new ControllableStorageClock();
        TimeCalibrationSegment calibration = StorageServiceTestSupport.Calibration(
            "UTC",
            new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero));
        using StorageService service = StorageService.OpenForTests(layout, clock);
        service.Start(calibration);
        service.WriteKeyboardTransition(StorageServiceTestSupport.Down(deviceId, calibration, 0));

        service.Stop(2_000_000);

        Assert.Equal(StorageServiceState.Stopped, service.Health.State);
        Assert.False(service.Health.AcceptingWrites);
        Assert.Equal(1, service.Health.DurableWatermark.KeyboardAggregateCommitSequence);
        Assert.Throws<InvalidOperationException>(() =>
            service.WriteKeyboardTransition(StorageServiceTestSupport.Up(deviceId, calibration, 2_000_000)));
        StorageKeyboardTestRow row = Assert.Single(StorageServiceTestSupport.ReadKeyboardRows(layout));
        Assert.Equal(1, row.PressCount);
        Assert.Equal(2_000_000, row.HoldDurationMicroseconds);
    }

    [Fact]
    public void BackwardsTimestampFaultsAndCannotResume()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid deviceId = StorageServiceTestSupport.RegisterKeyboard(layout);
        var clock = new ControllableStorageClock();
        TimeCalibrationSegment calibration = StorageServiceTestSupport.Calibration(
            "UTC",
            new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero));
        using StorageService service = StorageService.OpenForTests(layout, clock);
        service.Start(calibration);
        service.WriteKeyboardTransition(StorageServiceTestSupport.Down(deviceId, calibration, 10));

        Assert.Throws<InvalidDataException>(() => service.Checkpoint(9));

        Assert.Equal(StorageServiceState.IntegrityFault, service.Health.State);
        Assert.False(service.Health.AcceptingWrites);
        Assert.Throws<InvalidOperationException>(() => service.Resume(StoragePauseReason.User));
    }

    [Fact]
    public void InvalidTimeZoneOrCalibrationFrequencyFailsClosed()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        var clock = new ControllableStorageClock();
        TimeCalibrationSegment calibration = StorageServiceTestSupport.Calibration(
            "America/Los_Angeles",
            new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero));
        using StorageService service = StorageService.OpenForTests(layout, clock);

        Assert.Throws<InvalidDataException>(() => service.Start(calibration));

        Assert.Equal(StorageServiceState.IntegrityFault, service.Health.State);
        Assert.False(service.Health.AcceptingWrites);
        Assert.Equal(0, service.Health.DurableWatermark.CommitSequence);
        Assert.NotNull(service.Health.IntegrityFault);

        using var mismatchDirectory = new TemporaryDirectory();
        StorageLayout mismatchLayout = StorageLayout.Create(mismatchDirectory.Path);
        var mismatchClock = new ControllableStorageClock { Frequency = 2_000_000 };
        TimeCalibrationSegment mismatchCalibration = StorageServiceTestSupport.Calibration(
            "UTC",
            new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero));
        using (StorageService mismatch = StorageService.OpenForTests(mismatchLayout, mismatchClock))
        {
            Assert.Throws<InvalidDataException>(() => mismatch.Start(mismatchCalibration));
            Assert.Equal(StorageServiceState.IntegrityFault, mismatch.Health.State);
            Assert.Equal(0, mismatch.Health.DurableWatermark.CommitSequence);
        }

        using var conflictDirectory = new TemporaryDirectory();
        StorageLayout conflictLayout = StorageLayout.Create(conflictDirectory.Path);
        var conflictClock = new ControllableStorageClock();
        TimeCalibrationSegment first = StorageServiceTestSupport.Calibration(
            "UTC",
            new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero));
        using (StorageService conflict = StorageService.OpenForTests(conflictLayout, conflictClock))
        {
            conflict.Start(first);
            conflictClock.Frequency = 2_000_000;
            TimeCalibrationSegment conflictingFrequency = first with
            {
                Frequency = conflictClock.Frequency,
                MonotonicAnchor = 1_000_000,
                UtcAnchorMicroseconds = checked(first.UtcAnchorMicroseconds + 1_000_000)
            };

            Assert.Throws<InvalidDataException>(() =>
                conflict.ChangeCalibration(1_000_000, conflictingFrequency));
            Assert.Equal(StorageServiceState.IntegrityFault, conflict.Health.State);
            Assert.Equal(1, conflict.Health.DurableWatermark.CommitSequence);
        }

        using StorageService conflictReopened = StorageService.OpenForTests(
            conflictLayout,
            new ControllableStorageClock());
        Assert.Equal(1, conflictReopened.Health.DurableWatermark.CommitSequence);
    }
}

internal sealed class ControllableStorageClock : IStorageClock
{
    public long Frequency { get; set; } = 1_000_000;
    public long Timestamp { get; set; }
    public DateTimeOffset UtcNow { get; set; } = new(2026, 8, 9, 0, 0, 0, TimeSpan.Zero);
    public string TimeZoneId { get; set; } = "UTC";

    public long GetTimestamp() => Timestamp;
    public DateTimeOffset GetUtcNow() => UtcNow;
    public string GetTimeZoneId() => TimeZoneId;
}

internal sealed class ControllableStorageFaultInjector : IStorageServiceFaultInjector
{
    private int _positiveKeyboardBatchCount;

    public bool FailNextCommit { get; set; }
    public int? FailOnPositiveKeyboardBatchNumber { get; set; }
    public List<int> KeyboardRowCounts { get; } = [];

    public void BeforeStorageCommit(StorageLifecycleBoundary? boundary, int keyboardRowCount)
    {
        KeyboardRowCounts.Add(keyboardRowCount);
        if (keyboardRowCount > 0)
        {
            _positiveKeyboardBatchCount++;
        }

        bool failNumberedBatch = FailOnPositiveKeyboardBatchNumber == _positiveKeyboardBatchCount
            && keyboardRowCount > 0;
        if (!FailNextCommit && !failNumberedBatch)
        {
            return;
        }

        FailNextCommit = false;
        FailOnPositiveKeyboardBatchNumber = null;
        throw new IOException("Injected Storage commit failure.");
    }
}

internal static class StorageServiceTestSupport
{
    private const long UnixEpochTicks = 621355968000000000L;

    public static Guid RegisterKeyboard(StorageLayout layout)
    {
        Guid deviceId = Guid.NewGuid();
        using StorageDatabase database = StorageDatabase.Open(layout, new FakeClock());
        database.RegisterDevice(deviceId, kind: 1, identityConfidence: 0, createdUtcMicroseconds: 1);
        return deviceId;
    }

    public static TimeCalibrationSegment Calibration(
        string timeZoneId,
        DateTimeOffset utcAnchor,
        long monotonicAnchor = 0) => new(
            Guid.NewGuid(),
            Frequency: 1_000_000,
            monotonicAnchor,
            UtcAnchorMicroseconds: checked((utcAnchor.UtcDateTime.Ticks - UnixEpochTicks) / 10),
            SamplingUncertaintyTicks: 0,
            timeZoneId);

    public static StorageKeyboardTransition Down(
        Guid deviceId,
        TimeCalibrationSegment calibration,
        long timestamp,
        ushort makeCode = 30,
        bool isE0 = false,
        bool isE1 = false) => new(
            deviceId,
            makeCode,
            isE0,
            isE1,
            IsDown: true,
            timestamp,
            calibration.SessionId);

    public static StorageKeyboardTransition Up(
        Guid deviceId,
        TimeCalibrationSegment calibration,
        long timestamp,
        ushort makeCode = 30,
        bool isE0 = false,
        bool isE1 = false) => new(
            deviceId,
            makeCode,
            isE0,
            isE1,
            IsDown: false,
            timestamp,
            calibration.SessionId);

    public static IReadOnlyList<StorageKeyboardTestRow> ReadKeyboardRows(StorageLayout layout)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = layout.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT device_id, make_code, is_e0, is_e1, capture_local_date, time_zone_id,
                   press_count, hold_duration_us
            FROM keyboard_daily
            ORDER BY device_id, capture_local_date, make_code, is_e0, is_e1;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<StorageKeyboardTestRow>();
        while (reader.Read())
        {
            rows.Add(new StorageKeyboardTestRow(
                Guid.ParseExact(reader.GetString(0), "N"),
                checked((ushort)reader.GetInt32(1)),
                reader.GetInt32(2) != 0,
                reader.GetInt32(3) != 0,
                DateOnly.ParseExact(reader.GetString(4), "yyyy-MM-dd",
                    System.Globalization.CultureInfo.InvariantCulture),
                reader.GetString(5),
                reader.GetInt64(6),
                reader.GetInt64(7)));
        }

        return rows;
    }
}

internal sealed record StorageKeyboardTestRow(
    Guid DeviceId,
    ushort MakeCode,
    bool IsE0,
    bool IsE1,
    DateOnly CaptureLocalDate,
    string TimeZoneId,
    long PressCount,
    long HoldDurationMicroseconds);
