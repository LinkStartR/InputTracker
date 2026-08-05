using System.IO;

namespace InputTracker;

public sealed record DirectoryProbeResult(bool IsWritable, string DirectoryPath, string? Error);

public static class ProgramDirectoryProbe
{
    public static DirectoryProbeResult Check(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        string fullPath = Path.GetFullPath(directoryPath);

        if (!Directory.Exists(fullPath))
        {
            return new DirectoryProbeResult(false, fullPath, "目录不存在。");
        }

        string probePath = Path.Combine(fullPath, $".inputtracker-write-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            using var stream = new FileStream(
                probePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough | FileOptions.DeleteOnClose);
            stream.WriteByte(0x49);
            stream.Flush(flushToDisk: true);
            return new DirectoryProbeResult(true, fullPath, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return new DirectoryProbeResult(false, fullPath, ex.Message);
        }
    }
}
