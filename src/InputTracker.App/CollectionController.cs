using InputTracker.Input;

namespace InputTracker;

public enum CollectionMode
{
    NotCollecting,
    Starting,
    Collecting,
    Paused,
    IntegrityFault,
    ControlUncertain,
    StartFailed,
    StorageStartFailed,
    ConsentRequired,
    RejectedProgramDirectory,
    Restoring,
    RestoreSucceededAwaitExit,
    RestoreFailedAwaitExit,
    RestoreRecoveryRequired,
    RestoreControlUncertainAwaitExit
}

public sealed record CollectionSnapshot(
    CollectionMode Mode,
    string Label,
    string Explanation,
    bool CanPause,
    bool CanResume);

public sealed class CollectionController : IDisposable
{
    private readonly IInputCaptureControl? _capture;
    private readonly SynchronizationContext? _synchronizationContext;
    private bool _offlineRestoreActive;

    public CollectionController(
        DirectoryProbeResult directory,
        IInputCaptureControl? capture = null,
        string? storageStartupError = null,
        bool collectionConsentGranted = true,
        string? consentError = null)
    {
        _capture = capture;
        _synchronizationContext = SynchronizationContext.Current;
        if (!directory.IsWritable)
        {
            Current = Rejected(directory.Error ?? "程序目录不可写。");
        }
        else if (!collectionConsentGranted)
        {
            Current = new CollectionSnapshot(
                CollectionMode.ConsentRequired,
                "等待采集确认 · 未采集",
                consentError is null
                    ? "你尚未确认采集边界。Storage 和 Raw Input 均未启动；重新启动并确认后才会开始采集。"
                    : $"采集确认配置无法安全读取。Storage 和 Raw Input 均未启动；请检查程序目录中的 {PortableSettingsStore.FileName}。{consentError}",
                CanPause: false,
                CanResume: false);
        }
        else if (!string.IsNullOrWhiteSpace(storageStartupError))
        {
            Current = new CollectionSnapshot(
                CollectionMode.StorageStartFailed,
                "存储启动失败 · 未采集",
                $"Storage 未能安全启动，Raw Input 未启动。现有文件不会被自动修复或删除。{storageStartupError}",
                CanPause: false,
                CanResume: false);
        }
        else if (capture is null)
        {
            Current = NotCollecting();
        }
        else
        {
            capture.StatusChanged += Capture_StatusChanged;
            Current = FromCapture(capture.CurrentStatus);
        }
    }

    public event EventHandler<CollectionSnapshot>? Changed;

    public CollectionSnapshot Current { get; private set; }

    public void HandleCaptureControlException(string operation, Exception error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(error);

        InputCaptureStatus? status = _capture?.CurrentStatus;
        if (status?.State is InputCaptureState.IntegrityFault or InputCaptureState.StartFailed)
        {
            Capture_StatusChanged(this, status);
            return;
        }

        DiagnosticsSnapshot? diagnostics = _capture?.Diagnostics;
        bool captureClosed = diagnostics is not null
            && !diagnostics.IngressOpen
            && !diagnostics.RawInputRegistered;
        Set(captureClosed
            ? new CollectionSnapshot(
                CollectionMode.IntegrityFault,
                "采集控制失败 · 入口已关闭",
                $"{operation}失败；采集入口和 Raw Input 注册均已关闭。{error.Message}",
                CanPause: false,
                CanResume: false)
            : Uncertain(operation, error.Message));
    }

    public void HandlePreCaptureStartException(string operation, Exception error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(error);
        Set(new CollectionSnapshot(
            CollectionMode.StartFailed,
            "采集启动失败 · 未采集",
            $"{operation}失败；Raw Input 尚未启动，InputTracker 不会假装正在采集。{error.Message}",
            CanPause: false,
            CanResume: false));
    }

    public async Task PauseAsync()
    {
        if (Current.Mode == CollectionMode.RejectedProgramDirectory)
        {
            return;
        }

        if (_capture is null)
        {
            Set(new CollectionSnapshot(
                CollectionMode.Paused,
                "已暂停",
                "用户已暂停采集控制链。当前没有输入采集服务。",
                CanPause: false,
                CanResume: true));
            return;
        }

        await _capture.SetPauseReasonAsync(PauseReason.User, true);
    }

    public async Task ResumeAsync()
    {
        if (Current.Mode == CollectionMode.RejectedProgramDirectory)
        {
            return;
        }

        if (_capture is null)
        {
            Set(NotCollecting());
            return;
        }

        await _capture.SetPauseReasonAsync(PauseReason.User, false);
    }

    internal void HandleOfflineRestoreStatus(AppOfflineRestoreStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        _offlineRestoreActive = true;
        Set(status.Phase switch
        {
            AppOfflineRestorePhase.Restoring => new CollectionSnapshot(
                CollectionMode.Restoring,
                "正在停止采集 · 恢复准备中",
                $"尚未确认 Raw Input 已完全关闭。{status.Message}",
                CanPause: false,
                CanResume: false),
            AppOfflineRestorePhase.SucceededAwaitExit => new CollectionSnapshot(
                CollectionMode.RestoreSucceededAwaitExit,
                "恢复成功 · 正在退出",
                status.Message,
                CanPause: false,
                CanResume: false),
            AppOfflineRestorePhase.RecoveryRequired => new CollectionSnapshot(
                CollectionMode.RestoreRecoveryRequired,
                "恢复中断 · 正在退出",
                $"可能需要下次启动恢复一致状态。{status.Message}",
                CanPause: false,
                CanResume: false),
            AppOfflineRestorePhase.FailedAwaitExit when !status.CaptureConfirmedClosed => new CollectionSnapshot(
                CollectionMode.RestoreControlUncertainAwaitExit,
                "恢复停止边界失败 · 正在退出",
                $"Raw Input 是否完全关闭仍不确定；InputTracker 正在退出且不会恢复采集。{status.Message}",
                CanPause: false,
                CanResume: false),
            _ => new CollectionSnapshot(
                CollectionMode.RestoreFailedAwaitExit,
                "恢复失败 · 正在退出",
                status.Message,
                CanPause: false,
                CanResume: false)
        });
    }

    public void Dispose()
    {
        if (_capture is not null)
        {
            _capture.StatusChanged -= Capture_StatusChanged;
        }
    }

    private void Capture_StatusChanged(object? sender, InputCaptureStatus status)
    {
        if (_offlineRestoreActive) return;
        if (_synchronizationContext is not null && SynchronizationContext.Current != _synchronizationContext)
        {
            _synchronizationContext.Post(_ => Set(FromCapture(status)), null);
            return;
        }

        Set(FromCapture(status));
    }

    private void Set(CollectionSnapshot snapshot)
    {
        Current = snapshot;
        Changed?.Invoke(this, snapshot);
    }

    private static CollectionSnapshot NotCollecting() => new(
        CollectionMode.NotCollecting,
        "未采集",
        "安全外壳已就绪。Raw Input 采集将在后续阶段启用。",
        CanPause: true,
        CanResume: false);

    private CollectionSnapshot FromCapture(InputCaptureStatus status) => status.State switch
    {
        InputCaptureState.Starting => new CollectionSnapshot(
            CollectionMode.Starting,
            "正在启动采集",
            "正在初始化 Win32 Raw Input 后台采集。",
            CanPause: false,
            CanResume: false),
        InputCaptureState.Running => new CollectionSnapshot(
            CollectionMode.Collecting,
            "采集中",
            "Win32 Raw Input 正在后台接收物理键盘和鼠标事件，并写入程序目录中的 Storage。",
            CanPause: true,
            CanResume: false),
        InputCaptureState.Paused => new CollectionSnapshot(
            CollectionMode.Paused,
            "已暂停",
            $"后台输入接收已停止并完成暂停边界。暂停原因：{FormatReasons(status.PauseReasons)}。",
            CanPause: false,
            CanResume: (status.PauseReasons & PauseReason.User) != 0),
        InputCaptureState.IntegrityFault => CaptureFault(status),
        InputCaptureState.StartFailed => new CollectionSnapshot(
            CollectionMode.StartFailed,
            "采集启动失败",
            $"Raw Input 未能启动，InputTracker 不会假装正在采集。{status.Error}",
            CanPause: false,
            CanResume: false),
        InputCaptureState.Stopping => new CollectionSnapshot(
            CollectionMode.Paused,
            "正在停止采集",
            "正在注销 Raw Input 并排空已接受事件。",
            CanPause: false,
            CanResume: false),
        _ => NotCollecting()
    };

    private CollectionSnapshot CaptureFault(InputCaptureStatus status)
    {
        DiagnosticsSnapshot? diagnostics = _capture?.Diagnostics;
        if (diagnostics is null || diagnostics.IngressOpen || diagnostics.RawInputRegistered)
        {
            return Uncertain("完整性故障后的采集控制", status.Error);
        }

        return new CollectionSnapshot(
            CollectionMode.IntegrityFault,
            "完整性故障 · 已停止",
            $"InputTracker 检测到无法保证事件完整性的故障，已停止采集。{status.Error}",
            CanPause: false,
            CanResume: false);
    }

    private static CollectionSnapshot Uncertain(string operation, string? error) => new(
        CollectionMode.ControlUncertain,
        "采集状态不确定",
        $"{operation}失败；采集可能仍在运行。请退出 InputTracker 以结束采集。{error}",
        CanPause: false,
        CanResume: false);

    private static string FormatReasons(PauseReason reasons)
    {
        var labels = new List<string>(3);
        if ((reasons & PauseReason.User) != 0) labels.Add("用户暂停");
        if ((reasons & PauseReason.SessionLocked) != 0) labels.Add("会话锁定");
        if ((reasons & PauseReason.SystemSuspend) != 0) labels.Add("系统挂起");
        return labels.Count == 0 ? "未知" : string.Join("、", labels);
    }

    private static CollectionSnapshot Rejected(string error) => new(
        CollectionMode.RejectedProgramDirectory,
        "已暂停 · 采集已拒绝",
        $"程序目录不可写。InputTracker 不会改用 AppData 或其他目录。{Environment.NewLine}{error}",
        CanPause: false,
        CanResume: false);
}
