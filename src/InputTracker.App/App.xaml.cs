using System.Windows;

namespace InputTracker;

public partial class App : System.Windows.Application
{
    private SingleInstanceGuard? _singleInstance;
    private TrayIconController? _trayIcon;

    protected override void OnStartup(StartupEventArgs e)
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

        var collection = new CollectionController(directory);
        var window = new MainWindow(collection, directory);

        _trayIcon = new TrayIconController(
            collection,
            showWindow: () => ShowWindow(window),
            exitApplication: () => Shutdown(0));

        window.MinimizeToTrayRequested += (_, _) => window.Hide();
        window.ExitRequested += (_, _) => Shutdown(0);
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
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
