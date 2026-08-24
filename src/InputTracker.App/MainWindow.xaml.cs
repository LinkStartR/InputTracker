using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
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
    private bool _dashboardRefreshPendingIncludesMouse;
    private bool _updatingDeviceSelectors;

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
        PreviewMouseDeletionButton.IsEnabled = dataToolsAvailable;
        ValidateRestoreButton.IsEnabled = dataToolsAvailable;
        if (!dataToolsAvailable)
        {
            DataToolsStatusText.Text = "Storage 未安全启动，本地数据工具不可用。";
        }
        Loaded += MainWindow_Loaded;
    }

    public event EventHandler? MinimizeToTrayRequested;
    public event EventHandler? ExitRequested;

    internal void PrepareForExplicitExit() => _closePolicy.RequestExplicitExit();

    internal void PrepareForSessionEnding() => _closePolicy.RequestSessionEnding();

    internal void ShowDataToolExitBlocked()
    {
        DataToolsStatusText.Text = "数据操作或恢复选择正在进行；完成后再退出。";
        Show();
        WindowState = WindowState.Normal;
        Activate();
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
        if (_dashboard is not null) RenderMouse(_dashboard.MousePoints);
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
            StartupStatusText.Text = $"开机启动设置失败，未静默修改其他位置：{error.Message}";
        }
    }

    private void RefreshStartupState()
    {
        if (_startup is null)
        {
            StartupStatusText.Text = "开机启动控制不可用。";
            return;
        }

        try
        {
            StartupStatusText.Text = _startup.IsEnabled
                ? "已授权随 Windows 启动。关闭授权会立即删除 InputTracker 自身快捷方式。"
                : "未授权随 Windows 启动。InputTracker 不写注册表。";
        }
        catch (Exception error)
        {
            StartupStatusText.Text = $"无法安全确认开机启动状态：{error.Message}";
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
            if (_dataTools is null) throw new InvalidOperationException("Storage 数据工具不可用。");
            StorageBackupArtifact result = await _dataTools.CreateBackupAsync();
            if (CanUpdateDataToolsUi())
                DataToolsStatusText.Text = $"备份完成：{result.Path} · {result.FileCount:N0} 个文件";
        });

    private async void PreviewKeyboardDeletionButton_Click(object sender, RoutedEventArgs e) =>
        await PreviewDeletionAsync(StorageDeviceKind.Keyboard, _dashboard?.SelectedKeyboardDeviceId);

    private async void PreviewMouseDeletionButton_Click(object sender, RoutedEventArgs e) =>
        await PreviewDeletionAsync(StorageDeviceKind.Mouse, _dashboard?.SelectedMouseDeviceId);

    private async Task PreviewDeletionAsync(StorageDeviceKind kind, Guid? deviceId) =>
        await RunDataToolAsync(async () =>
        {
            if (_dataTools is null || _dashboard?.ResolvedPeriod is not { } period || deviceId is not Guid selected)
            {
                throw new InvalidOperationException(
                    kind == StorageDeviceKind.Keyboard
                        ? "请先明确选择一个键盘并完成当前周期读取。"
                        : "请先明确选择一个鼠标并完成当前周期读取。");
            }

            StorageDeletionDisplay preview = await _dataTools.PreviewDeletionAsync(
                selected, kind, period.UtcRange);
            string label = preview.DeviceKind == StorageDeviceKind.Keyboard ? "键盘" : "鼠标";
            if (CanUpdateDataToolsUi())
                DataToolsStatusText.Text =
                    $"删除预览目标：{label} {preview.DeviceId.ToString("N")[..8]}，UTC [{preview.UtcRange.StartUtcMicroseconds}, {preview.UtcRange.EndUtcMicroseconds})；"
                    + $"鼠标 {preview.MouseRecordCount:N0} 条，键盘聚合 {preview.KeyboardAggregateCount:N0} 行。{preview.Status}";
        });

    private void ValidateRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (_dataTools is null)
        {
            DataToolsStatusText.Text = "Storage 数据工具不可用。";
            return;
        }

        SetDataToolsEnabled(false);
        try
        {
            using AppStorageDataToolsController.RestoreInteraction interaction = _dataTools.TryBeginInteraction();
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择 InputTracker 备份（仅检查，不执行恢复）",
                Filter = "InputTracker backup (*.itbackup)|*.itbackup",
                InitialDirectory = System.IO.Path.Combine(_directory.DirectoryPath, "backups"),
                CheckFileExists = true,
                Multiselect = false
            };
            if (dialog.ShowDialog(this) != true) return;
            MessageBoxResult confirmation = System.Windows.MessageBox.Show(
                "只检查此备份的路径和文件边界。当前版本不会执行恢复，也不会更改现有数据。是否继续二次确认？",
                "恢复检查 · 不执行",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            RestoreSelection result = interaction.Validate(dialog.FileName, confirmation == MessageBoxResult.Yes);
            if (IsLoaded && !_dataTools.IsClosing) DataToolsStatusText.Text = result.Status;
        }
        catch (Exception error)
        {
            if (IsLoaded && !_dataTools.IsClosing)
                DataToolsStatusText.Text = $"恢复检查失败，未更改数据：{error.Message}";
        }
        finally
        {
            if (IsLoaded && !_dataTools.IsClosing) SetDataToolsEnabled(true);
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
        PreviewMouseDeletionButton.IsEnabled = enabled;
        ValidateRestoreButton.IsEnabled = enabled;
    }

    private bool CanUpdateDataToolsUi() => IsLoaded && !(_dataTools?.IsClosing ?? false);

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshStartupState();
        await RefreshDashboardAsync(includeMouse: true);
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

        await RefreshDashboardAsync(includeMouse: false);
        _dashboardRefreshTimer.Start();
    }

    private async void DashboardRefreshTimer_Tick(object? sender, EventArgs e) =>
        await RefreshDashboardAsync(includeMouse: false);

    private async void RefreshDashboardButton_Click(object sender, RoutedEventArgs e) =>
        await RefreshDashboardAsync(includeMouse: true);

    private async void QuerySelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingDeviceSelectors) await RefreshDashboardAsync(includeMouse: true);
    }

    private async Task RefreshDashboardAsync(bool includeMouse)
    {
        if (_dashboard is null || !IsLoaded) return;
        if (_dashboardRefreshInProgress)
        {
            _dashboardRefreshPending = true;
            _dashboardRefreshPendingIncludesMouse |= includeMouse;
            return;
        }

        _dashboardRefreshInProgress = true;
        try
        {
            bool refreshMouse = includeMouse;
            do
            {
                _dashboardRefreshPending = false;
                refreshMouse |= _dashboardRefreshPendingIncludesMouse;
                _dashboardRefreshPendingIncludesMouse = false;

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
                await _dashboard.RefreshAsync(refreshMouse);
                var keyboards = _dashboard.Devices.Where(device => device.Kind == StorageDeviceKind.Keyboard)
                    .Select(DeviceChoice.From).ToArray();
                var mice = _dashboard.Devices.Where(device => device.Kind == StorageDeviceKind.Mouse)
                    .Select(DeviceChoice.From).ToArray();
                _updatingDeviceSelectors = true;
                try
                {
                    UpdateDeviceSelector(KeyboardDeviceSelector, keyboards, _dashboard.SelectedKeyboardDeviceId);
                    UpdateDeviceSelector(MouseDeviceSelector, mice, _dashboard.SelectedMouseDeviceId);
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
                    ? "选择鼠标后读取完整原始轨迹"
                    : $"已完整读取 {_dashboard.TotalMousePointCount:N0} 个原始点 · 绘制 {_dashboard.MousePoints.Count:N0} 个有界显示点";
                MouseSummary.Text = mouseSummary;
                OverviewMouseSummary.Text = mouseSummary;
                if (refreshMouse) RenderMouse(_dashboard.MousePoints);
                DashboardRefreshStatusText.Text = $"已刷新 {DateTime.Now:HH:mm:ss} · 采集状态以页面顶部为准";
                refreshMouse = false;
            }
            while (_dashboardRefreshPending && IsLoaded);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            KeyboardSummary.Text = $"读取失败：{error.Message}";
            MouseSummary.Text = "读取失败；采集与持久化状态请以上方状态为准。";
            DashboardRefreshStatusText.Text = $"刷新失败 {DateTime.Now:HH:mm:ss} · {error.Message}";
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

    private void NavigationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, NavigationList)
            || NavigationList.SelectedItem is not TabItem selected
            || !string.Equals(selected.Tag?.ToString(), "MousePlayback", StringComparison.Ordinal)
            || _dashboard is null)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            () => RenderMouse(_dashboard.MousePoints),
            DispatcherPriority.Loaded);
    }

    private void RenderMouse(IReadOnlyList<MouseDisplayPoint> points)
    {
        MouseCanvas.Children.Clear();
        if (points.Count == 0) return;
        foreach (IReadOnlyList<System.Windows.Point> segment in ProjectMouseSegments(
            points, MouseCanvas.ActualWidth, MouseCanvas.Height))
        {
            var line = new Polyline
            {
                Stroke = (System.Windows.Media.Brush)FindResource("AccentBrush"),
                StrokeThickness = 1.5
            };
            foreach (System.Windows.Point point in segment)
            {
                line.Points.Add(point);
            }

            MouseCanvas.Children.Add(line);
        }
    }

    internal static IReadOnlyList<IReadOnlyList<System.Windows.Point>> ProjectMouseSegments(
        IReadOnlyList<MouseDisplayPoint> points,
        double width,
        double height)
    {
        var result = new List<IReadOnlyList<System.Windows.Point>>();
        List<System.Windows.Point>? segment = null;
        double x = width / 2;
        double y = height / 2;
        MouseDisplayPoint? previous = null;
        foreach (MouseDisplayPoint point in points)
        {
            bool startsNewSegment = segment is null
                || point.StartsNewSegment
                || previous?.MotionKind != point.MotionKind
                || previous?.UsesVirtualDesktop != point.UsesVirtualDesktop;
            if (startsNewSegment)
            {
                segment = [];
                result.Add(segment);
                x = width / 2;
                y = height / 2;
            }

            if (point.MotionKind == 1)
            {
                x = Math.Clamp(point.DeltaX / 65_535d * width, 0, Math.Max(0, width));
                y = Math.Clamp(point.DeltaY / 65_535d * height, 0, Math.Max(0, height));
            }
            else
            {
                x = Math.Clamp(x + point.DeltaX, 0, Math.Max(0, width));
                y = Math.Clamp(y + point.DeltaY, 0, Math.Max(0, height));
            }

            segment!.Add(new System.Windows.Point(x, y));
            previous = point;
        }

        return result;
    }

    private sealed record DeviceChoice(Guid DeviceId, string DisplayName)
    {
        public static DeviceChoice From(StorageDeviceInfo value)
        {
            string confidence = value.IdentityConfidence == StorageDeviceIdentityConfidence.PersistentHardware
                ? "可稳定识别"
                : "临时身份，重插可能变化";
            return new DeviceChoice(value.DeviceId, $"{value.Alias ?? value.DeviceId.ToString("N")[..8]} · {confidence}");
        }
    }
}
