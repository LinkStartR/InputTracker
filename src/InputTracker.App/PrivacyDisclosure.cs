namespace InputTracker;

internal static class PrivacyDisclosure
{
    private const string FutureStorageBoundary =
        "本阶段尚未接通 Storage，事件不会持久化。后续接通 Storage 后，键盘只按设备、物理键和日期累计次数与时长，不保存文字、事件顺序、窗口或进程；鼠标原始轨迹才会按设备持久化，直到用户主动按范围删除。";

    public static string For(CollectionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        string liveState = snapshot.Mode switch
        {
            CollectionMode.Collecting => "顶部状态为“采集中”：Win32 Raw Input 正在后台接收物理键盘和鼠标事件。",
            CollectionMode.StartFailed => "采集启动失败，当前未接收物理键盘或鼠标事件。",
            CollectionMode.RejectedProgramDirectory => "程序目录不可写，采集已拒绝，当前未接收物理键盘或鼠标事件。",
            CollectionMode.Paused or CollectionMode.IntegrityFault => "采集已停止，当前未接收物理键盘或鼠标事件。",
            CollectionMode.ControlUncertain => "采集控制状态不确定；是否仍在接收输入以顶部状态说明为准，请退出 InputTracker 以结束采集。",
            CollectionMode.Starting => "正在启动采集；只有顶部状态显示“采集中”时，Raw Input 才在后台接收输入。",
            _ => "当前未接收物理键盘或鼠标事件。"
        };

        return $"{liveState}{FutureStorageBoundary}";
    }
}
