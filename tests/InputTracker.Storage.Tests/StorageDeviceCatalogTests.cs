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
    public void ContainerWithoutSerialDoesNotClaimPermanentCrossInterfaceIdentity()
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

        Assert.NotEqual(original.Device.DeviceId, changedInterface.Device.DeviceId);
        Assert.Equal(StorageDeviceIdentityConfidence.TransientInterface, original.Device.IdentityConfidence);
        Assert.True(original.Device.IdentityAmbiguity.HasFlag(StorageDeviceIdentityAmbiguity.MissingPermanentIdentifier));
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
    [InlineData("identity_ambiguity", 16)]
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
