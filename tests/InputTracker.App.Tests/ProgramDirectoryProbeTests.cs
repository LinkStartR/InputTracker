using System.IO;

namespace InputTracker.App.Tests;

public sealed class ProgramDirectoryProbeTests
{
    [Fact]
    public void ExistingWritableDirectoryPassesWithoutLeavingProbeFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"InputTracker 测试 {Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            DirectoryProbeResult result = ProgramDirectoryProbe.Check(directory);

            Assert.True(result.IsWritable, result.Error);
            Assert.Empty(Directory.EnumerateFiles(directory, ".inputtracker-write-probe-*.tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void MissingDirectoryFailsClosed()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"InputTracker-missing-{Guid.NewGuid():N}");

        DirectoryProbeResult result = ProgramDirectoryProbe.Check(directory);

        Assert.False(result.IsWritable);
        Assert.Equal(Path.GetFullPath(directory), result.DirectoryPath);
    }
}
