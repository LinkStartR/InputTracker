namespace InputTracker.Storage.Tests;

public sealed class MouseChunkBuilderTests
{
    [Fact]
    public void EventLimitSealsExactly4096InOrderWithExactCanonicalLength()
    {
        Guid device = Guid.NewGuid();
        var builder = new MouseChunkBuilder(timestampFrequency: 1_000_000_000);
        MouseChunkBatch? sealedBatch = null;

        for (int index = 0; index < MouseChunkBuilder.MaxEvents; index++)
        {
            MouseChunkAppendResult result = builder.Append(device, 1, Record(index + 1, index));
            sealedBatch = result.Sealed ?? sealedBatch;
        }

        Assert.NotNull(sealedBatch);
        Assert.Equal(MouseChunkSealReason.EventLimit, sealedBatch.Reason);
        Assert.Equal(MouseChunkBuilder.MaxEvents, sealedBatch.Records.Count);
        Assert.Equal(Enumerable.Range(1, MouseChunkBuilder.MaxEvents).Select(value => (long)value), sealedBatch.Records.Select(record => record.Sequence));
        Assert.Equal(0, builder.BufferedEvents);
        AssertCanonicalLength(device, sealedBatch);
        Assert.InRange(sealedBatch.CanonicalLength, 1, MouseChunkBuilder.MaxCanonicalBytes);
    }

    [Fact]
    public void DurationBoundarySealsAtOneSecondAndTimerBoundaryWithoutLoss()
    {
        Guid device = Guid.NewGuid();
        var builder = new MouseChunkBuilder(timestampFrequency: 100);
        builder.Append(device, 1, Record(1, 0));
        MouseChunkAppendResult crossing = builder.Append(device, 1, Record(2, 100));

        Assert.NotNull(crossing.Sealed);
        Assert.Equal(MouseChunkSealReason.DurationLimit, crossing.Sealed.Reason);
        Assert.Equal([1L, 2L], crossing.Sealed.Records.Select(record => record.Sequence));
        Assert.Equal(0, crossing.BufferedEvents);
        AssertCanonicalLength(device, crossing.Sealed);

        builder.Append(device, 1, Record(3, 101));
        Assert.Null(builder.SealAtTimestamp(200));
        MouseChunkBatch timerSealed = builder.SealAtTimestamp(201)!;
        Assert.Equal(MouseChunkSealReason.DurationLimit, timerSealed.Reason);
        Assert.Equal(3, timerSealed.Records.Single().Sequence);
    }

    [Fact]
    public void CanonicalSizeBoundarySealsPreviousBatchAndKeepsCurrentEvent()
    {
        Guid device = Guid.NewGuid();
        var builder = new MouseChunkBuilder(timestampFrequency: long.MaxValue, maxEvents: 100, maxCanonicalBytes: 32);
        MouseChunkBatch? sealedBatch = null;
        var accepted = new List<long>();
        for (int index = 0; index < 10 && sealedBatch is null; index++)
        {
            MouseSpoolRecord record = new(
                index + 1,
                index,
                index % 2 == 0 ? int.MinValue : int.MaxValue,
                index % 2 == 0 ? int.MaxValue : int.MinValue,
                0,
                false,
                0x03FF,
                short.MinValue,
                short.MaxValue);
            accepted.Add(record.Sequence);
            sealedBatch = builder.Append(device, 1, record).Sealed;
        }

        Assert.NotNull(sealedBatch);
        Assert.Equal(MouseChunkSealReason.CanonicalSizeLimit, sealedBatch.Reason);
        Assert.True(sealedBatch.CanonicalLength <= 32);
        MouseChunkBatch remaining = builder.SealRemaining()!;
        Assert.Equal(accepted, sealedBatch.Records.Concat(remaining.Records).Select(record => record.Sequence));
        AssertCanonicalLength(device, sealedBatch);
        AssertCanonicalLength(device, remaining);
    }

    [Fact]
    public void IdentityPauseAndStopBoundariesSealOnlyCurrentMatchingBuffer()
    {
        Guid firstDevice = Guid.NewGuid();
        Guid otherDevice = Guid.NewGuid();
        var builder = new MouseChunkBuilder(timestampFrequency: 1000);
        builder.Append(firstDevice, 10, Record(1, 1));

        Assert.Null(builder.SealForDeviceRemoval(otherDevice));
        Assert.Null(builder.SealForCalibrationClose(11));
        MouseChunkBatch removed = builder.SealForDeviceRemoval(firstDevice)!;
        Assert.Equal(MouseChunkSealReason.DeviceBoundary, removed.Reason);

        builder.Append(firstDevice, 10, Record(2, 2));
        MouseChunkBatch calibration = builder.SealForCalibrationClose(10)!;
        Assert.Equal(MouseChunkSealReason.CalibrationBoundary, calibration.Reason);

        builder.Append(firstDevice, 11, Record(3, 3));
        MouseChunkBatch paused = builder.SealForPause()!;
        Assert.Equal(MouseChunkSealReason.PauseBoundary, paused.Reason);

        builder.Append(otherDevice, 12, Record(4, 4));
        MouseChunkBatch stopped = builder.SealRemaining()!;
        Assert.Equal(MouseChunkSealReason.ExplicitFlush, stopped.Reason);
        Assert.Equal([1L, 2L, 3L, 4L], new[] { removed, calibration, paused, stopped }.SelectMany(batch => batch.Records).Select(record => record.Sequence));
    }

    [Fact]
    public void ArrivalIdentityBoundarySealsOldBatchAndAcceptsNewEvent()
    {
        Guid firstDevice = Guid.NewGuid();
        Guid secondDevice = Guid.NewGuid();
        var builder = new MouseChunkBuilder(timestampFrequency: 1000);
        builder.Append(firstDevice, 1, Record(1, 1));

        MouseChunkAppendResult deviceBoundary = builder.Append(secondDevice, 1, Record(2, 2));

        Assert.Equal(MouseChunkSealReason.DeviceBoundary, deviceBoundary.Sealed!.Reason);
        Assert.Equal(1, deviceBoundary.Sealed.Records.Single().Sequence);
        Assert.Equal(2, builder.SealRemaining()!.Records.Single().Sequence);
    }

    [Fact]
    public void RejectedRecordDoesNotMutateBufferedState()
    {
        Guid device = Guid.NewGuid();
        var builder = new MouseChunkBuilder(timestampFrequency: 1000);
        builder.Append(device, 1, Record(1, 1));
        long canonicalBefore = builder.BufferedCanonicalLength;

        Assert.Throws<InvalidDataException>(() => builder.Append(
            device,
            1,
            new MouseSpoolRecord(2, 2, 0, 0, 2, false, 0, 0, 0)));

        Assert.Equal(1, builder.BufferedEvents);
        Assert.Equal(canonicalBefore, builder.BufferedCanonicalLength);
        builder.Append(device, 1, Record(2, 2));
        Assert.Equal([1L, 2L], builder.SealRemaining()!.Records.Select(record => record.Sequence));
    }

    private static MouseSpoolRecord Record(long sequence, long timestamp) =>
        new(sequence, timestamp, checked((int)sequence), checked((int)-sequence), 0, false, 0, 0, 0);

    private static void AssertCanonicalLength(Guid deviceId, MouseChunkBatch batch)
    {
        MouseChunkEncoded encoded = MouseChunkCodec.Encode(
            Guid.NewGuid(), deviceId, batch.CalibrationSegmentId, batch.Records);
        Assert.Equal(batch.CanonicalLength, encoded.Header.CanonicalLength);
    }
}
