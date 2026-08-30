using InputTracker.Input;
using InputTracker.Storage;
using System.IO;

namespace InputTracker;

internal enum AppOfflineRestorePhase
{
    Restoring = 0,
    SucceededAwaitExit = 1,
    FailedAwaitExit = 2,
    RecoveryRequired = 3
}

internal sealed record AppOfflineRestoreStatus(
    AppOfflineRestorePhase Phase,
    string Message,
    string? PreRestoreBackupPath,
    bool CaptureConfirmedClosed);

internal interface IAppOfflineRestoreRuntime
{
    ValueTask QuiesceUiAsync();
    void ReleaseSystemEventBridge();
    ValueTask ReleaseCaptureAsync();
    DiagnosticsSnapshot CaptureDiagnostics { get; }
    StorageServiceHealth StorageHealth { get; }
    void StopStorage(long monotonicTimestamp);
    void ReleaseStorageOwner();
    Task<StorageRestoreResult> RestoreOfflineAsync(string relativeBackupPath);
    bool IsRecoveryRequired { get; }
    void PublishStatus(AppOfflineRestoreStatus status);
    void RequestShutdown();
}

internal sealed class AppOfflineRestoreCoordinator(
    IAppOfflineRestoreRuntime runtime,
    Func<long> monotonicTimestamp)
{
    private readonly IAppOfflineRestoreRuntime _runtime = runtime
        ?? throw new ArgumentNullException(nameof(runtime));
    private readonly Func<long> _monotonicTimestamp = monotonicTimestamp
        ?? throw new ArgumentNullException(nameof(monotonicTimestamp));

    public async Task ExecuteAsync(AppStorageDataToolsController.RestoreLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        AppOfflineRestoreRequest request = lease.Consume();
        Exception? firstError = null;
        bool restoreAllowed = true;
        bool captureConfirmedClosed = false;

        async ValueTask RunAsync(Func<ValueTask> action)
        {
            try
            {
                await action();
            }
            catch (Exception error)
            {
                firstError ??= error;
                restoreAllowed = false;
            }
        }

        void Run(Action action)
        {
            try
            {
                action();
            }
            catch (Exception error)
            {
                firstError ??= error;
                restoreAllowed = false;
            }
        }

        try
        {
            await RunAsync(_runtime.QuiesceUiAsync);
            Run(() => _runtime.PublishStatus(new AppOfflineRestoreStatus(
                AppOfflineRestorePhase.Restoring,
                "正在停止采集并释放本地数据所有权；恢复完成前不会重新开始采集。",
                null,
                CaptureConfirmedClosed: false)));
            Run(_runtime.ReleaseSystemEventBridge);
            await RunAsync(_runtime.ReleaseCaptureAsync);
            Run(() =>
            {
                DiagnosticsSnapshot diagnostics = _runtime.CaptureDiagnostics;
                if (diagnostics.IngressOpen || diagnostics.RawInputRegistered)
                {
                    throw new InvalidOperationException(
                        "Raw Input teardown diagnostics remained open; offline restore was refused.");
                }
                captureConfirmedClosed = true;
            });

            StorageServiceHealth? storageBeforeStop = null;
            Run(() => storageBeforeStop = _runtime.StorageHealth);
            if (storageBeforeStop?.State is StorageServiceState.Running or StorageServiceState.Paused)
            {
                Run(() => _runtime.StopStorage(_monotonicTimestamp()));
            }
            else
            {
                firstError ??= new InvalidOperationException(
                    "Storage was not in a stoppable state at the offline restore boundary.");
                restoreAllowed = false;
            }

            Run(() =>
            {
                StorageServiceHealth health = _runtime.StorageHealth;
                if (health.State != StorageServiceState.Stopped || health.AcceptingWrites)
                {
                    throw new InvalidOperationException(
                        "Storage did not reach Stopped with writes rejected; offline restore was refused.");
                }
            });
            Run(_runtime.ReleaseStorageOwner);

            if (restoreAllowed)
            {
                try
                {
                    StorageRestoreResult result = await _runtime.RestoreOfflineAsync(request.RelativePath);
                    Run(() => _runtime.PublishStatus(new AppOfflineRestoreStatus(
                        AppOfflineRestorePhase.SucceededAwaitExit,
                        "离线恢复成功。InputTracker 将退出；请手动重新启动以重新取得 Storage 所有权。",
                        result.PreRestoreBackup.Path,
                        CaptureConfirmedClosed: true)));
                }
                catch (Exception error)
                {
                    firstError ??= error;
                    bool recoveryRequired;
                    try { recoveryRequired = _runtime.IsRecoveryRequired; }
                    catch { recoveryRequired = true; }
                    Run(() => _runtime.PublishStatus(new AppOfflineRestoreStatus(
                        recoveryRequired
                            ? AppOfflineRestorePhase.RecoveryRequired
                            : AppOfflineRestorePhase.FailedAwaitExit,
                        recoveryRequired
                            ? $"离线恢复中断，检测到待恢复日志；下次启动会先尝试恢复一致状态。{error.Message}"
                            : $"离线恢复失败，当前进程不会重新打开 Storage 或恢复采集。{error.Message}",
                        null,
                        CaptureConfirmedClosed: true)));
                }
            }
            else
            {
                Run(() => _runtime.PublishStatus(new AppOfflineRestoreStatus(
                    AppOfflineRestorePhase.FailedAwaitExit,
                    $"离线恢复在停止边界失败，未调用恢复入口；当前进程不会恢复采集。{firstError?.Message}",
                    null,
                    captureConfirmedClosed)));
            }
        }
        finally
        {
            _runtime.RequestShutdown();
        }
    }
}

internal static class AppOfflineRestoreOwnership
{
    public static T? Take<T>(ref T? owner) where T : class =>
        Interlocked.Exchange(ref owner, null);
}

internal sealed class AppOfflineRestoreLifecycleGate
{
    private int _restoreActive;
    private int _shutdownRequested;

    public bool TryBeginRestore() => Interlocked.Exchange(ref _restoreActive, 1) == 0;
    public bool RestoreActive => Volatile.Read(ref _restoreActive) != 0;
    public bool AllowsNormalExitResourceCleanup => !RestoreActive;
    public bool TryRequestShutdown() => Interlocked.Exchange(ref _shutdownRequested, 1) == 0;
}

internal static class AppOfflineRestorePaths
{
    public static string GetJournalPath(string programDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(programDirectory);
        return Path.Combine(Path.GetFullPath(programDirectory), "data", "restore-journal.json");
    }
}
