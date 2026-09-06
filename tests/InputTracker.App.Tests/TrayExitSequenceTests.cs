namespace InputTracker.App.Tests;

public sealed class TrayExitSequenceTests
{
    [Fact]
    public void ExitShutdownUsesNormalDispatcherPrioritySoCollectionCannotStarveIt()
    {
        Assert.Equal(
            System.Windows.Threading.DispatcherPriority.Normal,
            TrayIconController.ExitDispatchPriority);
    }

    [Fact]
    public void AcceptedExitWaitsForMenuClosedBeforeHidingTrayAndDeferredShutdown()
    {
        var sequence = new TrayExitSequence();
        var calls = new List<string>();
        Action? deferred = null;

        sequence.BeginExit();

        Assert.Empty(calls);
        Assert.False(sequence.CanShowWindow);

        sequence.ContinueAfterMenuClosed(
            hideTray: () => calls.Add("hide-tray"),
            defer: action =>
            {
                calls.Add("defer");
                deferred = action;
            },
            tryExit: () =>
            {
                calls.Add("shutdown");
                return true;
            },
            restoreTray: () => calls.Add("restore-tray"));

        Assert.Equal(["hide-tray", "defer"], calls);
        Assert.False(sequence.CanShowWindow);
        Assert.NotNull(deferred);

        deferred();

        Assert.Equal(["hide-tray", "defer", "shutdown"], calls);
        sequence.BeginExit();
        sequence.ContinueAfterMenuClosed(
            () => calls.Add("duplicate"),
            _ => { },
            () => true,
            () => { });
        Assert.DoesNotContain("duplicate", calls);
    }

    [Fact]
    public void RejectedExitRestoresTrayAndAllowsWindowCommandsAgain()
    {
        var sequence = new TrayExitSequence();
        Action? deferred = null;
        bool restored = false;

        sequence.BeginExit();
        sequence.ContinueAfterMenuClosed(
            hideTray: () => { },
            defer: action => deferred = action,
            tryExit: () => false,
            restoreTray: () => restored = true);

        Assert.False(sequence.CanShowWindow);
        deferred!();
        Assert.True(restored);
        Assert.True(sequence.CanShowWindow);
    }
}
