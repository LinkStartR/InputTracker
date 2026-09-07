namespace InputTracker.Storage.Tests;

public sealed class StorageDeletionPreviewTests
{
    [Fact]
    public async Task MouseTrajectoryDeletionPreviewIsUnavailableAfterFeatureRemoval()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid mouse = Guid.NewGuid();
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock()))
        {
            database.RegisterDevice(mouse, (int)StorageDeviceKind.Mouse, 0, 1);
        }
        using StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock());

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.PreviewDeletionAsync(mouse, new StorageUtcRange(0, long.MaxValue)));

        Assert.Contains("no longer collects trajectory", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(layout.SpoolDirectory, "*.mspool"));
        Assert.Empty(Directory.GetFiles(layout.ChunkDirectory, "*.mchunk"));
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
}
