namespace InputTracker.App.Tests;

public sealed class WindowClosePolicyTests
{
    [Fact]
    public void OrdinaryCloseRequiresHideUntilExplicitExitIsRequested()
    {
        var policy = new WindowClosePolicy();

        Assert.True(policy.ShouldHideInsteadOfClose());

        policy.RequestExplicitExit();

        Assert.False(policy.ShouldHideInsteadOfClose());
    }

    [Fact]
    public void SessionEndingAllowsCloseWithoutHideSideEffect()
    {
        var policy = new WindowClosePolicy();

        policy.RequestSessionEnding();

        Assert.False(policy.ShouldHideInsteadOfClose());
    }
}
