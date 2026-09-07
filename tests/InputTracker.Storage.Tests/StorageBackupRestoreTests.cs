using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace InputTracker.Storage.Tests;

public sealed class StorageBackupRestoreTests
{
    [Fact]
    public void BackupAndRestoreRoundTripsCommittedDataAndCreatesPreRestoreBackup()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid keyboard = Guid.NewGuid();
        SeedKeyboard(layout, keyboard, 2);

        StorageBackupArtifact backup;
        using (StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock()))
        {
            backup = service.CreateBackup("backups/known.itbackup");
        }

        SeedKeyboard(layout, keyboard, 3);
        StorageRestoreResult restored = StorageBackup.Restore(layout, "backups/known.itbackup");

        Assert.True(File.Exists(backup.Path));
        Assert.True(File.Exists(restored.PreRestoreBackup.Path));
        Assert.StartsWith(Path.Combine(directory.Path, "backups", "automatic"), restored.PreRestoreBackup.Path);
        using StorageService query = StorageService.OpenForTests(layout, new ControllableStorageClock());
        StorageKeyboardPeriodAggregate row = Assert.Single(query.QueryKeyboardPeriod(
            StoragePeriodKind.Day, new DateOnly(2026, 8, 13), "UTC", keyboard).Aggregates);
        Assert.Equal(2, row.PressCount);
    }

    [Fact]
    public void CorruptBackupFailsClosedWithoutChangingLiveStore()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid keyboard = Guid.NewGuid();
        SeedKeyboard(layout, keyboard, 2);
        using (StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock()))
        {
            service.CreateBackup("backups/corrupt.itbackup");
        }

        using (ZipArchive archive = ZipFile.Open(
            Path.Combine(directory.Path, "backups", "corrupt.itbackup"), ZipArchiveMode.Update))
        {
            ZipArchiveEntry database = archive.GetEntry("data/storage/store.sqlite3")!;
            using Stream stream = database.Open();
            stream.Position = 100;
            int value = stream.ReadByte();
            stream.Position = 100;
            stream.WriteByte((byte)(value ^ 1));
        }

        Assert.Throws<InvalidDataException>(() =>
            StorageBackup.Restore(layout, "backups/corrupt.itbackup"));

        using StorageService query = StorageService.OpenForTests(layout, new ControllableStorageClock());
        Assert.Equal(2, Assert.Single(query.QueryKeyboardPeriod(
            StoragePeriodKind.Day, new DateOnly(2026, 8, 13), "UTC", keyboard).Aggregates).PressCount);
    }

    [Theory]
    [InlineData("../outside.itbackup")]
    [InlineData("C:/outside.itbackup")]
    [InlineData("")]
    public void BackupAndRestoreRejectPathEscape(string relativePath)
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        using StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock());
        Assert.ThrowsAny<Exception>(() => service.CreateBackup(relativePath));
        Assert.ThrowsAny<Exception>(() => StorageBackup.Restore(layout, relativePath));
    }

    [Fact]
    public void IncompatibleManifestVersionFailsBeforeCreatingPreRestoreBackup()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        string backupPath = Path.Combine(directory.Path, "backups", "future.itbackup");
        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
        using (ZipArchive archive = ZipFile.Open(backupPath, ZipArchiveMode.Create))
        {
            ZipArchiveEntry manifest = archive.CreateEntry("manifest.json");
            using var writer = new StreamWriter(manifest.Open());
            writer.Write("{\"formatVersion\":2,\"schemaVersion\":4,\"files\":[]}");
        }

        Assert.Throws<InvalidDataException>(() => StorageBackup.Restore(layout, "backups/future.itbackup"));
        Assert.Empty(Directory.Exists(Path.Combine(directory.Path, "backups", "automatic"))
            ? Directory.EnumerateFiles(Path.Combine(directory.Path, "backups", "automatic"))
            : []);
    }

    [Fact]
    public void RestoreRejectsActiveStorageOwnerWithoutCreatingAutomaticBackup()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        using (StorageService source = StorageService.OpenForTests(layout, new ControllableStorageClock()))
        {
            source.CreateBackup("backups/active.itbackup");
        }
        using StorageService active = StorageService.OpenForTests(layout, new ControllableStorageClock());

        Assert.Throws<IOException>(() => StorageBackup.Restore(layout, "backups/active.itbackup"));

        Assert.Empty(Directory.Exists(Path.Combine(directory.Path, "backups", "automatic"))
            ? Directory.EnumerateFiles(Path.Combine(directory.Path, "backups", "automatic"))
            : []);
    }

    [Fact]
    public void BackupAndRestoreRoundTripsMouseButtonAggregatesWithoutTrajectoryFiles()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid mouse = Guid.NewGuid();
        var clock = new ControllableStorageClock { Timestamp = 0 };
        TimeCalibrationSegment calibration = TimeCalibrationSegment.Capture(Guid.NewGuid(), clock);
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock()))
        {
            database.RegisterDevice(mouse, (int)StorageDeviceKind.Mouse, 0, 1);
        }
        using (StorageService service = StorageService.OpenForTests(layout, clock))
        {
            service.Start(calibration);
            service.WriteMouseButtonTransition(new StorageMouseButtonTransition(
                mouse, calibration.SessionId, 1, 0x0005));
            service.Stop(2);
            service.CreateBackup("backups/mouse.itbackup");
        }
        IReadOnlyDictionary<string, string> committedFilesBeforeRestore = SnapshotCommittedMouseFiles(layout);

        File.WriteAllText(Path.Combine(layout.StorageDirectory, "outside-uncommitted.txt"), "not in backup");
        StorageBackup.Restore(layout, "backups/mouse.itbackup");

        using StorageService restored = StorageService.OpenForTests(layout, clock);
        StorageMouseButtonPeriodResult result = restored.QueryMouseButtonsPeriod(
            StoragePeriodKind.Day,
            DateOnly.FromDateTime(clock.UtcNow.UtcDateTime),
            TimeZoneInfo.Utc.Id,
            mouse);
        Assert.Equal(2, result.Aggregates.Sum(row => row.PressCount));
        Assert.True(File.Exists(Path.Combine(layout.StorageDirectory, "outside-uncommitted.txt")));
        Assert.Equal(committedFilesBeforeRestore, SnapshotCommittedMouseFiles(layout));
        Assert.Empty(committedFilesBeforeRestore);
    }

    [Fact]
    public void BackupRejectsJunctionArtifactDirectoryBeforeReadingExternalFiles()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        string external = Path.Combine(directory.Path, "external-spool");
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "secret.txt"), "must not be copied");
        using StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock());
        try
        {
            Directory.Delete(layout.SpoolDirectory);
            CreateJunction(layout.SpoolDirectory, external);
            Assert.Throws<InvalidDataException>(() => service.CreateBackup("backups/junction.itbackup"));
            Assert.False(File.Exists(Path.Combine(directory.Path, "backups", "junction.itbackup")));
            Assert.Equal(["secret.txt"], Directory.GetFiles(external).Select(Path.GetFileName));
        }
        finally
        {
            if (Directory.Exists(layout.SpoolDirectory)) Directory.Delete(layout.SpoolDirectory);
        }
    }

    [Fact]
    public void TemporaryCleanupRejectsJunctionAndLeavesExternalContentUntouched()
    {
        using var directory = new TemporaryDirectory();
        string operation = Path.Combine(directory.Path, "operation");
        string external = Path.Combine(directory.Path, "external");
        Directory.CreateDirectory(operation);
        Directory.CreateDirectory(external);
        File.WriteAllText(Path.Combine(external, "keep.txt"), "keep");
        CreateJunction(Path.Combine(operation, "linked"), external);
        try
        {
            Assert.Throws<InvalidDataException>(() => StorageBackup.DeleteTemporaryDirectoryForTests(operation));
            Assert.True(File.Exists(Path.Combine(external, "keep.txt")));
            Assert.True(Directory.Exists(operation));
        }
        finally
        {
            Directory.Delete(Path.Combine(operation, "linked"));
            Directory.Delete(operation);
        }
    }

    [Fact]
    public void BackupSourceValidationRejectsJunctionInUpperDirectoryChain()
    {
        using var program = new TemporaryDirectory();
        using var external = new TemporaryDirectory();
        string dataJunction = Path.Combine(program.Path, "data");
        string externalStorage = Path.Combine(external.Path, "storage", "spool");
        Directory.CreateDirectory(externalStorage);
        CreateJunction(dataJunction, external.Path);
        try
        {
            Assert.Throws<InvalidDataException>(() => StorageBackup.ValidateContainedDirectoryForTests(
                program.Path, Path.Combine(dataJunction, "storage", "spool")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(externalStorage));
        }
        finally
        {
            Directory.Delete(dataJunction);
        }
    }

    [Fact]
    public void RestoreBudgetRejectsEntryCountSingleTotalAndCompressionBombBoundaries()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid keyboard = Guid.NewGuid();
        SeedKeyboard(layout, keyboard, 3);
        StorageBackup.ValidateRestoreBudgetForTests(
            [new StorageBackupBudgetEntry(1, 1)], StorageBackup.MinimumFreeSpaceBytes + 1);

        Assert.Throws<InvalidDataException>(() => StorageBackup.ValidateRestoreBudgetForTests(
            Enumerable.Repeat(new StorageBackupBudgetEntry(1, 1), StorageBackup.MaximumEntryCount + 1).ToArray(),
            long.MaxValue));
        Assert.Throws<InvalidDataException>(() => StorageBackup.ValidateRestoreBudgetForTests(
            [new StorageBackupBudgetEntry(StorageBackup.MaximumSingleEntryBytes + 1, 1)], long.MaxValue));
        Assert.Throws<InvalidDataException>(() => StorageBackup.ValidateRestoreBudgetForTests(
            [new StorageBackupBudgetEntry(StorageBackup.MaximumTotalUncompressedBytes / 2 + 1, 1),
             new StorageBackupBudgetEntry(StorageBackup.MaximumTotalUncompressedBytes / 2 + 1, 1)],
            long.MaxValue));
        Assert.Throws<InvalidDataException>(() => StorageBackup.ValidateRestoreBudgetForTests(
            [new StorageBackupBudgetEntry(
                StorageBackup.MaximumCompressionExpansionRatio + StorageBackup.CompressionRatioSlackBytes + 1,
                1)],
            long.MaxValue));
        Assert.Throws<IOException>(() => StorageBackup.ValidateRestoreBudgetForTests(
            [new StorageBackupBudgetEntry(1, 1)], StorageBackup.MinimumFreeSpaceBytes));

        using StorageService unchanged = StorageService.OpenForTests(layout, new ControllableStorageClock());
        Assert.Equal(3, Assert.Single(unchanged.QueryKeyboardPeriod(
            StoragePeriodKind.Day, new DateOnly(2026, 8, 13), "UTC", keyboard).Aggregates).PressCount);
        string automatic = Path.Combine(layout.ProgramDirectory, "backups", "automatic");
        Assert.False(Directory.Exists(automatic) && Directory.EnumerateFiles(automatic).Any());
    }

    [Fact]
    public void AggregateCompressionBombFailsPublicRestorePathBeforeLiveOrAutomaticBackupChanges()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid keyboard = Guid.NewGuid();
        SeedKeyboard(layout, keyboard, 3);
        string liveHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(layout.DatabasePath)));
        string backup = Path.Combine(layout.ProgramDirectory, "backups", "aggregate-bomb.itbackup");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        byte[] repeated = new byte[1024 * 1024];
        var declared = new List<object>();
        using (ZipArchive archive = ZipFile.Open(backup, ZipArchiveMode.Create))
        {
            foreach (string name in new[] { "one.mchunk", "two.mchunk" })
            {
                string relative = $"data/storage/chunks/{name}";
                ZipArchiveEntry entry = archive.CreateEntry(relative, CompressionLevel.SmallestSize);
                using (Stream output = entry.Open()) output.Write(repeated);
                declared.Add(new
                {
                    RelativePath = relative,
                    Length = (long)repeated.Length,
                    Sha256 = Convert.ToHexString(SHA256.HashData(repeated))
                });
            }
            ZipArchiveEntry manifest = archive.CreateEntry("manifest.json", CompressionLevel.NoCompression);
            using Stream manifestOutput = manifest.Open();
            JsonSerializer.Serialize(manifestOutput, new
            {
                FormatVersion = 1,
                SchemaVersion = StorageSchema.CurrentVersion,
                Files = declared
            });
        }
        var testBudget = new StorageRestoreBudgetPolicy(
            10,
            2L * 1024 * 1024,
            4L * 1024 * 1024,
            100,
            1024 * 1024,
            0);

        Assert.Throws<InvalidDataException>(() => StorageBackup.RestoreForTests(
            layout, "backups/aggregate-bomb.itbackup", NoStorageRestoreFaultInjector.Instance, testBudget));

        Assert.Equal(liveHash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(layout.DatabasePath))));
        using StorageService unchanged = StorageService.OpenForTests(layout, new ControllableStorageClock());
        Assert.Equal(3, Assert.Single(unchanged.QueryKeyboardPeriod(
            StoragePeriodKind.Day, new DateOnly(2026, 8, 13), "UTC", keyboard).Aggregates).PressCount);
        string automatic = Path.Combine(layout.ProgramDirectory, "backups", "automatic");
        Assert.False(Directory.Exists(automatic) && Directory.EnumerateFiles(automatic).Any());
        string staging = Path.Combine(layout.ProgramDirectory, "data", "restore-staging");
        Assert.False(Directory.Exists(staging) && Directory.EnumerateFileSystemEntries(staging).Any());
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 1)]
    [InlineData(true, 2)]
    [InlineData(false, 2)]
    [InlineData(true, 3)]
    [InlineData(false, 3)]
    [InlineData(true, 4)]
    [InlineData(false, 4)]
    [InlineData(true, 5)]
    [InlineData(false, 5)]
    [InlineData(true, 6)]
    [InlineData(false, 6)]
    public void EveryStoreMoveFaultRestoresOriginalLiveStore(bool beforeMove, int ordinal)
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid keyboard = Guid.NewGuid();
        SeedKeyboard(layout, keyboard, 2);
        using (StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock()))
        {
            service.CreateBackup("backups/old.itbackup");
        }
        SeedKeyboard(layout, keyboard, 5);
        var faults = new MoveFaultInjector(beforeMove, ordinal);

        Assert.Throws<IOException>(() => StorageBackup.RestoreForTests(
            layout, "backups/old.itbackup", faults));

        using StorageService reopened = StorageService.OpenForTests(layout, new ControllableStorageClock());
        Assert.Equal(7, Assert.Single(reopened.QueryKeyboardPeriod(
            StoragePeriodKind.Day, new DateOnly(2026, 8, 13), "UTC", keyboard).Aggregates).PressCount);
        Assert.False(File.Exists(Path.Combine(layout.ProgramDirectory, "data", "restore-journal.json")));
    }

    [Fact]
    public void NewStorageServiceOwnerRecoversJournalLeftBySimulatedProcessTermination()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid keyboard = Guid.NewGuid();
        SeedKeyboard(layout, keyboard, 2);
        using (StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock()))
        {
            service.CreateBackup("backups/old.itbackup");
        }
        SeedKeyboard(layout, keyboard, 5);

        Assert.Throws<IOException>(() => StorageBackup.RestoreForTests(
            layout, "backups/old.itbackup", new ProcessTerminationFaultInjector(4)));
        Assert.True(File.Exists(Path.Combine(layout.ProgramDirectory, "data", "restore-journal.json")));

        using StorageService reopened = StorageService.OpenForTests(layout, new ControllableStorageClock());
        Assert.Equal(7, Assert.Single(reopened.QueryKeyboardPeriod(
            StoragePeriodKind.Day, new DateOnly(2026, 8, 13), "UTC", keyboard).Aggregates).PressCount);
        Assert.False(File.Exists(Path.Combine(layout.ProgramDirectory, "data", "restore-journal.json")));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(layout.ProgramDirectory, "data", "restore-staging")));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(layout.ProgramDirectory, "data", "restore-rollback")));
    }

    private static void SeedKeyboard(StorageLayout layout, Guid keyboard, long count)
    {
        using StorageDatabase database = StorageDatabase.Open(layout, new FakeClock());
        if (!database.ListDevices().Any(device => device.DeviceId == keyboard))
        {
            database.RegisterDevice(keyboard, (int)StorageDeviceKind.Keyboard, 0, 1);
        }
        database.UpsertKeyboardDaily(
            keyboard, 30, false, false, new DateOnly(2026, 8, 13), "UTC", count, count * 10);
    }

    private static IReadOnlyDictionary<string, string> SnapshotCommittedMouseFiles(StorageLayout layout)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string root in new[] { layout.SpoolDirectory, layout.ChunkDirectory })
        {
            foreach (string path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(layout.StorageDirectory, path).Replace('\\', '/');
                result[relative] = $"{new FileInfo(path).Length}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}";
            }
        }
        return result;
    }

    private static void CreateJunction(string junction, string target)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
            Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
            $"/d /c mklink /J \"{junction}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
    }

    private sealed class MoveFaultInjector(bool beforeMove, int targetOrdinal) : IStorageRestoreFaultInjector
    {
        private int _ordinal;

        public bool LeavesJournalForRecovery => false;

        public void BeforeMove(string source, string destination)
        {
            int ordinal = ++_ordinal;
            if (beforeMove && ordinal == targetOrdinal) throw new IOException("Injected before move.");
        }

        public void AfterMove(string source, string destination)
        {
            if (!beforeMove && _ordinal == targetOrdinal) throw new IOException("Injected after move.");
        }
    }

    private sealed class ProcessTerminationFaultInjector(int targetOrdinal) : IStorageRestoreFaultInjector
    {
        private int _ordinal;
        public bool LeavesJournalForRecovery => true;
        public void BeforeMove(string source, string destination) => _ordinal++;
        public void AfterMove(string source, string destination)
        {
            if (_ordinal == targetOrdinal) throw new IOException("Injected process termination boundary.");
        }
    }
}
