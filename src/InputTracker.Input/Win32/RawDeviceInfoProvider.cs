using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace InputTracker.Input.Win32;

internal sealed class RawDeviceInfoProvider : IRawDeviceInfoProvider
{
    private readonly WindowsDeviceIdentitySource _identitySource = new();

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

        string? path;
        RawInputInterop.NativeRawDeviceInfo information;
        try
        {
            path = ReadDeviceName(deviceHandle);
            information = ReadDeviceInformation(deviceHandle);
        }
        catch (Exception error) when (IsExpectedDeviceQueryFailure(error))
        {
            return new RawDeviceMetadata(
                hintedKind,
                null,
                null,
                DeviceIdentityConfidence.RequiresConfirmation);
        }
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
        RawDeviceIdentityObservation? identity = null;
        if (!string.IsNullOrWhiteSpace(path))
        {
            try
            {
                identity = _identitySource.Query(path, kind);
            }
            catch (Exception error) when (IsExpectedDeviceQueryFailure(error))
            {
                identity = new RawDeviceIdentityObservation(
                    kind,
                    path,
                    [],
                    null,
                    null,
                    DeviceTransportAssessment.Indeterminate,
                    DeviceIdentityEvidence.RawInputInterfacePath);
            }
        }

        DeviceIdentityConfidence confidence = identity?.Transport == DeviceTransportAssessment.Physical
            ? DeviceIdentityConfidence.SessionOnly
            : DeviceIdentityConfidence.RequiresConfirmation;
        return new RawDeviceMetadata(kind, null, summary, confidence, identity);
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

    private static bool IsExpectedDeviceQueryFailure(Exception error) => error is
        Win32Exception or UnauthorizedAccessException or IOException or InvalidDataException or OverflowException;
}
