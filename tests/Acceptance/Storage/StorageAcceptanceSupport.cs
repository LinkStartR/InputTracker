using InputTracker.Storage;
using InputTracker.AcceptanceTests.Bootstrap;

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
