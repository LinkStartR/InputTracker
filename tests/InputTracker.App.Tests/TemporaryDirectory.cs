namespace InputTracker.App.Tests;

using System.Diagnostics;

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"InputTracker-AppTests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public static void CreateJunction(string junctionPath, string targetPath)
    {
        using var process = Process.Start(new ProcessStartInfo(
            Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
            $"/d /c mklink /J \"{junctionPath}\" \"{targetPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Could not start junction fixture command.");
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        Assert.True((File.GetAttributes(junctionPath) & FileAttributes.ReparsePoint) != 0);
    }

    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }
}
