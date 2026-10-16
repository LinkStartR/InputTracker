using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace InputTracker.Storage.Tests;

public sealed class StorageGamepadTests
{
    [Fact]
    public void ConfigurationRevisionKeepsStatisticsAndHidCalibrationHistoryDistinct()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid gamepad = Guid.NewGuid();
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock()))
            database.RegisterDevice(gamepad, (int)StorageDeviceKind.Gamepad, 0, 1);

        TimeCalibrationSegment calibration = StorageServiceTestSupport.Calibration(
            "UTC", new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero));
        using StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock());
        StorageGamepadConfiguration first = service.SetGamepadConfiguration(
            gamepad, StorageGamepadLayout.Xbox, true,
            [
                Digital(StorageGamepadButton.South, 0x01, 0x45, [1]),
                Digital(StorageGamepadButton.DpadUp, 0x01, 0x39, [3]),
                Digital(StorageGamepadButton.DpadRight, 0x01, 0x39, [3]),
                Axis(StorageGamepadButton.LeftTrigger, 0x01, 0x32, 210, 170,
                    StorageGamepadAxisDirection.Increasing),
                Axis(StorageGamepadButton.RightTrigger, 0x01, 0x35, -210, -170,
                    StorageGamepadAxisDirection.Decreasing)
            ]);
        Assert.Equal(1, first.Revision);
        Assert.Equal(5, first.Bindings.Count);
        Assert.Equal(210, Assert.Single(first.Bindings, row => row.Button == StorageGamepadButton.LeftTrigger).ActivationThreshold);
        Assert.Empty(Assert.Single(first.Bindings, row => row.Button == StorageGamepadButton.LeftTrigger).ActiveValues);

        service.Start(calibration);
        service.WriteGamepadPress(Press(gamepad, StorageGamepadButton.South, calibration, 0));
        service.WriteGamepadPress(Press(gamepad, StorageGamepadButton.LeftTrigger, calibration, 1_000_000));

        StorageGamepadConfiguration second = service.SetGamepadConfiguration(
            gamepad, StorageGamepadLayout.Xbox, true,
            [Digital(StorageGamepadButton.South, 0x01, 0x46, [1])]);
        Assert.Equal(2, second.Revision);
        service.WriteGamepadPress(Press(gamepad, StorageGamepadButton.South, calibration, 2_000_000));

        StorageGamepadButtonPeriodResult result = service.QueryGamepadButtonsPeriod(
            StoragePeriodKind.Day, new DateOnly(2026, 9, 9), "UTC", gamepad);
        Assert.Equal(3, result.Aggregates.Sum(row => row.PressCount));
        Assert.Equal(
            [(1, StorageGamepadButton.South, 1L),
             (1, StorageGamepadButton.LeftTrigger, 1L),
             (2, StorageGamepadButton.South, 1L)],
            result.Aggregates.Select(row => (row.ConfigurationRevision, row.Button, row.PressCount)));

        service.SetGamepadConfiguration(gamepad, StorageGamepadLayout.Xbox, false, []);
        service.WriteGamepadPress(Press(gamepad, StorageGamepadButton.South, calibration, 3_000_000));
        Assert.Equal(3, service.QueryGamepadButtonsRange(
            new DateOnly(2026, 9, 9), new DateOnly(2026, 9, 9), "UTC", gamepad)
            .Aggregates.Sum(row => row.PressCount));
    }

    [Fact]
    public async Task GamepadFullDayDeletionUsesPreviewTokenBackupAndRestoresAllRevisions()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid gamepad = Guid.NewGuid();
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock()))
        {
            database.RegisterDevice(gamepad, (int)StorageDeviceKind.Gamepad, 0, 1);
            database.SetGamepadConfiguration(gamepad, StorageGamepadLayout.Xbox, true,
                [Digital(StorageGamepadButton.South, 1, 0x45, [1])], 1);
            database.CommitGamepadPress(DirectPress(gamepad, StorageGamepadButton.South),
                new DateOnly(2026, 9, 9), "UTC", NoStorageServiceFaultInjector.Instance);
            database.SetGamepadConfiguration(gamepad, StorageGamepadLayout.Xbox, true,
                [Digital(StorageGamepadButton.South, 1, 0x46, [1])], 2);
            database.CommitGamepadPress(DirectPress(gamepad, StorageGamepadButton.South),
                new DateOnly(2026, 9, 9), "UTC", NoStorageServiceFaultInjector.Instance);
        }

        StorageUtcRange day = StoragePeriodResolver.Resolve(
            StoragePeriodKind.Day, new DateOnly(2026, 9, 9), "UTC").UtcRange;
        using StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock());
        StorageDeletionPreview preview = await service.PreviewDeletionAsync(gamepad, day);
        Assert.Equal(StorageDeletionCapability.ExactGamepadDailyAggregates, preview.Capability);
        Assert.Equal(2, preview.GamepadAggregateCount);
        Assert.Equal(2, preview.GamepadPressCount);
        Assert.Throws<InvalidOperationException>(() => service.ExecuteDeletion(
            preview, new string('0', 64), "backups/wrong-gamepad-token.itbackup"));

        StorageDeletionResult deleted = service.ExecuteDeletion(
            preview, preview.Token, "backups/before-gamepad-delete.itbackup");
        Assert.Equal(2, deleted.DeletedGamepadAggregateCount);
        Assert.Equal(2, deleted.DeletedGamepadPressCount);
        Assert.Empty(service.QueryGamepadButtonsPeriod(
            StoragePeriodKind.Day, new DateOnly(2026, 9, 9), "UTC", gamepad).Aggregates);
        Assert.True(File.Exists(deleted.PreDeletionBackup.Path));

        service.Dispose();
        StorageBackup.Restore(layout, Path.GetRelativePath(
            layout.ProgramDirectory, deleted.PreDeletionBackup.Path));
        using StorageService restored = StorageService.OpenForTests(layout, new ControllableStorageClock());
        Assert.Equal(2, restored.QueryGamepadButtonsPeriod(
            StoragePeriodKind.Day, new DateOnly(2026, 9, 9), "UTC", gamepad)
            .Aggregates.Sum(row => row.PressCount));
        Assert.Equal(2, restored.GetGamepadConfiguration(gamepad)!.Revision);
    }

    [Fact]
    public void VersionSixDatabaseMigratesWithoutChangingItsExistingAggregates()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid keyboard = Guid.NewGuid();
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock()))
        {
            database.RegisterDevice(keyboard, (int)StorageDeviceKind.Keyboard, 0, 1);
            database.UpsertKeyboardDaily(keyboard, 30, false, false,
                new DateOnly(2026, 9, 9), "UTC", 4, 400);
        }
        DowngradeGamepadSchemaToVersionSix(layout.DatabasePath);

        using StorageDatabase migrated = StorageDatabase.Open(layout, new FakeClock());
        Assert.Equal(7, migrated.SchemaVersion);
        Assert.Equal(4, ReadScalar<long>(migrated.Connection,
            "SELECT press_count FROM keyboard_daily WHERE device_id = '" + keyboard.ToString("N") + "';"));
        Assert.Equal(StorageSchema.Version7Checksum, ReadScalar<string>(migrated.Connection,
            "SELECT checksum FROM schema_migrations WHERE version = 7;"));
    }

    [Fact]
    public void GamepadSchemaContainsCalibratedBindingsAndDailyCountsOnly()
    {
        using var directory = new TemporaryDirectory();
        using StorageDatabase database = StorageDatabase.Open(StorageLayout.Create(directory.Path), new FakeClock());
        IReadOnlyDictionary<string, IReadOnlyList<string>> schema = database.ReadSchemaColumns();

        Assert.Equal(
            ["device_id", "configuration_revision", "button", "capture_local_date", "time_zone_id", "press_count"],
            schema["gamepad_daily"]);
        Assert.Contains("gamepad_hid_bindings", schema.Keys);
        Assert.Contains("gamepad_hid_axis_bindings", schema.Keys);
        string columns = string.Join('|', schema
            .Where(pair => pair.Key.StartsWith("gamepad_", StringComparison.Ordinal))
            .SelectMany(pair => pair.Value));
        foreach (string forbidden in new[]
        {
            "interface_path", "hardware_id", "serial_number", "window", "process", "text",
            "sequence", "event_timestamp", "raw_report", "report_bytes", "route", "position"
        })
            Assert.DoesNotContain(forbidden, columns, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UncalibratedPhysicalButtonCannotCreateStatistics()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid gamepad = Guid.NewGuid();
        using StorageDatabase database = StorageDatabase.Open(layout, new FakeClock());
        database.RegisterDevice(gamepad, (int)StorageDeviceKind.Gamepad, 0, 1);
        database.SetGamepadConfiguration(gamepad, StorageGamepadLayout.Xbox, true,
            [Digital(StorageGamepadButton.South, 1, 0x45, [1])], 1);

        Assert.Throws<InvalidDataException>(() => database.CommitGamepadPress(
            DirectPress(gamepad, StorageGamepadButton.West),
            new DateOnly(2026, 9, 9), "UTC", NoStorageServiceFaultInjector.Instance));
        Assert.Equal(0, ReadScalar<long>(database.Connection, "SELECT COUNT(*) FROM gamepad_daily;"));
    }

    [Fact]
    public void V6BackupIsUpgradedAndRestoredWithItsOriginalData()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout live = StorageLayout.Create(directory.Path);
        Guid keyboard = Guid.NewGuid();
        using (StorageDatabase database = StorageDatabase.Open(live, new FakeClock()))
        {
            database.RegisterDevice(keyboard, (int)StorageDeviceKind.Keyboard, 0, 1);
            database.UpsertKeyboardDaily(keyboard, 30, false, false,
                new DateOnly(2026, 9, 9), "UTC", 6, 600);
        }
        using (StorageService service = StorageService.OpenForTests(live, new ControllableStorageClock()))
            service.CreateBackup("backups/current.itbackup");

        string legacyPath = CreateV6Backup(live, "backups/current.itbackup", "backups/legacy-v6.itbackup");
        StorageBackup.Restore(live, Path.GetRelativePath(live.ProgramDirectory, legacyPath));

        using StorageDatabase restored = StorageDatabase.Open(live, new FakeClock());
        Assert.Equal(7, restored.SchemaVersion);
        Assert.Equal(6, ReadScalar<long>(restored.Connection,
            "SELECT press_count FROM keyboard_daily WHERE device_id = '" + keyboard.ToString("N") + "';"));
    }

    private static StorageGamepadHidBinding Digital(
        StorageGamepadButton button, ushort usagePage, ushort usage, IReadOnlyList<int> values) =>
        new(button, usagePage, usage, 1, usage, values);

    private static StorageGamepadHidBinding Axis(
        StorageGamepadButton button,
        ushort usagePage,
        ushort usage,
        int activation,
        int release,
        StorageGamepadAxisDirection direction) =>
        new(button, usagePage, usage, 1, usage, [], activation, release, direction);

    private static StorageGamepadPress Press(
        Guid deviceId, StorageGamepadButton button, TimeCalibrationSegment calibration, long timestamp) =>
        new(deviceId, button, timestamp, calibration.SessionId);

    private static StorageGamepadPress DirectPress(Guid deviceId, StorageGamepadButton button) =>
        new(deviceId, button, 0, Guid.NewGuid());

    private static void DowngradeGamepadSchemaToVersionSix(string databasePath)
    {
        using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            DROP TABLE gamepad_daily;
            DROP TABLE gamepad_hid_axis_bindings;
            DROP TABLE gamepad_hid_bindings;
            DROP TABLE gamepad_configuration_current;
            DROP TABLE gamepad_configuration_revisions;
            DELETE FROM schema_migrations WHERE version = 7;
            PRAGMA user_version = 6;
            PRAGMA wal_checkpoint(TRUNCATE);
            """;
        command.ExecuteNonQuery();
    }

    private static string CreateV6Backup(StorageLayout layout, string sourceRelative, string targetRelative)
    {
        string work = Path.Combine(layout.ProgramDirectory, "data", "v6-test-work");
        Directory.CreateDirectory(work);
        string sourcePath = Path.Combine(layout.ProgramDirectory, sourceRelative.Replace('/', Path.DirectorySeparatorChar));
        string targetPath = Path.Combine(layout.ProgramDirectory, targetRelative.Replace('/', Path.DirectorySeparatorChar));
        string[] entries;
        using (ZipArchive source = ZipFile.OpenRead(sourcePath))
        {
            foreach (ZipArchiveEntry entry in source.Entries.Where(entry => entry.FullName != "manifest.json"))
            {
                string target = Path.Combine(work, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, overwrite: true);
            }
            entries = source.Entries.Where(entry => entry.FullName != "manifest.json")
                .Select(entry => entry.FullName).ToArray();
        }
        DowngradeGamepadSchemaToVersionSix(Path.Combine(work, "data", "storage", "store.sqlite3"));
        var files = entries.Select(name =>
        {
            string path = Path.Combine(work, name.Replace('/', Path.DirectorySeparatorChar));
            using Stream input = File.OpenRead(path);
            return new { relativePath = name, length = input.Length, sha256 = Convert.ToHexString(SHA256.HashData(input)) };
        }).ToArray();
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            FormatVersion = 1,
            SchemaVersion = 6,
            Files = files.Select(file => new
            {
                RelativePath = file.relativePath,
                Length = file.length,
                Sha256 = file.sha256
            }).ToArray()
        });
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        using (ZipArchive target = ZipFile.Open(targetPath, ZipArchiveMode.Create))
        {
            Write(target, "manifest.json", manifest);
            foreach (string name in entries)
            {
                ZipArchiveEntry entry = target.CreateEntry(name, CompressionLevel.Fastest);
                using Stream input = File.OpenRead(Path.Combine(work, name.Replace('/', Path.DirectorySeparatorChar)));
                using Stream output = entry.Open();
                input.CopyTo(output);
            }
        }
        return targetPath;
    }

    private static void Write(ZipArchive archive, string name, byte[] data)
    {
        using Stream output = archive.CreateEntry(name).Open();
        output.Write(data);
    }

    private static T ReadScalar<T>(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }
}
