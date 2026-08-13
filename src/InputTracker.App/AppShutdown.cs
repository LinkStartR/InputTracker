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
