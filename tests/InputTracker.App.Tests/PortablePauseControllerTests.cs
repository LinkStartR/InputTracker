namespace InputTracker.App.Tests;

using InputTracker.Input;

public sealed class PortablePauseControllerTests
{
    [Theory]
    [InlineData(PortableSystemMessageRouter.WmWtsSessionChange, PortableSystemMessageRouter.WtsSessionLock, (int)PortableSystemEvent.SessionLocked)]
    [InlineData(PortableSystemMessageRouter.WmWtsSessionChange, PortableSystemMessageRouter.WtsSessionUnlock, (int)PortableSystemEvent.SessionUnlocked)]
    [InlineData(PortableSystemMessageRouter.WmPowerBroadcast, PortableSystemMessageRouter.PbtApmSuspend, (int)PortableSystemEvent.SystemSuspending)]
    [InlineData(PortableSystemMessageRouter.WmPowerBroadcast, PortableSystemMessageRouter.PbtApmResumeAutomatic, (int)PortableSystemEvent.SystemResumed)]
    public void NativeMessagesMapToExactPortableEvent(int message, int parameter, int expected) =>
        Assert.Equal((PortableSystemEvent)expected, PortableSystemMessageRouter.Translate(message, parameter));

    [Fact]
    public void UnrelatedNativeMessagesAreIgnored() =>
        Assert.Null(PortableSystemMessageRouter.Translate(0x1234, 0));

    [Theory]
    [InlineData((int)PortableSystemEvent.SessionLocked, true)]
    [InlineData((int)PortableSystemEvent.SystemSuspending, true)]
    [InlineData((int)PortableSystemEvent.SessionUnlocked, false)]
    [InlineData((int)PortableSystemEvent.SystemResumed, false)]
    public void NativePauseMessagesRequireSynchronousBarrier(int value, bool expected) =>
        Assert.Equal(expected, PortableSystemMessageRouter.RequiresSynchronousCompletion((PortableSystemEvent)value));

    [Fact]
    public async Task UnlockDoesNotClearUserPause()
    {
        var capture = new FakeCapture();
        var controller = new PortablePauseController(capture);

        await capture.SetPauseReasonAsync(PauseReason.User, true);
        await controller.HandleAsync(PortableSystemEvent.SessionLocked);
        Assert.Equal(PauseReason.User | PauseReason.SessionLocked, capture.CurrentStatus.PauseReasons);

        await controller.HandleAsync(PortableSystemEvent.SessionUnlocked);
        Assert.Equal(PauseReason.User, capture.CurrentStatus.PauseReasons);
        Assert.Equal(InputCaptureState.Paused, capture.CurrentStatus.State);
    }

    [Fact]
    public async Task SuspendAndLockReasonsAreIndependentlyRemoved()
    {
        var capture = new FakeCapture();
        var controller = new PortablePauseController(capture);

        await controller.HandleAsync(PortableSystemEvent.SessionLocked);
        await controller.HandleAsync(PortableSystemEvent.SystemSuspending);
        await controller.HandleAsync(PortableSystemEvent.SessionUnlocked);
        Assert.Equal(PauseReason.SystemSuspend, capture.CurrentStatus.PauseReasons);

        await controller.HandleAsync(PortableSystemEvent.SystemResumed);
        Assert.Equal(PauseReason.None, capture.CurrentStatus.PauseReasons);
        Assert.Equal(InputCaptureState.Running, capture.CurrentStatus.State);
    }

    [Fact]
    public async Task DispatcherKeepsUnlockBehindBlockedLock()
    {
        var capture = new BlockingCapture();
        using var dispatcher = new PortableSystemEventDispatcher(
            new PortablePauseController(capture),
            (_, error) => throw error);

        Task locked = dispatcher.Enqueue(PortableSystemEvent.SessionLocked);
        await capture.LockEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task unlocked = dispatcher.Enqueue(PortableSystemEvent.SessionUnlocked);
        Assert.Equal([PauseReason.SessionLocked], capture.Calls);
        Assert.False(unlocked.IsCompleted);

        capture.ReleaseLock.SetResult();
        await Task.WhenAll(locked, unlocked);
        Assert.Equal([PauseReason.SessionLocked, PauseReason.SessionLocked], capture.Calls);
        Assert.Equal(PauseReason.None, capture.CurrentStatus.PauseReasons);
    }

    [Fact]
    public async Task DispatcherDisposeWaitsForInFlightBoundary()
    {
        var capture = new BlockingCapture();
        var dispatcher = new PortableSystemEventDispatcher(
            new PortablePauseController(capture),
            (_, error) => throw error);
        _ = dispatcher.Enqueue(PortableSystemEvent.SessionLocked);
        await capture.LockEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task disposing = Task.Run(dispatcher.Dispose);
        await Task.Delay(50);
        Assert.False(disposing.IsCompleted);

        capture.ReleaseLock.SetResult();
        await disposing.WaitAsync(TimeSpan.FromSeconds(5));
        Action enqueueAfterDispose = () => dispatcher.Enqueue(PortableSystemEvent.SessionUnlocked);
        ObjectDisposedException error = Assert.Throws<ObjectDisposedException>(enqueueAfterDispose);
        Assert.Contains(nameof(PortableSystemEventDispatcher), error.ObjectName);
    }

    [Fact]
    public async Task SynchronousDispatchDoesNotReturnBeforePauseBarrier()
    {
        var capture = new BlockingCapture();
        using var dispatcher = new PortableSystemEventDispatcher(
            new PortablePauseController(capture),
            (_, error) => throw error);

        Task nativeMessage = Task.Run(() =>
            dispatcher.EnqueueAndWait(PortableSystemEvent.SessionLocked));
        await capture.LockEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(nativeMessage.IsCompleted);

        capture.ReleaseLock.SetResult();
        await nativeMessage.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PauseReason.SessionLocked, capture.CurrentStatus.PauseReasons);
    }

    private sealed class FakeCapture : IInputCaptureControl
    {
        public InputCaptureStatus CurrentStatus { get; private set; } =
            new(InputCaptureState.Running, PauseReason.None, null);
        public DiagnosticsSnapshot Diagnostics => throw new NotSupportedException();
        public event EventHandler<InputCaptureStatus>? StatusChanged;

        public ValueTask SetPauseReasonAsync(PauseReason reason, bool active, CancellationToken cancellationToken = default)
        {
            PauseReason updated = active
                ? CurrentStatus.PauseReasons | reason
                : CurrentStatus.PauseReasons & ~reason;
            CurrentStatus = new InputCaptureStatus(
                updated == PauseReason.None ? InputCaptureState.Running : InputCaptureState.Paused,
                updated,
                null);
            StatusChanged?.Invoke(this, CurrentStatus);
            return ValueTask.CompletedTask;
        }

        public ValueTask SetGamepadCaptureEnabledAsync(bool enabled, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }

    private sealed class BlockingCapture : IInputCaptureControl
    {
        public TaskCompletionSource LockEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseLock { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<PauseReason> Calls { get; } = [];
        public InputCaptureStatus CurrentStatus { get; private set; } = new(InputCaptureState.Running, PauseReason.None, null);
        public DiagnosticsSnapshot Diagnostics => throw new NotSupportedException();
        public event EventHandler<InputCaptureStatus>? StatusChanged;

        public async ValueTask SetPauseReasonAsync(PauseReason reason, bool active, CancellationToken cancellationToken = default)
        {
            Calls.Add(reason);
            if (reason == PauseReason.SessionLocked && active)
            {
                LockEntered.SetResult();
                await ReleaseLock.Task.WaitAsync(cancellationToken);
            }

            PauseReason updated = active
                ? CurrentStatus.PauseReasons | reason
                : CurrentStatus.PauseReasons & ~reason;
            CurrentStatus = new InputCaptureStatus(
                updated == PauseReason.None ? InputCaptureState.Running : InputCaptureState.Paused,
                updated,
                null);
            StatusChanged?.Invoke(this, CurrentStatus);
        }

        public ValueTask SetGamepadCaptureEnabledAsync(bool enabled, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
