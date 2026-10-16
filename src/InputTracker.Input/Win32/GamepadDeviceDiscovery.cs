using System.Runtime.InteropServices;

namespace InputTracker.Input.Win32;

internal static class GamepadDeviceDiscovery
{
    internal static nint[] EnumerateDeviceHandles()
    {
        try
        {
            if (!TryGetDeviceList(out RawInputInterop.NativeRawInputDeviceList[] devices))
            {
                return [];
            }

            return FindGamepadDeviceHandles(devices, TryGetDeviceInformation);
        }
        catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
        {
            return [];
        }
    }

    internal static nint[] FindGamepadDeviceHandles(
        IReadOnlyList<RawInputInterop.NativeRawInputDeviceList> devices,
        Func<nint, RawInputInterop.NativeRawDeviceInfo?> getDeviceInformation)
    {
        var seenHandles = new HashSet<nint>();
        var handles = new List<nint>();
        foreach (RawInputInterop.NativeRawInputDeviceList device in devices)
        {
            if (device.DeviceHandle == 0
                || device.Type != RawInputInterop.RimTypeHid
                || !seenHandles.Add(device.DeviceHandle))
            {
                continue;
            }

            RawInputInterop.NativeRawDeviceInfo? information;
            try
            {
                information = getDeviceInformation(device.DeviceHandle);
            }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
            {
                continue;
            }

            if (information is not RawInputInterop.NativeRawDeviceInfo value
                || value.Type != RawInputInterop.RimTypeHid
                || value.Data.Hid.UsagePage != RawInputInterop.UsagePageGenericDesktop
                || value.Data.Hid.Usage is not (RawInputInterop.UsageGamepad or RawInputInterop.UsageJoystick))
            {
                continue;
            }

            handles.Add(device.DeviceHandle);
        }

        return handles.ToArray();
    }

    private static bool TryGetDeviceList(out RawInputInterop.NativeRawInputDeviceList[] devices)
    {
        devices = [];
        uint count = 0;
        uint deviceSize = (uint)Marshal.SizeOf<RawInputInterop.NativeRawInputDeviceList>();
        uint result = RawInputInterop.GetRawInputDeviceList(0, ref count, deviceSize);
        if (result == uint.MaxValue || count == 0)
        {
            return result != uint.MaxValue;
        }

        int bufferSize;
        try
        {
            bufferSize = checked((int)((ulong)count * deviceSize));
        }
        catch (OverflowException)
        {
            return false;
        }

        nint buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            uint requestedCount = count;
            result = RawInputInterop.GetRawInputDeviceList(buffer, ref requestedCount, deviceSize);
            if (result == uint.MaxValue || result > count)
            {
                return false;
            }

            devices = new RawInputInterop.NativeRawInputDeviceList[result];
            for (int index = 0; index < devices.Length; index++)
            {
                devices[index] = Marshal.PtrToStructure<RawInputInterop.NativeRawInputDeviceList>(
                    nint.Add(buffer, checked(index * (int)deviceSize)));
            }

            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static RawInputInterop.NativeRawDeviceInfo? TryGetDeviceInformation(nint deviceHandle)
    {
        var information = new RawInputInterop.NativeRawDeviceInfo
        {
            Size = (uint)Marshal.SizeOf<RawInputInterop.NativeRawDeviceInfo>()
        };
        uint size = information.Size;
        return RawInputInterop.GetRawInputDeviceInformation(
            deviceHandle,
            RawInputInterop.RidiDeviceInfo,
            ref information,
            ref size) == uint.MaxValue
                ? null
                : information;
    }
}
