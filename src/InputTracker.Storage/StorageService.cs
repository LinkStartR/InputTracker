namespace InputTracker.Storage;

internal interface IStorageServiceFaultInjector
{
    void BeforeStorageCommit(StorageLifecycleBoundary? boundary, int keyboardRowCount);
}

internal sealed class NoStorageServiceFaultInjector : IStorageServiceFaultInjector
{
    public static NoStorageServiceFaultInjector Instance { get; } = new();

    public void BeforeStorageCommit(StorageLifecycleBoundary? boundary, int keyboardRowCount)
    {
    }
}

public sealed class StorageService : IDisposable
{
    private const ushort KnownMouseButtonChanges = 0x03ff;
    private const ushort MouseButtonDownChanges = 0x0155;
    internal const int MaxPendingTransitions = 32;
    internal const int MaxPendingRows = 64;
    internal const int MaxBatchWindowMilliseconds = 250;
    private const int BatchTimerPeriodMilliseconds = 25;

    private static readonly object OwnersGate = new();
    private static readonly HashSet<string> OwnedStores = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly string _ownerKey;
    private readonly FileStream _ownerLock;
    private readonly StorageDatabase _database;
    private readonly IStorageClock _clock;
    private readonly IStorageServiceFaultInjector _faultInjector;
    private readonly Timer? _batchTimer;
    private readonly Dictionary<StorageKeyboardKey, StorageKeyboardHold> _openHolds = [];
    private readonly Dictionary<StorageKeyboardAggregateKey, StorageKeyboardAggregateDelta> _pending = [];
    private readonly Dictionary<Guid, int> _pendingTransitionsByDevice = [];
    private readonly Dictionary<Guid, long> _batchStartByDevice = [];

    private StorageServiceState _state = StorageServiceState.Created;
    private StoragePauseReason _pauseReasons;
    private StorageIntegrityFault? _integrityFault;
    private StorageServiceOrdinals _ordinals;
    private TimeCalibrationSegment? _activeCalibration;
    private long _lastMonotonicTimestamp;
    private bool _acceptingWrites;
    private bool _disposed;

    private StorageService(
        string ownerKey,
        FileStream ownerLock,
        StorageDatabase database,
        IStorageClock clock,
        IStorageServiceFaultInjector faultInjector,
        bool startTimer)
    {
        _ownerKey = ownerKey;
        _ownerLock = ownerLock;
        _database = database;
        _clock = clock;
        _faultInjector = faultInjector;
        _ordinals = database.ReadStorageServiceOrdinals();
        if (startTimer)
        {
            _batchTimer = new Timer(
                static state => ((StorageService)state!).OnBatchTimer(),
                this,
                BatchTimerPeriodMilliseconds,
                BatchTimerPeriodMilliseconds);
        }
    }

    public static StorageService Open(StorageLayout layout) => OpenCore(
        layout,
        SystemStorageClock.Instance,
        NoStorageServiceFaultInjector.Instance,
        startTimer: true);

    internal static StorageService OpenForTests(
        StorageLayout layout,
        IStorageClock clock,
        IStorageServiceFaultInjector? faultInjector = null,
        bool startTimer = false) => OpenCore(
            layout,
            clock,
            faultInjector ?? NoStorageServiceFaultInjector.Instance,
            startTimer);

    public StorageServiceHealth Health
    {
        get
        {
            lock (_gate)
            {
                return new StorageServiceHealth(
                    _state,
                    _acceptingWrites,
                    new StorageDurableWatermark(
                        _ordinals.CommitOrdinal,
                        _ordinals.KeyboardAggregateCommitOrdinal,
                        StorageDurabilityScope.SQLiteCommitted),
                    _integrityFault);
            }
        }
    }

    internal string OwnerLockPath => _ownerLock.Name;

    public StorageDeviceResolution ObserveDevice(StorageDeviceObservation observation)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            EnsureDeviceCatalogWritable();
            try
            {
                return _database.ObserveDevice(
                    observation,
                    checked((_clock.GetUtcNow().UtcDateTime.Ticks - 621355968000000000L) / 10));
            }
            catch (Exception error) when (IsControlledFailure(error))
            {
                EnterFault("storage.device-observation-failed", "Device observation failed closed.", null);
                throw;
            }
        }
    }

    public IReadOnlyList<StorageDeviceInfo> ListDevices()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return _database.ListDevices();
        }
    }

    public StorageDeviceInfo SetDeviceAlias(Guid deviceId, string? alias)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            EnsureDeviceCatalogWritable();
            try
            {
                return _database.SetDeviceAlias(deviceId, alias);
            }
            catch (Exception error) when (IsControlledFailure(error))
            {
                EnterFault("storage.device-alias-failed", "Device alias update failed closed.", null);
                throw;
            }
        }
    }

    public StorageKeyboardPeriodResult QueryKeyboardPeriod(
        StoragePeriodKind kind,
        DateOnly containingDate,
        string timeZoneId,
        Guid? deviceId = null)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return StorageQuery.QueryKeyboardPeriod(
                _database.Connection,
                kind,
                containingDate,
                timeZoneId,
                deviceId);
        }
    }

    public StorageKeyboardPeriodResult QueryKeyboardRange(
        DateOnly inclusiveStartDate,
        DateOnly inclusiveEndDate,
        string timeZoneId,
        Guid? deviceId = null)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return StorageQuery.QueryKeyboardRange(
                _database.Connection,
                inclusiveStartDate,
                inclusiveEndDate,
                timeZoneId,
                deviceId);
        }
    }

    public StorageMouseButtonPeriodResult QueryMouseButtonsPeriod(
        StoragePeriodKind kind,
        DateOnly containingDate,
        string timeZoneId,
        Guid? deviceId = null)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return StorageQuery.QueryMouseButtonsPeriod(
                _database.Connection,
                kind,
                containingDate,
                timeZoneId,
                deviceId);
        }
    }

    public StorageMouseButtonPeriodResult QueryMouseButtonsRange(
        DateOnly inclusiveStartDate,
        DateOnly inclusiveEndDate,
        string timeZoneId,
        Guid? deviceId = null)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return StorageQuery.QueryMouseButtonsRange(
                _database.Connection,
                inclusiveStartDate,
                inclusiveEndDate,
                timeZoneId,
                deviceId);
        }
    }

    public StorageExportArtifact ExportKeyboardCsv(
        StorageKeyboardPeriodResult result,
        string relativePath)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            IReadOnlyDictionary<Guid, string?> aliases = _database.ListDevices()
                .Where(device => device.Kind == StorageDeviceKind.Keyboard)
                .ToDictionary(device => device.DeviceId, device => device.Alias);
            return StorageExport.WriteKeyboardCsv(
                _database.Layout, result, aliases, relativePath);
        }
    }

    public StorageExportArtifact ExportKeyboardHeatmapPng(
        StorageKeyboardPeriodResult result,
        string relativePath,
        int width,
        int height)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return StorageExport.WriteKeyboardHeatmapPng(
                _database.Layout, result, relativePath, width, height);
        }
    }

    public StorageBackupArtifact CreateBackup(string relativePath)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_state is not StorageServiceState.Created
                and not StorageServiceState.Paused
                and not StorageServiceState.Stopped
                || _pending.Count != 0
                || _openHolds.Count != 0)
            {
                throw new InvalidOperationException(
                    "Backup requires Created, Paused, or Stopped storage with no buffered input.");
            }
            return StorageBackup.Create(_database, relativePath);
        }
    }

    public Task<StorageDeletionPreview> PreviewDeletionAsync(
        Guid deviceId,
        StorageUtcRange utcRange,
        CancellationToken cancellationToken = default)
    {
        utcRange.Validate();
        StorageDeletionImpact impact;
        StorageDurableWatermark watermark;
        lock (_gate)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            impact = StorageDeletion.DescribeImpact(_database.Connection, deviceId, utcRange);
            watermark = Health.DurableWatermark;
        }

        string fingerprint = StorageDeletion.CreateStateFingerprint(
            deviceId, utcRange, impact, watermark);
        StorageDeletionCapability capability = impact.HasPartialDayOverlap || impact.AggregateCount == 0
            ? StorageDeletionCapability.BlockedByCurrentFormat
            : impact.Kind == StorageDeviceKind.Keyboard
                ? StorageDeletionCapability.ExactKeyboardDailyAggregates
                : StorageDeletionCapability.ExactMouseButtonDailyAggregates;
        var preview = new StorageDeletionPreview(
            deviceId,
            utcRange,
            impact.Kind == StorageDeviceKind.Keyboard ? impact.AggregateCount : 0,
            impact.Kind == StorageDeviceKind.Keyboard ? impact.PressCount : 0,
            impact.Kind == StorageDeviceKind.Keyboard ? impact.HoldDurationMicroseconds : 0,
            impact.Kind == StorageDeviceKind.Mouse ? impact.AggregateCount : 0,
            impact.Kind == StorageDeviceKind.Mouse ? impact.PressCount : 0,
            capability,
            impact.HasPartialDayOverlap
                ? "The selected UTC range cuts through a stored local-day aggregate; InputTracker will not estimate a partial deletion."
                : impact.AggregateCount == 0
                    ? "The selected range contains no matching daily aggregate records."
                    : null,
            StorageDeletion.CreateToken(fingerprint),
            fingerprint);
        return Task.FromResult(preview);
    }

    public Task<StorageDeletionBatchPreview> PreviewDeletionBatchAsync(
        Guid keyboardDeviceId,
        Guid mouseDeviceId,
        StorageUtcRange utcRange,
        CancellationToken cancellationToken = default)
    {
        utcRange.Validate();
        if (keyboardDeviceId == Guid.Empty || mouseDeviceId == Guid.Empty || keyboardDeviceId == mouseDeviceId)
            throw new ArgumentException("Combined deletion requires distinct keyboard and mouse device identifiers.");

        StorageDeletionImpact keyboard;
        StorageDeletionImpact mouse;
        StorageDurableWatermark watermark;
        lock (_gate)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            keyboard = StorageDeletion.DescribeImpact(_database.Connection, keyboardDeviceId, utcRange);
            mouse = StorageDeletion.DescribeImpact(_database.Connection, mouseDeviceId, utcRange);
            if (keyboard.Kind != StorageDeviceKind.Keyboard || mouse.Kind != StorageDeviceKind.Mouse)
                throw new InvalidDataException("Combined deletion targets must be one keyboard and one mouse.");
            watermark = Health.DurableWatermark;
        }

        string fingerprint = StorageDeletion.CreateBatchStateFingerprint(
            keyboardDeviceId, keyboard, mouseDeviceId, mouse, utcRange, watermark);
        bool partial = keyboard.HasPartialDayOverlap || mouse.HasPartialDayOverlap;
        bool empty = keyboard.AggregateCount + mouse.AggregateCount == 0;
        var preview = new StorageDeletionBatchPreview(
            keyboardDeviceId,
            mouseDeviceId,
            utcRange,
            keyboard.AggregateCount,
            keyboard.PressCount,
            keyboard.HoldDurationMicroseconds,
            mouse.AggregateCount,
            mouse.PressCount,
            partial || empty
                ? StorageDeletionCapability.BlockedByCurrentFormat
                : StorageDeletionCapability.ExactKeyboardAndMouseDailyAggregates,
            partial
                ? "The selected UTC range cuts through a stored local-day aggregate; InputTracker will not estimate a partial deletion."
                : empty ? "The selected range contains no matching daily aggregate records." : null,
            StorageDeletion.CreateToken(fingerprint),
            fingerprint);
        return Task.FromResult(preview);
    }

    public StorageDeletionResult ExecuteDeletion(
        StorageDeletionPreview preview,
        string confirmationToken,
        string preDeletionBackupRelativePath)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(preDeletionBackupRelativePath);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_state is not StorageServiceState.Created
                and not StorageServiceState.Paused
                and not StorageServiceState.Stopped
                || _acceptingWrites
                || _pending.Count != 0
                || _openHolds.Count != 0)
            {
                throw new InvalidOperationException(
                    "Deletion requires Created, Paused, or Stopped storage with no buffered input.");
            }
            string expected = StorageDeletion.CreateToken(preview.StateFingerprint);
            if (!StorageDeletion.FixedTimeTokenEquals(confirmationToken, expected))
            {
                throw new InvalidOperationException("Deletion confirmation token does not match its preview.");
            }
            if (preview.Capability is not StorageDeletionCapability.ExactKeyboardDailyAggregates
                and not StorageDeletionCapability.ExactMouseButtonDailyAggregates)
            {
                throw new InvalidOperationException(
                    preview.BlockedReason ?? "Deletion is blocked by the current storage format.");
            }

            StorageDeletionImpact current = StorageDeletion.DescribeImpact(
                _database.Connection, preview.DeviceId, preview.UtcRange);
            StorageDurableWatermark watermark = new(
                _ordinals.CommitOrdinal,
                _ordinals.KeyboardAggregateCommitOrdinal,
                StorageDurabilityScope.SQLiteCommitted);
            string currentFingerprint = StorageDeletion.CreateStateFingerprint(
                preview.DeviceId, preview.UtcRange, current, watermark);
            if (!StorageDeletion.FixedTimeTokenEquals(currentFingerprint, preview.StateFingerprint)
                || current.HasPartialDayOverlap
                || current.KeyboardRows.Count != preview.KeyboardAggregateCount
                || (current.Kind == StorageDeviceKind.Keyboard ? current.PressCount : 0) != preview.KeyboardPressCount
                || current.HoldDurationMicroseconds != preview.KeyboardHoldDurationMicroseconds
                || current.MouseButtonRows.Count != preview.MouseButtonAggregateCount
                || (current.Kind == StorageDeviceKind.Mouse ? current.PressCount : 0) != preview.MouseButtonPressCount
                || (current.Kind == StorageDeviceKind.Keyboard
                    && preview.Capability != StorageDeletionCapability.ExactKeyboardDailyAggregates)
                || (current.Kind == StorageDeviceKind.Mouse
                    && preview.Capability != StorageDeletionCapability.ExactMouseButtonDailyAggregates))
            {
                throw new InvalidOperationException(
                    "Stored data changed after the deletion preview; review the range again before deleting.");
            }
            if (current.AggregateCount == 0)
            {
                throw new InvalidOperationException("The selected range has no records to delete.");
            }

            StorageBackupArtifact backup = StorageBackup.Create(_database, preDeletionBackupRelativePath);
            long deleted = StorageDeletion.DeleteRows(_database.Connection, preview.DeviceId, current);
            return new StorageDeletionResult(
                preview.DeviceId,
                preview.UtcRange,
                current.Kind == StorageDeviceKind.Keyboard ? deleted : 0,
                current.Kind == StorageDeviceKind.Keyboard ? current.PressCount : 0,
                current.Kind == StorageDeviceKind.Keyboard ? current.HoldDurationMicroseconds : 0,
                current.Kind == StorageDeviceKind.Mouse ? deleted : 0,
                current.Kind == StorageDeviceKind.Mouse ? current.PressCount : 0,
                backup);
        }
    }

    public StorageDeletionBatchResult ExecuteDeletionBatch(
        StorageDeletionBatchPreview preview,
        string confirmationToken,
        string preDeletionBackupRelativePath)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmationToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(preDeletionBackupRelativePath);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_state is not StorageServiceState.Created
                and not StorageServiceState.Paused
                and not StorageServiceState.Stopped
                || _acceptingWrites
                || _pending.Count != 0
                || _openHolds.Count != 0)
            {
                throw new InvalidOperationException(
                    "Deletion requires Created, Paused, or Stopped storage with no buffered input.");
            }
            string expected = StorageDeletion.CreateToken(preview.StateFingerprint);
            if (!StorageDeletion.FixedTimeTokenEquals(confirmationToken, expected))
                throw new InvalidOperationException("Deletion confirmation token does not match its preview.");
            if (preview.Capability != StorageDeletionCapability.ExactKeyboardAndMouseDailyAggregates)
                throw new InvalidOperationException(preview.BlockedReason ?? "Combined deletion is blocked.");

            StorageDeletionImpact keyboard = StorageDeletion.DescribeImpact(
                _database.Connection, preview.KeyboardDeviceId, preview.UtcRange);
            StorageDeletionImpact mouse = StorageDeletion.DescribeImpact(
                _database.Connection, preview.MouseDeviceId, preview.UtcRange);
            StorageDurableWatermark watermark = new(
                _ordinals.CommitOrdinal,
                _ordinals.KeyboardAggregateCommitOrdinal,
                StorageDurabilityScope.SQLiteCommitted);
            string currentFingerprint = StorageDeletion.CreateBatchStateFingerprint(
                preview.KeyboardDeviceId, keyboard,
                preview.MouseDeviceId, mouse,
                preview.UtcRange, watermark);
            if (keyboard.Kind != StorageDeviceKind.Keyboard
                || mouse.Kind != StorageDeviceKind.Mouse
                || keyboard.HasPartialDayOverlap
                || mouse.HasPartialDayOverlap
                || !StorageDeletion.FixedTimeTokenEquals(currentFingerprint, preview.StateFingerprint)
                || keyboard.AggregateCount != preview.KeyboardAggregateCount
                || keyboard.PressCount != preview.KeyboardPressCount
                || keyboard.HoldDurationMicroseconds != preview.KeyboardHoldDurationMicroseconds
                || mouse.AggregateCount != preview.MouseButtonAggregateCount
                || mouse.PressCount != preview.MouseButtonPressCount
                || keyboard.AggregateCount + mouse.AggregateCount == 0)
            {
                throw new InvalidOperationException(
                    "Stored data changed after the combined deletion preview; review the range again before deleting.");
            }

            StorageBackupArtifact backup = StorageBackup.Create(_database, preDeletionBackupRelativePath);
            _ = StorageDeletion.DeleteRowsBatch(
                _database.Connection,
                [(preview.KeyboardDeviceId, keyboard), (preview.MouseDeviceId, mouse)]);
            return new StorageDeletionBatchResult(
                preview.KeyboardDeviceId,
                preview.MouseDeviceId,
                preview.UtcRange,
                keyboard.AggregateCount,
                keyboard.PressCount,
                keyboard.HoldDurationMicroseconds,
                mouse.AggregateCount,
                mouse.PressCount,
                backup);
        }
    }

    public void ExecuteDeletion(StorageDeletionPreview preview, string confirmationToken)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmationToken);
        lock (_gate)
        {
            ThrowIfDisposed();
            string expected = StorageDeletion.CreateToken(preview.StateFingerprint);
            if (!StorageDeletion.FixedTimeTokenEquals(confirmationToken, expected))
            {
                throw new InvalidOperationException("Deletion confirmation token does not match its preview.");
            }
            throw new InvalidOperationException(
                "Deletion requires an explicit program-directory backup path before execution.");
        }
    }

    public void Start(TimeCalibrationSegment calibration)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_state != StorageServiceState.Created)
            {
                throw new InvalidOperationException("Storage service can only start from Created state.");
            }

            try
            {
                ValidateCalibrationForClock(calibration);
                _ordinals = _database.CommitStorageServiceBatch(
                    [],
                    calibration,
                    boundary: null,
                    _faultInjector);
                _activeCalibration = calibration;
                _lastMonotonicTimestamp = calibration.MonotonicAnchor;
                _state = StorageServiceState.Running;
                _acceptingWrites = true;
            }
            catch (Exception error) when (IsControlledFailure(error))
            {
                EnterFault("storage.start-failed", "Storage start or calibration commit failed.", boundary: null);
                throw;
            }
        }
    }

    public StorageKeyboardTransitionResult WriteKeyboardTransition(StorageKeyboardTransition transition)
    {
        lock (_gate)
        {
            EnsureRunningAndAccepting();
            ValidateTransition(transition);
            TrackTimestamp(transition.MonotonicTimestamp, boundary: null);
            var key = new StorageKeyboardKey(
                transition.DeviceId,
                transition.MakeCode,
                transition.IsE0,
                transition.IsE1);
            if (transition.IsDown)
            {
                if (_openHolds.ContainsKey(key))
                {
                    return StorageKeyboardTransitionResult.DuplicateDownIgnored;
                }

                TimeCalibrationSegment calibration = _activeCalibration!.Value;
                DateOnly pressDate;
                try
                {
                    pressDate = StorageKeyboardTime.GetLocalDate(calibration, transition.MonotonicTimestamp);
                }
                catch (Exception error) when (IsControlledFailure(error))
                {
                    EnterFault("keyboard.time-zone-failed", "Keyboard down could not be mapped to a local date.", null);
                    throw;
                }

                _openHolds.Add(key, new StorageKeyboardHold(key, transition.MonotonicTimestamp, calibration));
                AddPending(new StorageKeyboardAggregateDelta(
                    key.DeviceId,
                    key.MakeCode,
                    key.IsE0,
                    key.IsE1,
                    pressDate,
                    calibration.TimeZoneId,
                    PressCount: 1,
                    HoldDurationMicroseconds: 0));
                RecordPendingTransition(key.DeviceId, transition.MonotonicTimestamp);
                CommitIfDue(transition.MonotonicTimestamp);
                return StorageKeyboardTransitionResult.Accepted;
            }

            if (!_openHolds.TryGetValue(key, out StorageKeyboardHold? hold))
            {
                return StorageKeyboardTransitionResult.OrphanUpIgnored;
            }

            bool rowLimitTriggered = false;
            IEnumerable<StorageKeyboardAggregateDelta> duration = SplitHold(
                hold,
                transition.MonotonicTimestamp,
                null);
            foreach (StorageKeyboardAggregateDelta delta in duration)
            {
                if (!_pending.ContainsKey(delta.Key) && _pending.Count == MaxPendingRows)
                {
                    CommitScope(
                        transition.MonotonicTimestamp,
                        static _ => false,
                        static _ => true,
                        closeHolds: false,
                        boundary: null,
                        calibration: null);
                    rowLimitTriggered = true;
                }

                AddPending(delta);
            }

            _openHolds.Remove(key);
            RecordPendingTransition(key.DeviceId, transition.MonotonicTimestamp);
            if (rowLimitTriggered)
            {
                CommitScope(
                    transition.MonotonicTimestamp,
                    static _ => false,
                    static _ => true,
                    closeHolds: false,
                    boundary: null,
                    calibration: null);
            }
            else
            {
                CommitIfDue(transition.MonotonicTimestamp);
            }

            return StorageKeyboardTransitionResult.Accepted;
        }
    }

    public void Checkpoint(long monotonicTimestamp)
    {
        lock (_gate)
        {
            EnsureRunningAndAccepting();
            TrackTimestamp(monotonicTimestamp, boundary: null);
            CommitScope(
                monotonicTimestamp,
                static _ => true,
                static _ => true,
                closeHolds: false,
                boundary: null,
                calibration: null);
        }
    }

    public void Pause(StoragePauseReason reasons, long monotonicTimestamp)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            ValidatePauseReasons(reasons);
            if (_state is not StorageServiceState.Running and not StorageServiceState.Paused)
            {
                throw new InvalidOperationException("Storage service cannot pause from its current state.");
            }

            TrackTimestamp(monotonicTimestamp, GetPauseBoundary(reasons));
            StoragePauseReason added = reasons & ~_pauseReasons;
            if (added == StoragePauseReason.None)
            {
                return;
            }

            if (_pauseReasons == StoragePauseReason.None)
            {
                _acceptingWrites = false;
                CommitScope(
                    monotonicTimestamp,
                    static _ => true,
                    static _ => true,
                    closeHolds: true,
                    boundary: GetPauseBoundary(added),
                    calibration: null);
                _state = StorageServiceState.Paused;
            }

            _pauseReasons |= added;
        }
    }

    public void Resume(StoragePauseReason reasons)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            ValidatePauseReasons(reasons);
            if (_state != StorageServiceState.Paused)
            {
                throw new InvalidOperationException("Storage service can only resume from Paused state.");
            }

            _pauseReasons &= ~reasons;
            if (_pauseReasons == StoragePauseReason.None)
            {
                _state = StorageServiceState.Running;
                _acceptingWrites = true;
            }
        }
    }

    public void RemoveDevice(Guid deviceId, long monotonicTimestamp)
    {
        lock (_gate)
        {
            EnsureBoundaryState();
            if (deviceId == Guid.Empty)
            {
                Fail("keyboard.device-boundary-invalid", "Device removal has an empty device identifier.", StorageLifecycleBoundary.DeviceRemoval);
            }

            TrackTimestamp(monotonicTimestamp, StorageLifecycleBoundary.DeviceRemoval);
            bool restoreAccepting = _state == StorageServiceState.Running;
            _acceptingWrites = false;
            CommitScope(
                monotonicTimestamp,
                hold => hold.Key.DeviceId == deviceId,
                key => key.DeviceId == deviceId,
                closeHolds: true,
                boundary: StorageLifecycleBoundary.DeviceRemoval,
                calibration: null);
            _acceptingWrites = restoreAccepting;
        }
    }

    public void ChangeCalibration(long monotonicTimestamp, TimeCalibrationSegment calibration)
    {
        lock (_gate)
        {
            EnsureBoundaryState();
            StorageServiceState previousState = _state;
            try
            {
                ValidateCalibrationForClock(calibration);
                if (calibration.MonotonicAnchor < monotonicTimestamp)
                {
                    Fail(
                        "storage.calibration-order-invalid",
                        "The replacement calibration precedes its boundary.",
                        StorageLifecycleBoundary.CalibrationChange);
                }

                TrackTimestamp(monotonicTimestamp, StorageLifecycleBoundary.CalibrationChange);
                _acceptingWrites = false;
                CommitScope(
                    monotonicTimestamp,
                    static _ => true,
                    static _ => true,
                    closeHolds: true,
                    boundary: StorageLifecycleBoundary.CalibrationChange,
                    calibration: calibration);
                _activeCalibration = calibration;
                _lastMonotonicTimestamp = calibration.MonotonicAnchor;
                _state = previousState;
                _acceptingWrites = previousState == StorageServiceState.Running;
            }
            catch (Exception error) when (IsControlledFailure(error) && _state != StorageServiceState.IntegrityFault)
            {
                EnterFault(
                    "storage.calibration-change-failed",
                    "Calibration change failed closed.",
                    StorageLifecycleBoundary.CalibrationChange);
                throw;
            }
        }
    }

    public void Stop(long monotonicTimestamp)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_state == StorageServiceState.Created)
            {
                _state = StorageServiceState.Stopped;
                return;
            }

            EnsureBoundaryState();
            TrackTimestamp(monotonicTimestamp, StorageLifecycleBoundary.Stop);
            _acceptingWrites = false;
            CommitScope(
                monotonicTimestamp,
                static _ => true,
                static _ => true,
                closeHolds: true,
                boundary: StorageLifecycleBoundary.Stop,
                calibration: null);
            _pauseReasons = StoragePauseReason.None;
            _state = StorageServiceState.Stopped;
        }
    }

    internal void RunBatchTimerForTests() => OnBatchTimer();

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _acceptingWrites = false;
            _state = StorageServiceState.Disposed;
            _openHolds.Clear();
            _pending.Clear();
            _pendingTransitionsByDevice.Clear();
            _batchStartByDevice.Clear();
            _batchTimer?.Dispose();
            try
            {
                _database.Dispose();
            }
            finally
            {
                _ownerLock.Dispose();
                lock (OwnersGate)
                {
                    OwnedStores.Remove(_ownerKey);
                }
            }
        }
    }

    private static StorageService OpenCore(
        StorageLayout layout,
        IStorageClock clock,
        IStorageServiceFaultInjector faultInjector,
        bool startTimer)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(faultInjector);
        string ownerKey = Path.GetFullPath(layout.DatabasePath);
        lock (OwnersGate)
        {
            if (!OwnedStores.Add(ownerKey))
            {
                throw new InvalidOperationException("This storage database already has a StorageService owner in this process.");
            }
        }

        FileStream? ownerLock = null;
        StorageDatabase? database = null;
        try
        {
            string lockPath = Path.Combine(layout.StorageDirectory, "storage-service.writer.lock");
            ownerLock = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.Read,
                1,
                FileOptions.WriteThrough);
            StorageBackup.RecoverInterruptedRestore(layout, ownerLock);
            database = StorageDatabase.OpenOwned(layout, clock, ownerLock);
            return new StorageService(ownerKey, ownerLock, database, clock, faultInjector, startTimer);
        }
        catch
        {
            database?.Dispose();
            ownerLock?.Dispose();
            lock (OwnersGate)
            {
                OwnedStores.Remove(ownerKey);
            }

            throw;
        }
    }

    private void CommitIfDue(long monotonicTimestamp)
    {
        int transitions = _pendingTransitionsByDevice.Values.Sum();
        bool transitionLimit = transitions >= MaxPendingTransitions;
        bool rowLimit = _pending.Count >= MaxPendingRows;
        bool timeLimit = _batchStartByDevice.Values.Any(start =>
            monotonicTimestamp - start >= GetBatchWindowTicks(_activeCalibration!.Value.Frequency));
        if (transitionLimit || rowLimit || timeLimit)
        {
            CommitScope(
                monotonicTimestamp,
                static _ => true,
                static _ => true,
                closeHolds: false,
                boundary: null,
                calibration: null);
        }
    }

    private void CommitScope(
        long monotonicTimestamp,
        Func<StorageKeyboardHold, bool> holdPredicate,
        Func<StorageKeyboardAggregateKey, bool> pendingPredicate,
        bool closeHolds,
        StorageLifecycleBoundary? boundary,
        TimeCalibrationSegment? calibration)
    {
        StorageKeyboardHold[] holds = _openHolds.Values.Where(holdPredicate).ToArray();
        var batch = new Dictionary<StorageKeyboardAggregateKey, StorageKeyboardAggregateDelta>();
        var affectedDevices = new HashSet<Guid>(holds.Select(hold => hold.Key.DeviceId));
        foreach ((StorageKeyboardAggregateKey key, StorageKeyboardAggregateDelta delta) in _pending)
        {
            if (pendingPredicate(key))
            {
                affectedDevices.Add(key.DeviceId);
                AddBoundedBatchDelta(batch, delta, boundary);
            }
        }

        foreach (StorageKeyboardHold hold in holds)
        {
            foreach (StorageKeyboardAggregateDelta delta in SplitHold(hold, monotonicTimestamp, boundary))
            {
                AddBoundedBatchDelta(batch, delta, boundary);
            }
        }

        CommitKeyboardBatch(batch, calibration, boundary);

        foreach (StorageKeyboardAggregateKey key in _pending.Keys.Where(pendingPredicate).ToArray())
        {
            _pending.Remove(key);
        }

        foreach (StorageKeyboardHold hold in holds)
        {
            if (closeHolds)
            {
                _openHolds.Remove(hold.Key);
            }
            else
            {
                _openHolds[hold.Key] = hold with { CursorMonotonicTimestamp = monotonicTimestamp };
            }
        }

        foreach (Guid deviceId in affectedDevices)
        {
            _pendingTransitionsByDevice.Remove(deviceId);
            _batchStartByDevice.Remove(deviceId);
        }
    }

    private IEnumerable<StorageKeyboardAggregateDelta> SplitHold(
        StorageKeyboardHold hold,
        long monotonicTimestamp,
        StorageLifecycleBoundary? boundary)
    {
        using IEnumerator<StorageKeyboardAggregateDelta> enumerator =
            StorageKeyboardTime.SplitDuration(hold, monotonicTimestamp).GetEnumerator();
        while (true)
        {
            bool hasNext;
            try
            {
                hasNext = enumerator.MoveNext();
            }
            catch (Exception error) when (IsControlledFailure(error))
            {
                EnterFault("keyboard.duration-split-failed", "Keyboard duration split failed closed.", boundary);
                throw;
            }

            if (!hasNext)
            {
                yield break;
            }

            yield return enumerator.Current;
        }
    }

    private void AddBoundedBatchDelta(
        Dictionary<StorageKeyboardAggregateKey, StorageKeyboardAggregateDelta> batch,
        StorageKeyboardAggregateDelta delta,
        StorageLifecycleBoundary? boundary)
    {
        if (!batch.ContainsKey(delta.Key) && batch.Count == MaxPendingRows)
        {
            CommitKeyboardBatch(batch, calibration: null, boundary);
            batch.Clear();
        }

        AddDelta(batch, delta);
    }

    private void CommitKeyboardBatch(
        Dictionary<StorageKeyboardAggregateKey, StorageKeyboardAggregateDelta> batch,
        TimeCalibrationSegment? calibration,
        StorageLifecycleBoundary? boundary)
    {
        try
        {
            if (batch.Count > 0 || calibration is not null)
            {
                _ordinals = _database.CommitStorageServiceBatch(
                    batch.Values.ToArray(),
                    calibration,
                    boundary,
                    _faultInjector);
            }
        }
        catch (Exception error) when (IsControlledFailure(error))
        {
            EnterFault("storage.keyboard-commit-failed", "Keyboard aggregate commit failed closed.", boundary);
            throw;
        }
    }

    private void AddPending(StorageKeyboardAggregateDelta delta) => AddDelta(_pending, delta);

    private static void AddDelta(
        Dictionary<StorageKeyboardAggregateKey, StorageKeyboardAggregateDelta> target,
        StorageKeyboardAggregateDelta delta)
    {
        if (target.TryGetValue(delta.Key, out StorageKeyboardAggregateDelta existing))
        {
            target[delta.Key] = existing with
            {
                PressCount = checked(existing.PressCount + delta.PressCount),
                HoldDurationMicroseconds = checked(
                    existing.HoldDurationMicroseconds + delta.HoldDurationMicroseconds)
            };
        }
        else
        {
            target.Add(delta.Key, delta);
        }
    }

    private void RecordPendingTransition(Guid deviceId, long monotonicTimestamp)
    {
        _pendingTransitionsByDevice.TryGetValue(deviceId, out int count);
        _pendingTransitionsByDevice[deviceId] = checked(count + 1);
        _batchStartByDevice.TryAdd(deviceId, monotonicTimestamp);
    }

    private void OnBatchTimer()
    {
        lock (_gate)
        {
            if (_disposed
                || _state != StorageServiceState.Running
                || !_acceptingWrites
                || _batchStartByDevice.Count == 0)
            {
                return;
            }

            try
            {
                long timestamp = _clock.GetTimestamp();
                if (_batchStartByDevice.Values.Any(start =>
                    timestamp - start >= GetBatchWindowTicks(_activeCalibration!.Value.Frequency)))
                {
                    CommitScope(
                        timestamp,
                        static _ => false,
                        static _ => true,
                        closeHolds: false,
                        boundary: null,
                        calibration: null);
                }
            }
            catch (Exception error) when (IsControlledFailure(error))
            {
                if (_state != StorageServiceState.IntegrityFault)
                {
                    EnterFault("storage.batch-timer-failed", "Keyboard batch timer failed closed.", null);
                }
            }
        }
    }

    private void ValidateTransition(StorageKeyboardTransition transition)
    {
        if (transition.DeviceId == Guid.Empty
            || transition.CalibrationSessionId == Guid.Empty
            || transition.MonotonicTimestamp < 0
            || transition.IsE0 && transition.IsE1)
        {
            Fail("keyboard.transition-invalid", "Keyboard transition fields are invalid.", null);
        }

        if (_activeCalibration is not { } calibration
            || transition.CalibrationSessionId != calibration.SessionId)
        {
            Fail("keyboard.calibration-mismatch", "Keyboard transition calibration does not match the active segment.", null);
        }
    }

    private void ValidateCalibrationForClock(TimeCalibrationSegment calibration)
    {
        StorageKeyboardTime.ValidateCalibration(calibration);
        if (calibration.Frequency != _clock.Frequency)
        {
            throw new InvalidDataException("The calibration frequency does not match the active monotonic clock.");
        }
    }

    private void TrackTimestamp(long timestamp, StorageLifecycleBoundary? boundary)
    {
        if (timestamp < _lastMonotonicTimestamp)
        {
            Fail("storage.monotonic-order-invalid", "Storage monotonic time moved backwards.", boundary);
        }

        _lastMonotonicTimestamp = timestamp;
    }

    private void EnsureRunningAndAccepting()
    {
        ThrowIfDisposed();
        if (_state != StorageServiceState.Running || !_acceptingWrites)
        {
            throw new InvalidOperationException("Storage service is not accepting input transitions.");
        }
    }

    private void EnsureDeviceCatalogWritable()
    {
        if (_state is StorageServiceState.IntegrityFault
            or StorageServiceState.Stopped
            or StorageServiceState.Disposed)
        {
            throw new InvalidOperationException("Storage service cannot modify devices from its current state.");
        }
    }

    public void WriteMouseButtonTransition(StorageMouseButtonTransition transition)
    {
        lock (_gate)
        {
            EnsureRunningAndAccepting();
            if (transition.DeviceId == Guid.Empty
                || transition.CalibrationSessionId == Guid.Empty
                || transition.MonotonicTimestamp < 0
                || (transition.ButtonChanges & ~KnownMouseButtonChanges) != 0
                || _activeCalibration is null)
            {
                Fail("mouse-button.transition-invalid", "Mouse button transition fields are invalid.", null);
                return;
            }

            TimeCalibrationSegment calibration = _activeCalibration.Value;
            if (transition.CalibrationSessionId != calibration.SessionId)
            {
                Fail("mouse-button.transition-invalid", "Mouse button transition fields are invalid.", null);
                return;
            }

            ushort downChanges = (ushort)(transition.ButtonChanges & MouseButtonDownChanges);
            if (downChanges == 0)
            {
                return;
            }

            TrackTimestamp(transition.MonotonicTimestamp, boundary: null);
            try
            {
                DateOnly date = StorageKeyboardTime.GetLocalDate(calibration, transition.MonotonicTimestamp);
                var buttons = new List<StorageMouseButton>(5);
                if ((downChanges & (1 << 0)) != 0) buttons.Add(StorageMouseButton.Left);
                if ((downChanges & (1 << 2)) != 0) buttons.Add(StorageMouseButton.Right);
                if ((downChanges & (1 << 4)) != 0) buttons.Add(StorageMouseButton.Middle);
                if ((downChanges & (1 << 6)) != 0) buttons.Add(StorageMouseButton.X1);
                if ((downChanges & (1 << 8)) != 0) buttons.Add(StorageMouseButton.X2);
                _ordinals = _database.CommitMouseButtonPresses(
                    transition.DeviceId,
                    date,
                    calibration.TimeZoneId,
                    buttons,
                    _faultInjector);
            }
            catch (Exception error) when (IsControlledFailure(error))
            {
                EnterFault("storage.mouse-button-commit-failed", "Mouse button aggregate failed closed.", null);
                throw;
            }
        }
    }

    private void EnsureBoundaryState()
    {
        ThrowIfDisposed();
        if (_state is not StorageServiceState.Running and not StorageServiceState.Paused)
        {
            throw new InvalidOperationException("Storage service cannot process a boundary from its current state.");
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private void Fail(string code, string summary, StorageLifecycleBoundary? boundary)
    {
        EnterFault(code, summary, boundary);
        throw new InvalidDataException(summary);
    }

    private void EnterFault(string code, string summary, StorageLifecycleBoundary? boundary)
    {
        _state = StorageServiceState.IntegrityFault;
        _acceptingWrites = false;
        _integrityFault = new StorageIntegrityFault(code, summary, boundary);
    }

    private static void ValidatePauseReasons(StoragePauseReason reasons)
    {
        const StoragePauseReason known =
            StoragePauseReason.User | StoragePauseReason.SessionLock | StoragePauseReason.Suspend;
        if (reasons == StoragePauseReason.None || (reasons & ~known) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(reasons));
        }
    }

    private static StorageLifecycleBoundary GetPauseBoundary(StoragePauseReason reasons) =>
        reasons.HasFlag(StoragePauseReason.Suspend)
            ? StorageLifecycleBoundary.Suspend
            : reasons.HasFlag(StoragePauseReason.SessionLock)
                ? StorageLifecycleBoundary.SessionLock
                : StorageLifecycleBoundary.Pause;

    private static long GetBatchWindowTicks(long frequency) =>
        checked((long)(((Int128)frequency * MaxBatchWindowMilliseconds + 999) / 1000));

    private static bool IsControlledFailure(Exception error) => error is
        IOException
        or UnauthorizedAccessException
        or InvalidDataException
        or InvalidOperationException
        or OverflowException
        or Microsoft.Data.Sqlite.SqliteException;
}
