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
    public async Task FullDayDeletionCreatesBackupAndRemovesOnlyTheSelectedDeviceRange()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid keyboard = Guid.NewGuid();
        Guid otherKeyboard = Guid.NewGuid();
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock()))
        {
            database.RegisterDevice(keyboard, (int)StorageDeviceKind.Keyboard, 0, 1);
            database.RegisterDevice(otherKeyboard, (int)StorageDeviceKind.Keyboard, 0, 1);
            database.UpsertKeyboardDaily(keyboard, 30, false, false,
                new DateOnly(2026, 8, 13), "UTC", 4, 400);
            database.UpsertKeyboardDaily(keyboard, 31, false, false,
                new DateOnly(2026, 8, 13), "UTC", 2, 200);
            database.UpsertKeyboardDaily(keyboard, 30, false, false,
                new DateOnly(2026, 8, 14), "UTC", 8, 800);
            database.UpsertKeyboardDaily(otherKeyboard, 30, false, false,
                new DateOnly(2026, 8, 13), "UTC", 16, 1600);
        }
        StorageUtcRange day = StoragePeriodResolver.Resolve(
            StoragePeriodKind.Day, new DateOnly(2026, 8, 13), "UTC").UtcRange;
        StorageDeletionResult deleted;
        using (StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock()))
        {
            StorageDeletionPreview preview = await service.PreviewDeletionAsync(keyboard, day);

            Assert.Equal(StorageDeletionCapability.ExactKeyboardDailyAggregates, preview.Capability);
            Assert.Null(preview.BlockedReason);
            Assert.Equal(2, preview.KeyboardAggregateCount);
            Assert.Equal(6, preview.KeyboardPressCount);
            Assert.Equal(600, preview.KeyboardHoldDurationMicroseconds);
            Assert.Throws<InvalidOperationException>(() => service.ExecuteDeletion(
                preview, new string('0', 64), "backups/bad-token.itbackup"));

            deleted = service.ExecuteDeletion(
                preview, preview.Token, "backups/before-delete.itbackup");

            Assert.Equal(2, deleted.DeletedKeyboardAggregateCount);
            Assert.Equal(6, deleted.DeletedKeyboardPressCount);
            Assert.True(File.Exists(deleted.PreDeletionBackup.Path));
            Assert.Empty(service.QueryKeyboardPeriod(
                StoragePeriodKind.Day, new DateOnly(2026, 8, 13), "UTC", keyboard).Aggregates);
            Assert.Equal(8, Assert.Single(service.QueryKeyboardPeriod(
                StoragePeriodKind.Day, new DateOnly(2026, 8, 14), "UTC", keyboard).Aggregates).PressCount);
            Assert.Equal(16, Assert.Single(service.QueryKeyboardPeriod(
                StoragePeriodKind.Day, new DateOnly(2026, 8, 13), "UTC", otherKeyboard).Aggregates).PressCount);
            Assert.Throws<InvalidOperationException>(() => service.ExecuteDeletion(
                preview, preview.Token, "backups/stale.itbackup"));
        }

        StorageBackup.Restore(layout, Path.GetRelativePath(layout.ProgramDirectory, deleted.PreDeletionBackup.Path));
        using StorageService restored = StorageService.OpenForTests(layout, new ControllableStorageClock());
        Assert.Equal(6, restored.QueryKeyboardPeriod(
            StoragePeriodKind.Day, new DateOnly(2026, 8, 13), "UTC", keyboard).Aggregates.Sum(x => x.PressCount));
    }

    [Fact]
    public async Task PartialDayAndMixedTimeZoneOverlapFailClosedWithoutCreatingBackup()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid keyboard = Guid.NewGuid();
        Guid mixedKeyboard = Guid.NewGuid();
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock()))
        {
            database.RegisterDevice(keyboard, (int)StorageDeviceKind.Keyboard, 0, 1);
            database.RegisterDevice(mixedKeyboard, (int)StorageDeviceKind.Keyboard, 0, 1);
            database.UpsertKeyboardDaily(keyboard, 30, false, false,
                new DateOnly(2026, 3, 8), "Pacific Standard Time", 4, 400);
            database.UpsertKeyboardDaily(mixedKeyboard, 30, false, false,
                new DateOnly(2026, 3, 8), "Pacific Standard Time", 5, 500);
            database.UpsertKeyboardDaily(mixedKeyboard, 31, false, false,
                new DateOnly(2026, 3, 9), "UTC", 6, 600);
        }

        StorageUtcRange completeDay = StoragePeriodResolver.Resolve(
            StoragePeriodKind.Day, new DateOnly(2026, 3, 8), "Pacific Standard Time").UtcRange;
        using StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock());
        StorageDeletionPreview exact = await service.PreviewDeletionAsync(keyboard, completeDay);
        Assert.Equal(StorageDeletionCapability.ExactKeyboardDailyAggregates, exact.Capability);

        StorageDeletionPreview mixed = await service.PreviewDeletionAsync(mixedKeyboard, completeDay);
        Assert.Equal(StorageDeletionCapability.BlockedByCurrentFormat, mixed.Capability);
        Assert.Equal(1, mixed.KeyboardAggregateCount);

        var partial = new StorageUtcRange(
            completeDay.StartUtcMicroseconds + 1,
            completeDay.EndUtcMicroseconds);
        StorageDeletionPreview blocked = await service.PreviewDeletionAsync(keyboard, partial);
        Assert.Equal(StorageDeletionCapability.BlockedByCurrentFormat, blocked.Capability);
        Assert.NotNull(blocked.BlockedReason);
        Assert.Throws<InvalidOperationException>(() => service.ExecuteDeletion(
            blocked, blocked.Token, "backups/partial.itbackup"));
        Assert.False(File.Exists(Path.Combine(layout.ProgramDirectory, "backups", "partial.itbackup")));
        Assert.Equal(4, Assert.Single(service.QueryKeyboardPeriod(
            StoragePeriodKind.Day, new DateOnly(2026, 3, 8), "Pacific Standard Time", keyboard).Aggregates).PressCount);
    }
}
