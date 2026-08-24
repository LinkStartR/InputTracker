namespace InputTracker.Storage.Tests;

public sealed class MousePlaybackSnapshotTests
{
    [Fact]
    public void PublicPlaybackContractContainsOnlySnapshotMouseAndBoundaryFields()
    {
        Assert.Equal(
            ["DeviceId", "DurableWatermark", "First", "Last", "RecordCount", "SnapshotId", "UtcRange"],
            typeof(StorageMousePlaybackSnapshotInfo).GetProperties().Select(property => property.Name).Order().ToArray());
        Assert.Equal(
            ["Sequence", "SnapshotId", "UtcMicroseconds"],
            typeof(StorageMousePlaybackCursor).GetProperties().Select(property => property.Name).Order().ToArray());
        Assert.Equal(
            ["BoundaryReason", "Cursor", "Record", "StartsNewSegment"],
            typeof(StorageMousePlaybackRecord).GetProperties().Select(property => property.Name).Order().ToArray());
        foreach (Type type in new[]
        {
            typeof(StorageMousePlaybackSnapshotInfo),
            typeof(StorageMousePlaybackCursor),
            typeof(StorageMousePlaybackRecord)
        })
        {
            Assert.DoesNotContain(type.GetProperties(), property =>
                property.Name.Contains("Interface", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Hardware", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Keyboard", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Window", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Process", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Application", StringComparison.OrdinalIgnoreCase)
                || property.Name.Contains("Text", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task SeekAndInclusiveCursorPreserveEqualUtcSequencesWithoutDuplicates()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid deviceId = RegisterMouse(layout);
        TimeCalibrationSegment calibration = StorageServiceTestSupport.Calibration(
            "UTC", new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero));
        using StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock());
        service.Start(calibration);
        service.WriteMouseTransition(Point(deviceId, calibration, 0));
        service.WriteMouseTransition(Point(deviceId, calibration, 0));
        service.WriteMouseTransition(Point(deviceId, calibration, 1));
        service.Checkpoint(1);

        await using StorageMousePlaybackSnapshot snapshot = await service.OpenMousePlaybackSnapshotAsync(
            deviceId,
            new StorageUtcRange(calibration.UtcAnchorMicroseconds, calibration.UtcAnchorMicroseconds + 10));

        StorageMousePlaybackCursor? cursor = await snapshot.SeekAsync(calibration.UtcAnchorMicroseconds);
        Assert.NotNull(cursor);
        StorageMousePlaybackRecord[] records = await CollectAsync(snapshot.StreamFromAsync(cursor));

        Assert.Equal([1L, 2L, 3L], records.Select(record => record.Record.Sequence));
        Assert.Equal(3, snapshot.Info.RecordCount);
        Assert.Equal(snapshot.Info.SnapshotId, records[0].Cursor.SnapshotId);
        Assert.True(records[0].StartsNewSegment);
        Assert.Equal(StorageMousePlaybackBoundaryReason.ExplicitFlush, records[0].BoundaryReason);
    }

    [Fact]
    public async Task SnapshotRejectsForgedAndCrossSnapshotCursorsFailClosed()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid deviceId = RegisterMouse(layout);
        TimeCalibrationSegment calibration = StorageServiceTestSupport.Calibration(
            "UTC", new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero));
        using StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock());
        service.Start(calibration);
        service.WriteMouseTransition(Point(deviceId, calibration, 0));
        service.Checkpoint(0);

        await using StorageMousePlaybackSnapshot first = await service.OpenMousePlaybackSnapshotAsync(
            deviceId, new StorageUtcRange(calibration.UtcAnchorMicroseconds, calibration.UtcAnchorMicroseconds + 10));
        await using StorageMousePlaybackSnapshot second = await service.OpenMousePlaybackSnapshotAsync(
            deviceId, new StorageUtcRange(calibration.UtcAnchorMicroseconds, calibration.UtcAnchorMicroseconds + 10));
        StorageMousePlaybackCursor cursor = Assert.IsType<StorageMousePlaybackCursor>(first.Info.First);

        Assert.Throws<InvalidDataException>(() => second.StreamFromAsync(cursor));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await using IAsyncEnumerator<StorageMousePlaybackRecord> enumerator = first
                .StreamFromAsync(cursor with { Sequence = 2 })
                .GetAsyncEnumerator();
            await enumerator.MoveNextAsync();
        });
    }

    [Fact]
    public async Task OpenFailsBeforeReturningWhenLaterCommittedSegmentIsCorrupt()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid deviceId = RegisterMouse(layout);
        TimeCalibrationSegment calibration = StorageServiceTestSupport.Calibration(
            "UTC", new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero));
        using StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock());
        service.Start(calibration);
        service.WriteMouseTransition(Point(deviceId, calibration, 0));
        service.Checkpoint(0);
        service.WriteMouseTransition(Point(deviceId, calibration, 1));
        service.Checkpoint(1);
        string later = Directory.EnumerateFiles(layout.SpoolDirectory, "*.mspool")
            .OrderBy(path => MouseSpoolSegmentReader.Read(path).Header.FirstSequence)
            .Last();
        byte[] bytes = File.ReadAllBytes(later);
        bytes[^1] ^= 1;
        File.WriteAllBytes(later, bytes);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.OpenMousePlaybackSnapshotAsync(
            deviceId,
            new StorageUtcRange(calibration.UtcAnchorMicroseconds, calibration.UtcAnchorMicroseconds + 10)));
    }

    [Fact]
    public async Task SnapshotExcludesLiveAppendAndDisposeReleasesReadLeaseWithoutStoppingCapture()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid deviceId = RegisterMouse(layout);
        TimeCalibrationSegment calibration = StorageServiceTestSupport.Calibration(
            "UTC", new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero));
        using StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock());
        service.Start(calibration);
        service.WriteMouseTransition(Point(deviceId, calibration, 0));
        service.Checkpoint(0);
        StorageMousePlaybackSnapshot snapshot = await service.OpenMousePlaybackSnapshotAsync(
            deviceId,
            new StorageUtcRange(calibration.UtcAnchorMicroseconds, calibration.UtcAnchorMicroseconds + 10));
        string leased = Assert.Single(Directory.EnumerateFiles(layout.SpoolDirectory, "*.mspool"));
        Assert.True(MousePlaybackReadLease.IsLeased(leased));

        service.WriteMouseTransition(Point(deviceId, calibration, 1));
        service.Checkpoint(1);
        StorageMousePlaybackRecord[] records = await CollectAsync(snapshot.StreamFromAsync(null));
        Assert.Equal([1L], records.Select(record => record.Record.Sequence));

        await snapshot.DisposeAsync();
        Assert.False(MousePlaybackReadLease.IsLeased(leased));
        Assert.True(service.Health.AcceptingWrites);
    }

    [Fact]
    public async Task CrashAdoptedSpoolUsesRecoveryUnknownBoundaryReason()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid deviceId = RegisterMouse(layout);
        TimeCalibrationSegment calibration = StorageServiceTestSupport.Calibration(
            "UTC", new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero));
        long calibrationId;
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock()))
        {
            calibrationId = database.RecordCalibration(calibration);
        }

        Guid spoolId = Guid.NewGuid();
        string spoolPath = Path.Combine(layout.SpoolDirectory, spoolId.ToString("N") + ".mspool");
        using (var writer = new MouseSpoolWriter(spoolPath, new MouseSpoolHeader(
            spoolId, deviceId, calibration.SessionId, calibrationId, FirstSequence: 1)))
        {
            writer.Append(new MouseSpoolRecord(1, 0, 1, -1, 0, false, 0, 0, 0));
            writer.FlushFile();
        }

        using StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock());
        await using StorageMousePlaybackSnapshot snapshot = await service.OpenMousePlaybackSnapshotAsync(
            deviceId,
            new StorageUtcRange(calibration.UtcAnchorMicroseconds, calibration.UtcAnchorMicroseconds + 10));
        StorageMousePlaybackRecord record = Assert.Single(await CollectAsync(snapshot.StreamFromAsync(null)));
        Assert.Equal(StorageMousePlaybackBoundaryReason.RecoveryUnknown, record.BoundaryReason);
    }

    [Fact]
    public void VersionFourSpoolRowsMigrateToLegacyUnknownWithVersionedChecksum()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid deviceId = Guid.NewGuid();
        Guid sessionId = Guid.NewGuid();
        Guid segmentId = Guid.NewGuid();
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={layout.DatabasePath};Pooling=False"))
        {
            connection.Open();
            ExecuteMigration(connection, 1, StorageSchema.Version1Sql, "initial-storage-schema", StorageSchema.Version1Checksum);
            ExecuteMigration(connection, 2, StorageSchema.Version2Sql, "mouse-chunk-catalog", StorageSchema.Version2Checksum);
            ExecuteMigration(connection, 3, StorageSchema.Version3Sql, "storage-service-contracts", StorageSchema.Version3Checksum);
            ExecuteMigration(connection, 4, StorageSchema.Version4Sql, "mouse-ingestion-spool-directory", StorageSchema.Version4Checksum);
            using Microsoft.Data.Sqlite.SqliteCommand insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO devices(device_id, kind, identity_confidence, identity_fingerprint, alias, created_utc_us, identity_ambiguity, last_seen_utc_us)
                VALUES ($device, 0, 0, NULL, NULL, 1, 1, NULL);
                INSERT INTO clock_sessions(session_id, frequency, created_utc_us) VALUES ($session, 1000000, 1);
                INSERT INTO time_calibration_segments(session_id, monotonic_anchor, utc_anchor_us, uncertainty_ticks, time_zone_id)
                VALUES ($session, 0, 1, 0, 'UTC');
                INSERT INTO mouse_spool_segments(
                    spool_segment_id, device_id, calibration_segment_id, relative_path, first_sequence, last_sequence,
                    first_monotonic_timestamp, last_monotonic_timestamp, record_count, committed_length,
                    spool_file_sha256, canonical_sha256, created_utc_us)
                VALUES ($segment, $device, 1, 'placeholder.mspool', 1, 1, 0, 0, 1, 124,
                        '0000000000000000000000000000000000000000000000000000000000000000',
                        '0000000000000000000000000000000000000000000000000000000000000000', 1);
                """;
            insert.Parameters.AddWithValue("$device", deviceId.ToString("N"));
            insert.Parameters.AddWithValue("$session", sessionId.ToString("N"));
            insert.Parameters.AddWithValue("$segment", segmentId.ToString("N"));
            insert.ExecuteNonQuery();
        }

        using StorageDatabase database = StorageDatabase.Open(layout, new FakeClock());
        Assert.Equal(5, database.SchemaVersion);
        using Microsoft.Data.Sqlite.SqliteCommand reason = database.Connection.CreateCommand();
        reason.CommandText = "SELECT seal_reason FROM mouse_spool_segments WHERE spool_segment_id = $segment;";
        reason.Parameters.AddWithValue("$segment", segmentId.ToString("N"));
        Assert.Equal((long)StorageMousePlaybackBoundaryReason.LegacyUnknown, (long)reason.ExecuteScalar()!);
        reason.CommandText = "SELECT checksum FROM schema_migrations WHERE version = 5;";
        Assert.Equal(StorageSchema.Version5Checksum, (string)reason.ExecuteScalar()!);
    }

    [Fact]
    [Trait("Category", "StorageQueryScale")]
    public async Task OneMillionPlaybackSnapshotRecordsStreamWithExactHashAndBoundedMemory()
    {
        const int total = 1_000_000;
        const int recordsPerSegment = 4096;
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid deviceId = RegisterMouse(layout);
        TimeCalibrationSegment calibration = StorageServiceTestSupport.Calibration(
            "UTC", new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero));
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock()))
        {
            long calibrationId = database.RecordCalibration(calibration);
            for (int offset = 0; offset < total; offset += recordsPerSegment)
            {
                int count = Math.Min(recordsPerSegment, total - offset);
                long first = offset + 1L;
                Guid segmentId = Guid.NewGuid();
                string path = Path.Combine(layout.SpoolDirectory, segmentId.ToString("N") + ".mspool");
                using (var writer = new MouseSpoolWriter(path, new MouseSpoolHeader(
                    segmentId, deviceId, calibration.SessionId, calibrationId, first)))
                {
                    for (int index = 0; index < count; index++)
                    {
                        long sequence = first + index;
                        writer.Append(new MouseSpoolRecord(
                            sequence, sequence, checked((int)sequence), checked(-(int)sequence),
                            (byte)(sequence & 1), (sequence & 2) != 0, (ushort)(sequence & 0x03ff),
                            checked((short)(sequence % short.MaxValue)),
                            checked((short)-(sequence % short.MaxValue))));
                    }

                    writer.FlushFile();
                }

                database.CommitMouseSpoolSegment(
                    MouseSpoolSegmentReader.Read(path),
                    updatedUtcMicroseconds: offset + count,
                    sealReason: StorageMousePlaybackBoundaryReason.EventLimit);
            }
        }

        using StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock());
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long baseline = GC.GetTotalMemory(forceFullCollection: false);
        long peakGrowth = 0;
        await using StorageMousePlaybackSnapshot snapshot = await service.OpenMousePlaybackSnapshotAsync(
            deviceId,
            new StorageUtcRange(calibration.UtcAnchorMicroseconds, calibration.UtcAnchorMicroseconds + total + 1L));
        long observed = 0;
        ulong hash = 14695981039346656037UL;
        await foreach (StorageMousePlaybackRecord item in snapshot.StreamFromAsync(null))
        {
            StorageMouseRecord record = item.Record;
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

        Assert.Equal(total, snapshot.Info.RecordCount);
        Assert.Equal(total, observed);
        Assert.Equal(expectedHash, hash);
        Assert.InRange(peakGrowth, 0, 128L * 1024L * 1024L);
    }

    private static async Task<StorageMousePlaybackRecord[]> CollectAsync(IAsyncEnumerable<StorageMousePlaybackRecord> source)
    {
        var records = new List<StorageMousePlaybackRecord>();
        await foreach (StorageMousePlaybackRecord record in source) records.Add(record);
        return records.ToArray();
    }

    private static Guid RegisterMouse(StorageLayout layout)
    {
        Guid deviceId = Guid.NewGuid();
        using StorageDatabase database = StorageDatabase.Open(layout, new FakeClock());
        database.RegisterDevice(deviceId, (int)StorageDeviceKind.Mouse, 0, 1);
        return deviceId;
    }

    private static StorageMouseTransition Point(Guid deviceId, TimeCalibrationSegment calibration, long timestamp) => new(
        deviceId, calibration.SessionId, timestamp, 1, -1, 0, false, 0, 0, 0);

    private static void ExecuteMigration(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        int version,
        string sql,
        string name,
        string checksum)
    {
        using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
        command.CommandText = """
            INSERT INTO schema_migrations(version, name, checksum, applied_utc_us)
            VALUES ($version, $name, $checksum, $applied);
            """;
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$checksum", checksum);
        command.Parameters.AddWithValue("$applied", version);
        command.ExecuteNonQuery();
    }
}
