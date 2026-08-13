namespace InputTracker.App.Tests;

using InputTracker.Input;

public sealed class CollectionControllerTests
{
    [Fact]
    public async Task WritableDirectoryStartsHonestlyNotCollectingAndCanPause()
    {
        var controller = new CollectionController(new DirectoryProbeResult(true, @"C:\portable", null));

        Assert.Equal(CollectionMode.NotCollecting, controller.Current.Mode);
        Assert.True(controller.Current.CanPause);

        await controller.PauseAsync();
        Assert.Equal(CollectionMode.Paused, controller.Current.Mode);
        Assert.True(controller.Current.CanResume);

        await controller.ResumeAsync();
        Assert.Equal(CollectionMode.NotCollecting, controller.Current.Mode);
    }

    [Fact]
    public async Task RejectedDirectoryCannotBeResumed()
    {
        var controller = new CollectionController(new DirectoryProbeResult(false, @"C:\locked", "拒绝访问"));

        await controller.ResumeAsync();

        Assert.Equal(CollectionMode.RejectedProgramDirectory, controller.Current.Mode);
        Assert.False(controller.Current.CanPause);
        Assert.False(controller.Current.CanResume);
        Assert.Contains("不会改用 AppData", controller.Current.Explanation);
    }

    [Fact]
    public void MissingConsentIsExplicitAndCannotCollect()
    {
        var controller = new CollectionController(
            new DirectoryProbeResult(true, @"C:\portable", null),
            collectionConsentGranted: false);

        Assert.Equal(CollectionMode.ConsentRequired, controller.Current.Mode);
        Assert.Contains("Storage 和 Raw Input 均未启动", controller.Current.Explanation);
        Assert.False(controller.Current.CanPause);
        Assert.False(controller.Current.CanResume);
    }

    [Fact]
    public void PreCaptureBoundaryFailureIsKnownNotCollecting()
    {
        var capture = new FakeCapture(InputCaptureState.Stopped);
        using var controller = new CollectionController(
            new DirectoryProbeResult(true, @"C:\portable", null), capture);

        controller.HandlePreCaptureStartException(
            "注册锁屏采集边界",
            new InvalidOperationException("registration denied"));

        Assert.Equal(CollectionMode.StartFailed, controller.Current.Mode);
        Assert.Contains("Raw Input 尚未启动", controller.Current.Explanation);
        Assert.DoesNotContain("可能仍在运行", controller.Current.Explanation);
        Assert.False(controller.Current.CanPause);
        Assert.False(controller.Current.CanResume);
    }

    [Fact]
    public async Task CaptureStatesAreShownHonestlyAndUserPauseUsesReasonedControl()
    {
        var capture = new FakeCapture(InputCaptureState.Running);
        using var controller = new CollectionController(
            new DirectoryProbeResult(true, @"C:\portable", null),
            capture);

        Assert.Equal(CollectionMode.Collecting, controller.Current.Mode);
        Assert.Equal("采集中", controller.Current.Label);
        Assert.Contains("后台接收物理键盘和鼠标事件", controller.Current.Explanation);
        Assert.Contains("写入程序目录中的 Storage", controller.Current.Explanation);

        await controller.PauseAsync();
        Assert.Equal(PauseReason.User, capture.CurrentStatus.PauseReasons);
        Assert.Equal(CollectionMode.Paused, controller.Current.Mode);
        Assert.Equal("已暂停", controller.Current.Label);
        Assert.Contains("后台输入接收已停止", controller.Current.Explanation);

        capture.Publish(InputCaptureState.IntegrityFault, PauseReason.User, "queue overflow");
        Assert.Equal(CollectionMode.IntegrityFault, controller.Current.Mode);
        Assert.Equal("完整性故障 · 已停止", controller.Current.Label);
        Assert.Contains("queue overflow", controller.Current.Explanation);
        Assert.False(controller.Current.CanResume);
    }

    [Fact]
    public void PublishedStartFailureIsExplicitlyRenderedWhenStartupThrows()
    {
        var capture = new FakeCapture(InputCaptureState.Running);
        using var controller = new CollectionController(
            new DirectoryProbeResult(true, @"C:\portable", null),
            capture);
        capture.Publish(InputCaptureState.StartFailed, PauseReason.None, "registration denied");

        controller.HandleCaptureControlException("启动采集", new IOException("start failed"));

        Assert.Equal(CollectionMode.StartFailed, controller.Current.Mode);
        Assert.Equal("采集启动失败", controller.Current.Label);
        Assert.Contains("不会假装正在采集", controller.Current.Explanation);
        Assert.Contains("registration denied", controller.Current.Explanation);
        Assert.False(controller.Current.CanPause);
        Assert.False(controller.Current.CanResume);
    }

    [Fact]
    public void UnexpectedControlExceptionBecomesVisibleUncertainState()
    {
        var capture = new FakeCapture(InputCaptureState.Running);
        using var controller = new CollectionController(
            new DirectoryProbeResult(true, @"C:\portable", null),
            capture);

        controller.HandleCaptureControlException("暂停采集", new InvalidOperationException("unexpected failure"));

        Assert.Equal(CollectionMode.ControlUncertain, controller.Current.Mode);
        Assert.Contains("暂停采集", controller.Current.Explanation);
        Assert.Contains("unexpected failure", controller.Current.Explanation);
        Assert.Contains("采集可能仍在运行", controller.Current.Explanation);
        Assert.DoesNotContain("已停止", controller.Current.Label + controller.Current.Explanation);
        Assert.False(controller.Current.CanPause);
        Assert.False(controller.Current.CanResume);
    }

    [Fact]
    public void IntegrityFaultWithOpenRegistrationIsRenderedAsControlUncertain()
    {
        var capture = new FakeCapture(InputCaptureState.Running)
        {
            IngressOpenOverride = true,
            RawInputRegisteredOverride = true
        };
        using var controller = new CollectionController(
            new DirectoryProbeResult(true, @"C:\portable", null),
            capture);

        capture.Publish(InputCaptureState.IntegrityFault, PauseReason.None, "unregister failed");

        Assert.Equal(CollectionMode.ControlUncertain, controller.Current.Mode);
        Assert.Contains("采集可能仍在运行", controller.Current.Explanation);
        Assert.Contains("unregister failed", controller.Current.Explanation);
        Assert.DoesNotContain("已停止", controller.Current.Label + controller.Current.Explanation);
    }

    private sealed class FakeCapture(InputCaptureState initialState) : IInputCaptureControl
    {
        public InputCaptureStatus CurrentStatus { get; private set; } = new(initialState, PauseReason.None, null);
        public bool? IngressOpenOverride { get; set; }
        public bool? RawInputRegisteredOverride { get; set; }
        public DiagnosticsSnapshot Diagnostics => new(
            0, 0, 0, 0, 0, 0, 0, 1,
            CurrentStatus.State,
            CurrentStatus.PauseReasons,
            IngressOpenOverride ?? CurrentStatus.State == InputCaptureState.Running,
            RawInputRegisteredOverride ?? CurrentStatus.State == InputCaptureState.Running,
            0, 0, 0,
            CurrentStatus.Error);

        public event EventHandler<InputCaptureStatus>? StatusChanged;

        public ValueTask SetPauseReasonAsync(
            PauseReason reason,
            bool active,
            CancellationToken cancellationToken = default)
        {
            PauseReason updated = active
                ? CurrentStatus.PauseReasons | reason
                : CurrentStatus.PauseReasons & ~reason;
            Publish(updated == PauseReason.None ? InputCaptureState.Running : InputCaptureState.Paused, updated, null);
            return ValueTask.CompletedTask;
        }

        public void Publish(InputCaptureState state, PauseReason reasons, string? error)
        {
            CurrentStatus = new InputCaptureStatus(state, reasons, error);
            StatusChanged?.Invoke(this, CurrentStatus);
        }
    }
}
