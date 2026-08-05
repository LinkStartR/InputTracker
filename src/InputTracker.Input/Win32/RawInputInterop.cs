using System.Runtime.InteropServices;
using System.Text;

namespace InputTracker.Input.Win32;

internal static class RawInputInterop
{
    internal const uint WmInputDeviceChange = 0x00FE;
    internal const uint WmInput = 0x00FF;
    internal const uint WmClose = 0x0010;
    internal const uint WmDestroy = 0x0002;
    internal const uint WmAppControl = 0x8001;

    internal const nuint RimInput = 0;
    internal const uint RimTypeMouse = 0;
    internal const uint RimTypeKeyboard = 1;
    internal const uint RidInput = 0x10000003;
    internal const uint RidiDeviceName = 0x20000007;
    internal const uint RidiDeviceInfo = 0x2000000B;
    internal const uint RidevRemove = 0x00000001;
    internal const uint RidevInputSink = 0x00000100;
    internal const uint RidevDevNotify = 0x00002000;
    internal const nuint GidcArrival = 1;
    internal const nuint GidcRemoval = 2;

    internal const ushort UsagePageGenericDesktop = 0x01;
    internal const ushort UsageMouse = 0x02;
    internal const ushort UsageKeyboard = 0x06;

    internal static readonly nint HwndMessage = new(-3);

    internal delegate nint WindowProcedure(nint window, uint code, nuint word, nint data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WindowClass
    {
        internal uint Size;
        internal uint Style;
        internal nint WindowProcedure;
        internal int ClassExtra;
        internal int WindowExtra;
        internal nint Instance;
        internal nint Icon;
        internal nint Cursor;
        internal nint Background;
        internal string? MenuName;
        internal string ClassName;
        internal nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct RawInputDevice(ushort usagePage, ushort usage, uint flags, nint target)
    {
        internal readonly ushort UsagePage = usagePage;
        internal readonly ushort Usage = usage;
        internal readonly uint Flags = flags;
        internal readonly nint Target = target;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRawInputHeader
    {
        internal uint Type;
        internal uint Size;
        internal nint Device;
        internal nuint InputCode;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct NativeRawMouse
    {
        [FieldOffset(0)] internal ushort Flags;
        [FieldOffset(4)] internal uint CombinedButtons;
        [FieldOffset(4)] internal ushort ButtonFlags;
        [FieldOffset(6)] internal ushort ButtonData;
        [FieldOffset(8)] internal uint IgnoredRawButtons;
        [FieldOffset(12)] internal int LastX;
        [FieldOffset(16)] internal int LastY;
        [FieldOffset(20)] internal uint IgnoredExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRawKeyboard
    {
        internal ushort MakeCode;
        internal ushort Flags;
        private ushort Reserved;
        private ushort IgnoredVirtualKey;
        private uint IgnoredLegacyMessage;
        private uint IgnoredExtraInformation;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct NativeRawInputData
    {
        [FieldOffset(0)] internal NativeRawMouse Mouse;
        [FieldOffset(0)] internal NativeRawKeyboard Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRawInput
    {
        internal NativeRawInputHeader Header;
        internal NativeRawInputData Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRawDeviceInfo
    {
        internal uint Size;
        internal uint Type;
        internal NativeRawDeviceInfoData Data;
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    internal struct NativeRawDeviceInfoData
    {
        [FieldOffset(0)] internal NativeMouseDeviceInfo Mouse;
        [FieldOffset(0)] internal NativeKeyboardDeviceInfo Keyboard;
        [FieldOffset(0)] internal NativeHidDeviceInfo Hid;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeMouseDeviceInfo
    {
        internal uint Identifier;
        internal uint ButtonCount;
        internal uint SampleRate;
        internal int HasHorizontalWheel;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeKeyboardDeviceInfo
    {
        internal uint Type;
        internal uint SubType;
        internal uint KeyboardMode;
        internal uint FunctionKeyCount;
        internal uint IndicatorCount;
        internal uint TotalKeyCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeHidDeviceInfo
    {
        internal uint VendorId;
        internal uint ProductId;
        internal uint VersionNumber;
        internal ushort UsagePage;
        internal ushort Usage;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern ushort RegisterClassExW(ref WindowClass windowClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool UnregisterClassW(string className, nint instance);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern nint CreateWindowExW(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll")]
    internal static extern nint DefWindowProcW(nint window, uint code, nuint word, nint data);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int GetMessageW(out NativeWindowMessage message, nint window, uint minimum, uint maximum);

    [DllImport("user32.dll")]
    internal static extern bool TranslateMessage(ref NativeWindowMessage message);

    [DllImport("user32.dll")]
    internal static extern nint DispatchMessageW(ref NativeWindowMessage message);

    [DllImport("user32.dll")]
    internal static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool PostMessageW(nint window, uint code, nuint word, nint data);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RegisterRawInputDevices(
        [In] RawInputDevice[] devices,
        uint deviceCount,
        uint size);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetRawInputData(
        nint rawInput,
        uint command,
        nint data,
        ref uint size,
        uint headerSize);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern uint GetRawInputDeviceName(
        nint device,
        uint command,
        StringBuilder? data,
        ref uint size);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true)]
    internal static extern uint GetRawInputDeviceInformation(
        nint device,
        uint command,
        ref NativeRawDeviceInfo data,
        ref uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint GetModuleHandleW(string? moduleName);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeWindowMessage
    {
        internal nint Window;
        internal uint Code;
        internal nuint Word;
        internal nint Data;
        internal uint Time;
        internal int PointX;
        internal int PointY;
        internal uint Private;
    }
}
