namespace InputTracker.Storage.Tests;

public sealed class MouseSpoolTests
{
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
