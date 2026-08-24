using Microsoft.Data.Sqlite;

namespace InputTracker.Storage.Tests;

public sealed class MouseStorageRecoveryTests
{
    [Fact]
    public void AdoptionCompleteRejectsV4FinalOrphanWithoutAdvancingOrCreatingSpool()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        using (StorageService marker = StorageService.OpenForTests(layout, new ControllableStorageClock())) { }
        Guid deviceId = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();
        long calibrationId;
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock()))
        {
            database.RegisterDevice(deviceId, 0, 0, 1);
            calibrationId = database.RecordCalibration(TimeCalibrationSegment.Capture(sessionId, new FakeClock()));
            using SqliteCommand trigger = database.Connection.CreateCommand();
            trigger.CommandText = """
                CREATE TRIGGER fail_orphan_catalog BEFORE INSERT ON mouse_chunks
                BEGIN SELECT RAISE(ABORT, 'orphan'); END;
                """;
            trigger.ExecuteNonQuery();
            Assert.Throws<SqliteException>(() => database.CommitMouseChunk(
                Guid.NewGuid(), deviceId, calibrationId, Records(1, 2), 2));
            trigger.CommandText = "DROP TRIGGER fail_orphan_catalog;";
            trigger.ExecuteNonQuery();
        }

        AssertOpenFailsInvalidData(layout, new ControllableStorageClock());
        Assert.Empty(Directory.GetFiles(layout.SpoolDirectory, "*.mspool"));
        using StorageDatabase verify = StorageDatabase.Open(layout, new FakeClock());
        Assert.Empty(verify.ReadMouseDeviceWatermarks());
        Assert.Equal(new StorageServiceOrdinals(0, 0), verify.ReadStorageServiceOrdinals());
    }

    [Fact]
    public void UniqueHeaderOnlySpoolIsReusedAndHeaderPrefixRemainsUnchanged()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid deviceId = Guid.NewGuid();
        var clock = new ControllableStorageClock();
        TimeCalibrationSegment calibration = StorageServiceTestSupport.Calibration(
            "UTC", new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero));
        long calibrationId;
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock()))
        {
            database.RegisterDevice(deviceId, 0, 0, 1);
            calibrationId = database.RecordCalibration(calibration);
        }

        Guid segmentId = Guid.NewGuid();
        string path = System.IO.Path.Combine(layout.SpoolDirectory, segmentId.ToString("N") + ".mspool");
        using (var writer = new MouseSpoolWriter(
            path, new MouseSpoolHeader(segmentId, deviceId, calibration.SessionId, calibrationId, 1))) { }
        byte[] headerPrefix = File.ReadAllBytes(path);

        using (StorageService service = StorageService.OpenForTests(layout, clock))
        {
            service.Start(calibration);
            service.WriteMouseTransition(new StorageMouseTransition(
                deviceId, calibration.SessionId, 0, 1, -1, 0, false, 0, 0, 0));
            service.Checkpoint(0);
        }

        Assert.Equal(headerPrefix, File.ReadAllBytes(path)[..MouseSpoolHeaderCodec.HeaderSize]);
        Assert.Single(Directory.GetFiles(layout.SpoolDirectory, "*.mspool"));
        Assert.Equal([1L], MouseSpoolSegmentReader.Read(path).Records.Select(record => record.Sequence));
    }

    [Fact]
    public void CompetingHeaderOnlySpoolsForOneDeviceFailClosed()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid deviceId = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();
        long calibrationId;
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock()))
        {
            database.RegisterDevice(deviceId, 0, 0, 1);
            calibrationId = database.RecordCalibration(TimeCalibrationSegment.Capture(sessionId, new FakeClock()));
        }

        _ = WriteHeaderOnly(layout, deviceId, sessionId, calibrationId, 1);
        _ = WriteHeaderOnly(layout, deviceId, sessionId, calibrationId, 1);

        AssertOpenFailsInvalidData(layout, new ControllableStorageClock());
    }

    [Fact]
    public void MissingPublishedRowsCannotBeHiddenByPersistedWatermark()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid deviceId = Guid.NewGuid();
        var clock = new ControllableStorageClock();
        TimeCalibrationSegment calibration = StorageServiceTestSupport.Calibration(
            "UTC", new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero));
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock()))
        {
            database.RegisterDevice(deviceId, 0, 0, 1);
        }
        using (StorageService service = StorageService.OpenForTests(layout, clock))
        {
            service.Start(calibration);
            service.WriteMouseTransition(new StorageMouseTransition(
                deviceId, calibration.SessionId, 0, 1, 1, 0, false, 0, 0, 0));
            service.Checkpoint(0);
        }
        using (StorageDatabase corrupt = StorageDatabase.Open(layout, new FakeClock()))
        {
            using SqliteCommand delete = corrupt.Connection.CreateCommand();
            delete.CommandText = "DELETE FROM mouse_spool_segments;";
            delete.ExecuteNonQuery();
        }

        AssertOpenFailsInvalidData(layout, clock);
    }

    [Fact]
    public void MiddlePublishedRangeGapCannotBeHiddenByPersistedWatermark()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid deviceId = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock()))
        {
            database.RegisterDevice(deviceId, 0, 0, 1);
            long calibrationId = database.RecordCalibration(TimeCalibrationSegment.Capture(sessionId, new FakeClock()));
            database.CommitMouseSpoolSegment(
                MouseSpoolSegmentReader.Read(WriteUnpublishedSpool(layout, deviceId, sessionId, calibrationId, 1, 1)), 1);
            database.CommitMouseSpoolSegment(
                MouseSpoolSegmentReader.Read(WriteUnpublishedSpool(layout, deviceId, sessionId, calibrationId, 2, 1)), 2);
            database.CommitMouseSpoolSegment(
                MouseSpoolSegmentReader.Read(WriteUnpublishedSpool(layout, deviceId, sessionId, calibrationId, 3, 1)), 3);
            using SqliteCommand delete = database.Connection.CreateCommand();
            delete.CommandText = "DELETE FROM mouse_spool_segments WHERE first_sequence = 2;";
            delete.ExecuteNonQuery();
        }

        AssertOpenFailsInvalidData(layout, new ControllableStorageClock());
    }

    [Fact]
    public void SpoolCalibrationSessionMustMatchPersistedCalibrationRow()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid deviceId = Guid.NewGuid();
        long calibrationId;
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock()))
        {
            database.RegisterDevice(deviceId, 0, 0, 1);
            calibrationId = database.RecordCalibration(TimeCalibrationSegment.Capture(Guid.NewGuid(), new FakeClock()));
        }

        _ = WriteUnpublishedSpool(layout, deviceId, Guid.NewGuid(), calibrationId, 1, 1);

        AssertOpenFailsInvalidData(layout, new ControllableStorageClock());
    }

    [Fact]
    public void ReparsePointSpoolDirectoryFailsBeforeArtifactRead()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        string external = System.IO.Path.Combine(directory.Path, "external-spool");
        Directory.CreateDirectory(external);
        Directory.Delete(layout.SpoolDirectory);
        using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            "cmd.exe",
            $"/d /c mklink /J \"{layout.SpoolDirectory}\" \"{external}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!)
        {
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
        }

        try
        {
            AssertOpenFailsInvalidData(layout, new ControllableStorageClock());
        }
        finally
        {
            Directory.Delete(layout.SpoolDirectory);
        }
    }

    [Fact]
    public void UniqueHeaderlessLegacySpoolEquivalentToChunkIsReadOnlyObservation()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid deviceId = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();
        string legacyPath = System.IO.Path.Combine(layout.SpoolDirectory, "legacy.open");
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock()))
        {
            database.RegisterDevice(deviceId, 0, 0, 1);
            long calibrationId = database.RecordCalibration(TimeCalibrationSegment.Capture(sessionId, new FakeClock()));
            database.CommitMouseChunk(Guid.NewGuid(), deviceId, calibrationId, Records(1, 2), 1);
            using (var writer = new MouseSpoolWriter(legacyPath))
            {
                foreach (MouseSpoolRecord record in Records(1, 2)) writer.Append(record);
                writer.FlushFile();
            }
            using SqliteCommand legacy = database.Connection.CreateCommand();
            legacy.CommandText = """
                INSERT INTO mouse_spool_commits(spool_id, device_id, committed_length, record_count, power_durable_count, updated_utc_us)
                VALUES ('legacy', $device, $length, 2, 2, 1);
                """;
            legacy.Parameters.AddWithValue("$device", deviceId.ToString("N"));
            legacy.Parameters.AddWithValue("$length", new FileInfo(legacyPath).Length);
            legacy.ExecuteNonQuery();
            DowngradeToVersionThree(database);
        }
        byte[] before = File.ReadAllBytes(legacyPath);

        using StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock());

        Assert.Equal(2, service.Health.DurableWatermark.MouseSequenceByDevice[deviceId]);
        Assert.Equal(before, File.ReadAllBytes(legacyPath));
    }
    [Fact]
    public void CrashBeforeDirectoryRowPublishesValidatedSelfDescribingSpoolOnOpen()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid deviceId = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();
        long calibrationId;
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock()))
        {
            database.RegisterDevice(deviceId, kind: 0, identityConfidence: 0, createdUtcMicroseconds: 1);
            calibrationId = database.RecordCalibration(TimeCalibrationSegment.Capture(sessionId, new FakeClock()));
        }

        WriteUnpublishedSpool(layout, deviceId, sessionId, calibrationId, 1, 2);

        using StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock());

        Assert.Equal(StorageServiceState.Created, service.Health.State);
        Assert.Equal(2, service.Health.DurableWatermark.MouseSequenceByDevice[deviceId]);
        Assert.Equal(1, service.Health.DurableWatermark.CommitSequence);
    }

    [Fact]
    public void AdoptionCompleteStillPublishesValidatedV4SpoolAfterCrashBeforeDirectoryRow()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        using (StorageService marker = StorageService.OpenForTests(layout, new ControllableStorageClock())) { }
        Guid deviceId = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();
        long calibrationId;
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock()))
        {
            database.RegisterDevice(deviceId, kind: 0, identityConfidence: 0, createdUtcMicroseconds: 1);
            calibrationId = database.RecordCalibration(TimeCalibrationSegment.Capture(sessionId, new FakeClock()));
        }

        WriteUnpublishedSpool(layout, deviceId, sessionId, calibrationId, 1, 2);

        using StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock());
        Assert.Equal(2, service.Health.DurableWatermark.MouseSequenceByDevice[deviceId]);
        Assert.Equal(1, service.Health.DurableWatermark.CommitSequence);
    }

    [Fact]
    public void CorruptUnpublishedSpoolFailsOpenAndRemainsByteIdentical()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid deviceId = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();
        long calibrationId;
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock()))
        {
            database.RegisterDevice(deviceId, kind: 0, identityConfidence: 0, createdUtcMicroseconds: 1);
            calibrationId = database.RecordCalibration(TimeCalibrationSegment.Capture(sessionId, new FakeClock()));
        }

        string path = WriteUnpublishedSpool(layout, deviceId, sessionId, calibrationId, 1, 1);
        byte[] corrupted = File.ReadAllBytes(path);
        corrupted[^1] ^= 1;
        File.WriteAllBytes(path, corrupted);

        Assert.Throws<InvalidDataException>(() =>
            StorageService.OpenForTests(layout, new ControllableStorageClock()));
        Assert.Equal(corrupted, File.ReadAllBytes(path));
    }

    [Fact]
    public void ContinuousLegacyV3ChunksBecomeCreateOnlySpoolsAndRepeatedOpenIsIdempotent()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid deviceId = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();
        Guid chunkId = Guid.NewGuid();
        long calibrationId;
        string chunkPath;
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock()))
        {
            database.RegisterDevice(deviceId, kind: 0, identityConfidence: 0, createdUtcMicroseconds: 1);
            calibrationId = database.RecordCalibration(TimeCalibrationSegment.Capture(sessionId, new FakeClock()));
            MouseChunkDurableWatermark chunk = database.CommitMouseChunk(
                chunkId, deviceId, calibrationId, Records(1, 2), 2);
            chunkPath = System.IO.Path.Combine(layout.ChunkDirectory, chunk.RelativePath);
            DowngradeToVersionThree(database);
        }

        byte[] originalChunk = File.ReadAllBytes(chunkPath);
        using (StorageService first = StorageService.OpenForTests(layout, new ControllableStorageClock()))
        {
            Assert.Equal(2, first.Health.DurableWatermark.MouseSequenceByDevice[deviceId]);
        }

        string[] spoolsAfterFirst = Directory.GetFiles(layout.SpoolDirectory, "*.mspool");
        Assert.Single(spoolsAfterFirst);
        using (StorageService second = StorageService.OpenForTests(layout, new ControllableStorageClock()))
        {
            Assert.Equal(2, second.Health.DurableWatermark.MouseSequenceByDevice[deviceId]);
        }

        Assert.Equal(spoolsAfterFirst, Directory.GetFiles(layout.SpoolDirectory, "*.mspool"));
        Assert.Equal(originalChunk, File.ReadAllBytes(chunkPath));
    }

    [Fact]
    public void LegacyV3DeviceGlobalGapFailsBeforeCreatingAnySpool()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid deviceId = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock()))
        {
            database.RegisterDevice(deviceId, kind: 0, identityConfidence: 0, createdUtcMicroseconds: 1);
            long calibrationId = database.RecordCalibration(TimeCalibrationSegment.Capture(sessionId, new FakeClock()));
            database.CommitMouseChunk(Guid.NewGuid(), deviceId, calibrationId, Records(1, 1), 1);
            database.CommitMouseChunk(Guid.NewGuid(), deviceId, calibrationId, Records(3, 1), 2);
            DowngradeToVersionThree(database);
        }

        Assert.Throws<InvalidDataException>(() =>
            StorageService.OpenForTests(layout, new ControllableStorageClock()));
        Assert.Empty(Directory.GetFiles(layout.SpoolDirectory, "*.mspool"));
    }

    [Fact]
    public void RecoveryPreservesEveryPreexistingArtifactPathLengthAndHash()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid deviceId = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();
        long calibrationId;
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock()))
        {
            database.RegisterDevice(deviceId, kind: 0, identityConfidence: 0, createdUtcMicroseconds: 1);
            calibrationId = database.RecordCalibration(TimeCalibrationSegment.Capture(sessionId, new FakeClock()));
            database.CommitMouseChunk(Guid.NewGuid(), deviceId, calibrationId, Records(1, 2), 1);
            DowngradeToVersionThree(database);
        }

        _ = WriteUnpublishedSpool(layout, deviceId, sessionId, calibrationId, 1, 2);
        Guid tmpId = Guid.NewGuid();
        MouseChunkFile.WriteNew(
            System.IO.Path.Combine(layout.ChunkDirectory, tmpId.ToString("N") + ".tmp"),
            tmpId,
            deviceId,
            calibrationId,
            Records(1, 2));
        File.WriteAllBytes(System.IO.Path.Combine(layout.ChunkDirectory, "retained.corrupt"), [1, 2, 3, 4]);
        Dictionary<string, (long Length, string Hash)> before = SnapshotArtifacts(layout);

        using (StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock()))
        {
            Assert.Equal(2, service.Health.DurableWatermark.MouseSequenceByDevice[deviceId]);
        }

        Dictionary<string, (long Length, string Hash)> after = SnapshotArtifacts(layout);
        foreach ((string path, (long length, string hash)) in before)
        {
            Assert.True(after.TryGetValue(path, out var current));
            Assert.Equal(length, current.Length);
            Assert.Equal(hash, current.Hash);
        }
    }

    [Fact]
    public void CoveredValidTemporaryChunkIsCopiedCreateOnlyAndOriginalRemainsImmutable()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid deviceId = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();
        long calibrationId;
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock()))
        {
            database.RegisterDevice(deviceId, kind: 0, identityConfidence: 0, createdUtcMicroseconds: 1);
            calibrationId = database.RecordCalibration(TimeCalibrationSegment.Capture(sessionId, new FakeClock()));
        }

        _ = WriteUnpublishedSpool(layout, deviceId, sessionId, calibrationId, 1, 2);
        Guid temporaryId = Guid.NewGuid();
        string temporaryPath = System.IO.Path.Combine(
            layout.ChunkDirectory, temporaryId.ToString("N") + ".tmp");
        MouseChunkFile.WriteNew(
            temporaryPath, temporaryId, deviceId, calibrationId, Records(1, 2));
        byte[] before = File.ReadAllBytes(temporaryPath);

        using (StorageService first = StorageService.OpenForTests(layout, new ControllableStorageClock()))
        {
            Assert.Equal(2, first.Health.DurableWatermark.MouseSequenceByDevice[deviceId]);
        }

        Assert.Equal(before, File.ReadAllBytes(temporaryPath));
        using (StorageDatabase verify = StorageDatabase.Open(layout, new FakeClock()))
        {
            MouseChunkCatalogEntry copied = Assert.Single(verify.ReadMouseChunkCatalog());
            Assert.NotEqual(temporaryId, copied.ChunkId);
            Assert.Equal((1, 2), (copied.FirstSequence, copied.LastSequence));
        }

        using (StorageService second = StorageService.OpenForTests(layout, new ControllableStorageClock())) { }
        using StorageDatabase repeated = StorageDatabase.Open(layout, new FakeClock());
        Assert.Single(repeated.ReadMouseChunkCatalog());
        Assert.Equal(before, File.ReadAllBytes(temporaryPath));
    }

    [Fact]
    public void CorruptTemporaryChunkFailsOpenAndRemainsByteIdentical()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        string temporaryPath = System.IO.Path.Combine(
            layout.ChunkDirectory, Guid.NewGuid().ToString("N") + ".tmp");
        byte[] corrupt = [1, 2, 3, 4, 5];
        File.WriteAllBytes(temporaryPath, corrupt);

        Assert.Throws<InvalidDataException>(() =>
            StorageService.OpenForTests(layout, new ControllableStorageClock()));
        Assert.Equal(corrupt, File.ReadAllBytes(temporaryPath));
    }

    private static string WriteUnpublishedSpool(
        StorageLayout layout,
        Guid deviceId,
        Guid sessionId,
        long calibrationId,
        long firstSequence,
        int count)
    {
        Guid segmentId = Guid.NewGuid();
        string path = System.IO.Path.Combine(layout.SpoolDirectory, segmentId.ToString("N") + ".mspool");
        using var writer = new MouseSpoolWriter(
            path,
            new MouseSpoolHeader(segmentId, deviceId, sessionId, calibrationId, firstSequence));
        foreach (MouseSpoolRecord record in Records(firstSequence, count)) writer.Append(record);
        writer.FlushFile();
        return path;
    }

    private static string WriteHeaderOnly(
        StorageLayout layout,
        Guid deviceId,
        Guid sessionId,
        long calibrationId,
        long firstSequence)
    {
        Guid segmentId = Guid.NewGuid();
        string path = System.IO.Path.Combine(layout.SpoolDirectory, segmentId.ToString("N") + ".mspool");
        using var writer = new MouseSpoolWriter(
            path, new MouseSpoolHeader(segmentId, deviceId, sessionId, calibrationId, firstSequence));
        return path;
    }

    private static void AssertOpenFailsInvalidData(StorageLayout layout, ControllableStorageClock clock)
    {
        StorageService? unexpected = null;
        Exception? exception = Record.Exception(() => unexpected = StorageService.OpenForTests(layout, clock));
        unexpected?.Dispose();
        Assert.IsType<InvalidDataException>(exception);
    }

    private static MouseSpoolRecord[] Records(long firstSequence, int count) =>
        Enumerable.Range(0, count)
            .Select(index => new MouseSpoolRecord(
                firstSequence + index,
                firstSequence + index,
                index,
                -index,
                0,
                false,
                0,
                0,
                0))
            .ToArray();

    private static void DowngradeToVersionThree(StorageDatabase database)
    {
        using SqliteCommand command = database.Connection.CreateCommand();
        command.CommandText = """
            DROP TABLE mouse_device_watermarks;
            DROP TABLE mouse_spool_segments;
            DELETE FROM store_metadata WHERE key = 'mouse.v3-adoption-complete';
            DELETE FROM schema_migrations WHERE version >= 4;
            PRAGMA user_version = 3;
            """;
        command.ExecuteNonQuery();
    }

    private static Dictionary<string, (long Length, string Hash)> SnapshotArtifacts(StorageLayout layout) =>
        Directory.EnumerateFiles(layout.StorageDirectory, "*", SearchOption.AllDirectories)
            .Where(path => !string.Equals(path, layout.DatabasePath, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(path, layout.DatabasePath + "-wal", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(path, layout.DatabasePath + "-shm", StringComparison.OrdinalIgnoreCase)
                && !path.EndsWith("writer.lock", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(
                path => System.IO.Path.GetRelativePath(layout.StorageDirectory, path),
                path => (new FileInfo(path).Length, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))),
                StringComparer.OrdinalIgnoreCase);
}
