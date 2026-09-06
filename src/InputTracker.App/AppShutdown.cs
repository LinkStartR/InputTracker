using InputTracker.Storage;
using System.Diagnostics;

namespace InputTracker;

internal static class AppShutdown
{
    public static void Run(
        Func<Task>? stopCapture,
        StorageService? storage,
        params IDisposable?[] remaining)
    {
        Exception? first = null;
        try
        {
            stopCapture?.Invoke().GetAwaiter().GetResult();
        }
        catch (Exception error)
        {
            first = error;
        }

        try
        {
            if (storage?.Health.State is StorageServiceState.Running or StorageServiceState.Paused)
            {
                storage.Stop(Stopwatch.GetTimestamp());
            }
        }
        catch (Exception error)
        {
            first ??= error;
        }
        finally
        {
            try { storage?.Dispose(); }
            catch (Exception error) { first ??= error; }
        }

        foreach (IDisposable? disposable in remaining)
        {
            try { disposable?.Dispose(); }
            catch (Exception error) { first ??= error; }
        }

        if (first is not null)
        {
            throw new AggregateException("InputTracker shutdown completed with one or more cleanup failures.", first);
        }
    }
}

internal sealed class AppExitSequence
{
    private int _started;

    public bool TryBegin(
        Func<bool> acquireExit,
        Action prepareUi,
        Func<Task> cleanupAsync,
        Action<Exception?> finish)
    {
        ArgumentNullException.ThrowIfNull(acquireExit);
        ArgumentNullException.ThrowIfNull(prepareUi);
        ArgumentNullException.ThrowIfNull(cleanupAsync);
        ArgumentNullException.ThrowIfNull(finish);

        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            return true;
        }

        if (!acquireExit())
        {
            Volatile.Write(ref _started, 0);
            return false;
        }

        prepareUi();
        _ = CompleteAsync(cleanupAsync, finish);
        return true;
    }

    private static async Task CompleteAsync(Func<Task> cleanupAsync, Action<Exception?> finish)
    {
        await Task.Yield();
        Exception? error = null;
        try
        {
            await cleanupAsync();
        }
        catch (Exception cleanupError)
        {
            error = cleanupError;
        }

        finish(error);
    }
}
