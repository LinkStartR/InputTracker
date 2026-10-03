using System.Diagnostics;
using InputTracker.Input;
using InputTracker.Storage;

namespace InputTracker.App.Tests;

public sealed class GamepadAutomaticCalibrationTests
{
    private static readonly RuntimeDeviceId Device = new(71);
    private static readonly GamepadControlId First = new(0x09, 1, 0, 0);
    private static readonly GamepadControlId Second = new(0x09, 2, 0, 0);
    private static readonly GamepadControlId Hat = new(0x01, 0x39, 0, 0);
    private static readonly GamepadControlId Trigger = new(0x01, 0x32, 0, 0);

    [Fact]
    public void OnePressAndReleaseCompletesAfterReportSettles()
    {
        var session = Session(StorageGamepadButton.South, Event(First, 0, 0, baseline: true));
        session.Observe(Event(First, 1, 100));
        Assert.Null(session.TryComplete(Ticks(200)));
        session.Observe(Event(First, 0, 250));
        Assert.Null(session.TryComplete(Ticks(260)));
        StorageGamepadHidBinding binding = Assert.IsType<StorageGamepadHidBinding>(session.TryComplete(Ticks(280)));
        Assert.Equal(StorageGamepadButton.South, binding.Button);
        Assert.Equal(new[] { 1 }, binding.ActiveValues);
        Assert.Null(session.TryComplete(Ticks(300)));
    }

    [Fact]
    public void DuplicateSamplesDoNotCompleteHeldButtonOrAdvanceNextButton()
    {
        var session = Session(StorageGamepadButton.South, Event(First, 0, 0, baseline: true));
        session.Observe(Event(First, 1, 100));
        session.Observe(Event(First, 1, 150));
        Assert.Null(session.TryComplete(Ticks(200)));
        session.Observe(Event(First, 0, 250));
        session.Observe(Event(First, 0, 260));
        Assert.NotNull(session.TryComplete(Ticks(280)));
        session.BeginTarget(StorageGamepadButton.East);
        session.Observe(Event(First, 1, 100));
        session.Observe(Event(First, 0, 250));
        Assert.Null(session.TryComplete(Ticks(400)));
    }

    [Fact]
    public void ReportsQueuedBeforeNextPromptCannotCalibrateTheNextButton()
    {
        var session = Session(StorageGamepadButton.South,
            Event(First, 0, 0, baseline: true), Event(Second, 0, 0, baseline: true));
        session.Observe(Event(First, 1, 100));
        session.Observe(Event(First, 0, 250));
        Assert.NotNull(session.TryComplete(Ticks(400)));
        session.BeginTarget(StorageGamepadButton.East, Ticks(400));

        // These reports were captured before the East prompt and arrived after its UI update.
        session.Observe(Event(Second, 1, 251));
        session.Observe(Event(Second, 0, 351));
        Assert.Null(session.TryComplete(Ticks(500)));
        session.Observe(Event(Second, 1, 600));
        session.Observe(Event(Second, 0, 750));
        StorageGamepadHidBinding binding = Assert.IsType<StorageGamepadHidBinding>(session.TryComplete(Ticks(800)));
        Assert.Equal(StorageGamepadButton.East, binding.Button);
        Assert.Equal(Second.Usage, binding.Usage);
    }

    [Fact]
    public void LateHeldControlStillBlocksNewPressUntilEveryControlIsReleased()
    {
        var session = Session(StorageGamepadButton.South,
            Event(First, 0, 0, baseline: true), Event(Second, 0, 0, baseline: true));
        session.Observe(Event(First, 1, 100));
        session.Observe(Event(First, 0, 250));
        Assert.NotNull(session.TryComplete(Ticks(400)));
        session.BeginTarget(StorageGamepadButton.East, Ticks(400));
        session.Observe(Event(Second, 1, 251));
        session.Observe(Event(First, 1, 450));
        session.Observe(Event(Second, 0, 550));
        session.Observe(Event(First, 0, 650));
        Assert.Null(session.TryComplete(Ticks(700)));

        session.Observe(Event(Second, 1, 800));
        session.Observe(Event(Second, 0, 950));
        Assert.NotNull(session.TryComplete(Ticks(1000)));
    }

    [Fact]
    public void RetryingHeldConfirmedTriggerNeedsReleaseAndNewPress()
    {
        var session = Session(StorageGamepadButton.LeftTrigger, Event(Trigger, 0, 0, true, 0, 255));
        Assert.True(session.ConfirmRestingPoint());
        session.Observe(Event(Trigger, 255, 100, min: 0, max: 255));
        session.BeginTarget(StorageGamepadButton.LeftTrigger, Ticks(200));
        session.Observe(Event(Trigger, 230, 250, min: 0, max: 255));
        session.Observe(Event(Trigger, 0, 350, min: 0, max: 255));
        Assert.Null(session.TryComplete(Ticks(400)));
        session.Observe(Event(Trigger, 255, 500, min: 0, max: 255));
        session.Observe(Event(Trigger, 0, 650, min: 0, max: 255));
        Assert.NotNull(session.TryComplete(Ticks(700)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CombinationIsRejectedEvenWhenSecondControlChangesAfterCandidateRelease(bool afterRelease)
    {
        var session = Session(StorageGamepadButton.South, Event(First, 0, 0, baseline: true), Event(Second, 0, 0, baseline: true));
        session.Observe(Event(First, 1, 100));
        if (!afterRelease) session.Observe(Event(Second, 1, 100));
        session.Observe(Event(First, 0, 250));
        if (afterRelease) session.Observe(Event(Second, 1, 250));
        session.Observe(Event(Second, 0, 300));
        Assert.Null(session.TryComplete(Ticks(400)));
        session.Observe(Event(First, 1, 500));
        session.Observe(Event(First, 0, 650));
        Assert.NotNull(session.TryComplete(Ticks(700)));
    }

    [Fact]
    public void BounceAndVeryShortPressAreRejected()
    {
        var session = Session(StorageGamepadButton.South, Event(First, 0, 0, baseline: true));
        session.Observe(Event(First, 1, 100));
        session.Observe(Event(First, 0, 105));
        Assert.Null(session.TryComplete(Ticks(200)));
        session.Observe(Event(First, 1, 300));
        session.Observe(Event(First, 0, 450));
        session.Observe(Event(First, 1, 455));
        session.Observe(Event(First, 0, 460));
        Assert.Null(session.TryComplete(Ticks(500)));
    }

    [Fact]
    public void HeldBaselineAndUnknownFirstPressRequireReleaseBeforeCalibration()
    {
        var session = Session(StorageGamepadButton.South, Event(First, 1, 0, baseline: true));
        session.Observe(Event(First, 0, 100));
        Assert.Null(session.TryComplete(Ticks(200)));
        session.Observe(Event(Second, 1, 250));
        session.Observe(Event(Second, 0, 350));
        Assert.Null(session.TryComplete(Ticks(400)));
        session.Observe(Event(Second, 1, 500));
        session.Observe(Event(Second, 0, 650));
        Assert.NotNull(session.TryComplete(Ticks(700)));
    }

    [Theory]
    [InlineData(0, -1)]
    [InlineData(1, 0)]
    public void HatUpSupportsZeroOrOneBasedRangeAndNullNeutral(int minimum, int neutral)
    {
        var session = Session(StorageGamepadButton.DpadUp, Event(Hat, neutral, 0, true, minimum, minimum + 7));
        session.Observe(Event(Hat, minimum, 100, min: minimum, max: minimum + 7));
        session.Observe(Event(Hat, neutral, 250, min: minimum, max: minimum + 7));
        StorageGamepadHidBinding binding = Assert.IsType<StorageGamepadHidBinding>(session.TryComplete(Ticks(300)));
        Assert.Equal(new[] { minimum, minimum + 1, minimum + 7 }, binding.ActiveValues);
    }

    [Fact]
    public void DiagonalHatAndChangingDirectionAreRejected()
    {
        var session = Session(StorageGamepadButton.DpadUp, Event(Hat, -1, 0, true, 0, 7));
        session.Observe(Event(Hat, 1, 100, min: 0, max: 7));
        session.Observe(Event(Hat, -1, 250, min: 0, max: 7));
        Assert.Null(session.TryComplete(Ticks(300)));
        session.Observe(Event(Hat, 0, 400, min: 0, max: 7));
        session.Observe(Event(Hat, 2, 450, min: 0, max: 7));
        session.Observe(Event(Hat, -1, 550, min: 0, max: 7));
        Assert.Null(session.TryComplete(Ticks(600)));
    }

    [Theory]
    [InlineData(0, 255, StorageGamepadAxisDirection.Increasing, 140, 76)]
    [InlineData(255, 0, StorageGamepadAxisDirection.Decreasing, 115, 179)]
    public void TriggerNeedsRestingConfirmationAndFullRelease(int resting, int pressed,
        StorageGamepadAxisDirection direction, int activation, int release)
    {
        var session = Session(StorageGamepadButton.LeftTrigger, Event(Trigger, resting, 0, true, 0, 255));
        session.Observe(Event(Trigger, pressed, 100, min: 0, max: 255));
        session.Observe(Event(Trigger, resting, 200, min: 0, max: 255));
        Assert.Null(session.TryComplete(Ticks(250)));
        Assert.True(session.AwaitingRestingConfirmation);
        Assert.True(session.ConfirmRestingPoint());
        session.Observe(Event(Trigger, pressed, 300, min: 0, max: 255));
        session.Observe(Event(Trigger, (resting + pressed) / 2, 400, min: 0, max: 255));
        Assert.Null(session.TryComplete(Ticks(450)));
        session.Observe(Event(Trigger, resting, 500, min: 0, max: 255));
        StorageGamepadHidBinding binding = Assert.IsType<StorageGamepadHidBinding>(session.TryComplete(Ticks(550)));
        Assert.Equal(direction, binding.AxisDirection);
        Assert.Equal(activation, binding.ActivationThreshold);
        Assert.Equal(release, binding.ReleaseThreshold);
        session.BeginTarget(StorageGamepadButton.RightTrigger);
        Assert.False(session.AwaitingRestingConfirmation);
    }

    [Fact]
    public void CenteredSharedAxisAndStickAxesCannotBecomeAutomaticTriggers()
    {
        var stick = new GamepadControlId(0x01, 0x30, 0, 0);
        var session = Session(StorageGamepadButton.LeftTrigger,
            Event(stick, 0, 0, true, 0, 255), Event(Trigger, 128, 0, true, 0, 255));
        Assert.False(session.ConfirmRestingPoint());
        session.Observe(Event(stick, 255, 100, min: 0, max: 255));
        session.Observe(Event(stick, 0, 250, min: 0, max: 255));
        session.Observe(Event(Trigger, 255, 300, min: 0, max: 255));
        session.Observe(Event(Trigger, 128, 450, min: 0, max: 255));
        Assert.Null(session.TryComplete(Ticks(500)));
    }

    [Fact]
    public void StickMovementAlongsideButtonIsRejectedAndSmallNoiseIsIgnored()
    {
        var stick = new GamepadControlId(0x01, 0x30, 0, 0);
        var session = Session(StorageGamepadButton.South,
            Event(First, 0, 0, true), Event(stick, 128, 0, true, 0, 255));
        session.Observe(Event(stick, 255, 100, min: 0, max: 255));
        session.Observe(Event(First, 1, 150));
        session.Observe(Event(First, 0, 300));
        session.Observe(Event(stick, 128, 350, min: 0, max: 255));
        Assert.Null(session.TryComplete(Ticks(400)));
        session.Observe(Event(First, 1, 500));
        session.Observe(Event(stick, 130, 550, min: 0, max: 255));
        session.Observe(Event(First, 0, 650));
        Assert.NotNull(session.TryComplete(Ticks(700)));
    }

    private static GamepadAutomaticCalibration Session(StorageGamepadButton target, params GamepadControlEvent[] baseline)
    {
        var session = new GamepadAutomaticCalibration(baseline);
        session.BeginTarget(target);
        return session;
    }

    private static long Ticks(int milliseconds) => Stopwatch.Frequency * milliseconds / 1000;
    private static GamepadControlEvent Event(GamepadControlId control, int value, int milliseconds,
        bool baseline = false, int? min = null, int? max = null) => new(Device, control, value, Ticks(milliseconds), baseline, min, max);
}
