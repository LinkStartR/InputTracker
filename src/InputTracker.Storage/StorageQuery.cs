using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace InputTracker.Storage;

internal static class StorageQuery
{
    public static StorageKeyboardPeriodResult QueryKeyboardPeriod(
        SqliteConnection connection,
        StoragePeriodKind kind,
        DateOnly containingDate,
        string timeZoneId,
        Guid? deviceId)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (deviceId == Guid.Empty)
        {
            throw new ArgumentException("A device filter must be non-empty when supplied.", nameof(deviceId));
        }

        StorageResolvedPeriod period = StoragePeriodResolver.Resolve(kind, containingDate, timeZoneId);
        if (deviceId is Guid requested)
        {
            EnsureDeviceKind(connection, requested, StorageDeviceKind.Keyboard);
        }

        IReadOnlyList<string> zones = ReadCaptureTimeZones(connection, deviceId);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT k.device_id, k.make_code, k.is_e0, k.is_e1,
                   SUM(k.press_count), SUM(k.hold_duration_us)
            FROM keyboard_daily k
            JOIN devices d ON d.device_id = k.device_id
            WHERE d.kind = $keyboard
              AND k.time_zone_id = $zone
              AND k.capture_local_date >= $start
              AND k.capture_local_date < $end
              AND ($device IS NULL OR k.device_id = $device)
            GROUP BY k.device_id, k.make_code, k.is_e0, k.is_e1
            ORDER BY k.device_id, k.make_code, k.is_e0, k.is_e1;
            """;
        command.Parameters.AddWithValue("$keyboard", (int)StorageDeviceKind.Keyboard);
        command.Parameters.AddWithValue("$zone", period.TimeZoneId);
        command.Parameters.AddWithValue("$start", FormatDate(period.LocalStartDate));
        command.Parameters.AddWithValue("$end", FormatDate(period.LocalEndDate));
        command.Parameters.AddWithValue("$device", deviceId is Guid value
            ? value.ToString("N")
            : DBNull.Value);

        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<StorageKeyboardPeriodAggregate>();
        while (reader.Read())
        {
            if (!Guid.TryParseExact(reader.GetString(0), "N", out Guid rowDevice)
                || rowDevice == Guid.Empty
                || reader.GetInt64(1) is < 0 or > ushort.MaxValue)
            {
                throw new InvalidDataException("A keyboard aggregate identity is invalid.");
            }

            long isE0 = reader.GetInt64(2);
            long isE1 = reader.GetInt64(3);
            long presses = reader.GetInt64(4);
            long duration = reader.GetInt64(5);
            if ((isE0 != 0 && isE0 != 1)
                || (isE1 != 0 && isE1 != 1)
                || (isE0 == 1 && isE1 == 1)
                || presses < 0
                || duration < 0)
            {
                throw new InvalidDataException("A keyboard aggregate value is invalid.");
            }

            rows.Add(new StorageKeyboardPeriodAggregate(
                rowDevice,
                checked((ushort)reader.GetInt64(1)),
                isE0 == 1,
                isE1 == 1,
                presses,
                duration));
        }

        return new StorageKeyboardPeriodResult(period, rows, zones);
    }

    public static StorageMouseButtonPeriodResult QueryMouseButtonsPeriod(
        SqliteConnection connection,
        StoragePeriodKind kind,
        DateOnly containingDate,
        string timeZoneId,
        Guid? deviceId)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (deviceId == Guid.Empty)
        {
            throw new ArgumentException("A device filter must be non-empty when supplied.", nameof(deviceId));
        }

        StorageResolvedPeriod period = StoragePeriodResolver.Resolve(kind, containingDate, timeZoneId);
        if (deviceId is Guid requested)
        {
            EnsureDeviceKind(connection, requested, StorageDeviceKind.Mouse);
        }

        IReadOnlyList<string> zones = ReadMouseButtonCaptureTimeZones(connection, deviceId);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.device_id, m.button, SUM(m.press_count)
            FROM mouse_button_daily m
            JOIN devices d ON d.device_id = m.device_id
            WHERE d.kind = $mouse
              AND m.time_zone_id = $zone
              AND m.capture_local_date >= $start
              AND m.capture_local_date < $end
              AND ($device IS NULL OR m.device_id = $device)
            GROUP BY m.device_id, m.button
            ORDER BY m.device_id, m.button;
            """;
        command.Parameters.AddWithValue("$mouse", (int)StorageDeviceKind.Mouse);
        command.Parameters.AddWithValue("$zone", period.TimeZoneId);
        command.Parameters.AddWithValue("$start", FormatDate(period.LocalStartDate));
        command.Parameters.AddWithValue("$end", FormatDate(period.LocalEndDate));
        command.Parameters.AddWithValue("$device", deviceId is Guid value
            ? value.ToString("N")
            : DBNull.Value);

        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<StorageMouseButtonPeriodAggregate>();
        while (reader.Read())
        {
            long button = reader.GetInt64(1);
            long presses = reader.GetInt64(2);
            if (!Guid.TryParseExact(reader.GetString(0), "N", out Guid rowDevice)
                || rowDevice == Guid.Empty
                || button is < (long)StorageMouseButton.Left or > (long)StorageMouseButton.X2
                || presses <= 0)
            {
                throw new InvalidDataException("A mouse button aggregate is invalid.");
            }

            rows.Add(new StorageMouseButtonPeriodAggregate(
                rowDevice,
                (StorageMouseButton)button,
                presses));
        }

        return new StorageMouseButtonPeriodResult(period, rows, zones);
    }

    public static IAsyncEnumerable<StorageMouseRecord> CreateMouseSnapshot(
        StorageDatabase database,
        Guid deviceId,
        StorageUtcRange utcRange,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (deviceId == Guid.Empty)
        {
            throw new ArgumentException("A mouse device identifier must be non-empty.", nameof(deviceId));
        }

        utcRange.Validate();
        EnsureDeviceKind(database.Connection, deviceId, StorageDeviceKind.Mouse);
        MouseQuerySnapshot snapshot = CaptureMouseSnapshot(database, deviceId, utcRange);
        return StreamMouseSnapshot(snapshot, cancellationToken);
    }

    internal static MouseQuerySnapshot CaptureMouseSnapshot(
        StorageDatabase database,
        Guid deviceId,
        StorageUtcRange utcRange)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (deviceId == Guid.Empty)
        {
            throw new ArgumentException("A mouse device identifier must be non-empty.", nameof(deviceId));
        }

        utcRange.Validate();
        EnsureDeviceKind(database.Connection, deviceId, StorageDeviceKind.Mouse);
        long watermark = database.ReadMouseDeviceWatermarks()
            .TryGetValue(deviceId, out long sequence) ? sequence : 0;
        var segments = new List<MouseQuerySegment>();
        using SqliteCommand command = database.Connection.CreateCommand();
        command.CommandText = """
            SELECT s.spool_segment_id, s.calibration_segment_id, s.relative_path,
                   s.first_sequence, s.last_sequence, s.record_count, s.committed_length,
                   s.spool_file_sha256, s.canonical_sha256, s.seal_reason,
                   c.session_id, cs.frequency, c.monotonic_anchor, c.utc_anchor_us,
                   c.uncertainty_ticks, c.time_zone_id
            FROM mouse_spool_segments s
            JOIN time_calibration_segments c ON c.segment_id = s.calibration_segment_id
            JOIN clock_sessions cs ON cs.session_id = c.session_id
            WHERE s.device_id = $device
              AND s.last_sequence <= $watermark
            ORDER BY s.first_sequence;
            """;
        command.Parameters.AddWithValue("$device", deviceId.ToString("N"));
        command.Parameters.AddWithValue("$watermark", watermark);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!Guid.TryParseExact(reader.GetString(0), "N", out Guid segmentId)
                || segmentId == Guid.Empty
                || !Guid.TryParseExact(reader.GetString(10), "N", out Guid sessionId)
                || sessionId == Guid.Empty)
            {
                throw new InvalidDataException("A mouse query directory identity is invalid.");
            }

            var calibration = new TimeCalibrationSegment(
                sessionId,
                reader.GetInt64(11),
                reader.GetInt64(12),
                reader.GetInt64(13),
                reader.GetInt64(14),
                reader.GetString(15));
            StorageKeyboardTime.ValidateCalibration(calibration);
            segments.Add(new MouseQuerySegment(
                segmentId,
                deviceId,
                reader.GetInt64(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetString(7),
                reader.GetString(8),
                ReadSealReason(reader.GetInt64(9)),
                calibration));
        }

        ValidateDirectoryPrefix(segments, watermark);
        IReadOnlyList<MouseChunkCatalogEntry> chunks = database.ReadMouseChunkCatalog()
            .Where(entry => entry.DeviceId == deviceId && entry.LastSequence <= watermark)
            .OrderBy(entry => entry.FirstSequence)
            .ToArray();
        return new MouseQuerySnapshot(
            database.Layout,
            deviceId,
            utcRange,
            watermark,
            segments,
            chunks);
    }

    private static async IAsyncEnumerable<StorageMouseRecord> StreamMouseSnapshot(
        MouseQuerySnapshot snapshot,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Run(
            () => ValidateAllSegments(snapshot, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        foreach (MouseQuerySegment row in snapshot.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MouseSpoolSegment segment = ReadAndValidateSegment(snapshot, row);
            foreach (MouseSpoolRecord record in segment.Records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long utc = row.Calibration.ToUtcMicroseconds(record.MonotonicTimestamp);
                if (utc >= snapshot.UtcRange.StartUtcMicroseconds
                    && utc < snapshot.UtcRange.EndUtcMicroseconds)
                {
                    yield return new StorageMouseRecord(
                        snapshot.DeviceId,
                        record.Sequence,
                        utc,
                        record.DeltaX,
                        record.DeltaY,
                        record.MotionKind,
                        record.UsesVirtualDesktop,
                        record.ButtonChanges,
                        record.VerticalWheelDelta,
                        record.HorizontalWheelDelta);
                }
            }

            await Task.Yield();
        }
    }

    internal static void ValidateAllSegments(
        MouseQuerySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ValidateChunkRanges(snapshot);
        long previousUtc = long.MinValue;
        foreach (MouseQuerySegment row in snapshot.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MouseSpoolSegment segment = ReadAndValidateSegment(snapshot, row);

            foreach (MouseSpoolRecord record in segment.Records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long utc = row.Calibration.ToUtcMicroseconds(record.MonotonicTimestamp);
                if (utc < previousUtc)
                {
                    throw new InvalidDataException("Mouse query UTC mapping moved backwards.");
                }

                previousUtc = utc;
            }
        }
    }

    internal static MouseSpoolSegment ReadAndValidateSegment(
        MouseQuerySnapshot snapshot,
        MouseQuerySegment row)
    {
        string path = ResolveContainedFile(
            snapshot.Layout.SpoolDirectory,
            row.RelativePath,
            row.SegmentId.ToString("N") + ".mspool");
        MouseSpoolSegment segment = MouseSpoolSegmentReader.Read(path);
        string canonicalHash = MouseChunkCodec.Encode(
            Guid.NewGuid(), snapshot.DeviceId, row.CalibrationSegmentId, segment.Records).CanonicalSha256;
        if (segment.Header.SegmentId != row.SegmentId
            || segment.Header.DeviceId != snapshot.DeviceId
            || segment.Header.CalibrationSessionId != row.Calibration.SessionId
            || segment.Header.CalibrationSegmentId != row.CalibrationSegmentId
            || segment.Header.FirstSequence != row.FirstSequence
            || segment.Records.Count != row.RecordCount
            || segment.Records[^1].Sequence != row.LastSequence
            || segment.ValidLength != row.CommittedLength
            || !string.Equals(segment.SpoolFileSha256, row.SpoolFileSha256, StringComparison.Ordinal)
            || !string.Equals(canonicalHash, row.CanonicalSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A committed mouse spool does not match its directory row.");
        }

        ValidateEquivalentChunk(snapshot, row, segment.Records, canonicalHash);
        return segment;
    }

    internal static IReadOnlyList<string> GetReadLeasePaths(MouseQuerySnapshot snapshot)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (MouseQuerySegment segment in snapshot.Segments)
        {
            paths.Add(ResolveContainedFile(
                snapshot.Layout.SpoolDirectory,
                segment.RelativePath,
                segment.SegmentId.ToString("N") + ".mspool"));
        }

        foreach (MouseChunkCatalogEntry chunk in snapshot.Chunks)
        {
            paths.Add(ResolveContainedFile(
                snapshot.Layout.ChunkDirectory,
                chunk.RelativePath,
                chunk.ChunkId.ToString("N") + ".mchunk"));
        }

        return paths.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void ValidateChunkRanges(MouseQuerySnapshot snapshot)
    {
        foreach (MouseChunkCatalogEntry chunk in snapshot.Chunks)
        {
            int exactMatches = snapshot.Segments.Count(segment =>
                segment.FirstSequence == chunk.FirstSequence
                && segment.LastSequence == chunk.LastSequence);
            if (exactMatches != 1)
            {
                throw new InvalidDataException(
                    "A committed mouse chunk does not map to exactly one authoritative spool range.");
            }
        }
    }

    private static void ValidateEquivalentChunk(
        MouseQuerySnapshot snapshot,
        MouseQuerySegment row,
        IReadOnlyList<MouseSpoolRecord> records,
        string canonicalHash)
    {
        MouseChunkCatalogEntry[] equivalent = snapshot.Chunks.Where(entry =>
            entry.FirstSequence == row.FirstSequence && entry.LastSequence == row.LastSequence).ToArray();
        if (equivalent.Length == 0)
        {
            return;
        }

        if (equivalent.Length != 1
            || equivalent[0].CalibrationSegmentId != row.CalibrationSegmentId
            || !string.Equals(equivalent[0].CanonicalSha256, canonicalHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A mouse chunk representation conflicts with its authoritative spool range.");
        }

        MouseChunkCatalogEntry entry = equivalent[0];
        string path = ResolveContainedFile(
            snapshot.Layout.ChunkDirectory,
            entry.RelativePath,
            entry.ChunkId.ToString("N") + ".mchunk");
        MouseChunk chunk = MouseChunkReader.Read(path);
        if (chunk.Header.ChunkId != entry.ChunkId
            || chunk.Header.DeviceId != snapshot.DeviceId
            || chunk.Header.CalibrationSegmentId != row.CalibrationSegmentId
            || chunk.Header.FirstSequence != entry.FirstSequence
            || chunk.Header.LastSequence != entry.LastSequence
            || chunk.Header.FirstMonotonicTimestamp != entry.FirstMonotonicTimestamp
            || chunk.Header.LastMonotonicTimestamp != entry.LastMonotonicTimestamp
            || chunk.Header.EventCount != entry.EventCount
            || chunk.Header.CanonicalLength != entry.CanonicalLength
            || chunk.Header.CompressedLength != entry.CompressedLength
            || new FileInfo(path).Length != entry.FileLength
            || chunk.CompressedCrc32C != entry.CompressedCrc32C
            || !string.Equals(chunk.CanonicalSha256, canonicalHash, StringComparison.Ordinal)
            || !chunk.Records.SequenceEqual(records))
        {
            throw new InvalidDataException("A mouse chunk is not point-for-point equivalent to its authoritative spool range.");
        }
    }

    private static void ValidateDirectoryPrefix(IReadOnlyList<MouseQuerySegment> segments, long watermark)
    {
        long expected = 1;
        foreach (MouseQuerySegment segment in segments)
        {
            if (segment.FirstSequence != expected
                || segment.LastSequence < segment.FirstSequence
                || segment.RecordCount != checked(segment.LastSequence - segment.FirstSequence + 1)
                || segment.CommittedLength != MouseSpoolHeaderCodec.HeaderSize
                    + checked(segment.RecordCount * MouseSpoolRecordCodec.RecordSize)
                || segment.CalibrationSegmentId <= 0
                || segment.SpoolFileSha256.Length != 64
                || segment.CanonicalSha256.Length != 64)
            {
                throw new InvalidDataException("The mouse spool directory is not one exact committed prefix.");
            }

            expected = checked(segment.LastSequence + 1);
        }

        if (expected - 1 != watermark)
        {
            throw new InvalidDataException("The mouse spool directory does not exactly cover its device watermark.");
        }
    }

    private static string ResolveContainedFile(string rootPath, string relativePath, string expectedName)
    {
        string root = Path.GetFullPath(rootPath);
        if (!Directory.Exists(root)
            || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0
            || string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathFullyQualified(relativePath)
            || !string.Equals(relativePath, expectedName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A mouse query file path is invalid.");
        }

        string rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(fullPath)
            || (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("A mouse query file is missing, outside the store, or a reparse point.");
        }

        return fullPath;
    }

    private static IReadOnlyList<string> ReadCaptureTimeZones(
        SqliteConnection connection,
        Guid? deviceId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT k.time_zone_id
            FROM keyboard_daily k
            JOIN devices d ON d.device_id = k.device_id
            WHERE d.kind = $keyboard
              AND ($device IS NULL OR k.device_id = $device)
            ORDER BY k.time_zone_id;
            """;
        command.Parameters.AddWithValue("$keyboard", (int)StorageDeviceKind.Keyboard);
        command.Parameters.AddWithValue("$device", deviceId is Guid value
            ? value.ToString("N")
            : DBNull.Value);
        using SqliteDataReader reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            string zone = reader.GetString(0);
            _ = StoragePeriodResolver.Resolve(StoragePeriodKind.Day, new DateOnly(2000, 1, 1), zone);
            result.Add(zone);
        }

        return result;
    }

    private static IReadOnlyList<string> ReadMouseButtonCaptureTimeZones(
        SqliteConnection connection,
        Guid? deviceId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT m.time_zone_id
            FROM mouse_button_daily m
            JOIN devices d ON d.device_id = m.device_id
            WHERE d.kind = $mouse
              AND ($device IS NULL OR m.device_id = $device)
            ORDER BY m.time_zone_id;
            """;
        command.Parameters.AddWithValue("$mouse", (int)StorageDeviceKind.Mouse);
        command.Parameters.AddWithValue("$device", deviceId is Guid value
            ? value.ToString("N")
            : DBNull.Value);
        using SqliteDataReader reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            string zone = reader.GetString(0);
            _ = StoragePeriodResolver.Resolve(StoragePeriodKind.Day, new DateOnly(2000, 1, 1), zone);
            result.Add(zone);
        }

        return result;
    }

    private static void EnsureDeviceKind(
        SqliteConnection connection,
        Guid deviceId,
        StorageDeviceKind expected)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT kind FROM devices WHERE device_id = $device;";
        command.Parameters.AddWithValue("$device", deviceId.ToString("N"));
        object? value = command.ExecuteScalar();
        if (value is not long kind || kind != (long)expected)
        {
            throw new InvalidDataException("The requested device is missing or has the wrong kind.");
        }
    }

    private static string FormatDate(DateOnly value) =>
        value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    internal sealed record MouseQuerySnapshot(
        StorageLayout Layout,
        Guid DeviceId,
        StorageUtcRange UtcRange,
        long Watermark,
        IReadOnlyList<MouseQuerySegment> Segments,
        IReadOnlyList<MouseChunkCatalogEntry> Chunks);

    internal sealed record MouseQuerySegment(
        Guid SegmentId,
        Guid DeviceId,
        long CalibrationSegmentId,
        string RelativePath,
        long FirstSequence,
        long LastSequence,
        long RecordCount,
        long CommittedLength,
        string SpoolFileSha256,
        string CanonicalSha256,
        StorageMousePlaybackBoundaryReason BoundaryReason,
        TimeCalibrationSegment Calibration);

    private static StorageMousePlaybackBoundaryReason ReadSealReason(long value) =>
        value is >= (long)StorageMousePlaybackBoundaryReason.RangeStart
            and <= (long)StorageMousePlaybackBoundaryReason.RecoveryUnknown
            ? (StorageMousePlaybackBoundaryReason)value
            : throw new InvalidDataException("A mouse spool segment boundary reason is invalid.");

}
