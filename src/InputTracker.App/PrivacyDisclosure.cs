namespace InputTracker;

internal static class PrivacyDisclosure
{
    private const string StorageBoundary =
        "键盘只按设备、物理键和日期累计次数与时长，不保存文字、事件顺序、窗口或进程；鼠标原始轨迹按设备完整持久化。当前版本尚未提供范围删除，数据保留在程序目录。";

    public static string For(CollectionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        string liveState = snapshot.Mode switch
        {
            CollectionMode.Collecting => "顶部状态为“采集中”：Win32 Raw Input 正在后台接收物理键盘和鼠标事件。",
            CollectionMode.StartFailed => "采集启动失败，当前未接收物理键盘或鼠标事件。",
            CollectionMode.RejectedProgramDirectory => "程序目录不可写，采集已拒绝，当前未接收物理键盘或鼠标事件。",
            CollectionMode.StorageStartFailed => "Storage 启动失败，Raw Input 未启动，当前未接收物理键盘或鼠标事件。",
            CollectionMode.Restoring => "正在停止采集；尚未确认 Raw Input 已完全关闭，请等待当前进程退出。",
            CollectionMode.RestoreControlUncertainAwaitExit => "恢复停止边界失败，Raw Input 是否完全关闭仍不确定；InputTracker 正在退出。",
            CollectionMode.Paused or CollectionMode.IntegrityFault
                or CollectionMode.RestoreSucceededAwaitExit
                or CollectionMode.RestoreFailedAwaitExit
                or CollectionMode.RestoreRecoveryRequired => "采集已停止，当前未接收物理键盘或鼠标事件。",
            CollectionMode.ControlUncertain => "采集控制状态不确定；是否仍在接收输入以顶部状态说明为准，请退出 InputTracker 以结束采集。",
            CollectionMode.Starting => "正在启动采集；只有顶部状态显示“采集中”时，Raw Input 才在后台接收输入。",
            _ => "当前未接收物理键盘或鼠标事件。"
        };

        return $"{liveState}{StorageBoundary}";
    }
}
