using System.Windows;
using InputTracker.Input;
using InputTracker.Input.Win32;
using InputTracker.Storage;

namespace InputTracker;

public partial class App : System.Windows.Application
{
    private SingleInstanceGuard? _singleInstance;
    private TrayIconController? _trayIcon;
    private InputCaptureService? _capture;
    private CollectionController? _collection;
    private StorageService? _storage;
    private AppStorageCoordinator? _storageCoordinator;

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

        string? storageStartupError = null;
        DashboardViewModel? dashboard = null;
        if (directory.IsWritable)
        {
            try
            {
                _storage = StorageService.Open(StorageLayout.Create(AppContext.BaseDirectory));
                Guid calibrationSession = Guid.NewGuid();
                _storage.Start(TimeCalibrationSegment.Capture(calibrationSession, SystemStorageClock.Instance));
                _storageCoordinator = new AppStorageCoordinator(
                    new StorageServiceWriter(_storage), calibrationSession);
                _storageCoordinator.EnsureReady();
                _capture = new InputCaptureService(
                    _storageCoordinator,
                    options: null,
                    RawInputHostFactory.Instance,
                    new RawDeviceInfoProvider(),
                    _storageCoordinator);
                dashboard = new DashboardViewModel(new StorageDashboardDataSource(_storage));
            }
            catch (Exception error)
            {
                storageStartupError = error.Message;
                _storage?.Dispose();
                _storage = null;
            }
        }

        IInputCaptureControl? collectionControl = _capture is not null && _storageCoordinator is not null
            ? new PersistingCaptureControl(_capture, _storageCoordinator)
            : null;
        _collection = new CollectionController(directory, collectionControl, storageStartupError);
        var window = new MainWindow(_collection, directory, dashboard);

        _trayIcon = new TrayIconController(
            _collection,
            showWindow: () => ShowWindow(window),
            exitApplication: () =>
            {
                window.PrepareForExplicitExit();
                Shutdown(0);
            });

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
        try
        {
            AppShutdown.Run(
                _capture is null ? null : () => _capture.DisposeAsync().AsTask(),
                _storage,
                _collection,
                _trayIcon,
                _singleInstance);
        }
        finally
        {
            base.OnExit(e);
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        if (e.Cancel)
        {
            return;
        }

        if (MainWindow is MainWindow window)
        {
            window.PrepareForSessionEnding();
        }

        Shutdown(0);
    }

    private static void ShowWindow(Window window)
    {
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }
}
