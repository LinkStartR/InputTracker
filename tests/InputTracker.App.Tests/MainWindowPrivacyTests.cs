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
        Assert.Contains("本阶段尚未接通 Storage，事件不会持久化", disclosure);
        Assert.Contains("设备、物理键和日期", disclosure);
        Assert.Contains("不保存文字、事件顺序、窗口或进程", disclosure);
        Assert.Contains("鼠标原始轨迹才会按设备持久化", disclosure);
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
