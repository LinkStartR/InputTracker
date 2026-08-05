namespace InputTracker.App.Tests;

public sealed class SingleInstanceGuardTests
{
    [Fact]
    public void SecondGuardForSameScopeIsNotPrimary()
    {
        string scope = $"InputTracker-test-{Guid.NewGuid():N}";
        using SingleInstanceGuard first = SingleInstanceGuard.TryAcquire(scope);
        using SingleInstanceGuard second = SingleInstanceGuard.TryAcquire(scope);

        Assert.True(first.IsPrimary);
        Assert.False(second.IsPrimary);
    }

    [Fact]
    public void DifferentScopesCanEachBePrimary()
    {
        using SingleInstanceGuard first = SingleInstanceGuard.TryAcquire($"InputTracker-test-{Guid.NewGuid():N}");
        using SingleInstanceGuard second = SingleInstanceGuard.TryAcquire($"InputTracker-test-{Guid.NewGuid():N}");

        Assert.True(first.IsPrimary);
        Assert.True(second.IsPrimary);
    }
}
