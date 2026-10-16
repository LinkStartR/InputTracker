using System.Buffers.Binary;
using System.Text;

namespace InputTracker.Storage.Tests;

public sealed class StorageExportTests
{
    [Fact]
    public void CsvUsesStablePrivacySafeColumnsAndEscapesDeviceAlias()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid deviceId = Guid.NewGuid();
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock()))
        {
            database.RegisterDevice(deviceId, (int)StorageDeviceKind.Keyboard, 0, 1);
            database.SetDeviceAlias(deviceId, "Office, \"left\"");
            database.UpsertKeyboardDaily(
                deviceId, 30, false, false, new DateOnly(2026, 8, 13), "UTC", 7, 900);
        }

        using StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock());
        StorageKeyboardPeriodResult result = service.QueryKeyboardPeriod(
            StoragePeriodKind.Day, new DateOnly(2026, 8, 13), "UTC");

        StorageExportArtifact artifact = service.ExportKeyboardCsv(result, "exports/keyboard.csv");

        Assert.Equal(Path.Combine(directory.Path, "exports", "keyboard.csv"), artifact.Path);
        Assert.Equal(artifact.Sha256, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(artifact.Path))));
        string csv = File.ReadAllText(artifact.Path, Encoding.UTF8);
        Assert.Equal(
            "device_id,device_alias,make_code,is_e0,is_e1,press_count,hold_duration_us\r\n"
            + $"{deviceId:N},\"Office, \"\"left\"\"\",30,0,0,7,900\r\n",
            csv);
        Assert.DoesNotContain("window", csv, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("process", csv, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("text", csv, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PngIsDeterministicLosslessHeatmapWithRequestedDimensions()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid deviceId = Guid.NewGuid();
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock()))
        {
            database.RegisterDevice(deviceId, (int)StorageDeviceKind.Keyboard, 0, 1);
            database.UpsertKeyboardDaily(
                deviceId, 30, false, false, new DateOnly(2026, 8, 13), "UTC", 1, 10);
            database.UpsertKeyboardDaily(
                deviceId, 31, false, false, new DateOnly(2026, 8, 13), "UTC", 100, 20);
        }

        using StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock());
        StorageKeyboardPeriodResult result = service.QueryKeyboardPeriod(
            StoragePeriodKind.Day, new DateOnly(2026, 8, 13), "UTC", deviceId);

        StorageExportArtifact first = service.ExportKeyboardHeatmapPng(result, "exports/heatmap.png", 512, 512);
        StorageExportArtifact second = service.ExportKeyboardHeatmapPng(result, "exports/heatmap-2.png", 512, 512);

        byte[] bytes = File.ReadAllBytes(first.Path);
        Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes[..8]);
        Assert.Equal(512, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)));
        Assert.Equal(512, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)));
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.True(bytes.Length > 100);
    }

    [Theory]
    [InlineData("../outside.csv")]
    [InlineData("C:/outside.csv")]
    [InlineData("")]
    public void ExportRejectsPathsOutsideProgramDirectory(string relativePath)
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        using StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock());
        var result = new StorageKeyboardPeriodResult(
            StoragePeriodResolver.Resolve(StoragePeriodKind.Day, new DateOnly(2026, 8, 13), "UTC"),
            [],
            []);

        Assert.ThrowsAny<Exception>(() => service.ExportKeyboardCsv(result, relativePath));
    }

    [Fact]
    public void ExportRejectsJunctionBeforeCreatingAnythingInItsTarget()
    {
        using var program = new TemporaryDirectory();
        using var outside = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(program.Path);
        string junction = Path.Combine(program.Path, "exports");
        using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
            $"/d /c mklink /J \"{junction}\" \"{outside.Path}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!)
        {
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
        }

        try
        {
            using StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock());
            var result = new StorageKeyboardPeriodResult(
                StoragePeriodResolver.Resolve(StoragePeriodKind.Day, new DateOnly(2026, 8, 13), "UTC"),
                [],
                []);
            Assert.Throws<InvalidDataException>(() =>
                service.ExportKeyboardCsv(result, "exports/nested/keyboard.csv"));
            Assert.Empty(Directory.EnumerateFileSystemEntries(outside.Path));
        }
        finally
        {
            Directory.Delete(junction);
        }
    }
}
