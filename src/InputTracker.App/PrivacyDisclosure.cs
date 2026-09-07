namespace InputTracker;

internal static class PrivacyDisclosure
{
    private const string StorageBoundary =
        "键盘只按设备、每天统计实体按键的次数和按住时长，不保存输入内容、先后顺序、窗口或程序名称。鼠标只统计各按键的次数，不保存移动位置或路线。旧版本已经保存的轨迹文件会原样保留，程序不会自动删除。";

    public static string For(CollectionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        string liveState = snapshot.Mode switch
        {
            CollectionMode.Collecting => "顶部显示“记录中”时，InputTracker 会在后台接收键盘和鼠标活动。",
            CollectionMode.StartFailed => "记录启动失败，当前没有接收键盘或鼠标活动。",
            CollectionMode.RejectedProgramDirectory => "当前程序文件夹不能写入，因此没有开始记录。",
            CollectionMode.StorageStartFailed => "本地数据无法打开，因此没有开始记录。",
            CollectionMode.Restoring => "正在停止记录，请等待程序完成收尾。",
            CollectionMode.RestoreControlUncertainAwaitExit => "恢复准备失败，无法确认后台接收是否完全关闭；InputTracker 正在退出。",
            CollectionMode.Paused or CollectionMode.IntegrityFault
                or CollectionMode.RestoreSucceededAwaitExit
                or CollectionMode.RestoreFailedAwaitExit
                or CollectionMode.RestoreRecoveryRequired => "记录已停止，当前没有接收键盘或鼠标活动。",
            CollectionMode.ControlUncertain => "记录状态不确定，请以页面顶部说明为准；退出 InputTracker 可以结束后台接收。",
            CollectionMode.Starting => "正在开始记录；只有顶部显示“记录中”时才会在后台接收活动。",
            _ => "当前没有接收键盘或鼠标活动。"
        };

        return $"{liveState}{StorageBoundary}";
    }
}
