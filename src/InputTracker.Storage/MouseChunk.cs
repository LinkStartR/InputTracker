using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace InputTracker.Storage;

internal sealed record MouseChunkHeader(
    Guid ChunkId,
    Guid DeviceId,
    long CalibrationSegmentId,
    long FirstSequence,
    long LastSequence,
    long FirstMonotonicTimestamp,
    long LastMonotonicTimestamp,
    long EventCount,
    long CanonicalLength,
    long CompressedLength);

internal sealed record MouseChunk(
    MouseChunkHeader Header,
    IReadOnlyList<MouseSpoolRecord> Records,
    uint CompressedCrc32C,
    string CanonicalSha256);

internal sealed record MouseChunkEncoded(
    byte[] FileBytes,
    MouseChunkHeader Header,
    uint CompressedCrc32C,
    string CanonicalSha256);

internal sealed record MouseChunkFileWriteResult(
    string Path,
    MouseChunkHeader Header,
    long FileLength,
    uint CompressedCrc32C,
    string CanonicalSha256);

internal static class MouseChunkFormat
{
    public const uint Magic = 0x4B48434D; // MCHK in little endian.
    public const ushort Version = 1;
    public const ushort Codec = 1; // Delta/zigzag/varint canonical payload + Brotli.
    public const int HeaderSize = 108;
    public const int FooterSize = 40;
    public const uint EndMagic = 0x444E454D; // MEND in little endian.
    public const int MaxEventCount = 1_000_000;
    public const int MaxCanonicalLength = 256 * 1024 * 1024;
    public const int MaxCompressedLength = 256 * 1024 * 1024;
    public const long MaxFileLength = HeaderSize + MaxCompressedLength + FooterSize;
    public const ushort KnownButtonMask = 0x03FF;
}

internal static class MouseChunkFile
{
    public static MouseChunkFileWriteResult WriteNew(
        string path,
        Guid chunkId,
        Guid deviceId,
        long calibrationSegmentId,
        IReadOnlyList<MouseSpoolRecord> records)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        MouseChunkEncoded encoded = MouseChunkCodec.Encode(chunkId, deviceId, calibrationSegmentId, records);
        string fullPath = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
        using (var stream = new FileStream(
            fullPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            4096,
            FileOptions.SequentialScan))
        {
            stream.Write(encoded.FileBytes);
            stream.Flush(flushToDisk: true);
        }

        return new MouseChunkFileWriteResult(
            fullPath,
            encoded.Header,
            encoded.FileBytes.LongLength,
            encoded.CompressedCrc32C,
            encoded.CanonicalSha256);
    }
}

internal static class MouseChunkReader
{
    public static MouseChunk Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            throw new FileNotFoundException("Mouse chunk file does not exist.", file.FullName);
        }

        if (file.Length < MouseChunkFormat.HeaderSize + MouseChunkFormat.FooterSize
            || file.Length > MouseChunkFormat.MaxFileLength)
        {
            throw new InvalidDataException($"Mouse chunk file length {file.Length} is outside the supported range.");
        }

        return DecodeFailClosed(File.ReadAllBytes(file.FullName));
    }

    internal static MouseChunk Read(ReadOnlySpan<byte> bytes) => DecodeFailClosed(bytes);

    private static MouseChunk DecodeFailClosed(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return MouseChunkCodec.Decode(bytes);
        }
        catch (OverflowException error)
        {
            throw new InvalidDataException("Mouse chunk contains an overflowing numeric value.", error);
        }
    }
}

internal static class MouseChunkCodec
{
    public static MouseChunkEncoded Encode(
        Guid chunkId,
        Guid deviceId,
        long calibrationSegmentId,
        IReadOnlyList<MouseSpoolRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        if (chunkId == Guid.Empty || deviceId == Guid.Empty || calibrationSegmentId <= 0)
        {
            throw new ArgumentException("Chunk and device identifiers must be non-empty and calibration segment identifier positive.");
        }

        ValidateRecords(records);
        byte[] canonical = EncodeCanonical(records);
        byte[] compressed = Compress(canonical);
        uint compressedCrc = Crc32C.Calculate(compressed);
        byte[] canonicalHash = SHA256.HashData(canonical);
        MouseSpoolRecord first = records[0];
        MouseSpoolRecord last = records[^1];
        var header = new MouseChunkHeader(
            chunkId,
            deviceId,
            calibrationSegmentId,
            first.Sequence,
            last.Sequence,
            first.MonotonicTimestamp,
            last.MonotonicTimestamp,
            records.Count,
            canonical.LongLength,
            compressed.LongLength);

        byte[] file = new byte[checked(MouseChunkFormat.HeaderSize + compressed.Length + MouseChunkFormat.FooterSize)];
        WriteHeader(file, header);
        compressed.CopyTo(file.AsSpan(MouseChunkFormat.HeaderSize));
        int footerOffset = MouseChunkFormat.HeaderSize + compressed.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(footerOffset), compressedCrc);
        canonicalHash.CopyTo(file.AsSpan(footerOffset + sizeof(uint), 32));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(footerOffset + 36), MouseChunkFormat.EndMagic);
        return new MouseChunkEncoded(file, header, compressedCrc, Convert.ToHexString(canonicalHash));
    }

    public static MouseChunk Decode(ReadOnlySpan<byte> file)
    {
        if (file.Length < MouseChunkFormat.HeaderSize + MouseChunkFormat.FooterSize
            || file.Length > MouseChunkFormat.MaxFileLength)
        {
            throw new InvalidDataException($"Mouse chunk length {file.Length} is outside the supported range.");
        }

        MouseChunkHeader header = ReadHeader(file[..MouseChunkFormat.HeaderSize]);
        long expectedFileLength = checked(MouseChunkFormat.HeaderSize + header.CompressedLength + MouseChunkFormat.FooterSize);
        if (expectedFileLength != file.Length)
        {
            throw new InvalidDataException($"Mouse chunk length mismatch: header declares {expectedFileLength}, file has {file.Length}.");
        }

        int compressedLength = checked((int)header.CompressedLength);
        ReadOnlySpan<byte> compressed = file.Slice(MouseChunkFormat.HeaderSize, compressedLength);
        ReadOnlySpan<byte> footer = file.Slice(MouseChunkFormat.HeaderSize + compressedLength, MouseChunkFormat.FooterSize);
        if (BinaryPrimitives.ReadUInt32LittleEndian(footer[36..]) != MouseChunkFormat.EndMagic)
        {
            throw new InvalidDataException("Mouse chunk end magic is invalid.");
        }

        uint expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(footer);
        uint actualCrc = Crc32C.Calculate(compressed);
        if (actualCrc != expectedCrc)
        {
            throw new InvalidDataException($"Mouse chunk compressed CRC32C mismatch: expected {expectedCrc:X8}, actual {actualCrc:X8}.");
        }

        byte[] canonical = Decompress(compressed, checked((int)header.CanonicalLength));
        byte[] expectedHash = footer.Slice(sizeof(uint), 32).ToArray();
        byte[] actualHash = SHA256.HashData(canonical);
        if (!CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
        {
            throw new InvalidDataException("Mouse chunk canonical SHA-256 mismatch.");
        }

        IReadOnlyList<MouseSpoolRecord> records = DecodeCanonical(canonical, header);
        return new MouseChunk(header, records, actualCrc, Convert.ToHexString(actualHash));
    }

    private static void ValidateRecords(IReadOnlyList<MouseSpoolRecord> records)
    {
        if (records.Count is <= 0 or > MouseChunkFormat.MaxEventCount)
        {
            throw new InvalidDataException($"Mouse chunk event count {records.Count} is outside the supported range.");
        }

        long expectedSequence = records[0].Sequence;
        if (expectedSequence <= 0 || records[0].MonotonicTimestamp < 0)
        {
            throw new InvalidDataException("Mouse chunk sequences must be positive and monotonic timestamps non-negative.");
        }

        long previousTimestamp = records[0].MonotonicTimestamp;
        for (int index = 0; index < records.Count; index++)
        {
            MouseSpoolRecord record = records[index];
            if (record.Sequence != expectedSequence)
            {
                throw new InvalidDataException($"Mouse chunk sequence is not contiguous: expected {expectedSequence}, found {record.Sequence}.");
            }

            if (record.MonotonicTimestamp < previousTimestamp)
            {
                throw new InvalidDataException("Mouse chunk monotonic timestamps moved backwards.");
            }

            if (record.MotionKind > 1 || (record.ButtonChanges & ~MouseChunkFormat.KnownButtonMask) != 0)
            {
                throw new InvalidDataException("Mouse chunk contains an unknown motion kind or button flag.");
            }

            previousTimestamp = record.MonotonicTimestamp;
            if (index + 1 < records.Count)
            {
                expectedSequence = checked(expectedSequence + 1);
            }
        }
    }

    private static byte[] EncodeCanonical(IReadOnlyList<MouseSpoolRecord> records)
    {
        using var stream = new MemoryStream();
        long previousSequence = records[0].Sequence;
        long previousTimestamp = records[0].MonotonicTimestamp;
        int previousX = 0;
        int previousY = 0;
        for (int index = 0; index < records.Count; index++)
        {
            MouseSpoolRecord record = records[index];
            WriteVarUInt(stream, index == 0 ? 0UL : checked((ulong)(record.Sequence - previousSequence)));
            WriteVarUInt(stream, index == 0 ? 0UL : checked((ulong)(record.MonotonicTimestamp - previousTimestamp)));
            WriteVarUInt(stream, ZigZagEncode((long)record.DeltaX - previousX));
            WriteVarUInt(stream, ZigZagEncode((long)record.DeltaY - previousY));
            stream.WriteByte((byte)(record.MotionKind | (record.UsesVirtualDesktop ? 0x02 : 0x00)));
            WriteVarUInt(stream, record.ButtonChanges);
            WriteVarUInt(stream, ZigZagEncode(record.VerticalWheelDelta));
            WriteVarUInt(stream, ZigZagEncode(record.HorizontalWheelDelta));
            previousSequence = record.Sequence;
            previousTimestamp = record.MonotonicTimestamp;
            previousX = record.DeltaX;
            previousY = record.DeltaY;
        }

        if (stream.Length > MouseChunkFormat.MaxCanonicalLength)
        {
            throw new InvalidDataException("Mouse chunk canonical payload is too large.");
        }

        return stream.ToArray();
    }

    private static IReadOnlyList<MouseSpoolRecord> DecodeCanonical(ReadOnlySpan<byte> canonical, MouseChunkHeader header)
    {
        int offset = 0;
        int count = checked((int)header.EventCount);
        var records = new MouseSpoolRecord[count];
        long previousSequence = header.FirstSequence;
        long previousTimestamp = header.FirstMonotonicTimestamp;
        int previousX = 0;
        int previousY = 0;
        for (int index = 0; index < count; index++)
        {
            ulong sequenceDelta = ReadVarUInt(canonical, ref offset);
            ulong timestampDelta = ReadVarUInt(canonical, ref offset);
            long sequence = index == 0
                ? header.FirstSequence
                : checked(previousSequence + checked((long)sequenceDelta));
            long timestamp = index == 0
                ? header.FirstMonotonicTimestamp
                : checked(previousTimestamp + checked((long)timestampDelta));
            if ((index == 0 && (sequenceDelta != 0 || timestampDelta != 0))
                || (index > 0 && sequenceDelta != 1))
            {
                throw new InvalidDataException("Mouse chunk canonical sequence or first-timestamp delta is invalid.");
            }

            long x = checked(previousX + ZigZagDecode(ReadVarUInt(canonical, ref offset)));
            long y = checked(previousY + ZigZagDecode(ReadVarUInt(canonical, ref offset)));
            if (x is < int.MinValue or > int.MaxValue || y is < int.MinValue or > int.MaxValue)
            {
                throw new InvalidDataException("Mouse chunk coordinate is outside Int32 range.");
            }

            if (offset >= canonical.Length)
            {
                throw new InvalidDataException("Mouse chunk canonical payload ended before flags.");
            }

            byte flags = canonical[offset++];
            if ((flags & 0xFC) != 0)
            {
                throw new InvalidDataException("Mouse chunk canonical flags are invalid.");
            }

            ulong buttons = ReadVarUInt(canonical, ref offset);
            long vertical = ZigZagDecode(ReadVarUInt(canonical, ref offset));
            long horizontal = ZigZagDecode(ReadVarUInt(canonical, ref offset));
            if (buttons > MouseChunkFormat.KnownButtonMask
                || vertical is < short.MinValue or > short.MaxValue
                || horizontal is < short.MinValue or > short.MaxValue)
            {
                throw new InvalidDataException("Mouse chunk button or wheel value is outside the supported range.");
            }

            records[index] = new MouseSpoolRecord(
                sequence,
                timestamp,
                checked((int)x),
                checked((int)y),
                (byte)(flags & 0x01),
                (flags & 0x02) != 0,
                checked((ushort)buttons),
                checked((short)vertical),
                checked((short)horizontal));
            previousSequence = sequence;
            previousTimestamp = timestamp;
            previousX = checked((int)x);
            previousY = checked((int)y);
        }

        if (offset != canonical.Length
            || records[^1].Sequence != header.LastSequence
            || records[^1].MonotonicTimestamp != header.LastMonotonicTimestamp)
        {
            throw new InvalidDataException("Mouse chunk canonical count, trailing data, or header range is inconsistent.");
        }

        return records;
    }

    private static byte[] Compress(ReadOnlySpan<byte> canonical)
    {
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            brotli.Write(canonical);
        }

        if (output.Length > MouseChunkFormat.MaxCompressedLength)
        {
            throw new InvalidDataException("Mouse chunk compressed payload is too large.");
        }

        return output.ToArray();
    }

    private static byte[] Decompress(ReadOnlySpan<byte> compressed, int canonicalLength)
    {
        byte[] canonical = new byte[canonicalLength];
        var decoder = new BrotliDecoder();
        try
        {
            OperationStatus status = decoder.Decompress(compressed, canonical, out int consumed, out int written);
            if (status != OperationStatus.Done || consumed != compressed.Length || written != canonical.Length)
            {
                throw new InvalidDataException("Mouse chunk Brotli payload length or termination is invalid.");
            }
        }
        finally
        {
            decoder.Dispose();
        }

        return canonical;
    }

    private static void WriteHeader(Span<byte> destination, MouseChunkHeader header)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, MouseChunkFormat.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], MouseChunkFormat.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], MouseChunkFormat.Codec);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], MouseChunkFormat.HeaderSize);
        header.ChunkId.TryWriteBytes(destination[12..28]);
        header.DeviceId.TryWriteBytes(destination[28..44]);
        BinaryPrimitives.WriteInt64LittleEndian(destination[44..], header.CalibrationSegmentId);
        BinaryPrimitives.WriteInt64LittleEndian(destination[52..], header.FirstSequence);
        BinaryPrimitives.WriteInt64LittleEndian(destination[60..], header.LastSequence);
        BinaryPrimitives.WriteInt64LittleEndian(destination[68..], header.FirstMonotonicTimestamp);
        BinaryPrimitives.WriteInt64LittleEndian(destination[76..], header.LastMonotonicTimestamp);
        BinaryPrimitives.WriteInt64LittleEndian(destination[84..], header.EventCount);
        BinaryPrimitives.WriteInt64LittleEndian(destination[92..], header.CanonicalLength);
        BinaryPrimitives.WriteInt64LittleEndian(destination[100..], header.CompressedLength);
    }

    private static MouseChunkHeader ReadHeader(ReadOnlySpan<byte> source)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(source) != MouseChunkFormat.Magic)
        {
            throw new InvalidDataException("Mouse chunk magic is invalid.");
        }

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(source[4..]);
        if (version != MouseChunkFormat.Version)
        {
            throw new InvalidDataException($"Mouse chunk version {version} is not supported.");
        }

        ushort codec = BinaryPrimitives.ReadUInt16LittleEndian(source[6..]);
        uint headerSize = BinaryPrimitives.ReadUInt32LittleEndian(source[8..]);
        if (codec != MouseChunkFormat.Codec || headerSize != MouseChunkFormat.HeaderSize)
        {
            throw new InvalidDataException("Mouse chunk codec or header size is invalid.");
        }

        var header = new MouseChunkHeader(
            new Guid(source[12..28]),
            new Guid(source[28..44]),
            BinaryPrimitives.ReadInt64LittleEndian(source[44..]),
            BinaryPrimitives.ReadInt64LittleEndian(source[52..]),
            BinaryPrimitives.ReadInt64LittleEndian(source[60..]),
            BinaryPrimitives.ReadInt64LittleEndian(source[68..]),
            BinaryPrimitives.ReadInt64LittleEndian(source[76..]),
            BinaryPrimitives.ReadInt64LittleEndian(source[84..]),
            BinaryPrimitives.ReadInt64LittleEndian(source[92..]),
            BinaryPrimitives.ReadInt64LittleEndian(source[100..]));
        if (header.ChunkId == Guid.Empty || header.DeviceId == Guid.Empty || header.CalibrationSegmentId <= 0
            || header.FirstSequence <= 0 || header.LastSequence < header.FirstSequence
            || header.FirstMonotonicTimestamp < 0 || header.LastMonotonicTimestamp < header.FirstMonotonicTimestamp
            || header.EventCount is <= 0 or > MouseChunkFormat.MaxEventCount
            || header.EventCount != checked(header.LastSequence - header.FirstSequence + 1)
            || header.CanonicalLength is <= 0 or > MouseChunkFormat.MaxCanonicalLength
            || header.CompressedLength is <= 0 or > MouseChunkFormat.MaxCompressedLength)
        {
            throw new InvalidDataException("Mouse chunk header range or count is invalid.");
        }

        return header;
    }

    private static void WriteVarUInt(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }

        stream.WriteByte((byte)value);
    }

    private static ulong ReadVarUInt(ReadOnlySpan<byte> source, ref int offset)
    {
        ulong value = 0;
        for (int index = 0; index < 10; index++)
        {
            if (offset >= source.Length)
            {
                throw new InvalidDataException("Mouse chunk canonical varint is truncated.");
            }

            byte current = source[offset++];
            if (index == 9 && current > 1)
            {
                throw new InvalidDataException("Mouse chunk canonical varint overflows UInt64.");
            }

            value |= (ulong)(current & 0x7F) << (index * 7);
            if ((current & 0x80) == 0)
            {
                if (index > 0 && current == 0)
                {
                    throw new InvalidDataException("Mouse chunk canonical varint is not minimally encoded.");
                }

                return value;
            }
        }

        throw new InvalidDataException("Mouse chunk canonical varint is too long.");
    }

    private static ulong ZigZagEncode(long value) => unchecked((ulong)((value << 1) ^ (value >> 63)));

    private static long ZigZagDecode(ulong value) => unchecked((long)(value >> 1) ^ -((long)value & 1));
}
