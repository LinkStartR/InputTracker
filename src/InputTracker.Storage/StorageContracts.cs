namespace InputTracker.Storage;

public enum StorageDeviceKind
{
    Mouse = 0,
    Keyboard = 1
}

public enum StorageDeviceIdentityConfidence
{
    Unknown = 0,
    TransientInterface = 1,
    PersistentHardware = 2
}

[Flags]
public enum StorageDeviceIdentityAmbiguity
{
    None = 0,
    MissingPermanentIdentifier = 1,
    BluetoothTransport = 2,
    RemoteDesktop = 4,
    VirtualDevice = 8
}

public sealed record StorageDeviceObservation(
    StorageDeviceKind Kind,
    string InterfacePath,
    IReadOnlyList<string> HardwareIds,
    string? SerialNumber,
    Guid? ContainerId,
    bool IsBluetooth,
    bool IsRemoteDesktop,
    bool IsVirtual);

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

public readonly record struct StorageMouseTransition(
    Guid DeviceId,
    Guid CalibrationSessionId,
    long MonotonicTimestamp,
    int DeltaX,
    int DeltaY,
    byte MotionKind,
    bool UsesVirtualDesktop,
    ushort ButtonChanges,
    short VerticalWheelDelta,
    short HorizontalWheelDelta);

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
    IReadOnlyDictionary<Guid, long> MouseSequenceByDevice,
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
    Year = 3
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

public readonly record struct StorageMouseRecord(
    Guid DeviceId,
    long Sequence,
    long UtcMicroseconds,
    int DeltaX,
    int DeltaY,
    byte MotionKind,
    bool UsesVirtualDesktop,
    ushort ButtonChanges,
    short VerticalWheelDelta,
    short HorizontalWheelDelta);
