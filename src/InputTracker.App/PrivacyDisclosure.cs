namespace InputTracker;

internal static class PrivacyDisclosure
{
    private const string StorageBoundary =
        "键盘会按设备和日期，记录每个按键的次数和按住时长，不保存你输入的文字、按键顺序、窗口或程序名称。鼠标会按设备记录各个按键的次数，不保存移动位置或路线。";

    public static string For(CollectionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        string liveState = snapshot.Mode switch
        {
            CollectionMode.Collecting => "顶部显示“记录中”时，InputTracker 会在后台统计键盘和鼠标的使用次数。",
            CollectionMode.StartFailed => "记录启动失败，目前没有记录键盘或鼠标操作。",
            CollectionMode.RejectedProgramDirectory => "当前程序文件夹不能写入，因此没有开始记录。",
            CollectionMode.StorageStartFailed => "本地数据无法打开，因此没有开始记录。",
            CollectionMode.Restoring => "正在停止记录，请稍候。",
            CollectionMode.RestoreControlUncertainAwaitExit => "准备恢复时遇到问题，无法确认记录是否完全停止；InputTracker 正在退出。",
            CollectionMode.Paused or CollectionMode.IntegrityFault
                or CollectionMode.RestoreSucceededAwaitExit
                or CollectionMode.RestoreFailedAwaitExit
                or CollectionMode.RestoreRecoveryRequired => "记录已停止，目前没有记录键盘或鼠标操作。",
            CollectionMode.ControlUncertain => "记录状态还不确定，请查看页面顶部的说明。退出 InputTracker 可以停止后台记录。",
            CollectionMode.Starting => "正在准备记录。只有顶部显示“记录中”时，键盘和鼠标操作才会计入统计。",
            _ => "目前没有记录键盘或鼠标操作。"
        };

        return $"{liveState}{StorageBoundary}";
    }
}
