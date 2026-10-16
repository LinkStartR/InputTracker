using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace InputTracker.Storage;

public sealed record StorageBackupArtifact(string Path, string Sha256, long Length, int FileCount);

public sealed record StorageRestoreResult(StorageBackupArtifact PreRestoreBackup);

internal readonly record struct StorageBackupBudgetEntry(long Length, long CompressedLength);

internal readonly record struct StorageRestoreBudgetPolicy(
    int MaximumEntryCount,
    long MaximumSingleEntryBytes,
    long MaximumTotalUncompressedBytes,
    long MaximumCompressionExpansionRatio,
    long CompressionRatioSlackBytes,
    long MinimumFreeSpaceBytes);

internal interface IStorageRestoreFaultInjector
{
    bool LeavesJournalForRecovery { get; }
    void BeforeMove(string source, string destination);
    void AfterMove(string source, string destination);
}

internal sealed class NoStorageRestoreFaultInjector : IStorageRestoreFaultInjector
{
    public static NoStorageRestoreFaultInjector Instance { get; } = new();
    public bool LeavesJournalForRecovery => false;
    public void BeforeMove(string source, string destination) { }
    public void AfterMove(string source, string destination) { }
}

internal static class StorageBackup
{
    private const int FormatVersion = 1;
    private const int MaximumManifestBytes = 1024 * 1024;
    internal const int MaximumEntryCount = 100_000;
    internal const long MaximumSingleEntryBytes = 64L * 1024 * 1024 * 1024;
    internal const long MaximumTotalUncompressedBytes = 512L * 1024 * 1024 * 1024;
    internal const long MaximumCompressionExpansionRatio = 1000;
    internal const long CompressionRatioSlackBytes = 64L * 1024 * 1024;
    internal const long MinimumFreeSpaceBytes = 2L * 1024 * 1024 * 1024;
    private const string RestoreJournalName = "restore-journal.json";
    private static readonly StorageRestoreBudgetPolicy ProductionBudget = new(
        MaximumEntryCount,
        MaximumSingleEntryBytes,
        MaximumTotalUncompressedBytes,
        MaximumCompressionExpansionRatio,
        CompressionRatioSlackBytes,
        MinimumFreeSpaceBytes);

    internal static StorageRestoreResult Restore(StorageLayout layout, string relativeBackupPath)
        => RestoreCore(layout, relativeBackupPath, NoStorageRestoreFaultInjector.Instance, ProductionBudget);

    internal static StorageRestoreResult RestoreForTests(
        StorageLayout layout,
        string relativeBackupPath,
        IStorageRestoreFaultInjector faultInjector) =>
        RestoreCore(layout, relativeBackupPath, faultInjector, ProductionBudget);

    internal static StorageRestoreResult RestoreForTests(
        StorageLayout layout,
        string relativeBackupPath,
        IStorageRestoreFaultInjector faultInjector,
        StorageRestoreBudgetPolicy budget) =>
        RestoreCore(layout, relativeBackupPath, faultInjector, budget);

    internal static void RecoverInterruptedRestore(StorageLayout layout, FileStream ownerLock)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(ownerLock);
        string expectedLock = Path.Combine(layout.StorageDirectory, "storage-service.writer.lock");
        if (!ownerLock.CanWrite || !PathEquals(ownerLock.Name, expectedLock))
            throw new InvalidOperationException("Restore recovery requires the exact live store writer lock.");
        string journalPath = GetJournalPath(layout);
        if (!File.Exists(journalPath)) return;
        ValidateExistingFileChain(layout.ProgramDirectory, journalPath);
        RestoreOperationJournal journal = ReadJournal(journalPath);
        RollBackJournal(layout, journal);
        DeleteTemporaryDirectory(journal.RollbackRoot);
        DeleteTemporaryDirectory(journal.StagingRoot);
        File.Delete(journalPath);
    }

    private static StorageRestoreResult RestoreCore(
        StorageLayout layout,
        string relativeBackupPath,
        IStorageRestoreFaultInjector faultInjector,
        StorageRestoreBudgetPolicy budget)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(faultInjector);
        string backupPath = ResolveExistingBackup(layout, relativeBackupPath);
        string operationId = Guid.NewGuid().ToString("N");
        string stagingRoot = CreateContainedDirectory(
            layout.ProgramDirectory, Path.Combine("data", "restore-staging", operationId));
        StorageLayout stagingLayout = StorageLayout.Create(stagingRoot);
        StorageBackupManifest manifest;
        try
        {
            string ownerLockPath = Path.Combine(layout.StorageDirectory, "storage-service.writer.lock");
            using var ownerLock = new FileStream(
                ownerLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
            RecoverInterruptedRestore(layout, ownerLock);
            manifest = ExtractAndVerify(backupPath, stagingRoot, budget);
            if (manifest.FormatVersion != FormatVersion || !IsSupportedBackupSchema(manifest.SchemaVersion))
            {
                throw new InvalidDataException("Backup format or storage schema version is not supported.");
            }

            using (StorageService validation = StorageService.Open(stagingLayout))
            {
                if (validation.Health.IntegrityFault is not null)
                {
                    throw new InvalidDataException("Backup staging store entered an integrity fault.");
                }
            }

            StorageBackupArtifact preRestore;
            using (StorageDatabase live = StorageDatabase.OpenOwned(layout, SystemStorageClock.Instance, ownerLock))
            {
                string automaticRelative = Path.Combine(
                    "backups", "automatic", $"before-restore-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{operationId}.itbackup");
                preRestore = CreateCore(live, automaticRelative);
            }

            string rollbackRoot = CreateContainedDirectory(
                layout.ProgramDirectory, Path.Combine("data", "restore-rollback", operationId));
            RestoreOperationJournal journal = CreateJournal(layout, stagingLayout, rollbackRoot, operationId);
            WriteJournal(layout, journal, replace: false);
            bool journalResolved = false;
            try
            {
                ExecuteJournalMoves(layout, journal, faultInjector);
                using StorageDatabase restored = StorageDatabase.OpenOwned(
                    layout, SystemStorageClock.Instance, ownerLock);
                if (restored.SchemaVersion != StorageSchema.CurrentVersion)
                {
                    throw new InvalidDataException("Restored storage schema changed during installation.");
                }
                File.Delete(GetJournalPath(layout));
                journalResolved = true;
            }
            catch when (!faultInjector.LeavesJournalForRecovery)
            {
                RollBackJournal(layout, ReadJournal(GetJournalPath(layout)));
                File.Delete(GetJournalPath(layout));
                journalResolved = true;
                throw;
            }
            finally
            {
                if (journalResolved) DeleteTemporaryDirectory(rollbackRoot);
            }
            return new StorageRestoreResult(preRestore);
        }
        finally
        {
            if (!File.Exists(GetJournalPath(layout))) DeleteTemporaryDirectory(stagingRoot);
        }
    }

    internal static StorageBackupArtifact Create(
        StorageDatabase database,
        string relativeBackupPath) => CreateCore(database, relativeBackupPath);

    private static StorageBackupArtifact CreateCore(
        StorageDatabase database,
        string relativeBackupPath)
    {
        string destination = ResolveNewBackup(database.Layout, relativeBackupPath);
        string operationId = Guid.NewGuid().ToString("N");
        string stagingRoot = CreateContainedDirectory(
            database.Layout.ProgramDirectory, Path.Combine("data", "backup-staging", operationId));
        try
        {
            StorageLayout staging = StorageLayout.Create(stagingRoot);
            CopyDatabaseSnapshot(database.Connection, staging.DatabasePath);
            CopyCommittedArtifacts(database, "mouse_spool_segments", database.Layout.SpoolDirectory, staging.SpoolDirectory, ".mspool");
            CopyCommittedArtifacts(database, "mouse_chunks", database.Layout.ChunkDirectory, staging.ChunkDirectory, ".mchunk");
            using (StorageService validation = StorageService.Open(staging))
            {
                if (validation.Health.IntegrityFault is not null)
                {
                    throw new InvalidDataException("Backup staging store entered an integrity fault.");
                }
            }

            StorageBackupFile[] files = EnumerateBackupFiles(stagingRoot)
                .Select(path => DescribeFile(stagingRoot, path))
                .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
                .ToArray();
            var manifest = new StorageBackupManifest(FormatVersion, StorageSchema.CurrentVersion, files);
            byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
            string temporary = destination + "." + operationId + ".tmp";
            try
            {
                using (ZipArchive archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
                {
                    WriteEntry(archive, "manifest.json", manifestBytes);
                    foreach (StorageBackupFile file in files)
                    {
                        ZipArchiveEntry entry = archive.CreateEntry(file.RelativePath, CompressionLevel.Fastest);
                        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                        using Stream input = File.OpenRead(Path.Combine(stagingRoot,
                            file.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
                        using Stream output = entry.Open();
                        input.CopyTo(output);
                    }
                }

                using (var stream = new FileStream(temporary, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, destination, overwrite: false);
            }
            catch
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                throw;
            }

            using Stream backupStream = File.OpenRead(destination);
            byte[] hash = SHA256.HashData(backupStream);
            return new StorageBackupArtifact(
                destination, Convert.ToHexString(hash), new FileInfo(destination).Length, files.Length);
        }
        finally
        {
            DeleteTemporaryDirectory(stagingRoot);
        }
    }

    private static void CopyDatabaseSnapshot(SqliteConnection source, string destination)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = destination,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };
        using var target = new SqliteConnection(builder.ToString());
        target.Open();
        source.BackupDatabase(target);
        using SqliteCommand checkpoint = target.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        checkpoint.ExecuteNonQuery();
    }

    private static void CopyCommittedArtifacts(
        StorageDatabase database,
        string table,
        string source,
        string destination,
        string extension)
    {
        ValidateContainedDirectoryChain(database.Layout.ProgramDirectory, source);
        using SqliteCommand command = database.Connection.CreateCommand();
        command.CommandText = $"SELECT relative_path FROM {table} ORDER BY relative_path;";
        using SqliteDataReader reader = command.ExecuteReader();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            string name = reader.GetString(0);
            if (string.IsNullOrWhiteSpace(name)
                || name != Path.GetFileName(name)
                || !string.Equals(Path.GetExtension(name), extension, StringComparison.OrdinalIgnoreCase)
                || !names.Add(name))
            {
                throw new InvalidDataException("A committed backup artifact path is invalid or duplicated.");
            }
            string path = Path.Combine(source, name);
            if (!File.Exists(path))
            {
                throw new InvalidDataException("A committed backup artifact is missing.");
            }
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("A backup source artifact is a reparse point.");
            }
            File.Copy(path, Path.Combine(destination, name), overwrite: false);
        }
    }

    private static IEnumerable<string> EnumerateBackupFiles(string stagingRoot)
    {
        string database = Path.Combine(stagingRoot, "data", "storage", "store.sqlite3");
        if (!File.Exists(database)) throw new InvalidDataException("Backup snapshot database is missing.");
        yield return database;
        foreach (string folder in new[] { "spool", "chunks" })
        {
            string root = Path.Combine(stagingRoot, "data", "storage", folder);
            foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(path);
                if (name == "storage-service.writer.lock") continue;
                yield return path;
            }
        }
    }

    private static StorageBackupFile DescribeFile(string root, string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("A backup staging artifact is a reparse point.");
        }
        string relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
        using Stream stream = File.OpenRead(path);
        return new StorageBackupFile(relative, stream.Length, Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static StorageBackupManifest ExtractAndVerify(
        string backupPath,
        string stagingRoot,
        StorageRestoreBudgetPolicy budget)
    {
        using ZipArchive archive = ZipFile.OpenRead(backupPath);
        ZipArchiveEntry[] manifests = archive.Entries.Where(entry => entry.FullName == "manifest.json").ToArray();
        if (manifests.Length != 1 || manifests[0].Length is <= 0 or > MaximumManifestBytes)
        {
            throw new InvalidDataException("Backup contains no unique bounded manifest.");
        }
        StorageBackupManifest manifest;
        using (Stream stream = manifests[0].Open())
        {
            manifest = JsonSerializer.Deserialize<StorageBackupManifest>(stream)
                ?? throw new InvalidDataException("Backup manifest is empty.");
        }
        if (manifest.FormatVersion != FormatVersion || !IsSupportedBackupSchema(manifest.SchemaVersion))
        {
            throw new InvalidDataException("Backup format or storage schema version is not supported.");
        }
        if (manifest.Files is null || manifest.Files.Length == 0
            || manifest.Files.Length != manifest.Files.Select(file => file.RelativePath).Distinct(StringComparer.Ordinal).Count())
        {
            throw new InvalidDataException("Backup file directory is empty or duplicated.");
        }
        long total = 0;
        foreach (StorageBackupFile file in manifest.Files)
        {
            ValidateManifestFile(file);
            total = checked(total + file.Length);
        }
        long available = new DriveInfo(Path.GetPathRoot(stagingRoot)!).AvailableFreeSpace;
        ValidateRestoreBudget(
            archive.Entries.Where(entry => entry.FullName != "manifest.json")
                .Select(entry => new StorageBackupBudgetEntry(entry.Length, entry.CompressedLength))
                .ToArray(),
            available,
            budget);
        if (archive.Entries.Count != manifest.Files.Length + 1)
        {
            throw new InvalidDataException("Backup contains undeclared entries.");
        }
        foreach (StorageBackupFile file in manifest.Files)
        {
            ZipArchiveEntry entry = archive.GetEntry(file.RelativePath)
                ?? throw new InvalidDataException("Backup manifest entry is missing.");
            if (entry.Length != file.Length)
            {
                throw new InvalidDataException("Backup entry length differs from its manifest.");
            }
            string destination = ResolveManifestDestination(stagingRoot, file.RelativePath);
            CreateContainedDirectory(stagingRoot,
                Path.GetRelativePath(stagingRoot, Path.GetDirectoryName(destination)!));
            using Stream input = entry.Open();
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                CopyExactly(input, output, file.Length);
                output.Flush(flushToDisk: true);
            }
            using Stream verify = File.OpenRead(destination);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(verify)), file.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Backup entry SHA-256 differs from its manifest.");
            }
        }
        return manifest;
    }

    private static void ValidateManifestFile(StorageBackupFile file)
    {
        if (file.Length < 0 || file.Sha256.Length != 64
            || file.Sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException("Backup manifest file metadata is invalid.");
        }
        _ = ResolveManifestDestination(Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "manifest-validation")), file.RelativePath);
    }

    private static string ResolveManifestDestination(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || Path.IsPathFullyQualified(relative))
        {
            throw new InvalidDataException("Backup entry path is invalid.");
        }
        string[] parts = relative.Split('/');
        bool allowed = parts.Length == 3
            && parts.SequenceEqual(new[] { "data", "storage", "store.sqlite3" })
            || parts.Length == 4
            && parts[0] == "data" && parts[1] == "storage"
            && (parts[2] == "spool" || parts[2] == "chunks")
            && parts[3].Length > 0 && parts[3] == Path.GetFileName(parts[3]);
        if (!allowed || parts.Any(part => part is "." or ".." || string.IsNullOrWhiteSpace(part)))
        {
            throw new InvalidDataException("Backup entry is outside the supported storage file set.");
        }
        string fullRoot = Path.GetFullPath(root);
        string destination = Path.GetFullPath(Path.Combine(fullRoot, Path.Combine(parts)));
        string prefix = fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Backup entry escapes the staging directory.");
        }
        return destination;
    }

    internal static void ValidateRestoreBudgetForTests(
        IReadOnlyList<StorageBackupBudgetEntry> entries,
        long availableBytes) => ValidateRestoreBudget(entries, availableBytes, ProductionBudget);

    private static void ValidateRestoreBudget(
        IReadOnlyList<StorageBackupBudgetEntry> entries,
        long availableBytes,
        StorageRestoreBudgetPolicy budget)
    {
        if (budget.MaximumEntryCount <= 0
            || budget.MaximumSingleEntryBytes <= 0
            || budget.MaximumTotalUncompressedBytes <= 0
            || budget.MaximumCompressionExpansionRatio <= 0
            || budget.CompressionRatioSlackBytes < 0
            || budget.MinimumFreeSpaceBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(budget));
        if (entries.Count is <= 0 || entries.Count > budget.MaximumEntryCount)
            throw new InvalidDataException("Backup entry count exceeds the restore budget.");
        long totalUncompressed = 0;
        long totalCompressed = 0;
        foreach (StorageBackupBudgetEntry entry in entries)
        {
            if (entry.Length < 0 || entry.Length > budget.MaximumSingleEntryBytes
                || entry.CompressedLength < 0)
                throw new InvalidDataException("Backup entry exceeds the size budget.");
            totalUncompressed = checked(totalUncompressed + entry.Length);
            totalCompressed = checked(totalCompressed + entry.CompressedLength);
            if (totalUncompressed > budget.MaximumTotalUncompressedBytes)
                throw new InvalidDataException("Backup exceeds the total uncompressed budget.");
        }
        long lengthBeyondSlack = Math.Max(0, totalUncompressed - budget.CompressionRatioSlackBytes);
        long minimumCompressedLength = lengthBeyondSlack == 0
            ? 0
            : checked((lengthBeyondSlack - 1) / budget.MaximumCompressionExpansionRatio + 1);
        if (totalCompressed < minimumCompressedLength)
            throw new InvalidDataException("Backup exceeds the aggregate compression budget.");
        if (availableBytes <= budget.MinimumFreeSpaceBytes
            || totalUncompressed > availableBytes - budget.MinimumFreeSpaceBytes)
            throw new IOException("Backup would consume the required restore safety reserve.");
    }

    private static RestoreOperationJournal CreateJournal(
        StorageLayout live,
        StorageLayout staging,
        string rollbackRoot,
        string operationId)
    {
        string rollbackStorage = CreateContainedDirectory(live.ProgramDirectory,
            Path.GetRelativePath(live.ProgramDirectory, Path.Combine(rollbackRoot, "storage")));
        var moves = new List<RestoreMove>();
        foreach (string name in new[] { "store.sqlite3", "store.sqlite3-wal", "store.sqlite3-shm", "spool", "chunks" })
        {
            string source = Path.Combine(live.StorageDirectory, name);
            if (!File.Exists(source) && !Directory.Exists(source)) continue;
            moves.Add(new RestoreMove(source, Path.Combine(rollbackStorage, name), Directory.Exists(source), IsOriginal: true));
        }
        foreach (string name in new[] { "store.sqlite3", "spool", "chunks" })
        {
            string source = Path.Combine(staging.StorageDirectory, name);
            moves.Add(new RestoreMove(source, Path.Combine(live.StorageDirectory, name), Directory.Exists(source), IsOriginal: false));
        }
        return new RestoreOperationJournal(1, operationId, staging.ProgramDirectory, rollbackRoot, 0, moves.ToArray());
    }

    private static void ExecuteJournalMoves(
        StorageLayout layout,
        RestoreOperationJournal journal,
        IStorageRestoreFaultInjector faultInjector)
    {
        for (int index = 0; index < journal.Moves.Length; index++)
        {
            RestoreMove move = journal.Moves[index];
            ValidateMove(layout, move, forward: true);
            faultInjector.BeforeMove(move.Source, move.Destination);
            MovePath(move.Source, move.Destination, move.IsDirectory);
            journal = journal with { CompletedMoves = index + 1 };
            WriteJournal(layout, journal, replace: true);
            faultInjector.AfterMove(move.Source, move.Destination);
        }
    }

    private static void WriteJournal(StorageLayout layout, RestoreOperationJournal journal, bool replace)
    {
        ValidateJournal(layout, journal);
        string path = GetJournalPath(layout);
        if (File.Exists(path) != replace)
            throw new InvalidDataException("Restore journal replacement state is invalid.");
        string temporary = path + ".tmp";
        if (File.Exists(temporary)) File.Delete(temporary);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(journal);
        using (var stream = new FileStream(
            temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        if (replace) File.Replace(temporary, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
        else File.Move(temporary, path, overwrite: false);
    }

    private static RestoreOperationJournal ReadJournal(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        if (stream.Length is <= 0 or > MaximumManifestBytes)
            throw new InvalidDataException("Restore journal length is invalid.");
        return JsonSerializer.Deserialize<RestoreOperationJournal>(stream)
            ?? throw new InvalidDataException("Restore journal is empty.");
    }

    private static void ValidateJournal(StorageLayout layout, RestoreOperationJournal journal)
    {
        if (journal.Version != 1
            || !Guid.TryParseExact(journal.OperationId, "N", out Guid operationId)
            || operationId == Guid.Empty
            || journal.Moves is null
            || journal.Moves.Length is < 1 or > 8
            || journal.CompletedMoves < 0
            || journal.CompletedMoves > journal.Moves.Length)
            throw new InvalidDataException("Restore journal identity or move count is invalid.");
        string expectedStaging = Path.Combine(layout.ProgramDirectory, "data", "restore-staging", journal.OperationId);
        string expectedRollback = Path.Combine(layout.ProgramDirectory, "data", "restore-rollback", journal.OperationId);
        if (!PathEquals(journal.StagingRoot, expectedStaging)
            || !PathEquals(journal.RollbackRoot, expectedRollback))
            throw new InvalidDataException("Restore journal roots are not the exact operation directories.");
        var originalSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var originalDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stagedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stagedDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool reachedStagedPhase = false;
        foreach (RestoreMove move in journal.Moves)
        {
            ValidateJournalPath(layout, journal, move);
            if (move.IsOriginal)
            {
                if (reachedStagedPhase
                    || !originalSources.Add(move.Source)
                    || !originalDestinations.Add(move.Destination))
                    throw new InvalidDataException("Restore journal original moves are duplicated or out of phase.");
            }
            else
            {
                reachedStagedPhase = true;
                if (!stagedSources.Add(move.Source)
                    || !stagedDestinations.Add(move.Destination)
                    || !originalSources.Contains(move.Destination))
                    throw new InvalidDataException("Restore journal staged moves do not match the original live topology.");
            }
        }
        if (stagedDestinations.Count != 3)
            throw new InvalidDataException("Restore journal staged move set is incomplete.");
    }

    private static void ValidateJournalPath(
        StorageLayout layout,
        RestoreOperationJournal journal,
        RestoreMove move)
    {
        string name = Path.GetFileName(move.Source);
        string[] liveNames = ["store.sqlite3", "store.sqlite3-wal", "store.sqlite3-shm", "spool", "chunks"];
        string[] stagedNames = ["store.sqlite3", "spool", "chunks"];
        if (move.IsOriginal)
        {
            if (!liveNames.Contains(name, StringComparer.Ordinal)
                || !PathEquals(move.Source, Path.Combine(layout.StorageDirectory, name))
                || !PathEquals(move.Destination, Path.Combine(journal.RollbackRoot, "storage", name)))
                throw new InvalidDataException("Restore journal original move is outside its allowlist.");
        }
        else if (!stagedNames.Contains(name, StringComparer.Ordinal)
            || !PathEquals(move.Source, Path.Combine(journal.StagingRoot, "data", "storage", name))
            || !PathEquals(move.Destination, Path.Combine(layout.StorageDirectory, name)))
        {
            throw new InvalidDataException("Restore journal staged move is outside its allowlist.");
        }
        bool expectedDirectory = name is "spool" or "chunks";
        if (move.IsDirectory != expectedDirectory)
            throw new InvalidDataException("Restore journal object type is invalid.");
    }

    private static string GetJournalPath(StorageLayout layout) =>
        Path.Combine(layout.ProgramDirectory, "data", RestoreJournalName);

    private static bool PathEquals(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static void ValidateExistingFileChain(string root, string file)
    {
        ValidateExistingChain(root, Path.GetDirectoryName(Path.GetFullPath(file))!);
        if (!File.Exists(file) || (File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Restore journal is missing or a reparse point.");
    }

    private static void RollBackJournal(StorageLayout layout, RestoreOperationJournal journal)
    {
        ValidateJournal(layout, journal);
        int completed = ResolveCompletedMoveCount(journal);
        for (int index = completed - 1; index >= 0; index--)
            RollBackMove(layout, journal.Moves[index]);
    }

    private static int ResolveCompletedMoveCount(RestoreOperationJournal journal)
    {
        int completed = journal.CompletedMoves;
        if (completed == journal.Moves.Length) return completed;
        RestoreMove candidate = journal.Moves[completed];
        bool sourceExists = PathExists(candidate.Source, candidate.IsDirectory);
        bool destinationExists = PathExists(candidate.Destination, candidate.IsDirectory);
        if (sourceExists && !destinationExists) return completed;
        if (!sourceExists && destinationExists) return completed + 1;
        throw new InvalidDataException("Restore journal in-flight move state is ambiguous.");
    }

    private static void RollBackMove(StorageLayout layout, RestoreMove move)
    {
        bool sourceExists = PathExists(move.Source, move.IsDirectory);
        bool destinationExists = PathExists(move.Destination, move.IsDirectory);
        if (sourceExists && !destinationExists) return;
        if (!sourceExists && destinationExists)
        {
            ValidateMove(layout, move, forward: false);
            MovePath(move.Destination, move.Source, move.IsDirectory);
            return;
        }
        throw new InvalidDataException("Restore journal move state is ambiguous.");
    }

    private static void ValidateMove(StorageLayout layout, RestoreMove move, bool forward)
    {
        string source = forward ? move.Source : move.Destination;
        string destination = forward ? move.Destination : move.Source;
        ValidateContainedObject(layout.ProgramDirectory, source, move.IsDirectory, mustExist: true);
        ValidateContainedObject(layout.ProgramDirectory, destination, move.IsDirectory, mustExist: false);
    }

    private static void ValidateContainedObject(
        string programRoot, string path, bool isDirectory, bool mustExist)
    {
        string root = Path.GetFullPath(programRoot);
        string full = Path.GetFullPath(path);
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Restore move path escapes the program directory.");
        ValidateExistingChain(root, Path.GetDirectoryName(full)!);
        bool exists = isDirectory ? Directory.Exists(full) : File.Exists(full);
        bool wrongType = isDirectory ? File.Exists(full) : Directory.Exists(full);
        if (wrongType || exists != mustExist)
            throw new InvalidDataException("Restore move object has an unexpected type or existence state.");
        if (exists && (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Restore move object is a reparse point.");
    }

    private static bool PathExists(string path, bool isDirectory) =>
        isDirectory ? Directory.Exists(path) : File.Exists(path);

    private static void MovePath(string source, string destination, bool isDirectory)
    {
        if (isDirectory) Directory.Move(source, destination);
        else File.Move(source, destination);
    }

    private static string ResolveNewBackup(StorageLayout layout, string relativePath)
    {
        string path = ResolveBackupPath(layout, relativePath);
        string parent = Path.GetDirectoryName(path)!;
        CreateContainedDirectory(layout.ProgramDirectory, Path.GetRelativePath(layout.ProgramDirectory, parent));
        if (File.Exists(path) || Directory.Exists(path)) throw new IOException("Backup destination already exists.");
        return path;
    }

    private static string ResolveExistingBackup(StorageLayout layout, string relativePath)
    {
        string path = ResolveBackupPath(layout, relativePath);
        ValidateExistingChain(layout.ProgramDirectory, Path.GetDirectoryName(path)!);
        if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Backup is missing or is a reparse point.");
        }
        return path;
    }

    private static string ResolveBackupPath(StorageLayout layout, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathFullyQualified(relativePath)
            || !string.Equals(Path.GetExtension(relativePath), ".itbackup", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Backup path must be a relative .itbackup file.", nameof(relativePath));
        }
        string root = Path.GetFullPath(layout.ProgramDirectory);
        string path = Path.GetFullPath(Path.Combine(root, relativePath));
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Backup path escapes the program directory.");
        }
        return path;
    }

    private static string CreateContainedDirectory(string programRoot, string relative)
    {
        string root = Path.GetFullPath(programRoot);
        string target = Path.GetFullPath(Path.Combine(root, relative));
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Directory escapes the program directory.");
        }
        if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Program directory is missing or a reparse point.");
        }
        string current = root;
        foreach (string component in Path.GetRelativePath(root, target).Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (File.Exists(current)) throw new InvalidDataException("Directory chain contains a file.");
            if (!Directory.Exists(current)) Directory.CreateDirectory(current);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Directory chain contains a reparse point.");
            }
        }
        return target;
    }

    private static void ValidateExistingChain(string root, string target)
    {
        string current = Path.GetFullPath(root);
        foreach (string component in Path.GetRelativePath(current, Path.GetFullPath(target)).Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Directory.Exists(current) || (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Backup directory chain is missing or contains a reparse point.");
            current = Path.Combine(current, component);
        }
        if (!Directory.Exists(current) || (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Backup directory chain is missing or contains a reparse point.");
    }

    internal static void ValidateContainedDirectoryForTests(string programRoot, string path) =>
        ValidateContainedDirectoryChain(programRoot, path);

    private static void ValidateContainedDirectoryChain(string programRoot, string path)
    {
        string root = Path.GetFullPath(programRoot);
        string target = Path.GetFullPath(path);
        string prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Backup source directory escapes the program directory.");
        string current = root;
        foreach (string component in Path.GetRelativePath(root, target).Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Directory.Exists(current) || (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Backup source directory chain is missing or contains a reparse point.");
            current = Path.Combine(current, component);
        }
        if (!Directory.Exists(current) || (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Backup source directory chain is missing or contains a reparse point.");
    }

    private static void CopyExactly(Stream input, Stream output, long length)
    {
        byte[] buffer = new byte[81920];
        long remaining = length;
        while (remaining > 0)
        {
            int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
            if (read == 0) throw new InvalidDataException("Backup entry ended before its declared length.");
            output.Write(buffer, 0, read);
            remaining -= read;
        }
        if (input.ReadByte() != -1)
            throw new InvalidDataException("Backup entry exceeds its declared length.");
    }

    private static void WriteEntry(ZipArchive archive, string name, byte[] bytes)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Fastest);
        entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
        using Stream stream = entry.Open();
        stream.Write(bytes);
    }

    internal static void DeleteTemporaryDirectoryForTests(string path) => DeleteTemporaryDirectory(path);

    private static void DeleteTemporaryDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.Count != 0)
        {
            string entry = pending.Pop();
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Temporary cleanup refuses a reparse point.");
            }
            if (Directory.Exists(entry))
            {
                foreach (string child in Directory.EnumerateFileSystemEntries(entry, "*", SearchOption.TopDirectoryOnly))
                {
                    pending.Push(child);
                }
            }
        }
        Directory.Delete(path, recursive: true);
    }

    private static bool IsSupportedBackupSchema(int version) =>
        version == 6 || version == StorageSchema.CurrentVersion;

    private sealed record StorageBackupManifest(
        int FormatVersion,
        int SchemaVersion,
        StorageBackupFile[] Files);

    private sealed record StorageBackupFile(string RelativePath, long Length, string Sha256);

    private sealed record RestoreOperationJournal(
        int Version,
        string OperationId,
        string StagingRoot,
        string RollbackRoot,
        int CompletedMoves,
        RestoreMove[] Moves);

    private sealed record RestoreMove(
        string Source,
        string Destination,
        bool IsDirectory,
        bool IsOriginal);
}
