using System.ComponentModel;
using System.Runtime.InteropServices;

namespace InputTracker.Input.Win32;

internal sealed class WindowsDeviceIdentitySource
{
    public RawDeviceIdentityObservation Query(string interfacePath, RawDeviceKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interfacePath);

        DeviceIdentityInterop.DeviceInfoSet set = DeviceIdentityInterop.OpenDevice(interfacePath);
        try
        {
            DeviceIdentityInterop.DeviceProperties properties = DeviceIdentityInterop.ReadProperties(set.DeviceInstance);
            DeviceIdentityEvidence evidence = DeviceIdentityEvidence.RawInputInterfacePath |
                properties.Evidence;

            DeviceTransportAssessment transport = DeviceIdentityClassifier.Classify(
                interfacePath,
                properties.HardwareIds,
                properties.EnumeratorName,
                properties.BusTypeGuid,
                properties.ClassificationComplete);
            return new RawDeviceIdentityObservation(
                kind,
                interfacePath,
                properties.HardwareIds,
                null,
                properties.ContainerId,
                transport,
                evidence);
        }
        finally
        {
            set.Dispose();
        }
    }
}

internal static class DeviceIdentityInterop
{
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfDeviceInterface = 0x00000010;
    private const uint CrSuccess = 0x00000000;
    private const uint CrBufferSmall = 0x0000001A;
    private const uint CrNoSuchValue = 0x00000025;
    private const uint DevpropTypeString = 0x00000012;
    private const uint DevpropTypeStringList = 0x00002012;
    private const uint DevpropTypeGuid = 0x0000000D;

    private static readonly DevPropKey HardwareIds = new(
        new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 3);
    private static readonly DevPropKey BusTypeGuid = new(
        new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 21);
    private static readonly DevPropKey EnumeratorName = new(
        new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 24);
    private static readonly DevPropKey ContainerId = new(
        new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);

    public static DeviceInfoSet OpenDevice(string interfacePath)
    {
        nint set = SetupDiCreateDeviceInfoList(0, 0);
        if (set == new nint(-1))
        {
            throw NativeFailure("device-info-list");
        }

        var owner = new DeviceInfoSet(set, 0);
        try
        {
            var interfaceData = new SpDeviceInterfaceData
            {
                Size = (uint)Marshal.SizeOf<SpDeviceInterfaceData>()
            };
            if (!SetupDiOpenDeviceInterfaceW(set, interfacePath, 0, ref interfaceData))
            {
                throw NativeFailure("device-interface-open");
            }

            _ = SetupDiGetDeviceInterfaceDetailW(
                set,
                ref interfaceData,
                0,
                0,
                out uint required,
                0);
            if (required < (Environment.Is64BitProcess ? 8u : 6u))
            {
                throw NativeFailure("device-interface-size");
            }

            nint detail = Marshal.AllocHGlobal(checked((int)required));
            try
            {
                Marshal.WriteInt32(detail, Environment.Is64BitProcess ? 8 : 6);
                var info = new SpDevInfoData { Size = (uint)Marshal.SizeOf<SpDevInfoData>() };
                if (!SetupDiGetDeviceInterfaceDetailW(
                        set,
                        ref interfaceData,
                        detail,
                        required,
                        out _,
                        ref info))
                {
                    throw NativeFailure("device-interface-detail");
                }

                owner = owner with { DeviceInstance = info.DeviceInstance };
                return owner;
            }
            finally
            {
                Marshal.FreeHGlobal(detail);
            }
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    public static DeviceProperties ReadProperties(uint deviceInstance)
    {
        PropertyValue hardware = ReadProperty(deviceInstance, HardwareIds, DevpropTypeStringList);
        PropertyValue container = ReadProperty(deviceInstance, ContainerId, DevpropTypeGuid);
        PropertyValue bus = ReadProperty(deviceInstance, BusTypeGuid, DevpropTypeGuid);
        PropertyValue enumerator = ReadProperty(deviceInstance, EnumeratorName, DevpropTypeString);
        DeviceIdentityEvidence evidence = DeviceIdentityEvidence.None;
        if (hardware.Available) evidence |= DeviceIdentityEvidence.DevNodeHardwareIds;
        if (container.Available) evidence |= DeviceIdentityEvidence.ContainerId;
        if (bus.Available) evidence |= DeviceIdentityEvidence.BusTypeGuid;
        if (enumerator.Available) evidence |= DeviceIdentityEvidence.EnumeratorName;

        return new DeviceProperties(
            DeviceIdentityPropertyDecoder.MultiString(hardware.Data),
            null,
            DeviceIdentityPropertyDecoder.Guid(container.Data),
            DeviceIdentityPropertyDecoder.Guid(bus.Data),
            DeviceIdentityPropertyDecoder.String(enumerator.Data),
            hardware.Available && bus.Available && enumerator.Available,
            evidence);
    }

    private static PropertyValue ReadProperty(uint deviceInstance, DevPropKey key, uint expectedType)
    {
        uint size = 0;
        uint keyCopy = key.PropertyId;
        Guid categoryCopy = key.Category;
        var nativeKey = new DevPropKey(categoryCopy, keyCopy);
        uint result = CM_Get_DevNode_PropertyW(
            deviceInstance,
            ref nativeKey,
            out uint propertyType,
            null,
            ref size,
            0);
        if (result == CrNoSuchValue)
        {
            return default;
        }

        if (result != CrBufferSmall || size == 0)
        {
            return default;
        }

        byte[] buffer = new byte[checked((int)size)];
        result = CM_Get_DevNode_PropertyW(
            deviceInstance,
            ref nativeKey,
            out propertyType,
            buffer,
            ref size,
            0);
        if (result != CrSuccess || propertyType != expectedType || size > buffer.Length)
        {
            return default;
        }

        return new PropertyValue(buffer.AsSpan(0, checked((int)size)).ToArray(), true);
    }

    private static Win32Exception NativeFailure(string operation) =>
        new(Marshal.GetLastWin32Error(), $"Device identity query failed at {operation}.");

    internal readonly record struct DeviceProperties(
        string[] HardwareIds,
        string? SerialNumber,
        Guid? ContainerId,
        Guid? BusTypeGuid,
        string? EnumeratorName,
        bool ClassificationComplete,
        DeviceIdentityEvidence Evidence);

    private readonly record struct PropertyValue(byte[]? Bytes, bool Available)
    {
        public ReadOnlySpan<byte> Data => Bytes ?? [];
    }

    internal sealed record DeviceInfoSet(nint Handle, uint DeviceInstance) : IDisposable
    {
        public void Dispose()
        {
            if (Handle != 0 && Handle != new nint(-1))
            {
                SetupDiDestroyDeviceInfoList(Handle);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        internal uint Size;
        private Guid InterfaceClass;
        private uint Flags;
        private nuint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        internal uint Size;
        private Guid ClassGuid;
        internal uint DeviceInstance;
        private nuint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey(Guid category, uint propertyId)
    {
        internal Guid Category = category;
        internal uint PropertyId = propertyId;
    }

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern nint SetupDiCreateDeviceInfoList(nint classGuid, nint parentWindow);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiOpenDeviceInterfaceW(
        nint deviceInfoSet,
        string devicePath,
        uint openFlags,
        ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(
        nint deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData,
        nint deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        nint deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(
        nint deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData,
        nint deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize,
        out uint requiredSize,
        ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern uint CM_Get_DevNode_PropertyW(
        uint deviceInstance,
        ref DevPropKey propertyKey,
        out uint propertyType,
        byte[]? propertyBuffer,
        ref uint propertyBufferSize,
        uint flags);

}
