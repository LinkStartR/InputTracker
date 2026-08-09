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
    RejectedProgramDirectory
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

    public CollectionController(DirectoryProbeResult directory, IInputCaptureControl? capture = null)
    {
        _capture = capture;
        _synchronizationContext = SynchronizationContext.Current;
        if (!directory.IsWritable)
        {
            Current = Rejected(directory.Error ?? "程序目录不可写。");
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

    public void Dispose()
    {
        if (_capture is not null)
        {
            _capture.StatusChanged -= Capture_StatusChanged;
        }
    }

    private void Capture_StatusChanged(object? sender, InputCaptureStatus status)
    {
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
            "Win32 Raw Input 正在后台接收物理键盘和鼠标事件。Storage 尚未接通，当前事件尚未持久化。",
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
