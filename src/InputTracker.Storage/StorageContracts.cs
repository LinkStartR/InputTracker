namespace InputTracker.Storage;

public enum StorageDeviceKind
{
    Mouse = 0,
    Keyboard = 1,
    Gamepad = 2
}

public enum StorageDeviceIdentityConfidence
{
    Unknown = 0,
    TransientInterface = 1,
    PersistentHardware = 2,
    ReconnectableHardware = 3
}

[Flags]
public enum StorageDeviceIdentityAmbiguity
{
    None = 0,
    MissingPermanentIdentifier = 1,
    BluetoothTransport = 2,
    RemoteDesktop = 4,
    VirtualDevice = 8,
    IndeterminateTransport = 16,
    MultipleCandidates = 32
}

public sealed record StorageDeviceObservation(
    StorageDeviceKind Kind,
    string InterfacePath,
    IReadOnlyList<string> HardwareIds,
    string? SerialNumber,
    Guid? ContainerId,
    bool IsBluetooth,
    bool IsRemoteDesktop,
    bool IsVirtual,
    bool IsIndeterminate = false);

public sealed record StorageDeviceInfo(
    Guid DeviceId,
    StorageDeviceKind Kind,
    StorageDeviceIdentityConfidence IdentityConfidence,
    StorageDeviceIdentityAmbiguity IdentityAmbiguity,
    string? Alias,
    long CreatedUtcMicroseconds,
    long LastSeenUtcMicroseconds);

public sealed record StorageDeviceResolution(
    StorageDeviceInfo Device,
    bool ReusedExisting);

public enum StorageServiceState
{
    Created = 0,
    Running = 1,
    Paused = 2,
    IntegrityFault = 3,
    Stopped = 4,
    Disposed = 5
}

public enum StorageLifecycleBoundary
{
    Pause = 0,
    SessionLock = 1,
    Suspend = 2,
    DeviceRemoval = 3,
    CalibrationChange = 4,
    Stop = 5
}

[Flags]
public enum StoragePauseReason
{
    None = 0,
    User = 1,
    SessionLock = 2,
    Suspend = 4
}

public enum StorageKeyboardTransitionResult
{
    Accepted = 0,
    DuplicateDownIgnored = 1,
    OrphanUpIgnored = 2
}

public readonly record struct StorageKeyboardTransition(
    Guid DeviceId,
    ushort MakeCode,
    bool IsE0,
    bool IsE1,
    bool IsDown,
    long MonotonicTimestamp,
    Guid CalibrationSessionId);

public sealed record StorageIntegrityFault(
    string Code,
    string Summary,
    StorageLifecycleBoundary? Boundary);

public enum StorageDurabilityScope
{
    SQLiteCommitted = 0
}

public sealed record StorageDurableWatermark(
    long CommitSequence,
    long KeyboardAggregateCommitSequence,
    StorageDurabilityScope Scope);

public sealed record StorageServiceHealth(
    StorageServiceState State,
    bool AcceptingWrites,
    StorageDurableWatermark DurableWatermark,
    StorageIntegrityFault? IntegrityFault);

internal readonly record struct StorageMousePrefixCommit(
    Guid DeviceId,
    long DeviceSequence,
    long CommitOrdinal,
    long KeyboardAggregateCommitOrdinal);

public enum StoragePeriodKind
{
    Day = 0,
    Week = 1,
    Month = 2,
    Year = 3,
    Custom = 4
}

public readonly record struct StorageUtcRange(
    long StartUtcMicroseconds,
    long EndUtcMicroseconds)
{
    public StorageUtcRange Validate()
    {
        if (EndUtcMicroseconds <= StartUtcMicroseconds)
        {
            throw new ArgumentOutOfRangeException(nameof(EndUtcMicroseconds), "The UTC range must be non-empty and half-open.");
        }

        return this;
    }
}

public sealed record StorageResolvedPeriod(
    StoragePeriodKind Kind,
    DateOnly LocalStartDate,
    DateOnly LocalEndDate,
    string TimeZoneId,
    StorageUtcRange UtcRange);

public sealed record StorageKeyboardAggregate(
    Guid DeviceId,
    ushort MakeCode,
    bool IsE0,
    bool IsE1,
    DateOnly CaptureLocalDate,
    string TimeZoneId,
    long PressCount,
    long HoldDurationMicroseconds);

public sealed record StorageKeyboardPeriodAggregate(
    Guid DeviceId,
    ushort MakeCode,
    bool IsE0,
    bool IsE1,
    long PressCount,
    long HoldDurationMicroseconds);

public sealed record StorageKeyboardPeriodResult(
    StorageResolvedPeriod Period,
    IReadOnlyList<StorageKeyboardPeriodAggregate> Aggregates,
    IReadOnlyList<string> AvailableCaptureTimeZoneIds);

public enum StorageMouseButton
{
    Left = 0,
    Right = 1,
    Middle = 2,
    X1 = 3,
    X2 = 4
}

public sealed record StorageMouseButtonPeriodAggregate(
    Guid DeviceId,
    StorageMouseButton Button,
    long PressCount);

public sealed record StorageMouseButtonPeriodResult(
    StorageResolvedPeriod Period,
    IReadOnlyList<StorageMouseButtonPeriodAggregate> Aggregates,
    IReadOnlyList<string> AvailableCaptureTimeZoneIds);

public enum StorageGamepadLayout
{
    Xbox = 0
}

public enum StorageGamepadButton
{
    South = 0,
    East = 1,
    West = 2,
    North = 3,
    DpadUp = 4,
    DpadDown = 5,
    DpadLeft = 6,
    DpadRight = 7,
    LeftShoulder = 8,
    RightShoulder = 9,
    View = 10,
    Menu = 11,
    LeftThumb = 12,
    RightThumb = 13,
    LeftTrigger = 14,
    RightTrigger = 15
}

public enum StorageGamepadAxisDirection
{
    Increasing = 0,
    Decreasing = 1
}

public sealed record StorageGamepadHidBinding(
    StorageGamepadButton Button,
    ushort UsagePage,
    ushort Usage,
    ushort LinkCollection,
    ushort ElementIndex,
    IReadOnlyList<int> ActiveValues,
    int? ActivationThreshold = null,
    int? ReleaseThreshold = null,
    StorageGamepadAxisDirection? AxisDirection = null);

public sealed record StorageGamepadConfiguration(
    Guid DeviceId,
    int Revision,
    StorageGamepadLayout Layout,
    bool Enabled,
    IReadOnlyList<StorageGamepadHidBinding> Bindings);

public readonly record struct StorageGamepadPress(
    Guid DeviceId,
    StorageGamepadButton Button,
    long MonotonicTimestamp,
    Guid CalibrationSessionId);

public sealed record StorageGamepadButtonPeriodAggregate(
    Guid DeviceId,
    int ConfigurationRevision,
    StorageGamepadButton Button,
    long PressCount);

public sealed record StorageGamepadButtonPeriodResult(
    StorageResolvedPeriod Period,
    IReadOnlyList<StorageGamepadButtonPeriodAggregate> Aggregates,
    IReadOnlyList<string> AvailableCaptureTimeZoneIds);

public readonly record struct StorageMouseButtonTransition(
    Guid DeviceId,
    Guid CalibrationSessionId,
    long MonotonicTimestamp,
    ushort ButtonChanges);

internal enum LegacyMouseSegmentBoundaryReason
{
    RangeStart = 0,
    DurationLimit = 1,
    EventLimit = 2,
    CanonicalSizeLimit = 3,
    DeviceBoundary = 4,
    CalibrationBoundary = 5,
    PauseBoundary = 6,
    ExplicitFlush = 7,
    LegacyUnknown = 8,
    RecoveryUnknown = 9
}
