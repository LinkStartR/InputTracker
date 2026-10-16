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
                "等待你的确认 · 未开始记录",
                consentError is null
                    ? "你还没有允许开始记录。确认前不会接收键盘或鼠标活动；重新启动并确认后才会开始。"
                    : $"无法读取你的记录许可，当前不会接收键盘或鼠标活动。请检查程序文件夹中的 {PortableSettingsStore.FileName}。{consentError}",
                CanPause: false,
                CanResume: false);
        }
        else if (!string.IsNullOrWhiteSpace(storageStartupError))
        {
            Current = new CollectionSnapshot(
                CollectionMode.StorageStartFailed,
                "本地数据无法打开 · 未开始记录",
                $"本地数据没有成功打开，因此没有开始记录。现有文件不会被自动修复或删除。{storageStartupError}",
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
                "记录控制失败 · 已停止",
                $"{operation}失败，后台接收已经关闭。{error.Message}",
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
            "记录启动失败 · 未开始记录",
            $"{operation}失败，当前没有接收键盘或鼠标活动。{error.Message}",
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
                "已经暂停。当前没有运行中的记录服务。",
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
                "正在停止记录 · 准备恢复",
                $"后台接收还在关闭中，请稍候。{status.Message}",
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
                "恢复准备失败 · 正在退出",
                $"无法确认后台接收是否完全关闭；InputTracker 正在退出，也不会重新开始记录。{status.Message}",
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
        "未开始记录",
        "程序已准备好，当前没有接收键盘或鼠标活动。",
        CanPause: true,
        CanResume: false);

    private CollectionSnapshot FromCapture(InputCaptureStatus status) => status.State switch
    {
        InputCaptureState.Starting => new CollectionSnapshot(
            CollectionMode.Starting,
            "正在开始记录",
            "正在连接键盘和鼠标…",
            CanPause: false,
            CanResume: false),
        InputCaptureState.Running => new CollectionSnapshot(
            CollectionMode.Collecting,
            "记录中",
            "正在后台记录键盘和鼠标活动，数据只保存在当前程序文件夹里。",
            CanPause: true,
            CanResume: false),
        InputCaptureState.Paused => new CollectionSnapshot(
            CollectionMode.Paused,
            "已暂停",
            $"已经停止接收新的键盘和鼠标活动，暂停前的数据已保存。原因：{FormatReasons(status.PauseReasons)}。",
            CanPause: false,
            CanResume: (status.PauseReasons & PauseReason.User) != 0),
        InputCaptureState.IntegrityFault => CaptureFault(status),
        InputCaptureState.StartFailed => new CollectionSnapshot(
            CollectionMode.StartFailed,
            "记录启动失败",
            $"当前没有接收键盘或鼠标活动。{status.Error}",
            CanPause: false,
            CanResume: false),
        InputCaptureState.Stopping => new CollectionSnapshot(
            CollectionMode.Paused,
            "正在停止",
            "正在保存最后一批记录…",
            CanPause: false,
            CanResume: false),
        _ => NotCollecting()
    };

    private CollectionSnapshot CaptureFault(InputCaptureStatus status)
    {
        DiagnosticsSnapshot? diagnostics = _capture?.Diagnostics;
        if (diagnostics is null || diagnostics.IngressOpen || diagnostics.RawInputRegistered)
        {
            return Uncertain("记录异常后的停止操作", status.Error);
        }

        return new CollectionSnapshot(
            CollectionMode.IntegrityFault,
            "记录异常 · 已停止",
            $"为避免统计不准确，InputTracker 已停止记录。{status.Error}",
            CanPause: false,
            CanResume: false);
    }

    private static CollectionSnapshot Uncertain(string operation, string? error) => new(
        CollectionMode.ControlUncertain,
        "记录状态不确定",
        $"{operation}失败，后台可能仍在接收活动。请退出 InputTracker。{error}",
        CanPause: false,
        CanResume: false);

    private static string FormatReasons(PauseReason reasons)
    {
        var labels = new List<string>(3);
        if ((reasons & PauseReason.User) != 0) labels.Add("你已暂停");
        if ((reasons & PauseReason.SessionLocked) != 0) labels.Add("电脑已锁定");
        if ((reasons & PauseReason.SystemSuspend) != 0) labels.Add("电脑正在休眠");
        return labels.Count == 0 ? "未知" : string.Join("、", labels);
    }

    private static CollectionSnapshot Rejected(string error) => new(
        CollectionMode.RejectedProgramDirectory,
        "已暂停 · 未开始记录",
        $"当前程序文件夹不能写入。InputTracker 不会把数据改存到 AppData 或其他位置。{Environment.NewLine}{error}",
        CanPause: false,
        CanResume: false);
}
