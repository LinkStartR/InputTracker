using InputTracker.Input;
using InputTracker.Storage;
using System.IO;

namespace InputTracker;

internal interface IAppStorageDataTools
{
    StorageServiceHealth Health { get; }
    StorageExportArtifact ExportKeyboardCsv(StorageKeyboardPeriodResult period, string relativePath);
    StorageExportArtifact ExportKeyboardHeatmapPng(
        StorageKeyboardPeriodResult period, string relativePath, int width, int height);
    StorageBackupArtifact CreateBackup(string relativePath);
    Task<StorageDeletionPreview> PreviewDeletionAsync(
        Guid deviceId, StorageUtcRange range, CancellationToken token);
    IReadOnlyList<StorageDeviceInfo> ListDevices();
}

internal sealed class StorageServiceDataTools(StorageService service) : IAppStorageDataTools
{
    public StorageServiceHealth Health => service.Health;
    public StorageExportArtifact ExportKeyboardCsv(StorageKeyboardPeriodResult period, string relativePath) =>
        service.ExportKeyboardCsv(period, relativePath);
    public StorageExportArtifact ExportKeyboardHeatmapPng(
        StorageKeyboardPeriodResult period, string relativePath, int width, int height) =>
        service.ExportKeyboardHeatmapPng(period, relativePath, width, height);
    public StorageBackupArtifact CreateBackup(string relativePath) => service.CreateBackup(relativePath);
    public Task<StorageDeletionPreview> PreviewDeletionAsync(
        Guid deviceId, StorageUtcRange range, CancellationToken token) =>
        service.PreviewDeletionAsync(deviceId, range, token);
    public IReadOnlyList<StorageDeviceInfo> ListDevices() => service.ListDevices();
}

internal sealed record StorageDataToolsExportResult(
    StorageExportArtifact Csv,
    StorageExportArtifact Png);

internal sealed record StorageDeletionDisplay(
    Guid DeviceId,
    StorageDeviceKind DeviceKind,
    StorageUtcRange UtcRange,
    long KeyboardAggregateCount,
    string Status);

internal sealed record RestoreSelection(
    string RelativePath,
    bool Confirmed,
    bool CanExecute,
    string Status);

internal sealed record AppOfflineRestoreRequest(string RelativePath);

internal sealed class AppStorageDataToolsController
{
    private const int HeatmapWidth = 1440;
    private const int HeatmapHeight = 900;
    private readonly IAppStorageDataTools _storage;
    private readonly IInputCaptureControl _capture;
    private readonly string _programDirectory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _lifecycleGate = new();
    private bool _closing;

    public AppStorageDataToolsController(
        IAppStorageDataTools storage,
        IInputCaptureControl capture,
        string programDirectory,
        Func<DateTimeOffset>? clock = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        ArgumentException.ThrowIfNullOrWhiteSpace(programDirectory);
        _programDirectory = Path.GetFullPath(programDirectory);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public Task<StorageDataToolsExportResult> ExportKeyboardAsync(
        StorageKeyboardPeriodResult period,
        CancellationToken cancellationToken = default) => RunExclusiveAsync(
            () => Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                string suffix = Timestamp();
                StorageExportArtifact csv = _storage.ExportKeyboardCsv(
                    period, Path.Combine("data", "exports", $"keyboard-{suffix}.csv"));
                StorageExportArtifact png = _storage.ExportKeyboardHeatmapPng(
                    period, Path.Combine("data", "exports", $"keyboard-{suffix}.png"),
                    HeatmapWidth, HeatmapHeight);
                return new StorageDataToolsExportResult(csv, png);
            }, cancellationToken), cancellationToken);

    public async Task<StorageBackupArtifact> CreateBackupAsync(CancellationToken cancellationToken = default)
    {
        return await RunExclusiveAsync(async () =>
        {
            if ((_capture.CurrentStatus.PauseReasons & PauseReason.User) == 0)
            {
                throw new InvalidOperationException("创建备份前必须由用户明确点击暂停；InputTracker 不会为备份自动暂停或自动恢复。");
            }

            StorageServiceHealth health = _storage.Health;
            if (health.State != StorageServiceState.Paused || health.AcceptingWrites)
            {
                throw new InvalidOperationException("Storage did not reach a safe paused backup boundary.");
            }

            return await Task.Run(
                () => _storage.CreateBackup(Path.Combine(
                    "backups", $"InputTracker-{Timestamp()}.itbackup")),
                cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<StorageDeletionDisplay> PreviewDeletionAsync(
        Guid deviceId,
        StorageDeviceKind expectedKind,
        StorageUtcRange range,
        CancellationToken cancellationToken = default)
    {
        if (expectedKind != StorageDeviceKind.Keyboard)
        {
            throw new InvalidOperationException("鼠标轨迹功能已移除，没有可预览的鼠标轨迹删除范围。");
        }

        return await RunExclusiveAsync(async () =>
        {
            StorageDeviceInfo? device = _storage.ListDevices()
                .SingleOrDefault(value => value.DeviceId == deviceId);
            if (device is null || device.Kind != expectedKind)
            {
                throw new InvalidDataException("删除预览目标设备不存在或类型与明确选择不一致。");
            }

            StorageDeletionPreview preview = await _storage.PreviewDeletionAsync(
                deviceId, range, cancellationToken).ConfigureAwait(false);
            if (preview.DeviceId != deviceId || preview.UtcRange != range)
            {
                throw new InvalidDataException("Storage returned a deletion preview for a different device or range.");
            }
            string status = preview.Capability == StorageDeletionCapability.BlockedByCurrentFormat
                ? $"当前存储格式阻塞实际范围删除：{preview.BlockedReason}"
                : "删除能力状态未知，未执行任何删除。";
            return new StorageDeletionDisplay(
                preview.DeviceId,
                expectedKind,
                preview.UtcRange,
                preview.KeyboardAggregateCount,
                status);
        }, cancellationToken).ConfigureAwait(false);
    }

    private RestoreSelection ValidateRestoreSelectionCore(string selectedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedPath);
        string fullPath = Path.GetFullPath(selectedPath);
        string relative = Path.GetRelativePath(_programDirectory, fullPath);
        if (relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative)
            || !string.Equals(Path.GetExtension(fullPath), ".itbackup", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("恢复文件必须是程序目录内的 .itbackup 文件。");
        }

        ValidateNoReparseChain(fullPath);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("恢复文件不存在。", fullPath);
        return new RestoreSelection(
            relative,
            Confirmed: false,
            CanExecute: false,
            "恢复备份已验证；尚未停止采集或更改数据。请阅读最终确认后再继续。");
    }

    private RestoreLease TransferToRestoreCore(RestoreSelection selection, bool finalConfirmed)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (!finalConfirmed)
            throw new InvalidOperationException("离线恢复需要明确的最终二次确认。");

        InputCaptureStatus capture = _capture.CurrentStatus;
        if (capture.State != InputCaptureState.Paused
            || (capture.PauseReasons & PauseReason.User) == 0)
        {
            throw new InvalidOperationException(
                "离线恢复前必须由用户明确暂停；锁屏或系统挂起不构成恢复授权。");
        }

        StorageServiceHealth storage = _storage.Health;
        if (storage.State != StorageServiceState.Paused || storage.AcceptingWrites)
        {
            throw new InvalidOperationException(
                "Storage 未到达暂停且拒绝新写入的离线恢复边界。");
        }

        lock (_lifecycleGate)
        {
            if (_closing)
                throw new InvalidOperationException("InputTracker 正在退出，不再接受恢复请求。");
            _closing = true;
        }

        return new RestoreLease(new AppOfflineRestoreRequest(selection.RelativePath));
    }

    public RestoreInteraction TryBeginInteraction()
    {
        if (!_operationGate.Wait(0))
            throw new InvalidOperationException("已有本地数据操作或恢复选择正在执行；请等待其完成后重试。");
        lock (_lifecycleGate)
        {
            if (_closing)
            {
                _operationGate.Release();
                throw new InvalidOperationException("InputTracker 正在退出，不再接受新的数据操作。");
            }
            return new RestoreInteraction(this);
        }
    }

    public bool TryBeginExplicitExit()
    {
        lock (_lifecycleGate)
        {
            if (_closing) return false;
            _closing = true;
            if (!_operationGate.Wait(0))
            {
                _closing = false;
                return false;
            }
            return true;
        }
    }

    public bool BeginSessionEnding(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        lock (_lifecycleGate) _closing = true;
        return _operationGate.Wait(timeout);
    }

    public bool IsClosing
    {
        get { lock (_lifecycleGate) return _closing; }
    }

    private async Task<T> RunExclusiveAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ThrowIfClosing();
        if (!await _operationGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("已有本地数据操作正在执行；请等待其完成后重试。");
        }

        try
        {
            return await operation().ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private void ThrowIfClosing()
    {
        lock (_lifecycleGate)
        {
            if (_closing) throw new InvalidOperationException("InputTracker 正在退出，不再接受新的数据操作。");
        }
    }

    internal sealed class RestoreInteraction(AppStorageDataToolsController owner) : IDisposable
    {
        private AppStorageDataToolsController? _owner = owner;
        private RestoreSelection? _validatedSelection;

        public RestoreSelection Validate(string selectedPath)
        {
            AppStorageDataToolsController current = _owner
                ?? throw new ObjectDisposedException(nameof(RestoreInteraction));
            if (_validatedSelection is not null)
                throw new InvalidOperationException("同一次恢复交互只能验证一个备份选择。");
            current.ThrowIfClosing();
            return _validatedSelection = current.ValidateRestoreSelectionCore(selectedPath);
        }

        public RestoreLease TransferToRestore(RestoreSelection selection, bool finalConfirmed)
        {
            AppStorageDataToolsController current = _owner
                ?? throw new ObjectDisposedException(nameof(RestoreInteraction));
            if (!ReferenceEquals(selection, _validatedSelection))
                throw new InvalidOperationException("只能转交本次交互实际验证过的恢复选择。");
            RestoreLease lease = current.TransferToRestoreCore(selection, finalConfirmed);
            _owner = null;
            _validatedSelection = null;
            return lease;
        }

        public void Dispose()
        {
            AppStorageDataToolsController? current = Interlocked.Exchange(ref _owner, null);
            current?._operationGate.Release();
        }
    }

    internal sealed class RestoreLease(AppOfflineRestoreRequest request)
    {
        private AppOfflineRestoreRequest? _request = request;

        public AppOfflineRestoreRequest Consume() => Interlocked.Exchange(ref _request, null)
            ?? throw new InvalidOperationException("恢复 lease 已被使用，不能重复执行。");
    }

    private string Timestamp() => _clock().UtcDateTime.ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);

    private void ValidateNoReparseChain(string filePath)
    {
        string current = _programDirectory;
        if (!Directory.Exists(current) || (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("程序目录缺失或为 reparse point。");
        foreach (string part in Path.GetRelativePath(
            _programDirectory, Path.GetDirectoryName(filePath)!).Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if (!Directory.Exists(current) || (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("恢复路径目录链缺失或包含 reparse point。");
        }
        if (File.Exists(filePath) && (File.GetAttributes(filePath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("恢复文件不能是 reparse point。");
    }
}
