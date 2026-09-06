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
    public void AcceptedExitClosesMenuAndHidesTrayBeforeDeferredShutdown()
    {
        var sequence = new TrayExitSequence();
        var calls = new List<string>();
        Action? deferred = null;

        sequence.BeginExit(
            closeMenu: () => calls.Add("close-menu"),
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

        Assert.Equal(["close-menu", "hide-tray", "defer"], calls);
        Assert.False(sequence.CanShowWindow);
        Assert.NotNull(deferred);

        deferred();

        Assert.Equal(["close-menu", "hide-tray", "defer", "shutdown"], calls);
        sequence.BeginExit(() => calls.Add("duplicate"), () => { }, _ => { }, () => true, () => { });
        Assert.DoesNotContain("duplicate", calls);
    }

    [Fact]
    public void RejectedExitRestoresTrayAndAllowsWindowCommandsAgain()
    {
        var sequence = new TrayExitSequence();
        Action? deferred = null;
        bool restored = false;

        sequence.BeginExit(
            closeMenu: () => { },
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
