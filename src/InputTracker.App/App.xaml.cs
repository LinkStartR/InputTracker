using System.Windows;
using System.IO;
using System.Diagnostics;
using InputTracker.Input;
using InputTracker.Input.Win32;
using InputTracker.Storage;

namespace InputTracker;

public partial class App : System.Windows.Application, IAppOfflineRestoreRuntime
{
    private SingleInstanceGuard? _singleInstance;
    private TrayIconController? _trayIcon;
    private InputCaptureService? _capture;
    private CollectionController? _collection;
    private StorageService? _storage;
    private AppStorageCoordinator? _storageCoordinator;
    private PortableSystemEventBridge? _systemEvents;
    private AppStorageDataToolsController? _dataTools;
    private MousePlaybackController? _mousePlayback;
    private StorageLayout? _storageLayout;
    private bool _disposeStorageOnExit = true;
    private InputCaptureService? _releasedCaptureForRestore;
    private readonly AppOfflineRestoreLifecycleGate _offlineRestoreLifecycle = new();

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
        MousePlaybackController? mousePlayback = null;
        if (directory.IsWritable && collectionConsentGranted)
        {
            try
            {
                _storageLayout = StorageLayout.Create(AppContext.BaseDirectory);
                _storage = StorageService.Open(_storageLayout);
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
                mousePlayback = new MousePlaybackController(new StorageMousePlaybackDataSource(_storage));
                _mousePlayback = mousePlayback;
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
        var window = new MainWindow(_collection, directory, dashboard, startup, _dataTools, mousePlayback);

        _trayIcon = new TrayIconController(
            _collection,
            showWindow: () => ShowWindow(window),
            exitApplication: () =>
            {
                TryExplicitExit(window);
            });

        window.MinimizeToTrayRequested += (_, _) => window.Hide();
        window.ExitRequested += (_, _) => TryExplicitExit(window);
        window.OfflineRestoreRequested += async (_, request) =>
        {
            if (!_offlineRestoreLifecycle.TryBeginRestore())
            {
                return;
            }

            var coordinator = new AppOfflineRestoreCoordinator(this, Stopwatch.GetTimestamp);
            await coordinator.ExecuteAsync(request.Lease);
        };
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
        bool normalResourceCleanup = _offlineRestoreLifecycle.AllowsNormalExitResourceCleanup;
        try
        {
            if (normalResourceCleanup)
                _mousePlayback?.DisposeAsync().AsTask().GetAwaiter().GetResult();
            AppShutdown.Run(
                !normalResourceCleanup || (_capture is null && _systemEvents is null)
                    ? null
                    : () => StopCaptureAndSystemEventsAsync(_capture, _systemEvents),
                normalResourceCleanup && _disposeStorageOnExit ? _storage : null,
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
            if (_offlineRestoreLifecycle.RestoreActive)
            {
                _ = window.PrepareForOfflineRestore();
                window.PrepareForSessionEnding();
                if (_offlineRestoreLifecycle.TryRequestShutdown()) Shutdown(0);
                return;
            }
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

    async ValueTask IAppOfflineRestoreRuntime.QuiesceUiAsync()
    {
        if (MainWindow is not MainWindow window)
            throw new InvalidOperationException("Offline restore requires the active MainWindow lifecycle owner.");
        MousePlaybackController? windowPlayback = window.PrepareForOfflineRestore();
        MousePlaybackController? appPlayback = AppOfflineRestoreOwnership.Take(ref _mousePlayback);
        if (windowPlayback is not null && appPlayback is not null
            && !ReferenceEquals(windowPlayback, appPlayback))
        {
            throw new InvalidOperationException("Mouse playback ownership changed during offline restore teardown.");
        }
        MousePlaybackController? playback = appPlayback ?? windowPlayback;
        if (playback is not null) await playback.DisposeAsync();
    }

    void IAppOfflineRestoreRuntime.ReleaseSystemEventBridge()
    {
        PortableSystemEventBridge owner = AppOfflineRestoreOwnership.Take(ref _systemEvents)
            ?? throw new InvalidOperationException("System event bridge ownership was unavailable during offline restore.");
        owner.Dispose();
    }

    async ValueTask IAppOfflineRestoreRuntime.ReleaseCaptureAsync()
    {
        InputCaptureService owner = AppOfflineRestoreOwnership.Take(ref _capture)
            ?? throw new InvalidOperationException("Capture ownership was unavailable during offline restore.");
        _releasedCaptureForRestore = owner;
        await owner.DisposeAsync();
    }

    DiagnosticsSnapshot IAppOfflineRestoreRuntime.CaptureDiagnostics =>
        _releasedCaptureForRestore?.Diagnostics
        ?? throw new InvalidOperationException("Capture diagnostics were unavailable after owner release.");

    StorageServiceHealth IAppOfflineRestoreRuntime.StorageHealth =>
        _storage?.Health
        ?? throw new InvalidOperationException("Storage ownership was unavailable during offline restore.");

    void IAppOfflineRestoreRuntime.StopStorage(long monotonicTimestamp)
    {
        StorageService owner = _storage
            ?? throw new InvalidOperationException("Storage ownership was unavailable during offline restore stop.");
        owner.Stop(monotonicTimestamp);
    }

    void IAppOfflineRestoreRuntime.ReleaseStorageOwner()
    {
        StorageService owner = AppOfflineRestoreOwnership.Take(ref _storage)
            ?? throw new InvalidOperationException("Storage ownership was unavailable during offline restore disposal.");
        _storageCoordinator = null;
        _dataTools = null;
        _disposeStorageOnExit = false;
        owner.Dispose();
    }

    Task<StorageRestoreResult> IAppOfflineRestoreRuntime.RestoreOfflineAsync(string relativeBackupPath)
    {
        StorageLayout layout = _storageLayout
            ?? throw new InvalidOperationException("The startup Storage layout is unavailable for offline restore.");
        return Task.Run(() => StorageOfflineRestore.RestoreOffline(layout, relativeBackupPath));
    }

    bool IAppOfflineRestoreRuntime.IsRecoveryRequired =>
        File.Exists(AppOfflineRestorePaths.GetJournalPath(AppContext.BaseDirectory));

    void IAppOfflineRestoreRuntime.PublishStatus(AppOfflineRestoreStatus status)
    {
        _collection?.HandleOfflineRestoreStatus(status);
        if (MainWindow is MainWindow window) window.ShowOfflineRestoreStatus(status);
    }

    void IAppOfflineRestoreRuntime.RequestShutdown()
    {
        if (!_offlineRestoreLifecycle.TryRequestShutdown()) return;
        if (MainWindow is MainWindow window) window.PrepareForExplicitExit();
        Shutdown(0);
    }
}
