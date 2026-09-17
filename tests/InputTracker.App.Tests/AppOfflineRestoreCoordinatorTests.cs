namespace InputTracker.App.Tests;

using InputTracker.Input;
using InputTracker.Storage;

public sealed class AppOfflineRestoreCoordinatorTests
{
    [Fact]
    public async Task HappyPathUsesExactOrderAndTerminatesOnce()
    {
        var runtime = new FakeRuntime();
        AppStorageDataToolsController.RestoreLease lease = CreateLease();
        var coordinator = new AppOfflineRestoreCoordinator(runtime, () => 1234);

        await coordinator.ExecuteAsync(lease);

        Assert.Equal(
        [
            "ui", "status:Restoring", "system-events", "capture", "diagnostics",
            "storage-health", "storage-stop:1234", "storage-health", "storage-dispose",
            "restore", "status:SucceededAwaitExit", "shutdown"
        ], runtime.Operations);
        Assert.Equal(1, runtime.RestoreCount);
        Assert.Equal(1, runtime.ShutdownCount);
        Assert.Equal("automatic-before.itbackup", runtime.Statuses[^1].PreRestoreBackupPath);
    }

    [Theory]
    [InlineData(FailurePoint.Ui)]
    [InlineData(FailurePoint.SystemEvents)]
    [InlineData(FailurePoint.Capture)]
    [InlineData(FailurePoint.DiagnosticsOpen)]
    [InlineData(FailurePoint.StorageStop)]
    [InlineData(FailurePoint.StorageState)]
    [InlineData(FailurePoint.StorageDispose)]
    public async Task TeardownFailurePreventsRestoreAndNeverRepeatsOwnerRelease(FailurePoint failure)
    {
        var runtime = new FakeRuntime { Failure = failure };
        var coordinator = new AppOfflineRestoreCoordinator(runtime, () => 1234);

        await coordinator.ExecuteAsync(CreateLease());

        Assert.Equal(0, runtime.RestoreCount);
        Assert.Equal(1, runtime.ShutdownCount);
        Assert.Equal(AppOfflineRestorePhase.FailedAwaitExit, runtime.Statuses[^1].Phase);
        Assert.True(runtime.UiCount <= 1);
        Assert.True(runtime.SystemEventCount <= 1);
        Assert.True(runtime.CaptureCount <= 1);
        Assert.True(runtime.StorageStopCount <= 1);
        Assert.True(runtime.StorageDisposeCount <= 1);
        Assert.DoesNotContain("resume", runtime.Operations);
        if (failure is FailurePoint.Ui or FailurePoint.SystemEvents)
        {
            Assert.Contains("capture", runtime.Operations);
            Assert.Contains("storage-dispose", runtime.Operations);
        }
    }

    [Theory]
    [InlineData(false, (int)AppOfflineRestorePhase.FailedAwaitExit)]
    [InlineData(true, (int)AppOfflineRestorePhase.RecoveryRequired)]
    public async Task RestoreFailureIsTerminalAndNeverReopensOwners(
        bool recoveryRequired,
        int expectedValue)
    {
        var runtime = new FakeRuntime
        {
            Failure = FailurePoint.Restore,
            RecoveryRequired = recoveryRequired
        };

        await new AppOfflineRestoreCoordinator(runtime, () => 1234).ExecuteAsync(CreateLease());

        Assert.Equal(1, runtime.RestoreCount);
        Assert.Equal(1, runtime.ShutdownCount);
        Assert.Equal((AppOfflineRestorePhase)expectedValue, runtime.Statuses[^1].Phase);
        Assert.DoesNotContain("resume", runtime.Operations);
        Assert.DoesNotContain("storage-open", runtime.Operations);
    }

    [Fact]
    public async Task OpenCaptureDiagnosticsProducesExplicitUncertainTerminalState()
    {
        var runtime = new FakeRuntime { Failure = FailurePoint.DiagnosticsOpen };

        await new AppOfflineRestoreCoordinator(runtime, () => 1234).ExecuteAsync(CreateLease());

        Assert.False(runtime.Statuses[^1].CaptureConfirmedClosed);
        Assert.Equal(AppOfflineRestorePhase.FailedAwaitExit, runtime.Statuses[^1].Phase);
        Assert.Equal(0, runtime.RestoreCount);
        Assert.Equal(1, runtime.ShutdownCount);
    }

    [Fact]
    public void OwnerHandoffCannotDisposeTheSameReferenceTwice()
    {
        var owner = new DisposeProbe();
        DisposeProbe? field = owner;

        AppOfflineRestoreOwnership.Take(ref field)?.Dispose();
        AppOfflineRestoreOwnership.Take(ref field)?.Dispose();

        Assert.Null(field);
        Assert.Equal(1, owner.DisposeCount);
    }

    [Fact]
    public void RestoreLifecycleSuppressesNormalExitCleanupAndDeduplicatesShutdown()
    {
        var gate = new AppOfflineRestoreLifecycleGate();

        Assert.True(gate.AllowsNormalExitResourceCleanup);
        Assert.True(gate.TryBeginRestore());
        Assert.False(gate.TryBeginRestore());
        Assert.True(gate.RestoreActive);
        Assert.False(gate.AllowsNormalExitResourceCleanup);
        Assert.True(gate.TryRequestShutdown());
        Assert.False(gate.TryRequestShutdown());
    }

    [Fact]
    public void RecoveryJournalPathMatchesStorageProtocolRoot()
    {
        using var temporary = new TemporaryDirectory();

        string path = AppOfflineRestorePaths.GetJournalPath(temporary.Path);

        Assert.Equal(
            Path.Combine(Path.GetFullPath(temporary.Path), "data", "restore-journal.json"),
            path);
        Assert.DoesNotContain(Path.Combine("data", "storage"), path, StringComparison.OrdinalIgnoreCase);
    }

    private static AppStorageDataToolsController.RestoreLease CreateLease()
    {
        using var temporary = new TemporaryDirectory();
        string backup = Path.Combine(temporary.Path, "backups", "restore.itbackup");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.WriteAllText(backup, "fixture");
        var controller = new AppStorageDataToolsController(
            new SafeStorageTools(),
            new PausedCapture(),
            temporary.Path,
            () => DateTimeOffset.UnixEpoch);
        AppStorageDataToolsController.RestoreInteraction interaction = controller.TryBeginInteraction();
        RestoreSelection selection = interaction.Validate(backup);
        return interaction.TransferToRestore(selection, finalConfirmed: true);
    }

    public enum FailurePoint
    {
        None,
        Ui,
        SystemEvents,
        Capture,
        DiagnosticsOpen,
        StorageStop,
        StorageState,
        StorageDispose,
        Restore
    }

    private sealed class FakeRuntime : IAppOfflineRestoreRuntime
    {
        private StorageServiceState _state = StorageServiceState.Paused;
        private bool _acceptingWrites;
        public FailurePoint Failure { get; init; }
        public bool RecoveryRequired { get; init; }
        public List<string> Operations { get; } = [];
        public List<AppOfflineRestoreStatus> Statuses { get; } = [];
        public int UiCount { get; private set; }
        public int SystemEventCount { get; private set; }
        public int CaptureCount { get; private set; }
        public int StorageStopCount { get; private set; }
        public int StorageDisposeCount { get; private set; }
        public int RestoreCount { get; private set; }
        public int ShutdownCount { get; private set; }

        public ValueTask QuiesceUiAsync()
        {
            UiCount++;
            Operations.Add("ui");
            if (Failure == FailurePoint.Ui) throw new IOException("ui failed");
            return ValueTask.CompletedTask;
        }

        public void ReleaseSystemEventBridge()
        {
            SystemEventCount++;
            Operations.Add("system-events");
            if (Failure == FailurePoint.SystemEvents) throw new IOException("events failed");
        }

        public ValueTask ReleaseCaptureAsync()
        {
            CaptureCount++;
            Operations.Add("capture");
            if (Failure == FailurePoint.Capture) throw new IOException("capture failed");
            return ValueTask.CompletedTask;
        }

        public DiagnosticsSnapshot CaptureDiagnostics
        {
            get
            {
                Operations.Add("diagnostics");
                return new DiagnosticsSnapshot(
                    1, 1, 1, 1, 0, 0, 0, 16,
                    InputCaptureState.Stopped,
                    PauseReason.None,
                    Failure == FailurePoint.DiagnosticsOpen,
                    Failure == FailurePoint.DiagnosticsOpen,
                    1, 1, 1, null);
            }
        }

        public StorageServiceHealth StorageHealth
        {
            get
            {
                Operations.Add("storage-health");
                StorageServiceState state = Failure == FailurePoint.StorageState && _state == StorageServiceState.Stopped
                    ? StorageServiceState.IntegrityFault
                    : _state;
                bool accepting = Failure == FailurePoint.StorageState && _state == StorageServiceState.Stopped
                    || _acceptingWrites;
                return Health(state, accepting);
            }
        }

        public void StopStorage(long monotonicTimestamp)
        {
            StorageStopCount++;
            Operations.Add($"storage-stop:{monotonicTimestamp}");
            if (Failure == FailurePoint.StorageStop) throw new IOException("stop failed");
            _state = StorageServiceState.Stopped;
            _acceptingWrites = false;
        }

        public void ReleaseStorageOwner()
        {
            StorageDisposeCount++;
            Operations.Add("storage-dispose");
            if (Failure == FailurePoint.StorageDispose) throw new IOException("dispose failed");
        }

        public Task<StorageRestoreResult> RestoreOfflineAsync(string relativeBackupPath)
        {
            RestoreCount++;
            Operations.Add("restore");
            if (Failure == FailurePoint.Restore) throw new IOException("restore failed");
            return Task.FromResult(new StorageRestoreResult(
                new StorageBackupArtifact("automatic-before.itbackup", "SHA", 1, 1)));
        }

        public bool IsRecoveryRequired => RecoveryRequired;

        public void PublishStatus(AppOfflineRestoreStatus status)
        {
            Statuses.Add(status);
            Operations.Add($"status:{status.Phase}");
        }

        public void RequestShutdown()
        {
            ShutdownCount++;
            Operations.Add("shutdown");
        }
    }

    private sealed class DisposeProbe : IDisposable
    {
        public int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }

    private sealed class SafeStorageTools : IAppStorageDataTools
    {
        public StorageServiceHealth Health => AppOfflineRestoreCoordinatorTests.Health(
            StorageServiceState.Paused,
            false);
        public StorageExportArtifact ExportKeyboardCsv(StorageKeyboardPeriodResult period, string relativePath) => throw new NotSupportedException();
        public StorageExportArtifact ExportKeyboardHeatmapPng(StorageKeyboardPeriodResult period, string relativePath, int width, int height) => throw new NotSupportedException();
        public StorageBackupArtifact CreateBackup(string relativePath) => throw new NotSupportedException();
        public Task<StorageDeletionPreview> PreviewDeletionAsync(Guid deviceId, StorageUtcRange range, CancellationToken token) => throw new NotSupportedException();
        public StorageDeletionResult ExecuteDeletion(
            StorageDeletionPreview preview, string confirmationToken, string preDeletionBackupRelativePath) =>
            throw new NotSupportedException();
        public IReadOnlyList<StorageDeviceInfo> ListDevices() => [];
    }

    private sealed class PausedCapture : IInputCaptureControl
    {
        public InputCaptureStatus CurrentStatus { get; } = new(InputCaptureState.Paused, PauseReason.User, null);
        public DiagnosticsSnapshot Diagnostics => throw new NotSupportedException();
        public event EventHandler<InputCaptureStatus>? StatusChanged { add { } remove { } }
        public ValueTask SetPauseReasonAsync(PauseReason reason, bool active, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static StorageServiceHealth Health(StorageServiceState state, bool acceptingWrites) => new(
        state,
        acceptingWrites,
        new StorageDurableWatermark(0, 0, StorageDurabilityScope.SQLiteCommitted),
        null);
}
