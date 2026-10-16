namespace InputTracker.AcceptanceTests.Portable;

using InputTracker.AcceptanceTests.Bootstrap;

public sealed class PortableContractAcceptanceTests
{
    [Fact]
    public void SettingsStayProgramLocalAndConsentFailsClosed()
    {
        string source = ReadAppSource("PortableSettings.cs");

        Assert.Contains("programDirectory", source, StringComparison.Ordinal);
        Assert.Contains("CollectionConsentGranted", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplicationData", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Registry", source, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupUsesOnlyExactOwnedShortcut()
    {
        string source = ReadAppSource("StartupShortcutManager.cs");

        Assert.Contains("InputTracker.lnk", source, StringComparison.Ordinal);
        Assert.Contains("OwnedDescription", source, StringComparison.Ordinal);
        Assert.Contains("will not be overwritten", source, StringComparison.Ordinal);
        Assert.Contains("will not be deleted", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.Win32.Registry", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LockSuspendAndSessionEndingHaveExplicitControlPaths()
    {
        string pause = ReadAppSource("PortablePauseController.cs");
        string bridge = ReadAppSource("PortableSystemEventBridge.cs");
        string close = ReadAppSource("WindowClosePolicy.cs");
        string application = ReadAppSource("App.xaml.cs");

        Assert.Contains("SessionLocked", pause, StringComparison.Ordinal);
        Assert.Contains("SystemSuspend", pause, StringComparison.Ordinal);
        Assert.Contains("RequestSessionEnding", close, StringComparison.Ordinal);
        Assert.Contains("RequiresSynchronousCompletion", bridge, StringComparison.Ordinal);
        Assert.Contains("OnSessionEnding", application, StringComparison.Ordinal);
        Assert.Contains("PrepareForSessionEnding", application, StringComparison.Ordinal);
    }

    private static string ReadAppSource(string name)
    {
        string root = AcceptanceEnvironment.FindRepositoryRoot();
        return File.ReadAllText(Path.Combine(root, "src", "InputTracker.App", name));
    }
}
