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

public enum StorageMousePlaybackBoundaryReason
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

public readonly record struct StorageMousePlaybackCursor(
    Guid SnapshotId,
    long Sequence,
    long UtcMicroseconds);

public sealed record StorageMousePlaybackSnapshotInfo(
    Guid SnapshotId,
    Guid DeviceId,
    StorageUtcRange UtcRange,
    long DurableWatermark,
    long RecordCount,
    StorageMousePlaybackCursor? First,
    StorageMousePlaybackCursor? Last);

/// <summary>
/// One exact mouse record in a playback snapshot. <see cref="BoundaryReason"/> is the
/// persisted reason for its containing spool segment; consumers treat it as a display
/// discontinuity only when <see cref="StartsNewSegment"/> is true.
/// </summary>
public readonly record struct StorageMousePlaybackRecord(
    StorageMouseRecord Record,
    bool StartsNewSegment,
    StorageMousePlaybackBoundaryReason BoundaryReason,
    StorageMousePlaybackCursor Cursor);
