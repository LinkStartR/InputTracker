using InputTracker.Storage;
using InputTracker.AcceptanceTests.Bootstrap;
using System.Diagnostics;

namespace InputTracker.AcceptanceTests.Storage;

internal static class StorageAcceptanceSupport
{
    public static StorageDeviceObservation MouseObservation() => new(
        StorageDeviceKind.Mouse,
        @"\\?\HID#VID_A11E&PID_0001#acceptance",
        ["HID\\VID_A11E&PID_0001"],
        "ACCEPTANCE-0001",
        Guid.Parse("8b8a4273-d780-4b70-92c8-48192ec7ecbb"),
        IsBluetooth: false,
        IsRemoteDesktop: false,
        IsVirtual: false);

    public static string CreateCommittedMouseChunk(string programDirectory)
    {
        using StorageService service = StorageService.Open(StorageLayout.Create(programDirectory));
        Guid deviceId = service.ObserveDevice(MouseObservation()).Device.DeviceId;
        Guid calibrationId = Guid.NewGuid();
        TimeCalibrationSegment calibration = TimeCalibrationSegment.Capture(
            calibrationId, SystemStorageClock.Instance);
        service.Start(calibration);
        long recordTimestamp = Stopwatch.GetTimestamp();
        service.WriteMouseTransition(new StorageMouseTransition(
            deviceId, calibrationId, recordTimestamp, 7, -3, 0, false, 1, 0, 0));
        service.Stop(Stopwatch.GetTimestamp());

        return Assert.Single(Directory.EnumerateFiles(
            Path.Combine(programDirectory, "data", "storage", "chunks"), "*.mchunk", SearchOption.AllDirectories));
    }
}

internal sealed class AcceptanceTemporaryDirectory : IDisposable
{
    public AcceptanceTemporaryDirectory(string label)
    {
        Path = System.IO.Path.Combine(
            AcceptanceEnvironment.FindRepositoryRoot(), ".tools", "acceptance", label, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }
}
