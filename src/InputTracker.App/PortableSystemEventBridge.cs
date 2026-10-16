using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace InputTracker;

internal static class PortableSystemMessageRouter
{
    internal const int WmWtsSessionChange = 0x02B1;
    internal const int WmPowerBroadcast = 0x0218;
    internal const int WtsSessionLock = 0x7;
    internal const int WtsSessionUnlock = 0x8;
    internal const int PbtApmSuspend = 0x0004;
    internal const int PbtApmResumeAutomatic = 0x0012;

    public static PortableSystemEvent? Translate(int message, nint wParam) => message switch
    {
        WmWtsSessionChange when wParam == WtsSessionLock => PortableSystemEvent.SessionLocked,
        WmWtsSessionChange when wParam == WtsSessionUnlock => PortableSystemEvent.SessionUnlocked,
        WmPowerBroadcast when wParam == PbtApmSuspend => PortableSystemEvent.SystemSuspending,
        WmPowerBroadcast when wParam == PbtApmResumeAutomatic => PortableSystemEvent.SystemResumed,
        _ => null
    };

    public static bool RequiresSynchronousCompletion(PortableSystemEvent value) =>
        value is PortableSystemEvent.SessionLocked or PortableSystemEvent.SystemSuspending;
}

internal sealed class PortableSystemEventBridge : IDisposable
{
    private const uint NotifyForThisSession = 0;
    private readonly PortableSystemEventDispatcher _dispatcher;
    private readonly HwndSource _source;
    private readonly nint _windowHandle;
    private bool _registered;
    private bool _disposed;

    public PortableSystemEventBridge(
        Window window,
        PortablePauseController pauseController,
        Action<string, Exception> reportFailure)
    {
        ArgumentNullException.ThrowIfNull(window);
        _dispatcher = new PortableSystemEventDispatcher(pauseController, reportFailure);
        _windowHandle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_windowHandle)
            ?? throw new InvalidOperationException("InputTracker could not attach to its window message source.");
        _source.AddHook(WindowProcedure);
        if (!WTSRegisterSessionNotification(_windowHandle, NotifyForThisSession))
        {
            _source.RemoveHook(WindowProcedure);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Session lock notification registration failed.");
        }

        _registered = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _source.RemoveHook(WindowProcedure);
        Exception? first = null;
        if (_registered && !WTSUnRegisterSessionNotification(_windowHandle))
        {
            first = new Win32Exception(Marshal.GetLastWin32Error(), "Session lock notification unregistration failed.");
        }

        _registered = false;
        try
        {
            _dispatcher.Dispose();
        }
        catch (Exception error)
        {
            first ??= error;
        }

        if (first is not null) throw first;
    }

    private nint WindowProcedure(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        PortableSystemEvent? systemEvent = PortableSystemMessageRouter.Translate(message, wParam);
        if (systemEvent is not null)
        {
            if (PortableSystemMessageRouter.RequiresSynchronousCompletion(systemEvent.Value))
            {
                _dispatcher.EnqueueAndWait(systemEvent.Value);
            }
            else
            {
                _dispatcher.Enqueue(systemEvent.Value);
            }
        }

        return 0;
    }

    [DllImport("Wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSRegisterSessionNotification(nint windowHandle, uint flags);

    [DllImport("Wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSUnRegisterSessionNotification(nint windowHandle);
}

internal sealed class PortableSystemEventDispatcher : IDisposable
{
    private readonly object _gate = new();
    private readonly PortablePauseController _pauseController;
    private readonly Action<string, Exception> _reportFailure;
    private Task _tail = Task.CompletedTask;
    private bool _accepting = true;

    public PortableSystemEventDispatcher(
        PortablePauseController pauseController,
        Action<string, Exception> reportFailure)
    {
        _pauseController = pauseController ?? throw new ArgumentNullException(nameof(pauseController));
        _reportFailure = reportFailure ?? throw new ArgumentNullException(nameof(reportFailure));
    }

    public Task Enqueue(PortableSystemEvent systemEvent)
    {
        lock (_gate)
        {
            if (!_accepting) throw new ObjectDisposedException(nameof(PortableSystemEventDispatcher));
            _tail = _tail.ContinueWith(
                _ => ApplyAsync(systemEvent),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
            return _tail;
        }
    }

    public void EnqueueAndWait(PortableSystemEvent systemEvent) =>
        Enqueue(systemEvent).GetAwaiter().GetResult();

    public void Dispose()
    {
        Task tail;
        lock (_gate)
        {
            if (!_accepting) return;
            _accepting = false;
            tail = _tail;
        }

        tail.GetAwaiter().GetResult();
    }

    private async Task ApplyAsync(PortableSystemEvent systemEvent)
    {
        try
        {
            await _pauseController.HandleAsync(systemEvent);
        }
        catch (Exception error)
        {
            _reportFailure($"处理系统采集边界 {systemEvent}", error);
        }
    }
}
