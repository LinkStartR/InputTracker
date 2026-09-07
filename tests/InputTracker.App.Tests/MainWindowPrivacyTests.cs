namespace InputTracker.App.Tests;

public sealed class MainWindowPrivacyTests
{
    [Theory]
    [InlineData(CollectionMode.Collecting, "顶部显示“记录中”")]
    [InlineData(CollectionMode.StartFailed, "记录启动失败，当前没有接收键盘或鼠标活动")]
    [InlineData(CollectionMode.RejectedProgramDirectory, "当前程序文件夹不能写入，因此没有开始记录")]
    [InlineData(CollectionMode.Restoring, "正在停止记录")]
    [InlineData(CollectionMode.RestoreControlUncertainAwaitExit, "无法确认后台接收是否完全关闭")]
    public void PrivacyBoundaryMatchesVisibleCollectionState(CollectionMode mode, string expectedState)
    {
        var snapshot = new CollectionSnapshot(mode, "status", "explanation", false, false);

        string disclosure = PrivacyDisclosure.For(snapshot);

        Assert.Contains(expectedState, disclosure);
        Assert.DoesNotContain("Storage 尚未接通", disclosure);
        Assert.Contains("按设备、每天统计实体按键", disclosure);
        Assert.Contains("不保存输入内容、先后顺序、窗口或程序名称", disclosure);
        Assert.Contains("鼠标移动路线按设备保存在当前程序文件夹", disclosure);
        Assert.Contains("当前版本还不能按范围删除", disclosure);
    }

    [Fact]
    public void PrivacyCardIsRenderedFromTheCurrentSnapshot()
    {
        string repositoryRoot = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "InputTracker.App",
            "MainWindow.xaml"));
        string codeBehind = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src",
            "InputTracker.App",
            "MainWindow.xaml.cs"));

        Assert.Contains("x:Name=\"PrivacyExplanation\"", xaml);
        Assert.DoesNotContain("当前版本会在后台接收", xaml);
        Assert.Contains("PrivacyExplanation.Text = PrivacyDisclosure.For(snapshot)", codeBehind);
    }

    [Fact]
    public void PortableControlsDiscloseStartupAndUninstallBoundaries()
    {
        string repositoryRoot = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(repositoryRoot, "src", "InputTracker.App", "MainWindow.xaml"));
        string code = File.ReadAllText(Path.Combine(repositoryRoot, "src", "InputTracker.App", "MainWindow.xaml.cs"));

        Assert.Contains("启用开机启动", xaml);
        Assert.Contains("关闭开机启动", xaml);
        Assert.Contains("所有数据都在上方显示的程序文件夹里", xaml);
        Assert.Contains("再删除程序即可", xaml);
        Assert.Contains("_startup.Enable()", code);
        Assert.Contains("_startup.Disable()", code);

        string consent = File.ReadAllText(Path.Combine(repositoryRoot, "src", "InputTracker.App", "PortableSettings.cs"));
        Assert.Contains("当前测试版还不能按范围删除", consent);
    }

    [Fact]
    public void RestoreUiRequiresFinalDestructiveConfirmationAndRaisesRequestOnlyAfterLeaseTransfer()
    {
        string repositoryRoot = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(repositoryRoot, "src", "InputTracker.App", "MainWindow.xaml"));
        string code = File.ReadAllText(Path.Combine(repositoryRoot, "src", "InputTracker.App", "MainWindow.xaml.cs"));

        Assert.Contains("恢复本地备份", xaml);
        Assert.Contains("当前采集会停止", code);
        Assert.Contains("现有数据将被替换", code);
        Assert.Contains("自动生成恢复前备份", code);
        Assert.Contains("成功或失败后 InputTracker 都会退出", code);
        Assert.Contains("TransferToRestore", code);
        Assert.Contains("OfflineRestoreRequested?.Invoke", code);
        Assert.Contains("_dashboardRefreshTimer.Stop()", code);
        Assert.Contains("_mousePlaybackFrameTimer.Stop()", code);
        Assert.Contains("SetDataToolsEnabled(false)", code);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "InputTracker.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate InputTracker.sln from the test output directory.");
    }
}
