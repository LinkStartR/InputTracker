using System.Windows;
using System.IO;
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
    private PortableSystemEventBridge? _systemEvents;
    private AppStorageDataToolsController? _dataTools;
    private bool _disposeStorageOnExit = true;

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

        bool collectionConsentGranted = false;
        string? consentError = null;
        if (directory.IsWritable)
        {
            try
            {
                collectionConsentGranted = FirstRunConsent.EnsureGranted(
                    new PortableSettingsStore(AppContext.BaseDirectory),
                    new WpfPortableConsentPrompt());
            }
            catch (Exception error)
            {
                consentError = error.Message;
            }
        }

        string? storageStartupError = null;
        DashboardViewModel? dashboard = null;
        if (directory.IsWritable && collectionConsentGranted)
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
        _collection = new CollectionController(
            directory,
            collectionControl,
            storageStartupError,
            collectionConsentGranted,
            consentError);
        string executablePath = Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "InputTracker.exe");
        StartupShortcutManager? startup = CreateStartupShortcutManager(executablePath);
        _dataTools = _storage is not null && collectionControl is not null
            ? new AppStorageDataToolsController(
                new StorageServiceDataTools(_storage),
                collectionControl,
                directory.DirectoryPath)
            : null;
        var window = new MainWindow(_collection, directory, dashboard, startup, _dataTools);

        _trayIcon = new TrayIconController(
            _collection,
            showWindow: () => ShowWindow(window),
            exitApplication: () =>
            {
                TryExplicitExit(window);
            });

        window.MinimizeToTrayRequested += (_, _) => window.Hide();
        window.ExitRequested += (_, _) => TryExplicitExit(window);
        MainWindow = window;
        window.Show();

        if (_capture is not null)
        {
            try
            {
                try
                {
                    _systemEvents = new PortableSystemEventBridge(
                        window,
                        new PortablePauseController(collectionControl!),
                        (operation, error) => window.Dispatcher.BeginInvoke(
                            () => _collection.HandleCaptureControlException(operation, error)));
                }
                catch (Exception error)
                {
                    _collection.HandlePreCaptureStartException("注册锁屏采集边界", error);
                    return;
                }

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
                _capture is null && _systemEvents is null
                    ? null
                    : () => StopCaptureAndSystemEventsAsync(_capture, _systemEvents),
                _disposeStorageOnExit ? _storage : null,
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
            _disposeStorageOnExit = _dataTools?.BeginSessionEnding(TimeSpan.FromSeconds(2)) ?? true;
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

    private void TryExplicitExit(MainWindow window)
    {
        if (_dataTools is not null && !_dataTools.TryBeginExplicitExit())
        {
            window.ShowDataToolExitBlocked();
            return;
        }
        window.PrepareForExplicitExit();
        Shutdown(0);
    }

    private static StartupShortcutManager? CreateStartupShortcutManager(string executablePath)
    {
        string startupDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        return string.IsNullOrWhiteSpace(startupDirectory)
            ? null
            : new StartupShortcutManager(executablePath, startupDirectory);
    }

    private static async Task StopCaptureAndSystemEventsAsync(
        InputCaptureService? capture,
        PortableSystemEventBridge? systemEvents)
    {
        Exception? first = null;
        try
        {
            systemEvents?.Dispose();
        }
        catch (Exception error)
        {
            first = error;
        }

        try
        {
            if (capture is not null) await capture.DisposeAsync();
        }
        catch (Exception error)
        {
            first ??= error;
        }

        if (first is not null)
        {
            throw new AggregateException(
                "System lifecycle notification and capture cleanup completed with a failure.", first);
        }
    }
}
