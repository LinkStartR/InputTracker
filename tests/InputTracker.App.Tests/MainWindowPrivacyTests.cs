namespace InputTracker.App.Tests;

public sealed class MainWindowPrivacyTests
{
    [Theory]
    [InlineData(CollectionMode.Collecting, "顶部显示“记录中”时，InputTracker 会在后台统计")]
    [InlineData(CollectionMode.StartFailed, "记录启动失败，目前没有记录键盘或鼠标操作")]
    [InlineData(CollectionMode.RejectedProgramDirectory, "当前程序文件夹不能写入，因此没有开始记录")]
    [InlineData(CollectionMode.Restoring, "正在停止记录")]
    [InlineData(CollectionMode.RestoreControlUncertainAwaitExit, "无法确认记录是否完全停止")]
    public void PrivacyBoundaryMatchesVisibleCollectionState(CollectionMode mode, string expectedState)
    {
        var snapshot = new CollectionSnapshot(mode, "status", "explanation", false, false);

        string disclosure = PrivacyDisclosure.For(snapshot);

        Assert.Contains(expectedState, disclosure);
        Assert.DoesNotContain("Storage 尚未接通", disclosure);
        Assert.Contains("按设备和日期，记录每个按键的次数和按住时长", disclosure);
        Assert.Contains("不保存你输入的文字、按键顺序、窗口或程序名称", disclosure);
        Assert.Contains("鼠标会按设备记录各个按键的次数", disclosure);
        Assert.Contains("不保存移动位置或路线", disclosure);
        Assert.DoesNotContain("轨迹", disclosure);
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
        Assert.Contains("所有数据都保存在这里", xaml);
        Assert.Contains("再删除程序即可", xaml);
        Assert.Contains("_startup.Enable()", code);
        Assert.Contains("_startup.Disable()", code);

        string consent = File.ReadAllText(Path.Combine(repositoryRoot, "src", "InputTracker.App", "PortableSettings.cs"));
        Assert.Contains("鼠标会分设备记录各个按键的次数，不保存移动位置或路线", consent);
        Assert.DoesNotContain("轨迹", consent);
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
        Assert.Contains("MouseDeviceSelector.IsEnabled = false", code);
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
