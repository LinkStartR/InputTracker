using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace InputTracker.Input.Win32;

internal sealed class RawDeviceInfoProvider : IRawDeviceInfoProvider
{
    public RawDeviceMetadata Query(nint deviceHandle, RawDeviceKind hintedKind)
    {
        if (deviceHandle == 0)
        {
            return new RawDeviceMetadata(
                RawDeviceKind.Unknown,
                null,
                null,
                DeviceIdentityConfidence.Unattributed);
        }

        string? path = ReadDeviceName(deviceHandle);
        RawInputInterop.NativeRawDeviceInfo information = ReadDeviceInformation(deviceHandle);
        RawDeviceKind kind = information.Type switch
        {
            RawInputInterop.RimTypeMouse => RawDeviceKind.Mouse,
            RawInputInterop.RimTypeKeyboard => RawDeviceKind.Keyboard,
            _ => hintedKind
        };
        string summary = kind switch
        {
            RawDeviceKind.Mouse => $"buttons={information.Data.Mouse.ButtonCount};sampleRate={information.Data.Mouse.SampleRate};horizontalWheel={information.Data.Mouse.HasHorizontalWheel != 0}",
            RawDeviceKind.Keyboard => $"type={information.Data.Keyboard.Type};subType={information.Data.Keyboard.SubType};keys={information.Data.Keyboard.TotalKeyCount}",
            _ => $"type={information.Type}"
        };
        DeviceIdentityConfidence confidence = string.IsNullOrWhiteSpace(path)
            || path.Contains("RDP", StringComparison.OrdinalIgnoreCase)
            || path.Contains("ROOT#", StringComparison.OrdinalIgnoreCase)
                ? DeviceIdentityConfidence.RequiresConfirmation
                : DeviceIdentityConfidence.SessionOnly;
        return new RawDeviceMetadata(kind, path, summary, confidence);
    }

    private static string? ReadDeviceName(nint handle)
    {
        uint characterCount = 0;
        uint first = RawInputInterop.GetRawInputDeviceName(
            handle,
            RawInputInterop.RidiDeviceName,
            null,
            ref characterCount);
        if (first == uint.MaxValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetRawInputDeviceInfoW(RIDI_DEVICENAME) failed.");
        }

        if (characterCount == 0)
        {
            return null;
        }

        var buffer = new StringBuilder(checked((int)characterCount));
        uint second = RawInputInterop.GetRawInputDeviceName(
            handle,
            RawInputInterop.RidiDeviceName,
            buffer,
            ref characterCount);
        if (second == uint.MaxValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetRawInputDeviceInfoW(RIDI_DEVICENAME) failed.");
        }

        return buffer.ToString();
    }

    private static RawInputInterop.NativeRawDeviceInfo ReadDeviceInformation(nint handle)
    {
        var information = new RawInputInterop.NativeRawDeviceInfo
        {
            Size = (uint)Marshal.SizeOf<RawInputInterop.NativeRawDeviceInfo>()
        };
        uint size = information.Size;
        uint result = RawInputInterop.GetRawInputDeviceInformation(
            handle,
            RawInputInterop.RidiDeviceInfo,
            ref information,
            ref size);
        if (result == uint.MaxValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "GetRawInputDeviceInfoW(RIDI_DEVICEINFO) failed.");
        }

        return information;
    }
}
