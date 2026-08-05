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

public enum RawDeviceKind : byte
{
    Mouse,
    Keyboard,
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
    DeviceChange,
    AggregationBoundary
}

public readonly record struct CapturedInput
{
    private CapturedInput(
        CapturedInputKind kind,
        KeyboardPhysicalEvent keyboard,
        MouseRawEvent mouse,
        DeviceChangeEvent deviceChange,
        AggregationBoundaryEvent boundary)
    {
        Kind = kind;
        Keyboard = keyboard;
        Mouse = mouse;
        DeviceChange = deviceChange;
        Boundary = boundary;
    }

    public CapturedInputKind Kind { get; }
    public KeyboardPhysicalEvent Keyboard { get; }
    public MouseRawEvent Mouse { get; }
    public DeviceChangeEvent DeviceChange { get; }
    public AggregationBoundaryEvent Boundary { get; }

    public static CapturedInput FromKeyboard(KeyboardPhysicalEvent value) =>
        new(CapturedInputKind.Keyboard, value, default, default, default);

    public static CapturedInput FromMouse(MouseRawEvent value) =>
        new(CapturedInputKind.Mouse, default, value, default, default);

    public static CapturedInput FromDeviceChange(DeviceChangeEvent value) =>
        new(CapturedInputKind.DeviceChange, default, default, value, default);

    public static CapturedInput FromBoundary(AggregationBoundaryEvent value) =>
        new(CapturedInputKind.AggregationBoundary, default, default, default, value);
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
