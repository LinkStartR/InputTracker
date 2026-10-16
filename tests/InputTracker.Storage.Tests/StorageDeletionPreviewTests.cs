namespace InputTracker.Storage.Tests;

public sealed class StorageDeletionPreviewTests
{
    [Fact]
    public async Task CombinedDeletionUsesOneBackupAndOneAtomicKeyboardMouseCommit()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid keyboard = Guid.NewGuid();
        Guid mouse = Guid.NewGuid();
        Guid otherKeyboard = Guid.NewGuid();
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock()))
        {
            database.RegisterDevice(keyboard, (int)StorageDeviceKind.Keyboard, 0, 1);
            database.RegisterDevice(mouse, (int)StorageDeviceKind.Mouse, 0, 1);
            database.RegisterDevice(otherKeyboard, (int)StorageDeviceKind.Keyboard, 0, 1);
            database.UpsertKeyboardDaily(keyboard, 30, false, false,
                new DateOnly(2026, 9, 18), "UTC", 4, 400);
            database.UpsertKeyboardDaily(otherKeyboard, 31, false, false,
                new DateOnly(2026, 9, 18), "UTC", 7, 700);
            database.CommitMouseButtonPresses(mouse, new DateOnly(2026, 9, 18), "UTC",
                [StorageMouseButton.Left, StorageMouseButton.Right], NoStorageServiceFaultInjector.Instance);
        }
        StorageUtcRange day = StoragePeriodResolver.ResolveRange(
            new DateOnly(2026, 9, 18), new DateOnly(2026, 9, 18), "UTC").UtcRange;
        StorageDeletionBatchResult deleted;
        using (StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock()))
        {
            StorageDeletionBatchPreview preview = await service.PreviewDeletionBatchAsync(keyboard, mouse, day);
            Assert.Equal(StorageDeletionCapability.ExactKeyboardAndMouseDailyAggregates, preview.Capability);
            Assert.Equal(4, preview.KeyboardPressCount);
            Assert.Equal(2, preview.MouseButtonPressCount);

            deleted = service.ExecuteDeletionBatch(
                preview, preview.Token, "backups/before-delete-all.itbackup");

            Assert.Equal(4, deleted.DeletedKeyboardPressCount);
            Assert.Equal(2, deleted.DeletedMouseButtonPressCount);
            Assert.Empty(service.QueryKeyboardRange(
                new DateOnly(2026, 9, 18), new DateOnly(2026, 9, 18), "UTC", keyboard).Aggregates);
            Assert.Empty(service.QueryMouseButtonsRange(
                new DateOnly(2026, 9, 18), new DateOnly(2026, 9, 18), "UTC", mouse).Aggregates);
            Assert.Equal(7, Assert.Single(service.QueryKeyboardRange(
                new DateOnly(2026, 9, 18), new DateOnly(2026, 9, 18), "UTC", otherKeyboard).Aggregates).PressCount);
            Assert.True(File.Exists(deleted.PreDeletionBackup.Path));
            Assert.Throws<InvalidOperationException>(() => service.ExecuteDeletionBatch(
                preview, preview.Token, "backups/stale-all.itbackup"));
        }

        StorageBackup.Restore(layout, Path.GetRelativePath(layout.ProgramDirectory, deleted.PreDeletionBackup.Path));
        using StorageService restored = StorageService.OpenForTests(layout, new ControllableStorageClock());
        Assert.Equal(4, restored.QueryKeyboardRange(
            new DateOnly(2026, 9, 18), new DateOnly(2026, 9, 18), "UTC", keyboard).Aggregates.Sum(x => x.PressCount));
        Assert.Equal(2, restored.QueryMouseButtonsRange(
            new DateOnly(2026, 9, 18), new DateOnly(2026, 9, 18), "UTC", mouse).Aggregates.Sum(x => x.PressCount));
    }

    [Fact]
    public async Task MouseButtonDeletionNeverCreatesTrajectoryArtifacts()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid mouse = Guid.NewGuid();
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock()))
        {
            database.RegisterDevice(mouse, (int)StorageDeviceKind.Mouse, 0, 1);
            database.CommitMouseButtonPresses(mouse, new DateOnly(2026, 9, 18), "UTC",
                [StorageMouseButton.Left], NoStorageServiceFaultInjector.Instance);
        }
        StorageUtcRange day = StoragePeriodResolver.Resolve(
            StoragePeriodKind.Day, new DateOnly(2026, 9, 18), "UTC").UtcRange;
        using StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock());

        StorageDeletionPreview preview = await service.PreviewDeletionAsync(mouse, day);
        service.ExecuteDeletion(preview, preview.Token, "backups/no-trajectory.itbackup");

        Assert.Empty(Directory.GetFiles(layout.SpoolDirectory, "*.mspool"));
        Assert.Empty(Directory.GetFiles(layout.ChunkDirectory, "*.mchunk"));
    }

    [Fact]
    public async Task MouseButtonFullDayDeletionCreatesBackupAndKeepsOtherDeviceAndDate()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid mouse = Guid.NewGuid();
        Guid otherMouse = Guid.NewGuid();
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock()))
        {
            database.RegisterDevice(mouse, (int)StorageDeviceKind.Mouse, 0, 1);
            database.RegisterDevice(otherMouse, (int)StorageDeviceKind.Mouse, 0, 1);
            database.CommitMouseButtonPresses(mouse, new DateOnly(2026, 9, 18), "UTC",
                [StorageMouseButton.Left, StorageMouseButton.Right], NoStorageServiceFaultInjector.Instance);
            database.CommitMouseButtonPresses(mouse, new DateOnly(2026, 9, 18), "UTC",
                [StorageMouseButton.Left], NoStorageServiceFaultInjector.Instance);
            database.CommitMouseButtonPresses(mouse, new DateOnly(2026, 9, 17), "UTC",
                [StorageMouseButton.Middle], NoStorageServiceFaultInjector.Instance);
            database.CommitMouseButtonPresses(otherMouse, new DateOnly(2026, 9, 18), "UTC",
                [StorageMouseButton.X1], NoStorageServiceFaultInjector.Instance);
        }
        StorageUtcRange day = StoragePeriodResolver.Resolve(
            StoragePeriodKind.Day, new DateOnly(2026, 9, 18), "UTC").UtcRange;
        StorageDeletionResult deleted;
        using (StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock()))
        {
            StorageDeletionPreview preview = await service.PreviewDeletionAsync(mouse, day);

            Assert.Equal(StorageDeletionCapability.ExactMouseButtonDailyAggregates, preview.Capability);
            Assert.Equal(2, preview.MouseButtonAggregateCount);
            Assert.Equal(3, preview.MouseButtonPressCount);
            Assert.Equal(0, preview.KeyboardAggregateCount);

            deleted = service.ExecuteDeletion(
                preview, preview.Token, "backups/before-mouse-delete.itbackup");

            Assert.Equal(2, deleted.DeletedMouseButtonAggregateCount);
            Assert.Equal(3, deleted.DeletedMouseButtonPressCount);
            Assert.Empty(service.QueryMouseButtonsPeriod(
                StoragePeriodKind.Day, new DateOnly(2026, 9, 18), "UTC", mouse).Aggregates);
            Assert.Equal(1, Assert.Single(service.QueryMouseButtonsPeriod(
                StoragePeriodKind.Day, new DateOnly(2026, 9, 17), "UTC", mouse).Aggregates).PressCount);
            Assert.Equal(1, Assert.Single(service.QueryMouseButtonsPeriod(
                StoragePeriodKind.Day, new DateOnly(2026, 9, 18), "UTC", otherMouse).Aggregates).PressCount);
            Assert.True(File.Exists(deleted.PreDeletionBackup.Path));
        }

        StorageBackup.Restore(layout, Path.GetRelativePath(layout.ProgramDirectory, deleted.PreDeletionBackup.Path));
        using StorageService restored = StorageService.OpenForTests(layout, new ControllableStorageClock());
        Assert.Equal(3, restored.QueryMouseButtonsPeriod(
            StoragePeriodKind.Day, new DateOnly(2026, 9, 18), "UTC", mouse).Aggregates.Sum(x => x.PressCount));
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
