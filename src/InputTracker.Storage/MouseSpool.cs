using System.Buffers.Binary;

namespace InputTracker.Storage;

public readonly record struct MouseSpoolRecord(
    long Sequence,
    long MonotonicTimestamp,
    int DeltaX,
    int DeltaY,
    byte MotionKind,
    bool UsesVirtualDesktop,
    ushort ButtonChanges,
    short VerticalWheelDelta,
    short HorizontalWheelDelta);

public readonly record struct SpoolRecoveryResult(
    long Recovered,
    long Lost,
    long Corrupt,
    long ValidLength,
    long LastSequence);

public readonly record struct SpoolDurableWatermark(
    string SpoolId,
    long SpoolAppended,
    long PowerDurable,
    long DurableLength);

internal readonly record struct SpoolFlushResult(
    long FileFlushedCount,
    long FlushedLength);

internal static class MouseSpoolRecordCodec
{
    private const uint Magic = 0x5250534D; // MSPR in little endian.
    private const ushort Version = 1;
    public const int RecordSize = 44;
    private const int ChecksumOffset = RecordSize - sizeof(uint);

    public static byte[] Encode(MouseSpoolRecord record)
    {
        byte[] bytes = new byte[RecordSize];
        Span<byte> span = bytes;
        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], Version);
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], RecordSize);
        BinaryPrimitives.WriteInt64LittleEndian(span[8..], record.Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(span[16..], record.MonotonicTimestamp);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], record.DeltaX);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], record.DeltaY);
        span[32] = record.MotionKind;
        span[33] = record.UsesVirtualDesktop ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..], record.ButtonChanges);
        BinaryPrimitives.WriteInt16LittleEndian(span[36..], record.VerticalWheelDelta);
        BinaryPrimitives.WriteInt16LittleEndian(span[38..], record.HorizontalWheelDelta);
        BinaryPrimitives.WriteUInt32LittleEndian(span[ChecksumOffset..], Crc32C.Calculate(span[..ChecksumOffset]));
        return bytes;
    }

    public static MouseSpoolRecord Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != RecordSize)
        {
            throw new InvalidDataException($"Mouse spool record length {bytes.Length} is not {RecordSize}.");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic
            || BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]) != Version
            || BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]) != RecordSize)
        {
            throw new InvalidDataException("Mouse spool record header is invalid.");
        }

        uint expected = BinaryPrimitives.ReadUInt32LittleEndian(bytes[ChecksumOffset..]);
        uint actual = Crc32C.Calculate(bytes[..ChecksumOffset]);
        if (expected != actual)
        {
            throw new InvalidDataException($"Mouse spool record CRC32C mismatch: expected {expected:X8}, actual {actual:X8}.");
        }

        return new MouseSpoolRecord(
            BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[16..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[24..]),
            BinaryPrimitives.ReadInt32LittleEndian(bytes[28..]),
            bytes[32],
            bytes[33] != 0,
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[34..]),
            BinaryPrimitives.ReadInt16LittleEndian(bytes[36..]),
            BinaryPrimitives.ReadInt16LittleEndian(bytes[38..]));
    }
}

public static class MouseSpoolRecovery
{
    public static SpoolRecoveryResult Recover(string path, bool truncateIncompleteTail)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path)) return new SpoolRecoveryResult(0, 0, 0, 0, 0);

        FileAccess access = truncateIncompleteTail ? FileAccess.ReadWrite : FileAccess.Read;
        using var stream = new FileStream(path, FileMode.Open, access, FileShare.Read, 4096, FileOptions.SequentialScan);
        byte[] buffer = new byte[MouseSpoolRecordCodec.RecordSize];
        long recovered = 0;
        long lastSequence = 0;
        long validLength = 0;

        while (validLength + buffer.Length <= stream.Length)
        {
            stream.ReadExactly(buffer);
            MouseSpoolRecord record;
            try
            {
                record = MouseSpoolRecordCodec.Decode(buffer);
            }
            catch (InvalidDataException error)
            {
                throw new InvalidDataException(
                    $"Mouse spool corruption at byte offset {validLength}; {recovered} records form the valid prefix.",
                    error);
            }

            if (recovered > 0 && record.Sequence != lastSequence + 1)
            {
                throw new InvalidDataException(
                    $"Mouse spool sequence gap at byte offset {validLength}: expected {lastSequence + 1}, found {record.Sequence}.");
            }

            recovered++;
            lastSequence = record.Sequence;
            validLength += buffer.Length;
        }

        long tailLength = stream.Length - validLength;
        long lost = tailLength == 0 ? 0 : 1;
        if (tailLength > 0 && truncateIncompleteTail)
        {
            stream.SetLength(validLength);
            stream.Flush(flushToDisk: true);
        }

        return new SpoolRecoveryResult(recovered, lost, 0, validLength, lastSequence);
    }
}

public sealed class MouseSpoolWriter : IDisposable
{
    private readonly FileStream _stream;
    private long _lastSequence;
    private bool _disposed;

    public MouseSpoolWriter(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        SpoolRecoveryResult recovery = MouseSpoolRecovery.Recover(path, truncateIncompleteTail: true);
        Path = System.IO.Path.GetFullPath(path);
        _lastSequence = recovery.LastSequence;
        Recovered = recovery.Recovered;
        Lost = recovery.Lost;
        _stream = new FileStream(Path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, 1, FileOptions.SequentialScan);
        _stream.Position = _stream.Length;
        SpoolAppended = recovery.Recovered;
    }

    public string Path { get; }
    public long Accepted { get; private set; }
    public long SinkAcknowledged { get; private set; }
    public long SpoolAppended { get; private set; }
    public long Recovered { get; }
    public long Lost { get; }
    public long Length => _stream.Length;

    public void Append(MouseSpoolRecord record)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long expected = _lastSequence + 1;
        if (record.Sequence != expected)
        {
            throw new InvalidDataException($"Mouse spool expected sequence {expected}, found {record.Sequence}.");
        }

        Accepted++;
        byte[] bytes = MouseSpoolRecordCodec.Encode(record);
        _stream.Write(bytes);
        _lastSequence = record.Sequence;
        SpoolAppended++;
        SinkAcknowledged++;
    }

    internal SpoolFlushResult FlushFile()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _stream.Flush(flushToDisk: true);
        return new SpoolFlushResult(SpoolAppended, _stream.Length);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stream.Dispose();
    }
}
