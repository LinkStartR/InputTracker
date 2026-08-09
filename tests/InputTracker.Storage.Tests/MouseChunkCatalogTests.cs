using Microsoft.Data.Sqlite;

namespace InputTracker.Storage.Tests;

public sealed class MouseChunkCatalogTests
{
    [Theory]
    [InlineData(@"C:\outside\chunk.mchunk")]
    [InlineData(@"..\outside\chunk.mchunk")]
    [InlineData(@"chunks-evil\chunk.mchunk")]
    public void CatalogPathsFailClosedBeforeOutsideAccess(string untrustedPath)
    {
        using var fixture = new ChunkFixture();
        Guid chunkId = Guid.NewGuid();
        fixture.Database.CommitMouseChunk(
            chunkId,
            fixture.DeviceId,
            fixture.CalibrationSegmentId,
            Records(1, 2),
            1);
        UpdateRelativePath(fixture.Database, chunkId, untrustedPath);

        Assert.Throws<InvalidDataException>(() => fixture.Database.ReadMouseChunkCatalog());
        MouseChunkRecoveryReport report = fixture.Database.RecoverMouseChunks(2);

        Assert.Equal(0, report.Diagnostics.Durable);
        Assert.Contains(report.Issues, issue =>
            issue.Category == MouseChunkIssueCategory.UnsafeRelativePath
            && issue.RelativePath == "<invalid-relative-path>"
            && !issue.Message.Contains(fixture.Directory.Path, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(5, 6)]
    [InlineData(12, 3)]
    [InlineData(8, 15)]
    [InlineData(10, 10)]
    public void CommitRejectsPartialContainedContainingAndSameRangeOverlap(long firstSequence, int count)
    {
        using var fixture = new ChunkFixture();
        fixture.Database.CommitMouseChunk(
            Guid.NewGuid(),
            fixture.DeviceId,
            fixture.CalibrationSegmentId,
            Records(10, 10),
            1);
        Guid rejectedId = Guid.NewGuid();

        Assert.Throws<InvalidDataException>(() => fixture.Database.CommitMouseChunk(
            rejectedId,
            fixture.DeviceId,
            fixture.CalibrationSegmentId,
            Records(firstSequence, count),
            2));
        Assert.False(File.Exists(FinalPath(fixture.Layout, rejectedId)));
    }

    [Fact]
    public void AdjacentRangesCommitAndRecoverWithoutOverlapFault()
    {
        using var fixture = new ChunkFixture();
        fixture.Database.CommitMouseChunk(
            Guid.NewGuid(), fixture.DeviceId, fixture.CalibrationSegmentId, Records(1, 5), 1);
        fixture.Database.CommitMouseChunk(
            Guid.NewGuid(), fixture.DeviceId, fixture.CalibrationSegmentId, Records(6, 5), 2);

        MouseChunkRecoveryReport report = fixture.Database.RecoverMouseChunks(3);

        Assert.Equal(10, report.Diagnostics.Durable);
        Assert.DoesNotContain(report.Issues, issue => issue.Category == MouseChunkIssueCategory.SequenceOverlap);
    }

    [Fact]
    public void BadOrphansDoNotRollbackLegalRecoveryAndRepeatedRecoveryIsIdempotent()
    {
        using var fixture = new ChunkFixture();
        Guid validChunk = Guid.NewGuid();
        Guid missingDeviceChunk = Guid.NewGuid();
        Guid missingCalibrationChunk = Guid.NewGuid();
        WriteFinal(fixture.Layout, validChunk, fixture.DeviceId, fixture.CalibrationSegmentId, Records(1, 2));
        WriteFinal(fixture.Layout, missingDeviceChunk, Guid.NewGuid(), fixture.CalibrationSegmentId, Records(1, 2));
        WriteFinal(fixture.Layout, missingCalibrationChunk, fixture.DeviceId, fixture.CalibrationSegmentId + 1000, Records(10, 2));

        MouseChunkRecoveryReport first = fixture.Database.RecoverMouseChunks(10);

        Assert.Equal(2, first.Diagnostics.Recovered);
        Assert.Single(fixture.Database.ReadMouseChunkCatalog());
        Assert.Contains(first.Issues, issue => issue.Category == MouseChunkIssueCategory.MissingDevice);
        Assert.Contains(first.Issues, issue => issue.Category == MouseChunkIssueCategory.MissingCalibrationSegment);
        Assert.True(File.Exists(FinalPath(fixture.Layout, validChunk)));
        Assert.True(File.Exists(FinalPath(fixture.Layout, missingDeviceChunk)));
        Assert.True(File.Exists(FinalPath(fixture.Layout, missingCalibrationChunk)));

        MouseChunkRecoveryReport second = fixture.Database.RecoverMouseChunks(11);

        Assert.Equal(0, second.Diagnostics.Recovered);
        Assert.Equal(2, second.Diagnostics.Durable);
        Assert.Single(fixture.Database.ReadMouseChunkCatalog());
    }

    [Fact]
    public void ValidAndCorruptTemporaryFilesAreReportedAndRetainedWithoutIndexing()
    {
        using var fixture = new ChunkFixture();
        Guid validId = Guid.NewGuid();
        Guid corruptId = Guid.NewGuid();
        string validPath = System.IO.Path.Combine(fixture.Layout.ChunkDirectory, validId.ToString("N") + ".tmp");
        string corruptPath = System.IO.Path.Combine(fixture.Layout.ChunkDirectory, corruptId.ToString("N") + ".tmp");
        MouseChunkFile.WriteNew(
            validPath, validId, fixture.DeviceId, fixture.CalibrationSegmentId, Records(1, 2));
        File.WriteAllBytes(corruptPath, [0x01, 0x02, 0x03]);

        MouseChunkRecoveryReport report = fixture.Database.RecoverMouseChunks(2);

        Assert.Empty(fixture.Database.ReadMouseChunkCatalog());
        Assert.True(File.Exists(validPath));
        Assert.True(File.Exists(corruptPath));
        Assert.Contains(report.Issues, issue => issue.Category == MouseChunkIssueCategory.TemporaryFileValid);
        Assert.Contains(report.Issues, issue => issue.Category == MouseChunkIssueCategory.TemporaryFileCorrupt);
    }

    [Fact]
    public void OverflowingFinalChunkDoesNotAbortIndependentLegalOrphanRecovery()
    {
        using var fixture = new ChunkFixture();
        Guid corruptId = Guid.NewGuid();
        MouseChunkEncoded corrupt = MouseChunkCodec.Encode(
            corruptId,
            fixture.DeviceId,
            fixture.CalibrationSegmentId,
            [
                new MouseSpoolRecord(1, long.MaxValue, 0, 0, 0, false, 0, 0, 0),
                new MouseSpoolRecord(2, long.MaxValue, 0, 0, 0, false, 0, 0, 0)
            ]);
        byte[] canonical = MouseChunkCodecTests.Decompress(corrupt.FileBytes);
        canonical[9] = 1;
        File.WriteAllBytes(
            FinalPath(fixture.Layout, corruptId),
            MouseChunkCodecTests.RebuildWithCanonical(corrupt.FileBytes, canonical));
        WriteFinal(
            fixture.Layout, Guid.NewGuid(), fixture.DeviceId, fixture.CalibrationSegmentId, Records(10, 2));

        MouseChunkRecoveryReport report = fixture.Database.RecoverMouseChunks(3);

        Assert.Equal(2, report.Diagnostics.Recovered);
        Assert.Single(fixture.Database.ReadMouseChunkCatalog());
        Assert.Contains(report.Issues, issue => issue.Category == MouseChunkIssueCategory.CorruptFinalFile);
    }

    [Fact]
    public void DatabaseInsertFailurePublishesNoWatermarkAndLeavesRecoverableFinalFile()
    {
        using var fixture = new ChunkFixture();
        Guid chunkId = Guid.NewGuid();
        using (SqliteCommand trigger = fixture.Database.Connection.CreateCommand())
        {
            trigger.CommandText = """
                CREATE TRIGGER fail_mouse_chunk_insert
                BEFORE INSERT ON mouse_chunks
                BEGIN
                    SELECT RAISE(ABORT, 'test-only insert failure');
                END;
                """;
            trigger.ExecuteNonQuery();
        }
        MouseChunkDurableWatermark? published = null;

        Assert.Throws<SqliteException>(() => published = fixture.Database.CommitMouseChunk(
            chunkId,
            fixture.DeviceId,
            fixture.CalibrationSegmentId,
            Records(1, 2),
            1));

        Assert.Null(published);
        Assert.True(File.Exists(FinalPath(fixture.Layout, chunkId)));
        Assert.Empty(fixture.Database.ReadMouseChunkCatalog());
        using (SqliteCommand drop = fixture.Database.Connection.CreateCommand())
        {
            drop.CommandText = "DROP TRIGGER fail_mouse_chunk_insert;";
            drop.ExecuteNonQuery();
        }

        MouseChunkRecoveryReport recovered = fixture.Database.RecoverMouseChunks(2);
        Assert.Equal(2, recovered.Diagnostics.Recovered);
        Assert.Single(fixture.Database.ReadMouseChunkCatalog());
    }

    [Fact]
    public void IndexedMissingAndCorruptFilesNeverCountAsDurable()
    {
        using var fixture = new ChunkFixture();
        Guid corruptId = Guid.NewGuid();
        Guid missingId = Guid.NewGuid();
        MouseChunkEncoded corrupt = MouseChunkCodec.Encode(
            corruptId, fixture.DeviceId, fixture.CalibrationSegmentId, Records(1, 2));
        byte[] corruptBytes = corrupt.FileBytes.ToArray();
        corruptBytes[MouseChunkFormat.HeaderSize] ^= 0x01;
        File.WriteAllBytes(FinalPath(fixture.Layout, corruptId), corruptBytes);
        InsertCatalog(fixture.Database, corrupt, recovered: false);
        MouseChunkEncoded missing = MouseChunkCodec.Encode(
            missingId, fixture.DeviceId, fixture.CalibrationSegmentId, Records(10, 2));
        InsertCatalog(fixture.Database, missing, recovered: false);

        MouseChunkRecoveryReport report = fixture.Database.RecoverMouseChunks(3);

        Assert.Equal(0, report.Diagnostics.Durable);
        Assert.Equal(1, report.Diagnostics.Corrupt);
        Assert.Equal(1, report.Diagnostics.MissingFile);
        Assert.Contains(report.Issues, issue => issue.Category == MouseChunkIssueCategory.CorruptFinalFile);
        Assert.Contains(report.Issues, issue => issue.Category == MouseChunkIssueCategory.MissingFile);
    }

    [Fact]
    public void OverlappingCatalogRowsAreIntegrityFaultsAndNeverCountAsDurable()
    {
        using var fixture = new ChunkFixture();
        MouseChunkEncoded first = WriteFinal(
            fixture.Layout, Guid.NewGuid(), fixture.DeviceId, fixture.CalibrationSegmentId, Records(1, 5));
        MouseChunkEncoded second = WriteFinal(
            fixture.Layout, Guid.NewGuid(), fixture.DeviceId, fixture.CalibrationSegmentId, Records(4, 5));
        InsertCatalog(fixture.Database, first, recovered: false);
        InsertCatalog(fixture.Database, second, recovered: false);

        MouseChunkRecoveryReport report = fixture.Database.RecoverMouseChunks(3);

        Assert.Equal(0, report.Diagnostics.Durable);
        Assert.Equal(2, report.Issues.Count(issue => issue.Category == MouseChunkIssueCategory.SequenceOverlap));
    }

    [Fact]
    public void CandidateOverlapRetainsEveryConflictingOrphanWithoutIndexingEither()
    {
        using var fixture = new ChunkFixture();
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        WriteFinal(fixture.Layout, first, fixture.DeviceId, fixture.CalibrationSegmentId, Records(1, 5));
        WriteFinal(fixture.Layout, second, fixture.DeviceId, fixture.CalibrationSegmentId, Records(4, 5));

        MouseChunkRecoveryReport report = fixture.Database.RecoverMouseChunks(3);

        Assert.Equal(0, report.Diagnostics.Recovered);
        Assert.Empty(fixture.Database.ReadMouseChunkCatalog());
        Assert.True(File.Exists(FinalPath(fixture.Layout, first)));
        Assert.True(File.Exists(FinalPath(fixture.Layout, second)));
        Assert.Equal(2, report.Issues.Count(issue => issue.Category == MouseChunkIssueCategory.SequenceOverlap));
    }

    [Fact]
    public void DamagedCatalogRowReturnsGlobalFailClosedIntegrityReport()
    {
        using var fixture = new ChunkFixture();
        MouseChunkEncoded encoded = WriteFinal(
            fixture.Layout, Guid.NewGuid(), fixture.DeviceId, fixture.CalibrationSegmentId, Records(1, 2));
        InsertCatalog(fixture.Database, encoded, recovered: false);
        using (SqliteCommand command = fixture.Database.Connection.CreateCommand())
        {
            command.CommandText = "UPDATE mouse_chunks SET compressed_crc32c = 4294967296;";
            command.ExecuteNonQuery();
        }

        MouseChunkRecoveryReport report = fixture.Database.RecoverMouseChunks(3);

        Assert.Equal(0, report.Diagnostics.Durable);
        Assert.True(report.HasIntegrityFault);
        Assert.Contains(report.Issues, issue => issue.Category == MouseChunkIssueCategory.CatalogMismatch);
        Assert.Throws<InvalidDataException>(() => fixture.Database.ReadMouseChunkCatalog());
    }

    private static MouseSpoolRecord[] Records(long firstSequence, int count) =>
        Enumerable.Range(0, count)
            .Select(index => new MouseSpoolRecord(
                firstSequence + index,
                firstSequence + index,
                index,
                -index,
                (byte)(index % 2),
                index % 2 == 0,
                (ushort)(index % 2 == 0 ? 0x03FF : 0),
                (short)(index % 2 == 0 ? 120 : -120),
                (short)(index % 2 == 0 ? -120 : 120)))
            .ToArray();

    private static MouseChunkEncoded WriteFinal(
        StorageLayout layout,
        Guid chunkId,
        Guid deviceId,
        long calibrationSegmentId,
        IReadOnlyList<MouseSpoolRecord> records)
    {
        MouseChunkEncoded encoded = MouseChunkCodec.Encode(chunkId, deviceId, calibrationSegmentId, records);
        using var stream = new FileStream(
            FinalPath(layout, chunkId), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        stream.Write(encoded.FileBytes);
        stream.Flush(flushToDisk: true);
        return encoded;
    }

    private static void InsertCatalog(StorageDatabase database, MouseChunkEncoded encoded, bool recovered)
    {
        using SqliteCommand command = database.Connection.CreateCommand();
        command.CommandText = """
            INSERT INTO mouse_chunks(
                chunk_id, device_id, calibration_segment_id, relative_path,
                first_sequence, last_sequence,
                first_monotonic_timestamp, last_monotonic_timestamp,
                event_count, canonical_length, compressed_length, file_length,
                compressed_crc32c, canonical_sha256, created_utc_us, recovered)
            VALUES (
                $chunk, $device, $segment, $path,
                $firstSequence, $lastSequence,
                $firstTimestamp, $lastTimestamp,
                $events, $canonicalLength, $compressedLength, $fileLength,
                $crc, $sha, 1, $recovered);
            """;
        command.Parameters.AddWithValue("$chunk", encoded.Header.ChunkId.ToString("N"));
        command.Parameters.AddWithValue("$device", encoded.Header.DeviceId.ToString("N"));
        command.Parameters.AddWithValue("$segment", encoded.Header.CalibrationSegmentId);
        command.Parameters.AddWithValue("$path", encoded.Header.ChunkId.ToString("N") + ".mchunk");
        command.Parameters.AddWithValue("$firstSequence", encoded.Header.FirstSequence);
        command.Parameters.AddWithValue("$lastSequence", encoded.Header.LastSequence);
        command.Parameters.AddWithValue("$firstTimestamp", encoded.Header.FirstMonotonicTimestamp);
        command.Parameters.AddWithValue("$lastTimestamp", encoded.Header.LastMonotonicTimestamp);
        command.Parameters.AddWithValue("$events", encoded.Header.EventCount);
        command.Parameters.AddWithValue("$canonicalLength", encoded.Header.CanonicalLength);
        command.Parameters.AddWithValue("$compressedLength", encoded.Header.CompressedLength);
        command.Parameters.AddWithValue("$fileLength", encoded.FileBytes.LongLength);
        command.Parameters.AddWithValue("$crc", (long)encoded.CompressedCrc32C);
        command.Parameters.AddWithValue("$sha", encoded.CanonicalSha256);
        command.Parameters.AddWithValue("$recovered", recovered ? 1 : 0);
        command.ExecuteNonQuery();
    }

    private static void UpdateRelativePath(StorageDatabase database, Guid chunkId, string path)
    {
        using SqliteCommand command = database.Connection.CreateCommand();
        command.CommandText = "UPDATE mouse_chunks SET relative_path = $path WHERE chunk_id = $chunk;";
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$chunk", chunkId.ToString("N"));
        command.ExecuteNonQuery();
    }

    private static string FinalPath(StorageLayout layout, Guid chunkId) =>
        System.IO.Path.Combine(layout.ChunkDirectory, chunkId.ToString("N") + ".mchunk");

    private sealed class ChunkFixture : IDisposable
    {
        public ChunkFixture()
        {
            Directory = new TemporaryDirectory();
            Layout = StorageLayout.Create(Directory.Path);
            var clock = new FakeClock();
            Database = StorageDatabase.Open(Layout, clock);
            DeviceId = Guid.NewGuid();
            Database.RegisterDevice(DeviceId, kind: 0, identityConfidence: 0, createdUtcMicroseconds: 1);
            CalibrationSegmentId = Database.RecordCalibration(TimeCalibrationSegment.Capture(Guid.NewGuid(), clock));
        }

        public TemporaryDirectory Directory { get; }
        public StorageLayout Layout { get; }
        public StorageDatabase Database { get; }
        public Guid DeviceId { get; }
        public long CalibrationSegmentId { get; }

        public void Dispose()
        {
            Database.Dispose();
            Directory.Dispose();
        }
    }
}
