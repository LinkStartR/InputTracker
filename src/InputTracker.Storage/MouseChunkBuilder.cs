namespace InputTracker.Storage;

internal enum MouseChunkSealReason
{
    EventLimit = 0,
    CanonicalSizeLimit = 1,
    DurationLimit = 2,
    DeviceBoundary = 3,
    CalibrationBoundary = 4,
    PauseBoundary = 5,
    ExplicitFlush = 6
}

internal sealed record MouseChunkBatch(
    Guid DeviceId,
    long CalibrationSegmentId,
    IReadOnlyList<MouseSpoolRecord> Records,
    long CanonicalLength,
    MouseChunkSealReason Reason);

internal sealed record MouseChunkAppendResult(
    MouseChunkBatch? Sealed,
    int BufferedEvents,
    long BufferedCanonicalLength);

internal sealed class MouseChunkBuilder
{
    public const int MaxEvents = 4096;
    public const int MaxCanonicalBytes = 256 * 1024;

    private readonly long _timestampFrequency;
    private readonly int _maxEvents;
    private readonly int _maxCanonicalBytes;
    private readonly List<MouseSpoolRecord> _records = [];
    private Guid _deviceId;
    private long _calibrationSegmentId;
    private long _canonicalLength;

    public MouseChunkBuilder(long timestampFrequency)
        : this(timestampFrequency, MaxEvents, MaxCanonicalBytes)
    {
    }

    internal MouseChunkBuilder(long timestampFrequency, int maxEvents, int maxCanonicalBytes)
    {
        if (timestampFrequency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timestampFrequency));
        }

        if (maxEvents <= 0 || maxEvents > MaxEvents)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEvents));
        }

        if (maxCanonicalBytes <= 0 || maxCanonicalBytes > MaxCanonicalBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCanonicalBytes));
        }

        _timestampFrequency = timestampFrequency;
        _maxEvents = maxEvents;
        _maxCanonicalBytes = maxCanonicalBytes;
    }

    public int BufferedEvents => _records.Count;
    public long BufferedCanonicalLength => _canonicalLength;

    public MouseChunkAppendResult Append(Guid deviceId, long calibrationSegmentId, MouseSpoolRecord record)
    {
        if (deviceId == Guid.Empty)
        {
            throw new ArgumentException("Device identifier must be non-empty.", nameof(deviceId));
        }

        if (calibrationSegmentId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(calibrationSegmentId));
        }

        MouseChunkBatch? sealedBatch = null;
        if (_records.Count > 0)
        {
            MouseChunkSealReason? boundary = GetBoundary(deviceId, calibrationSegmentId, record);
            if (boundary is not null)
            {
                sealedBatch = Seal(boundary.Value);
            }
        }

        AddToBuffer(deviceId, calibrationSegmentId, record);
        if (_records.Count > 1
            && _records[^1].MonotonicTimestamp - _records[0].MonotonicTimestamp >= _timestampFrequency)
        {
            sealedBatch = Seal(MouseChunkSealReason.DurationLimit);
        }
        else if (_records.Count == _maxEvents)
        {
            sealedBatch = Seal(MouseChunkSealReason.EventLimit);
        }
        else if (_canonicalLength == _maxCanonicalBytes)
        {
            sealedBatch = Seal(MouseChunkSealReason.CanonicalSizeLimit);
        }

        return new MouseChunkAppendResult(sealedBatch, _records.Count, _canonicalLength);
    }

    public MouseChunkBatch? SealForPause() =>
        _records.Count == 0 ? null : Seal(MouseChunkSealReason.PauseBoundary);

    public MouseChunkBatch? SealAtTimestamp(long monotonicTimestamp) =>
        _records.Count > 0
            && monotonicTimestamp >= _records[0].MonotonicTimestamp
            && monotonicTimestamp - _records[0].MonotonicTimestamp >= _timestampFrequency
                ? Seal(MouseChunkSealReason.DurationLimit)
                : null;

    public MouseChunkBatch? SealForDeviceRemoval(Guid deviceId) =>
        _records.Count > 0 && deviceId == _deviceId
            ? Seal(MouseChunkSealReason.DeviceBoundary)
            : null;

    public MouseChunkBatch? SealForCalibrationClose(long calibrationSegmentId) =>
        _records.Count > 0 && calibrationSegmentId == _calibrationSegmentId
            ? Seal(MouseChunkSealReason.CalibrationBoundary)
            : null;

    public MouseChunkBatch? SealRemaining() =>
        _records.Count == 0 ? null : Seal(MouseChunkSealReason.ExplicitFlush);

    private MouseChunkSealReason? GetBoundary(Guid deviceId, long calibrationSegmentId, MouseSpoolRecord record)
    {
        if (deviceId != _deviceId)
        {
            return MouseChunkSealReason.DeviceBoundary;
        }

        if (calibrationSegmentId != _calibrationSegmentId)
        {
            return MouseChunkSealReason.CalibrationBoundary;
        }

        MouseSpoolRecord previous = _records[^1];
        if (record.Sequence != checked(previous.Sequence + 1))
        {
            throw new InvalidDataException("Mouse chunk builder requires contiguous sequences.");
        }

        if (record.MonotonicTimestamp < previous.MonotonicTimestamp)
        {
            throw new InvalidDataException("Mouse chunk builder monotonic timestamp moved backwards.");
        }

        if (record.MonotonicTimestamp - _records[0].MonotonicTimestamp > _timestampFrequency)
        {
            return MouseChunkSealReason.DurationLimit;
        }

        long nextLength = checked(_canonicalLength + GetEncodedLength(record, previous, first: false));
        return nextLength > _maxCanonicalBytes ? MouseChunkSealReason.CanonicalSizeLimit : null;
    }

    private void AddToBuffer(Guid deviceId, long calibrationSegmentId, MouseSpoolRecord record)
    {
        if (record.MotionKind > 1 || (record.ButtonChanges & ~MouseChunkFormat.KnownButtonMask) != 0)
        {
            throw new InvalidDataException("Mouse chunk contains an unknown motion kind or button flag.");
        }

        long nextCanonicalLength;
        if (_records.Count == 0)
        {
            if (record.Sequence <= 0 || record.MonotonicTimestamp < 0)
            {
                throw new InvalidDataException("Mouse chunk sequences must be positive and timestamps non-negative.");
            }

            nextCanonicalLength = GetEncodedLength(record, default, first: true);
        }
        else
        {
            nextCanonicalLength = checked(_canonicalLength + GetEncodedLength(record, _records[^1], first: false));
        }

        if (nextCanonicalLength > _maxCanonicalBytes)
        {
            throw new InvalidDataException("A single mouse event exceeds the production canonical chunk limit.");
        }

        if (_records.Count == 0)
        {
            _deviceId = deviceId;
            _calibrationSegmentId = calibrationSegmentId;
        }

        _canonicalLength = nextCanonicalLength;
        _records.Add(record);
    }

    private MouseChunkBatch Seal(MouseChunkSealReason reason)
    {
        var batch = new MouseChunkBatch(
            _deviceId,
            _calibrationSegmentId,
            _records.ToArray(),
            _canonicalLength,
            reason);
        _records.Clear();
        _deviceId = Guid.Empty;
        _calibrationSegmentId = 0;
        _canonicalLength = 0;
        return batch;
    }

    private static int GetEncodedLength(MouseSpoolRecord current, MouseSpoolRecord previous, bool first)
    {
        ulong sequenceDelta = first ? 0 : checked((ulong)(current.Sequence - previous.Sequence));
        ulong timestampDelta = first ? 0 : checked((ulong)(current.MonotonicTimestamp - previous.MonotonicTimestamp));
        long previousX = first ? 0 : previous.DeltaX;
        long previousY = first ? 0 : previous.DeltaY;
        return VarUIntLength(sequenceDelta)
            + VarUIntLength(timestampDelta)
            + VarUIntLength(ZigZagEncode((long)current.DeltaX - previousX))
            + VarUIntLength(ZigZagEncode((long)current.DeltaY - previousY))
            + 1
            + VarUIntLength(current.ButtonChanges)
            + VarUIntLength(ZigZagEncode(current.VerticalWheelDelta))
            + VarUIntLength(ZigZagEncode(current.HorizontalWheelDelta));
    }

    private static int VarUIntLength(ulong value)
    {
        int length = 1;
        while (value >= 0x80)
        {
            length++;
            value >>= 7;
        }

        return length;
    }

    private static ulong ZigZagEncode(long value) => unchecked((ulong)((value << 1) ^ (value >> 63)));
}
