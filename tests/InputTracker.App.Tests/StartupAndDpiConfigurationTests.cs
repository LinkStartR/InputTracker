using System.Xml.Linq;

namespace InputTracker.App.Tests;

public sealed class StartupAndDpiConfigurationTests
{
    [Fact]
    public void StartupStatusUsesShortEnabledAndDisabledMessages()
    {
        string code = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "InputTracker.App", "MainWindow.xaml.cs"));

        Assert.Contains("? \"开机启动已开启。\"", code, StringComparison.Ordinal);
        Assert.Contains(": \"开机启动已关闭。\";", code, StringComparison.Ordinal);
        Assert.Contains("无法读取开机启动状态：{error.Message}", code, StringComparison.Ordinal);
    }

    [Fact]
    public void AppManifestAndRuntimeConfigEnablePerMonitorWpfDpiScaling()
    {
        string appDirectory = Path.Combine(FindRepositoryRoot(), "src", "InputTracker.App");
        XDocument manifest = XDocument.Load(Path.Combine(appDirectory, "app.manifest"));
        XNamespace settings = "http://schemas.microsoft.com/SMI/2016/WindowsSettings";
        XElement dpiAwareness = Assert.Single(manifest.Descendants(settings + "dpiAwareness"));
        Assert.Equal("PerMonitorV2, PerMonitor", dpiAwareness.Value);

        XDocument project = XDocument.Load(Path.Combine(appDirectory, "InputTracker.App.csproj"));
        XElement runtimeOption = Assert.Single(project.Descendants("RuntimeHostConfigurationOption"));
        Assert.Equal("Switch.System.Windows.DoNotScaleForDpiChanges", (string?)runtimeOption.Attribute("Include"));
        Assert.Equal("false", (string?)runtimeOption.Attribute("Value"));
    }

    [Fact]
    public void StartupButtonsAreDisabledAndExplainedWhenNoStartupFolderIsAvailable()
    {
        string root = FindRepositoryRoot();
        string code = File.ReadAllText(Path.Combine(root, "src", "InputTracker.App", "MainWindow.xaml.cs"));
        string xaml = File.ReadAllText(Path.Combine(root, "src", "InputTracker.App", "MainWindow.xaml"));

        Assert.Contains("x:Name=\"EnableStartupButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DisableStartupButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("EnableStartupButton.IsEnabled = false;", code, StringComparison.Ordinal);
        Assert.Contains("DisableStartupButton.IsEnabled = false;", code, StringComparison.Ordinal);
        Assert.Contains("没有可用的 Startup 文件夹", code, StringComparison.Ordinal);
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
