using Microsoft.Data.Sqlite;

namespace InputTracker.Storage;

public sealed record MouseChunkCatalogEntry(
    Guid ChunkId,
    Guid DeviceId,
    long CalibrationSegmentId,
    string RelativePath,
    long FirstSequence,
    long LastSequence,
    long FirstMonotonicTimestamp,
    long LastMonotonicTimestamp,
    long EventCount,
    long CanonicalLength,
    long CompressedLength,
    long FileLength,
    uint CompressedCrc32C,
    string CanonicalSha256,
    long CreatedUtcMicroseconds,
    bool Recovered);

public sealed record MouseChunkDurableWatermark(
    Guid ChunkId,
    long Encoded,
    long Persisted,
    long PowerDurable,
    string RelativePath);

public enum MouseChunkIssueSeverity
{
    Observation = 0,
    IntegrityFault = 1
}

public enum MouseChunkIssueCategory
{
    TemporaryFileValid = 0,
    TemporaryFileCorrupt = 1,
    OrphanFinalFile = 2,
    CorruptFinalFile = 3,
    MissingIndex = 4,
    MissingFile = 5,
    CatalogMismatch = 6,
    UnsafeRelativePath = 7,
    MissingDevice = 8,
    MissingCalibrationSegment = 9,
    SequenceOverlap = 10,
    CatalogInsertFailed = 11
}

public sealed record MouseChunkIntegrityIssue(
    MouseChunkIssueSeverity Severity,
    MouseChunkIssueCategory Category,
    string RelativePath,
    string ErrorType,
    string Message);

public sealed record MouseChunkDiagnostics(
    long Encoded,
    long Persisted,
    long Durable,
    long Recovered,
    long Orphan,
    long Corrupt,
    long MissingIndex,
    long MissingFile);

public sealed record MouseChunkRecoveryReport(
    MouseChunkDiagnostics Diagnostics,
    IReadOnlyList<MouseChunkIntegrityIssue> Issues)
{
    public bool HasIntegrityFault => Issues.Any(issue => issue.Severity == MouseChunkIssueSeverity.IntegrityFault);
}

internal static class MouseChunkCatalog
{
    private const string FinalExtension = ".mchunk";
    private const string TemporaryExtension = ".tmp";
    private const string InvalidDiagnosticPath = "<invalid-relative-path>";
    private const string UnknownDiagnosticFile = "<unrecognized-chunk-file>";

    public static MouseChunkDurableWatermark Commit(
        StorageDatabase database,
        Guid chunkId,
        Guid deviceId,
        long calibrationSegmentId,
        IReadOnlyList<MouseSpoolRecord> records,
        long createdUtcMicroseconds)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(records);
        if (records.Count == 0)
        {
            throw new InvalidDataException("Mouse chunk must contain at least one event.");
        }

        EnsureForeignKeysExist(database.Connection, deviceId, calibrationSegmentId);
        long firstSequence = records[0].Sequence;
        long lastSequence = records[^1].Sequence;
        if (HasSequenceOverlap(database.Connection, null, deviceId, firstSequence, lastSequence))
        {
            throw new InvalidDataException("Mouse chunk sequence range overlaps an indexed chunk.");
        }

        string relativePath = GetFinalFileName(chunkId);
        string finalPath = ResolveCatalogPath(database.Layout, relativePath, chunkId);
        string temporaryPath = ResolveTemporaryPath(database.Layout, chunkId);
        MouseChunkFileWriteResult flushed = MouseChunkFile.WriteNew(
            temporaryPath,
            chunkId,
            deviceId,
            calibrationSegmentId,
            records);

        File.Move(temporaryPath, finalPath);
        var entry = CreateEntry(flushed, relativePath, createdUtcMicroseconds, recovered: false);
        using SqliteTransaction transaction = database.Connection.BeginTransaction();
        if (HasSequenceOverlap(
            database.Connection,
            transaction,
            entry.DeviceId,
            entry.FirstSequence,
            entry.LastSequence))
        {
            throw new InvalidDataException("Mouse chunk sequence range overlaps an indexed chunk.");
        }

        Insert(database.Connection, transaction, entry);
        transaction.Commit();
        return new MouseChunkDurableWatermark(
            chunkId,
            flushed.Header.EventCount,
            flushed.Header.EventCount,
            flushed.Header.EventCount,
            relativePath);
    }

    public static IReadOnlyList<MouseChunkCatalogEntry> ReadAll(StorageDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        IReadOnlyList<MouseChunkCatalogEntry> entries;
        try
        {
            entries = ReadRows(database.Connection);
        }
        catch (Exception error) when (IsCatalogValidationError(error))
        {
            throw new InvalidDataException("Mouse chunk catalog contains an invalid row.", error);
        }

        foreach (MouseChunkCatalogEntry entry in entries)
        {
            _ = ResolveCatalogPath(database.Layout, entry.RelativePath, entry.ChunkId);
        }

        for (int left = 0; left < entries.Count; left++)
        {
            for (int right = left + 1; right < entries.Count; right++)
            {
                if (RangesOverlap(entries[left], entries[right]))
                {
                    throw new InvalidDataException("Mouse chunk catalog contains overlapping sequence ranges.");
                }
            }
        }

        return entries;
    }

    public static MouseChunkRecoveryReport Recover(StorageDatabase database, long recoveredUtcMicroseconds)
    {
        ArgumentNullException.ThrowIfNull(database);
        var issues = new List<MouseChunkIntegrityIssue>();
        try
        {
            _ = GetValidatedChunkRoot(database.Layout);
        }
        catch (InvalidDataException)
        {
            issues.Add(CreateIssue(
                MouseChunkIssueSeverity.IntegrityFault,
                MouseChunkIssueCategory.UnsafeRelativePath,
                InvalidDiagnosticPath));
            return new MouseChunkRecoveryReport(
                new MouseChunkDiagnostics(0, 0, 0, 0, 0, 1, 0, 0),
                issues);
        }

        IReadOnlyList<MouseChunkCatalogEntry> catalog;
        try
        {
            catalog = ReadRows(database.Connection);
        }
        catch (Exception error) when (IsCatalogValidationError(error))
        {
            issues.Add(CreateIssue(
                MouseChunkIssueSeverity.IntegrityFault,
                MouseChunkIssueCategory.CatalogMismatch,
                InvalidDiagnosticPath));
            return new MouseChunkRecoveryReport(
                new MouseChunkDiagnostics(0, 0, 0, 0, 0, 1, 0, 0),
                issues);
        }

        var validCatalog = new List<MouseChunkCatalogEntry>();
        var catalogByChunk = new Dictionary<Guid, MouseChunkCatalogEntry>();
        var catalogByPath = new Dictionary<string, MouseChunkCatalogEntry>(StringComparer.OrdinalIgnoreCase);
        var observedFinalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<RecoveryCandidate>();
        long encoded = 0;
        long persisted = 0;
        long durable = 0;
        long recovered = 0;
        long orphan = 0;
        long corrupt = 0;
        long missingIndex = 0;
        long missingFile = 0;

        foreach (MouseChunkCatalogEntry entry in catalog)
        {
            catalogByChunk[entry.ChunkId] = entry;
            try
            {
                _ = ResolveCatalogPath(database.Layout, entry.RelativePath, entry.ChunkId);
                validCatalog.Add(entry);
                catalogByPath.Add(entry.RelativePath, entry);
            }
            catch (InvalidDataException)
            {
                corrupt++;
                issues.Add(CreateIssue(
                    MouseChunkIssueSeverity.IntegrityFault,
                    MouseChunkIssueCategory.UnsafeRelativePath,
                    InvalidDiagnosticPath));
            }
        }

        var overlappingCatalogChunks = new HashSet<Guid>();
        for (int left = 0; left < catalog.Count; left++)
        {
            for (int right = left + 1; right < catalog.Count; right++)
            {
                if (RangesOverlap(catalog[left], catalog[right]))
                {
                    overlappingCatalogChunks.Add(catalog[left].ChunkId);
                    overlappingCatalogChunks.Add(catalog[right].ChunkId);
                }
            }
        }

        foreach (MouseChunkCatalogEntry entry in catalog.Where(entry => overlappingCatalogChunks.Contains(entry.ChunkId)))
        {
            issues.Add(CreateIssue(
                MouseChunkIssueSeverity.IntegrityFault,
                MouseChunkIssueCategory.SequenceOverlap,
                GetDiagnosticCatalogPath(entry)));
        }

        foreach (string path in Directory.EnumerateFiles(database.Layout.ChunkDirectory, "*" + TemporaryExtension, SearchOption.TopDirectoryOnly))
        {
            string diagnosticPath = GetDiagnosticFileName(path, TemporaryExtension);
            if (IsReparsePoint(path))
            {
                corrupt++;
                issues.Add(CreateIssue(
                    MouseChunkIssueSeverity.IntegrityFault,
                    MouseChunkIssueCategory.UnsafeRelativePath,
                    diagnosticPath));
                continue;
            }

            try
            {
                MouseChunk chunk = MouseChunkReader.Read(path);
                encoded += chunk.Header.EventCount;
                persisted += chunk.Header.EventCount;
                orphan++;
                issues.Add(CreateIssue(
                    MouseChunkIssueSeverity.Observation,
                    MouseChunkIssueCategory.TemporaryFileValid,
                    diagnosticPath));
            }
            catch (Exception error) when (IsFileValidationError(error))
            {
                corrupt++;
                issues.Add(CreateIssue(
                    MouseChunkIssueSeverity.IntegrityFault,
                    MouseChunkIssueCategory.TemporaryFileCorrupt,
                    diagnosticPath));
            }
        }

        foreach (string path in Directory.EnumerateFiles(database.Layout.ChunkDirectory, "*" + FinalExtension, SearchOption.TopDirectoryOnly))
        {
            string relativePath = Path.GetFileName(path);
            string diagnosticPath = GetDiagnosticFileName(path, FinalExtension);
            observedFinalPaths.Add(relativePath);
            catalogByPath.TryGetValue(relativePath, out MouseChunkCatalogEntry? indexed);
            if (IsReparsePoint(path))
            {
                corrupt++;
                issues.Add(CreateIssue(
                    MouseChunkIssueSeverity.IntegrityFault,
                    MouseChunkIssueCategory.UnsafeRelativePath,
                    diagnosticPath));
                continue;
            }

            MouseChunk chunk;
            try
            {
                chunk = MouseChunkReader.Read(path);
            }
            catch (Exception error) when (IsFileValidationError(error))
            {
                corrupt++;
                orphan += indexed is null ? 1 : 0;
                issues.Add(CreateIssue(
                    MouseChunkIssueSeverity.IntegrityFault,
                    MouseChunkIssueCategory.CorruptFinalFile,
                    diagnosticPath));
                continue;
            }

            encoded += chunk.Header.EventCount;
            persisted += chunk.Header.EventCount;
            if (!string.Equals(relativePath, GetFinalFileName(chunk.Header.ChunkId), StringComparison.Ordinal))
            {
                corrupt++;
                orphan += indexed is null ? 1 : 0;
                issues.Add(CreateIssue(
                    MouseChunkIssueSeverity.IntegrityFault,
                    MouseChunkIssueCategory.CatalogMismatch,
                    diagnosticPath));
                continue;
            }

            if (indexed is not null)
            {
                if (overlappingCatalogChunks.Contains(indexed.ChunkId))
                {
                    continue;
                }

                if (Matches(indexed, chunk, new FileInfo(path).Length))
                {
                    durable += chunk.Header.EventCount;
                }
                else
                {
                    corrupt++;
                    issues.Add(CreateIssue(
                        MouseChunkIssueSeverity.IntegrityFault,
                        MouseChunkIssueCategory.CatalogMismatch,
                        diagnosticPath));
                }

                continue;
            }

            orphan++;
            missingIndex++;
            issues.Add(CreateIssue(
                MouseChunkIssueSeverity.Observation,
                MouseChunkIssueCategory.OrphanFinalFile,
                diagnosticPath));
            issues.Add(CreateIssue(
                MouseChunkIssueSeverity.Observation,
                MouseChunkIssueCategory.MissingIndex,
                diagnosticPath));

            if (catalogByChunk.ContainsKey(chunk.Header.ChunkId))
            {
                corrupt++;
                issues.Add(CreateIssue(
                    MouseChunkIssueSeverity.IntegrityFault,
                    MouseChunkIssueCategory.CatalogMismatch,
                    diagnosticPath));
                continue;
            }

            candidates.Add(new RecoveryCandidate(
                CreateEntry(chunk, relativePath, new FileInfo(path).Length, recoveredUtcMicroseconds),
                diagnosticPath));
        }

        foreach (MouseChunkCatalogEntry indexed in validCatalog)
        {
            if (!observedFinalPaths.Contains(indexed.RelativePath))
            {
                missingFile++;
                issues.Add(CreateIssue(
                    MouseChunkIssueSeverity.IntegrityFault,
                    MouseChunkIssueCategory.MissingFile,
                    GetDiagnosticCatalogPath(indexed)));
            }
        }

        var blocked = new HashSet<Guid>();
        foreach (RecoveryCandidate candidate in candidates)
        {
            MouseChunkCatalogEntry entry = candidate.Entry;
            if (!DeviceExists(database.Connection, null, entry.DeviceId))
            {
                blocked.Add(entry.ChunkId);
                issues.Add(CreateIssue(
                    MouseChunkIssueSeverity.IntegrityFault,
                    MouseChunkIssueCategory.MissingDevice,
                    candidate.DiagnosticPath));
            }

            if (!CalibrationSegmentExists(database.Connection, null, entry.CalibrationSegmentId))
            {
                blocked.Add(entry.ChunkId);
                issues.Add(CreateIssue(
                    MouseChunkIssueSeverity.IntegrityFault,
                    MouseChunkIssueCategory.MissingCalibrationSegment,
                    candidate.DiagnosticPath));
            }

            if (OverlapsAny(catalog, entry))
            {
                blocked.Add(entry.ChunkId);
                issues.Add(CreateIssue(
                    MouseChunkIssueSeverity.IntegrityFault,
                    MouseChunkIssueCategory.SequenceOverlap,
                    candidate.DiagnosticPath));
            }
        }

        for (int left = 0; left < candidates.Count; left++)
        {
            for (int right = left + 1; right < candidates.Count; right++)
            {
                if (RangesOverlap(candidates[left].Entry, candidates[right].Entry))
                {
                    blocked.Add(candidates[left].Entry.ChunkId);
                    blocked.Add(candidates[right].Entry.ChunkId);
                    issues.Add(CreateIssue(
                        MouseChunkIssueSeverity.IntegrityFault,
                        MouseChunkIssueCategory.SequenceOverlap,
                        candidates[left].DiagnosticPath));
                    issues.Add(CreateIssue(
                        MouseChunkIssueSeverity.IntegrityFault,
                        MouseChunkIssueCategory.SequenceOverlap,
                        candidates[right].DiagnosticPath));
                }
            }
        }

        foreach (RecoveryCandidate candidate in candidates)
        {
            if (blocked.Contains(candidate.Entry.ChunkId))
            {
                continue;
            }

            try
            {
                using SqliteTransaction transaction = database.Connection.BeginTransaction();
                MouseChunkCatalogEntry entry = candidate.Entry;
                if (!DeviceExists(database.Connection, transaction, entry.DeviceId))
                {
                    issues.Add(CreateIssue(
                        MouseChunkIssueSeverity.IntegrityFault,
                        MouseChunkIssueCategory.MissingDevice,
                        candidate.DiagnosticPath));
                    continue;
                }

                if (!CalibrationSegmentExists(database.Connection, transaction, entry.CalibrationSegmentId))
                {
                    issues.Add(CreateIssue(
                        MouseChunkIssueSeverity.IntegrityFault,
                        MouseChunkIssueCategory.MissingCalibrationSegment,
                        candidate.DiagnosticPath));
                    continue;
                }

                if (HasSequenceOverlap(
                    database.Connection,
                    transaction,
                    entry.DeviceId,
                    entry.FirstSequence,
                    entry.LastSequence))
                {
                    issues.Add(CreateIssue(
                        MouseChunkIssueSeverity.IntegrityFault,
                        MouseChunkIssueCategory.SequenceOverlap,
                        candidate.DiagnosticPath));
                    continue;
                }

                Insert(database.Connection, transaction, entry);
                transaction.Commit();
                durable += entry.EventCount;
                recovered += entry.EventCount;
            }
            catch (SqliteException)
            {
                issues.Add(CreateIssue(
                    MouseChunkIssueSeverity.IntegrityFault,
                    MouseChunkIssueCategory.CatalogInsertFailed,
                    candidate.DiagnosticPath));
            }
        }

        return new MouseChunkRecoveryReport(
            new MouseChunkDiagnostics(encoded, persisted, durable, recovered, orphan, corrupt, missingIndex, missingFile),
            issues);
    }

    private static IReadOnlyList<MouseChunkCatalogEntry> ReadRows(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT chunk_id, device_id, calibration_segment_id, relative_path,
                   first_sequence, last_sequence,
                   first_monotonic_timestamp, last_monotonic_timestamp,
                   event_count, canonical_length, compressed_length, file_length,
                   compressed_crc32c, canonical_sha256, created_utc_us, recovered
            FROM mouse_chunks
            ORDER BY chunk_id;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var entries = new List<MouseChunkCatalogEntry>();
        while (reader.Read())
        {
            entries.Add(new MouseChunkCatalogEntry(
                Guid.ParseExact(reader.GetString(0), "N"),
                Guid.ParseExact(reader.GetString(1), "N"),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetInt64(8),
                reader.GetInt64(9),
                reader.GetInt64(10),
                reader.GetInt64(11),
                checked((uint)reader.GetInt64(12)),
                reader.GetString(13),
                reader.GetInt64(14),
                reader.GetInt64(15) == 1));
        }

        return entries;
    }

    private static MouseChunkCatalogEntry CreateEntry(
        MouseChunkFileWriteResult flushed,
        string relativePath,
        long createdUtcMicroseconds,
        bool recovered) => new(
            flushed.Header.ChunkId,
            flushed.Header.DeviceId,
            flushed.Header.CalibrationSegmentId,
            relativePath,
            flushed.Header.FirstSequence,
            flushed.Header.LastSequence,
            flushed.Header.FirstMonotonicTimestamp,
            flushed.Header.LastMonotonicTimestamp,
            flushed.Header.EventCount,
            flushed.Header.CanonicalLength,
            flushed.Header.CompressedLength,
            flushed.FileLength,
            flushed.CompressedCrc32C,
            flushed.CanonicalSha256,
            createdUtcMicroseconds,
            recovered);

    private static MouseChunkCatalogEntry CreateEntry(
        MouseChunk chunk,
        string relativePath,
        long fileLength,
        long createdUtcMicroseconds) => new(
            chunk.Header.ChunkId,
            chunk.Header.DeviceId,
            chunk.Header.CalibrationSegmentId,
            relativePath,
            chunk.Header.FirstSequence,
            chunk.Header.LastSequence,
            chunk.Header.FirstMonotonicTimestamp,
            chunk.Header.LastMonotonicTimestamp,
            chunk.Header.EventCount,
            chunk.Header.CanonicalLength,
            chunk.Header.CompressedLength,
            fileLength,
            chunk.CompressedCrc32C,
            chunk.CanonicalSha256,
            createdUtcMicroseconds,
            Recovered: true);

    private static void Insert(SqliteConnection connection, SqliteTransaction transaction, MouseChunkCatalogEntry entry)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
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
                $crc, $sha, $created, $recovered);
            """;
        command.Parameters.AddWithValue("$chunk", entry.ChunkId.ToString("N"));
        command.Parameters.AddWithValue("$device", entry.DeviceId.ToString("N"));
        command.Parameters.AddWithValue("$segment", entry.CalibrationSegmentId);
        command.Parameters.AddWithValue("$path", entry.RelativePath);
        command.Parameters.AddWithValue("$firstSequence", entry.FirstSequence);
        command.Parameters.AddWithValue("$lastSequence", entry.LastSequence);
        command.Parameters.AddWithValue("$firstTimestamp", entry.FirstMonotonicTimestamp);
        command.Parameters.AddWithValue("$lastTimestamp", entry.LastMonotonicTimestamp);
        command.Parameters.AddWithValue("$events", entry.EventCount);
        command.Parameters.AddWithValue("$canonicalLength", entry.CanonicalLength);
        command.Parameters.AddWithValue("$compressedLength", entry.CompressedLength);
        command.Parameters.AddWithValue("$fileLength", entry.FileLength);
        command.Parameters.AddWithValue("$crc", (long)entry.CompressedCrc32C);
        command.Parameters.AddWithValue("$sha", entry.CanonicalSha256);
        command.Parameters.AddWithValue("$created", entry.CreatedUtcMicroseconds);
        command.Parameters.AddWithValue("$recovered", entry.Recovered ? 1 : 0);
        command.ExecuteNonQuery();
    }

    private static bool Matches(MouseChunkCatalogEntry entry, MouseChunk chunk, long fileLength) =>
        entry.ChunkId == chunk.Header.ChunkId
        && entry.DeviceId == chunk.Header.DeviceId
        && entry.CalibrationSegmentId == chunk.Header.CalibrationSegmentId
        && entry.FirstSequence == chunk.Header.FirstSequence
        && entry.LastSequence == chunk.Header.LastSequence
        && entry.FirstMonotonicTimestamp == chunk.Header.FirstMonotonicTimestamp
        && entry.LastMonotonicTimestamp == chunk.Header.LastMonotonicTimestamp
        && entry.EventCount == chunk.Header.EventCount
        && entry.CanonicalLength == chunk.Header.CanonicalLength
        && entry.CompressedLength == chunk.Header.CompressedLength
        && entry.FileLength == fileLength
        && entry.CompressedCrc32C == chunk.CompressedCrc32C
        && string.Equals(entry.CanonicalSha256, chunk.CanonicalSha256, StringComparison.Ordinal);

    private static void EnsureForeignKeysExist(SqliteConnection connection, Guid deviceId, long calibrationSegmentId)
    {
        if (!DeviceExists(connection, null, deviceId))
        {
            throw new InvalidDataException("Mouse chunk device is not registered.");
        }

        if (!CalibrationSegmentExists(connection, null, calibrationSegmentId))
        {
            throw new InvalidDataException("Mouse chunk calibration segment is not registered.");
        }
    }

    private static bool DeviceExists(SqliteConnection connection, SqliteTransaction? transaction, Guid deviceId) =>
        Exists(connection, transaction, "SELECT 1 FROM devices WHERE device_id = $value;", deviceId.ToString("N"));

    private static bool CalibrationSegmentExists(SqliteConnection connection, SqliteTransaction? transaction, long segmentId) =>
        Exists(connection, transaction, "SELECT 1 FROM time_calibration_segments WHERE segment_id = $value;", segmentId);

    private static bool Exists(SqliteConnection connection, SqliteTransaction? transaction, string sql, object value)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$value", value);
        return command.ExecuteScalar() is not null;
    }

    private static bool HasSequenceOverlap(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid deviceId,
        long firstSequence,
        long lastSequence)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1
            FROM mouse_chunks
            WHERE device_id = $device
              AND first_sequence <= $last
              AND last_sequence >= $first
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$device", deviceId.ToString("N"));
        command.Parameters.AddWithValue("$first", firstSequence);
        command.Parameters.AddWithValue("$last", lastSequence);
        return command.ExecuteScalar() is not null;
    }

    private static bool OverlapsAny(IEnumerable<MouseChunkCatalogEntry> entries, MouseChunkCatalogEntry candidate) =>
        entries.Any(entry => RangesOverlap(entry, candidate));

    private static bool RangesOverlap(MouseChunkCatalogEntry left, MouseChunkCatalogEntry right) =>
        left.DeviceId == right.DeviceId
        && left.FirstSequence <= right.LastSequence
        && left.LastSequence >= right.FirstSequence;

    private static string ResolveCatalogPath(StorageLayout layout, string relativePath, Guid chunkId)
    {
        if (chunkId == Guid.Empty
            || string.IsNullOrWhiteSpace(relativePath)
            || Path.IsPathFullyQualified(relativePath)
            || !string.Equals(relativePath, Path.GetFileName(relativePath), StringComparison.Ordinal)
            || !string.Equals(relativePath, GetFinalFileName(chunkId), StringComparison.Ordinal))
        {
            throw new InvalidDataException("Mouse chunk catalog relative path is invalid.");
        }

        string root = GetValidatedChunkRoot(layout);
        string rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Mouse chunk catalog relative path escapes the chunk directory.");
        }

        return fullPath;
    }

    private static string ResolveTemporaryPath(StorageLayout layout, Guid chunkId)
    {
        string root = GetValidatedChunkRoot(layout);
        string fullPath = Path.GetFullPath(Path.Combine(root, chunkId.ToString("N") + TemporaryExtension));
        string rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Mouse chunk temporary path escapes the chunk directory.");
        }

        return fullPath;
    }

    private static bool IsFileValidationError(Exception error) =>
        error is InvalidDataException or IOException or UnauthorizedAccessException;

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static string GetValidatedChunkRoot(StorageLayout layout)
    {
        string root = Path.GetFullPath(layout.ChunkDirectory);
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Mouse chunk directory cannot be a reparse point.");
        }

        return root;
    }

    private static bool IsCatalogValidationError(Exception error) =>
        error is InvalidDataException or FormatException or OverflowException or InvalidCastException;

    private static MouseChunkIntegrityIssue CreateIssue(
        MouseChunkIssueSeverity severity,
        MouseChunkIssueCategory category,
        string relativePath) => new(
            severity,
            category,
            relativePath,
            category.ToString(),
            GetControlledMessage(category));

    private static string GetControlledMessage(MouseChunkIssueCategory category) => category switch
    {
        MouseChunkIssueCategory.TemporaryFileValid => "Valid temporary chunk retained without catalog publication.",
        MouseChunkIssueCategory.TemporaryFileCorrupt => "Temporary chunk failed integrity validation and was retained.",
        MouseChunkIssueCategory.OrphanFinalFile => "Valid final chunk has no catalog entry.",
        MouseChunkIssueCategory.CorruptFinalFile => "Final chunk failed integrity validation and was retained.",
        MouseChunkIssueCategory.MissingIndex => "Catalog entry was missing before recovery.",
        MouseChunkIssueCategory.MissingFile => "Catalog entry has no final chunk file.",
        MouseChunkIssueCategory.CatalogMismatch => "Chunk header or content does not match its catalog identity.",
        MouseChunkIssueCategory.UnsafeRelativePath => "Catalog relative path was rejected before file access.",
        MouseChunkIssueCategory.MissingDevice => "Chunk device is not registered; orphan was retained without indexing.",
        MouseChunkIssueCategory.MissingCalibrationSegment => "Chunk calibration segment is not registered; orphan was retained without indexing.",
        MouseChunkIssueCategory.SequenceOverlap => "Chunk sequence range overlaps another chunk; orphan was retained without indexing.",
        MouseChunkIssueCategory.CatalogInsertFailed => "Chunk catalog insertion failed; orphan was retained without indexing.",
        _ => "Mouse chunk integrity observation."
    };

    private static string GetDiagnosticCatalogPath(MouseChunkCatalogEntry entry) =>
        entry.ChunkId != Guid.Empty
            && string.Equals(entry.RelativePath, GetFinalFileName(entry.ChunkId), StringComparison.Ordinal)
            ? entry.RelativePath
            : InvalidDiagnosticPath;

    private static string GetDiagnosticFileName(string path, string extension)
    {
        string name = Path.GetFileName(path);
        string stem = Path.GetFileNameWithoutExtension(name);
        return string.Equals(Path.GetExtension(name), extension, StringComparison.OrdinalIgnoreCase)
            && Guid.TryParseExact(stem, "N", out _)
            ? name.ToLowerInvariant()
            : UnknownDiagnosticFile;
    }

    private static string GetFinalFileName(Guid chunkId)
    {
        if (chunkId == Guid.Empty)
        {
            throw new ArgumentException("Chunk identifier must be non-empty.", nameof(chunkId));
        }

        return chunkId.ToString("N") + FinalExtension;
    }

    private sealed record RecoveryCandidate(MouseChunkCatalogEntry Entry, string DiagnosticPath);
}
