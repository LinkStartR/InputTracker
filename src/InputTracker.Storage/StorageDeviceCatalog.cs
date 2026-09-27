using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace InputTracker.Storage;

internal static class StorageDeviceCatalog
{
    public static StorageDeviceResolution Observe(
        StorageDatabase database,
        StorageDeviceObservation observation,
        long observedUtcMicroseconds)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.Kind is not StorageDeviceKind.Keyboard
            and not StorageDeviceKind.Mouse
            and not StorageDeviceKind.Gamepad)
        {
            throw new ArgumentOutOfRangeException(nameof(observation), "A device observation kind is unsupported.");
        }

        if (string.IsNullOrWhiteSpace(observation.InterfacePath))
        {
            throw new ArgumentException("A device observation must contain an interface path.", nameof(observation));
        }

        ArgumentNullException.ThrowIfNull(observation.HardwareIds);
        if (observedUtcMicroseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(observedUtcMicroseconds));
        }

        NormalizedIdentity identity = Normalize(observation);
        byte[] interfaceFingerprint = Hash(Compose(
            ((int)observation.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture),
            identity.InterfacePath));
        byte[] hardwareFingerprint = Hash(Compose(
            ((int)observation.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture),
            string.Join("\n", identity.HardwareIds),
            identity.SerialNumber,
            identity.ContainerId));
        StorageDeviceIdentityAmbiguity ambiguity = GetAmbiguity(observation, identity);
        bool mayPersistAcrossInterfaces = ambiguity == StorageDeviceIdentityAmbiguity.None;
        bool mayReconnectAcrossInterfaces = ambiguity == StorageDeviceIdentityAmbiguity.MissingPermanentIdentifier
            && identity.ContainerId.Length > 0
            && identity.HardwareIds.Count > 0;
        byte[]? stableHardwareFingerprint = mayPersistAcrossInterfaces ? hardwareFingerprint : null;
        StorageDeviceIdentityConfidence confidence = stableHardwareFingerprint is not null
            ? StorageDeviceIdentityConfidence.PersistentHardware
            : mayReconnectAcrossInterfaces
                ? StorageDeviceIdentityConfidence.ReconnectableHardware
                : StorageDeviceIdentityConfidence.TransientInterface;

        SqliteConnection connection = database.Connection;
        using SqliteTransaction transaction = connection.BeginTransaction();
        InterfaceMatch? interfaceMatch = FindInterface(connection, transaction, interfaceFingerprint);
        if (interfaceMatch is not null)
        {
            if (interfaceMatch.Kind != observation.Kind
                || !interfaceMatch.HardwareFingerprint.AsSpan().SequenceEqual(hardwareFingerprint)
                || !NullableFingerprintsEqual(interfaceMatch.StableHardwareFingerprint, stableHardwareFingerprint))
            {
                throw new InvalidDataException("A known device interface was observed with conflicting hardware identity.");
            }

            Touch(connection, transaction, interfaceMatch.DeviceId, interfaceFingerprint, observedUtcMicroseconds);
            if (confidence == StorageDeviceIdentityConfidence.ReconnectableHardware)
            {
                UpdateIdentityAssessment(connection, transaction, interfaceMatch.DeviceId, confidence, ambiguity);
            }
            StorageDeviceInfo existing = ReadDevice(connection, transaction, interfaceMatch.DeviceId);
            transaction.Commit();
            return new StorageDeviceResolution(existing, ReusedExisting: true);
        }

        Guid deviceId;
        bool reused;
        if (stableHardwareFingerprint is not null)
        {
            IReadOnlyList<Guid> stableMatches = FindStableHardware(
                connection,
                transaction,
                stableHardwareFingerprint,
                observation.Kind);
            if (stableMatches.Count > 1)
            {
                throw new InvalidDataException("A persistent hardware identity maps to multiple devices; automatic merge is fail-closed.");
            }

            reused = stableMatches.Count == 1;
            deviceId = reused ? stableMatches[0] : Guid.NewGuid();
        }
        else if (mayReconnectAcrossInterfaces)
        {
            IReadOnlyList<Guid> reconnectMatches = FindHardware(
                connection,
                transaction,
                hardwareFingerprint,
                observation.Kind);
            reused = reconnectMatches.Count == 1;
            deviceId = reused ? reconnectMatches[0] : Guid.NewGuid();
            if (reconnectMatches.Count > 1)
            {
                confidence = StorageDeviceIdentityConfidence.TransientInterface;
                ambiguity |= StorageDeviceIdentityAmbiguity.MultipleCandidates;
                foreach (Guid candidate in reconnectMatches)
                {
                    UpdateIdentityAssessment(connection, transaction, candidate, confidence, ambiguity);
                }
            }
        }
        else
        {
            reused = false;
            deviceId = Guid.NewGuid();
        }

        if (!reused)
        {
            InsertDevice(
                connection,
                transaction,
                deviceId,
                observation.Kind,
                confidence,
                ambiguity,
                stableHardwareFingerprint ?? hardwareFingerprint,
                observedUtcMicroseconds);
        }
        else
        {
            TouchDevice(connection, transaction, deviceId, observedUtcMicroseconds);
            if (confidence == StorageDeviceIdentityConfidence.ReconnectableHardware)
            {
                UpdateIdentityAssessment(connection, transaction, deviceId, confidence, ambiguity);
            }
        }

        InsertMapping(
            connection,
            transaction,
            interfaceFingerprint,
            deviceId,
            observation.Kind,
            hardwareFingerprint,
            stableHardwareFingerprint,
            observedUtcMicroseconds);
        StorageDeviceInfo result = ReadDevice(connection, transaction, deviceId);
        transaction.Commit();
        return new StorageDeviceResolution(result, reused);
    }

    public static IReadOnlyList<StorageDeviceInfo> ReadAll(StorageDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        using SqliteCommand command = database.Connection.CreateCommand();
        command.CommandText = """
            SELECT device_id, kind, identity_confidence, identity_ambiguity, alias,
                   created_utc_us, COALESCE(last_seen_utc_us, created_utc_us)
            FROM devices
            ORDER BY created_utc_us, device_id;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var devices = new List<StorageDeviceInfo>();
        while (reader.Read())
        {
            devices.Add(ReadDevice(reader));
        }

        return devices;
    }

    public static StorageDeviceInfo SetAlias(StorageDatabase database, Guid deviceId, string? alias)
    {
        ArgumentNullException.ThrowIfNull(database);
        if (deviceId == Guid.Empty)
        {
            throw new ArgumentException("Device identifier must be non-empty.", nameof(deviceId));
        }

        string? normalizedAlias = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim();
        if (normalizedAlias?.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(alias), "A device alias cannot exceed 128 characters.");
        }

        using SqliteTransaction transaction = database.Connection.BeginTransaction();
        using (SqliteCommand command = database.Connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE devices SET alias = $alias WHERE device_id = $device;";
            command.Parameters.AddWithValue("$alias", (object?)normalizedAlias ?? DBNull.Value);
            command.Parameters.AddWithValue("$device", deviceId.ToString("N"));
            if (command.ExecuteNonQuery() != 1)
            {
                throw new KeyNotFoundException("The device does not exist.");
            }
        }

        StorageDeviceInfo result = ReadDevice(database.Connection, transaction, deviceId);
        transaction.Commit();
        return result;
    }

    private static NormalizedIdentity Normalize(StorageDeviceObservation observation)
    {
        string interfacePath = observation.InterfacePath.Trim().ToUpperInvariant();
        string[] hardwareIds = observation.HardwareIds
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string serial = observation.SerialNumber?.Trim().ToUpperInvariant() ?? string.Empty;
        string container = observation.ContainerId is { } value && value != Guid.Empty
            ? value.ToString("N")
            : string.Empty;
        return new NormalizedIdentity(interfacePath, hardwareIds, serial, container);
    }

    private static StorageDeviceIdentityAmbiguity GetAmbiguity(
        StorageDeviceObservation observation,
        NormalizedIdentity identity)
    {
        StorageDeviceIdentityAmbiguity result = StorageDeviceIdentityAmbiguity.None;
        if (identity.SerialNumber.Length == 0)
        {
            result |= StorageDeviceIdentityAmbiguity.MissingPermanentIdentifier;
        }

        if (observation.IsBluetooth)
        {
            result |= StorageDeviceIdentityAmbiguity.BluetoothTransport;
        }

        if (observation.IsRemoteDesktop)
        {
            result |= StorageDeviceIdentityAmbiguity.RemoteDesktop;
        }

        if (observation.IsVirtual)
        {
            result |= StorageDeviceIdentityAmbiguity.VirtualDevice;
        }

        if (observation.IsIndeterminate)
        {
            result |= StorageDeviceIdentityAmbiguity.IndeterminateTransport;
        }

        return result;
    }

    private static string Compose(params string[] values)
    {
        var builder = new StringBuilder();
        foreach (string value in values)
        {
            builder.Append(value.Length)
                .Append(':')
                .Append(value)
                .Append(';');
        }

        return builder.ToString();
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static InterfaceMatch? FindInterface(
        SqliteConnection connection,
        SqliteTransaction transaction,
        byte[] interfaceFingerprint)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT device_id, kind, hardware_fingerprint, stable_hardware_fingerprint
            FROM device_identity_mappings
            WHERE interface_fingerprint = $interface;
            """;
        command.Parameters.AddWithValue("$interface", interfaceFingerprint);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new InterfaceMatch(
            Guid.ParseExact(reader.GetString(0), "N"),
            (StorageDeviceKind)reader.GetInt32(1),
            reader.GetFieldValue<byte[]>(2),
            reader.IsDBNull(3) ? null : reader.GetFieldValue<byte[]>(3));
    }

    private static IReadOnlyList<Guid> FindStableHardware(
        SqliteConnection connection,
        SqliteTransaction transaction,
        byte[] stableHardwareFingerprint,
        StorageDeviceKind kind)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT DISTINCT device_id
            FROM device_identity_mappings
            WHERE stable_hardware_fingerprint = $stable AND kind = $kind
            LIMIT 2;
            """;
        command.Parameters.AddWithValue("$stable", stableHardwareFingerprint);
        command.Parameters.AddWithValue("$kind", (int)kind);
        using SqliteDataReader reader = command.ExecuteReader();
        var matches = new List<Guid>(2);
        while (reader.Read())
        {
            matches.Add(Guid.ParseExact(reader.GetString(0), "N"));
        }

        return matches;
    }

    private static IReadOnlyList<Guid> FindHardware(
        SqliteConnection connection,
        SqliteTransaction transaction,
        byte[] hardwareFingerprint,
        StorageDeviceKind kind)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT DISTINCT device_id
            FROM device_identity_mappings
            WHERE hardware_fingerprint = $hardware AND kind = $kind
            LIMIT 2;
            """;
        command.Parameters.AddWithValue("$hardware", hardwareFingerprint);
        command.Parameters.AddWithValue("$kind", (int)kind);
        using SqliteDataReader reader = command.ExecuteReader();
        var matches = new List<Guid>(2);
        while (reader.Read())
        {
            matches.Add(Guid.ParseExact(reader.GetString(0), "N"));
        }

        return matches;
    }

    private static void InsertDevice(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid deviceId,
        StorageDeviceKind kind,
        StorageDeviceIdentityConfidence confidence,
        StorageDeviceIdentityAmbiguity ambiguity,
        byte[] identityFingerprint,
        long observedUtcMicroseconds)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO devices(
                device_id, kind, identity_confidence, identity_fingerprint, alias,
                created_utc_us, identity_ambiguity, last_seen_utc_us)
            VALUES ($device, $kind, $confidence, $identity, NULL, $observed, $ambiguity, $observed);
            """;
        command.Parameters.AddWithValue("$device", deviceId.ToString("N"));
        command.Parameters.AddWithValue("$kind", (int)kind);
        command.Parameters.AddWithValue("$confidence", (int)confidence);
        command.Parameters.AddWithValue("$identity", identityFingerprint);
        command.Parameters.AddWithValue("$observed", observedUtcMicroseconds);
        command.Parameters.AddWithValue("$ambiguity", (int)ambiguity);
        command.ExecuteNonQuery();
    }

    private static void InsertMapping(
        SqliteConnection connection,
        SqliteTransaction transaction,
        byte[] interfaceFingerprint,
        Guid deviceId,
        StorageDeviceKind kind,
        byte[] hardwareFingerprint,
        byte[]? stableHardwareFingerprint,
        long observedUtcMicroseconds)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO device_identity_mappings(
                interface_fingerprint, device_id, kind, hardware_fingerprint,
                stable_hardware_fingerprint, first_seen_utc_us, last_seen_utc_us)
            VALUES ($interface, $device, $kind, $hardware, $stable, $observed, $observed);
            """;
        command.Parameters.AddWithValue("$interface", interfaceFingerprint);
        command.Parameters.AddWithValue("$device", deviceId.ToString("N"));
        command.Parameters.AddWithValue("$kind", (int)kind);
        command.Parameters.AddWithValue("$hardware", hardwareFingerprint);
        command.Parameters.AddWithValue("$stable", (object?)stableHardwareFingerprint ?? DBNull.Value);
        command.Parameters.AddWithValue("$observed", observedUtcMicroseconds);
        command.ExecuteNonQuery();
    }

    private static void Touch(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid deviceId,
        byte[] interfaceFingerprint,
        long observedUtcMicroseconds)
    {
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE device_identity_mappings
                SET last_seen_utc_us = MAX(last_seen_utc_us, $observed)
                WHERE interface_fingerprint = $interface;
                """;
            command.Parameters.AddWithValue("$observed", observedUtcMicroseconds);
            command.Parameters.AddWithValue("$interface", interfaceFingerprint);
            command.ExecuteNonQuery();
        }

        TouchDevice(connection, transaction, deviceId, observedUtcMicroseconds);
    }

    private static void TouchDevice(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid deviceId,
        long observedUtcMicroseconds)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE devices
            SET last_seen_utc_us = MAX(COALESCE(last_seen_utc_us, created_utc_us), $observed)
            WHERE device_id = $device;
            """;
        command.Parameters.AddWithValue("$observed", observedUtcMicroseconds);
        command.Parameters.AddWithValue("$device", deviceId.ToString("N"));
        command.ExecuteNonQuery();
    }

    private static void UpdateIdentityAssessment(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid deviceId,
        StorageDeviceIdentityConfidence confidence,
        StorageDeviceIdentityAmbiguity ambiguity)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE devices
            SET identity_confidence = CASE
                    WHEN (identity_ambiguity & $multiple) = 0 THEN $confidence
                    ELSE identity_confidence
                END,
                identity_ambiguity = CASE
                    WHEN (identity_ambiguity & $multiple) = 0 THEN $ambiguity
                    ELSE identity_ambiguity
                END
            WHERE device_id = $device;
            """;
        command.Parameters.AddWithValue("$confidence", (int)confidence);
        command.Parameters.AddWithValue("$ambiguity", (int)ambiguity);
        command.Parameters.AddWithValue("$multiple", (int)StorageDeviceIdentityAmbiguity.MultipleCandidates);
        command.Parameters.AddWithValue("$device", deviceId.ToString("N"));
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidDataException("A device identity mapping references a missing device.");
        }
    }

    private static StorageDeviceInfo ReadDevice(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid deviceId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT device_id, kind, identity_confidence, identity_ambiguity, alias,
                   created_utc_us, COALESCE(last_seen_utc_us, created_utc_us)
            FROM devices
            WHERE device_id = $device;
            """;
        command.Parameters.AddWithValue("$device", deviceId.ToString("N"));
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            throw new InvalidDataException("A device identity mapping references a missing device.");
        }

        return ReadDevice(reader);
    }

    private static StorageDeviceInfo ReadDevice(SqliteDataReader reader)
    {
        var kind = (StorageDeviceKind)reader.GetInt32(1);
        var confidence = (StorageDeviceIdentityConfidence)reader.GetInt32(2);
        var ambiguity = (StorageDeviceIdentityAmbiguity)reader.GetInt32(3);
        const StorageDeviceIdentityAmbiguity knownAmbiguity =
            StorageDeviceIdentityAmbiguity.MissingPermanentIdentifier
            | StorageDeviceIdentityAmbiguity.BluetoothTransport
            | StorageDeviceIdentityAmbiguity.RemoteDesktop
            | StorageDeviceIdentityAmbiguity.VirtualDevice
            | StorageDeviceIdentityAmbiguity.IndeterminateTransport
            | StorageDeviceIdentityAmbiguity.MultipleCandidates;
        if (!Enum.IsDefined(kind)
            || !Enum.IsDefined(confidence)
            || (ambiguity & ~knownAmbiguity) != 0)
        {
            throw new InvalidDataException("A device row contains an unsupported kind, confidence, or ambiguity value.");
        }

        return new StorageDeviceInfo(
            Guid.ParseExact(reader.GetString(0), "N"),
            kind,
            confidence,
            ambiguity,
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetInt64(5),
            reader.GetInt64(6));
    }

    private static bool NullableFingerprintsEqual(byte[]? left, byte[]? right) =>
        left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);

    private sealed record NormalizedIdentity(
        string InterfacePath,
        IReadOnlyList<string> HardwareIds,
        string SerialNumber,
        string ContainerId);

    private sealed record InterfaceMatch(
        Guid DeviceId,
        StorageDeviceKind Kind,
        byte[] HardwareFingerprint,
        byte[]? StableHardwareFingerprint);
}
