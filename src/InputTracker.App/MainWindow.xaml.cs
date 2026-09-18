using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using InputTracker.Storage;

namespace InputTracker;

public partial class MainWindow : Window
{
    private readonly CollectionController _collection;
    private readonly DirectoryProbeResult _directory;
    private readonly WindowClosePolicy _closePolicy = new();
    private readonly DashboardViewModel? _dashboard;
    private readonly StartupShortcutManager? _startup;
    private readonly AppStorageDataToolsController? _dataTools;
    private readonly KeyboardLayoutModel _keyboardLayout = KeyboardLayoutModel.CreateDefault();
    private readonly DispatcherTimer _dashboardRefreshTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _dashboardRefreshInProgress;
    private bool _dashboardRefreshPending;
    private bool _updatingDeviceSelectors;
    private bool _deviceAliasChangeInProgress;

    internal MainWindow(
        CollectionController collection,
        DirectoryProbeResult directory,
        DashboardViewModel? dashboard = null,
        StartupShortcutManager? startup = null,
        AppStorageDataToolsController? dataTools = null)
    {
        InitializeComponent();
        _collection = collection;
        _directory = directory;
        _dashboard = dashboard;
        _startup = startup;
        _dataTools = dataTools;
        _collection.Changed += Collection_Changed;
        _dashboardRefreshTimer.Tick += DashboardRefreshTimer_Tick;
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
        PeriodSelector.IsEnabled = false;
        DeletionKindSelector.IsEnabled = false;
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
    }

    private async void PauseButton_Click(object sender, RoutedEventArgs e) =>
        await ExecuteControlAsync("暂停采集", _collection.PauseAsync);

    private async void ResumeButton_Click(object sender, RoutedEventArgs e) =>
        await ExecuteControlAsync("恢复采集", _collection.ResumeAsync);
    private void HideButton_Click(object sender, RoutedEventArgs e) => MinimizeToTrayRequested?.Invoke(this, EventArgs.Empty);
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
            StartupStatusText.Text = "当前无法设置开机启动。";
            return;
        }

        try
        {
            StartupStatusText.Text = _startup.IsEnabled
                ? "InputTracker 会随 Windows 启动。关闭后会立即删除它创建的启动快捷方式。"
                : "InputTracker 不会随 Windows 启动，也不会写入注册表。";
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

    private async Task DeleteSelectedRangeAsync() =>
        await RunDataToolAsync(async () =>
        {
            StorageDeviceKind kind = DeletionKindSelector.SelectedItem is ComboBoxItem item
                && Enum.TryParse(item.Tag?.ToString(), out StorageDeviceKind selectedKind)
                    ? selectedKind
                    : throw new InvalidOperationException("请选择要删除键盘统计还是鼠标按键统计。");
            Guid? deviceId = kind == StorageDeviceKind.Keyboard
                ? _dashboard?.SelectedKeyboardDeviceId
                : _dashboard?.SelectedMouseDeviceId;
            if (_dataTools is null || _dashboard?.ResolvedPeriod is not { } period || deviceId is not Guid selected)
            {
                throw new InvalidOperationException(
                    kind == StorageDeviceKind.Keyboard
                        ? "请先在键盘热力图中选择一个键盘。"
                        : "请先在鼠标按键页选择一只鼠标。");
            }

            StorageDeletionDisplay preview = await _dataTools.PreviewDeletionAsync(
                selected, kind, period.UtcRange);
            if (!preview.CanExecute)
            {
                if (CanUpdateDataToolsUi()) DataToolsStatusText.Text = preview.Status;
                return;
            }

            string localRange = period.LocalEndDate == period.LocalStartDate.AddDays(1)
                ? period.LocalStartDate.ToString("yyyy-MM-dd")
                : $"{period.LocalStartDate:yyyy-MM-dd} 至 {period.LocalEndDate.AddDays(-1):yyyy-MM-dd}";
            string target = kind == StorageDeviceKind.Keyboard ? "键盘统计" : "鼠标按键统计";
            MessageBoxResult confirmation = System.Windows.MessageBox.Show(
                $"将永久删除所选设备在 {localRange} 的 {preview.PressCount:N0} 次{target}。\n\n"
                + "删除前会自动创建完整备份；其他设备和其他日期不受影响。是否继续？",
                $"最终确认 · 删除{target}",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (confirmation != MessageBoxResult.Yes)
            {
                if (CanUpdateDataToolsUi()) DataToolsStatusText.Text = "已取消删除，没有改动数据。";
                return;
            }

            StorageDeletionResult result = await _dataTools.ExecutePreparedDeletionAsync(
                preview, finalConfirmed: true);
            if (CanUpdateDataToolsUi())
                DataToolsStatusText.Text =
                    $"已删除 {(kind == StorageDeviceKind.Keyboard ? result.DeletedKeyboardPressCount : result.DeletedMouseButtonPressCount):N0} 次{target}。"
                    + $"删除前备份：{result.PreDeletionBackup.Path}";
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
        ValidateRestoreButton.IsEnabled = enabled;
    }

    private bool CanUpdateDataToolsUi() => IsLoaded && !(_dataTools?.IsClosing ?? false);

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshStartupState();
        await RefreshDashboardAsync();
        if (_dashboard is not null && IsVisible) _dashboardRefreshTimer.Start();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _dashboardRefreshTimer.Stop();
        _dashboardRefreshTimer.Tick -= DashboardRefreshTimer_Tick;
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
            MouseAliasStatusText.Text = "改名只方便你辨认，已有记录不会变化。";
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

                if (PeriodSelector.SelectedItem is ComboBoxItem period
                    && Enum.TryParse(period.Tag?.ToString(), out StoragePeriodKind value))
                {
                    _dashboard.Period = value;
                }

                _dashboard.AnchorDate = DateOnly.FromDateTime(DateTime.Today);
                _dashboard.TimeZoneId = TimeZoneInfo.Local.Id;
                _dashboard.SelectedKeyboardDeviceId = KeyboardDeviceSelector.SelectedValue is Guid keyboardId
                    ? keyboardId : null;
                _dashboard.SelectedMouseDeviceId = MouseDeviceSelector.SelectedValue is Guid mouseId
                    ? mouseId : null;
                await _dashboard.RefreshAsync();
                var keyboards = _dashboard.Devices.Where(device => device.Kind == StorageDeviceKind.Keyboard)
                    .Select(DeviceChoice.From).ToArray();
                var mice = _dashboard.Devices.Where(device => device.Kind == StorageDeviceKind.Mouse)
                    .Select(DeviceChoice.From).ToArray();
                _updatingDeviceSelectors = true;
                try
                {
                    UpdateDeviceSelector(KeyboardDeviceSelector, keyboards, _dashboard.SelectedKeyboardDeviceId);
                    UpdateDeviceSelector(MouseDeviceSelector, mice, _dashboard.SelectedMouseDeviceId);
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
                MouseSummary.Text = mouseSummary;
                OverviewMouseSummary.Text = mouseSummary;
                DashboardRefreshStatusText.Text = $"更新于 {DateTime.Now:HH:mm:ss} · 记录状态见页面顶部";
            }
            while (_dashboardRefreshPending && IsLoaded);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            KeyboardSummary.Text = $"读取失败：{error.Message}";
            MouseSummary.Text = "暂时无法读取；请以页面顶部的记录状态为准。";
            DashboardRefreshStatusText.Text = $"更新失败 {DateTime.Now:HH:mm:ss} · {error.Message}";
        }
        finally
        {
            _dashboardRefreshInProgress = false;
        }
    }

    private static void UpdateDeviceSelector(System.Windows.Controls.ComboBox selector, DeviceChoice[] choices, Guid? selectedDeviceId)
    {
        DeviceChoice[] current = selector.Items.Cast<DeviceChoice>().ToArray();
        if (!current.SequenceEqual(choices)) selector.ItemsSource = choices;
        selector.SelectedValue = selectedDeviceId;
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

    internal static string FormatDeviceDisplayName(StorageDeviceInfo value)
    {
        string confidence = value.IdentityConfidence switch
        {
            StorageDeviceIdentityConfidence.PersistentHardware => "重连后仍可识别",
            StorageDeviceIdentityConfidence.ReconnectableHardware => "已记住，同型号设备可能混淆",
            _ when value.IdentityAmbiguity.HasFlag(StorageDeviceIdentityAmbiguity.MultipleCandidates) =>
                "有相似设备，请按名称辨认",
            _ => "重连后可能显示为新设备"
        };
        return $"{value.Alias ?? value.DeviceId.ToString("N")[..8]} · {confidence}";
    }

    private sealed record DeviceChoice(Guid DeviceId, string DisplayName)
    {
        public static DeviceChoice From(StorageDeviceInfo value) =>
            new(value.DeviceId, FormatDeviceDisplayName(value));
    }
}

internal sealed class OfflineRestoreRequestedEventArgs(
    AppStorageDataToolsController.RestoreLease lease) : EventArgs
{
    public AppStorageDataToolsController.RestoreLease Lease { get; } = lease
        ?? throw new ArgumentNullException(nameof(lease));
}
