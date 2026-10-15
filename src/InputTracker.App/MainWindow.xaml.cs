using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using InputTracker.Storage;
using InputTracker.Input;

namespace InputTracker;

public partial class MainWindow : Window
{
    private readonly CollectionController _collection;
    private readonly DirectoryProbeResult _directory;
    private readonly WindowClosePolicy _closePolicy = new();
    private readonly DashboardViewModel? _dashboard;
    private readonly StartupShortcutManager? _startup;
    private readonly AppStorageDataToolsController? _dataTools;
    private readonly AppStorageCoordinator? _gamepadCoordinator;
    private readonly IInputCaptureControl? _capture;
    private readonly StorageService? _storage;
    private readonly PortableSettingsStore? _settings;
    private readonly KeyboardLayoutModel _keyboardLayout = KeyboardLayoutModel.CreateDefault();
    private readonly DispatcherTimer _dashboardRefreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _gamepadCalibrationRefreshTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private bool _dashboardRefreshInProgress;
    private bool _dashboardRefreshPending;
    private bool _updatingDeviceSelectors;
    private bool _deviceAliasChangeInProgress;
    private bool _updatingGamepadUi;
    private bool _userSelectedGamepadThisRun;
    private bool _gamepadAutomaticCalibrationTickInFlight;
    private bool _gamepadCalibrationUiClosed;
    private GamepadBrand? _pendingGamepadBrand;
    private RuntimeDeviceId? _gamepadCalibrationRuntimeId;
    private Guid? _gamepadPreferenceDeviceId;

    internal MainWindow(
        CollectionController collection,
        DirectoryProbeResult directory,
        DashboardViewModel? dashboard = null,
        StartupShortcutManager? startup = null,
        AppStorageDataToolsController? dataTools = null,
        AppStorageCoordinator? gamepadCoordinator = null,
        IInputCaptureControl? capture = null,
        StorageService? storage = null,
        PortableSettingsStore? settings = null)
    {
        InitializeComponent();
        _collection = collection;
        _directory = directory;
        _dashboard = dashboard;
        _startup = startup;
        _dataTools = dataTools;
        _gamepadCoordinator = gamepadCoordinator;
        _capture = capture;
        _storage = storage;
        _settings = settings;
        GamepadBrandSelector.ItemsSource = new[] { "Xbox", "其他品牌 / 第三方" };
        GamepadBrandSelector.SelectedIndex = -1;
        GamepadLayoutSelector.ItemsSource = new[] { "Xbox 布局" };
        GamepadLayoutSelector.SelectedIndex = -1;
        RestoreRememberedGamepadPreferences(
            settings?.Load().GamepadDeviceId ?? FindRememberedGamepadDeviceId());
        GamepadDetectDevicesButton.Click += GamepadDetectDevicesButton_Click;
        GamepadCheckButtonsButton.Click += GamepadCheckButtonsButton_Click;
        GamepadSkipButton.Click += GamepadSkipButton_Click;
        GamepadManualCalibrationButton.Click += GamepadManualCalibrationButton_Click;
        GamepadEnableStatsButton.Click += GamepadEnableStatsButton_Click;
        GamepadDeviceSelector.SelectionChanged += GamepadDeviceSelector_SelectionChanged;
        GamepadBrandSelector.SelectionChanged += GamepadBrandSelector_SelectionChanged;
        GamepadLayoutSelector.SelectionChanged += (_, _) => RefreshGamepadUi();
        DeletionKindSelector.SelectionChanged += DeletionKindSelector_SelectionChanged;
        UpdateDeletionDeviceSelectorVisibility();
        DateOnly today = DateOnly.FromDateTime(DateTime.Today);
        ViewDateRangePicker.SetRange(today, today, today);
        DeletionDateRangePicker.SetRange(today, today, today);
        _collection.Changed += Collection_Changed;
        _dashboardRefreshTimer.Tick += DashboardRefreshTimer_Tick;
        _gamepadCalibrationRefreshTimer.Tick += GamepadAutomaticCalibrationRefreshTimer_Tick;
        IsVisibleChanged += MainWindow_IsVisibleChanged;
        Closed += MainWindow_Closed;
        KeyboardHeatmapPresentation emptyHeatmap = KeyboardHeatmapPresentation.Create(_keyboardLayout, []);
        HeatmapItems.ItemsSource = emptyHeatmap.PhysicalKeys;
        OtherHeatmapItems.ItemsSource = emptyHeatmap.OtherKeys;
        Render(_collection.Current);
        bool dataToolsAvailable = _dataTools is not null;
        ExportKeyboardButton.IsEnabled = dataToolsAvailable;
        CreateBackupButton.IsEnabled = dataToolsAvailable;
        PreviewKeyboardDeletionButton.IsEnabled = dataToolsAvailable;
        DeletionKindSelector.IsEnabled = dataToolsAvailable;
        DeletionKeyboardDeviceSelector.IsEnabled = dataToolsAvailable;
        DeletionMouseDeviceSelector.IsEnabled = dataToolsAvailable;
        DeletionGamepadDeviceSelector.IsEnabled = dataToolsAvailable;
        ValidateRestoreButton.IsEnabled = dataToolsAvailable;
        if (!dataToolsAvailable)
        {
            DataToolsStatusText.Text = "本地数据没有成功打开，暂时不能使用数据工具。";
        }
        Loaded += MainWindow_Loaded;
    }

    public event EventHandler? MinimizeToTrayRequested;
    public event EventHandler? ExitRequested;
    internal event EventHandler<OfflineRestoreRequestedEventArgs>? OfflineRestoreRequested;

    internal void PrepareForExplicitExit()
    {
        _closePolicy.RequestExplicitExit();
        _dashboardRefreshTimer.Stop();
    }

    internal void PrepareForSessionEnding() => _closePolicy.RequestSessionEnding();

    internal void ShowDataToolExitBlocked()
    {
        DataToolsStatusText.Text = "数据操作或恢复选择正在进行；完成后再退出。";
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    internal void PrepareForOfflineRestore()
    {
        _dashboardRefreshTimer.Stop();
        _dashboardRefreshPending = false;
        SetDataToolsEnabled(false);
        RefreshDashboardButton.IsEnabled = false;
        ViewRangeControls.IsEnabled = false;
        DeletionKindSelector.IsEnabled = false;
        DeletionKeyboardDeviceSelector.IsEnabled = false;
        DeletionMouseDeviceSelector.IsEnabled = false;
        DeletionGamepadDeviceSelector.IsEnabled = false;
        KeyboardDeviceSelector.IsEnabled = false;
        MouseDeviceSelector.IsEnabled = false;
        KeyboardAliasInput.IsEnabled = false;
        MouseAliasInput.IsEnabled = false;
        SaveKeyboardAliasButton.IsEnabled = false;
        ClearKeyboardAliasButton.IsEnabled = false;
        SaveMouseAliasButton.IsEnabled = false;
        ClearMouseAliasButton.IsEnabled = false;
        PauseButton.IsEnabled = false;
        ResumeButton.IsEnabled = false;
        DataToolsStatusText.Text = "正在停止记录并关闭本地数据，准备恢复备份…";
    }

    internal void ShowOfflineRestoreStatus(AppOfflineRestoreStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        DataToolsStatusText.Text = status.PreRestoreBackupPath is null
            ? status.Message
            : $"{status.Message} 自动生成的恢复前备份：{status.PreRestoreBackupPath}";
    }

    private void Collection_Changed(object? sender, CollectionSnapshot snapshot) => Render(snapshot);

    private void Render(CollectionSnapshot snapshot)
    {
        StatusLabel.Text = snapshot.Label;
        StatusExplanation.Text = snapshot.Explanation;
        PrivacyExplanation.Text = PrivacyDisclosure.For(snapshot);
        DirectoryText.Text = $"数据位置：{_directory.DirectoryPath}";
        PauseButton.IsEnabled = snapshot.CanPause;
        ResumeButton.IsEnabled = snapshot.CanResume;
        PauseButton.Visibility = snapshot.CanPause ? Visibility.Visible : Visibility.Collapsed;
        ResumeButton.Visibility = snapshot.CanResume ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void PauseButton_Click(object sender, RoutedEventArgs e) =>
        await ExecuteControlAsync("暂停采集", _collection.PauseAsync);

    private async void ResumeButton_Click(object sender, RoutedEventArgs e) =>
        await ExecuteControlAsync("恢复采集", _collection.ResumeAsync);
    private void TitleMinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void TitleMaximizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void TitleCloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        ExitRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_closePolicy.ShouldHideInsteadOfClose())
        {
            return;
        }

        e.Cancel = true;
        MinimizeToTrayRequested?.Invoke(this, EventArgs.Empty);
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            MinimizeToTrayRequested?.Invoke(this, EventArgs.Empty);
        }

        bool maximized = WindowState == WindowState.Maximized;
        TitleMaximizeIcon.Data = Geometry.Parse(maximized
            ? "M 2 0 H 10 V 8 H 8 M 0 2 H 8 V 10 H 0 Z"
            : "M 0 0 H 10 V 10 H 0 Z");
        TitleMaximizeButton.ToolTip = maximized ? "还原" : "最大化";
    }

    private async Task ExecuteControlAsync(string operation, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception error)
        {
            _collection.HandleCaptureControlException(operation, error);
        }
    }

    private void EnableStartupButton_Click(object sender, RoutedEventArgs e) => ChangeStartup(enable: true);

    private void DisableStartupButton_Click(object sender, RoutedEventArgs e) => ChangeStartup(enable: false);

    private void LightThemeButton_Click(object sender, RoutedEventArgs e) => ApplyTheme(UiThemeKind.Light);

    private void DarkThemeButton_Click(object sender, RoutedEventArgs e) => ApplyTheme(UiThemeKind.Dark);

    private void ApplyTheme(UiThemeKind theme)
    {
        UiThemeController.Apply(System.Windows.Application.Current.Resources, theme);
        ThemeStatusText.Text = theme == UiThemeKind.Dark
            ? "当前为深色主题。"
            : "当前为浅色主题。";
    }

    private void ChangeStartup(bool enable)
    {
        if (_startup is null) return;
        try
        {
            if (enable) _startup.Enable();
            else _startup.Disable();
            RefreshStartupState();
        }
        catch (Exception error)
        {
            StartupStatusText.Text = $"开机启动设置失败，没有改动其他位置：{error.Message}";
        }
    }

    private void RefreshStartupState()
    {
        if (_startup is null)
        {
            EnableStartupButton.IsEnabled = false;
            DisableStartupButton.IsEnabled = false;
            StartupStatusText.Text = "当前无法设置开机启动：这台电脑上没有可用的 Startup 文件夹，程序不会创建或删除任何快捷方式。";
            return;
        }

        EnableStartupButton.IsEnabled = true;
        DisableStartupButton.IsEnabled = true;

        try
        {
            StartupStatusText.Text = _startup.IsEnabled
                ? "开机启动已开启。"
                : "开机启动已关闭。";
        }
        catch (Exception error)
        {
            StartupStatusText.Text = $"无法读取开机启动状态：{error.Message}";
        }
    }

    private async void ExportKeyboardButton_Click(object sender, RoutedEventArgs e)
    {
        if (_dataTools is null || _dashboard?.KeyboardPeriodResult is not { } period)
        {
            DataToolsStatusText.Text = "请先完成一次键盘统计读取。";
            return;
        }

        await RunDataToolAsync(async () =>
        {
            StorageDataToolsExportResult result = await _dataTools.ExportKeyboardAsync(period);
            if (CanUpdateDataToolsUi())
                DataToolsStatusText.Text = $"导出完成：{result.Csv.Path}；{result.Png.Path}";
        });
    }

    private async void CreateBackupButton_Click(object sender, RoutedEventArgs e) =>
        await RunDataToolAsync(async () =>
        {
            if (_dataTools is null) throw new InvalidOperationException("本地数据工具暂时不可用。");
            StorageBackupArtifact result = await _dataTools.CreateBackupAsync();
            if (CanUpdateDataToolsUi())
                DataToolsStatusText.Text = $"备份完成：{result.Path} · {result.FileCount:N0} 个文件";
        });

    private async void PreviewKeyboardDeletionButton_Click(object sender, RoutedEventArgs e) =>
        await DeleteSelectedRangeAsync();

    private void DeletionKindSelector_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateDeletionDeviceSelectorVisibility();

    private void UpdateDeletionDeviceSelectorVisibility()
    {
        DeletionTargetKind target = DeletionKindSelector.SelectedItem is ComboBoxItem item
            && Enum.TryParse(item.Tag?.ToString(), out DeletionTargetKind selected)
                ? selected
                : DeletionTargetKind.Keyboard;
        DeletionKeyboardDevicePanel.Visibility = target is DeletionTargetKind.Keyboard or DeletionTargetKind.All
            ? Visibility.Visible : Visibility.Collapsed;
        DeletionMouseDevicePanel.Visibility = target is DeletionTargetKind.Mouse or DeletionTargetKind.All
            ? Visibility.Visible : Visibility.Collapsed;
        DeletionGamepadDevicePanel.Visibility = target == DeletionTargetKind.Gamepad
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ViewDateRangePicker_RangeChanged(object? sender, EventArgs e)
    {
        if (IsLoaded) await RefreshDashboardAsync();
    }

    private void NavigationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, NavigationList)) return;

        string? page = (NavigationList.SelectedItem as TabItem)?.Tag?.ToString();
        bool showViewRange = page is "Overview" or "KeyboardHeatmap" or "MouseButtons" or "DataTools";
        ViewRangeControls.Visibility = showViewRange ? Visibility.Visible : Visibility.Collapsed;
        ViewRangeSpacer.Visibility = showViewRange ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task DeleteSelectedRangeAsync() =>
        await RunDataToolAsync(async () =>
        {
            if (_dataTools is null || _dashboard is null)
                throw new InvalidOperationException("本地数据工具暂时不可用。");
            DeletionTargetKind targetKind = DeletionKindSelector.SelectedItem is ComboBoxItem item
                && Enum.TryParse(item.Tag?.ToString(), out DeletionTargetKind selectedKind)
                    ? selectedKind
                    : throw new InvalidOperationException("请选择键盘、鼠标、手柄按键或全部。");
            AppDateRangeSelection dates = new(
                DeletionDateRangePicker.InclusiveStartDate,
                DeletionDateRangePicker.InclusiveEndDate);
            StorageResolvedPeriod period = StoragePeriodResolver.ResolveRange(
                dates.InclusiveStartDate, dates.InclusiveEndDate, TimeZoneInfo.Local.Id);
            string localRange = dates.InclusiveStartDate == dates.InclusiveEndDate
                ? dates.InclusiveStartDate.ToString("yyyy-MM-dd")
                : $"{dates.InclusiveStartDate:yyyy-MM-dd} 至 {dates.InclusiveEndDate:yyyy-MM-dd}";

            if (targetKind == DeletionTargetKind.All)
            {
                if (DeletionKeyboardDeviceSelector.SelectedValue is not Guid batchKeyboardId
                    || DeletionMouseDeviceSelector.SelectedValue is not Guid batchMouseId)
                    throw new InvalidOperationException("选择“全部”前，请分别选择一个键盘和一只鼠标。");
                StorageDeletionBatchDisplay preview = await _dataTools.PreviewDeletionBatchAsync(
                    batchKeyboardId, batchMouseId, period.UtcRange);
                if (!preview.CanExecute)
                {
                    if (CanUpdateDataToolsUi()) DataToolsStatusText.Text = preview.Status;
                    return;
                }
                string keyboardName = (DeletionKeyboardDeviceSelector.SelectedItem as DeviceChoice)?.Name ?? "所选键盘";
                string mouseName = (DeletionMouseDeviceSelector.SelectedItem as DeviceChoice)?.Name ?? "所选鼠标";
                MessageBoxResult confirmation = System.Windows.MessageBox.Show(
                    $"将永久删除 {localRange} 内“{keyboardName}”的 {preview.KeyboardPressCount:N0} 次键盘按键，"
                    + $"以及“{mouseName}”的 {preview.MouseButtonPressCount:N0} 次鼠标按键。\n\n"
                    + "删除前只创建一份完整备份，并一次提交两类删除；其他设备和其他日期不受影响。是否继续？",
                    "最终确认 · 全部删除",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);
                if (confirmation != MessageBoxResult.Yes)
                {
                    if (CanUpdateDataToolsUi()) DataToolsStatusText.Text = "已取消删除，没有改动数据。";
                    return;
                }
                StorageDeletionBatchResult result = await _dataTools.ExecutePreparedDeletionBatchAsync(
                    preview, finalConfirmed: true);
                if (CanUpdateDataToolsUi())
                    DataToolsStatusText.Text =
                        $"已删除 {result.DeletedKeyboardPressCount:N0} 次键盘按键和 "
                        + $"{result.DeletedMouseButtonPressCount:N0} 次鼠标按键。删除前备份：{result.PreDeletionBackup.Path}";
                return;
            }

            StorageDeviceKind deviceKind = targetKind switch
            {
                DeletionTargetKind.Keyboard => StorageDeviceKind.Keyboard,
                DeletionTargetKind.Mouse => StorageDeviceKind.Mouse,
                DeletionTargetKind.Gamepad => StorageDeviceKind.Gamepad,
                _ => throw new InvalidOperationException("请选择单个设备类型。")
            };
            System.Windows.Controls.ComboBox selector = deviceKind switch
            {
                StorageDeviceKind.Keyboard => DeletionKeyboardDeviceSelector,
                StorageDeviceKind.Mouse => DeletionMouseDeviceSelector,
                _ => DeletionGamepadDeviceSelector
            };
            Guid? deviceId = selector.SelectedValue is Guid selectedDeviceId ? selectedDeviceId : null;
            if (deviceId is not Guid selected)
                throw new InvalidOperationException(deviceKind switch
                {
                    StorageDeviceKind.Keyboard => "请先选择一个键盘。",
                    StorageDeviceKind.Mouse => "请先选择一只鼠标。",
                    _ => "请先选择一个手柄。"
                });
            StorageDeletionDisplay single = await _dataTools.PreviewDeletionAsync(
                selected, deviceKind, period.UtcRange);
            if (!single.CanExecute)
            {
                if (CanUpdateDataToolsUi()) DataToolsStatusText.Text = single.Status;
                return;
            }
            string target = deviceKind switch
            {
                StorageDeviceKind.Keyboard => "键盘统计",
                StorageDeviceKind.Mouse => "鼠标按键统计",
                _ => "手柄按键统计"
            };
            string deviceName = (selector.SelectedItem as DeviceChoice)?.Name ?? "所选设备";
            MessageBoxResult singleConfirmation = System.Windows.MessageBox.Show(
                $"将永久删除“{deviceName}”在 {localRange} 的 {single.PressCount:N0} 次{target}。\n\n"
                + "删除前会自动创建完整备份；其他设备和其他日期不受影响。是否继续？",
                $"最终确认 · 删除{target}",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (singleConfirmation != MessageBoxResult.Yes)
            {
                if (CanUpdateDataToolsUi()) DataToolsStatusText.Text = "已取消删除，没有改动数据。";
                return;
            }
            StorageDeletionResult singleResult = await _dataTools.ExecutePreparedDeletionAsync(
                single, finalConfirmed: true);
            if (CanUpdateDataToolsUi()) DataToolsStatusText.Text =
                $"已删除 {(deviceKind switch { StorageDeviceKind.Keyboard => singleResult.DeletedKeyboardPressCount, StorageDeviceKind.Mouse => singleResult.DeletedMouseButtonPressCount, _ => singleResult.DeletedGamepadPressCount }):N0} 次{target}。"
                + $"删除前备份：{singleResult.PreDeletionBackup.Path}";
        });

    private void ValidateRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (_dataTools is null)
        {
            DataToolsStatusText.Text = "本地数据工具暂时不可用。";
            return;
        }

        bool transferred = false;
        SetDataToolsEnabled(false);
        try
        {
            using AppStorageDataToolsController.RestoreInteraction interaction = _dataTools.TryBeginInteraction();
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择程序目录内的 InputTracker 恢复备份",
                Filter = "InputTracker backup (*.itbackup)|*.itbackup",
                InitialDirectory = System.IO.Path.Combine(_directory.DirectoryPath, "backups"),
                CheckFileExists = true,
                Multiselect = false
            };
            if (dialog.ShowDialog(this) != true) return;
            RestoreSelection selection = interaction.Validate(dialog.FileName);
            DataToolsStatusText.Text = selection.Status;
            MessageBoxResult confirmation = System.Windows.MessageBox.Show(
                "即将执行离线恢复：当前采集会停止，现有数据将被替换，InputTracker 会自动生成恢复前备份；成功或失败后 InputTracker 都会退出。是否确认继续？",
                "最终确认 · 恢复本地备份",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirmation != MessageBoxResult.Yes)
            {
                DataToolsStatusText.Text = "已取消恢复；保持当前用户暂停状态，未停止服务或更改数据。";
                return;
            }
            if (OfflineRestoreRequested is null)
                throw new InvalidOperationException("离线恢复生命周期协调器不可用，未执行恢复。");
            AppStorageDataToolsController.RestoreLease lease = interaction.TransferToRestore(
                selection,
                finalConfirmed: true);
            transferred = true;
            OfflineRestoreRequested?.Invoke(this, new OfflineRestoreRequestedEventArgs(lease));
        }
        catch (Exception error)
        {
            if (IsLoaded && !_dataTools.IsClosing)
                DataToolsStatusText.Text = $"恢复请求失败，未停止服务或更改数据：{error.Message}";
        }
        finally
        {
            if (!transferred && IsLoaded && !_dataTools.IsClosing) SetDataToolsEnabled(true);
        }
    }

    private async Task RunDataToolAsync(Func<Task> action)
    {
        SetDataToolsEnabled(false);
        try
        {
            DataToolsStatusText.Text = "正在执行本地数据操作…";
            await action();
        }
        catch (Exception error)
        {
            if (IsLoaded && !(_dataTools?.IsClosing ?? false))
                DataToolsStatusText.Text = $"数据操作失败，未宣称成功：{error.Message}";
        }
        finally
        {
            if (IsLoaded && !(_dataTools?.IsClosing ?? false))
                SetDataToolsEnabled(_dataTools is not null);
        }
    }

    private void SetDataToolsEnabled(bool enabled)
    {
        ExportKeyboardButton.IsEnabled = enabled;
        CreateBackupButton.IsEnabled = enabled;
        PreviewKeyboardDeletionButton.IsEnabled = enabled;
        DeletionKindSelector.IsEnabled = enabled;
        DeletionKeyboardDeviceSelector.IsEnabled = enabled;
        DeletionMouseDeviceSelector.IsEnabled = enabled;
        DeletionGamepadDeviceSelector.IsEnabled = enabled;
        DeletionDateControls.IsEnabled = enabled;
        ValidateRestoreButton.IsEnabled = enabled;
    }

    private bool CanUpdateDataToolsUi() => IsLoaded && !(_dataTools?.IsClosing ?? false);

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshStartupState();
        bool gamepadReady = _gamepadCoordinator is not null && _capture is not null && _storage is not null && _settings is not null;
        GamepadDetectDevicesButton.IsEnabled = gamepadReady;
        GamepadBrandSelector.IsEnabled = gamepadReady;
        GamepadLayoutSelector.IsEnabled = gamepadReady;
        await RefreshDashboardAsync();
        RefreshGamepadUi();
        if (_dashboard is not null && IsVisible) _dashboardRefreshTimer.Start();
    }

    private async void GamepadDetectDevicesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null || _capture is null || _gamepadCoordinator is null) return;
        if (GamepadBrandSelector.SelectedItem is not string || GamepadLayoutSelector.SelectedItem is null)
        {
            GamepadStatusText.Text = "请先选择品牌和 Xbox 布局，再检测设备。";
            return;
        }
        try
        {
            PortableSettings settings = _settings.Load();
            if (!settings.GamepadConsentGranted)
            {
                bool consent = new GamepadConfirmationWindow(
                    "手柄采集说明",
                    "开启手柄检测后，InputTracker 会读取所选手柄的按键状态，用于逐键校准和统计按键次数。\n\n按键映射、设备标识和统计保存在本机数据文件中；不会记录摇杆移动或扳机行程。手柄检测和统计需单独授权。",
                    "同意并继续",
                    "暂不授权") { Owner = this }.ShowDialog() == true;
                if (!consent) return;
                _settings.SetGamepadConsent(true);
            }

            _gamepadCoordinator.SetGamepadCaptureEnabled(true);
            await _capture.SetGamepadCaptureEnabledAsync(true);
            GamepadStatusText.Text = "正在检测手柄。请按一下手柄任意键使设备出现，再选择设备并逐键校准；未保存校准不会计数。";
            RefreshGamepadUi();
        }
        catch (Exception error)
        {
            GamepadStatusText.Text = $"手柄检测失败：{error.Message}";
        }
    }

    private void GamepadCheckButtonsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_gamepadCoordinator is null || SelectedGamepad is not { } device) return;
        try
        {
            if (_gamepadCoordinator.IsGamepadAutomaticCalibration)
            {
                if (_gamepadCoordinator.IsGamepadTriggerAwaitingRestingConfirmation)
                {
                    if (!_gamepadCoordinator.ConfirmGamepadTriggerRestingPoint())
                    {
                        GamepadStatusText.Text = "尚未检测到扳机轴状态。请松开当前扳机并操作一次手柄，再确认松开值。";
                        return;
                    }
                    RefreshGamepadUi(automaticCalibrationProgressOnly: true);
                    return;
                }

                if (_gamepadCoordinator.CurrentCalibrationTarget is null)
                {
                    bool confirmation = new GamepadConfirmationWindow(
                        "确认保存手柄校准",
                        $"已识别 {_gamepadCoordinator.GamepadCalibrationBindings.Count} 个按键映射。保存后，统计会按这组映射写入本机数据。请确认设备和 Xbox 布局选择正确。",
                        "保存校准",
                        "返回检查") { Owner = this }.ShowDialog() == true;
                    if (!confirmation) return;
                    _gamepadCalibrationRefreshTimer.Stop();
                    _gamepadCoordinator.SaveGamepadCalibration(device.RuntimeId);
                    _gamepadCalibrationRuntimeId = null;
                    if (device.PersistentDeviceId is Guid selectedDeviceId)
                        _settings?.SetGamepadConfiguration(enabled: false, deviceId: selectedDeviceId);
                    SaveSelectedGamepadBrand(device);
                    GamepadStatusText.Text = "校准已保存；统计仍保持停用。";
                    RefreshGamepadUi();
                    return;
                }

                bool cancel = new GamepadConfirmationWindow(
                    "取消自动检测",
                    "本次尚未保存的按键映射会被丢弃，原有校准配置会保留。",
                    "取消检测",
                    "继续检测") { Owner = this }.ShowDialog() == true;
                if (cancel)
                {
                    _gamepadCalibrationRefreshTimer.Stop();
                    _gamepadCoordinator.CancelGamepadCalibration();
                    _gamepadCalibrationRuntimeId = null;
                    GamepadStatusText.Text = "自动检测已取消；已保存的校准配置仍保留。";
                    RefreshGamepadUi();
                }
                return;
            }

            if (_gamepadCoordinator.IsGamepadCalibrating)
            {
                if (_gamepadCoordinator.IsGamepadTriggerAwaitingRestingConfirmation)
                {
                    if (!_gamepadCoordinator.ConfirmGamepadTriggerRestingPoint())
                    {
                        GamepadStatusText.Text = "尚未检测到扳机轴状态。请松开当前扳机并操作一次手柄，再确认松开值。";
                        return;
                    }
                    GamepadStatusText.Text = "松开值已记录；请将当前扳机完全按下，再确认识别结果。";
                    RefreshGamepadUi();
                    return;
                }
                if (_gamepadCoordinator.HasGamepadCalibrationCandidate)
                {
                    _gamepadCoordinator.AcceptGamepadCalibrationCandidate();
                    GamepadStatusText.Text = "按键映射已确认；请继续下一项。";
                    RefreshGamepadUi();
                    return;
                }
                if (_gamepadCoordinator.CurrentCalibrationTarget is not null)
                {
                    GamepadStatusText.Text = "请继续按当前提示逐键操作，或点击“此键不支持”。";
                    return;
                }
                bool confirmation = new GamepadConfirmationWindow(
                    "确认保存手柄校准",
                    $"已识别 {_gamepadCoordinator.GamepadCalibrationBindings.Count} 个按键映射。保存后，统计会按这组映射写入本机数据。请确认手柄和 Xbox 布局选择正确。",
                    "保存校准",
                    "返回检查") { Owner = this }.ShowDialog() == true;
                if (!confirmation) return;
                _gamepadCoordinator.SaveGamepadCalibration(device.RuntimeId);
                _gamepadCalibrationRuntimeId = null;
                if (device.PersistentDeviceId is Guid selectedDeviceId)
                    _settings?.SetGamepadConfiguration(enabled: false, deviceId: selectedDeviceId);
                SaveSelectedGamepadBrand(device);
                GamepadStatusText.Text = "校准已保存；尚未启用统计。";
            }
            else
            {
                StorageGamepadConfiguration? previous = _gamepadCoordinator.GetGamepadConfiguration(device.RuntimeId);
                if (_gamepadCoordinator.HasEnabledGamepad || previous?.Enabled == true)
                    throw new InvalidOperationException("请先停用当前统计，再重新检测手柄按键。");
                if (GamepadLayoutSelector.SelectedItem is null)
                    throw new InvalidOperationException("请先选择 Xbox 布局。");
                if ((previous is null || previous.Bindings.Count == 0) && SelectedGamepadBrand is null)
                    throw new InvalidOperationException("首次检测前请先选择品牌。");
                bool start = new GamepadConfirmationWindow(
                    "自动检测手柄按键",
                    "请按屏幕提示依次按下并松开手柄按键。识别成功后会自动进入下一项；不支持的按键可以跳过。扳机可能需要先确认松开值。完成后统一确认保存，本次保存前不会累计次数。",
                    "开始检测",
                    "稍后再说") { Owner = this }.ShowDialog() == true;
                if (!start) return;
                _gamepadCoordinator.BeginGamepadAutomaticCalibration(device.RuntimeId);
                _gamepadCalibrationRuntimeId = device.RuntimeId;
                _gamepadCalibrationRefreshTimer.Start();
                GamepadStatusText.Text = "请按提示按下并松开按键；检测会自动推进。";
            }
            RefreshGamepadUi();
        }
        catch (Exception error)
        {
            GamepadStatusText.Text = $"校准失败：{error.Message}";
        }
    }

    private void GamepadSkipButton_Click(object sender, RoutedEventArgs e)
    {
        if (_gamepadCoordinator?.HasGamepadCalibrationCandidate == true)
            _gamepadCoordinator.RetryGamepadCalibrationTarget();
        else
            _gamepadCoordinator?.SkipGamepadCalibrationTarget();
        RefreshGamepadUi(_gamepadCoordinator?.IsGamepadAutomaticCalibration == true);
    }

    private void GamepadManualCalibrationButton_Click(object sender, RoutedEventArgs e)
    {
        if (_gamepadCoordinator?.IsGamepadAutomaticCalibration != true) return;
        _gamepadCoordinator.SwitchGamepadCalibrationToManual();
        _gamepadCalibrationRefreshTimer.Stop();
        GamepadStatusText.Text = "已切换为手动确认当前按键；请按提示操作并确认识别结果。";
        RefreshGamepadUi();
    }

    private async void GamepadEnableStatsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_gamepadCoordinator is null || _capture is null || _settings is null || SelectedGamepad is not { } device) return;
        bool configurationChanged = false;
        StorageGamepadConfiguration? previous = _gamepadCoordinator.GetGamepadConfiguration(device.RuntimeId);
        try
        {
            bool enable = !_gamepadCoordinator.IsGamepadEnabled(device.RuntimeId);
            if (enable)
            {
                if (_gamepadCoordinator.IsGamepadCalibrating || previous is null || previous.Bindings.Count == 0)
                    throw new InvalidOperationException("请先完成并保存至少一个按键映射，再启用统计。");
                string brand = GamepadBrandSelector.SelectedItem as string ?? "品牌未记录";
                bool final = new GamepadConfirmationWindow(
                    "确认启用手柄统计",
                    $"将开始统计“{device.DisplayName}”（{brand}，Xbox 布局）中已校准按键的按下次数，并保存在本机。再次点击可停用；修改校准会使用新的配置版本。",
                    "启用统计",
                    "暂不开启") { Owner = this }.ShowDialog() == true;
                if (!final) return;
            }
            _gamepadCoordinator.SetGamepadEnabled(device.RuntimeId, enable);
            configurationChanged = true;
            if (enable) SaveSelectedGamepadBrand(device);
            bool captureEnabled = _settings.Load().GamepadConsentGranted;
            await _capture.SetGamepadCaptureEnabledAsync(captureEnabled);
            GamepadStatusText.Text = enable ? "手柄统计已启用。" : "手柄统计已停用。";
            RefreshGamepadUi();
        }
        catch (Exception error)
        {
            if (configurationChanged)
            {
                try
                {
                    _gamepadCoordinator.SetGamepadEnabled(device.RuntimeId, previous?.Enabled == true);
                    await _capture.SetGamepadCaptureEnabledAsync(_settings.Load().GamepadConsentGranted);
                }
                catch { }
            }
            GamepadStatusText.Text = $"无法更改手柄统计状态：{error.Message}";
        }
    }

    private GamepadDeviceChoice? SelectedGamepad => GamepadDeviceSelector.SelectedItem as GamepadDeviceChoice;

    private void GamepadDeviceSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingGamepadUi) return;
        if (_gamepadCalibrationRuntimeId is RuntimeDeviceId calibrationDevice
            && SelectedGamepad?.RuntimeId != calibrationDevice)
        {
            _gamepadCalibrationRefreshTimer.Stop();
            _gamepadCoordinator?.CancelGamepadCalibration();
            _gamepadCalibrationRuntimeId = null;
            GamepadStatusText.Text = "所选手柄已变化，未保存的校准已取消。";
        }
        _userSelectedGamepadThisRun = true;
        RestoreRememberedGamepadPreferences(SelectedGamepad?.PersistentDeviceId, keepPendingBrand: true);
        RefreshGamepadUi();
    }

    private void GamepadBrandSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingGamepadUi) return;
        GamepadBrand? brand = SelectedGamepadBrand;
        if (SelectedGamepad is { PersistentDeviceId: Guid deviceId } && brand is GamepadBrand selectedBrand)
        {
            if (_settings?.Load().GamepadConsentGranted == true)
                _settings.SetGamepadBrand(deviceId, selectedBrand);
            _pendingGamepadBrand = null;
        }
        else
        {
            _pendingGamepadBrand = brand;
        }

        RefreshGamepadUi();
    }

    private GamepadBrand? SelectedGamepadBrand => GamepadBrandSelector.SelectedItem switch
    {
        "Xbox" => GamepadBrand.Xbox,
        "其他品牌 / 第三方" => GamepadBrand.ThirdParty,
        _ => null
    };

    private void SaveSelectedGamepadBrand(GamepadDeviceChoice device)
    {
        if (device.PersistentDeviceId is not Guid deviceId || SelectedGamepadBrand is not GamepadBrand brand
            || _settings?.Load().GamepadConsentGranted != true)
            return;

        _settings.SetGamepadBrand(deviceId, brand);
        _pendingGamepadBrand = null;
    }

    private void RestoreRememberedGamepadPreferences(Guid? deviceId, bool keepPendingBrand = false)
    {
        if (_settings is null) return;
        deviceId ??= FindRememberedGamepadDeviceId();
        GamepadBrand? brand = deviceId is Guid id ? _settings.GetGamepadBrand(id) : null;
        if (brand is not null) _pendingGamepadBrand = null;
        else if (!keepPendingBrand) _pendingGamepadBrand = null;
        brand ??= _pendingGamepadBrand;
        bool wasUpdatingGamepadUi = _updatingGamepadUi;
        _updatingGamepadUi = true;
        try
        {
            GamepadBrandSelector.SelectedItem = brand switch
            {
                GamepadBrand.Xbox => "Xbox",
                GamepadBrand.ThirdParty => "其他品牌 / 第三方",
                _ => null
            };
            StorageGamepadConfiguration? configuration = deviceId is Guid storedId
                ? _storage?.GetGamepadConfiguration(storedId)
                : null;
            if (configuration?.Layout == StorageGamepadLayout.Xbox)
                GamepadLayoutSelector.SelectedIndex = 0;
            else if (GamepadDeviceSelection.ShouldClearUnrememberedLayout(
                         _gamepadPreferenceDeviceId, deviceId, configuration?.Layout))
                GamepadLayoutSelector.SelectedIndex = -1;
            _gamepadPreferenceDeviceId = deviceId;
        }
        finally
        {
            _updatingGamepadUi = wasUpdatingGamepadUi;
        }
    }

    private Guid? FindRememberedGamepadDeviceId()
    {
        if (_storage is null) return null;
        Guid? preferredDeviceId = _settings?.Load().GamepadDeviceId;
        IReadOnlyList<StorageDeviceInfo> devices = _storage.ListDevices()
            .Where(device => device.Kind == StorageDeviceKind.Gamepad)
            .OrderBy(device => device.CreatedUtcMicroseconds)
            .ThenBy(device => device.DeviceId)
            .ToArray();
        if (preferredDeviceId is Guid preferred
            && _storage.GetGamepadConfiguration(preferred) is { Bindings.Count: > 0 })
            return preferred;

        return devices.FirstOrDefault(device =>
            _storage.GetGamepadConfiguration(device.DeviceId) is { Bindings.Count: > 0 })?.DeviceId;
    }

    private void RefreshGamepadUi()
        => RefreshGamepadUi(automaticCalibrationProgressOnly: false);

    private void RefreshGamepadUi(bool automaticCalibrationProgressOnly)
    {
        if (_gamepadCoordinator is null || _settings is null) return;
        if (_updatingGamepadUi) return;
        bool progressOnly = automaticCalibrationProgressOnly || _gamepadCoordinator.IsGamepadAutomaticCalibration;
        _updatingGamepadUi = true;
        try
        {
            RefreshGamepadUiCore(progressOnly);
        }
        finally
        {
            _updatingGamepadUi = false;
        }
    }

    private void RefreshGamepadUiCore(bool automaticCalibrationProgressOnly)
    {
        AppStorageCoordinator gamepadCoordinator = _gamepadCoordinator!;
        if (automaticCalibrationProgressOnly)
        {
            RefreshGamepadCalibrationProgress(gamepadCoordinator);
            return;
        }

        PortableSettingsStore settingsStore = _settings!;
        bool consented = settingsStore.Load().GamepadConsentGranted;
        GamepadBrandSelector.IsEnabled = _capture is not null;
        GamepadLayoutSelector.IsEnabled = _capture is not null;
        GamepadDeviceSelector.IsEnabled = consented && !gamepadCoordinator.IsGamepadCalibrating;
        GamepadDetectDevicesButton.IsEnabled = _capture is not null
            && GamepadBrandSelector.SelectedItem is string && GamepadLayoutSelector.SelectedItem is not null;
        GamepadCheckButtonsButton.IsEnabled = consented && SelectedGamepad is not null;
        GamepadSkipButton.IsEnabled = consented && gamepadCoordinator.IsGamepadCalibrating
            && gamepadCoordinator.CurrentCalibrationTarget is not null;
        GamepadSkipButton.Content = gamepadCoordinator.HasGamepadCalibrationCandidate ? "重新操作" : "此键不支持";
        StorageGamepadConfiguration? selectedConfiguration = SelectedGamepad is { } selectedChoice
            ? gamepadCoordinator.GetGamepadConfiguration(selectedChoice.RuntimeId) : null;
        GamepadEnableStatsButton.IsEnabled = consented && SelectedGamepad is not null
            && !gamepadCoordinator.IsGamepadCalibrating
            && (selectedConfiguration is { Bindings.Count: > 0 } || selectedConfiguration is { Enabled: true });
        IReadOnlyDictionary<Guid, string> labels = _storage is null
            ? new Dictionary<Guid, string>()
            : GamepadDeviceLabels.Create(_storage.ListDevices());
        GamepadDeviceChoice[] devices = gamepadCoordinator.GamepadDevices
            .Select(device =>
            {
                Guid? persistentDeviceId = gamepadCoordinator.GetGamepadStorageId(device.RuntimeId);
                string displayName = persistentDeviceId is Guid id && labels.TryGetValue(id, out string? label)
                    ? label : "待识别手柄";
                return new GamepadDeviceChoice(device, displayName, persistentDeviceId);
            })
            .ToArray();
        Guid? preferredDeviceId = settingsStore.Load().GamepadDeviceId;
        RuntimeDeviceId? selectedRuntimeId = GamepadDeviceSelection.SelectRuntimeId(
            devices.Select(device => (device.RuntimeId, device.PersistentDeviceId)).ToArray(),
            preferredDeviceId,
            SelectedGamepad?.RuntimeId,
            _userSelectedGamepadThisRun);
        if (!GamepadDeviceSelector.Items.Cast<GamepadDeviceChoice>().SequenceEqual(devices))
            GamepadDeviceSelector.ItemsSource = devices;
        GamepadDeviceSelector.SelectedValue = selectedRuntimeId;
        GamepadDeviceChoice? selectedDevice = SelectedGamepad;
        if (selectedDevice?.PersistentDeviceId is Guid selectedStorageId)
            RestoreRememberedGamepadPreferences(selectedStorageId, keepPendingBrand: true);
        if (SelectedGamepad is not { } gamepad)
        {
            Guid? rememberedId = FindRememberedGamepadDeviceId();
            StorageGamepadConfiguration? rememberedConfiguration = rememberedId is Guid id
                ? _storage?.GetGamepadConfiguration(id) : null;
            string rememberedLabel = rememberedId is Guid remembered
                && labels.TryGetValue(remembered, out string? savedLabel) ? savedLabel : "上次手柄";
            bool hasRestoredCalibration = rememberedConfiguration is { Bindings.Count: > 0, Layout: StorageGamepadLayout.Xbox };
            bool rememberedCaptureEnabled = rememberedConfiguration?.Enabled == true
                && settingsStore.Load().GamepadCaptureEnabled;
            GamepadEnabledStatusText.Text = !consented ? "尚未授权"
                : hasRestoredCalibration
                    ? $"已恢复配置 · {(rememberedCaptureEnabled ? "统计已启用" : "统计已停用，配置已保留")} · 等待上次手柄连接"
                    : "等待检测设备";
            GamepadCalibrationStatusText.Text = hasRestoredCalibration
                ? $"已恢复 Xbox 布局校准 {rememberedConfiguration!.Bindings.Count} 键"
            : gamepadCoordinator.GamepadCalibrationInterrupted ? "连接中断，请重新选择设备并校准" : "尚未校准";
            GamepadHeatmapItems.ItemsSource = GamepadLayoutModel.CreateXboxLayout();
            SetGamepadHighlights([], hasRestoredCalibration
                ? $"已记住“{rememberedLabel}”的 Xbox 布局配置；连接并操作该手柄后会自动恢复。"
                : "连接手柄并完成按键校准后，这里会显示真实按键次数。");
            if (consented && hasRestoredCalibration)
                GamepadStatusText.Text = $"已恢复“{rememberedLabel}”的校准配置；{(rememberedCaptureEnabled ? "统计已启用" : "上次已停用，本次不会自动启用统计")}。等待该手柄连接；如未出现在列表中，可按一下唤醒。";
            else if (consented && devices.Length == 0)
                GamepadStatusText.Text = "请连接手柄；如设备未出现在列表中，可按一下唤醒。";
            return;
        }
        StorageGamepadConfiguration? config = gamepadCoordinator.GetGamepadConfiguration(gamepad.RuntimeId);
        string deviceStatus = gamepadCoordinator.GetGamepadStatus(gamepad.RuntimeId) ?? "已连接";
        bool restoredConfiguration = config is { Bindings.Count: > 0, Layout: StorageGamepadLayout.Xbox }
            && settingsStore.Load().GamepadDeviceId == gamepadCoordinator.GetGamepadStorageId(gamepad.RuntimeId);
        GamepadEnabledStatusText.Text = $"{deviceStatus} · {(restoredConfiguration ? "已恢复配置 · " : string.Empty)}{(config?.Enabled == true ? "统计已启用" : "统计已停用")}";
        bool globallyEnabled = gamepadCoordinator.IsGamepadEnabled(gamepad.RuntimeId);
        GamepadEnableStatsButton.Content = globallyEnabled ? "停用统计" : "启用统计";
        StorageGamepadButton? target = gamepadCoordinator.CurrentCalibrationTarget;
        GamepadCalibrationStatusText.Text = gamepadCoordinator.GamepadCalibrationInterrupted
            ? "连接中断，请重新校准"
            : gamepadCoordinator.IsGamepadCalibrating
            ? gamepadCoordinator.HasGamepadCalibrationCandidate
                ? $"已识别：{GetGamepadButtonLabel(target!.Value)}，请确认或重试"
                : target is null ? "可保存校准" : gamepadCoordinator.IsGamepadTriggerAwaitingRestingConfirmation
                    ? $"请松开并确认：{GetGamepadButtonLabel(target.Value)}" : $"请操作：{GetGamepadButtonLabel(target.Value)}"
            : config is null ? "尚未校准" : restoredConfiguration
                ? $"已恢复 Xbox 布局校准 {config.Bindings.Count} 键"
                : $"已校准 {config.Bindings.Count} 键";
        GamepadCheckButtonsButton.Content = gamepadCoordinator.IsGamepadAutomaticCalibration
            ? target is null ? "确认并保存"
                : gamepadCoordinator.IsGamepadTriggerAwaitingRestingConfirmation ? "确认扳机松开值" : "取消自动检测"
            : gamepadCoordinator.IsGamepadCalibrating && target is null
                ? "确认并保存" : gamepadCoordinator.HasGamepadCalibrationCandidate ? "确认此键"
                : gamepadCoordinator.IsGamepadTriggerAwaitingRestingConfirmation ? "确认扳机松开值"
                : gamepadCoordinator.IsGamepadCalibrating ? "校准中" : "自动检测按键";
        GamepadManualCalibrationButton.Visibility = gamepadCoordinator.IsGamepadAutomaticCalibration
            ? Visibility.Visible : Visibility.Collapsed;
        GamepadManualCalibrationButton.IsEnabled = gamepadCoordinator.IsGamepadAutomaticCalibration;
        IReadOnlySet<GamepadPhysicalPosition>? support = config is null
            ? null
            : config.Bindings.Select(binding => ToPhysicalPosition(binding.Button)).ToHashSet();
        Dictionary<GamepadPhysicalPosition, long> counts = [];
        Guid? storageId = gamepadCoordinator.GetGamepadStorageId(gamepad.RuntimeId);
        bool countsRead = false;
        if (config is not null && _storage is not null && storageId is Guid deviceId)
        {
            try
            {
                StorageGamepadButtonPeriodResult result = _storage.QueryGamepadButtonsRange(
                    ViewDateRangePicker.InclusiveStartDate, ViewDateRangePicker.InclusiveEndDate,
                    TimeZoneInfo.Local.Id, deviceId);
                foreach (IGrouping<StorageGamepadButton, StorageGamepadButtonPeriodAggregate> group in result.Aggregates.GroupBy(row => row.Button))
                    counts[ToPhysicalPosition(group.Key)] = group.Sum(row => row.PressCount);
                countsRead = true;
            }
            catch (Exception error) { GamepadStatusText.Text = $"手柄统计读取失败：{error.Message}"; }
        }
        IReadOnlyList<GamepadHeatmapPosition> heatmap = GamepadLayoutModel.CreateXboxLayout(counts, support);
        GamepadHeatmapItems.ItemsSource = heatmap;
        GamepadHeatmapPosition[] highlights = heatmap
            .Where(position => position.Support == GamepadPositionSupport.Supported && position.PressCount > 0)
            .OrderByDescending(position => position.PressCount)
            .Take(4)
            .ToArray();
        string emptyMessage = config is null || config.Bindings.Count == 0
            ? "完成逐键校准后，这里会显示真实按键次数。"
            : countsRead ? "所选时间范围内暂无按键记录。" : "暂时无法读取当前手柄的真实次数。";
        SetGamepadHighlights(highlights, emptyMessage);
    }

    private void RefreshGamepadCalibrationProgress(AppStorageCoordinator gamepadCoordinator)
    {
        if (!gamepadCoordinator.IsGamepadAutomaticCalibration) return;
        StorageGamepadButton? target = gamepadCoordinator.CurrentCalibrationTarget;
        bool awaitingRestingPoint = gamepadCoordinator.IsGamepadTriggerAwaitingRestingConfirmation;
        GamepadCalibrationStatusText.Text = awaitingRestingPoint
            ? $"请松开并确认：{GetGamepadButtonLabel(target!.Value)}"
            : target is null ? "全部按键已检测，可统一保存校准"
            : gamepadCoordinator.HasGamepadCalibrationCandidate
                ? $"已识别：{GetGamepadButtonLabel(target.Value)}，请松开手柄"
                : $"请按下并松开：{GetGamepadButtonLabel(target.Value)}";
        GamepadCheckButtonsButton.Content = target is null ? "确认并保存"
            : awaitingRestingPoint ? "确认扳机松开值" : "取消自动检测";
        GamepadManualCalibrationButton.Visibility = Visibility.Visible;
        GamepadManualCalibrationButton.IsEnabled = true;
        GamepadSkipButton.Content = gamepadCoordinator.HasGamepadCalibrationCandidate ? "重新操作" : "此键不支持";
        GamepadSkipButton.IsEnabled = target is not null;
        GamepadStatusText.Text = gamepadCoordinator.GamepadAutomaticCalibrationFeedback
            ?? (awaitingRestingPoint ? "请松开当前扳机并确认松开值。"
                : target is null ? "所有支持的按键已识别；确认后统一保存。"
                : $"请按下并松开{GetGamepadButtonLabel(target.Value)}，识别后会自动进入下一项。");
    }

    private void SetGamepadHighlights(IReadOnlyCollection<GamepadHeatmapPosition> highlights, string emptyMessage)
    {
        GamepadHighlightsItems.ItemsSource = highlights;
        GamepadHighlightsItems.Visibility = highlights.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        GamepadHighlightsEmptyText.Text = emptyMessage;
        GamepadHighlightsEmptyText.Visibility = highlights.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string GetGamepadButtonLabel(StorageGamepadButton button) => button switch
    {
        StorageGamepadButton.South => "A",
        StorageGamepadButton.East => "B",
        StorageGamepadButton.West => "X",
        StorageGamepadButton.North => "Y",
        StorageGamepadButton.DpadUp => "方向键上",
        StorageGamepadButton.DpadRight => "方向键右",
        StorageGamepadButton.DpadDown => "方向键下",
        StorageGamepadButton.DpadLeft => "方向键左",
        StorageGamepadButton.LeftTrigger => "LT",
        StorageGamepadButton.RightTrigger => "RT",
        StorageGamepadButton.LeftShoulder => "LB",
        StorageGamepadButton.RightShoulder => "RB",
        StorageGamepadButton.LeftThumb => "L3",
        StorageGamepadButton.RightThumb => "R3",
        StorageGamepadButton.View => "View",
        StorageGamepadButton.Menu => "Menu",
        _ => button.ToString()
    };

    private static GamepadPhysicalPosition ToPhysicalPosition(StorageGamepadButton button) => button switch
    {
        StorageGamepadButton.South => GamepadPhysicalPosition.South,
        StorageGamepadButton.East => GamepadPhysicalPosition.East,
        StorageGamepadButton.West => GamepadPhysicalPosition.West,
        StorageGamepadButton.North => GamepadPhysicalPosition.North,
        StorageGamepadButton.DpadUp => GamepadPhysicalPosition.DpadUp,
        StorageGamepadButton.DpadRight => GamepadPhysicalPosition.DpadRight,
        StorageGamepadButton.DpadDown => GamepadPhysicalPosition.DpadDown,
        StorageGamepadButton.DpadLeft => GamepadPhysicalPosition.DpadLeft,
        StorageGamepadButton.LeftTrigger => GamepadPhysicalPosition.LeftTrigger,
        StorageGamepadButton.RightTrigger => GamepadPhysicalPosition.RightTrigger,
        StorageGamepadButton.LeftShoulder => GamepadPhysicalPosition.LeftShoulder,
        StorageGamepadButton.RightShoulder => GamepadPhysicalPosition.RightShoulder,
        StorageGamepadButton.LeftThumb => GamepadPhysicalPosition.LeftStickPress,
        StorageGamepadButton.RightThumb => GamepadPhysicalPosition.RightStickPress,
        StorageGamepadButton.View => GamepadPhysicalPosition.View,
        _ => GamepadPhysicalPosition.Menu
    };

    private sealed record GamepadDeviceChoice(InputDeviceDescriptor Descriptor, string DisplayName, Guid? PersistentDeviceId)
    {
        public RuntimeDeviceId RuntimeId => Descriptor.RuntimeId;
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _dashboardRefreshTimer.Stop();
        _dashboardRefreshTimer.Tick -= DashboardRefreshTimer_Tick;
        _gamepadCalibrationRefreshTimer.Stop();
        _gamepadCalibrationRefreshTimer.Tick -= GamepadAutomaticCalibrationRefreshTimer_Tick;
        _gamepadCalibrationUiClosed = true;
        if (_gamepadCoordinator?.IsGamepadCalibrating == true)
            _gamepadCoordinator.CancelGamepadCalibration();
        _gamepadCalibrationRuntimeId = null;
        IsVisibleChanged -= MainWindow_IsVisibleChanged;
        _collection.Changed -= Collection_Changed;
    }

    private async void MainWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_dashboard is null || !IsLoaded) return;
        if (!IsVisible)
        {
            _dashboardRefreshTimer.Stop();
            return;
        }

        await RefreshDashboardAsync();
        _dashboardRefreshTimer.Start();
    }

    private async void DashboardRefreshTimer_Tick(object? sender, EventArgs e) =>
        await RefreshDashboardAsync();

    private async void GamepadAutomaticCalibrationRefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (_gamepadCoordinator is null || !_gamepadCoordinator.IsGamepadAutomaticCalibration)
        {
            _gamepadCalibrationRefreshTimer.Stop();
            if (_gamepadCoordinator?.IsGamepadCalibrating != true)
                _gamepadCalibrationRuntimeId = null;
            RefreshGamepadUi();
            return;
        }

        if (_gamepadAutomaticCalibrationTickInFlight) return;
        if (_gamepadCalibrationUiClosed || _capture is null)
        {
            _gamepadCalibrationRefreshTimer.Stop();
            _gamepadCoordinator.CancelGamepadCalibration();
            _gamepadCalibrationRuntimeId = null;
            RefreshGamepadUi();
            return;
        }
        if (_settings?.Load().GamepadConsentGranted != true)
        {
            _gamepadCalibrationRefreshTimer.Stop();
            _gamepadCoordinator.CancelGamepadCalibration();
            _gamepadCalibrationRuntimeId = null;
            try { await _capture.SetGamepadCaptureEnabledAsync(false); }
            catch { }
            RefreshGamepadUi();
            return;
        }

        _gamepadAutomaticCalibrationTickInFlight = true;
        try
        {
            await _capture.SetGamepadCaptureEnabledAsync(true);
            if (_gamepadCalibrationUiClosed || !_gamepadCalibrationRefreshTimer.IsEnabled
                || !_gamepadCoordinator.IsGamepadAutomaticCalibration)
                return;
            if (_settings.Load().GamepadConsentGranted != true)
            {
                _gamepadCalibrationRefreshTimer.Stop();
                _gamepadCoordinator.CancelGamepadCalibration();
                _gamepadCalibrationRuntimeId = null;
                await _capture.SetGamepadCaptureEnabledAsync(false);
                RefreshGamepadUi();
                return;
            }

            _gamepadCoordinator.UpdateGamepadAutomaticCalibration();
            if (_gamepadCoordinator.IsGamepadAutomaticCalibration)
                RefreshGamepadUi(automaticCalibrationProgressOnly: true);
            else
            {
                _gamepadCalibrationRefreshTimer.Stop();
                _gamepadCalibrationRuntimeId = null;
                RefreshGamepadUi();
            }
        }
        catch
        {
            _gamepadCalibrationRefreshTimer.Stop();
            _gamepadCoordinator.CancelGamepadCalibration();
            _gamepadCalibrationRuntimeId = null;
            GamepadStatusText.Text = "自动检测已停止；采集状态发生变化，请检查设备后重新检测。";
            RefreshGamepadUi();
        }
        finally
        {
            _gamepadAutomaticCalibrationTickInFlight = false;
        }
    }

    private async void RefreshDashboardButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshDashboardAsync();

    private async void QuerySelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!AreDeviceAliasEditorsReady()) return;

        UpdateDeviceAliasEditors();
        if (_updatingDeviceSelectors) return;

        if (ReferenceEquals(sender, KeyboardDeviceSelector))
            KeyboardAliasStatusText.Text = "改名只方便你辨认，已有记录不会变化。";
        else if (ReferenceEquals(sender, MouseDeviceSelector))
        {
            MouseAliasStatusText.Text = "改名只方便你辨认，已有记录不会变化。";
            MousePageStatusText.Text = MouseDeviceSelector.SelectedValue is null
                ? "选择鼠标后查看各按键次数"
                : "正在更新所选鼠标的按键统计…";
            SetMouseButtonCountLabels(null);
        }
        await RefreshDashboardAsync();
    }

    private bool AreDeviceAliasEditorsReady() =>
        KeyboardDeviceSelector is not null
        && KeyboardAliasInput is not null
        && KeyboardAliasStatusText is not null
        && SaveKeyboardAliasButton is not null
        && ClearKeyboardAliasButton is not null
        && MouseDeviceSelector is not null
        && MouseAliasInput is not null
        && MouseAliasStatusText is not null
        && SaveMouseAliasButton is not null
        && ClearMouseAliasButton is not null;

    private async void SaveKeyboardAliasButton_Click(object sender, RoutedEventArgs e) =>
        await ChangeDeviceAliasAsync(
            KeyboardDeviceSelector, KeyboardAliasInput, KeyboardAliasStatusText, clear: false);

    private async void ClearKeyboardAliasButton_Click(object sender, RoutedEventArgs e) =>
        await ChangeDeviceAliasAsync(
            KeyboardDeviceSelector, KeyboardAliasInput, KeyboardAliasStatusText, clear: true);

    private async void SaveMouseAliasButton_Click(object sender, RoutedEventArgs e) =>
        await ChangeDeviceAliasAsync(
            MouseDeviceSelector, MouseAliasInput, MouseAliasStatusText, clear: false);

    private async void ClearMouseAliasButton_Click(object sender, RoutedEventArgs e) =>
        await ChangeDeviceAliasAsync(
            MouseDeviceSelector, MouseAliasInput, MouseAliasStatusText, clear: true);

    private async Task ChangeDeviceAliasAsync(
        System.Windows.Controls.ComboBox selector,
        System.Windows.Controls.TextBox input,
        TextBlock status,
        bool clear)
    {
        if (_dashboard is null || selector.SelectedValue is not Guid deviceId)
        {
            status.Text = "请先选择一个设备。";
            return;
        }

        string? requestedAlias = clear ? null : input.Text;
        _deviceAliasChangeInProgress = true;
        UpdateDeviceAliasEditors();
        try
        {
            StorageDeviceInfo updated = await _dashboard.SetDeviceAliasAsync(
                deviceId, requestedAlias);
            input.Text = updated.Alias ?? string.Empty;
            status.Text = updated.Alias is null
                ? "名称已清除，原来的记录仍在。"
                : $"已保存为“{updated.Alias}”。";
            await RefreshDashboardAsync();
        }
        catch (Exception error)
        {
            status.Text = $"名称没有保存：{error.Message}";
        }
        finally
        {
            _deviceAliasChangeInProgress = false;
            UpdateDeviceAliasEditors();
        }
    }

    private async Task RefreshDashboardAsync()
    {
        if (_dashboard is null || !IsLoaded) return;
        if (_dashboardRefreshInProgress)
        {
            _dashboardRefreshPending = true;
            return;
        }

        _dashboardRefreshInProgress = true;
        try
        {
            do
            {
                _dashboardRefreshPending = false;

                _dashboard.AnchorDate = DateOnly.FromDateTime(DateTime.Today);
                _dashboard.TimeZoneId = TimeZoneInfo.Local.Id;
                _dashboard.CustomStartDate = ViewDateRangePicker.InclusiveStartDate;
                _dashboard.CustomEndDate = ViewDateRangePicker.InclusiveEndDate;
                _dashboard.SelectedKeyboardDeviceId = KeyboardDeviceSelector.SelectedValue is Guid keyboardId
                    ? keyboardId : null;
                _dashboard.SelectedMouseDeviceId = MouseDeviceSelector.SelectedValue is Guid mouseId
                    ? mouseId : null;
                await _dashboard.RefreshAsync();
                DeviceChoice[] keyboards = CreateDeviceChoices(_dashboard.Devices, StorageDeviceKind.Keyboard);
                DeviceChoice[] mice = CreateDeviceChoices(_dashboard.Devices, StorageDeviceKind.Mouse);
                DeviceChoice[] gamepads = CreateDeviceChoices(_dashboard.Devices, StorageDeviceKind.Gamepad);
                _updatingDeviceSelectors = true;
                try
                {
                    Guid? deletionKeyboardId = DeletionKeyboardDeviceSelector.SelectedValue is Guid value
                        ? value : _dashboard.SelectedKeyboardDeviceId;
                    Guid? deletionMouseId = DeletionMouseDeviceSelector.SelectedValue is Guid mouseValue
                        ? mouseValue : _dashboard.SelectedMouseDeviceId;
                    UpdateDeviceSelector(KeyboardDeviceSelector, keyboards, _dashboard.SelectedKeyboardDeviceId);
                    UpdateDeviceSelector(MouseDeviceSelector, mice, _dashboard.SelectedMouseDeviceId);
                    UpdateDeviceSelector(DeletionKeyboardDeviceSelector, keyboards, deletionKeyboardId, selectFirst: true);
                    UpdateDeviceSelector(DeletionMouseDeviceSelector, mice, deletionMouseId, selectFirst: true);
                    Guid? deletionGamepadId = DeletionGamepadDeviceSelector.SelectedValue is Guid gamepadValue ? gamepadValue : null;
                    UpdateDeviceSelector(DeletionGamepadDeviceSelector, gamepads, deletionGamepadId, selectFirst: true);
                    UpdateDeviceAliasEditors();
                }
                finally
                {
                    _updatingDeviceSelectors = false;
                }

                string keyboardSummary = $"按键 {_dashboard.TotalPressCount:N0} 次 · 按住 {_dashboard.TotalHoldDurationMicroseconds / 1_000_000d:N1} 秒";
                KeyboardSummary.Text = keyboardSummary;
                OverviewKeyboardSummary.Text = keyboardSummary;
                KeyboardHeatmapPresentation heatmap = KeyboardHeatmapPresentation.Create(
                    _keyboardLayout, _dashboard.Heatmap);
                HeatmapItems.ItemsSource = heatmap.PhysicalKeys;
                OtherHeatmapItems.ItemsSource = heatmap.OtherKeys;
                string mouseSummary = _dashboard.SelectedMouseDeviceId is null
                    ? "选择一只鼠标后查看按键次数"
                    : FormatMouseSummary(_dashboard.MouseButtons);
                MousePageStatusText.Text = _dashboard.SelectedMouseDeviceId is null
                    ? "选择鼠标后查看各按键次数"
                    : "所选时间范围内的按键统计";
                SetMouseButtonCountLabels(_dashboard.SelectedMouseDeviceId is null
                    ? null
                    : _dashboard.MouseButtons);
                OverviewMouseSummary.Text = mouseSummary;
                DashboardRefreshStatusText.Text = $"更新于 {DateTime.Now:HH:mm:ss}";
                RefreshGamepadUi();
            }
            while (_dashboardRefreshPending && IsLoaded);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            KeyboardSummary.Text = $"读取失败：{error.Message}";
            MousePageStatusText.Text = "暂时无法读取；请以页面顶部的记录状态为准。";
            OverviewMouseSummary.Text = "暂时无法读取；请以页面顶部的记录状态为准。";
            SetMouseButtonCountLabels(null);
            DashboardRefreshStatusText.Text = $"更新失败 {DateTime.Now:HH:mm:ss} · {error.Message}";
        }
        finally
        {
            _dashboardRefreshInProgress = false;
        }
    }

    private static void UpdateDeviceSelector(
        System.Windows.Controls.ComboBox selector,
        DeviceChoice[] choices,
        Guid? selectedDeviceId,
        bool selectFirst = false)
    {
        DeviceChoice[] current = selector.Items.Cast<DeviceChoice>().ToArray();
        if (!current.SequenceEqual(choices)) selector.ItemsSource = choices;
        Guid? target = selectedDeviceId is Guid requested
            && choices.Any(choice => choice.DeviceId == requested)
                ? requested
                : selectFirst ? choices.FirstOrDefault()?.DeviceId : null;
        selector.SelectedValue = target;
    }

    private void UpdateDeviceAliasEditors()
    {
        UpdateDeviceAliasEditor(
            KeyboardDeviceSelector, KeyboardAliasInput,
            SaveKeyboardAliasButton, ClearKeyboardAliasButton,
            StorageDeviceKind.Keyboard);
        UpdateDeviceAliasEditor(
            MouseDeviceSelector, MouseAliasInput,
            SaveMouseAliasButton, ClearMouseAliasButton,
            StorageDeviceKind.Mouse);
    }

    private void UpdateDeviceAliasEditor(
        System.Windows.Controls.ComboBox selector,
        System.Windows.Controls.TextBox input,
        System.Windows.Controls.Button save,
        System.Windows.Controls.Button clear,
        StorageDeviceKind kind)
    {
        Guid? selectedId = selector.SelectedValue is Guid value ? value : null;
        StorageDeviceInfo? selected = _dashboard?.Devices.FirstOrDefault(device =>
            device.DeviceId == selectedId && device.Kind == kind);
        if (!input.IsKeyboardFocusWithin)
        {
            input.Text = selected?.Alias ?? string.Empty;
        }

        bool enabled = selected is not null && !_deviceAliasChangeInProgress;
        input.IsEnabled = enabled;
        save.IsEnabled = enabled;
        clear.IsEnabled = enabled && selected!.Alias is not null;
    }

    internal static string FormatMouseSummary(MouseButtonCounts buttons)
    {
        ArgumentNullException.ThrowIfNull(buttons);
        return $"左键 {buttons.Left:N0} 次 · 右键 {buttons.Right:N0} 次 · 中键 {buttons.Middle:N0} 次 · "
            + $"侧键一 {buttons.X1:N0} 次 · 侧键二 {buttons.X2:N0} 次";
    }

    private void SetMouseButtonCountLabels(MouseButtonCounts? buttons)
    {
        MouseLeftCountText.Text = buttons is null ? "— 次" : $"{buttons.Left:N0} 次";
        MouseRightCountText.Text = buttons is null ? "— 次" : $"{buttons.Right:N0} 次";
        MouseMiddleCountText.Text = buttons is null ? "— 次" : $"{buttons.Middle:N0} 次";
        MouseX1CountText.Text = buttons is null ? "— 次" : $"{buttons.X1:N0} 次";
        MouseX2CountText.Text = buttons is null ? "— 次" : $"{buttons.X2:N0} 次";
    }

    internal static string FormatDeviceDisplayName(StorageDeviceInfo value, int ordinal = 1)
    {
        if (ordinal <= 0) throw new ArgumentOutOfRangeException(nameof(ordinal));
        return FormatDeviceName(value, ordinal);
    }

    private static string FormatDeviceName(StorageDeviceInfo value, int ordinal) =>
        value.Alias ?? $"{(value.Kind switch { StorageDeviceKind.Keyboard => "键盘", StorageDeviceKind.Mouse => "鼠标", _ => "手柄" })}{ordinal}";

    private static DeviceChoice[] CreateDeviceChoices(
        IReadOnlyList<StorageDeviceInfo> devices,
        StorageDeviceKind kind)
    {
        int ordinal = 0;
        return devices.Where(device => device.Kind == kind)
            .Select(device =>
            {
                ordinal++;
                string name = FormatDeviceName(device, ordinal);
                return new DeviceChoice(device.DeviceId, name, FormatDeviceDisplayName(device, ordinal));
            })
            .ToArray();
    }

    private sealed record DeviceChoice(Guid DeviceId, string Name, string DisplayName);

    private enum DeletionTargetKind
    {
        Keyboard,
        Mouse,
        Gamepad,
        All
    }
}

internal sealed class OfflineRestoreRequestedEventArgs(
    AppStorageDataToolsController.RestoreLease lease) : EventArgs
{
    public AppStorageDataToolsController.RestoreLease Lease { get; } = lease
        ?? throw new ArgumentNullException(nameof(lease));
}
