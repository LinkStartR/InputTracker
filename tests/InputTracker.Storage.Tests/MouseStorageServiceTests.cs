using Microsoft.Data.Sqlite;

namespace InputTracker.Storage.Tests;

public sealed class MouseStorageServiceTests
{
    [Theory]
    [InlineData("unknown-device")]
    [InlineData("wrong-calibration")]
    [InlineData("negative-time")]
    [InlineData("unknown-motion")]
    [InlineData("unknown-buttons")]
    public void InvalidMouseInputFaultsBeforeSequenceBuilderOrArtifactMutation(string invalidCase)
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid deviceId = RegisterMouse(layout);
        var clock = new ControllableStorageClock();
        TimeCalibrationSegment calibration = StorageServiceTestSupport.Calibration(
            "UTC", new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero));
        using StorageService service = StorageService.OpenForTests(layout, clock);
        service.Start(calibration);
        StorageDurableWatermark before = service.Health.DurableWatermark;
        StorageMouseTransition transition = Point(deviceId, calibration, 0) with
        {
            DeviceId = invalidCase == "unknown-device" ? Guid.NewGuid() : deviceId,
            CalibrationSessionId = invalidCase == "wrong-calibration" ? Guid.NewGuid() : calibration.SessionId,
            MonotonicTimestamp = invalidCase == "negative-time" ? -1 : 0,
            MotionKind = invalidCase == "unknown-motion" ? (byte)2 : (byte)0,
            ButtonChanges = invalidCase == "unknown-buttons" ? (ushort)0x0400 : (ushort)0
        };

        Assert.ThrowsAny<Exception>(() => service.WriteMouseTransition(transition));

        Assert.Equal(StorageServiceState.IntegrityFault, service.Health.State);
        StorageDurableWatermark after = service.Health.DurableWatermark;
        Assert.Equal(before.CommitSequence, after.CommitSequence);
        Assert.Equal(before.KeyboardAggregateCommitSequence, after.KeyboardAggregateCommitSequence);
        Assert.Equal(before.Scope, after.Scope);
        Assert.Equal(before.MouseSequenceByDevice.OrderBy(pair => pair.Key), after.MouseSequenceByDevice.OrderBy(pair => pair.Key));
        Assert.Empty(Directory.EnumerateFiles(layout.SpoolDirectory));
        Assert.Empty(ReadChunkRanges(layout));
    }

    [Fact]
    public void ExactDurationSealIncludesCurrentPointInPublishedSpoolAndChunk()
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
        service.WriteMouseTransition(Point(deviceId, calibration, 1_000_000) with { DeltaX = 7 });

        Assert.Equal(2, service.Health.DurableWatermark.MouseSequenceByDevice[deviceId]);
        Assert.Equal([(1L, 2L)], ReadChunkRanges(layout));
        Assert.Equal([1L, 2L], ReadSpoolSequences(layout));
    }

    [Fact]
    public void CrossingDurationSealExcludesCurrentUntilNextBoundary()
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
        service.WriteMouseTransition(Point(deviceId, calibration, 1_000_001) with { DeltaY = -9 });

        Assert.Equal(1, service.Health.DurableWatermark.MouseSequenceByDevice[deviceId]);
        Assert.Equal([(1L, 1L)], ReadChunkRanges(layout));
        service.Checkpoint(1_000_001);
        Assert.Equal(2, service.Health.DurableWatermark.MouseSequenceByDevice[deviceId]);
        Assert.Equal([(1L, 1L), (2L, 2L)], ReadChunkRanges(layout));
        Assert.Equal([1L, 2L], ReadSpoolSequences(layout));
    }

    [Fact]
    public void ChunkCatalogFailureHasUncertainAckButPublishedPrefixRemainsAuthoritative()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid deviceId = RegisterMouse(layout);
        using (StorageDatabase setup = StorageDatabase.Open(layout, new FakeClock()))
        {
            using SqliteCommand trigger = setup.Connection.CreateCommand();
            trigger.CommandText = """
                CREATE TRIGGER fail_service_chunk_catalog
                BEFORE INSERT ON mouse_chunks
                BEGIN SELECT RAISE(ABORT, 'test chunk catalog failure'); END;
                """;
            trigger.ExecuteNonQuery();
        }

        var clock = new ControllableStorageClock();
        TimeCalibrationSegment calibration = StorageServiceTestSupport.Calibration(
            "UTC", new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero));
        using (StorageService service = StorageService.OpenForTests(layout, clock))
        {
            service.Start(calibration);
            service.WriteMouseTransition(Point(deviceId, calibration, 0));

            Assert.Throws<SqliteException>(() =>
                service.WriteMouseTransition(Point(deviceId, calibration, 1_000_000)));

            Assert.Equal(StorageServiceState.IntegrityFault, service.Health.State);
            Assert.Equal(2, service.Health.DurableWatermark.MouseSequenceByDevice[deviceId]);
            Assert.Throws<InvalidOperationException>(() =>
                service.WriteMouseTransition(Point(deviceId, calibration, 1_000_001)));
        }

        using (StorageDatabase repair = StorageDatabase.Open(layout, new FakeClock()))
        {
            using SqliteCommand drop = repair.Connection.CreateCommand();
            drop.CommandText = "DROP TRIGGER fail_service_chunk_catalog;";
            drop.ExecuteNonQuery();
        }

        using StorageService reopened = StorageService.OpenForTests(layout, clock);
        Assert.Equal(2, reopened.Health.DurableWatermark.MouseSequenceByDevice[deviceId]);
        Assert.Equal(2, reopened.Health.DurableWatermark.CommitSequence);
    }

    [Fact]
    public void PausePublishesEveryDeviceInDeterministicIsolationAndPreventsWrites()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid first = RegisterMouse(layout);
        Guid second = RegisterMouse(layout);
        var clock = new ControllableStorageClock();
        TimeCalibrationSegment calibration = StorageServiceTestSupport.Calibration(
            "UTC", new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero));
        using StorageService service = StorageService.OpenForTests(layout, clock);
        service.Start(calibration);
        service.WriteMouseTransition(Point(second, calibration, 0) with { DeltaX = 22 });
        service.WriteMouseTransition(Point(first, calibration, 0) with { DeltaX = 11 });

        service.Pause(StoragePauseReason.User, 1);

        Assert.Equal(StorageServiceState.Paused, service.Health.State);
        Assert.Equal(1, service.Health.DurableWatermark.MouseSequenceByDevice[first]);
        Assert.Equal(1, service.Health.DurableWatermark.MouseSequenceByDevice[second]);
        Assert.Throws<InvalidOperationException>(() => service.WriteMouseTransition(Point(first, calibration, 2)));
    }

    [Fact]
    public void TenThousandMixedPointsAreNeverSampledDuplicatedOrReordered()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid deviceId = RegisterMouse(layout);
        var clock = new ControllableStorageClock();
        TimeCalibrationSegment calibration = StorageServiceTestSupport.Calibration(
            "UTC", new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero));
        var expected = new List<MouseSpoolRecord>(10_000);
        using (StorageService service = StorageService.OpenForTests(layout, clock))
        {
            service.Start(calibration);
            for (int index = 0; index < 10_000; index++)
            {
                var transition = new StorageMouseTransition(
                    deviceId,
                    calibration.SessionId,
                    index,
                    index % 2 == 0 ? int.MaxValue - index : int.MinValue + index,
                    index % 3 == 0 ? -index : index,
                    (byte)(index & 1),
                    (index & 2) != 0,
                    (ushort)(index % 1024),
                    (short)(index % 2 == 0 ? short.MaxValue : short.MinValue),
                    (short)(index % 2 == 0 ? short.MinValue : short.MaxValue));
                service.WriteMouseTransition(transition);
                expected.Add(new MouseSpoolRecord(
                    index + 1L,
                    transition.MonotonicTimestamp,
                    transition.DeltaX,
                    transition.DeltaY,
                    transition.MotionKind,
                    transition.UsesVirtualDesktop,
                    transition.ButtonChanges,
                    transition.VerticalWheelDelta,
                    transition.HorizontalWheelDelta));
            }

            service.Checkpoint(9_999);
            Assert.Equal(10_000, service.Health.DurableWatermark.MouseSequenceByDevice[deviceId]);
        }

        MouseSpoolRecord[] spool = Directory.EnumerateFiles(layout.SpoolDirectory, "*.mspool")
            .SelectMany(path => MouseSpoolSegmentReader.Read(path).Records)
            .OrderBy(record => record.Sequence)
            .ToArray();
        MouseSpoolRecord[] chunks = Directory.EnumerateFiles(layout.ChunkDirectory, "*.mchunk")
            .SelectMany(path => MouseChunkReader.Read(path).Records)
            .OrderBy(record => record.Sequence)
            .ToArray();
        Assert.Equal(expected, spool);
        Assert.Equal(expected, chunks);
        Assert.Equal(10_000, ReadCatalogEventCount(layout));
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

    private static Guid RegisterMouse(StorageLayout layout)
    {
        Guid deviceId = Guid.NewGuid();
        using StorageDatabase database = StorageDatabase.Open(layout, new FakeClock());
        database.RegisterDevice(deviceId, kind: 0, identityConfidence: 0, createdUtcMicroseconds: 1);
        return deviceId;
    }

    private static IReadOnlyList<(long First, long Last)> ReadChunkRanges(StorageLayout layout)
    {
        using var connection = new SqliteConnection($"Data Source={layout.DatabasePath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT first_sequence, last_sequence FROM mouse_chunks ORDER BY first_sequence;";
        using SqliteDataReader reader = command.ExecuteReader();
        var result = new List<(long, long)>();
        while (reader.Read()) result.Add((reader.GetInt64(0), reader.GetInt64(1)));
        return result;
    }

    private static IReadOnlyList<long> ReadSpoolSequences(StorageLayout layout) =>
        Directory.EnumerateFiles(layout.SpoolDirectory, "*.mspool")
            .SelectMany(path => MouseSpoolSegmentReader.Read(path).Records)
            .OrderBy(record => record.Sequence)
            .Select(record => record.Sequence)
            .ToArray();

    private static long ReadCatalogEventCount(StorageLayout layout)
    {
        using var connection = new SqliteConnection($"Data Source={layout.DatabasePath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(SUM(event_count), 0) FROM mouse_chunks;";
        return (long)command.ExecuteScalar()!;
    }
}
