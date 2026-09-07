namespace InputTracker.App.Tests;

public sealed class DeviceAliasUiTests
{
    [Fact]
    public void KeyboardAndMousePagesExposeBoundedSaveAndClearControls()
    {
        string root = FindRepositoryRoot();
        string xaml = File.ReadAllText(Path.Combine(root, "src", "InputTracker.App", "MainWindow.xaml"));
        string code = File.ReadAllText(Path.Combine(root, "src", "InputTracker.App", "MainWindow.xaml.cs"));

        Assert.Contains("x:Name=\"KeyboardAliasInput\"", xaml);
        Assert.Contains("x:Name=\"MouseAliasInput\"", xaml);
        Assert.Equal(2, CountOccurrences(xaml, "MaxLength=\"128\""));
        Assert.Contains("SaveKeyboardAliasButton_Click", xaml);
        Assert.Contains("ClearKeyboardAliasButton_Click", xaml);
        Assert.Contains("SaveMouseAliasButton_Click", xaml);
        Assert.Contains("ClearMouseAliasButton_Click", xaml);
        Assert.Contains("SetDeviceAliasAsync", code);
    }

    private static int CountOccurrences(string value, string expected)
    {
        int count = 0;
        int offset = 0;
        while ((offset = value.IndexOf(expected, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += expected.Length;
        }

        return count;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "InputTracker.sln"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate InputTracker.sln.");
    }
}
