using System.Diagnostics;
using System.Security.Cryptography;
using System.Xml.Linq;

namespace InputTracker.AcceptanceTests.Bootstrap;

public sealed class BootstrapAcceptanceTests
{
    [Fact]
    public void TestHostRunsOnDotNet10()
    {
        Assert.Equal(10, Environment.Version.Major);
    }

    [Fact]
    public void ManifestIsAsInvokerWithoutUiAccess()
    {
        string repositoryRoot = AcceptanceEnvironment.FindRepositoryRoot();
        string manifestPath = Path.Combine(repositoryRoot, "src", "InputTracker.App", "app.manifest");
        string projectPath = Path.Combine(repositoryRoot, "src", "InputTracker.App", "InputTracker.App.csproj");

        XDocument manifest = XDocument.Load(manifestPath);
        XElement requestedLevel = Assert.Single(
            manifest.Descendants(),
            element => element.Name.LocalName == "requestedExecutionLevel");

        Assert.Equal("asInvoker", requestedLevel.Attribute("level")?.Value);
        Assert.Equal("false", requestedLevel.Attribute("uiAccess")?.Value);

        XDocument project = XDocument.Load(projectPath);
        XElement applicationManifest = Assert.Single(
            project.Descendants(),
            element => element.Name.LocalName == "ApplicationManifest");
        Assert.Equal("app.manifest", applicationManifest.Value);
    }

    [Fact]
    public void ReleaseOutputsStayUnderGreenToolsDirectory()
    {
        string repositoryRoot = AcceptanceEnvironment.FindRepositoryRoot();
        string appExecutable = AcceptanceEnvironment.FindReleaseAppExecutable(repositoryRoot);
        string toolsRoot = Path.GetFullPath(Path.Combine(repositoryRoot, ".tools")) + Path.DirectorySeparatorChar;

        Assert.StartsWith(toolsRoot, Path.GetFullPath(appExecutable), StringComparison.OrdinalIgnoreCase);

        string[] forbiddenOutputDirectories = Directory
            .EnumerateDirectories(repositoryRoot, "*", SearchOption.AllDirectories)
            .Where(path =>
            {
                string relative = Path.GetRelativePath(repositoryRoot, path);
                if (relative.StartsWith($".tools{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith($".git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                string name = Path.GetFileName(path);
                return name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                       name.Equals("obj", StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();

        Assert.Empty(forbiddenOutputDirectories);
    }

    [Fact]
    public void SelfCheckPassesFromUnicodeSpacePathWithoutResidue()
    {
        using var app = AcceptanceEnvironment.CopyReleaseApp("中文 空格自检");
        IReadOnlyDictionary<string, FileState> before = AcceptanceEnvironment.SnapshotFiles(app.DirectoryPath);

        using Process process = AcceptanceEnvironment.Start(app.ExecutablePath, "--self-check");
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(15)), "--self-check 未在 15 秒内退出。");
        Assert.Equal(0, process.ExitCode);

        IReadOnlyDictionary<string, FileState> after = AcceptanceEnvironment.SnapshotFiles(app.DirectoryPath);
        Assert.Equal(
            before.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase),
            after.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void SameDirectorySecondProcessExitsTwoAndProcessesTerminate()
    {
        using var app = AcceptanceEnvironment.CopyReleaseApp("中文 单实例");
        using Process holder = AcceptanceEnvironment.Start(app.ExecutablePath, "--hold-smoke");

        try
        {
            AcceptanceEnvironment.WaitForApplicationMutex(app.DirectoryPath, holder, TimeSpan.FromSeconds(10));

            using Process second = AcceptanceEnvironment.Start(app.ExecutablePath, "--self-check");
            Assert.True(second.WaitForExit(TimeSpan.FromSeconds(15)), "第二个实例未在 15 秒内退出。");
            Assert.Equal(2, second.ExitCode);

            Assert.True(holder.WaitForExit(TimeSpan.FromSeconds(15)), "持有实例未按约定退出。");
            Assert.Equal(0, holder.ExitCode);
            Assert.True(holder.HasExited);
            Assert.True(second.HasExited);
        }
        finally
        {
            AcceptanceEnvironment.TerminateIfRunning(holder);
        }
    }
}

internal sealed record FileState(long Length, DateTime LastWriteTimeUtc);

internal sealed class CopiedApplication : IDisposable
{
    public CopiedApplication(string directoryPath, string executablePath)
    {
        DirectoryPath = directoryPath;
        ExecutablePath = executablePath;
    }

    public string DirectoryPath { get; }

    public string ExecutablePath { get; }

    public void Dispose()
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (Directory.Exists(DirectoryPath))
                {
                    Directory.Delete(DirectoryPath, recursive: true);
                }

                return;
            }
            catch (IOException) when (attempt < 19)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException) when (attempt < 19)
            {
                Thread.Sleep(100);
            }
        }
    }
}

internal static class AcceptanceEnvironment
{
    public static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "InputTracker.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("无法从测试输出目录定位 InputTracker.sln。");
    }

    public static string FindReleaseAppExecutable(string repositoryRoot)
    {
        string outputRoot = Path.Combine(repositoryRoot, ".tools", "build", "InputTracker.App", "Release");
        if (!Directory.Exists(outputRoot))
        {
            throw new DirectoryNotFoundException($"Release 输出目录不存在：{outputRoot}");
        }

        string? executable = Directory
            .EnumerateFiles(outputRoot, "InputTracker.exe", SearchOption.AllDirectories)
            .Where(path => File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "InputTracker.runtimeconfig.json")))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

        return executable ?? throw new FileNotFoundException("Release 输出中未找到可运行的 InputTracker.exe。");
    }

    public static CopiedApplication CopyReleaseApp(string directoryLabel)
    {
        string repositoryRoot = FindRepositoryRoot();
        string sourceExecutable = FindReleaseAppExecutable(repositoryRoot);
        string sourceDirectory = Path.GetDirectoryName(sourceExecutable)!;
        string destination = Path.Combine(
            Path.GetTempPath(),
            $"InputTracker-Acceptance-{directoryLabel}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(destination);

        foreach (string sourcePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceDirectory, sourcePath);
            string destinationPath = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath);
        }

        return new CopiedApplication(destination, Path.Combine(destination, "InputTracker.exe"));
    }

    public static Process Start(string executablePath, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = Path.GetDirectoryName(executablePath)!,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return Process.Start(startInfo) ?? throw new InvalidOperationException($"无法启动 {executablePath}");
    }

    public static IReadOnlyDictionary<string, FileState> SnapshotFiles(string directoryPath) =>
        Directory
            .EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(directoryPath, path),
                path =>
                {
                    var info = new FileInfo(path);
                    return new FileState(info.Length, info.LastWriteTimeUtc);
                },
                StringComparer.OrdinalIgnoreCase);

    public static void WaitForApplicationMutex(string applicationDirectory, Process process, TimeSpan timeout)
    {
        string scope = Path.GetFullPath(
            Path.TrimEndingDirectorySeparator(applicationDirectory) + Path.DirectorySeparatorChar);
        byte[] hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(scope.ToUpperInvariant()));
        string mutexName = $"Local\\InputTracker-{Convert.ToHexString(hash.AsSpan(0, 12))}";
        Stopwatch timer = Stopwatch.StartNew();

        while (timer.Elapsed < timeout)
        {
            if (process.HasExited)
            {
                throw new Xunit.Sdk.XunitException(
                    $"持有实例在创建 Mutex 前退出，退出码 {process.ExitCode}。");
            }

            try
            {
                using Mutex mutex = Mutex.OpenExisting(mutexName);
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(50);
            }
        }

        throw new Xunit.Sdk.XunitException($"未在 {timeout.TotalSeconds:N0} 秒内观察到应用 Mutex。");
    }

    public static void TerminateIfRunning(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        process.Kill(entireProcessTree: true);
        process.WaitForExit(TimeSpan.FromSeconds(5));
    }
}
