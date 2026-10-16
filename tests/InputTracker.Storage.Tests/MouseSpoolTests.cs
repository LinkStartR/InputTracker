namespace InputTracker.Storage.Tests;

public sealed class MouseSpoolTests
{
    [Fact]
    public void SelfDescribingSegmentHeaderIsFlushedAndRoundTripsFromSequence129()
    {
        using var directory = new TemporaryDirectory();
        string path = System.IO.Path.Combine(directory.Path, "segment.mspool");
        var header = new MouseSpoolHeader(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 17, 129);

        using (var writer = new MouseSpoolWriter(path, header))
        {
            Assert.Equal(MouseSpoolHeaderCodec.HeaderSize, writer.Length);
            writer.Append(Record(129));
            writer.Append(Record(130));
            writer.FlushFile();
        }

        MouseSpoolSegment segment = MouseSpoolSegmentReader.Read(path);
        Assert.Equal(header, segment.Header);
        Assert.Equal([Record(129), Record(130)], segment.Records);
        Assert.Equal(MouseSpoolHeaderCodec.HeaderSize + 2 * MouseSpoolRecordCodec.RecordSize, segment.ValidLength);
    }

    [Fact]
    public void IncompleteOrCorruptSelfDescribingSegmentFailsWithoutTruncatingOrRepairing()
    {
        using var directory = new TemporaryDirectory();
        string incomplete = System.IO.Path.Combine(directory.Path, "incomplete.mspool");
        File.WriteAllBytes(incomplete, new byte[MouseSpoolHeaderCodec.HeaderSize - 1]);
        byte[] incompleteBefore = File.ReadAllBytes(incomplete);
        Assert.Throws<InvalidDataException>(() => MouseSpoolSegmentReader.Read(incomplete));
        Assert.Equal(incompleteBefore, File.ReadAllBytes(incomplete));

        string corrupt = System.IO.Path.Combine(directory.Path, "corrupt.mspool");
        var header = new MouseSpoolHeader(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 3, 1);
        using (var writer = new MouseSpoolWriter(corrupt, header))
        {
            writer.Append(Record(1));
            writer.FlushFile();
        }

        byte[] corruptBefore = File.ReadAllBytes(corrupt);
        corruptBefore[^1] ^= 1;
        File.WriteAllBytes(corrupt, corruptBefore);
        Assert.Throws<InvalidDataException>(() => new MouseSpoolWriter(corrupt, header));
        Assert.Equal(corruptBefore, File.ReadAllBytes(corrupt));
    }

    [Fact]
    public void RecoveryKeepsValidPrefixAndPreciselyReportsTruncatedTail()
    {
        using var directory = new TemporaryDirectory();
        string path = System.IO.Path.Combine(directory.Path, "mouse.open");
        using (var writer = new MouseSpoolWriter(path))
        {
            for (int index = 1; index <= 10; index++) writer.Append(Record(index));
            writer.FlushFile();
        }

        byte[] partial = MouseSpoolRecordCodec.Encode(Record(11));
        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            stream.Write(partial.AsSpan(0, partial.Length / 2));
            stream.Flush(flushToDisk: true);
        }

        SpoolRecoveryResult result = MouseSpoolRecovery.Recover(path, truncateIncompleteTail: true);

        Assert.Equal(10, result.Recovered);
        Assert.Equal(1, result.Lost);
        Assert.Equal(0, result.Corrupt);
        Assert.Equal(10 * MouseSpoolRecordCodec.RecordSize, result.ValidLength);
        Assert.Equal(result.ValidLength, new FileInfo(path).Length);
    }

    [Fact]
    public void RecoveryFailsClosedOnCrcCorruptionWithoutTruncatingValidData()
    {
        using var directory = new TemporaryDirectory();
        string path = System.IO.Path.Combine(directory.Path, "mouse.open");
        using (var writer = new MouseSpoolWriter(path))
        {
            writer.Append(Record(1));
            writer.Append(Record(2));
            writer.FlushFile();
        }

        byte[] before = File.ReadAllBytes(path);
        before[MouseSpoolRecordCodec.RecordSize + 20] ^= 0x01;
        File.WriteAllBytes(path, before);
        long length = new FileInfo(path).Length;

        InvalidDataException error = Assert.Throws<InvalidDataException>(
            () => MouseSpoolRecovery.Recover(path, truncateIncompleteTail: true));

        Assert.Contains("valid prefix", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(length, new FileInfo(path).Length);
    }

    [Fact]
    public void SinkAcknowledgementIsDistinctFromFileFlush()
    {
        using var directory = new TemporaryDirectory();
        string path = System.IO.Path.Combine(directory.Path, "mouse.open");
        using var writer = new MouseSpoolWriter(path);

        for (int index = 1; index <= 1000; index++) writer.Append(Record(index));

        Assert.Equal(1000, writer.Accepted);
        Assert.Equal(1000, writer.SinkAcknowledged);
        Assert.Equal(1000, writer.SpoolAppended);

        SpoolFlushResult flush = writer.FlushFile();
        Assert.Equal(1000, flush.FileFlushedCount);
        Assert.Equal(1000 * MouseSpoolRecordCodec.RecordSize, flush.FlushedLength);
    }

    private static MouseSpoolRecord Record(long sequence) => new(
        sequence,
        1_000_000 + sequence,
        (int)(sequence % 11) - 5,
        (int)(sequence % 7) - 3,
        (byte)(sequence & 1),
        (sequence & 2) != 0,
        (ushort)(sequence % 1024),
        (short)(sequence % 3 == 0 ? 120 : 0),
        (short)(sequence % 5 == 0 ? -120 : 0));
}
