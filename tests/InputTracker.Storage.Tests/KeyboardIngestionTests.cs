namespace InputTracker.Storage.Tests;

public sealed class KeyboardIngestionTests
{
    public static TheoryData<string, DateTimeOffset, DateOnly, DateOnly> MidnightCases => new()
    {
        {
            "UTC",
            new DateTimeOffset(2026, 8, 9, 23, 59, 59, TimeSpan.Zero),
            new DateOnly(2026, 8, 9),
            new DateOnly(2026, 8, 10)
        },
        {
            "China Standard Time",
            new DateTimeOffset(2026, 8, 9, 15, 59, 59, TimeSpan.Zero),
            new DateOnly(2026, 8, 9),
            new DateOnly(2026, 8, 10)
        }
    };

    public static TheoryData<DateTimeOffset, DateOnly, long> PacificDstCases => new()
    {
        {
            new DateTimeOffset(2026, 3, 8, 8, 0, 0, TimeSpan.Zero),
            new DateOnly(2026, 3, 8),
            23L * 60 * 60 * 1_000_000
        },
        {
            new DateTimeOffset(2026, 11, 1, 7, 0, 0, TimeSpan.Zero),
            new DateOnly(2026, 11, 1),
            25L * 60 * 60 * 1_000_000
        }
    };

    [Fact]
    public void DuplicateDownAndOrphanUpDoNotCreateOrDoubleCountAggregates()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid deviceId = StorageServiceTestSupport.RegisterKeyboard(layout);
        TimeCalibrationSegment calibration = StorageServiceTestSupport.Calibration(
            "UTC",
            new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero));
        using StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock());
        service.Start(calibration);
        StorageDurableWatermark started = service.Health.DurableWatermark;

        Assert.Equal(
            StorageKeyboardTransitionResult.OrphanUpIgnored,
            service.WriteKeyboardTransition(StorageServiceTestSupport.Up(deviceId, calibration, 0)));
        Assert.Equal(
            StorageKeyboardTransitionResult.Accepted,
            service.WriteKeyboardTransition(StorageServiceTestSupport.Down(deviceId, calibration, 1)));
        Assert.Equal(started, service.Health.DurableWatermark);
        Assert.Empty(StorageServiceTestSupport.ReadKeyboardRows(layout));
        Assert.Equal(
            StorageKeyboardTransitionResult.DuplicateDownIgnored,
            service.WriteKeyboardTransition(StorageServiceTestSupport.Down(deviceId, calibration, 2)));
        Assert.Equal(
            StorageKeyboardTransitionResult.Accepted,
            service.WriteKeyboardTransition(StorageServiceTestSupport.Up(deviceId, calibration, 3)));

        service.Stop(3);

        StorageKeyboardTestRow row = Assert.Single(StorageServiceTestSupport.ReadKeyboardRows(layout));
        Assert.Equal(1, row.PressCount);
        Assert.Equal(2, row.HoldDurationMicroseconds);
        Assert.Equal(1, service.Health.DurableWatermark.KeyboardAggregateCommitSequence);
    }

    [Fact]
    public void ThirtySecondPendingTransitionTriggersOneAtomicBatch()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid deviceId = StorageServiceTestSupport.RegisterKeyboard(layout);
        TimeCalibrationSegment calibration = StorageServiceTestSupport.Calibration(
            "UTC",
            new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero));
        using StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock());
        service.Start(calibration);

        for (ushort makeCode = 1; makeCode < StorageService.MaxPendingTransitions; makeCode++)
        {
            service.WriteKeyboardTransition(StorageServiceTestSupport.Down(
                deviceId,
                calibration,
                makeCode,
                makeCode));
        }

        Assert.Equal(0, service.Health.DurableWatermark.KeyboardAggregateCommitSequence);
        Assert.Empty(StorageServiceTestSupport.ReadKeyboardRows(layout));

        service.WriteKeyboardTransition(StorageServiceTestSupport.Down(
            deviceId,
            calibration,
            StorageService.MaxPendingTransitions,
            checked((ushort)StorageService.MaxPendingTransitions)));

        IReadOnlyList<StorageKeyboardTestRow> rows = StorageServiceTestSupport.ReadKeyboardRows(layout);
        Assert.Equal(StorageService.MaxPendingTransitions, rows.Count);
        Assert.Equal((long)StorageService.MaxPendingTransitions, rows.Sum(row => row.PressCount));
        Assert.Equal(1, service.Health.DurableWatermark.KeyboardAggregateCommitSequence);
    }

    [Fact]
    public void SixtyFifthDailyRowStreamsAcrossBoundedTransactionsWithoutLoss()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid deviceId = StorageServiceTestSupport.RegisterKeyboard(layout);
        TimeCalibrationSegment calibration = StorageServiceTestSupport.Calibration(
            "UTC",
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var faults = new ControllableStorageFaultInjector();
        using StorageService service = StorageService.OpenForTests(
            layout,
            new ControllableStorageClock(),
            faults);
        service.Start(calibration);
        long day = 24L * 60 * 60 * 1_000_000;
        long duration = checked((StorageService.MaxPendingRows + 1L) * day);

        service.WriteKeyboardTransition(StorageServiceTestSupport.Down(deviceId, calibration, 0));
        service.WriteKeyboardTransition(StorageServiceTestSupport.Up(deviceId, calibration, duration));

        IReadOnlyList<StorageKeyboardTestRow> rows = StorageServiceTestSupport.ReadKeyboardRows(layout);
        Assert.Equal(StorageService.MaxPendingRows + 1, rows.Count);
        Assert.Equal(1, rows.Sum(row => row.PressCount));
        Assert.Equal(duration, rows.Sum(row => row.HoldDurationMicroseconds));
        Assert.Equal([StorageService.MaxPendingRows, 1], faults.KeyboardRowCounts.Where(count => count > 0));
        Assert.Equal(2, service.Health.DurableWatermark.KeyboardAggregateCommitSequence);

        using var failureDirectory = new TemporaryDirectory();
        StorageLayout failureLayout = StorageLayout.Create(failureDirectory.Path);
        Guid failureDeviceId = StorageServiceTestSupport.RegisterKeyboard(failureLayout);
        var failureFaults = new ControllableStorageFaultInjector
        {
            FailOnPositiveKeyboardBatchNumber = 2
        };
        using (StorageService failing = StorageService.OpenForTests(
            failureLayout,
            new ControllableStorageClock(),
            failureFaults))
        {
            TimeCalibrationSegment failureCalibration = StorageServiceTestSupport.Calibration(
                "UTC",
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            failing.Start(failureCalibration);
            failing.WriteKeyboardTransition(StorageServiceTestSupport.Down(
                failureDeviceId,
                failureCalibration,
                0));

            Assert.Throws<IOException>(() => failing.WriteKeyboardTransition(
                StorageServiceTestSupport.Up(
                    failureDeviceId,
                    failureCalibration,
                    duration)));

            Assert.Equal(StorageServiceState.IntegrityFault, failing.Health.State);
            Assert.False(failing.Health.AcceptingWrites);
            Assert.Equal(1, failing.Health.DurableWatermark.KeyboardAggregateCommitSequence);
            Assert.Equal([StorageService.MaxPendingRows, 1], failureFaults.KeyboardRowCounts.Where(count => count > 0));
            Assert.Throws<InvalidOperationException>(() => failing.Checkpoint(duration));
            IReadOnlyList<StorageKeyboardTestRow> durablePrefix =
                StorageServiceTestSupport.ReadKeyboardRows(failureLayout);
            Assert.Equal(StorageService.MaxPendingRows, durablePrefix.Count);
            Assert.Equal(1, durablePrefix.Sum(row => row.PressCount));
            Assert.Equal(StorageService.MaxPendingRows * day,
                durablePrefix.Sum(row => row.HoldDurationMicroseconds));
        }

        using StorageService failureReopened = StorageService.OpenForTests(
            failureLayout,
            new ControllableStorageClock());
        Assert.Equal(1, failureReopened.Health.DurableWatermark.KeyboardAggregateCommitSequence);
        IReadOnlyList<StorageKeyboardTestRow> reopenedPrefix =
            StorageServiceTestSupport.ReadKeyboardRows(failureLayout);
        Assert.Equal(StorageService.MaxPendingRows, reopenedPrefix.Count);
        Assert.Equal(StorageService.MaxPendingRows * day,
            reopenedPrefix.Sum(row => row.HoldDurationMicroseconds));
    }

    [Fact]
    public void ActiveTimerCommitsAtTwoHundredFiftyMillisecondsWithoutAnotherTransition()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid deviceId = StorageServiceTestSupport.RegisterKeyboard(layout);
        var clock = new ControllableStorageClock();
        TimeCalibrationSegment calibration = StorageServiceTestSupport.Calibration(
            "UTC",
            new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero));
        using StorageService service = StorageService.OpenForTests(layout, clock, startTimer: true);
        service.Start(calibration);
        service.WriteKeyboardTransition(StorageServiceTestSupport.Down(deviceId, calibration, 0));

        clock.Timestamp = 249_999;
        service.RunBatchTimerForTests();
        Assert.Equal(0, service.Health.DurableWatermark.KeyboardAggregateCommitSequence);
        clock.Timestamp = 1_000_000;

        Assert.True(SpinWait.SpinUntil(
            () => service.Health.DurableWatermark.KeyboardAggregateCommitSequence == 1,
            TimeSpan.FromSeconds(2)));
        StorageKeyboardTestRow timerRow = Assert.Single(StorageServiceTestSupport.ReadKeyboardRows(layout));
        Assert.Equal(1, timerRow.PressCount);
        Assert.Equal(0, timerRow.HoldDurationMicroseconds);

        Assert.Equal(
            StorageKeyboardTransitionResult.Accepted,
            service.WriteKeyboardTransition(StorageServiceTestSupport.Up(deviceId, calibration, 100_000)));
        service.Stop(100_000);

        Assert.Equal(StorageServiceState.Stopped, service.Health.State);
        StorageKeyboardTestRow finalRow = Assert.Single(StorageServiceTestSupport.ReadKeyboardRows(layout));
        Assert.Equal(1, finalRow.PressCount);
        Assert.Equal(100_000, finalRow.HoldDurationMicroseconds);
    }

    [Fact]
    public void DisposeWithoutStopDoesNotSynthesizeCrashOpenDuration()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid deviceId = StorageServiceTestSupport.RegisterKeyboard(layout);
        TimeCalibrationSegment calibration = StorageServiceTestSupport.Calibration(
            "UTC",
            new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero));
        using (StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock()))
        {
            service.Start(calibration);
            service.WriteKeyboardTransition(StorageServiceTestSupport.Down(deviceId, calibration, 0));
            service.Checkpoint(1_000_000);
        }

        StorageKeyboardTestRow beforeReopen = Assert.Single(StorageServiceTestSupport.ReadKeyboardRows(layout));
        Assert.Equal(1, beforeReopen.PressCount);
        Assert.Equal(1_000_000, beforeReopen.HoldDurationMicroseconds);
        using (StorageService reopened = StorageService.OpenForTests(layout, new ControllableStorageClock()))
        {
            TimeCalibrationSegment replacement = StorageServiceTestSupport.Calibration(
                "UTC",
                new DateTimeOffset(2026, 8, 9, 0, 1, 0, TimeSpan.Zero));
            reopened.Start(replacement);
        }

        Assert.Equal(beforeReopen, Assert.Single(StorageServiceTestSupport.ReadKeyboardRows(layout)));
    }

    [Theory]
    [MemberData(nameof(MidnightCases))]
    public void HoldIsSplitAtExplicitWindowsTimeZoneMidnight(
        string timeZoneId,
        DateTimeOffset utcAnchor,
        DateOnly firstDate,
        DateOnly secondDate)
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid deviceId = StorageServiceTestSupport.RegisterKeyboard(layout);
        TimeCalibrationSegment calibration = StorageServiceTestSupport.Calibration(timeZoneId, utcAnchor);
        using StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock());
        service.Start(calibration);
        service.WriteKeyboardTransition(StorageServiceTestSupport.Down(deviceId, calibration, 0));
        service.WriteKeyboardTransition(StorageServiceTestSupport.Up(deviceId, calibration, 2_000_000));
        service.Stop(2_000_000);

        IReadOnlyList<StorageKeyboardTestRow> rows = StorageServiceTestSupport.ReadKeyboardRows(layout);
        Assert.Collection(
            rows,
            row =>
            {
                Assert.Equal(firstDate, row.CaptureLocalDate);
                Assert.Equal(1, row.PressCount);
                Assert.Equal(1_000_000, row.HoldDurationMicroseconds);
                Assert.Equal(timeZoneId, row.TimeZoneId);
            },
            row =>
            {
                Assert.Equal(secondDate, row.CaptureLocalDate);
                Assert.Equal(0, row.PressCount);
                Assert.Equal(1_000_000, row.HoldDurationMicroseconds);
                Assert.Equal(timeZoneId, row.TimeZoneId);
            });
    }

    [Theory]
    [MemberData(nameof(PacificDstCases))]
    public void PacificDstLocalDayUsesItsActualUtcDuration(
        DateTimeOffset utcAnchor,
        DateOnly localDate,
        long durationMicroseconds)
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid deviceId = StorageServiceTestSupport.RegisterKeyboard(layout);
        TimeCalibrationSegment calibration = StorageServiceTestSupport.Calibration(
            "Pacific Standard Time",
            utcAnchor);
        using StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock());
        service.Start(calibration);
        service.WriteKeyboardTransition(StorageServiceTestSupport.Down(deviceId, calibration, 0));
        service.WriteKeyboardTransition(StorageServiceTestSupport.Up(deviceId, calibration, durationMicroseconds));
        service.Stop(durationMicroseconds);

        StorageKeyboardTestRow row = Assert.Single(StorageServiceTestSupport.ReadKeyboardRows(layout));
        Assert.Equal(localDate, row.CaptureLocalDate);
        Assert.Equal(1, row.PressCount);
        Assert.Equal(durationMicroseconds, row.HoldDurationMicroseconds);
    }

    [Fact]
    public void DeviceRemovalSettlesOnlyThatDevice()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid firstDevice = StorageServiceTestSupport.RegisterKeyboard(layout);
        Guid secondDevice = StorageServiceTestSupport.RegisterKeyboard(layout);
        TimeCalibrationSegment calibration = StorageServiceTestSupport.Calibration(
            "UTC",
            new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero));
        using StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock());
        service.Start(calibration);
        service.WriteKeyboardTransition(StorageServiceTestSupport.Down(firstDevice, calibration, 0));
        service.WriteKeyboardTransition(StorageServiceTestSupport.Down(secondDevice, calibration, 10));

        service.RemoveDevice(firstDevice, 1_000_000);
        IReadOnlyList<StorageKeyboardTestRow> firstCommit = StorageServiceTestSupport.ReadKeyboardRows(layout);
        StorageKeyboardTestRow firstRow = Assert.Single(firstCommit);
        Assert.Equal(firstDevice, firstRow.DeviceId);
        Assert.Equal(1_000_000, firstRow.HoldDurationMicroseconds);

        service.Checkpoint(2_000_000);
        StorageKeyboardTestRow secondRow = Assert.Single(
            StorageServiceTestSupport.ReadKeyboardRows(layout),
            row => row.DeviceId == secondDevice);
        Assert.Equal(1_999_990, secondRow.HoldDurationMicroseconds);
    }

    [Fact]
    public void DualE0E1PrefixFailsClosedWithoutPersistingKeyboardData()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid deviceId = StorageServiceTestSupport.RegisterKeyboard(layout);
        TimeCalibrationSegment calibration = StorageServiceTestSupport.Calibration(
            "UTC",
            new DateTimeOffset(2026, 8, 9, 0, 0, 0, TimeSpan.Zero));
        using StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock());
        service.Start(calibration);

        Assert.Throws<InvalidDataException>(() => service.WriteKeyboardTransition(
            StorageServiceTestSupport.Down(deviceId, calibration, 0, isE0: true, isE1: true)));

        Assert.Equal(StorageServiceState.IntegrityFault, service.Health.State);
        Assert.False(service.Health.AcceptingWrites);
        Assert.Empty(StorageServiceTestSupport.ReadKeyboardRows(layout));
        Assert.Equal(0, service.Health.DurableWatermark.KeyboardAggregateCommitSequence);
    }
}
