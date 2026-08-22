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
    public async Task DeletionPreviewNeverExposesConfirmationToken()
    {
        var storage = new FakeStorageTools();
        var controller = new AppStorageDataToolsController(storage, new FakeCaptureControl(), @"C:\portable", Clock);
        var range = new StorageUtcRange(10, 20);

        Guid deviceId = Guid.Parse("12345678-1234-1234-1234-1234567890ab");
        StorageDeletionDisplay result = await controller.PreviewDeletionAsync(
            deviceId, StorageDeviceKind.Mouse, range);

        Assert.Equal(deviceId, result.DeviceId);
        Assert.Equal(StorageDeviceKind.Mouse, result.DeviceKind);
        Assert.Equal(range, result.UtcRange);
        Assert.Equal(12, result.MouseRecordCount);
        Assert.Equal(3, result.KeyboardAggregateCount);
        Assert.Contains("当前存储格式", result.Status);
        Assert.DoesNotContain("SECRET-TOKEN", result.ToString());
    }

    [Fact]
    public void RestoreSelectionRequiresContainedBackupAndConfirmationButNeverExecutes()
    {
        using var temporary = new TemporaryDirectory();
        string backup = Path.Combine(temporary.Path, "backups", "chosen.itbackup");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.WriteAllText(backup, "fixture");
        var controller = new AppStorageDataToolsController(
            new FakeStorageTools(), new FakeCaptureControl(), temporary.Path, Clock);

        RestoreSelection result;
        using (AppStorageDataToolsController.RestoreInteraction interaction = controller.TryBeginInteraction())
            result = interaction.Validate(backup, confirmed: true);

        Assert.False(result.CanExecute);
        Assert.True(result.Confirmed);
        Assert.Contains("离线恢复执行尚未接通", result.Status);
        Assert.Equal(Path.GetRelativePath(temporary.Path, backup), result.RelativePath);
        using AppStorageDataToolsController.RestoreInteraction outsideInteraction = controller.TryBeginInteraction();
        Assert.Throws<InvalidDataException>(() => outsideInteraction.Validate(
            Path.Combine(Path.GetPathRoot(temporary.Path)!, "outside.itbackup"), true));
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
            Path.Combine(link, "outside.itbackup"), confirmed: true));
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

    [Fact]
    public async Task PreviewRejectsDifferentReturnedRangeAndDeviceKind()
    {
        Guid device = Guid.Parse("12345678-1234-1234-1234-1234567890ab");
        var storage = new FakeStorageTools { ReturnedRange = new StorageUtcRange(11, 20) };
        var controller = new AppStorageDataToolsController(
            storage, new FakeCaptureControl(), @"C:\portable", Clock);
        await Assert.ThrowsAsync<InvalidDataException>(() => controller.PreviewDeletionAsync(
            device, StorageDeviceKind.Mouse, new StorageUtcRange(10, 20)));

        storage = new FakeStorageTools { DeviceKind = StorageDeviceKind.Keyboard };
        controller = new AppStorageDataToolsController(
            storage, new FakeCaptureControl(), @"C:\portable", Clock);
        await Assert.ThrowsAsync<InvalidDataException>(() => controller.PreviewDeletionAsync(
            device, StorageDeviceKind.Mouse, new StorageUtcRange(10, 20)));
    }

    private static DateTimeOffset Clock() => new(2026, 8, 13, 12, 34, 56, TimeSpan.Zero);

    private static StorageKeyboardPeriodResult SamplePeriod() => new(
        new StorageResolvedPeriod(
            StoragePeriodKind.Day,
            new DateOnly(2026, 8, 13),
            new DateOnly(2026, 8, 14),
            "UTC",
            new StorageUtcRange(0, 100)),
        [],
        ["UTC"]);

    private sealed class FakeStorageTools : IAppStorageDataTools
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
                deviceId, ReturnedRange ?? range, 12, 3, StorageDeletionCapability.BlockedByCurrentFormat,
                "当前存储格式无法安全执行精确范围删除。", "SECRET-TOKEN", "SECRET-FINGERPRINT"));

        public IReadOnlyList<StorageDeviceInfo> ListDevices() =>
        [
            new StorageDeviceInfo(
                Guid.Parse("12345678-1234-1234-1234-1234567890ab"),
                DeviceKind,
                StorageDeviceIdentityConfidence.TransientInterface,
                StorageDeviceIdentityAmbiguity.MissingPermanentIdentifier,
                null,
                0,
                0)
        ];

        public StorageServiceHealth Health => new(
            StorageServiceState.Paused,
            false,
            new StorageDurableWatermark(0, 0, new Dictionary<Guid, long>(), StorageDurabilityScope.SQLiteCommitted),
            null);
    }

    private sealed class FakeCaptureControl(PauseReason reasons = PauseReason.None) : IInputCaptureControl
    {
        public List<string> Operations { get; } = [];
        public InputCaptureStatus CurrentStatus { get; private set; } = new(
            reasons == PauseReason.None ? InputCaptureState.Running : InputCaptureState.Paused, reasons, null);
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
    }
}
