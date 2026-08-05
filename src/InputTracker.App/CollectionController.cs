namespace InputTracker;

public enum CollectionMode
{
    NotCollecting,
    Paused,
    RejectedProgramDirectory
}

public sealed record CollectionSnapshot(
    CollectionMode Mode,
    string Label,
    string Explanation,
    bool CanPause,
    bool CanResume);

public sealed class CollectionController
{
    public CollectionController(DirectoryProbeResult directory)
    {
        Current = directory.IsWritable
            ? NotCollecting()
            : Rejected(directory.Error ?? "程序目录不可写。");
    }

    public event EventHandler<CollectionSnapshot>? Changed;

    public CollectionSnapshot Current { get; private set; }

    public void Pause()
    {
        if (Current.Mode == CollectionMode.RejectedProgramDirectory)
        {
            return;
        }

        Set(new CollectionSnapshot(
            CollectionMode.Paused,
            "已暂停",
            "用户已暂停采集控制链。当前版本尚未启用输入采集。",
            CanPause: false,
            CanResume: true));
    }

    public void Resume()
    {
        if (Current.Mode == CollectionMode.RejectedProgramDirectory)
        {
            return;
        }

        Set(NotCollecting());
    }

    private void Set(CollectionSnapshot snapshot)
    {
        Current = snapshot;
        Changed?.Invoke(this, snapshot);
    }

    private static CollectionSnapshot NotCollecting() => new(
        CollectionMode.NotCollecting,
        "未采集",
        "安全外壳已就绪。Raw Input 采集将在后续阶段启用。",
        CanPause: true,
        CanResume: false);

    private static CollectionSnapshot Rejected(string error) => new(
        CollectionMode.RejectedProgramDirectory,
        "已暂停 · 采集已拒绝",
        $"程序目录不可写。InputTracker 不会改用 AppData 或其他目录。{Environment.NewLine}{error}",
        CanPause: false,
        CanResume: false);
}
