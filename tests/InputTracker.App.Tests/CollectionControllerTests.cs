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
        Assert.Contains("不会把数据改存到 AppData", controller.Current.Explanation);
    }

    [Fact]
    public void MissingConsentIsExplicitAndCannotCollect()
    {
        var controller = new CollectionController(
            new DirectoryProbeResult(true, @"C:\portable", null),
            collectionConsentGranted: false);

        Assert.Equal(CollectionMode.ConsentRequired, controller.Current.Mode);
        Assert.Contains("确认前不会接收键盘或鼠标活动", controller.Current.Explanation);
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
        Assert.Contains("当前没有接收键盘或鼠标活动", controller.Current.Explanation);
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
        Assert.Equal("记录中", controller.Current.Label);
        Assert.Contains("后台记录键盘和鼠标活动", controller.Current.Explanation);
        Assert.Contains("当前程序文件夹", controller.Current.Explanation);

        await controller.PauseAsync();
        Assert.Equal(PauseReason.User, capture.CurrentStatus.PauseReasons);
        Assert.Equal(CollectionMode.Paused, controller.Current.Mode);
        Assert.Equal("已暂停", controller.Current.Label);
        Assert.Contains("停止接收新的键盘和鼠标活动", controller.Current.Explanation);
        Assert.Contains("暂停前的数据已保存", controller.Current.Explanation);

        capture.Publish(InputCaptureState.IntegrityFault, PauseReason.User, "queue overflow");
        Assert.Equal(CollectionMode.IntegrityFault, controller.Current.Mode);
        Assert.Equal("记录异常 · 已停止", controller.Current.Label);
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
        Assert.Equal("记录启动失败", controller.Current.Label);
        Assert.Contains("当前没有接收键盘或鼠标活动", controller.Current.Explanation);
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
        Assert.Contains("后台可能仍在接收活动", controller.Current.Explanation);
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
        Assert.Contains("后台可能仍在接收活动", controller.Current.Explanation);
        Assert.Contains("unregister failed", controller.Current.Explanation);
        Assert.DoesNotContain("已停止", controller.Current.Label + controller.Current.Explanation);
    }

    [Theory]
    [InlineData((int)AppOfflineRestorePhase.Restoring, CollectionMode.Restoring, "正在停止记录")]
    [InlineData((int)AppOfflineRestorePhase.SucceededAwaitExit, CollectionMode.RestoreSucceededAwaitExit, "恢复成功")]
    [InlineData((int)AppOfflineRestorePhase.FailedAwaitExit, CollectionMode.RestoreFailedAwaitExit, "恢复失败")]
    [InlineData((int)AppOfflineRestorePhase.RecoveryRequired, CollectionMode.RestoreRecoveryRequired, "下次启动")]
    public void OfflineRestoreStatesAreTerminalAndCannotPauseOrResume(
        int phaseValue,
        CollectionMode expectedMode,
        string expectedText)
    {
        var capture = new FakeCapture(InputCaptureState.Paused);
        using var controller = new CollectionController(
            new DirectoryProbeResult(true, @"C:\portable", null), capture);

        controller.HandleOfflineRestoreStatus(new AppOfflineRestoreStatus(
            (AppOfflineRestorePhase)phaseValue,
            "details",
            null,
            CaptureConfirmedClosed: true));

        Assert.Equal(expectedMode, controller.Current.Mode);
        Assert.Contains(expectedText, controller.Current.Label + controller.Current.Explanation);
        Assert.False(controller.Current.CanPause);
        Assert.False(controller.Current.CanResume);
        if ((AppOfflineRestorePhase)phaseValue == AppOfflineRestorePhase.Restoring)
        {
            Assert.Contains("后台接收还在关闭中", controller.Current.Explanation);
            Assert.DoesNotContain("采集已停止", controller.Current.Label + controller.Current.Explanation);
        }
    }

    [Fact]
    public void FailedRestoreWithOpenCaptureNeverClaimsCollectionStopped()
    {
        var capture = new FakeCapture(InputCaptureState.Paused);
        using var controller = new CollectionController(
            new DirectoryProbeResult(true, @"C:\portable", null), capture);

        controller.HandleOfflineRestoreStatus(new AppOfflineRestoreStatus(
            AppOfflineRestorePhase.FailedAwaitExit,
            "diagnostics remained open",
            null,
            CaptureConfirmedClosed: false));

        Assert.Equal(CollectionMode.RestoreControlUncertainAwaitExit, controller.Current.Mode);
        Assert.Contains("无法确认后台接收是否完全关闭", controller.Current.Explanation);
        Assert.DoesNotContain("采集已停止", controller.Current.Label + controller.Current.Explanation);
        Assert.False(controller.Current.CanPause);
        Assert.False(controller.Current.CanResume);
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
