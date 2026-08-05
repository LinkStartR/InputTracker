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
    public async Task CaptureStatesAreShownHonestlyAndUserPauseUsesReasonedControl()
    {
        var capture = new FakeCapture(InputCaptureState.Running);
        using var controller = new CollectionController(
            new DirectoryProbeResult(true, @"C:\portable", null),
            capture);

        Assert.Equal(CollectionMode.Collecting, controller.Current.Mode);
        Assert.Contains("尚未持久化", controller.Current.Explanation);

        await controller.PauseAsync();
        Assert.Equal(PauseReason.User, capture.CurrentStatus.PauseReasons);
        Assert.Equal(CollectionMode.Paused, controller.Current.Mode);

        capture.Publish(InputCaptureState.IntegrityFault, PauseReason.User, "queue overflow");
        Assert.Equal(CollectionMode.IntegrityFault, controller.Current.Mode);
        Assert.Contains("queue overflow", controller.Current.Explanation);
        Assert.False(controller.Current.CanResume);
    }

    private sealed class FakeCapture(InputCaptureState initialState) : IInputCaptureControl
    {
        public InputCaptureStatus CurrentStatus { get; private set; } = new(initialState, PauseReason.None, null);
        public DiagnosticsSnapshot Diagnostics => new(
            0, 0, 0, 0, 0, 0, 0, 1,
            CurrentStatus.State,
            CurrentStatus.PauseReasons,
            CurrentStatus.State == InputCaptureState.Running,
            CurrentStatus.State == InputCaptureState.Running,
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
