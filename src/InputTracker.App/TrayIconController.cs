using System.Drawing;

namespace InputTracker;

public sealed class TrayIconController : IDisposable
{
    private readonly CollectionController _collection;
    private readonly System.Windows.Forms.NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.ToolStripMenuItem _pauseItem;
    private readonly System.Windows.Forms.ToolStripMenuItem _resumeItem;

    public TrayIconController(
        CollectionController collection,
        Action showWindow,
        Action exitApplication)
    {
        _collection = collection;
        _pauseItem = new System.Windows.Forms.ToolStripMenuItem("暂停", null, async (_, _) => await _collection.PauseAsync());
        _resumeItem = new System.Windows.Forms.ToolStripMenuItem("恢复", null, async (_, _) => await _collection.ResumeAsync());

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("打开 InputTracker", null, (_, _) => showWindow());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(_resumeItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => exitApplication());

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            ContextMenuStrip = menu,
            Icon = SystemIcons.Application,
            Visible = true
        };
        _notifyIcon.DoubleClick += (_, _) => showWindow();
        _collection.Changed += OnCollectionChanged;
        Render(_collection.Current);
    }

    public void Dispose()
    {
        _collection.Changed -= OnCollectionChanged;
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
    }

    private void OnCollectionChanged(object? sender, CollectionSnapshot snapshot) => Render(snapshot);

    private void Render(CollectionSnapshot snapshot)
    {
        _notifyIcon.Text = snapshot.Mode switch
        {
            CollectionMode.Paused => "InputTracker - 已暂停",
            CollectionMode.Collecting => "InputTracker - 采集中",
            CollectionMode.IntegrityFault => "InputTracker - 完整性故障，采集已停止",
            CollectionMode.StartFailed => "InputTracker - 采集启动失败",
            CollectionMode.RejectedProgramDirectory => "InputTracker - 目录不可写，采集已拒绝",
            _ => "InputTracker - 未采集"
        };
        _pauseItem.Enabled = snapshot.CanPause;
        _resumeItem.Enabled = snapshot.CanResume;
    }
}
