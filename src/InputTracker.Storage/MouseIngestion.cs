namespace InputTracker.Storage;

using System.Collections.ObjectModel;
using Microsoft.Data.Sqlite;

internal sealed class MouseIngestion : IDisposable
{
    private readonly StorageDatabase _database;
    private readonly Dictionary<Guid, DeviceState> _devices = [];
    private readonly Dictionary<Guid, MouseSpoolSegment> _headerOnlyCandidates = [];
    private IReadOnlyDictionary<Guid, long> _watermarks;
    private TimeCalibrationSegment? _calibration;
    private long _calibrationSegmentId;
    private StorageServiceOrdinals _ordinals;
    private bool _disposed;

    public MouseIngestion(StorageDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _watermarks = WrapWatermarks(database.ReadMouseDeviceWatermarks());
        _ordinals = database.ReadStorageServiceOrdinals();
        RecoverExistingArtifacts();
    }

    public bool HasBuffered => _devices.Values.Any(state => state.Builder.BufferedEvents > 0);
    public IReadOnlyDictionary<Guid, long> Watermarks => _watermarks;
    public StorageServiceOrdinals Ordinals => _ordinals;

    public void SetCalibration(TimeCalibrationSegment calibration, long calibrationSegmentId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (calibration.SessionId == Guid.Empty || calibrationSegmentId <= 0)
        {
            throw new InvalidDataException("Mouse calibration identity is invalid.");
        }

        long[] candidateCalibrationIds = _headerOnlyCandidates.Values
            .Where(candidate => candidate.Header.CalibrationSessionId == calibration.SessionId)
            .Select(candidate => candidate.Header.CalibrationSegmentId)
            .Distinct()
            .ToArray();
        if (candidateCalibrationIds.Length > 1)
        {
            throw new InvalidDataException("Header-only mouse spools disagree on the active calibration identity.");
        }

        if (candidateCalibrationIds.Length == 1)
        {
            if (!_database.CalibrationSegmentMatches(candidateCalibrationIds[0], calibration))
            {
                throw new InvalidDataException("A header-only mouse spool calibration differs from the active calibration.");
            }

            calibrationSegmentId = candidateCalibrationIds[0];
        }

        _calibration = calibration;
        _calibrationSegmentId = calibrationSegmentId;
    }

    public void Validate(StorageMouseTransition transition, long serviceLastTimestamp)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (transition.DeviceId == Guid.Empty
            || transition.CalibrationSessionId == Guid.Empty
            || transition.MonotonicTimestamp < 0
            || transition.MonotonicTimestamp < serviceLastTimestamp
            || transition.MotionKind > 1
            || (transition.ButtonChanges & ~MouseChunkFormat.KnownButtonMask) != 0)
        {
            throw new InvalidDataException("Mouse transition fields are invalid.");
        }

        if (_calibration is not { } calibration
            || transition.CalibrationSessionId != calibration.SessionId)
        {
            throw new InvalidDataException("Mouse transition calibration does not match the active segment.");
        }

        if (!_database.IsRegisteredMouse(transition.DeviceId))
        {
            throw new InvalidDataException("Mouse transition device is unknown or is not a mouse.");
        }

        if (_devices.TryGetValue(transition.DeviceId, out DeviceState? existing)
            && transition.MonotonicTimestamp < existing.LastMonotonicTimestamp)
        {
            throw new InvalidDataException("Mouse device monotonic time moved backwards.");
        }

        long durable = _watermarks.TryGetValue(transition.DeviceId, out long value) ? value : 0;
        long next = existing?.NextSequence ?? checked(durable + 1);
        if (next <= 0 || next == long.MaxValue)
        {
            throw new InvalidDataException("Mouse device sequence cannot advance.");
        }
    }

    public void WriteValidated(StorageMouseTransition transition)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        TimeCalibrationSegment calibration = _calibration!.Value;
        if (!_devices.TryGetValue(transition.DeviceId, out DeviceState? state))
        {
            long durable = _watermarks.TryGetValue(transition.DeviceId, out long value) ? value : 0;
            state = new DeviceState(
                new MouseChunkBuilder(calibration.Frequency),
                checked(durable + 1),
                transition.MonotonicTimestamp);
            _devices.Add(transition.DeviceId, state);
        }

        long sequence = state.NextSequence;
        var record = new MouseSpoolRecord(
            sequence,
            transition.MonotonicTimestamp,
            transition.DeltaX,
            transition.DeltaY,
            transition.MotionKind,
            transition.UsesVirtualDesktop,
            transition.ButtonChanges,
            transition.VerticalWheelDelta,
            transition.HorizontalWheelDelta);
        MouseChunkAppendResult append = state.Builder.Append(
            transition.DeviceId,
            _calibrationSegmentId,
            record);

        if (append.Sealed is null)
        {
            EnsureWriter(state, transition.DeviceId, calibration.SessionId, _calibrationSegmentId, sequence);
            state.Writer!.Append(record);
        }
        else if (append.Sealed.Records[^1].Sequence == sequence)
        {
            EnsureWriter(
                state,
                transition.DeviceId,
                calibration.SessionId,
                append.Sealed.CalibrationSegmentId,
                append.Sealed.Records[0].Sequence);
            state.Writer!.Append(record);
            Publish(state, append.Sealed);
        }
        else
        {
            Publish(state, append.Sealed);
            EnsureWriter(state, transition.DeviceId, calibration.SessionId, _calibrationSegmentId, sequence);
            state.Writer!.Append(record);
        }

        state.NextSequence = checked(sequence + 1);
        state.LastMonotonicTimestamp = transition.MonotonicTimestamp;
    }

    public void SealAll() => SealStates(_devices.Values);

    public void SealDevice(Guid deviceId)
    {
        if (_devices.TryGetValue(deviceId, out DeviceState? state))
        {
            Seal(state);
        }
    }

    public void SealCalibration(long calibrationSegmentId)
    {
        foreach (DeviceState state in _devices.Values.OrderBy(state => state.DeviceId))
        {
            MouseChunkBatch? batch = state.Builder.SealForCalibrationClose(calibrationSegmentId);
            if (batch is not null)
            {
                Publish(state, batch);
            }
        }
    }

    public void SealAtTimestamp(long monotonicTimestamp)
    {
        foreach (DeviceState state in _devices.Values.OrderBy(state => state.DeviceId))
        {
            MouseChunkBatch? batch = state.Builder.SealAtTimestamp(monotonicTimestamp);
            if (batch is not null)
            {
                Publish(state, batch);
            }
        }
    }

    public void RefreshPublishedState()
    {
        IReadOnlyDictionary<Guid, long> next = _database.ReadMouseDeviceWatermarks();
        if (!_watermarks.OrderBy(pair => pair.Key).SequenceEqual(next.OrderBy(pair => pair.Key)))
        {
            _watermarks = WrapWatermarks(next);
        }

        _ordinals = _database.ReadStorageServiceOrdinals();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (DeviceState state in _devices.Values)
        {
            state.Writer?.Dispose();
        }

        _devices.Clear();
    }

    private void SealStates(IEnumerable<DeviceState> states)
    {
        foreach (DeviceState state in states.OrderBy(state => state.DeviceId))
        {
            Seal(state);
        }
    }

    private void Seal(DeviceState state)
    {
        MouseChunkBatch? batch = state.Builder.SealRemaining();
        if (batch is not null)
        {
            Publish(state, batch);
        }
    }

    private void Publish(DeviceState state, MouseChunkBatch batch)
    {
        if (state.Writer is null)
        {
            throw new InvalidDataException("A sealed mouse batch has no spool writer.");
        }

        state.Writer.FlushFile();
        string spoolPath = state.Writer.Path;
        state.Writer.Dispose();
        state.Writer = null;
        MouseSpoolSegment segment = MouseSpoolSegmentReader.Read(spoolPath);
        if (segment.Header.DeviceId != batch.DeviceId
            || segment.Header.CalibrationSegmentId != batch.CalibrationSegmentId
            || !segment.Records.SequenceEqual(batch.Records))
        {
            throw new InvalidDataException("Mouse builder batch and spool segment differ.");
        }

        StorageMousePrefixCommit committed = _database.CommitMouseSpoolSegment(
            segment,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000,
            ToPlaybackBoundaryReason(batch.Reason));
        _ordinals = new StorageServiceOrdinals(
            committed.CommitOrdinal,
            committed.KeyboardAggregateCommitOrdinal);
        _watermarks = _database.ReadMouseDeviceWatermarks();
        _database.CommitMouseChunk(
            Guid.NewGuid(),
            batch.DeviceId,
            batch.CalibrationSegmentId,
            batch.Records,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000);
    }

    private void EnsureWriter(
        DeviceState state,
        Guid deviceId,
        Guid calibrationSessionId,
        long calibrationSegmentId,
        long firstSequence)
    {
        if (state.Writer is not null)
        {
            return;
        }

        Guid segmentId;
        string path;
        if (_headerOnlyCandidates.Remove(deviceId, out MouseSpoolSegment? candidate))
        {
            if (candidate.Header.DeviceId != deviceId
                || candidate.Header.CalibrationSessionId != calibrationSessionId
                || candidate.Header.CalibrationSegmentId != calibrationSegmentId
                || candidate.Header.FirstSequence != firstSequence)
            {
                throw new InvalidDataException("The retained header-only mouse spool does not match the active device and calibration boundary.");
            }

            segmentId = candidate.Header.SegmentId;
            path = candidate.Path;
        }
        else
        {
            segmentId = Guid.NewGuid();
            path = Path.Combine(_database.Layout.SpoolDirectory, segmentId.ToString("N") + ".mspool");
        }

        state.DeviceId = deviceId;
        state.Writer = new MouseSpoolWriter(
            path,
            new MouseSpoolHeader(
                segmentId,
                deviceId,
                calibrationSessionId,
                calibrationSegmentId,
                firstSequence));
    }

    private void RecoverExistingArtifacts()
    {
        ValidateContainedDirectoryChain(_database.Layout.ProgramDirectory, _database.Layout.SpoolDirectory, "Mouse spool");
        ValidateContainedDirectoryChain(_database.Layout.ProgramDirectory, _database.Layout.ChunkDirectory, "Mouse chunk");
        bool adoptionComplete = _database.IsMouseV3AdoptionComplete();
        ValidateHeaderlessLegacySpools();
        IReadOnlyDictionary<Guid, PublishedSpoolRow> published = ReadPublishedSpoolRows();
        MouseSpoolSegment[] segments = EnumerateContainedFiles(_database.Layout.SpoolDirectory, "*.mspool")
            .Select(path =>
            {
                MouseSpoolSegment segment = MouseSpoolSegmentReader.Read(path);
                _database.ValidateMouseSpoolIdentity(segment.Header);
                return segment;
            })
            .OrderBy(segment => segment.Header.DeviceId)
            .ThenBy(segment => segment.Header.FirstSequence)
            .ToArray();
        ValidatePublishedPrefix(published.Values, _watermarks);
        var observedPublished = new HashSet<Guid>();
        var projected = new Dictionary<Guid, long>(_watermarks);
        var unpublished = new List<MouseSpoolSegment>();
        foreach (MouseSpoolSegment segment in segments)
        {
            if (segment.Records.Count == 0)
            {
                if (published.ContainsKey(segment.Header.SegmentId))
                {
                    throw new InvalidDataException("A header-only mouse spool cannot be published.");
                }

                if (!_headerOnlyCandidates.TryAdd(segment.Header.DeviceId, segment))
                {
                    throw new InvalidDataException("Multiple header-only mouse spools compete for one device.");
                }

                continue;
            }

            string canonicalHash = GetCanonicalHash(
                segment.Header.DeviceId,
                segment.Header.CalibrationSegmentId,
                segment.Records);
            if (published.TryGetValue(segment.Header.SegmentId, out PublishedSpoolRow? row))
            {
                if (!Matches(row, segment, canonicalHash))
                {
                    throw new InvalidDataException("Published mouse spool row and file differ.");
                }

                observedPublished.Add(segment.Header.SegmentId);
                continue;
            }

            projected.TryGetValue(segment.Header.DeviceId, out long current);
            long last = segment.Records[^1].Sequence;

            if (segment.Header.FirstSequence == checked(current + 1))
            {
                projected[segment.Header.DeviceId] = last;
                unpublished.Add(segment);
            }
            else if (last <= current
                && HasEquivalentPublishedRange(
                    segment.Header.DeviceId,
                    segment.Header.CalibrationSegmentId,
                    segment.Header.FirstSequence,
                    last,
                    canonicalHash))
            {
                continue;
            }
            else
            {
                throw new InvalidDataException("Unpublished mouse spool does not form the next device-global range.");
            }
        }

        if (published.Keys.Any(id => !observedPublished.Contains(id)))
        {
            throw new InvalidDataException("A published mouse spool file is missing.");
        }

        foreach (MouseSpoolSegment segment in unpublished)
        {
            _database.CommitMouseSpoolSegment(
                segment,
                GetUtcMicroseconds(),
                StorageMousePlaybackBoundaryReason.RecoveryUnknown);
        }

        RefreshPublishedState();
        if (adoptionComplete)
        {
            MouseChunkRecoveryReport chunkRecovery = _database.RecoverMouseChunks(GetUtcMicroseconds());
            if (chunkRecovery.HasIntegrityFault)
            {
                throw new InvalidDataException("A pre-existing mouse chunk artifact failed integrity validation.");
            }

            ValidateCatalogCoveredByPublishedSpools();
        }
        else
        {
            MouseChunkRecoveryReport chunkRecovery = _database.RecoverMouseChunks(GetUtcMicroseconds());
            if (chunkRecovery.HasIntegrityFault)
            {
                throw new InvalidDataException("A pre-existing mouse chunk artifact failed integrity validation.");
            }

            RecoverLegacyChunks();
            RefreshPublishedState();
            ValidateCatalogCoveredByPublishedSpools();
            _database.CompleteMouseV3Adoption();
        }

        RecoverTemporaryChunks();
        RefreshPublishedState();
    }

    private void RecoverLegacyChunks()
    {
        IReadOnlyList<MouseChunkCatalogEntry> entries = _database.ReadMouseChunkCatalog();
        var verified = new List<(MouseChunkCatalogEntry Entry, MouseChunk Chunk)>();
        foreach (MouseChunkCatalogEntry entry in entries)
        {
            string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                _database.Layout.ChunkDirectory,
                entry.RelativePath));
            MouseChunk chunk = MouseChunkReader.Read(path);
            if (chunk.Header.ChunkId != entry.ChunkId
                || chunk.Header.DeviceId != entry.DeviceId
                || chunk.Header.CalibrationSegmentId != entry.CalibrationSegmentId
                || chunk.Header.FirstSequence != entry.FirstSequence
                || chunk.Header.LastSequence != entry.LastSequence
                || chunk.Header.FirstMonotonicTimestamp != entry.FirstMonotonicTimestamp
                || chunk.Header.LastMonotonicTimestamp != entry.LastMonotonicTimestamp
                || chunk.Header.EventCount != entry.EventCount
                || !string.Equals(chunk.CanonicalSha256, entry.CanonicalSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Legacy mouse chunk row and final file differ.");
            }

            verified.Add((entry, chunk));
        }

        var projected = new Dictionary<Guid, long>(_watermarks);
        var planned = new List<(MouseChunkCatalogEntry Entry, MouseChunk Chunk)>();
        foreach ((MouseChunkCatalogEntry entry, MouseChunk chunk) in verified
            .OrderBy(pair => pair.Entry.DeviceId)
            .ThenBy(pair => pair.Entry.FirstSequence))
        {
            projected.TryGetValue(entry.DeviceId, out long current);
            if (entry.LastSequence <= current
                && HasEquivalentPublishedRange(
                    entry.DeviceId,
                    entry.CalibrationSegmentId,
                    entry.FirstSequence,
                    entry.LastSequence,
                    entry.CanonicalSha256))
            {
                continue;
            }

            if (entry.FirstSequence != checked(current + 1))
            {
                throw new InvalidDataException("Legacy mouse chunks do not form one device-global contiguous prefix.");
            }

            projected[entry.DeviceId] = entry.LastSequence;
            planned.Add((entry, chunk));
        }

        foreach ((MouseChunkCatalogEntry entry, MouseChunk chunk) in planned)
        {
            Guid sessionId = ReadCalibrationSessionId(entry.CalibrationSegmentId);
            Guid segmentId = Guid.NewGuid();
            string path = Path.Combine(
                _database.Layout.SpoolDirectory,
                segmentId.ToString("N") + ".mspool");
            using (var writer = new MouseSpoolWriter(
                path,
                new MouseSpoolHeader(
                    segmentId,
                    entry.DeviceId,
                    sessionId,
                    entry.CalibrationSegmentId,
                    entry.FirstSequence)))
            {
                foreach (MouseSpoolRecord record in chunk.Records)
                {
                    writer.Append(record);
                }

                writer.FlushFile();
            }

            MouseSpoolSegment segment = MouseSpoolSegmentReader.Read(path);
            _database.CommitMouseSpoolSegment(
                segment,
                GetUtcMicroseconds(),
                StorageMousePlaybackBoundaryReason.RecoveryUnknown);
        }
    }

    private void RecoverTemporaryChunks()
    {
        string[] temporaryPaths = Directory.EnumerateFiles(_database.Layout.ChunkDirectory, "*.tmp")
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (string temporaryPath in temporaryPaths)
        {
            MouseChunk temporary = MouseChunkReader.Read(temporaryPath);
            _watermarks.TryGetValue(temporary.Header.DeviceId, out long durable);
            if (temporary.Header.LastSequence > durable
                || !HasEquivalentPublishedRange(
                    temporary.Header.DeviceId,
                    temporary.Header.CalibrationSegmentId,
                    temporary.Header.FirstSequence,
                    temporary.Header.LastSequence,
                    temporary.CanonicalSha256))
            {
                throw new InvalidDataException("A temporary mouse chunk is not covered by an equivalent published spool prefix.");
            }

            IReadOnlyList<MouseChunkCatalogEntry> catalog = _database.ReadMouseChunkCatalog();
            MouseChunkCatalogEntry? sameRange = catalog.SingleOrDefault(entry =>
                entry.DeviceId == temporary.Header.DeviceId
                && entry.FirstSequence == temporary.Header.FirstSequence
                && entry.LastSequence == temporary.Header.LastSequence);
            if (sameRange is not null)
            {
                if (!string.Equals(sameRange.CanonicalSha256, temporary.CanonicalSha256, StringComparison.Ordinal)
                    || sameRange.CalibrationSegmentId != temporary.Header.CalibrationSegmentId)
                {
                    throw new InvalidDataException("A temporary mouse chunk conflicts with its cataloged range.");
                }

                continue;
            }

            _database.CommitMouseChunk(
                Guid.NewGuid(),
                temporary.Header.DeviceId,
                temporary.Header.CalibrationSegmentId,
                temporary.Records,
                GetUtcMicroseconds());
        }
    }

    private void ValidateHeaderlessLegacySpools()
    {
        string[] paths = EnumerateContainedFiles(_database.Layout.SpoolDirectory, "*")
            .Where(path => !string.Equals(Path.GetExtension(path), ".mspool", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (string path in paths)
        {
            IReadOnlyList<MouseSpoolRecord> records = LegacyMouseSpoolReader.Read(path);
            string spoolId = Path.GetFileNameWithoutExtension(path);
            using SqliteCommand command = _database.Connection.CreateCommand();
            command.CommandText = """
                SELECT device_id, committed_length, record_count, power_durable_count
                FROM mouse_spool_commits
                WHERE spool_id = $spool;
                """;
            command.Parameters.AddWithValue("$spool", spoolId);
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read()
                || !Guid.TryParseExact(reader.GetString(0), "N", out Guid deviceId)
                || deviceId == Guid.Empty
                || reader.GetInt64(1) != new FileInfo(path).Length
                || reader.GetInt64(2) != records.Count
                || reader.GetInt64(3) != records.Count
                || reader.Read())
            {
                throw new InvalidDataException("A headerless mouse spool lacks one exact legacy durability row.");
            }

            long first = records[0].Sequence;
            long last = records[^1].Sequence;
            IReadOnlyList<MouseChunkCatalogEntry> candidates = _database.ReadMouseChunkCatalog()
                .Where(entry => entry.DeviceId == deviceId
                    && entry.FirstSequence == first
                    && entry.LastSequence == last
                    && entry.EventCount == records.Count)
                .ToArray();
            if (candidates.Count != 1)
            {
                throw new InvalidDataException("A headerless mouse spool lacks one unique cataloged chunk equivalence proof.");
            }

            MouseChunkCatalogEntry candidate = candidates[0];
            string chunkPath = GetContainedFilePath(
                _database.Layout.ChunkDirectory,
                candidate.RelativePath,
                requireExists: true);
            MouseChunk chunk = MouseChunkReader.Read(chunkPath);
            string canonicalHash = GetCanonicalHash(deviceId, candidate.CalibrationSegmentId, records);
            if (chunk.Header.DeviceId != deviceId
                || chunk.Header.CalibrationSegmentId != candidate.CalibrationSegmentId
                || !chunk.Records.SequenceEqual(records)
                || !string.Equals(chunk.CanonicalSha256, canonicalHash, StringComparison.Ordinal)
                || !string.Equals(candidate.CanonicalSha256, canonicalHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException("A headerless mouse spool differs from its unique cataloged chunk.");
            }

            Guid sessionId = ReadCalibrationSessionId(candidate.CalibrationSegmentId);
            _database.ValidateMouseSpoolIdentity(new MouseSpoolHeader(
                Guid.NewGuid(), deviceId, sessionId, candidate.CalibrationSegmentId, first));
        }
    }

    private void ValidateCatalogCoveredByPublishedSpools()
    {
        foreach (MouseChunkCatalogEntry entry in _database.ReadMouseChunkCatalog())
        {
            string path = GetContainedFilePath(
                _database.Layout.ChunkDirectory,
                entry.RelativePath,
                requireExists: true);
            MouseChunk chunk = MouseChunkReader.Read(path);
            if (chunk.Header.ChunkId != entry.ChunkId
                || chunk.Header.DeviceId != entry.DeviceId
                || chunk.Header.CalibrationSegmentId != entry.CalibrationSegmentId
                || chunk.Header.FirstSequence != entry.FirstSequence
                || chunk.Header.LastSequence != entry.LastSequence
                || !string.Equals(chunk.CanonicalSha256, entry.CanonicalSha256, StringComparison.Ordinal)
                || !HasEquivalentPublishedRange(
                    entry.DeviceId,
                    entry.CalibrationSegmentId,
                    entry.FirstSequence,
                    entry.LastSequence,
                    entry.CanonicalSha256))
            {
                throw new InvalidDataException("A cataloged mouse chunk is not covered by one equivalent published spool range.");
            }
        }
    }

    private static void ValidatePublishedPrefix(
        IEnumerable<PublishedSpoolRow> rows,
        IReadOnlyDictionary<Guid, long> watermarks)
    {
        ILookup<Guid, PublishedSpoolRow> byDevice = rows.ToLookup(row => row.DeviceId);
        var devices = byDevice.Select(group => group.Key)
            .Concat(watermarks.Keys)
            .Distinct()
            .ToArray();
        foreach (Guid deviceId in devices)
        {
            PublishedSpoolRow[] ordered = byDevice[deviceId]
                .OrderBy(row => row.FirstSequence)
                .ToArray();
            if (!watermarks.TryGetValue(deviceId, out long watermark)
                || watermark <= 0
                || ordered.Length == 0)
            {
                throw new InvalidDataException("Mouse durable watermark and published rows do not describe the same non-empty prefix.");
            }

            long expected = 1;
            foreach (PublishedSpoolRow row in ordered)
            {
                if (row.FirstSequence != expected)
                {
                    throw new InvalidDataException("Published mouse spool rows contain a device-global gap or overlap.");
                }

                expected = checked(row.LastSequence + 1);
            }

            if (ordered[^1].LastSequence != watermark)
            {
                throw new InvalidDataException("Mouse durable watermark does not equal the exact published prefix end.");
            }
        }
    }

    private static string[] EnumerateContainedFiles(string root, string pattern)
    {
        return Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly)
            .Select(path => GetContainedFilePath(root, Path.GetFileName(path), requireExists: true))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string GetContainedFilePath(string root, string name, bool requireExists)
    {
        if (string.IsNullOrWhiteSpace(name)
            || Path.IsPathFullyQualified(name)
            || !string.Equals(name, Path.GetFileName(name), StringComparison.Ordinal))
        {
            throw new InvalidDataException("Mouse artifact path is not one contained file name.");
        }

        string fullRoot = Path.GetFullPath(root);
        string fullPath = Path.GetFullPath(Path.Combine(fullRoot, name));
        string prefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || (requireExists && !File.Exists(fullPath)))
        {
            throw new InvalidDataException("Mouse artifact path escapes its contained directory or is missing.");
        }

        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Mouse artifact file cannot be a reparse point.");
        }

        return fullPath;
    }

    private static void ValidateContainedDirectoryChain(string programRoot, string root, string description)
    {
        string fullProgramRoot = Path.GetFullPath(programRoot);
        string fullRoot = Path.GetFullPath(root);
        string programPrefix = fullProgramRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullRoot.StartsWith(programPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"{description} directory escapes the program directory.");
        }

        string relative = Path.GetRelativePath(fullProgramRoot, fullRoot);
        string current = fullProgramRoot;
        foreach (string component in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Directory.Exists(current)
                || (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"{description} directory chain contains a missing directory or reparse point.");
            }

            current = Path.Combine(current, component);
        }

        if (!Directory.Exists(current)
            || (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"{description} directory chain contains a missing directory or reparse point.");
        }
    }

    private IReadOnlyDictionary<Guid, PublishedSpoolRow> ReadPublishedSpoolRows()
    {
        using SqliteCommand command = _database.Connection.CreateCommand();
        command.CommandText = """
            SELECT spool_segment_id, device_id, calibration_segment_id, relative_path,
                   first_sequence, last_sequence, record_count, committed_length,
                   spool_file_sha256, canonical_sha256
            FROM mouse_spool_segments
            ORDER BY device_id, first_sequence;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new Dictionary<Guid, PublishedSpoolRow>();
        while (reader.Read())
        {
            var row = new PublishedSpoolRow(
                Guid.ParseExact(reader.GetString(0), "N"),
                Guid.ParseExact(reader.GetString(1), "N"),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetString(8),
                reader.GetString(9));
            if (!rows.TryAdd(row.SegmentId, row))
            {
                throw new InvalidDataException("Mouse spool directory contains a duplicate segment.");
            }
        }

        return rows;
    }

    private bool HasEquivalentPublishedRange(
        Guid deviceId,
        long calibrationSegmentId,
        long firstSequence,
        long lastSequence,
        string canonicalHash)
    {
        using SqliteCommand command = _database.Connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM mouse_spool_segments
            WHERE device_id = $device
              AND calibration_segment_id = $calibration
              AND first_sequence = $first
              AND last_sequence = $last
              AND canonical_sha256 = $hash;
            """;
        command.Parameters.AddWithValue("$device", deviceId.ToString("N"));
        command.Parameters.AddWithValue("$calibration", calibrationSegmentId);
        command.Parameters.AddWithValue("$first", firstSequence);
        command.Parameters.AddWithValue("$last", lastSequence);
        command.Parameters.AddWithValue("$hash", canonicalHash);
        return (long)command.ExecuteScalar()! == 1;
    }

    private Guid ReadCalibrationSessionId(long calibrationSegmentId)
    {
        using SqliteCommand command = _database.Connection.CreateCommand();
        command.CommandText = "SELECT session_id FROM time_calibration_segments WHERE segment_id = $segment;";
        command.Parameters.AddWithValue("$segment", calibrationSegmentId);
        object? value = command.ExecuteScalar();
        if (value is not string text || !Guid.TryParseExact(text, "N", out Guid sessionId) || sessionId == Guid.Empty)
        {
            throw new InvalidDataException("Mouse calibration session is missing or invalid.");
        }

        return sessionId;
    }

    private static bool Matches(PublishedSpoolRow row, MouseSpoolSegment segment, string canonicalHash) =>
        row.SegmentId == segment.Header.SegmentId
        && row.DeviceId == segment.Header.DeviceId
        && row.CalibrationSegmentId == segment.Header.CalibrationSegmentId
        && string.Equals(row.RelativePath, segment.Header.SegmentId.ToString("N") + ".mspool", StringComparison.Ordinal)
        && row.FirstSequence == segment.Header.FirstSequence
        && row.LastSequence == segment.Records[^1].Sequence
        && row.RecordCount == segment.Records.Count
        && row.CommittedLength == segment.ValidLength
        && string.Equals(row.SpoolFileSha256, segment.SpoolFileSha256, StringComparison.Ordinal)
        && string.Equals(row.CanonicalSha256, canonicalHash, StringComparison.Ordinal);

    private static string GetCanonicalHash(
        Guid deviceId,
        long calibrationSegmentId,
        IReadOnlyList<MouseSpoolRecord> records) =>
        MouseChunkCodec.Encode(Guid.NewGuid(), deviceId, calibrationSegmentId, records).CanonicalSha256;

    private static StorageMousePlaybackBoundaryReason ToPlaybackBoundaryReason(MouseChunkSealReason reason) =>
        reason switch
        {
            MouseChunkSealReason.DurationLimit => StorageMousePlaybackBoundaryReason.DurationLimit,
            MouseChunkSealReason.EventLimit => StorageMousePlaybackBoundaryReason.EventLimit,
            MouseChunkSealReason.CanonicalSizeLimit => StorageMousePlaybackBoundaryReason.CanonicalSizeLimit,
            MouseChunkSealReason.DeviceBoundary => StorageMousePlaybackBoundaryReason.DeviceBoundary,
            MouseChunkSealReason.CalibrationBoundary => StorageMousePlaybackBoundaryReason.CalibrationBoundary,
            MouseChunkSealReason.PauseBoundary => StorageMousePlaybackBoundaryReason.PauseBoundary,
            MouseChunkSealReason.ExplicitFlush => StorageMousePlaybackBoundaryReason.ExplicitFlush,
            _ => throw new InvalidDataException("Mouse chunk seal reason is invalid.")
        };

    private static long GetUtcMicroseconds() =>
        checked(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000);

    private static IReadOnlyDictionary<Guid, long> WrapWatermarks(
        IReadOnlyDictionary<Guid, long> source) =>
        new ReadOnlyDictionary<Guid, long>(new Dictionary<Guid, long>(source));

    private sealed class DeviceState(
        MouseChunkBuilder builder,
        long nextSequence,
        long lastMonotonicTimestamp)
    {
        public Guid DeviceId { get; set; }
        public MouseChunkBuilder Builder { get; } = builder;
        public MouseSpoolWriter? Writer { get; set; }
        public long NextSequence { get; set; } = nextSequence;
        public long LastMonotonicTimestamp { get; set; } = lastMonotonicTimestamp;
    }

    private sealed record PublishedSpoolRow(
        Guid SegmentId,
        Guid DeviceId,
        long CalibrationSegmentId,
        string RelativePath,
        long FirstSequence,
        long LastSequence,
        long RecordCount,
        long CommittedLength,
        string SpoolFileSha256,
        string CanonicalSha256);
}
