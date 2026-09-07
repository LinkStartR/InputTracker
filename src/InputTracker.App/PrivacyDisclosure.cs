namespace InputTracker;

internal static class PrivacyDisclosure
{
    private const string StorageBoundary =
        "键盘只按设备、每天统计实体按键的次数和按住时长，不保存输入内容、先后顺序、窗口或程序名称。鼠标移动路线按设备保存在当前程序文件夹；当前版本还不能按范围删除。";

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
