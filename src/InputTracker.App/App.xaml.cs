using System.Windows;
using InputTracker.Input;

namespace InputTracker;

public partial class App : System.Windows.Application
{
    private SingleInstanceGuard? _singleInstance;
    private TrayIconController? _trayIcon;
    private InputCaptureService? _capture;
    private CollectionController? _collection;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        bool selfCheck = e.Args.Contains("--self-check", StringComparer.OrdinalIgnoreCase);
        bool holdSmoke = e.Args.Contains("--hold-smoke", StringComparer.OrdinalIgnoreCase);
        bool nonInteractive = selfCheck || holdSmoke;

        _singleInstance = SingleInstanceGuard.TryAcquireForExecutable();
        if (!_singleInstance.IsPrimary)
        {
            if (!nonInteractive)
            {
                System.Windows.MessageBox.Show(
                    "InputTracker 已在运行。请从任务栏托盘打开现有实例。",
                    "InputTracker",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            Shutdown(2);
            return;
        }

        DirectoryProbeResult directory = ProgramDirectoryProbe.Check(AppContext.BaseDirectory);
        if (selfCheck)
        {
            Shutdown(directory.IsWritable ? 0 : 3);
            return;
        }

        if (holdSmoke)
        {
            Thread.Sleep(TimeSpan.FromSeconds(5));
            Shutdown(0);
            return;
        }

        _capture = directory.IsWritable ? new InputCaptureService(new InputCoreSink()) : null;
        _collection = new CollectionController(directory, _capture);
        var window = new MainWindow(_collection, directory);

        _trayIcon = new TrayIconController(
            _collection,
            showWindow: () => ShowWindow(window),
            exitApplication: () => Shutdown(0));

        window.MinimizeToTrayRequested += (_, _) => window.Hide();
        window.ExitRequested += (_, _) => Shutdown(0);
        MainWindow = window;
        window.Show();

        if (_capture is not null)
        {
            try
            {
                await _capture.StartAsync();
            }
            catch (Exception error)
            {
                _collection.HandleCaptureControlException("启动采集", error);
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _capture?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _collection?.Dispose();
        _trayIcon?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static void ShowWindow(Window window)
    {
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }
}
