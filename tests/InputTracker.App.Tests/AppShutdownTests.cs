namespace InputTracker.App.Tests;

public sealed class AppShutdownTests
{
    [Fact]
    public void LaterResourcesAreDisposedWhenCaptureStopFails()
    {
        var first = new Probe();
        var second = new Probe();

        Assert.Throws<AggregateException>(() => AppShutdown.Run(
            () => Task.FromException(new IOException("capture stop failed")),
            storage: null,
            first,
            second));

        Assert.True(first.Disposed);
        Assert.True(second.Disposed);
    }

    private sealed class Probe : IDisposable
    {
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
