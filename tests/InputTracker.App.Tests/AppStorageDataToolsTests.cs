namespace InputTracker.App.Tests;

using InputTracker.Input;
using InputTracker.Storage;

public sealed class AppStorageDataToolsTests
{
    [Fact]
    public async Task ExportUsesContainedUniquePathsAndReturnsPublicArtifacts()
    {
        var storage = new FakeStorageTools();
        var controller = new AppStorageDataToolsController(
            storage, new FakeCaptureControl(), @"C:\portable", () => new DateTimeOffset(2026, 8, 13, 12, 34, 56, TimeSpan.Zero));

        StorageDataToolsExportResult result = await controller.ExportKeyboardAsync(SamplePeriod());

        Assert.Equal(Path.Combine("data", "exports", "keyboard-20260813-123456-000.csv"), storage.CsvPath);
        Assert.Equal(Path.Combine("data", "exports", "keyboard-20260813-123456-000.png"), storage.PngPath);
        Assert.EndsWith(".csv", result.Csv.Path, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(".png", result.Png.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BackupRejectsUnlessUserAlreadyPausedWithoutCallingStorage()
    {
        var storage = new FakeStorageTools();
        var capture = new FakeCaptureControl();
        var controller = new AppStorageDataToolsController(storage, capture, @"C:\portable", Clock);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.CreateBackupAsync());

        Assert.Contains("用户明确点击暂停", error.Message);
        Assert.Empty(capture.Operations);
        Assert.Empty(storage.Operations);
    }

    [Fact]
    public async Task BackupWhileUserPausedNeverChangesPauseEvenWhenBackupFails()
    {
        var storage = new FakeStorageTools { ThrowOnBackup = true };
        var capture = new FakeCaptureControl(PauseReason.User);
        var controller = new AppStorageDataToolsController(storage, capture, @"C:\portable", Clock);

        await Assert.ThrowsAsync<IOException>(() => controller.CreateBackupAsync());
        Assert.Empty(capture.Operations);
        Assert.Equal(PauseReason.User, capture.CurrentStatus.PauseReasons);

        capture = new FakeCaptureControl(PauseReason.User);
        storage = new FakeStorageTools();
        controller = new AppStorageDataToolsController(storage, capture, @"C:\portable", Clock);
        await controller.CreateBackupAsync();
        Assert.Empty(capture.Operations);
        Assert.Equal(["backup"], storage.Operations);
        Assert.Equal(PauseReason.User, capture.CurrentStatus.PauseReasons);
    }

    [Fact]
    public async Task KeyboardDeletionPreviewNeverExposesConfirmationToken()
    {
        var storage = new FakeStorageTools { DeviceKind = StorageDeviceKind.Keyboard };
        var controller = new AppStorageDataToolsController(
            storage,
            new FakeCaptureControl(PauseReason.User),
            @"C:\portable",
            Clock);
        var range = new StorageUtcRange(10, 20);

        Guid deviceId = Guid.Parse("12345678-1234-1234-1234-1234567890ab");
        StorageDeletionDisplay result = await controller.PreviewDeletionAsync(
            deviceId, StorageDeviceKind.Keyboard, range);

        Assert.Equal(deviceId, result.DeviceId);
        Assert.Equal(StorageDeviceKind.Keyboard, result.DeviceKind);
        Assert.Equal(range, result.UtcRange);
        Assert.Equal(3, result.KeyboardAggregateCount);
        Assert.Equal(6, result.KeyboardPressCount);
        Assert.True(result.CanExecute);
        Assert.Contains("自动备份", result.Status);
        Assert.DoesNotContain("SECRET-TOKEN", result.ToString());
    }

    [Fact]
    public async Task KeyboardDeletionRequiresUserPauseAndExecutesOnlyThePreparedConfirmedPreview()
    {
        Guid deviceId = Guid.Parse("12345678-1234-1234-1234-1234567890ab");
        var range = new StorageUtcRange(10, 20);
        var storage = new FakeStorageTools { DeviceKind = StorageDeviceKind.Keyboard };
        var running = new AppStorageDataToolsController(
            storage, new FakeCaptureControl(), @"C:\portable", Clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => running.PreviewDeletionAsync(
            deviceId, StorageDeviceKind.Keyboard, range));
        Assert.Empty(storage.Operations);

        var capture = new FakeCaptureControl(PauseReason.User);
        var controller = new AppStorageDataToolsController(storage, capture, @"C:\portable", Clock);
        StorageDeletionDisplay display = await controller.PreviewDeletionAsync(
            deviceId, StorageDeviceKind.Keyboard, range);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.ExecutePreparedDeletionAsync(display, finalConfirmed: false));
        Assert.DoesNotContain("delete", storage.Operations);

        StorageDeletionResult result = await controller.ExecutePreparedDeletionAsync(
            display, finalConfirmed: true);
        Assert.Equal(6, result.DeletedKeyboardPressCount);
        Assert.Contains("delete", storage.Operations);
        Assert.Equal(PauseReason.User, capture.CurrentStatus.PauseReasons);
        Assert.StartsWith(Path.Combine("backups", "before-delete-20260813-123456-000"), storage.DeletionBackupPath);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.ExecutePreparedDeletionAsync(display, finalConfirmed: true));
    }

    [Fact]
    public void RestoreSelectionRequiresContainedBackupBeforeFinalConfirmation()
    {
        using var temporary = new TemporaryDirectory();
        string backup = Path.Combine(temporary.Path, "backups", "chosen.itbackup");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.WriteAllText(backup, "fixture");
        var controller = new AppStorageDataToolsController(
            new FakeStorageTools(), new FakeCaptureControl(), temporary.Path, Clock);

        RestoreSelection result;
        using (AppStorageDataToolsController.RestoreInteraction interaction = controller.TryBeginInteraction())
            result = interaction.Validate(backup);

        Assert.False(result.CanExecute);
        Assert.False(result.Confirmed);
        Assert.Contains("已验证", result.Status);
        Assert.Equal(Path.GetRelativePath(temporary.Path, backup), result.RelativePath);
        using AppStorageDataToolsController.RestoreInteraction outsideInteraction = controller.TryBeginInteraction();
        Assert.Throws<InvalidDataException>(() => outsideInteraction.Validate(
            Path.Combine(Path.GetPathRoot(temporary.Path)!, "outside.itbackup")));
    }

    [Fact]
    public void RestoreSelectionRejectsJunctionWithoutReadingExternalBackup()
    {
        using var temporary = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        string link = Path.Combine(temporary.Path, "backups");
        TemporaryDirectory.CreateJunction(link, outside.Path);
        string externalBackup = Path.Combine(outside.Path, "outside.itbackup");
        File.WriteAllText(externalBackup, "external");
        var controller = new AppStorageDataToolsController(
            new FakeStorageTools(), new FakeCaptureControl(), temporary.Path, Clock);

        using AppStorageDataToolsController.RestoreInteraction interaction = controller.TryBeginInteraction();
        Assert.Throws<InvalidDataException>(() => interaction.Validate(
            Path.Combine(link, "outside.itbackup")));
        Assert.Equal("external", File.ReadAllText(externalBackup));
        Directory.Delete(link);
    }

    [Fact]
    public async Task BusyExplicitExitReturnsFalseWithoutReleasingOperation()
    {
        var storage = new FakeStorageTools { BlockCsv = true };
        var controller = new AppStorageDataToolsController(
            storage, new FakeCaptureControl(PauseReason.User), @"C:\portable", Clock);
        Task<StorageDataToolsExportResult> export = controller.ExportKeyboardAsync(SamplePeriod());
        Assert.True(storage.CsvEntered.Wait(TimeSpan.FromSeconds(5)));

        Assert.False(controller.TryBeginExplicitExit());
        Assert.False(export.IsCompleted);

        storage.ReleaseCsv.Set();
        await export;
        Assert.True(controller.TryBeginExplicitExit());
    }

    [Fact]
    public async Task IdleExplicitExitClosesControllerAndRejectsNewOperations()
    {
        var controller = new AppStorageDataToolsController(
            new FakeStorageTools(), new FakeCaptureControl(PauseReason.User), @"C:\portable", Clock);

        Assert.True(controller.TryBeginExplicitExit());
        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.ExportKeyboardAsync(SamplePeriod()));
        Assert.Contains("正在退出", error.Message);
    }

    [Fact]
    public async Task SessionEndingTimesOutWithinBoundAndSucceedsAfterCompletion()
    {
        var storage = new FakeStorageTools { BlockCsv = true };
        var controller = new AppStorageDataToolsController(
            storage, new FakeCaptureControl(PauseReason.User), @"C:\portable", Clock);
        Task<StorageDataToolsExportResult> export = controller.ExportKeyboardAsync(SamplePeriod());
        Assert.True(storage.CsvEntered.Wait(TimeSpan.FromSeconds(5)));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(controller.BeginSessionEnding(TimeSpan.FromMilliseconds(100)));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        storage.ReleaseCsv.Set();
        await export;
        Assert.True(controller.BeginSessionEnding(TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task RestoreInteractionLeaseBlocksExplicitExitUntilReleased()
    {
        var controller = new AppStorageDataToolsController(
            new FakeStorageTools(), new FakeCaptureControl(), @"C:\portable", Clock);

        AppStorageDataToolsController.RestoreInteraction interaction = controller.TryBeginInteraction();
        Assert.False(controller.TryBeginExplicitExit());
        InvalidOperationException busy = await Assert.ThrowsAsync<InvalidOperationException>(
            () => controller.ExportKeyboardAsync(SamplePeriod()));
        Assert.Contains("正在执行", busy.Message);
        interaction.Dispose();
        Assert.True(controller.TryBeginExplicitExit());
    }

    [Theory]
    [InlineData(PauseReason.None, InputCaptureState.Running, StorageServiceState.Paused, false, true)]
    [InlineData(PauseReason.SessionLocked, InputCaptureState.Paused, StorageServiceState.Paused, false, true)]
    [InlineData(PauseReason.SystemSuspend, InputCaptureState.Paused, StorageServiceState.Paused, false, true)]
    [InlineData(PauseReason.User, InputCaptureState.Paused, StorageServiceState.Running, true, true)]
    [InlineData(PauseReason.User, InputCaptureState.Paused, StorageServiceState.Paused, true, true)]
    [InlineData(PauseReason.User, InputCaptureState.Paused, StorageServiceState.Paused, false, false)]
    public void RestoreTransferRejectsUnsafeBoundaryWithoutClosingController(
        PauseReason reasons,
        InputCaptureState captureState,
        StorageServiceState storageState,
        bool acceptingWrites,
        bool finalConfirmed)
    {
        using var temporary = new TemporaryDirectory();
        string backup = CreateBackupFixture(temporary.Path);
        var storage = new FakeStorageTools(storageState, acceptingWrites);
        var capture = new FakeCaptureControl(reasons, captureState);
        var controller = new AppStorageDataToolsController(storage, capture, temporary.Path, Clock);

        using (AppStorageDataToolsController.RestoreInteraction interaction = controller.TryBeginInteraction())
        {
            RestoreSelection selection = interaction.Validate(backup);
            Assert.Throws<InvalidOperationException>(() =>
                interaction.TransferToRestore(selection, finalConfirmed));
        }

        Assert.False(controller.IsClosing);
        using AppStorageDataToolsController.RestoreInteraction retry = controller.TryBeginInteraction();
    }

    [Fact]
    public async Task RestoreLeaseIsSingleUseAndPermanentlyBlocksOtherLifecycleOwners()
    {
        using var temporary = new TemporaryDirectory();
        string backup = CreateBackupFixture(temporary.Path);
        var controller = new AppStorageDataToolsController(
            new FakeStorageTools(),
            new FakeCaptureControl(PauseReason.User, InputCaptureState.Paused),
            temporary.Path,
            Clock);
        AppStorageDataToolsController.RestoreInteraction interaction = controller.TryBeginInteraction();
        RestoreSelection selection = interaction.Validate(backup);

        AppStorageDataToolsController.RestoreLease lease = interaction.TransferToRestore(selection, finalConfirmed: true);
        interaction.Dispose();

        Assert.Equal(selection.RelativePath, lease.Consume().RelativePath);
        Assert.Throws<InvalidOperationException>(() => lease.Consume());
        Assert.True(controller.IsClosing);
        Assert.False(controller.TryBeginExplicitExit());
        Assert.False(controller.BeginSessionEnding(TimeSpan.Zero));
        Assert.Throws<InvalidOperationException>(() => controller.TryBeginInteraction());
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.ExportKeyboardAsync(SamplePeriod()));
    }

    [Fact]
    public void RestoreTransferRejectsForgedCrossInteractionAndRepeatedSelection()
    {
        using var temporary = new TemporaryDirectory();
        string backup = CreateBackupFixture(temporary.Path);
        var controller = new AppStorageDataToolsController(
            new FakeStorageTools(),
            new FakeCaptureControl(PauseReason.User, InputCaptureState.Paused),
            temporary.Path,
            Clock);

        RestoreSelection firstSelection;
        using (AppStorageDataToolsController.RestoreInteraction first = controller.TryBeginInteraction())
        {
            firstSelection = first.Validate(backup);
            Assert.Throws<InvalidOperationException>(() => first.Validate(backup));
            var forged = firstSelection with { Status = "forged" };
            Assert.Throws<InvalidOperationException>(() => first.TransferToRestore(forged, finalConfirmed: true));
        }

        using AppStorageDataToolsController.RestoreInteraction second = controller.TryBeginInteraction();
        RestoreSelection secondSelection = second.Validate(backup);
        Assert.Throws<InvalidOperationException>(() => second.TransferToRestore(firstSelection, finalConfirmed: true));
        AppStorageDataToolsController.RestoreLease lease = second.TransferToRestore(secondSelection, finalConfirmed: true);
        Assert.ThrowsAny<InvalidOperationException>(() => second.TransferToRestore(secondSelection, finalConfirmed: true));
        Assert.Equal(secondSelection.RelativePath, lease.Consume().RelativePath);
    }

    [Fact]
    public async Task PreviewSupportsSelectedMouseButtonsAndRejectsDifferentKeyboardRange()
    {
        Guid device = Guid.Parse("12345678-1234-1234-1234-1234567890ab");
        var storage = new FakeStorageTools { DeviceKind = StorageDeviceKind.Mouse };
        var controller = new AppStorageDataToolsController(
            storage, new FakeCaptureControl(PauseReason.User), @"C:\portable", Clock);
        StorageDeletionDisplay mouse = await controller.PreviewDeletionAsync(
            device, StorageDeviceKind.Mouse, new StorageUtcRange(10, 20));
        Assert.True(mouse.CanExecute);
        Assert.Equal(6, mouse.PressCount);
        Assert.Contains("鼠标按键", mouse.Status);

        storage = new FakeStorageTools
        {
            DeviceKind = StorageDeviceKind.Keyboard,
            ReturnedRange = new StorageUtcRange(11, 20)
        };
        controller = new AppStorageDataToolsController(
            storage, new FakeCaptureControl(PauseReason.User), @"C:\portable", Clock);
        await Assert.ThrowsAsync<InvalidDataException>(() => controller.PreviewDeletionAsync(
            device, StorageDeviceKind.Keyboard, new StorageUtcRange(10, 20)));
    }

    [Fact]
    public async Task CombinedDeletionUsesPreparedKeyboardAndMousePreviewAndOneBackup()
    {
        Guid keyboard = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        Guid mouse = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
        var storage = new FakeStorageTools();
        var controller = new AppStorageDataToolsController(
            storage, new FakeCaptureControl(PauseReason.User), @"C:\portable", Clock);

        StorageDeletionBatchDisplay preview = await controller.PreviewDeletionBatchAsync(
            keyboard, mouse, new StorageUtcRange(10, 20));

        Assert.True(preview.CanExecute);
        Assert.Equal(6, preview.KeyboardPressCount);
        Assert.Equal(8, preview.MouseButtonPressCount);
        StorageDeletionBatchResult result = await controller.ExecutePreparedDeletionBatchAsync(
            preview, finalConfirmed: true);
        Assert.Equal(6, result.DeletedKeyboardPressCount);
        Assert.Equal(8, result.DeletedMouseButtonPressCount);
        Assert.Equal(["delete-all"], storage.Operations);
        Assert.StartsWith(Path.Combine("backups", "before-delete-20260813-123456-000"), storage.DeletionBackupPath);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            controller.ExecutePreparedDeletionBatchAsync(preview, finalConfirmed: true));
    }

    private static DateTimeOffset Clock() => new(2026, 8, 13, 12, 34, 56, TimeSpan.Zero);

    private static string CreateBackupFixture(string root)
    {
        string backup = Path.Combine(root, "backups", "chosen.itbackup");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.WriteAllText(backup, "fixture");
        return backup;
    }

    private static StorageKeyboardPeriodResult SamplePeriod() => new(
        new StorageResolvedPeriod(
            StoragePeriodKind.Day,
            new DateOnly(2026, 8, 13),
            new DateOnly(2026, 8, 14),
            "UTC",
            new StorageUtcRange(0, 100)),
        [],
        ["UTC"]);

    private sealed class FakeStorageTools(
        StorageServiceState state = StorageServiceState.Paused,
        bool acceptingWrites = false) : IAppStorageDataTools
    {
        public string? CsvPath { get; private set; }
        public string? PngPath { get; private set; }
        public bool ThrowOnBackup { get; init; }
        public bool BlockCsv { get; init; }
        public ManualResetEventSlim CsvEntered { get; } = new(false);
        public ManualResetEventSlim ReleaseCsv { get; } = new(false);
        public StorageUtcRange? ReturnedRange { get; init; }
        public List<string> Operations { get; } = [];
        public StorageDeviceKind DeviceKind { get; init; } = StorageDeviceKind.Mouse;
        public string? DeletionBackupPath { get; private set; }

        public StorageExportArtifact ExportKeyboardCsv(StorageKeyboardPeriodResult period, string relativePath)
        {
            CsvPath = relativePath;
            CsvEntered.Set();
            if (BlockCsv) ReleaseCsv.Wait(TimeSpan.FromSeconds(5));
            return new StorageExportArtifact(Path.Combine(@"C:\portable", relativePath), "CSV", 1);
        }

        public StorageExportArtifact ExportKeyboardHeatmapPng(StorageKeyboardPeriodResult period, string relativePath, int width, int height)
        {
            PngPath = relativePath;
            return new StorageExportArtifact(Path.Combine(@"C:\portable", relativePath), "PNG", 1);
        }

        public StorageBackupArtifact CreateBackup(string relativePath)
        {
            Operations.Add("backup");
            if (ThrowOnBackup) throw new IOException("backup failed");
            return new StorageBackupArtifact(Path.Combine(@"C:\portable", relativePath), "BACKUP", 1, 1);
        }

        public Task<StorageDeletionPreview> PreviewDeletionAsync(Guid deviceId, StorageUtcRange range, CancellationToken token) =>
            Task.FromResult(new StorageDeletionPreview(
                deviceId,
                ReturnedRange ?? range,
                DeviceKind == StorageDeviceKind.Keyboard ? 3 : 0,
                DeviceKind == StorageDeviceKind.Keyboard ? 6 : 0,
                DeviceKind == StorageDeviceKind.Keyboard ? 600 : 0,
                DeviceKind == StorageDeviceKind.Mouse ? 3 : 0,
                DeviceKind == StorageDeviceKind.Mouse ? 6 : 0,
                DeviceKind == StorageDeviceKind.Keyboard
                    ? StorageDeletionCapability.ExactKeyboardDailyAggregates
                    : StorageDeletionCapability.ExactMouseButtonDailyAggregates,
                null, "SECRET-TOKEN", "SECRET-FINGERPRINT"));

        public StorageDeletionResult ExecuteDeletion(
            StorageDeletionPreview preview,
            string confirmationToken,
            string preDeletionBackupRelativePath)
        {
            Operations.Add("delete");
            DeletionBackupPath = preDeletionBackupRelativePath;
            Assert.Equal("SECRET-TOKEN", confirmationToken);
            return new StorageDeletionResult(
                preview.DeviceId,
                preview.UtcRange,
                preview.KeyboardAggregateCount,
                preview.KeyboardPressCount,
                preview.KeyboardHoldDurationMicroseconds,
                preview.MouseButtonAggregateCount,
                preview.MouseButtonPressCount,
                new StorageBackupArtifact(
                    Path.Combine(@"C:\portable", preDeletionBackupRelativePath), "BACKUP", 1, 1));
        }

        public Task<StorageDeletionBatchPreview> PreviewDeletionBatchAsync(
            Guid keyboardDeviceId,
            Guid mouseDeviceId,
            StorageUtcRange range,
            CancellationToken token) => Task.FromResult(new StorageDeletionBatchPreview(
                keyboardDeviceId,
                mouseDeviceId,
                range,
                3,
                6,
                600,
                4,
                8,
                StorageDeletionCapability.ExactKeyboardAndMouseDailyAggregates,
                null,
                "SECRET-BATCH-TOKEN",
                "SECRET-BATCH-FINGERPRINT"));

        public StorageDeletionBatchResult ExecuteDeletionBatch(
            StorageDeletionBatchPreview preview,
            string confirmationToken,
            string preDeletionBackupRelativePath)
        {
            Assert.Equal("SECRET-BATCH-TOKEN", confirmationToken);
            Operations.Add("delete-all");
            DeletionBackupPath = preDeletionBackupRelativePath;
            return new StorageDeletionBatchResult(
                preview.KeyboardDeviceId,
                preview.MouseDeviceId,
                preview.UtcRange,
                preview.KeyboardAggregateCount,
                preview.KeyboardPressCount,
                preview.KeyboardHoldDurationMicroseconds,
                preview.MouseButtonAggregateCount,
                preview.MouseButtonPressCount,
                new StorageBackupArtifact(
                    Path.Combine(@"C:\portable", preDeletionBackupRelativePath), "BACKUP", 1, 1));
        }

        public IReadOnlyList<StorageDeviceInfo> ListDevices() =>
        [
            new StorageDeviceInfo(
                Guid.Parse("12345678-1234-1234-1234-1234567890ab"),
                DeviceKind,
                StorageDeviceIdentityConfidence.TransientInterface,
                StorageDeviceIdentityAmbiguity.MissingPermanentIdentifier,
                null,
                0,
                0),
            new StorageDeviceInfo(
                Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                StorageDeviceKind.Keyboard,
                StorageDeviceIdentityConfidence.TransientInterface,
                StorageDeviceIdentityAmbiguity.MissingPermanentIdentifier,
                null, 0, 0),
            new StorageDeviceInfo(
                Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
                StorageDeviceKind.Mouse,
                StorageDeviceIdentityConfidence.TransientInterface,
                StorageDeviceIdentityAmbiguity.MissingPermanentIdentifier,
                null, 0, 0)
        ];

        public StorageServiceHealth Health => new(
            state,
            acceptingWrites,
            new StorageDurableWatermark(0, 0, StorageDurabilityScope.SQLiteCommitted),
            null);
    }

    private sealed class FakeCaptureControl(
        PauseReason reasons = PauseReason.None,
        InputCaptureState? state = null) : IInputCaptureControl
    {
        public List<string> Operations { get; } = [];
        public InputCaptureStatus CurrentStatus { get; private set; } = new(
            state ?? (reasons == PauseReason.None ? InputCaptureState.Running : InputCaptureState.Paused), reasons, null);
        public DiagnosticsSnapshot Diagnostics => throw new NotSupportedException();
        public event EventHandler<InputCaptureStatus>? StatusChanged;

        public ValueTask SetPauseReasonAsync(PauseReason reason, bool active, CancellationToken cancellationToken = default)
        {
            Operations.Add($"set:{reason}:{active}");
            PauseReason updated = active ? CurrentStatus.PauseReasons | reason : CurrentStatus.PauseReasons & ~reason;
            CurrentStatus = new InputCaptureStatus(
                updated == PauseReason.None ? InputCaptureState.Running : InputCaptureState.Paused, updated, null);
            StatusChanged?.Invoke(this, CurrentStatus);
            return ValueTask.CompletedTask;
        }

        public ValueTask SetGamepadCaptureEnabledAsync(bool enabled, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
