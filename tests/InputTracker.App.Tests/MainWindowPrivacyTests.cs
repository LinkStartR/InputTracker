namespace InputTracker.App.Tests;

public sealed class MainWindowPrivacyTests
{
    [Theory]
    [InlineData(CollectionMode.Collecting, "正在后台接收物理键盘和鼠标事件")]
    [InlineData(CollectionMode.StartFailed, "采集启动失败，当前未接收物理键盘或鼠标事件")]
    [InlineData(CollectionMode.RejectedProgramDirectory, "采集已拒绝，当前未接收物理键盘或鼠标事件")]
    public void PrivacyBoundaryMatchesVisibleCollectionState(CollectionMode mode, string expectedState)
    {
        var snapshot = new CollectionSnapshot(mode, "status", "explanation", false, false);

        string disclosure = PrivacyDisclosure.For(snapshot);

        Assert.Contains(expectedState, disclosure);
        Assert.DoesNotContain("Storage 尚未接通", disclosure);
        Assert.Contains("设备、物理键和日期", disclosure);
        Assert.Contains("不保存文字、事件顺序、窗口或进程", disclosure);
        Assert.Contains("鼠标原始轨迹按设备完整持久化", disclosure);
        Assert.Contains("当前版本尚未提供范围删除", disclosure);
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
        Assert.Contains("数据、配置、日志和备份均位于", xaml);
        Assert.Contains("删除该绿色目录即可", xaml);
        Assert.Contains("_startup.Enable()", code);
        Assert.Contains("_startup.Disable()", code);

        string consent = File.ReadAllText(Path.Combine(repositoryRoot, "src", "InputTracker.App", "PortableSettings.cs"));
        Assert.Contains("当前测试版尚未提供范围删除", consent);
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
