using Microsoft.Data.Sqlite;

namespace InputTracker.Storage.Tests;

public sealed class StorageDeviceCatalogTests
{
    [Fact]
    public void LegacyRegisteredDevicesUseExistingKindEncodingAndUnknownConfidence()
    {
        using var directory = new TemporaryDirectory();
        using StorageDatabase database = StorageDatabase.Open(StorageLayout.Create(directory.Path), new FakeClock());
        Guid mouse = Guid.NewGuid();
        Guid keyboard = Guid.NewGuid();
        database.RegisterDevice(mouse, kind: 0, identityConfidence: 0, createdUtcMicroseconds: 1);
        database.RegisterDevice(keyboard, kind: 1, identityConfidence: 0, createdUtcMicroseconds: 2);

        IReadOnlyDictionary<Guid, StorageDeviceInfo> devices = database.ListDevices()
            .ToDictionary(device => device.DeviceId);

        Assert.Equal(StorageDeviceKind.Mouse, devices[mouse].Kind);
        Assert.Equal(StorageDeviceKind.Keyboard, devices[keyboard].Kind);
        Assert.Equal(StorageDeviceIdentityConfidence.Unknown, devices[mouse].IdentityConfidence);
        Assert.Equal(StorageDeviceIdentityConfidence.Unknown, devices[keyboard].IdentityConfidence);
    }

    [Fact]
    public void StableHardwareReinsertReusesPersistentGuidAndAliasAcrossInterfacePaths()
    {
        using var directory = new TemporaryDirectory();
        using StorageDatabase database = StorageDatabase.Open(StorageLayout.Create(directory.Path), new FakeClock());
        StorageDeviceObservation first = Observation("HID#FIRST", serial: "SERIAL-42");
        StorageDeviceObservation second = first with { InterfacePath = "hid#second" };

        StorageDeviceResolution created = database.ObserveDevice(first, 10);
        StorageDeviceInfo aliased = database.SetDeviceAlias(created.Device.DeviceId, "  Desk keyboard  ");
        StorageDeviceResolution reinserted = database.ObserveDevice(second, 20);

        Assert.False(created.ReusedExisting);
        Assert.True(reinserted.ReusedExisting);
        Assert.Equal(created.Device.DeviceId, reinserted.Device.DeviceId);
        Assert.Equal(StorageDeviceIdentityConfidence.PersistentHardware, reinserted.Device.IdentityConfidence);
        Assert.Equal(StorageDeviceIdentityAmbiguity.None, reinserted.Device.IdentityAmbiguity);
        Assert.Equal("Desk keyboard", aliased.Alias);
        Assert.Equal("Desk keyboard", reinserted.Device.Alias);
        Assert.Equal(20, reinserted.Device.LastSeenUtcMicroseconds);
        Assert.Single(database.ListDevices());
        Assert.Equal(2, CountMappings(database.Connection));
    }

    [Fact]
    public void StableHardwareAndAliasSurviveCloseReopenAndChangedInterface()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        StorageDeviceObservation first = Observation("HID#FIRST", serial: "SERIAL-REOPEN");
        Guid deviceId;
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock()))
        {
            StorageDeviceResolution created = database.ObserveDevice(first, 10);
            deviceId = created.Device.DeviceId;
            database.SetDeviceAlias(deviceId, "Reopened keyboard");
        }

        using StorageDatabase reopened = StorageDatabase.Open(layout, new FakeClock());
        StorageDeviceResolution reinserted = reopened.ObserveDevice(
            first with { InterfacePath = "HID#SECOND" },
            20);

        Assert.True(reinserted.ReusedExisting);
        Assert.Equal(deviceId, reinserted.Device.DeviceId);
        Assert.Equal("Reopened keyboard", reinserted.Device.Alias);
        Assert.Single(reopened.ListDevices());
        Assert.Equal(2, CountMappings(reopened.Connection));
    }

    [Fact]
    public void HardwareIdOrderAndCaseDoNotChangeStableIdentity()
    {
        using var directory = new TemporaryDirectory();
        using StorageDatabase database = StorageDatabase.Open(StorageLayout.Create(directory.Path), new FakeClock());
        StorageDeviceObservation first = Observation("HID#A", serial: "abc") with
        {
            HardwareIds = ["HID\\VID_1234&PID_5678", "HID_DEVICE_SYSTEM_KEYBOARD"]
        };
        StorageDeviceObservation second = first with
        {
            InterfacePath = "HID#B",
            HardwareIds = ["hid_device_system_keyboard", "hid\\vid_1234&pid_5678"],
            SerialNumber = "ABC"
        };

        StorageDeviceResolution original = database.ObserveDevice(first, 1);
        StorageDeviceResolution reinserted = database.ObserveDevice(second, 2);

        Assert.Equal(original.Device.DeviceId, reinserted.Device.DeviceId);
        Assert.True(reinserted.ReusedExisting);
    }

    [Fact]
    public void MissingPermanentIdentifierDoesNotMergeAChangedInterface()
    {
        using var directory = new TemporaryDirectory();
        using StorageDatabase database = StorageDatabase.Open(StorageLayout.Create(directory.Path), new FakeClock());
        StorageDeviceObservation first = Observation("HID#A", serial: null);
        StorageDeviceObservation second = first with { InterfacePath = "HID#B" };

        StorageDeviceResolution original = database.ObserveDevice(first, 1);
        StorageDeviceResolution changedInterface = database.ObserveDevice(second, 2);

        Assert.NotEqual(original.Device.DeviceId, changedInterface.Device.DeviceId);
        Assert.Equal(StorageDeviceIdentityConfidence.TransientInterface, original.Device.IdentityConfidence);
        Assert.True(original.Device.IdentityAmbiguity.HasFlag(StorageDeviceIdentityAmbiguity.MissingPermanentIdentifier));
        Assert.Equal(2, database.ListDevices().Count);
    }

    [Fact]
    public void PhysicalContainerReusesDeviceAcrossInterfaceWithoutClaimingPermanentIdentity()
    {
        using var directory = new TemporaryDirectory();
        using StorageDatabase database = StorageDatabase.Open(StorageLayout.Create(directory.Path), new FakeClock());
        StorageDeviceObservation first = Observation("HID#A", serial: null) with
        {
            ContainerId = Guid.NewGuid()
        };
        StorageDeviceObservation second = first with { InterfacePath = "HID#B" };

        StorageDeviceResolution original = database.ObserveDevice(first, 1);
        StorageDeviceResolution changedInterface = database.ObserveDevice(second, 2);

        Assert.Equal(original.Device.DeviceId, changedInterface.Device.DeviceId);
        Assert.True(changedInterface.ReusedExisting);
        Assert.Equal(StorageDeviceIdentityConfidence.ReconnectableHardware, original.Device.IdentityConfidence);
        Assert.Equal(StorageDeviceIdentityConfidence.ReconnectableHardware, changedInterface.Device.IdentityConfidence);
        Assert.True(original.Device.IdentityAmbiguity.HasFlag(StorageDeviceIdentityAmbiguity.MissingPermanentIdentifier));
        Assert.Single(database.ListDevices());
        Assert.Equal(2, CountMappings(database.Connection));
    }

    [Fact]
    public void PhysicalContainerAndAliasSurviveCloseReopenAndChangedInterface()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid container = Guid.NewGuid();
        Guid originalId;
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock()))
        {
            StorageDeviceResolution original = database.ObserveDevice(
                Observation("HID#FIRST", serial: null) with { ContainerId = container }, 1);
            originalId = original.Device.DeviceId;
            database.SetDeviceAlias(originalId, "Desk keyboard");
        }

        using StorageDatabase reopened = StorageDatabase.Open(layout, new FakeClock());
        StorageDeviceResolution reinserted = reopened.ObserveDevice(
            Observation("HID#SECOND", serial: null) with { ContainerId = container }, 2);

        Assert.True(reinserted.ReusedExisting);
        Assert.Equal(originalId, reinserted.Device.DeviceId);
        Assert.Equal("Desk keyboard", reinserted.Device.Alias);
        Assert.Equal(StorageDeviceIdentityConfidence.ReconnectableHardware, reinserted.Device.IdentityConfidence);
        Assert.True(reinserted.Device.IdentityAmbiguity.HasFlag(
            StorageDeviceIdentityAmbiguity.MissingPermanentIdentifier));
    }

    [Fact]
    public void SameContainerWithDifferentHardwareDoesNotMerge()
    {
        using var directory = new TemporaryDirectory();
        using StorageDatabase database = StorageDatabase.Open(StorageLayout.Create(directory.Path), new FakeClock());
        Guid container = Guid.NewGuid();
        StorageDeviceObservation first = Observation("HID#FIRST", serial: null) with
        {
            ContainerId = container
        };
        StorageDeviceObservation second = first with
        {
            InterfacePath = "HID#SECOND",
            HardwareIds = ["HID\\VID_9999&PID_0001"]
        };

        StorageDeviceResolution original = database.ObserveDevice(first, 1);
        StorageDeviceResolution changedHardware = database.ObserveDevice(second, 2);

        Assert.False(changedHardware.ReusedExisting);
        Assert.NotEqual(original.Device.DeviceId, changedHardware.Device.DeviceId);
    }

    [Fact]
    public void IndeterminateTransportNeverReusesContainerAcrossInterfaces()
    {
        using var directory = new TemporaryDirectory();
        using StorageDatabase database = StorageDatabase.Open(StorageLayout.Create(directory.Path), new FakeClock());
        Guid container = Guid.NewGuid();
        StorageDeviceObservation first = Observation("HID#A", serial: null) with
        {
            ContainerId = container,
            IsIndeterminate = true
        };

        StorageDeviceResolution original = database.ObserveDevice(first, 1);
        StorageDeviceResolution changedInterface = database.ObserveDevice(
            first with { InterfacePath = "HID#B" }, 2);

        Assert.NotEqual(original.Device.DeviceId, changedInterface.Device.DeviceId);
        Assert.Equal(StorageDeviceIdentityConfidence.TransientInterface, original.Device.IdentityConfidence);
        Assert.True(original.Device.IdentityAmbiguity.HasFlag(
            StorageDeviceIdentityAmbiguity.IndeterminateTransport));
    }

    [Fact]
    public void MultipleContainerCandidatesStaySeparateAndAreReportedAsAmbiguous()
    {
        using var directory = new TemporaryDirectory();
        using StorageDatabase database = StorageDatabase.Open(StorageLayout.Create(directory.Path), new FakeClock());
        StorageDeviceObservation observation = Observation("HID#FIRST", serial: null) with
        {
            ContainerId = Guid.NewGuid()
        };
        StorageDeviceResolution first = database.ObserveDevice(observation, 1);
        byte[] hardwareFingerprint;
        using (SqliteCommand read = database.Connection.CreateCommand())
        {
            read.CommandText = "SELECT hardware_fingerprint FROM device_identity_mappings LIMIT 1;";
            hardwareFingerprint = (byte[])read.ExecuteScalar()!;
        }

        Guid duplicate = Guid.NewGuid();
        database.RegisterDevice(duplicate, kind: 1, identityConfidence: 1, createdUtcMicroseconds: 2);
        using (SqliteCommand insert = database.Connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO device_identity_mappings(
                    interface_fingerprint, device_id, kind, hardware_fingerprint,
                    stable_hardware_fingerprint, first_seen_utc_us, last_seen_utc_us)
                VALUES ($interface, $device, 1, $hardware, NULL, 2, 2);
                """;
            insert.Parameters.AddWithValue("$interface", Enumerable.Repeat((byte)0xA5, 32).ToArray());
            insert.Parameters.AddWithValue("$device", duplicate.ToString("N"));
            insert.Parameters.AddWithValue("$hardware", hardwareFingerprint);
            insert.ExecuteNonQuery();
        }

        StorageDeviceResolution result = database.ObserveDevice(
            observation with { InterfacePath = "HID#THIRD" }, 3);

        Assert.False(result.ReusedExisting);
        Assert.NotEqual(first.Device.DeviceId, result.Device.DeviceId);
        Assert.NotEqual(duplicate, result.Device.DeviceId);
        Assert.Equal(StorageDeviceIdentityConfidence.TransientInterface, result.Device.IdentityConfidence);
        Assert.True(result.Device.IdentityAmbiguity.HasFlag(StorageDeviceIdentityAmbiguity.MultipleCandidates));
        Assert.All(database.ListDevices(), device =>
        {
            Assert.Equal(StorageDeviceIdentityConfidence.TransientInterface, device.IdentityConfidence);
            Assert.True(device.IdentityAmbiguity.HasFlag(StorageDeviceIdentityAmbiguity.MultipleCandidates));
        });
    }

    [Theory]
    [InlineData(true, false, false, StorageDeviceIdentityAmbiguity.BluetoothTransport)]
    [InlineData(false, true, false, StorageDeviceIdentityAmbiguity.RemoteDesktop)]
    [InlineData(false, false, true, StorageDeviceIdentityAmbiguity.VirtualDevice)]
    public void NonPhysicalTransportsNeverClaimPermanentCrossInterfaceIdentity(
        bool bluetooth,
        bool remoteDesktop,
        bool virtualDevice,
        StorageDeviceIdentityAmbiguity expectedAmbiguity)
    {
        using var directory = new TemporaryDirectory();
        using StorageDatabase database = StorageDatabase.Open(StorageLayout.Create(directory.Path), new FakeClock());
        StorageDeviceObservation first = Observation("HID#A", serial: "SERIAL") with
        {
            IsBluetooth = bluetooth,
            IsRemoteDesktop = remoteDesktop,
            IsVirtual = virtualDevice
        };
        StorageDeviceObservation second = first with { InterfacePath = "HID#B" };

        StorageDeviceResolution original = database.ObserveDevice(first, 1);
        StorageDeviceResolution changedInterface = database.ObserveDevice(second, 2);

        Assert.NotEqual(original.Device.DeviceId, changedInterface.Device.DeviceId);
        Assert.Equal(StorageDeviceIdentityConfidence.TransientInterface, original.Device.IdentityConfidence);
        Assert.True(original.Device.IdentityAmbiguity.HasFlag(expectedAmbiguity));
    }

    [Fact]
    public void ReusedInterfaceWithConflictingHardwareFailsClosedWithoutNewMapping()
    {
        using var directory = new TemporaryDirectory();
        using StorageDatabase database = StorageDatabase.Open(StorageLayout.Create(directory.Path), new FakeClock());
        StorageDeviceObservation first = Observation("HID#SAME", serial: "SERIAL-A");
        StorageDeviceObservation conflicting = first with { SerialNumber = "SERIAL-B" };
        database.ObserveDevice(first, 1);

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            database.ObserveDevice(conflicting, 2));

        Assert.Contains("conflicting", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(database.ListDevices());
        Assert.Equal(1, CountMappings(database.Connection));
    }

    [Theory]
    [InlineData("kind", 99)]
    [InlineData("identity_confidence", 99)]
    [InlineData("identity_ambiguity", 64)]
    public void UnsupportedLegacyDeviceEnumValuesFailClosed(string column, int invalidValue)
    {
        using var directory = new TemporaryDirectory();
        using StorageDatabase database = StorageDatabase.Open(StorageLayout.Create(directory.Path), new FakeClock());
        Guid deviceId = Guid.NewGuid();
        database.RegisterDevice(deviceId, kind: 0, identityConfidence: 0, createdUtcMicroseconds: 1);
        string validatedColumn = column switch
        {
            "kind" => "kind",
            "identity_confidence" => "identity_confidence",
            "identity_ambiguity" => "identity_ambiguity",
            _ => throw new ArgumentOutOfRangeException(nameof(column))
        };
        using (SqliteCommand command = database.Connection.CreateCommand())
        {
            command.CommandText = $"UPDATE devices SET {validatedColumn} = $invalid WHERE device_id = $device;";
            command.Parameters.AddWithValue("$invalid", invalidValue);
            command.Parameters.AddWithValue("$device", deviceId.ToString("N"));
            command.ExecuteNonQuery();
        }

        InvalidDataException error = Assert.Throws<InvalidDataException>(() => database.ListDevices());

        Assert.Contains("unsupported", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void IdentityMappingsPersistOnlyFingerprintsWhileAliasRemainsVisibleMetadata()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid containerId = Guid.Parse("11111111-2222-3333-4444-555555555555");
        using StorageDatabase database = StorageDatabase.Open(layout, new FakeClock());
        StorageDeviceResolution resolution = database.ObserveDevice(
            Observation("HID#PRIVATE-INTERFACE", serial: "PRIVATE-SERIAL") with
            {
                HardwareIds = ["HID\\PRIVATE-HARDWARE-ID"],
                ContainerId = containerId
            },
            1);
        database.SetDeviceAlias(resolution.Device.DeviceId, "VISIBLE USER ALIAS");
        using (SqliteCommand command = database.Connection.CreateCommand())
        {
            command.CommandText = """
                SELECT length(interface_fingerprint), length(hardware_fingerprint),
                       length(stable_hardware_fingerprint)
                FROM device_identity_mappings;
                """;
            using SqliteDataReader reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(32, reader.GetInt32(0));
            Assert.Equal(32, reader.GetInt32(1));
            Assert.Equal(32, reader.GetInt32(2));
        }

        string[] storageFiles = Directory.GetFiles(layout.StorageDirectory, "store.sqlite3*");
        Assert.Contains(storageFiles, path => path.EndsWith("-wal", StringComparison.OrdinalIgnoreCase));
        string storageText = string.Concat(storageFiles.Select(path =>
            System.Text.Encoding.UTF8.GetString(ReadSharedBytes(path))));
        Assert.DoesNotContain("PRIVATE-INTERFACE", storageText, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE-HARDWARE-ID", storageText, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE-SERIAL", storageText, StringComparison.Ordinal);
        Assert.DoesNotContain(containerId.ToString("D"), storageText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(containerId.ToString("N"), storageText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("VISIBLE USER ALIAS", storageText, StringComparison.Ordinal);
    }

    private static StorageDeviceObservation Observation(string interfacePath, string? serial) => new(
        StorageDeviceKind.Keyboard,
        interfacePath,
        ["HID\\VID_1234&PID_5678"],
        serial,
        ContainerId: null,
        IsBluetooth: false,
        IsRemoteDesktop: false,
        IsVirtual: false);

    private static int CountMappings(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM device_identity_mappings;";
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static byte[] ReadSharedBytes(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
