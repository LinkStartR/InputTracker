namespace InputTracker.Storage.Tests;

public sealed class StorageMouseButtonQueryTests
{
    private const ushort LeftDown = 1 << 0;
    private const ushort RightDown = 1 << 2;
    private const ushort MiddleDown = 1 << 4;
    private const ushort X1Down = 1 << 6;
    private const ushort X2Down = 1 << 8;

    [Fact]
    public void QueryCountsButtonDownOnceByDeviceAndCaptureDate()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid first = RegisterMouse(layout);
        Guid second = RegisterMouse(layout);
        TimeCalibrationSegment calibration = StorageServiceTestSupport.Calibration(
            "UTC",
            new DateTimeOffset(2026, 9, 6, 23, 59, 59, TimeSpan.Zero));

        using StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock());
        service.Start(calibration);
        service.WriteMouseButtonTransition(Point(first, calibration, 0, LeftDown | RightDown));
        service.WriteMouseButtonTransition(Point(first, calibration, 2_000_000, MiddleDown | X1Down | X2Down));
        service.WriteMouseButtonTransition(Point(second, calibration, 2_000_001, LeftDown));
        service.Checkpoint(2_000_001);

        StorageMouseButtonPeriodResult firstDay = service.QueryMouseButtonsPeriod(
            StoragePeriodKind.Day, new DateOnly(2026, 9, 6), "UTC", first);
        Assert.Equal(["UTC"], firstDay.AvailableCaptureTimeZoneIds);
        Assert.Equal(
            [(StorageMouseButton.Left, 1L), (StorageMouseButton.Right, 1L)],
            firstDay.Aggregates.Select(row => (row.Button, row.PressCount)));

        StorageMouseButtonPeriodResult secondDay = service.QueryMouseButtonsPeriod(
            StoragePeriodKind.Day, new DateOnly(2026, 9, 7), "UTC", first);
        Assert.Equal(
            [
                (StorageMouseButton.Middle, 1L),
                (StorageMouseButton.X1, 1L),
                (StorageMouseButton.X2, 1L)
            ],
            secondDay.Aggregates.Select(row => (row.Button, row.PressCount)));

        StorageMouseButtonPeriodResult allDevices = service.QueryMouseButtonsPeriod(
            StoragePeriodKind.Month, new DateOnly(2026, 9, 7), "UTC");
        Assert.Equal(6, allDevices.Aggregates.Sum(row => row.PressCount));
        Assert.Equal(5, allDevices.Aggregates.Count(row => row.DeviceId == first));
        Assert.Single(allDevices.Aggregates, row => row.DeviceId == second && row.Button == StorageMouseButton.Left);
    }

    [Fact]
    public void ReopenKeepsDirectButtonAggregatesWithoutTrajectoryFiles()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid mouse = RegisterMouse(layout);
        TimeCalibrationSegment calibration = StorageServiceTestSupport.Calibration(
            "UTC",
            new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero));
        using (StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock()))
        {
            service.Start(calibration);
            service.WriteMouseButtonTransition(Point(mouse, calibration, 0, LeftDown));
            service.Checkpoint(0);
        }

        using StorageService reopened = StorageService.OpenForTests(layout, new ControllableStorageClock());
        StorageMouseButtonPeriodResult result = reopened.QueryMouseButtonsPeriod(
            StoragePeriodKind.Day, new DateOnly(2026, 9, 7), "UTC", mouse);

        StorageMouseButtonPeriodAggregate row = Assert.Single(result.Aggregates);
        Assert.Equal(StorageMouseButton.Left, row.Button);
        Assert.Equal(1, row.PressCount);
        Assert.Empty(Directory.GetFiles(layout.SpoolDirectory, "*.mspool"));
        Assert.Empty(Directory.GetFiles(layout.ChunkDirectory, "*.mchunk"));
    }

    [Fact]
    public void DirectButtonAggregationDoesNotCreateTrajectoryArtifacts()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid mouse = RegisterMouse(layout);
        TimeCalibrationSegment calibration = StorageServiceTestSupport.Calibration(
            "UTC",
            new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero));

        using StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock());
        service.Start(calibration);
        service.WriteMouseButtonTransition(new StorageMouseButtonTransition(
            mouse,
            calibration.SessionId,
            0,
            LeftDown | RightDown));

        StorageMouseButtonPeriodResult result = service.QueryMouseButtonsPeriod(
            StoragePeriodKind.Day, new DateOnly(2026, 9, 7), "UTC", mouse);
        Assert.Equal(2, result.Aggregates.Sum(row => row.PressCount));
        Assert.Empty(Directory.GetFiles(layout.SpoolDirectory, "*.mspool"));
        Assert.Empty(Directory.GetFiles(layout.ChunkDirectory, "*.mchunk"));
    }

    private static Guid RegisterMouse(StorageLayout layout)
    {
        Guid deviceId = Guid.NewGuid();
        using StorageDatabase database = StorageDatabase.Open(layout, new FakeClock());
        database.RegisterDevice(deviceId, (int)StorageDeviceKind.Mouse, 0, 1);
        return deviceId;
    }

    private static StorageMouseButtonTransition Point(
        Guid deviceId,
        TimeCalibrationSegment calibration,
        long timestamp,
        ushort buttons) => new(
            deviceId,
            calibration.SessionId,
            timestamp,
            ButtonChanges: buttons);
}
