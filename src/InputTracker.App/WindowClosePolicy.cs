namespace InputTracker;

internal sealed class WindowClosePolicy
{
    private bool _explicitExitRequested;

    public void RequestExplicitExit() => _explicitExitRequested = true;

    public void RequestSessionEnding() => _explicitExitRequested = true;

    public bool ShouldHideInsteadOfClose() => !_explicitExitRequested;
}
