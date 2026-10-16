using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using InputTracker.Input;

namespace InputTracker.Storage.Tests;

public sealed class MouseChunkCodecTests
{
    [Fact]
    public void RoundTripsCompleteInputMouseContractAndExtremeSignedValues()
    {
        using var directory = new TemporaryDirectory();
        Guid chunkId = Guid.NewGuid();
        Guid deviceId = Guid.NewGuid();
        const long calibrationSegmentId = 77;
        MouseButtonChanges allButtons =
            MouseButtonChanges.LeftDown | MouseButtonChanges.LeftUp
            | MouseButtonChanges.RightDown | MouseButtonChanges.RightUp
            | MouseButtonChanges.MiddleDown | MouseButtonChanges.MiddleUp
            | MouseButtonChanges.X1Down | MouseButtonChanges.X1Up
            | MouseButtonChanges.X2Down | MouseButtonChanges.X2Up;
        MouseRawEvent[] input =
        [
            new(new RuntimeDeviceId(0xABC), int.MinValue, int.MaxValue, MouseMotionKind.Relative, false, allButtons, short.MinValue, short.MaxValue, 10_000),
            new(new RuntimeDeviceId(0xABC), int.MaxValue, int.MinValue, MouseMotionKind.Absolute, true, MouseButtonChanges.X1Down | MouseButtonChanges.X2Up, short.MaxValue, short.MinValue, 10_000),
            new(new RuntimeDeviceId(0xABC), -1, 1, MouseMotionKind.Relative, true, MouseButtonChanges.None, -120, 120, long.MaxValue - 1)
        ];
        MouseSpoolRecord[] records = input.Select((value, index) => new MouseSpoolRecord(
            900 + index,
            value.Timestamp,
            value.DeltaX,
            value.DeltaY,
            (byte)value.MotionKind,
            value.UsesVirtualDesktop,
            (ushort)value.ButtonChanges,
            value.VerticalWheelDelta,
            value.HorizontalWheelDelta)).ToArray();
        string path = System.IO.Path.Combine(directory.Path, "chunk.mchunk");

        MouseChunkFileWriteResult written = MouseChunkFile.WriteNew(path, chunkId, deviceId, calibrationSegmentId, records);
        MouseChunk read = MouseChunkReader.Read(path);

        Assert.Equal(chunkId, read.Header.ChunkId);
        Assert.Equal(deviceId, read.Header.DeviceId);
        Assert.Equal(calibrationSegmentId, read.Header.CalibrationSegmentId);
        Assert.Equal(records, read.Records.ToArray());
        Assert.Equal(records.Length, read.Header.EventCount);
        Assert.Equal(records[0].Sequence, read.Header.FirstSequence);
        Assert.Equal(records[^1].Sequence, read.Header.LastSequence);
        Assert.Equal(new FileInfo(path).Length, written.FileLength);
        Assert.Equal(written.CompressedCrc32C, read.CompressedCrc32C);
        Assert.Equal(written.CanonicalSha256, read.CanonicalSha256);
        Assert.Equal(0x03FF, (ushort)allButtons);
    }

    [Fact]
    public void Crc32CMatchesPublishedCheckValue()
    {
        Assert.Equal(0xE3069283u, Crc32C.Calculate(Encoding.ASCII.GetBytes("123456789")));
    }

    [Fact]
    public void ReaderFailsClosedOnFutureVersionBitFlipAndTruncation()
    {
        MouseChunkEncoded encoded = MouseChunkCodec.Encode(
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            [new MouseSpoolRecord(1, 1, -1, 1, 0, false, 0x03FF, -120, 120)]);

        byte[] future = encoded.FileBytes.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(future.AsSpan(4), 2);
        Assert.Throws<InvalidDataException>(() => MouseChunkReader.Read(future));

        byte[] flipped = encoded.FileBytes.ToArray();
        flipped[MouseChunkFormat.HeaderSize] ^= 0x01;
        Assert.Throws<InvalidDataException>(() => MouseChunkReader.Read(flipped));

        Assert.Throws<InvalidDataException>(() => MouseChunkReader.Read(encoded.FileBytes.AsSpan(0, encoded.FileBytes.Length - 1)));
    }

    [Fact]
    public void ReaderFailsClosedWhenCanonicalSha256FooterIsModifiedWithValidCompressedCrc()
    {
        MouseChunkEncoded encoded = MouseChunkCodec.Encode(
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            [new MouseSpoolRecord(1, 1, -1, 1, 0, false, 0x03FF, -120, 120)]);
        byte[] modified = encoded.FileBytes.ToArray();
        long compressedLength = BinaryPrimitives.ReadInt64LittleEndian(modified.AsSpan(100));
        int footerOffset = checked(MouseChunkFormat.HeaderSize + (int)compressedLength);
        modified[footerOffset + sizeof(uint)] ^= 0x01;

        Assert.Equal(
            BinaryPrimitives.ReadUInt32LittleEndian(encoded.FileBytes.AsSpan(footerOffset)),
            BinaryPrimitives.ReadUInt32LittleEndian(modified.AsSpan(footerOffset)));
        Assert.Throws<InvalidDataException>(() => MouseChunkReader.Read(modified));
    }

    [Fact]
    public void ReaderFailsClosedOnNonMinimalCanonicalVarint()
    {
        MouseChunkEncoded encoded = MouseChunkCodec.Encode(
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            [new MouseSpoolRecord(1, 1, 0, 0, 0, false, 0, 0, 0)]);
        byte[] canonical = Decompress(encoded.FileBytes);
        byte[] nonMinimal = [0x80, 0x00, .. canonical.AsSpan(1)];
        byte[] malformed = RebuildWithCanonical(encoded.FileBytes, nonMinimal);

        Assert.Throws<InvalidDataException>(() => MouseChunkReader.Read(malformed));
    }

    [Fact]
    public void ReaderClassifiesCanonicalArithmeticOverflowAsInvalidData()
    {
        MouseChunkEncoded encoded = MouseChunkCodec.Encode(
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            [
                new MouseSpoolRecord(1, long.MaxValue, 0, 0, 0, false, 0, 0, 0),
                new MouseSpoolRecord(2, long.MaxValue, 0, 0, 0, false, 0, 0, 0)
            ]);
        byte[] canonical = Decompress(encoded.FileBytes);
        canonical[9] = 1;
        byte[] malformed = RebuildWithCanonical(encoded.FileBytes, canonical);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => MouseChunkReader.Read(malformed));
        Assert.IsType<OverflowException>(error.InnerException);
    }

    [Fact]
    public void ChunkHeaderAndPayloadDoNotContainPrivateNamesOrPaths()
    {
        const string privateText = @"\\?\HID#private-interface User Alias window-title process.exe keyboard";
        MouseChunkEncoded encoded = MouseChunkCodec.Encode(
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            [new MouseSpoolRecord(1, 1, 1, -1, 0, false, 0, 0, 0)]);

        string decodedAsText = Encoding.UTF8.GetString(encoded.FileBytes);
        foreach (string token in privateText.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            Assert.DoesNotContain(token, decodedAsText, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal static byte[] Decompress(byte[] file)
    {
        long compressedLength = BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(100));
        using var input = new MemoryStream(file, MouseChunkFormat.HeaderSize, checked((int)compressedLength));
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        brotli.CopyTo(output);
        return output.ToArray();
    }

    internal static byte[] RebuildWithCanonical(byte[] original, byte[] canonical)
    {
        using var compressedStream = new MemoryStream();
        using (var brotli = new BrotliStream(compressedStream, CompressionLevel.Fastest, leaveOpen: true))
        {
            brotli.Write(canonical);
        }

        byte[] compressed = compressedStream.ToArray();
        byte[] rebuilt = new byte[MouseChunkFormat.HeaderSize + compressed.Length + MouseChunkFormat.FooterSize];
        original.AsSpan(0, MouseChunkFormat.HeaderSize).CopyTo(rebuilt);
        BinaryPrimitives.WriteInt64LittleEndian(rebuilt.AsSpan(92), canonical.LongLength);
        BinaryPrimitives.WriteInt64LittleEndian(rebuilt.AsSpan(100), compressed.LongLength);
        compressed.CopyTo(rebuilt.AsSpan(MouseChunkFormat.HeaderSize));
        int footerOffset = MouseChunkFormat.HeaderSize + compressed.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(rebuilt.AsSpan(footerOffset), Crc32C.Calculate(compressed));
        SHA256.HashData(canonical).CopyTo(rebuilt.AsSpan(footerOffset + sizeof(uint), 32));
        BinaryPrimitives.WriteUInt32LittleEndian(rebuilt.AsSpan(footerOffset + 36), MouseChunkFormat.EndMagic);
        return rebuilt;
    }
}
