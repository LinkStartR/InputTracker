namespace InputTracker.Storage.Tests;

public sealed class StorageDeletionPreviewTests
{
    [Fact]
    public async Task PreviewCountsExactDeviceAndHalfOpenRangeWithoutChangingStore()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid mouse = Guid.NewGuid();
        Guid keyboard = Guid.NewGuid();
        var clock = new ControllableStorageClock { Timestamp = 0 };
        TimeCalibrationSegment calibration = TimeCalibrationSegment.Capture(Guid.NewGuid(), clock);
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock()))
        {
            database.RegisterDevice(mouse, (int)StorageDeviceKind.Mouse, 0, 1);
            database.RegisterDevice(keyboard, (int)StorageDeviceKind.Keyboard, 0, 1);
            database.UpsertKeyboardDaily(keyboard, 30, false, false,
                new DateOnly(2026, 8, 13), "UTC", 4, 400);
        }
        using StorageService service = StorageService.OpenForTests(layout, clock);
        service.Start(calibration);
        service.WriteMouseTransition(new StorageMouseTransition(
            mouse, calibration.SessionId, 1, 1, 1, 0, false, 0, 0, 0));
        service.WriteMouseTransition(new StorageMouseTransition(
            mouse, calibration.SessionId, 2, 2, 2, 0, false, 0, 0, 0));
        service.Stop(3);
        long start = calibration.ToUtcMicroseconds(1);
        long end = calibration.ToUtcMicroseconds(2);

        StorageDeletionPreview preview = await service.PreviewDeletionAsync(
            mouse, new StorageUtcRange(start, end));

        Assert.Equal(mouse, preview.DeviceId);
        Assert.Equal(1, preview.MouseRecordCount);
        Assert.Equal(0, preview.KeyboardAggregateCount);
        Assert.Equal(64, preview.Token.Length);
        Assert.Equal(StorageDeletionCapability.BlockedByCurrentFormat, preview.Capability);
        Assert.Contains("continuous prefix", preview.BlockedReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, (await ReadMouse(service, mouse)).Count);
    }

    [Fact]
    public async Task ExecuteAlwaysFailsClosedWhileCurrentFormatCannotRewriteExactRange()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid keyboard = Guid.NewGuid();
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock()))
        {
            database.RegisterDevice(keyboard, (int)StorageDeviceKind.Keyboard, 0, 1);
            database.UpsertKeyboardDaily(keyboard, 30, false, false,
                new DateOnly(2026, 8, 13), "UTC", 4, 400);
        }
        using StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock());
        StorageDeletionPreview preview = await service.PreviewDeletionAsync(
            keyboard, new StorageUtcRange(0, long.MaxValue));

        Assert.Equal(StorageDeletionCapability.BlockedByCurrentFormat, preview.Capability);
        Assert.Throws<InvalidOperationException>(() => service.ExecuteDeletion(preview, preview.Token));
        Assert.Throws<InvalidOperationException>(() => service.ExecuteDeletion(preview, new string('0', 64)));
        Assert.Equal(4, Assert.Single(service.QueryKeyboardPeriod(
            StoragePeriodKind.Day, new DateOnly(2026, 8, 13), "UTC", keyboard).Aggregates).PressCount);
    }

    private static async Task<List<StorageMouseRecord>> ReadMouse(StorageService service, Guid deviceId)
    {
        var result = new List<StorageMouseRecord>();
        await foreach (StorageMouseRecord record in service.StreamMouseAsync(
            deviceId, new StorageUtcRange(0, long.MaxValue)))
        {
            result.Add(record);
        }
        return result;
    }
}
