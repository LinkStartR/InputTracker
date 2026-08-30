using System.Reflection;

namespace InputTracker.Storage.Tests;

public sealed class StorageOfflineRestoreTests
{
    [Fact]
    public void PublicSurfaceHasOneNarrowOfflineRestoreEntryPointWithoutFaultInjection()
    {
        Assembly storageAssembly = typeof(StorageLayout).Assembly;
        string[] publicRestoreMethods = storageAssembly.ExportedTypes
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(method => method.Name.Contains("Restore", StringComparison.Ordinal))
            .Select(method => $"{method.DeclaringType!.Name}.{method.Name}(" + string.Join(",",
                method.GetParameters().Select(parameter => parameter.ParameterType.Name)) + ")")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["StorageOfflineRestore.RestoreOffline(StorageLayout,String)"],
            publicRestoreMethods);
        Assert.True(storageAssembly.GetType("InputTracker.Storage.StorageBackup")!.IsNotPublic);
        Assert.DoesNotContain(storageAssembly.ExportedTypes,
            type => type.Name.Contains("FaultInjector", StringComparison.Ordinal));
    }

    [Fact]
    public void ActiveStorageOwnerFailsClosedWithoutAutomaticBackupOrJournal()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        using (StorageService source = StorageService.OpenForTests(layout, new ControllableStorageClock()))
        {
            source.CreateBackup("backups/active.itbackup");
        }
        using StorageService active = StorageService.OpenForTests(layout, new ControllableStorageClock());

        Assert.Throws<IOException>(() =>
            StorageOfflineRestore.RestoreOffline(layout, "backups/active.itbackup"));

        string automaticDirectory = Path.Combine(directory.Path, "backups", "automatic");
        Assert.False(Directory.Exists(automaticDirectory)
            && Directory.EnumerateFiles(automaticDirectory, "*.itbackup").Any());
        Assert.False(File.Exists(Path.Combine(directory.Path, "data", "restore-journal.json")));
    }

    [Fact]
    public void DisposableStoresRoundTripBOverAThenAutomaticBackupRestoresAIntoC()
    {
        using var liveDirectory = new TemporaryDirectory();
        using var sourceDirectory = new TemporaryDirectory();
        using var recoveryDirectory = new TemporaryDirectory();
        StorageLayout liveLayout = StorageLayout.Create(liveDirectory.Path);
        StorageLayout sourceLayout = StorageLayout.Create(sourceDirectory.Path);
        StorageLayout recoveryLayout = StorageLayout.Create(recoveryDirectory.Path);
        Guid keyboard = Guid.NewGuid();

        SeedKeyboard(liveLayout, keyboard, 2);
        SeedKeyboard(sourceLayout, keyboard, 7);
        StorageBackupArtifact stateB;
        using (StorageService source = StorageService.OpenForTests(sourceLayout, new ControllableStorageClock()))
        {
            stateB = source.CreateBackup("backups/state-b.itbackup");
        }

        string importedBRelative = Path.Combine("backups", "imported", "state-b.itbackup");
        string importedB = Path.Combine(liveLayout.ProgramDirectory, importedBRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(importedB)!);
        File.Copy(stateB.Path, importedB, overwrite: false);

        StorageRestoreResult restoredB = StorageOfflineRestore.RestoreOffline(liveLayout, importedBRelative);

        AssertKeyboardCount(liveLayout, keyboard, 7);
        Assert.True(File.Exists(restoredB.PreRestoreBackup.Path));
        Assert.StartsWith(
            Path.Combine(liveLayout.ProgramDirectory, "backups", "automatic") + Path.DirectorySeparatorChar,
            restoredB.PreRestoreBackup.Path,
            StringComparison.OrdinalIgnoreCase);

        SeedKeyboard(recoveryLayout, keyboard, 11);
        string importedARelative = Path.Combine("backups", "imported", "state-a.itbackup");
        string importedA = Path.Combine(recoveryLayout.ProgramDirectory, importedARelative);
        Directory.CreateDirectory(Path.GetDirectoryName(importedA)!);
        File.Copy(restoredB.PreRestoreBackup.Path, importedA, overwrite: false);

        StorageOfflineRestore.RestoreOffline(recoveryLayout, importedARelative);

        AssertKeyboardCount(recoveryLayout, keyboard, 2);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../outside.itbackup")]
    [InlineData("backups/not-a-backup.zip")]
    [InlineData("C:/outside.itbackup")]
    public void FacadeRejectsNonRelativeContainedBackupPath(string relativePath)
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);

        Assert.ThrowsAny<Exception>(() =>
            StorageOfflineRestore.RestoreOffline(layout, relativePath));
    }

    [Fact]
    public void FacadeRejectsBackupBehindDirectoryJunction()
    {
        using var liveDirectory = new TemporaryDirectory();
        using var externalDirectory = new TemporaryDirectory();
        StorageLayout liveLayout = StorageLayout.Create(liveDirectory.Path);
        StorageLayout externalLayout = StorageLayout.Create(externalDirectory.Path);
        using (StorageService source = StorageService.OpenForTests(externalLayout, new ControllableStorageClock()))
        {
            source.CreateBackup("backups/external.itbackup");
        }

        string junction = Path.Combine(liveLayout.ProgramDirectory, "linked-backups");
        CreateJunction(junction, Path.Combine(externalLayout.ProgramDirectory, "backups"));
        try
        {
            Assert.Throws<InvalidDataException>(() =>
                StorageOfflineRestore.RestoreOffline(liveLayout, "linked-backups/external.itbackup"));
        }
        finally
        {
            if (Directory.Exists(junction)) Directory.Delete(junction);
        }
    }

    [Fact]
    public void FacadeRejectsForgedLayoutWithoutWritingOutsideProgramDirectory()
    {
        using var liveDirectory = new TemporaryDirectory();
        using var externalDirectory = new TemporaryDirectory();
        StorageLayout liveLayout = StorageLayout.Create(liveDirectory.Path);
        using (StorageService source = StorageService.OpenForTests(liveLayout, new ControllableStorageClock()))
        {
            source.CreateBackup("backups/known.itbackup");
        }
        string forgedStorage = Path.Combine(externalDirectory.Path, "forged-storage");
        var forgedLayout = new StorageLayout(
            liveLayout.ProgramDirectory,
            forgedStorage,
            Path.Combine(forgedStorage, "store.sqlite3"),
            Path.Combine(forgedStorage, "spool"),
            Path.Combine(forgedStorage, "chunks"));

        Assert.Throws<InvalidDataException>(() =>
            StorageOfflineRestore.RestoreOffline(forgedLayout, "backups/known.itbackup"));

        Assert.Empty(Directory.EnumerateFileSystemEntries(externalDirectory.Path));
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

    private static void AssertKeyboardCount(StorageLayout layout, Guid keyboard, long expected)
    {
        using StorageService query = StorageService.OpenForTests(layout, new ControllableStorageClock());
        StorageKeyboardPeriodAggregate row = Assert.Single(query.QueryKeyboardPeriod(
            StoragePeriodKind.Day, new DateOnly(2026, 8, 13), "UTC", keyboard).Aggregates);
        Assert.Equal((expected, expected * 10), (row.PressCount, row.HoldDurationMicroseconds));
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
}
