namespace InputTracker.Input;

public readonly record struct RuntimeDeviceId(ulong Value)
{
    public static RuntimeDeviceId Unattributed { get; } = new(0);

    public override string ToString() => Value == 0 ? "unattributed" : Value.ToString("X16");
}

public enum PhysicalKeyPrefix : byte
{
    None,
    E0,
    E1
}

public enum PhysicalKeyTransition : byte
{
    Down,
    Up
}

public readonly record struct KeyboardPhysicalEvent(
    RuntimeDeviceId DeviceId,
    ushort MakeCode,
    PhysicalKeyPrefix Prefix,
    PhysicalKeyTransition Transition,
    long Timestamp);

public enum MouseMotionKind : byte
{
    Relative,
    Absolute
}

[Flags]
public enum MouseButtonChanges : ushort
{
    None = 0,
    LeftDown = 1 << 0,
    LeftUp = 1 << 1,
    RightDown = 1 << 2,
    RightUp = 1 << 3,
    MiddleDown = 1 << 4,
    MiddleUp = 1 << 5,
    X1Down = 1 << 6,
    X1Up = 1 << 7,
    X2Down = 1 << 8,
    X2Up = 1 << 9
}

public readonly record struct MouseRawEvent(
    RuntimeDeviceId DeviceId,
    int DeltaX,
    int DeltaY,
    MouseMotionKind MotionKind,
    bool UsesVirtualDesktop,
    MouseButtonChanges ButtonChanges,
    short VerticalWheelDelta,
    short HorizontalWheelDelta,
    long Timestamp);

/// <summary>Identifies one HID control in the gamepad's input descriptor.</summary>
public readonly record struct GamepadControlId(
    ushort UsagePage,
    ushort Usage,
    ushort LinkCollection,
    ushort ElementIndex);

/// <summary>
/// Reports a changed raw gamepad control value. Button controls use 0 for released and 1 for pressed.
/// Hat Switch controls use the descriptor's logical value; -1 means the descriptor's NULL/neutral state.
/// Other HID value controls use the descriptor's raw logical value and include logical bounds when available.
/// </summary>
public readonly record struct GamepadControlEvent(
    RuntimeDeviceId DeviceId,
    GamepadControlId Control,
    int Value,
    long Timestamp,
    bool IsBaseline = false,
    int? LogicalMinimum = null,
    int? LogicalMaximum = null);

public enum GamepadCaptureStatus : byte
{
    Ready,
    Unsupported,
    Faulted
}

public readonly record struct GamepadInputStatusEvent(
    RuntimeDeviceId DeviceId,
    GamepadCaptureStatus Status,
    string? Error,
    long Timestamp);

public enum RawDeviceKind : byte
{
    Mouse,
    Keyboard,
    Gamepad,
    Other,
    Unknown
}

public enum DeviceIdentityConfidence : byte
{
    SessionOnly,
    RequiresConfirmation,
    Unattributed
}

public sealed record InputDeviceDescriptor(
    RuntimeDeviceId RuntimeId,
    int ArrivalGeneration,
    RawDeviceKind Kind,
    string? InterfacePath,
    string? HardwareSummary,
    DeviceIdentityConfidence Confidence);

public enum DeviceChangeKind : byte
{
    Arrived,
    Removed
}

public readonly record struct DeviceChangeEvent(
    DeviceChangeKind Change,
    InputDeviceDescriptor Device,
    long Timestamp);

[Flags]
public enum PauseReason : byte
{
    None = 0,
    User = 1 << 0,
    SessionLocked = 1 << 1,
    SystemSuspend = 1 << 2
}

public enum AggregationBoundaryKind : byte
{
    Paused,
    DeviceRemoved,
    Stopping
}

public readonly record struct AggregationBoundaryEvent(
    AggregationBoundaryKind Boundary,
    RuntimeDeviceId DeviceId,
    PauseReason PauseReasons,
    long Timestamp);

public enum CapturedInputKind : byte
{
    Keyboard,
    Mouse,
    Gamepad,
    GamepadStatus,
    DeviceChange,
    AggregationBoundary
}

public readonly record struct CapturedInput
{
    private CapturedInput(
        CapturedInputKind kind,
        KeyboardPhysicalEvent keyboard,
        MouseRawEvent mouse,
        GamepadControlEvent gamepad,
        GamepadInputStatusEvent gamepadStatus,
        DeviceChangeEvent deviceChange,
        AggregationBoundaryEvent boundary)
    {
        Kind = kind;
        Keyboard = keyboard;
        Mouse = mouse;
        Gamepad = gamepad;
        GamepadStatus = gamepadStatus;
        DeviceChange = deviceChange;
        Boundary = boundary;
    }

    public CapturedInputKind Kind { get; }
    public KeyboardPhysicalEvent Keyboard { get; }
    public MouseRawEvent Mouse { get; }
    public GamepadControlEvent Gamepad { get; }
    public GamepadInputStatusEvent GamepadStatus { get; }
    public DeviceChangeEvent DeviceChange { get; }
    public AggregationBoundaryEvent Boundary { get; }

    public static CapturedInput FromKeyboard(KeyboardPhysicalEvent value) =>
        new(CapturedInputKind.Keyboard, value, default, default, default, default, default);

    public static CapturedInput FromMouse(MouseRawEvent value) =>
        new(CapturedInputKind.Mouse, default, value, default, default, default, default);

    public static CapturedInput FromGamepad(GamepadControlEvent value) =>
        new(CapturedInputKind.Gamepad, default, default, value, default, default, default);

    public static CapturedInput FromGamepadStatus(GamepadInputStatusEvent value) =>
        new(CapturedInputKind.GamepadStatus, default, default, default, value, default, default);

    public static CapturedInput FromDeviceChange(DeviceChangeEvent value) =>
        new(CapturedInputKind.DeviceChange, default, default, default, default, value, default);

    public static CapturedInput FromBoundary(AggregationBoundaryEvent value) =>
        new(CapturedInputKind.AggregationBoundary, default, default, default, default, default, value);
}

public interface IInputEventSink
{
    ValueTask WriteAsync(CapturedInput input, CancellationToken cancellationToken);
}

public sealed record InputPipelineDiagnostics(
    long Produced,
    long Accepted,
    long Dequeued,
    long SinkAcknowledged,
    long Overflow,
    long ParseErrors,
    long OutOfOrder,
    int QueueCapacity);
