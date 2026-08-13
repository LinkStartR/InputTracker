using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using InputTracker.Storage;

namespace InputTracker;

public partial class MainWindow : Window
{
    private readonly CollectionController _collection;
    private readonly DirectoryProbeResult _directory;
    private readonly WindowClosePolicy _closePolicy = new();
    private readonly DashboardViewModel? _dashboard;
    private readonly StartupShortcutManager? _startup;

    internal MainWindow(
        CollectionController collection,
        DirectoryProbeResult directory,
        DashboardViewModel? dashboard = null,
        StartupShortcutManager? startup = null)
    {
        InitializeComponent();
        _collection = collection;
        _directory = directory;
        _dashboard = dashboard;
        _startup = startup;
        _collection.Changed += Collection_Changed;
        Closed += (_, _) => _collection.Changed -= Collection_Changed;
        Render(_collection.Current);
        Loaded += async (_, _) => await RefreshDashboardAsync();
        Loaded += (_, _) => RefreshStartupState();
    }

    public event EventHandler? MinimizeToTrayRequested;
    public event EventHandler? ExitRequested;

    internal void PrepareForExplicitExit() => _closePolicy.RequestExplicitExit();

    internal void PrepareForSessionEnding() => _closePolicy.RequestSessionEnding();

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
        PrepareForExplicitExit();
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

    private async void QuerySelection_Changed(object sender, SelectionChangedEventArgs e) =>
        await RefreshDashboardAsync();

    private async Task RefreshDashboardAsync()
    {
        if (_dashboard is null || !IsLoaded) return;
        try
        {
            if (PeriodSelector.SelectedItem is ComboBoxItem period
                && Enum.TryParse(period.Tag?.ToString(), out StoragePeriodKind value))
            {
                _dashboard.Period = value;
            }

            _dashboard.AnchorDate = DateOnly.FromDateTime(DateTime.Today);
            _dashboard.TimeZoneId = TimeZoneInfo.Local.Id;
            _dashboard.SelectedKeyboardDeviceId = KeyboardDeviceSelector.SelectedValue as Guid?;
            _dashboard.SelectedMouseDeviceId = MouseDeviceSelector.SelectedValue as Guid?;
            await _dashboard.RefreshAsync();
            var keyboards = _dashboard.Devices.Where(device => device.Kind == StorageDeviceKind.Keyboard)
                .Select(DeviceChoice.From).ToArray();
            var mice = _dashboard.Devices.Where(device => device.Kind == StorageDeviceKind.Mouse)
                .Select(DeviceChoice.From).ToArray();
            if (KeyboardDeviceSelector.Items.Count == 0) KeyboardDeviceSelector.ItemsSource = keyboards;
            if (MouseDeviceSelector.Items.Count == 0) MouseDeviceSelector.ItemsSource = mice;
            KeyboardSummary.Text = $"按键 {_dashboard.TotalPressCount:N0} 次 · 按住 {_dashboard.TotalHoldDurationMicroseconds / 1_000_000d:N1} 秒";
            HeatmapItems.ItemsSource = _dashboard.Heatmap.Select(cell =>
                $"{(cell.IsE0 ? "E0-" : cell.IsE1 ? "E1-" : string.Empty)}{cell.MakeCode:X2}  {cell.PressCount:N0}");
            MouseSummary.Text = _dashboard.SelectedMouseDeviceId is null
                ? "选择鼠标后读取完整原始轨迹"
                : $"已完整读取 {_dashboard.TotalMousePointCount:N0} 个原始点 · 绘制 {_dashboard.MousePoints.Count:N0} 个有界显示点";
            RenderMouse(_dashboard.MousePoints);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            KeyboardSummary.Text = $"读取失败：{error.Message}";
            MouseSummary.Text = "读取失败；采集与持久化状态请以上方状态为准。";
        }
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
