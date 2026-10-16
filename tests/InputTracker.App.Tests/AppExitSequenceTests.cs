namespace InputTracker.App.Tests;

public sealed class AppExitSequenceTests
{
    [Fact]
    public async Task CleanupFinishesBeforeShutdownAndStartReturnsImmediately()
    {
        var sequence = new AppExitSequence();
        var allowCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new System.Collections.Concurrent.ConcurrentQueue<string>();
        int prepared = 0;

        bool accepted = sequence.TryBegin(
            acquireExit: () => true,
            prepareUi: () =>
            {
                calls.Enqueue("prepare-ui");
                Interlocked.Exchange(ref prepared, 1);
            },
            cleanupAsync: async () =>
            {
                calls.Enqueue("cleanup-start");
                await allowCleanup.Task;
                calls.Enqueue("cleanup-finish");
            },
            finish: error =>
            {
                calls.Enqueue("shutdown");
                finished.TrySetResult(error);
            });

        Assert.True(accepted);
        Assert.Equal(1, Volatile.Read(ref prepared));
        Assert.False(finished.Task.IsCompleted);

        allowCleanup.TrySetResult();
        Assert.Null(await finished.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(["prepare-ui", "cleanup-start", "cleanup-finish", "shutdown"], calls.ToArray());
    }

    [Fact]
    public async Task CleanupFailureIsReportedAndStillFinishesExit()
    {
        var sequence = new AppExitSequence();
        var finished = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);

        Assert.True(sequence.TryBegin(
            acquireExit: () => true,
            prepareUi: () => { },
            cleanupAsync: () => Task.FromException(new IOException("cleanup failed")),
            finish: error => finished.TrySetResult(error)));

        Exception? error = await finished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsType<IOException>(error);
        Assert.Equal("cleanup failed", error.Message);
    }

    [Fact]
    public void RejectedOrRepeatedExitDoesNotStartCleanupTwice()
    {
        var sequence = new AppExitSequence();
        int prepared = 0;
        int cleaned = 0;

        Assert.False(sequence.TryBegin(
            acquireExit: () => false,
            prepareUi: () => prepared++,
            cleanupAsync: () =>
            {
                cleaned++;
                return Task.CompletedTask;
            },
            finish: _ => { }));
        Assert.True(sequence.TryBegin(
            acquireExit: () => true,
            prepareUi: () => prepared++,
            cleanupAsync: () => Task.Delay(Timeout.InfiniteTimeSpan),
            finish: _ => { }));
        Assert.True(sequence.TryBegin(
            acquireExit: () => throw new InvalidOperationException("must not reacquire"),
            prepareUi: () => prepared++,
            cleanupAsync: () =>
            {
                cleaned++;
                return Task.CompletedTask;
            },
            finish: _ => { }));

        Assert.Equal(1, prepared);
        Assert.Equal(0, cleaned);
    }
}
