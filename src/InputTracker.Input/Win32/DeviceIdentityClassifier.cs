using System.Text;

namespace InputTracker.Input.Win32;

internal enum DeviceTransportAssessment : byte
{
    Physical,
    Bluetooth,
    RemoteDesktop,
    KnownVirtual,
    Indeterminate
}

[Flags]
internal enum DeviceIdentityEvidence : ushort
{
    None = 0,
    RawInputInterfacePath = 1 << 0,
    DevNodeHardwareIds = 1 << 1,
    ContainerId = 1 << 2,
    BusTypeGuid = 1 << 3,
    EnumeratorName = 1 << 4
}

internal sealed record RawDeviceIdentityObservation(
    RawDeviceKind Kind,
    string InterfacePath,
    IReadOnlyList<string> HardwareIds,
    string? SerialNumber,
    Guid? ContainerId,
    DeviceTransportAssessment Transport,
    DeviceIdentityEvidence Evidence);

internal interface IRawDeviceIdentityObserver
{
    ValueTask ObserveAsync(
        RuntimeDeviceId runtimeId,
        RawDeviceIdentityObservation identity,
        CancellationToken cancellationToken);
}

internal sealed class NullRawDeviceIdentityObserver : IRawDeviceIdentityObserver
{
    public static NullRawDeviceIdentityObserver Instance { get; } = new();

    public ValueTask ObserveAsync(
        RuntimeDeviceId runtimeId,
        RawDeviceIdentityObservation identity,
        CancellationToken cancellationToken) => ValueTask.CompletedTask;
}

internal static class DeviceIdentityClassifier
{
    private static readonly Guid BluetoothBusType =
        new("9D7DEBBC-C85D-11D1-9EB4-006008C3A19A");

    public static DeviceTransportAssessment Classify(
        string interfacePath,
        IReadOnlyList<string> hardwareIds,
        string? enumeratorName,
        Guid? busTypeGuid,
        bool classificationComplete)
    {
        ArgumentNullException.ThrowIfNull(interfacePath);
        ArgumentNullException.ThrowIfNull(hardwareIds);

        string enumerator = enumeratorName?.Trim() ?? string.Empty;
        if (ContainsAny(hardwareIds, "ROOT\\RDP_KBD", "ROOT\\RDP_MOU")
            || ContainsAny([interfacePath], "ROOT#RDP_KBD", "ROOT#RDP_MOU"))
        {
            return DeviceTransportAssessment.RemoteDesktop;
        }

        if (busTypeGuid == BluetoothBusType
            || StartsWithAny(enumerator, "BTH", "BTHENUM", "BTHLE")
            || StartsWithAny(hardwareIds, "BTH\\", "BTHENUM\\", "BTHLE\\"))
        {
            return DeviceTransportAssessment.Bluetooth;
        }

        if (StartsWithAny(enumerator, "ROOT", "VMBUS", "SWD")
            || StartsWithAny(hardwareIds, "ROOT\\", "VMBUS\\", "SWD\\"))
        {
            return DeviceTransportAssessment.KnownVirtual;
        }

        return classificationComplete
            ? DeviceTransportAssessment.Physical
            : DeviceTransportAssessment.Indeterminate;
    }

    private static bool ContainsAny(IEnumerable<string> values, params string[] prefixes) =>
        values.Any(value => prefixes.Any(prefix =>
            value.Contains(prefix, StringComparison.OrdinalIgnoreCase)));

    private static bool StartsWithAny(string value, params string[] prefixes) =>
        prefixes.Any(prefix => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static bool StartsWithAny(IEnumerable<string> values, params string[] prefixes) =>
        values.Any(value => StartsWithAny(value, prefixes));
}

internal static class DeviceIdentityPropertyDecoder
{
    public static string[] MultiString(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return [];
        }

        if ((data.Length & 1) != 0)
        {
            throw new InvalidDataException("A device identity property has an invalid byte length.");
        }

        return Encoding.Unicode.GetString(data)
            .Split('\0', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public static string? String(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return null;
        }

        if ((data.Length & 1) != 0)
        {
            throw new InvalidDataException("A device identity property has an invalid byte length.");
        }

        string value = Encoding.Unicode.GetString(data).TrimEnd('\0').Trim();
        return value.Length == 0 ? null : value;
    }

    public static Guid? Guid(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return null;
        }

        if (data.Length != 16)
        {
            throw new InvalidDataException("A device identity GUID property has an invalid byte length.");
        }

        var result = new Guid(data);
        return result == System.Guid.Empty ? null : result;
    }

}
