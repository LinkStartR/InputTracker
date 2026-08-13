namespace InputTracker.Storage.Tests;

public sealed class StorageMouseQueryTests
{
    [Fact]
    [Trait("Category", "StorageQueryScale")]
    public async Task StreamsOneMillionCommittedRecordsWithExactCountHashAndBoundedLiveMemory()
    {
        const int total = 1_000_000;
        const int recordsPerSegment = 4096;
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid deviceId = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();
        TimeCalibrationSegment calibration = StorageServiceTestSupport.Calibration(
            "UTC", new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero));
        calibration = calibration with { SessionId = sessionId };
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock()))
        {
            database.RegisterDevice(deviceId, (int)StorageDeviceKind.Mouse, 0, 1);
            long calibrationId = database.RecordCalibration(calibration);
            for (int offset = 0; offset < total; offset += recordsPerSegment)
            {
                int count = Math.Min(recordsPerSegment, total - offset);
                long first = offset + 1L;
                Guid segmentId = Guid.NewGuid();
                string path = System.IO.Path.Combine(
                    layout.SpoolDirectory, segmentId.ToString("N") + ".mspool");
                using (var writer = new MouseSpoolWriter(
                    path,
                    new MouseSpoolHeader(segmentId, deviceId, sessionId, calibrationId, first)))
                {
                    for (int index = 0; index < count; index++)
                    {
                        long sequence = first + index;
                        writer.Append(new MouseSpoolRecord(
                            sequence,
                            sequence,
                            checked((int)sequence),
                            checked(-(int)sequence),
                            (byte)(sequence & 1),
                            (sequence & 2) != 0,
                            (ushort)(sequence & 0x03ff),
                            checked((short)(sequence % short.MaxValue)),
                            checked((short)-(sequence % short.MaxValue))));
                    }

                    writer.FlushFile();
                }

                database.CommitMouseSpoolSegment(
                    MouseSpoolSegmentReader.Read(path),
                    updatedUtcMicroseconds: offset + count);
            }
        }

        using StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock());
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long baseline = GC.GetTotalMemory(forceFullCollection: false);
        long peakGrowth = 0;
        long observed = 0;
        ulong hash = 14695981039346656037UL;
        await foreach (StorageMouseRecord record in service.StreamMouseAsync(
            deviceId,
            new StorageUtcRange(
                calibration.UtcAnchorMicroseconds,
                calibration.UtcAnchorMicroseconds + total + 1L)))
        {
            observed++;
            hash = unchecked((hash ^ (ulong)record.Sequence) * 1099511628211UL);
            hash = unchecked((hash ^ (uint)record.DeltaX) * 1099511628211UL);
            if ((observed & 0x3fff) == 0)
            {
                peakGrowth = Math.Max(peakGrowth, GC.GetTotalMemory(forceFullCollection: false) - baseline);
            }
        }

        ulong expectedHash = 14695981039346656037UL;
        for (long sequence = 1; sequence <= total; sequence++)
        {
            expectedHash = unchecked((expectedHash ^ (ulong)sequence) * 1099511628211UL);
            expectedHash = unchecked((expectedHash ^ (uint)sequence) * 1099511628211UL);
        }

        Assert.Equal(total, observed);
        Assert.Equal(expectedHash, hash);
        Assert.InRange(peakGrowth, 0, 128L * 1024L * 1024L);
    }

    [Fact]
    public async Task StreamsAuthoritativeCommittedSpoolOnceAndFiltersUtcHalfOpenRange()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid deviceId = RegisterMouse(layout);
        var clock = new ControllableStorageClock();
        TimeCalibrationSegment calibration = StorageServiceTestSupport.Calibration(
            "UTC", new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero));
        using StorageService service = StorageService.OpenForTests(layout, clock);
        service.Start(calibration);
        for (int index = 0; index < 4; index++)
        {
            service.WriteMouseTransition(Point(deviceId, calibration, index * 1_000_000L) with
            {
                DeltaX = index,
                DeltaY = -index,
                MotionKind = (byte)(index % 2),
                UsesVirtualDesktop = index % 2 == 0,
                ButtonChanges = (ushort)(1 << index),
                VerticalWheelDelta = (short)(index * 10),
                HorizontalWheelDelta = (short)(-index * 10)
            });
        }

        service.Checkpoint(3_000_000);
        long utc = calibration.UtcAnchorMicroseconds;
        StorageMouseRecord[] records = await CollectAsync(service.StreamMouseAsync(
            deviceId,
            new StorageUtcRange(utc + 1_000_000, utc + 3_000_000)));

        Assert.Equal([2L, 3L], records.Select(record => record.Sequence));
        Assert.Equal([1, 2], records.Select(record => record.DeltaX));
        Assert.All(records, record => Assert.Equal(deviceId, record.DeviceId));
        Assert.Equal(2, records.Length);
        Assert.NotEmpty(Directory.EnumerateFiles(layout.ChunkDirectory, "*.mchunk"));
    }

    [Fact]
    public async Task SnapshotExcludesTailCommittedAfterQueryWasCreated()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid deviceId = RegisterMouse(layout);
        var clock = new ControllableStorageClock();
        TimeCalibrationSegment calibration = StorageServiceTestSupport.Calibration(
            "UTC", new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero));
        using StorageService service = StorageService.OpenForTests(layout, clock);
        service.Start(calibration);
        service.WriteMouseTransition(Point(deviceId, calibration, 0));
        service.WriteMouseTransition(Point(deviceId, calibration, 1));
        service.Checkpoint(1);

        IAsyncEnumerable<StorageMouseRecord> snapshot = service.StreamMouseAsync(
            deviceId,
            new StorageUtcRange(calibration.UtcAnchorMicroseconds, calibration.UtcAnchorMicroseconds + 10));
        service.WriteMouseTransition(Point(deviceId, calibration, 2));
        service.Checkpoint(2);

        StorageMouseRecord[] records = await CollectAsync(snapshot);
        Assert.Equal([1L, 2L], records.Select(record => record.Sequence));
        Assert.Equal(2, records.Length);
        Assert.Equal(3, service.Health.DurableWatermark.MouseSequenceByDevice[deviceId]);
    }

    [Fact]
    public async Task LaterSegmentCorruptionFailsBeforeFirstRecordIsReturned()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid deviceId = RegisterMouse(layout);
        var clock = new ControllableStorageClock();
        TimeCalibrationSegment calibration = StorageServiceTestSupport.Calibration(
            "UTC", new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero));
        using StorageService service = StorageService.OpenForTests(layout, clock);
        service.Start(calibration);
        service.WriteMouseTransition(Point(deviceId, calibration, 0));
        service.Checkpoint(0);
        service.WriteMouseTransition(Point(deviceId, calibration, 1));
        service.Checkpoint(1);
        IAsyncEnumerable<StorageMouseRecord> snapshot = service.StreamMouseAsync(
            deviceId,
            new StorageUtcRange(calibration.UtcAnchorMicroseconds, calibration.UtcAnchorMicroseconds + 10));

        string later = Directory.EnumerateFiles(layout.SpoolDirectory, "*.mspool")
            .OrderBy(path => MouseSpoolSegmentReader.Read(path).Header.FirstSequence)
            .Last();
        byte[] bytes = File.ReadAllBytes(later);
        bytes[^1] ^= 1;
        File.WriteAllBytes(later, bytes);

        await using IAsyncEnumerator<StorageMouseRecord> enumerator = snapshot.GetAsyncEnumerator();
        await Assert.ThrowsAsync<InvalidDataException>(async () => await enumerator.MoveNextAsync());
    }

    [Fact]
    public async Task UnknownKeyboardEmptyDeviceAndInvalidRangeFailClosed()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid keyboard = StorageServiceTestSupport.RegisterKeyboard(layout);
        Guid mouse = RegisterMouse(layout);
        using StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock());

        Assert.Throws<ArgumentException>(() => service.StreamMouseAsync(
            Guid.Empty, new StorageUtcRange(0, 1)));
        Assert.Throws<InvalidDataException>(() => service.StreamMouseAsync(
            Guid.NewGuid(), new StorageUtcRange(0, 1)));
        Assert.Throws<InvalidDataException>(() => service.StreamMouseAsync(
            keyboard, new StorageUtcRange(0, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.StreamMouseAsync(
            mouse, new StorageUtcRange(1, 1)));
        await Task.CompletedTask;
    }

    private static async Task<StorageMouseRecord[]> CollectAsync(
        IAsyncEnumerable<StorageMouseRecord> source)
    {
        var result = new List<StorageMouseRecord>();
        await foreach (StorageMouseRecord record in source) result.Add(record);
        return result.ToArray();
    }

    private static Guid RegisterMouse(StorageLayout layout)
    {
        Guid deviceId = Guid.NewGuid();
        using StorageDatabase database = StorageDatabase.Open(layout, new FakeClock());
        database.RegisterDevice(deviceId, (int)StorageDeviceKind.Mouse, 0, 1);
        return deviceId;
    }

    private static StorageMouseTransition Point(
        Guid deviceId,
        TimeCalibrationSegment calibration,
        long timestamp) => new(
            deviceId,
            calibration.SessionId,
            timestamp,
            DeltaX: 1,
            DeltaY: -1,
            MotionKind: 0,
            UsesVirtualDesktop: false,
            ButtonChanges: 0,
            VerticalWheelDelta: 0,
            HorizontalWheelDelta: 0);
}
