using System.Drawing;
using System.IO;

namespace InputTracker;

public sealed class TrayIconController : IDisposable
{
    internal const System.Windows.Threading.DispatcherPriority ExitDispatchPriority =
        System.Windows.Threading.DispatcherPriority.Normal;

    private readonly CollectionController _collection;
    private readonly Action _showWindow;
    private readonly Func<bool> _exitApplication;
    private readonly TrayExitSequence _exitSequence = new();
    private readonly Icon _icon;
    private readonly System.Windows.Forms.NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.ContextMenuStrip _contextMenu;
    private readonly System.Windows.Forms.ToolStripMenuItem _openItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _pauseItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _resumeItem;
    private bool _disposed;

    public TrayIconController(
        CollectionController collection,
        Action showWindow,
        Func<bool> exitApplication)
    {
        _collection = collection;
        _showWindow = showWindow;
        _exitApplication = exitApplication;
        _openItem = new System.Windows.Forms.ToolStripMenuItem(
            "打开 InputTracker",
            null,
            (_, _) => RequestShowWindow());
        _pauseItem = new System.Windows.Forms.ToolStripMenuItem(
            "暂停",
            null,
            async (_, _) => await ExecuteControlAsync("暂停采集", _collection.PauseAsync));
        _resumeItem = new System.Windows.Forms.ToolStripMenuItem(
            "恢复",
            null,
            async (_, _) => await ExecuteControlAsync("恢复采集", _collection.ResumeAsync));

        _contextMenu = new System.Windows.Forms.ContextMenuStrip();
        _contextMenu.Items.Add(_openItem);
        _contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _contextMenu.Items.Add(_pauseItem);
        _contextMenu.Items.Add(_resumeItem);
        _contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _contextMenu.Items.Add("退出", null, (_, _) => BeginExit());
        _contextMenu.Closed += ContextMenu_Closed;

        using Stream iconStream = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/InputTracker;component/Assets/InputTracker.ico")).Stream;
        _icon = new Icon(iconStream);

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            ContextMenuStrip = _contextMenu,
            Icon = _icon,
            Visible = true
        };
        _notifyIcon.DoubleClick += NotifyIcon_DoubleClick;
        _collection.Changed += OnCollectionChanged;
        Render(_collection.Current);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _exitSequence.Dispose();
        _collection.Changed -= OnCollectionChanged;
        _notifyIcon.DoubleClick -= NotifyIcon_DoubleClick;
        _contextMenu.Closed -= ContextMenu_Closed;
        _contextMenu.Close();
        _notifyIcon.Visible = false;
        _contextMenu.Dispose();
        _notifyIcon.Dispose();
        _icon.Dispose();
    }

    internal void PrepareForApplicationExit()
    {
        if (_disposed) return;
        _openItem.Enabled = false;
        _pauseItem.Enabled = false;
        _resumeItem.Enabled = false;
        _notifyIcon.Visible = false;
    }

    private void NotifyIcon_DoubleClick(object? sender, EventArgs e) => RequestShowWindow();

    private void RequestShowWindow()
    {
        if (_exitSequence.CanShowWindow) _showWindow();
    }

    private void BeginExit() => _exitSequence.BeginExit();

    private void ContextMenu_Closed(object? sender, System.Windows.Forms.ToolStripDropDownClosedEventArgs e) =>
        _exitSequence.ContinueAfterMenuClosed(
            hideTray: PrepareForApplicationExit,
            defer: action => System.Windows.Application.Current.Dispatcher.BeginInvoke(
                action,
                ExitDispatchPriority),
            tryExit: _exitApplication,
            restoreTray: () =>
            {
                if (_disposed) return;
                _notifyIcon.Visible = true;
                _openItem.Enabled = true;
                Render(_collection.Current);
            });

    private void OnCollectionChanged(object? sender, CollectionSnapshot snapshot) => Render(snapshot);

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

    private void Render(CollectionSnapshot snapshot)
    {
        _notifyIcon.Text = snapshot.Mode switch
        {
            CollectionMode.Paused => "InputTracker - 已暂停",
            CollectionMode.Collecting => "InputTracker - 记录中",
            CollectionMode.IntegrityFault => "InputTracker - 记录异常，已停止",
            CollectionMode.ControlUncertain => "InputTracker - 记录状态不确定",
            CollectionMode.StartFailed => "InputTracker - 无法开始记录",
            CollectionMode.RejectedProgramDirectory => "InputTracker - 文件夹不可写，未开始记录",
            _ => "InputTracker - 未开始记录"
        };
        _openItem.Enabled = _exitSequence.CanShowWindow;
        _pauseItem.Enabled = snapshot.CanPause && _exitSequence.CanShowWindow;
        _resumeItem.Enabled = snapshot.CanResume && _exitSequence.CanShowWindow;
    }
}

internal sealed class TrayExitSequence : IDisposable
{
    private bool _exitPending;
    private bool _continuationScheduled;
    private bool _disposed;

    public bool CanShowWindow => !_exitPending && !_disposed;

    public void BeginExit()
    {
        if (_exitPending || _disposed) return;
        _exitPending = true;
    }

    public void ContinueAfterMenuClosed(
        Action hideTray,
        Action<Action> defer,
        Func<bool> tryExit,
        Action restoreTray)
    {
        if (!_exitPending || _continuationScheduled || _disposed) return;
        _continuationScheduled = true;
        hideTray();
        defer(() =>
        {
            if (_disposed || tryExit()) return;
            _exitPending = false;
            _continuationScheduled = false;
            restoreTray();
        });
    }

    public void Dispose()
    {
        _disposed = true;
        _exitPending = true;
    }
}
