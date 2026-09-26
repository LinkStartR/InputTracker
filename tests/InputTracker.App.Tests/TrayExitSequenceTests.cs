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

        sequence.MenuOpened();
        sequence.BeginExit(
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

        Assert.Empty(calls);
        Assert.False(sequence.CanShowWindow);

        sequence.MenuClosed();

        Assert.Equal(["hide-tray", "defer"], calls);
        Assert.False(sequence.CanShowWindow);
        Assert.NotNull(deferred);

        deferred();

        Assert.Equal(["hide-tray", "defer", "shutdown"], calls);
        sequence.MenuClosed();
        Assert.Equal(["hide-tray", "defer", "shutdown"], calls);
    }

    [Fact]
    public void ExitContinuesWhenMenuClosedEventArrivesBeforeExitClick()
    {
        var sequence = new TrayExitSequence();
        var calls = new List<string>();
        Action? deferred = null;

        sequence.MenuOpened();
        sequence.MenuClosed();
        Assert.Empty(calls);

        sequence.BeginExit(
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
        deferred!();
        Assert.Equal(["hide-tray", "defer", "shutdown"], calls);
    }

    [Fact]
    public void RejectedExitRestoresTrayAndAllowsWindowCommandsAgain()
    {
        var sequence = new TrayExitSequence();
        Action? deferred = null;
        bool restored = false;

        sequence.MenuOpened();
        sequence.BeginExit(
            hideTray: () => { },
            defer: action => deferred = action,
            tryExit: () => false,
            restoreTray: () => restored = true);

        Assert.False(sequence.CanShowWindow);
        sequence.MenuClosed();
        deferred!();
        Assert.True(restored);
        Assert.True(sequence.CanShowWindow);

        sequence.MenuOpened();
        Assert.True(sequence.CanShowWindow);
    }
}
