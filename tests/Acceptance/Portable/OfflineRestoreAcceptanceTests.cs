using System.Reflection;
using InputTracker.AcceptanceTests.Bootstrap;
using InputTracker.AcceptanceTests.Storage;
using InputTracker.Storage;

namespace InputTracker.AcceptanceTests.Portable;

public sealed class OfflineRestoreAcceptanceTests
{
    [Fact]
    public void StoragePublicSurfaceHasOneOfflineEntryAndNoFaultSeam()
    {
        Assembly assembly = typeof(StorageLayout).Assembly;
        string[] restoreMethods = assembly.ExportedTypes
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(method => method.Name.Contains("Restore", StringComparison.Ordinal))
            .Select(method => $"{method.DeclaringType!.Name}.{method.Name}(" + string.Join(",",
                method.GetParameters().Select(parameter => parameter.ParameterType.Name)) + ")")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["StorageOfflineRestore.RestoreOffline(StorageLayout,String)"], restoreMethods);
        Assert.DoesNotContain(assembly.ExportedTypes,
            type => type.Name.Contains("FaultInjector", StringComparison.Ordinal));
    }

    [Fact]
    public void ActiveOwnerRejectsOfflineRestoreBeforeJournalOrAutomaticBackup()
    {
        using var directory = new AcceptanceTemporaryDirectory("offline restore active owner");
        StorageLayout layout = StorageLayout.Create(directory.Path);
        SeedKeyboard(layout, PressCountA);
        using (StorageService backupOwner = StorageService.Open(layout))
        {
            backupOwner.CreateBackup("backups/active.itbackup");
        }
        using StorageService activeOwner = StorageService.Open(layout);

        Assert.Throws<IOException>(() =>
            StorageOfflineRestore.RestoreOffline(layout, "backups/active.itbackup"));

        Assert.False(File.Exists(Path.Combine(directory.Path, "data", "restore-journal.json")));
        string automatic = Path.Combine(directory.Path, "backups", "automatic");
        Assert.False(Directory.Exists(automatic) && Directory.EnumerateFileSystemEntries(automatic).Any());
    }

    [Fact]
    public void DisposableAtoBRestoreAndAutomaticBackupRoundTripExactly()
    {
        using var live = new AcceptanceTemporaryDirectory("offline restore live A");
        using var source = new AcceptanceTemporaryDirectory("offline restore source B");
        using var recovery = new AcceptanceTemporaryDirectory("offline restore recovery C");
        StorageLayout liveLayout = StorageLayout.Create(live.Path);
        StorageLayout sourceLayout = StorageLayout.Create(source.Path);
        StorageLayout recoveryLayout = StorageLayout.Create(recovery.Path);

        SeedKeyboard(liveLayout, PressCountA);
        SeedKeyboard(sourceLayout, PressCountB);
        StorageBackupArtifact backupB;
        using (StorageService owner = StorageService.Open(sourceLayout))
        {
            backupB = owner.CreateBackup("backups/state-b.itbackup");
        }
        string importedB = ImportBackup(backupB.Path, liveLayout, "backups/imported/state-b.itbackup");

        StorageRestoreResult restored = StorageOfflineRestore.RestoreOffline(
            liveLayout, Path.GetRelativePath(liveLayout.ProgramDirectory, importedB));

        AssertKeyboard(liveLayout, PressCountB);
        Assert.True(File.Exists(restored.PreRestoreBackup.Path));
        Assert.StartsWith(Path.Combine(live.Path, "backups", "automatic") + Path.DirectorySeparatorChar,
            restored.PreRestoreBackup.Path, StringComparison.OrdinalIgnoreCase);

        SeedKeyboard(recoveryLayout, PressCountC);
        string importedA = ImportBackup(
            restored.PreRestoreBackup.Path, recoveryLayout, "backups/imported/state-a.itbackup");
        StorageOfflineRestore.RestoreOffline(
            recoveryLayout, Path.GetRelativePath(recoveryLayout.ProgramDirectory, importedA));

        AssertKeyboard(recoveryLayout, PressCountA);
    }

    [Fact]
    public void RelativePathBoundaryRejectsEscapeAbsoluteWrongExtensionAndMissingFile()
    {
        using var directory = new AcceptanceTemporaryDirectory("offline restore path boundary live");
        using var external = new AcceptanceTemporaryDirectory("offline restore path boundary external");
        StorageLayout layout = StorageLayout.Create(directory.Path);
        string outside = Path.Combine(external.Path, "outside.itbackup");
        File.WriteAllText(outside, "external sentinel");
        string before = SnapshotDirectory(external.Path);
        string[] invalidPaths =
        [
            Path.GetRelativePath(layout.ProgramDirectory, outside),
            outside,
            "backups/not-backup.zip",
            "backups/missing.itbackup"
        ];

        foreach (string invalidPath in invalidPaths)
        {
            Assert.ThrowsAny<Exception>(() => StorageOfflineRestore.RestoreOffline(layout, invalidPath));
        }

        Assert.Equal(before, SnapshotDirectory(external.Path));
        Assert.False(File.Exists(Path.Combine(directory.Path, "data", "restore-journal.json")));
    }

    [Fact]
    public void JunctionBackupPathIsRejectedWithoutChangingExternalDirectory()
    {
        using var live = new AcceptanceTemporaryDirectory("offline restore junction live");
        using var external = new AcceptanceTemporaryDirectory("offline restore junction external");
        StorageLayout externalLayout = StorageLayout.Create(external.Path);
        SeedKeyboard(externalLayout, PressCountB);
        using (StorageService owner = StorageService.Open(externalLayout))
        {
            owner.CreateBackup("backups/external.itbackup");
        }
        string before = SnapshotDirectory(external.Path);
        string junction = Path.Combine(live.Path, "linked-backups");
        CreateJunction(junction, Path.Combine(external.Path, "backups"));
        try
        {
            Assert.Throws<InvalidDataException>(() => StorageOfflineRestore.RestoreOffline(
                StorageLayout.Create(live.Path), "linked-backups/external.itbackup"));
        }
        finally
        {
            if (Directory.Exists(junction)) Directory.Delete(junction);
        }

        Assert.Equal(before, SnapshotDirectory(external.Path));
    }

    [Fact]
    public void ForgedLayoutIsRejectedBeforeWritingOutsideProgramDirectory()
    {
        using var live = new AcceptanceTemporaryDirectory("offline restore canonical live");
        using var external = new AcceptanceTemporaryDirectory("offline restore forged external");
        StorageLayout canonical = StorageLayout.Create(live.Path);
        SeedKeyboard(canonical, PressCountA);
        using (StorageService owner = StorageService.Open(canonical))
        {
            owner.CreateBackup("backups/state.itbackup");
        }
        string forgedStorage = Path.Combine(external.Path, "forged");
        var forged = new StorageLayout(
            canonical.ProgramDirectory,
            forgedStorage,
            Path.Combine(forgedStorage, "store.sqlite3"),
            Path.Combine(forgedStorage, "spool"),
            Path.Combine(forgedStorage, "chunks"));

        Assert.Throws<InvalidDataException>(() =>
            StorageOfflineRestore.RestoreOffline(forged, "backups/state.itbackup"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(external.Path));
    }

    [Fact]
    public void AppSourceLocksUserConsentOrderTerminalStateAndSingleShutdownContract()
    {
        string controller = ReadAppSource("AppStorageDataTools.cs");
        string coordinator = ReadAppSource("AppOfflineRestoreCoordinator.cs");
        string application = ReadAppSource("App.xaml.cs");
        string window = ReadAppSource("MainWindow.xaml.cs");
        string collection = ReadAppSource("CollectionController.cs");

        Assert.Contains("PauseReason.User", controller, StringComparison.Ordinal);
        Assert.Contains("InputCaptureState.Paused", controller, StringComparison.Ordinal);
        Assert.Contains("AcceptingWrites", controller, StringComparison.Ordinal);
        Assert.Contains("finalConfirmed", controller, StringComparison.Ordinal);
        Assert.Contains("RestoreLease", controller, StringComparison.Ordinal);

        string[] ordered =
        [
            "QuiesceUiAsync", "ReleaseSystemEventBridge", "ReleaseCaptureAsync", "CaptureDiagnostics",
            "StopStorage", "ReleaseStorageOwner", "RestoreOfflineAsync", "RequestShutdown"
        ];
        int position = -1;
        foreach (string step in ordered)
        {
            int next = coordinator.IndexOf(step, position + 1, StringComparison.Ordinal);
            Assert.True(next > position, $"Offline restore order is missing or reversed at {step}.");
            position = next;
        }
        Assert.Contains("IngressOpen || diagnostics.RawInputRegistered", coordinator, StringComparison.Ordinal);
        Assert.Contains("StorageServiceState.Stopped", coordinator, StringComparison.Ordinal);
        Assert.Contains("finally", coordinator, StringComparison.Ordinal);
        Assert.Contains("_runtime.RequestShutdown()", coordinator, StringComparison.Ordinal);
        Assert.Contains("AllowsNormalExitResourceCleanup", application, StringComparison.Ordinal);
        Assert.Contains("StorageOfflineRestore.RestoreOffline", application, StringComparison.Ordinal);
        Assert.Contains("OfflineRestoreRequested?.Invoke", window, StringComparison.Ordinal);
        Assert.DoesNotContain("StorageOfflineRestore.RestoreOffline", window, StringComparison.Ordinal);
        Assert.Contains("RestoreSucceededAwaitExit", collection, StringComparison.Ordinal);
        Assert.Contains("RestoreFailedAwaitExit", collection, StringComparison.Ordinal);
        Assert.Contains("RestoreRecoveryRequired", collection, StringComparison.Ordinal);
    }

    private const long PressCountA = 2;
    private const long PressCountB = 7;
    private const long PressCountC = 11;
    private static readonly Guid KeyboardId = Guid.Parse("201e2044-adc1-4b43-a1cf-6f087891536c");

    private static void SeedKeyboard(StorageLayout layout, long count)
    {
        using StorageDatabase database = StorageDatabase.Open(layout);
        if (!database.ListDevices().Any(device => device.DeviceId == KeyboardId))
        {
            database.RegisterDevice(KeyboardId, (int)StorageDeviceKind.Keyboard, 0, 1);
        }
        database.UpsertKeyboardDaily(
            KeyboardId, 30, false, false, new DateOnly(2026, 8, 13), "UTC", count, count * 10);
    }

    private static void AssertKeyboard(StorageLayout layout, long expected)
    {
        using StorageService owner = StorageService.Open(layout);
        StorageKeyboardPeriodAggregate row = Assert.Single(owner.QueryKeyboardPeriod(
            StoragePeriodKind.Day, new DateOnly(2026, 8, 13), "UTC", KeyboardId).Aggregates);
        Assert.Equal(expected, row.PressCount);
        Assert.Equal(expected * 10, row.HoldDurationMicroseconds);
    }

    private static string ImportBackup(string source, StorageLayout layout, string relativePath)
    {
        string destination = Path.Combine(layout.ProgramDirectory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: false);
        return destination;
    }

    private static string ReadAppSource(string name) => File.ReadAllText(Path.Combine(
        AcceptanceEnvironment.FindRepositoryRoot(), "src", "InputTracker.App", name));

    private static string SnapshotDirectory(string root) => string.Join("\n",
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => $"{Path.GetRelativePath(root, path)}|{new FileInfo(path).Length}|{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))}")
            .Order(StringComparer.Ordinal));

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
