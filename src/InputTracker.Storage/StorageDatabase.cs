using Microsoft.Data.Sqlite;

namespace InputTracker.Storage;

public sealed class StorageDatabase : IDisposable
{
    private static readonly (StorageMouseButton Button, ushort DownMask)[] MouseButtonDownMasks =
    [
        (StorageMouseButton.Left, 1 << 0),
        (StorageMouseButton.Right, 1 << 2),
        (StorageMouseButton.Middle, 1 << 4),
        (StorageMouseButton.X1, 1 << 6),
        (StorageMouseButton.X2, 1 << 8)
    ];

    private readonly SqliteConnection _connection;
    private readonly FileStream? _writerGuard;

    private StorageDatabase(
        StorageLayout layout,
        SqliteConnection connection,
        string sqliteVersion,
        FileStream? writerGuard)
    {
        Layout = layout;
        _connection = connection;
        SqliteVersion = sqliteVersion;
        _writerGuard = writerGuard;
    }

    public StorageLayout Layout { get; }
    public string SqliteVersion { get; }
    public int SchemaVersion => ExecuteScalar<int>("PRAGMA user_version;");
    public string JournalMode => ExecuteScalar<string>("PRAGMA journal_mode;");
    public int Synchronous => ExecuteScalar<int>("PRAGMA synchronous;");
    public bool ForeignKeysEnabled => ExecuteScalar<int>("PRAGMA foreign_keys;") == 1;
    public bool SecureDeleteEnabled => ExecuteScalar<int>("PRAGMA secure_delete;") == 1;
    public int BusyTimeoutMilliseconds => ExecuteScalar<int>("PRAGMA busy_timeout;");

    public static StorageDatabase Open(StorageLayout layout, IStorageClock? clock = null) =>
        OpenCore(layout, clock, ownerLock: null);

    internal static StorageDatabase OpenOwned(
        StorageLayout layout,
        IStorageClock clock,
        FileStream ownerLock)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(ownerLock);
        string expectedLockPath = Path.GetFullPath(Path.Combine(
            layout.StorageDirectory,
            "storage-service.writer.lock"));
        if (!ownerLock.CanWrite
            || !string.Equals(Path.GetFullPath(ownerLock.Name), expectedLockPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The supplied Storage owner lock does not belong to this store.");
        }

        return OpenCore(layout, clock, ownerLock);
    }

    private static StorageDatabase OpenCore(
        StorageLayout layout,
        IStorageClock? clock,
        FileStream? ownerLock)
    {
        ArgumentNullException.ThrowIfNull(layout);
        clock ??= SystemStorageClock.Instance;
        FileStream? writerGuard = null;
        if (ownerLock is null)
        {
            string lockPath = Path.Combine(layout.StorageDirectory, "storage-service.writer.lock");
            writerGuard = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.Read,
                1,
                FileOptions.WriteThrough);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = layout.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };
        SqliteConnection? connection = null;
        try
        {
            connection = new SqliteConnection(builder.ToString());
            connection.Open();
            Configure(connection);
            ApplyMigrations(connection, clock);
            string sqliteVersion = Scalar<string>(connection, "SELECT sqlite_version();");
            return new StorageDatabase(layout, connection, sqliteVersion, writerGuard);
        }
        catch
        {
            connection?.Dispose();
            writerGuard?.Dispose();
            throw;
        }
    }

    public long RecordCalibration(TimeCalibrationSegment segment)
    {
        using SqliteTransaction transaction = _connection.BeginTransaction();
        InsertCalibration(_connection, transaction, segment);

        long id = Scalar<long>(_connection, "SELECT last_insert_rowid();", transaction);
        transaction.Commit();
        return id;
    }

    public void RegisterDevice(Guid deviceId, int kind, int identityConfidence, long createdUtcMicroseconds)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO devices(device_id, kind, identity_confidence, created_utc_us)
            VALUES ($device, $kind, $confidence, $created);
            """;
        command.Parameters.AddWithValue("$device", deviceId.ToString("N"));
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$confidence", identityConfidence);
        command.Parameters.AddWithValue("$created", createdUtcMicroseconds);
        command.ExecuteNonQuery();
    }

    public StorageDeviceResolution ObserveDevice(
        StorageDeviceObservation observation,
        long observedUtcMicroseconds) =>
        StorageDeviceCatalog.Observe(this, observation, observedUtcMicroseconds);

    public IReadOnlyList<StorageDeviceInfo> ListDevices() => StorageDeviceCatalog.ReadAll(this);

    public StorageDeviceInfo SetDeviceAlias(Guid deviceId, string? alias) =>
        StorageDeviceCatalog.SetAlias(this, deviceId, alias);

    public StorageGamepadConfiguration SetGamepadConfiguration(
        Guid deviceId,
        StorageGamepadLayout layout,
        bool enabled,
        IReadOnlyList<StorageGamepadHidBinding> bindings,
        long createdUtcMicroseconds)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        ValidateGamepadConfiguration(deviceId, layout, bindings, createdUtcMicroseconds);
        using SqliteTransaction transaction = _connection.BeginTransaction();
        EnsureDeviceKind(_connection, transaction, deviceId, StorageDeviceKind.Gamepad);
        int revision;
        using (SqliteCommand current = _connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = "SELECT revision FROM gamepad_configuration_current WHERE device_id = $device;";
            current.Parameters.AddWithValue("$device", deviceId.ToString("N"));
            object? value = current.ExecuteScalar();
            revision = value is long previous ? checked((int)previous + 1) : 1;
        }

        using (SqliteCommand config = _connection.CreateCommand())
        {
            config.Transaction = transaction;
            config.CommandText = """
                INSERT INTO gamepad_configuration_revisions(device_id, revision, layout, enabled, created_utc_us)
                VALUES ($device, $revision, $layout, $enabled, $created);
                INSERT INTO gamepad_configuration_current(device_id, revision)
                VALUES ($device, $revision)
                ON CONFLICT(device_id) DO UPDATE SET revision = excluded.revision;
                """;
            config.Parameters.AddWithValue("$device", deviceId.ToString("N"));
            config.Parameters.AddWithValue("$revision", revision);
            config.Parameters.AddWithValue("$layout", (int)layout);
            config.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
            config.Parameters.AddWithValue("$created", createdUtcMicroseconds);
            config.ExecuteNonQuery();
        }

        foreach (StorageGamepadHidBinding binding in bindings)
        {
            if (binding.ActiveValues.Count == 0)
            {
                using SqliteCommand axis = _connection.CreateCommand();
                axis.Transaction = transaction;
                axis.CommandText = """
                    INSERT INTO gamepad_hid_axis_bindings(
                        device_id, revision, button, usage_page, usage, link_collection, element_index,
                        activation_threshold, release_threshold, axis_direction)
                    VALUES ($device, $revision, $button, $page, $usage, $collection, $element, $activation, $release, $direction);
                    """;
                axis.Parameters.AddWithValue("$device", deviceId.ToString("N"));
                axis.Parameters.AddWithValue("$revision", revision);
                axis.Parameters.AddWithValue("$button", (int)binding.Button);
                axis.Parameters.AddWithValue("$page", (int)binding.UsagePage);
                axis.Parameters.AddWithValue("$usage", (int)binding.Usage);
                axis.Parameters.AddWithValue("$collection", (int)binding.LinkCollection);
                axis.Parameters.AddWithValue("$element", (int)binding.ElementIndex);
                axis.Parameters.AddWithValue("$activation", binding.ActivationThreshold!.Value);
                axis.Parameters.AddWithValue("$release", binding.ReleaseThreshold!.Value);
                axis.Parameters.AddWithValue("$direction", (int)binding.AxisDirection!.Value);
                axis.ExecuteNonQuery();
                continue;
            }
            foreach (int activeValue in binding.ActiveValues.Distinct())
            {
                using SqliteCommand insert = _connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO gamepad_hid_bindings(
                        device_id, revision, button, usage_page, usage, link_collection, element_index, active_value)
                    VALUES ($device, $revision, $button, $page, $usage, $collection, $element, $value);
                    """;
                insert.Parameters.AddWithValue("$device", deviceId.ToString("N"));
                insert.Parameters.AddWithValue("$revision", revision);
                insert.Parameters.AddWithValue("$button", (int)binding.Button);
                insert.Parameters.AddWithValue("$page", (int)binding.UsagePage);
                insert.Parameters.AddWithValue("$usage", (int)binding.Usage);
                insert.Parameters.AddWithValue("$collection", (int)binding.LinkCollection);
                insert.Parameters.AddWithValue("$element", (int)binding.ElementIndex);
                insert.Parameters.AddWithValue("$value", activeValue);
                insert.ExecuteNonQuery();
            }
        }

        transaction.Commit();
        return GetGamepadConfiguration(deviceId) ?? throw new InvalidDataException("Saved gamepad configuration is missing.");
    }

    public StorageGamepadConfiguration? GetGamepadConfiguration(Guid deviceId)
    {
        EnsureDeviceKind(_connection, transaction: null, deviceId, StorageDeviceKind.Gamepad);
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = """
            SELECT r.revision, r.layout, r.enabled
            FROM gamepad_configuration_current c
            JOIN gamepad_configuration_revisions r
              ON r.device_id = c.device_id AND r.revision = c.revision
            WHERE c.device_id = $device;
            """;
        command.Parameters.AddWithValue("$device", deviceId.ToString("N"));
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        int revision = checked((int)reader.GetInt64(0));
        var layout = (StorageGamepadLayout)reader.GetInt32(1);
        long enabled = reader.GetInt64(2);
        if (!Enum.IsDefined(layout) || (enabled != 0 && enabled != 1))
            throw new InvalidDataException("Stored gamepad configuration is invalid.");
        reader.Close();

        using SqliteCommand bindings = _connection.CreateCommand();
        bindings.CommandText = """
            SELECT button, usage_page, usage, link_collection, element_index, active_value
            FROM gamepad_hid_bindings
            WHERE device_id = $device AND revision = $revision
            ORDER BY button, usage_page, usage, link_collection, element_index, active_value;
            """;
        bindings.Parameters.AddWithValue("$device", deviceId.ToString("N"));
        bindings.Parameters.AddWithValue("$revision", revision);
        using SqliteDataReader bindingReader = bindings.ExecuteReader();
        var grouped = new Dictionary<(StorageGamepadButton Button, ushort Page, ushort Usage, ushort Collection, ushort Element), List<int>>();
        while (bindingReader.Read())
        {
            long rawButton = bindingReader.GetInt64(0);
            if (rawButton is < 0 or > 15) throw new InvalidDataException("Stored gamepad binding button is invalid.");
            var key = ((StorageGamepadButton)rawButton,
                checked((ushort)bindingReader.GetInt64(1)),
                checked((ushort)bindingReader.GetInt64(2)),
                checked((ushort)bindingReader.GetInt64(3)),
                checked((ushort)bindingReader.GetInt64(4)));
            if (!grouped.TryGetValue(key, out List<int>? values)) grouped.Add(key, values = []);
            values.Add(checked((int)bindingReader.GetInt64(5)));
        }
        var result = grouped.Select(pair => new StorageGamepadHidBinding(
            pair.Key.Button, pair.Key.Page, pair.Key.Usage, pair.Key.Collection, pair.Key.Element, pair.Value)).ToList();
        using (SqliteCommand axes = _connection.CreateCommand())
        {
            axes.CommandText = """
                SELECT button, usage_page, usage, link_collection, element_index,
                       activation_threshold, release_threshold, axis_direction
                FROM gamepad_hid_axis_bindings WHERE device_id = $device AND revision = $revision
                ORDER BY button, usage_page, usage, link_collection, element_index;
                """;
            axes.Parameters.AddWithValue("$device", deviceId.ToString("N"));
            axes.Parameters.AddWithValue("$revision", revision);
            using SqliteDataReader axisReader = axes.ExecuteReader();
            while (axisReader.Read())
            {
                long rawButton = axisReader.GetInt64(0);
                long rawDirection = axisReader.GetInt64(7);
                int activation = checked((int)axisReader.GetInt64(5));
                int release = checked((int)axisReader.GetInt64(6));
                if (rawButton is < 0 or > 15 || rawDirection is < 0 or > 1
                    || rawDirection == 0 && activation <= release
                    || rawDirection == 1 && activation >= release)
                    throw new InvalidDataException("Stored gamepad axis binding is invalid.");
                result.Add(new StorageGamepadHidBinding(
                    (StorageGamepadButton)rawButton,
                    checked((ushort)axisReader.GetInt64(1)),
                    checked((ushort)axisReader.GetInt64(2)),
                    checked((ushort)axisReader.GetInt64(3)),
                    checked((ushort)axisReader.GetInt64(4)),
                    [], activation, release, (StorageGamepadAxisDirection)rawDirection));
            }
        }
        return new StorageGamepadConfiguration(deviceId, revision, layout, enabled == 1, result);
    }

    internal StorageServiceOrdinals CommitGamepadPress(
        StorageGamepadPress press,
        DateOnly captureLocalDate,
        string timeZoneId,
        IStorageServiceFaultInjector faultInjector)
    {
        if (press.DeviceId == Guid.Empty || !Enum.IsDefined(press.Button)
            || string.IsNullOrWhiteSpace(timeZoneId))
            throw new InvalidDataException("A gamepad press is invalid.");
        using SqliteTransaction transaction = _connection.BeginTransaction();
        using SqliteCommand kind = _connection.CreateCommand();
        kind.Transaction = transaction;
        kind.CommandText = """
            SELECT r.revision, r.enabled FROM devices d
            JOIN gamepad_configuration_current c ON c.device_id = d.device_id
            JOIN gamepad_configuration_revisions r ON r.device_id = c.device_id AND r.revision = c.revision
            WHERE d.device_id = $device AND d.kind = $kind;
            """;
        kind.Parameters.AddWithValue("$device", press.DeviceId.ToString("N"));
        kind.Parameters.AddWithValue("$kind", (int)StorageDeviceKind.Gamepad);
        using SqliteDataReader config = kind.ExecuteReader();
        if (!config.Read())
            throw new InvalidDataException("A gamepad press references a device without a configuration.");
        int revision = checked((int)config.GetInt64(0));
        bool enabled = config.GetInt64(1) == 1;
        config.Close();
        if (!enabled)
        {
            transaction.Rollback();
            return ReadStorageServiceOrdinals();
        }

        using (SqliteCommand mapped = _connection.CreateCommand())
        {
            mapped.Transaction = transaction;
            mapped.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM gamepad_hid_bindings
                     WHERE device_id = $device AND revision = $revision AND button = $button)
                  + (SELECT COUNT(*) FROM gamepad_hid_axis_bindings
                     WHERE device_id = $device AND revision = $revision AND button = $button);
                """;
            mapped.Parameters.AddWithValue("$device", press.DeviceId.ToString("N"));
            mapped.Parameters.AddWithValue("$revision", revision);
            mapped.Parameters.AddWithValue("$button", (int)press.Button);
            if (Convert.ToInt64(mapped.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0)
                throw new InvalidDataException("A gamepad press button is not present in its calibrated configuration.");
        }

        using SqliteCommand aggregate = _connection.CreateCommand();
        aggregate.Transaction = transaction;
        aggregate.CommandText = """
            INSERT INTO gamepad_daily(device_id, configuration_revision, button, capture_local_date, time_zone_id, press_count)
            VALUES ($device, $revision, $button, $date, $zone, 1)
            ON CONFLICT(device_id, configuration_revision, button, capture_local_date, time_zone_id)
            DO UPDATE SET press_count = press_count + 1;
            """;
        aggregate.Parameters.AddWithValue("$device", press.DeviceId.ToString("N"));
        aggregate.Parameters.AddWithValue("$revision", revision);
        aggregate.Parameters.AddWithValue("$button", (int)press.Button);
        aggregate.Parameters.AddWithValue("$date", captureLocalDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        aggregate.Parameters.AddWithValue("$zone", timeZoneId);
        aggregate.ExecuteNonQuery();
        StorageServiceOrdinals current = ReadStorageServiceOrdinals(_connection, transaction);
        var next = new StorageServiceOrdinals(checked(current.CommitOrdinal + 1), current.KeyboardAggregateCommitOrdinal);
        WriteStorageServiceOrdinals(_connection, transaction, next);
        faultInjector.BeforeStorageCommit(boundary: null, keyboardRowCount: 0);
        transaction.Commit();
        return next;
    }

    private static void ValidateGamepadConfiguration(
        Guid deviceId,
        StorageGamepadLayout layout,
        IReadOnlyList<StorageGamepadHidBinding> bindings,
        long createdUtcMicroseconds)
    {
        if (deviceId == Guid.Empty || !Enum.IsDefined(layout) || createdUtcMicroseconds < 0)
            throw new ArgumentException("Gamepad configuration fields or bindings are invalid.", nameof(bindings));
        var identities = new HashSet<(StorageGamepadButton, ushort, ushort, ushort, ushort)>();
        foreach (StorageGamepadHidBinding binding in bindings)
        {
            if (binding is null || !Enum.IsDefined(binding.Button) || binding.ActiveValues is null
                || !identities.Add((binding.Button, binding.UsagePage, binding.Usage,
                    binding.LinkCollection, binding.ElementIndex)))
                throw new ArgumentException("Gamepad binding identity is invalid or duplicated.", nameof(bindings));
            if (binding.ActiveValues.Count > 0)
            {
                if (binding.ActivationThreshold is not null || binding.ReleaseThreshold is not null
                    || binding.AxisDirection is not null
                    || binding.ActiveValues.Distinct().Count() != binding.ActiveValues.Count)
                    throw new ArgumentException("Digital gamepad bindings require unique active values only.", nameof(bindings));
                continue;
            }

            if (binding.ActivationThreshold is not int activation
                || binding.ReleaseThreshold is not int release
                || binding.AxisDirection is not StorageGamepadAxisDirection direction
                || !Enum.IsDefined(direction)
                || direction == StorageGamepadAxisDirection.Increasing && activation <= release
                || direction == StorageGamepadAxisDirection.Decreasing && activation >= release)
                throw new ArgumentException("Axis gamepad bindings require ordered activation and release thresholds.", nameof(bindings));
        }
    }

    private static void EnsureDeviceKind(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid deviceId,
        StorageDeviceKind expected)
    {
        if (deviceId == Guid.Empty) throw new ArgumentException("Device identifier must be non-empty.", nameof(deviceId));
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT kind FROM devices WHERE device_id = $device;";
        command.Parameters.AddWithValue("$device", deviceId.ToString("N"));
        if (command.ExecuteScalar() is not long kind || kind != (long)expected)
            throw new InvalidDataException("The device has an unexpected kind or is missing.");
    }

    public void UpsertKeyboardDaily(
        Guid deviceId,
        ushort makeCode,
        bool isE0,
        bool isE1,
        DateOnly captureLocalDate,
        string timeZoneId,
        long pressCount,
        long holdDurationMicroseconds)
    {
        if (isE0 && isE1)
        {
            throw new ArgumentException("A physical key cannot use both E0 and E1 prefixes.");
        }

        int physicalKey = makeCode | (isE0 ? 1 << 16 : 0) | (isE1 ? 1 << 17 : 0);
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO keyboard_daily(
                device_id, physical_key, capture_local_date, time_zone_id, press_count, hold_duration_us)
            VALUES ($device, $key, $date, $zone, $count, $duration)
            ON CONFLICT(device_id, physical_key, capture_local_date, time_zone_id)
            DO UPDATE SET
                press_count = press_count + excluded.press_count,
                hold_duration_us = hold_duration_us + excluded.hold_duration_us;
            """;
        command.Parameters.AddWithValue("$device", deviceId.ToString("N"));
        command.Parameters.AddWithValue("$key", physicalKey);
        command.Parameters.AddWithValue("$date", captureLocalDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$zone", timeZoneId);
        command.Parameters.AddWithValue("$count", pressCount);
        command.Parameters.AddWithValue("$duration", holdDurationMicroseconds);
        command.ExecuteNonQuery();
    }

    internal StorageServiceOrdinals ReadStorageServiceOrdinals() =>
        ReadStorageServiceOrdinals(_connection, transaction: null);

    internal IReadOnlyDictionary<Guid, long> ReadMouseDeviceWatermarks()
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = "SELECT device_id, durable_sequence FROM mouse_device_watermarks ORDER BY device_id;";
        using SqliteDataReader reader = command.ExecuteReader();
        var result = new Dictionary<Guid, long>();
        while (reader.Read())
        {
            if (!Guid.TryParseExact(reader.GetString(0), "N", out Guid deviceId)
                || deviceId == Guid.Empty
                || reader.GetInt64(1) < 0
                || !result.TryAdd(deviceId, reader.GetInt64(1)))
            {
                throw new InvalidDataException("Mouse device watermark contains invalid data.");
            }
        }

        return result;
    }

    internal bool IsRegisteredMouse(Guid deviceId)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = "SELECT kind FROM devices WHERE device_id = $device;";
        command.Parameters.AddWithValue("$device", deviceId.ToString("N"));
        object? value = command.ExecuteScalar();
        return value is long kind && kind == (long)StorageDeviceKind.Mouse;
    }

    internal void ValidateMouseSpoolIdentity(MouseSpoolHeader header)
    {
        using SqliteTransaction transaction = _connection.BeginTransaction();
        ValidateMouseSpoolForeignKeys(_connection, transaction, header);
        transaction.Rollback();
    }

    internal bool IsMouseV3AdoptionComplete()
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = "SELECT value FROM store_metadata WHERE key = 'mouse.v3-adoption-complete';";
        object? value = command.ExecuteScalar();
        return value switch
        {
            "0" => false,
            "1" => true,
            _ => throw new InvalidDataException("Mouse v3 adoption marker is missing or invalid.")
        };
    }

    internal void CompleteMouseV3Adoption()
    {
        using SqliteTransaction transaction = _connection.BeginTransaction();
        using SqliteCommand command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE store_metadata
            SET value = '1'
            WHERE key = 'mouse.v3-adoption-complete' AND value = '0';
            """;
        if (command.ExecuteNonQuery() != 1)
        {
            throw new InvalidDataException("Mouse v3 adoption marker did not make the required atomic transition.");
        }

        transaction.Commit();
        if (!IsMouseV3AdoptionComplete())
        {
            throw new InvalidDataException("Mouse v3 adoption marker did not persist completion.");
        }
    }

    internal long FindCalibrationSegmentId(TimeCalibrationSegment calibration)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = """
            SELECT segment_id
            FROM time_calibration_segments
            WHERE session_id = $session
              AND monotonic_anchor = $monotonic
              AND utc_anchor_us = $utc
              AND uncertainty_ticks = $uncertainty
              AND time_zone_id = $zone
            ORDER BY segment_id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$session", calibration.SessionId.ToString("N"));
        command.Parameters.AddWithValue("$monotonic", calibration.MonotonicAnchor);
        command.Parameters.AddWithValue("$utc", calibration.UtcAnchorMicroseconds);
        command.Parameters.AddWithValue("$uncertainty", calibration.SamplingUncertaintyTicks);
        command.Parameters.AddWithValue("$zone", calibration.TimeZoneId);
        object? result = command.ExecuteScalar();
        if (result is not long segmentId || segmentId <= 0)
        {
            throw new InvalidDataException("Active calibration segment was not persisted.");
        }

        return segmentId;
    }

    internal bool CalibrationSegmentMatches(long segmentId, TimeCalibrationSegment calibration)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM time_calibration_segments c
            JOIN clock_sessions s ON s.session_id = c.session_id
            WHERE c.segment_id = $segment
              AND c.session_id = $session
              AND s.frequency = $frequency
              AND monotonic_anchor = $monotonic
              AND utc_anchor_us = $utc
              AND uncertainty_ticks = $uncertainty
              AND time_zone_id = $zone;
            """;
        command.Parameters.AddWithValue("$segment", segmentId);
        command.Parameters.AddWithValue("$session", calibration.SessionId.ToString("N"));
        command.Parameters.AddWithValue("$frequency", calibration.Frequency);
        command.Parameters.AddWithValue("$monotonic", calibration.MonotonicAnchor);
        command.Parameters.AddWithValue("$utc", calibration.UtcAnchorMicroseconds);
        command.Parameters.AddWithValue("$uncertainty", calibration.SamplingUncertaintyTicks);
        command.Parameters.AddWithValue("$zone", calibration.TimeZoneId);
        return (long)command.ExecuteScalar()! == 1;
    }

    internal StorageMousePrefixCommit CommitMouseSpoolSegment(
        MouseSpoolSegment segment,
        long updatedUtcMicroseconds,
        LegacyMouseSegmentBoundaryReason sealReason = LegacyMouseSegmentBoundaryReason.LegacyUnknown)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (sealReason is < LegacyMouseSegmentBoundaryReason.RangeStart
            or > LegacyMouseSegmentBoundaryReason.RecoveryUnknown)
        {
            throw new ArgumentOutOfRangeException(nameof(sealReason));
        }
        if (segment.Records.Count == 0
            || segment.ValidLength != MouseSpoolHeaderCodec.HeaderSize
                + (long)segment.Records.Count * MouseSpoolRecordCodec.RecordSize
            || segment.Header.FirstSequence != segment.Records[0].Sequence
            || segment.Records[^1].Sequence != checked(segment.Header.FirstSequence + segment.Records.Count - 1))
        {
            throw new InvalidDataException("Mouse spool segment range or length is invalid.");
        }

        string relativePath = segment.Header.SegmentId.ToString("N") + ".mspool";
        string root = Path.GetFullPath(Layout.SpoolDirectory);
        string rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string expectedPath = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!expectedPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expectedPath, Path.GetFullPath(segment.Path), StringComparison.OrdinalIgnoreCase)
            || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0
            || (File.GetAttributes(expectedPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("Mouse spool path is outside the contained spool directory.");
        }

        MouseSpoolSegment verified = MouseSpoolSegmentReader.Read(expectedPath);
        if (verified.Header != segment.Header
            || verified.ValidLength != segment.ValidLength
            || !string.Equals(verified.SpoolFileSha256, segment.SpoolFileSha256, StringComparison.Ordinal)
            || !verified.Records.SequenceEqual(segment.Records))
        {
            throw new InvalidDataException("Mouse spool changed before directory publication.");
        }

        MouseChunkEncoded canonical = MouseChunkCodec.Encode(
            Guid.NewGuid(),
            segment.Header.DeviceId,
            segment.Header.CalibrationSegmentId,
            segment.Records);
        using SqliteTransaction transaction = _connection.BeginTransaction();
        ValidateMouseSpoolForeignKeys(_connection, transaction, segment.Header);
        long currentDeviceSequence = ReadMouseDeviceWatermark(
            _connection, transaction, segment.Header.DeviceId);
        if (segment.Header.FirstSequence != checked(currentDeviceSequence + 1))
        {
            throw new InvalidDataException("Mouse spool range does not begin at the next device-global sequence.");
        }

        using (SqliteCommand overlap = _connection.CreateCommand())
        {
            overlap.Transaction = transaction;
            overlap.CommandText = """
                SELECT 1 FROM mouse_spool_segments
                WHERE device_id = $device
                  AND first_sequence <= $last
                  AND last_sequence >= $first
                LIMIT 1;
                """;
            overlap.Parameters.AddWithValue("$device", segment.Header.DeviceId.ToString("N"));
            overlap.Parameters.AddWithValue("$first", segment.Records[0].Sequence);
            overlap.Parameters.AddWithValue("$last", segment.Records[^1].Sequence);
            if (overlap.ExecuteScalar() is not null)
            {
                throw new InvalidDataException("Mouse spool range overlaps a published device-global range.");
            }
        }

        using (SqliteCommand insert = _connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO mouse_spool_segments(
                    spool_segment_id, device_id, calibration_segment_id, relative_path,
                    first_sequence, last_sequence,
                    first_monotonic_timestamp, last_monotonic_timestamp,
                    record_count, committed_length, spool_file_sha256, canonical_sha256, created_utc_us, seal_reason)
                VALUES (
                    $segment, $device, $calibration, $path,
                    $first, $last, $firstTimestamp, $lastTimestamp,
                    $count, $length, $fileHash, $canonicalHash, $created, $sealReason);
                """;
            insert.Parameters.AddWithValue("$segment", segment.Header.SegmentId.ToString("N"));
            insert.Parameters.AddWithValue("$device", segment.Header.DeviceId.ToString("N"));
            insert.Parameters.AddWithValue("$calibration", segment.Header.CalibrationSegmentId);
            insert.Parameters.AddWithValue("$path", relativePath);
            insert.Parameters.AddWithValue("$first", segment.Records[0].Sequence);
            insert.Parameters.AddWithValue("$last", segment.Records[^1].Sequence);
            insert.Parameters.AddWithValue("$firstTimestamp", segment.Records[0].MonotonicTimestamp);
            insert.Parameters.AddWithValue("$lastTimestamp", segment.Records[^1].MonotonicTimestamp);
            insert.Parameters.AddWithValue("$count", segment.Records.Count);
            insert.Parameters.AddWithValue("$length", segment.ValidLength);
            insert.Parameters.AddWithValue("$fileHash", segment.SpoolFileSha256);
            insert.Parameters.AddWithValue("$canonicalHash", canonical.CanonicalSha256);
            insert.Parameters.AddWithValue("$created", updatedUtcMicroseconds);
            insert.Parameters.AddWithValue("$sealReason", (int)sealReason);
            insert.ExecuteNonQuery();
        }

        AggregateMouseButtons(_connection, transaction, segment);

        long nextDeviceSequence = segment.Records[^1].Sequence;
        using (SqliteCommand watermark = _connection.CreateCommand())
        {
            watermark.Transaction = transaction;
            watermark.CommandText = """
                INSERT INTO mouse_device_watermarks(device_id, durable_sequence, updated_utc_us)
                VALUES ($device, $sequence, $updated)
                ON CONFLICT(device_id) DO UPDATE SET
                    durable_sequence = excluded.durable_sequence,
                    updated_utc_us = excluded.updated_utc_us;
                """;
            watermark.Parameters.AddWithValue("$device", segment.Header.DeviceId.ToString("N"));
            watermark.Parameters.AddWithValue("$sequence", nextDeviceSequence);
            watermark.Parameters.AddWithValue("$updated", updatedUtcMicroseconds);
            watermark.ExecuteNonQuery();
        }

        StorageServiceOrdinals current = ReadStorageServiceOrdinals(_connection, transaction);
        var next = new StorageServiceOrdinals(
            checked(current.CommitOrdinal + 1),
            current.KeyboardAggregateCommitOrdinal);
        WriteStorageServiceOrdinals(_connection, transaction, next);
        transaction.Commit();
        return new StorageMousePrefixCommit(
            segment.Header.DeviceId,
            nextDeviceSequence,
            next.CommitOrdinal,
            next.KeyboardAggregateCommitOrdinal);
    }

    internal void BackfillMouseButtonAggregates(MouseSpoolSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (segment.Records.Count == 0)
        {
            return;
        }

        using SqliteTransaction transaction = _connection.BeginTransaction();
        if (MouseButtonsAlreadyAggregated(_connection, transaction, segment.Header.SegmentId))
        {
            transaction.Rollback();
            return;
        }

        MouseChunkEncoded canonical = MouseChunkCodec.Encode(
            Guid.NewGuid(),
            segment.Header.DeviceId,
            segment.Header.CalibrationSegmentId,
            segment.Records);
        using (SqliteCommand published = _connection.CreateCommand())
        {
            published.Transaction = transaction;
            published.CommandText = """
                SELECT COUNT(*)
                FROM mouse_spool_segments
                WHERE spool_segment_id = $segment
                  AND device_id = $device
                  AND calibration_segment_id = $calibration
                  AND first_sequence = $first
                  AND last_sequence = $last
                  AND record_count = $count
                  AND committed_length = $length
                  AND spool_file_sha256 = $fileHash
                  AND canonical_sha256 = $canonicalHash;
                """;
            published.Parameters.AddWithValue("$segment", segment.Header.SegmentId.ToString("N"));
            published.Parameters.AddWithValue("$device", segment.Header.DeviceId.ToString("N"));
            published.Parameters.AddWithValue("$calibration", segment.Header.CalibrationSegmentId);
            published.Parameters.AddWithValue("$first", segment.Records[0].Sequence);
            published.Parameters.AddWithValue("$last", segment.Records[^1].Sequence);
            published.Parameters.AddWithValue("$count", segment.Records.Count);
            published.Parameters.AddWithValue("$length", segment.ValidLength);
            published.Parameters.AddWithValue("$fileHash", segment.SpoolFileSha256);
            published.Parameters.AddWithValue("$canonicalHash", canonical.CanonicalSha256);
            if ((long)published.ExecuteScalar()! != 1)
            {
                throw new InvalidDataException("Mouse button backfill source is not an exact published spool segment.");
            }
        }

        AggregateMouseButtons(_connection, transaction, segment);
        transaction.Commit();
    }

    internal StorageServiceOrdinals CommitStorageServiceBatch(
        IReadOnlyList<StorageKeyboardAggregateDelta> deltas,
        TimeCalibrationSegment? calibration,
        StorageLifecycleBoundary? boundary,
        IStorageServiceFaultInjector faultInjector)
    {
        ArgumentNullException.ThrowIfNull(deltas);
        ArgumentNullException.ThrowIfNull(faultInjector);
        using SqliteTransaction transaction = _connection.BeginTransaction();
        if (calibration is { } segment)
        {
            InsertCalibration(_connection, transaction, segment);
        }

        foreach (StorageKeyboardAggregateDelta delta in deltas)
        {
            if (delta.DeviceId == Guid.Empty
                || delta.IsE0 && delta.IsE1
                || delta.PressCount < 0
                || delta.HoldDurationMicroseconds < 0
                || delta.PressCount == 0 && delta.HoldDurationMicroseconds == 0)
            {
                throw new InvalidDataException("A keyboard aggregate delta is invalid.");
            }

            using SqliteCommand command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO keyboard_daily(
                    device_id, physical_key, capture_local_date, time_zone_id, press_count, hold_duration_us)
                VALUES ($device, $key, $date, $zone, $count, $duration)
                ON CONFLICT(device_id, physical_key, capture_local_date, time_zone_id)
                DO UPDATE SET
                    press_count = press_count + excluded.press_count,
                    hold_duration_us = hold_duration_us + excluded.hold_duration_us;
                """;
            command.Parameters.AddWithValue("$device", delta.DeviceId.ToString("N"));
            command.Parameters.AddWithValue("$key", PackPhysicalKey(delta.MakeCode, delta.IsE0, delta.IsE1));
            command.Parameters.AddWithValue("$date", delta.CaptureLocalDate.ToString(
                "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$zone", delta.TimeZoneId);
            command.Parameters.AddWithValue("$count", delta.PressCount);
            command.Parameters.AddWithValue("$duration", delta.HoldDurationMicroseconds);
            command.ExecuteNonQuery();
        }

        StorageServiceOrdinals current = ReadStorageServiceOrdinals(_connection, transaction);
        StorageServiceOrdinals next = current;
        if (deltas.Count > 0 || calibration is not null)
        {
            next = new StorageServiceOrdinals(
                checked(current.CommitOrdinal + 1),
                deltas.Count > 0
                    ? checked(current.KeyboardAggregateCommitOrdinal + 1)
                    : current.KeyboardAggregateCommitOrdinal);
            WriteStorageServiceOrdinals(_connection, transaction, next);
        }

        faultInjector.BeforeStorageCommit(boundary, deltas.Count);
        transaction.Commit();
        return next;
    }

    internal StorageServiceOrdinals CommitMouseButtonPresses(
        Guid deviceId,
        DateOnly captureLocalDate,
        string timeZoneId,
        IReadOnlyList<StorageMouseButton> buttons,
        IStorageServiceFaultInjector faultInjector)
    {
        ArgumentNullException.ThrowIfNull(buttons);
        ArgumentNullException.ThrowIfNull(faultInjector);
        if (deviceId == Guid.Empty
            || string.IsNullOrWhiteSpace(timeZoneId)
            || buttons.Count == 0
            || buttons.Distinct().Count() != buttons.Count
            || buttons.Any(button => button is < StorageMouseButton.Left or > StorageMouseButton.X2))
        {
            throw new InvalidDataException("A mouse button aggregate is invalid.");
        }

        using SqliteTransaction transaction = _connection.BeginTransaction();
        using (SqliteCommand device = _connection.CreateCommand())
        {
            device.Transaction = transaction;
            device.CommandText = "SELECT kind FROM devices WHERE device_id = $device;";
            device.Parameters.AddWithValue("$device", deviceId.ToString("N"));
            if (device.ExecuteScalar() is not long kind || kind != (long)StorageDeviceKind.Mouse)
            {
                throw new InvalidDataException("A mouse button aggregate references a non-mouse device.");
            }
        }

        foreach (StorageMouseButton button in buttons)
        {
            using SqliteCommand aggregate = _connection.CreateCommand();
            aggregate.Transaction = transaction;
            aggregate.CommandText = """
                INSERT INTO mouse_button_daily(
                    device_id, button, capture_local_date, time_zone_id, press_count)
                VALUES ($device, $button, $date, $zone, 1)
                ON CONFLICT(device_id, button, capture_local_date, time_zone_id)
                DO UPDATE SET press_count = press_count + 1;
                """;
            aggregate.Parameters.AddWithValue("$device", deviceId.ToString("N"));
            aggregate.Parameters.AddWithValue("$button", (int)button);
            aggregate.Parameters.AddWithValue("$date", captureLocalDate.ToString(
                "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture));
            aggregate.Parameters.AddWithValue("$zone", timeZoneId);
            aggregate.ExecuteNonQuery();
        }

        StorageServiceOrdinals current = ReadStorageServiceOrdinals(_connection, transaction);
        var next = new StorageServiceOrdinals(
            checked(current.CommitOrdinal + 1),
            current.KeyboardAggregateCommitOrdinal);
        WriteStorageServiceOrdinals(_connection, transaction, next);
        faultInjector.BeforeStorageCommit(boundary: null, keyboardRowCount: 0);
        transaction.Commit();
        return next;
    }

    internal SpoolDurableWatermark CommitSpoolBoundary(
        Guid deviceId,
        MouseSpoolWriter writer,
        string spoolId,
        long updatedUtcMicroseconds)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentException.ThrowIfNullOrWhiteSpace(spoolId);
        SpoolFlushResult flush = writer.FlushFile();
        using SqliteTransaction transaction = _connection.BeginTransaction();
        using SqliteCommand command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO mouse_spool_commits(
                spool_id, device_id, committed_length, record_count, power_durable_count, updated_utc_us)
            VALUES ($spool, $device, $length, $records, $durable, $updated)
            ON CONFLICT(spool_id) DO UPDATE SET
                committed_length = excluded.committed_length,
                record_count = excluded.record_count,
                power_durable_count = excluded.power_durable_count,
                updated_utc_us = excluded.updated_utc_us;
            """;
        command.Parameters.AddWithValue("$spool", spoolId);
        command.Parameters.AddWithValue("$device", deviceId.ToString("N"));
        command.Parameters.AddWithValue("$length", flush.FlushedLength);
        command.Parameters.AddWithValue("$records", flush.FileFlushedCount);
        command.Parameters.AddWithValue("$durable", flush.FileFlushedCount);
        command.Parameters.AddWithValue("$updated", updatedUtcMicroseconds);
        command.ExecuteNonQuery();
        transaction.Commit();
        return new SpoolDurableWatermark(
            spoolId,
            flush.FileFlushedCount,
            flush.FileFlushedCount,
            flush.FlushedLength);
    }

    internal MouseChunkDurableWatermark CommitMouseChunk(
        Guid chunkId,
        Guid deviceId,
        long calibrationSegmentId,
        IReadOnlyList<MouseSpoolRecord> records,
        long createdUtcMicroseconds) => MouseChunkCatalog.Commit(
            this,
            chunkId,
            deviceId,
            calibrationSegmentId,
            records,
            createdUtcMicroseconds);

    internal IReadOnlyList<MouseChunkCatalogEntry> ReadMouseChunkCatalog() => MouseChunkCatalog.ReadAll(this);

    internal MouseChunkRecoveryReport RecoverMouseChunks(long recoveredUtcMicroseconds) =>
        MouseChunkCatalog.Recover(this, recoveredUtcMicroseconds);

    internal SqliteConnection Connection => _connection;

    public IReadOnlyDictionary<string, IReadOnlyList<string>> ReadSchemaColumns()
    {
        var result = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        using SqliteCommand tables = _connection.CreateCommand();
        tables.CommandText = "SELECT name FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        using SqliteDataReader reader = tables.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        reader.Close();

        foreach (string name in names)
        {
            using SqliteCommand columns = _connection.CreateCommand();
            columns.CommandText = $"PRAGMA table_info([{name.Replace("]", "]]", StringComparison.Ordinal)}]);";
            using SqliteDataReader columnReader = columns.ExecuteReader();
            var values = new List<string>();
            while (columnReader.Read()) values.Add(columnReader.GetString(1));
            result.Add(name, values);
        }

        return result;
    }

    public void Dispose()
    {
        try
        {
            _connection.Dispose();
        }
        finally
        {
            _writerGuard?.Dispose();
        }
    }

    private T ExecuteScalar<T>(string commandText) => Scalar<T>(_connection, commandText);

    private static int PackPhysicalKey(ushort makeCode, bool isE0, bool isE1) =>
        makeCode | (isE0 ? 1 << 16 : 0) | (isE1 ? 1 << 17 : 0);

    private static void InsertCalibration(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TimeCalibrationSegment segment)
    {
        using (SqliteCommand session = connection.CreateCommand())
        {
            session.Transaction = transaction;
            session.CommandText = """
                INSERT OR IGNORE INTO clock_sessions(session_id, frequency, created_utc_us)
                VALUES ($session, $frequency, $created);
                """;
            session.Parameters.AddWithValue("$session", segment.SessionId.ToString("N"));
            session.Parameters.AddWithValue("$frequency", segment.Frequency);
            session.Parameters.AddWithValue("$created", segment.UtcAnchorMicroseconds);
            session.ExecuteNonQuery();
        }

        using (SqliteCommand persistedSession = connection.CreateCommand())
        {
            persistedSession.Transaction = transaction;
            persistedSession.CommandText = "SELECT frequency FROM clock_sessions WHERE session_id = $session;";
            persistedSession.Parameters.AddWithValue("$session", segment.SessionId.ToString("N"));
            object? stored = persistedSession.ExecuteScalar();
            if (stored is not long storedFrequency || storedFrequency != segment.Frequency)
            {
                throw new InvalidDataException("A clock session identifier has a conflicting frequency.");
            }
        }

        using SqliteCommand calibration = connection.CreateCommand();
        calibration.Transaction = transaction;
        calibration.CommandText = """
            INSERT INTO time_calibration_segments(
                session_id, monotonic_anchor, utc_anchor_us, uncertainty_ticks, time_zone_id)
            VALUES ($session, $mono, $utc, $uncertainty, $zone);
            """;
        calibration.Parameters.AddWithValue("$session", segment.SessionId.ToString("N"));
        calibration.Parameters.AddWithValue("$mono", segment.MonotonicAnchor);
        calibration.Parameters.AddWithValue("$utc", segment.UtcAnchorMicroseconds);
        calibration.Parameters.AddWithValue("$uncertainty", segment.SamplingUncertaintyTicks);
        calibration.Parameters.AddWithValue("$zone", segment.TimeZoneId);
        calibration.ExecuteNonQuery();
    }

    private static StorageServiceOrdinals ReadStorageServiceOrdinals(
        SqliteConnection connection,
        SqliteTransaction? transaction)
    {
        const string commitKey = "storage.service.commit-ordinal";
        const string keyboardKey = "storage.service.keyboard-aggregate-commit-ordinal";
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT key, value
            FROM store_metadata
            WHERE key IN ('storage.service.commit-ordinal', 'storage.service.keyboard-aggregate-commit-ordinal')
            ORDER BY key;
            """;
        using SqliteDataReader reader = command.ExecuteReader();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
        {
            values.Add(reader.GetString(0), reader.GetString(1));
        }

        if (values.Count == 0)
        {
            return new StorageServiceOrdinals(0, 0);
        }

        if (values.Count != 2
            || !values.TryGetValue(commitKey, out string? commitText)
            || !values.TryGetValue(keyboardKey, out string? keyboardText)
            || !long.TryParse(commitText, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out long commit)
            || !long.TryParse(keyboardText, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out long keyboard)
            || commit < 0
            || keyboard < 0
            || keyboard > commit)
        {
            throw new InvalidDataException("Storage service watermark metadata is missing or invalid.");
        }

        return new StorageServiceOrdinals(commit, keyboard);
    }

    private static long ReadMouseDeviceWatermark(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid deviceId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT durable_sequence FROM mouse_device_watermarks WHERE device_id = $device;";
        command.Parameters.AddWithValue("$device", deviceId.ToString("N"));
        object? value = command.ExecuteScalar();
        if (value is null)
        {
            return 0;
        }

        long sequence = Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
        if (sequence < 0)
        {
            throw new InvalidDataException("Mouse device watermark is invalid.");
        }

        return sequence;
    }

    private static void ValidateMouseSpoolForeignKeys(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MouseSpoolHeader header)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT d.kind, c.session_id
            FROM devices d
            JOIN time_calibration_segments c ON c.segment_id = $calibration
            WHERE d.device_id = $device;
            """;
        command.Parameters.AddWithValue("$device", header.DeviceId.ToString("N"));
        command.Parameters.AddWithValue("$calibration", header.CalibrationSegmentId);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()
            || reader.GetInt32(0) != (int)StorageDeviceKind.Mouse
            || !Guid.TryParseExact(reader.GetString(1), "N", out Guid sessionId)
            || sessionId != header.CalibrationSessionId
            || reader.Read())
        {
            throw new InvalidDataException("Mouse spool device or calibration identity is invalid.");
        }
    }

    private static void WriteStorageServiceOrdinals(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StorageServiceOrdinals ordinals)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO store_metadata(key, value)
            VALUES
                ('storage.service.commit-ordinal', $commit),
                ('storage.service.keyboard-aggregate-commit-ordinal', $keyboard)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.AddWithValue("$commit", ordinals.CommitOrdinal.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$keyboard", ordinals.KeyboardAggregateCommitOrdinal.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }

    private static bool MouseButtonsAlreadyAggregated(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid segmentId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1
            FROM mouse_button_aggregated_segments
            WHERE spool_segment_id = $segment;
            """;
        command.Parameters.AddWithValue("$segment", segmentId.ToString("N"));
        return command.ExecuteScalar() is not null;
    }

    private static void AggregateMouseButtons(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MouseSpoolSegment segment)
    {
        if (MouseButtonsAlreadyAggregated(connection, transaction, segment.Header.SegmentId))
        {
            return;
        }

        if (!segment.Records.Any(record => (record.ButtonChanges & 0x0155) != 0))
        {
            MarkMouseButtonsAggregated(connection, transaction, segment.Header.SegmentId);
            return;
        }

        TimeCalibrationSegment calibration = ReadMouseCalibration(connection, transaction, segment.Header);
        TimeZoneInfo zone = StorageKeyboardTime.ValidateCalibration(calibration);
        var counts = new Dictionary<(StorageMouseButton Button, DateOnly Date), long>();
        foreach (MouseSpoolRecord record in segment.Records)
        {
            ushort downChanges = (ushort)(record.ButtonChanges & 0x0155);
            if (downChanges == 0)
            {
                continue;
            }

            DateOnly date = StorageKeyboardTime.GetLocalDate(
                calibration.ToUtcMicroseconds(record.MonotonicTimestamp),
                zone);
            foreach ((StorageMouseButton button, ushort downMask) in MouseButtonDownMasks)
            {
                if ((downChanges & downMask) == 0)
                {
                    continue;
                }

                var key = (button, date);
                counts.TryGetValue(key, out long current);
                counts[key] = checked(current + 1);
            }
        }

        foreach (((StorageMouseButton button, DateOnly date), long count) in counts
            .OrderBy(pair => pair.Key.Date)
            .ThenBy(pair => pair.Key.Button))
        {
            using SqliteCommand aggregate = connection.CreateCommand();
            aggregate.Transaction = transaction;
            aggregate.CommandText = """
                INSERT INTO mouse_button_daily(
                    device_id, button, capture_local_date, time_zone_id, press_count)
                VALUES ($device, $button, $date, $zone, $count)
                ON CONFLICT(device_id, button, capture_local_date, time_zone_id)
                DO UPDATE SET press_count = press_count + excluded.press_count;
                """;
            aggregate.Parameters.AddWithValue("$device", segment.Header.DeviceId.ToString("N"));
            aggregate.Parameters.AddWithValue("$button", (int)button);
            aggregate.Parameters.AddWithValue("$date", date.ToString(
                "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture));
            aggregate.Parameters.AddWithValue("$zone", calibration.TimeZoneId);
            aggregate.Parameters.AddWithValue("$count", count);
            aggregate.ExecuteNonQuery();
        }

        MarkMouseButtonsAggregated(connection, transaction, segment.Header.SegmentId);
    }

    private static void MarkMouseButtonsAggregated(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid segmentId)
    {
        using SqliteCommand marker = connection.CreateCommand();
        marker.Transaction = transaction;
        marker.CommandText = """
            INSERT INTO mouse_button_aggregated_segments(spool_segment_id)
            VALUES ($segment);
            """;
        marker.Parameters.AddWithValue("$segment", segmentId.ToString("N"));
        marker.ExecuteNonQuery();
    }

    private static TimeCalibrationSegment ReadMouseCalibration(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MouseSpoolHeader header)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT c.session_id, s.frequency, c.monotonic_anchor, c.utc_anchor_us,
                   c.uncertainty_ticks, c.time_zone_id
            FROM time_calibration_segments c
            JOIN clock_sessions s ON s.session_id = c.session_id
            WHERE c.segment_id = $segment;
            """;
        command.Parameters.AddWithValue("$segment", header.CalibrationSegmentId);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read()
            || !Guid.TryParseExact(reader.GetString(0), "N", out Guid sessionId)
            || sessionId == Guid.Empty
            || sessionId != header.CalibrationSessionId)
        {
            throw new InvalidDataException("Mouse button aggregate calibration identity is invalid.");
        }

        var calibration = new TimeCalibrationSegment(
            sessionId,
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetString(5));
        if (reader.Read())
        {
            throw new InvalidDataException("Mouse button aggregate calibration is ambiguous.");
        }

        StorageKeyboardTime.ValidateCalibration(calibration);
        return calibration;
    }

    private static void Configure(SqliteConnection connection)
    {
        string journal = Scalar<string>(connection, "PRAGMA journal_mode = WAL;");
        if (!string.Equals(journal, "wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"SQLite refused WAL mode and returned '{journal}'.");
        }

        Execute(connection, """
            PRAGMA synchronous = FULL;
            PRAGMA foreign_keys = ON;
            PRAGMA secure_delete = ON;
            PRAGMA busy_timeout = 5000;
            """);
    }

    private static void ApplyMigrations(SqliteConnection connection, IStorageClock clock)
    {
        int version = Scalar<int>(connection, "PRAGMA user_version;");
        if (version > StorageSchema.CurrentVersion)
        {
            throw new InvalidDataException($"Storage schema version {version} is newer than supported version {StorageSchema.CurrentVersion}.");
        }

        if (version == 0)
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            Execute(connection, StorageSchema.Version1Sql, transaction);
            using SqliteCommand record = connection.CreateCommand();
            record.Transaction = transaction;
            record.CommandText = """
                INSERT INTO schema_migrations(version, name, checksum, applied_utc_us)
                VALUES (1, 'initial-storage-schema', $checksum, $applied);
                """;
            record.Parameters.AddWithValue("$checksum", StorageSchema.Version1Checksum);
            record.Parameters.AddWithValue("$applied", ToUnixMicroseconds(clock.GetUtcNow()));
            record.ExecuteNonQuery();
            transaction.Commit();
            version = 1;
        }

        VerifyMigrationChecksum(connection, 1, StorageSchema.Version1Checksum);
        if (version == 1)
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            Execute(connection, StorageSchema.Version2Sql, transaction);
            using SqliteCommand record = connection.CreateCommand();
            record.Transaction = transaction;
            record.CommandText = """
                INSERT INTO schema_migrations(version, name, checksum, applied_utc_us)
                VALUES (2, 'mouse-chunk-catalog', $checksum, $applied);
                """;
            record.Parameters.AddWithValue("$checksum", StorageSchema.Version2Checksum);
            record.Parameters.AddWithValue("$applied", ToUnixMicroseconds(clock.GetUtcNow()));
            record.ExecuteNonQuery();
            transaction.Commit();
            version = 2;
        }

        VerifyMigrationChecksum(connection, 2, StorageSchema.Version2Checksum);
        if (version == 2)
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            Execute(connection, StorageSchema.Version3Sql, transaction);
            using SqliteCommand record = connection.CreateCommand();
            record.Transaction = transaction;
            record.CommandText = """
                INSERT INTO schema_migrations(version, name, checksum, applied_utc_us)
                VALUES (3, 'storage-service-contracts', $checksum, $applied);
                """;
            record.Parameters.AddWithValue("$checksum", StorageSchema.Version3Checksum);
            record.Parameters.AddWithValue("$applied", ToUnixMicroseconds(clock.GetUtcNow()));
            record.ExecuteNonQuery();
            transaction.Commit();
            version = 3;
        }

        VerifyMigrationChecksum(connection, 3, StorageSchema.Version3Checksum);
        if (version == 3)
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            Execute(connection, StorageSchema.Version4Sql, transaction);
            using SqliteCommand record = connection.CreateCommand();
            record.Transaction = transaction;
            record.CommandText = """
                INSERT INTO schema_migrations(version, name, checksum, applied_utc_us)
                VALUES (4, 'mouse-ingestion-spool-directory', $checksum, $applied);
                """;
            record.Parameters.AddWithValue("$checksum", StorageSchema.Version4Checksum);
            record.Parameters.AddWithValue("$applied", ToUnixMicroseconds(clock.GetUtcNow()));
            record.ExecuteNonQuery();
            transaction.Commit();
            version = 4;
        }

        VerifyMigrationChecksum(connection, 4, StorageSchema.Version4Checksum);
        if (version == 4)
        {
            ValidateVersionFourShape(connection);
            using SqliteTransaction transaction = connection.BeginTransaction();
            Execute(connection, StorageSchema.Version5Sql, transaction);
            using SqliteCommand record = connection.CreateCommand();
            record.Transaction = transaction;
            record.CommandText = """
                INSERT INTO schema_migrations(version, name, checksum, applied_utc_us)
                VALUES (5, 'mouse-playback-seal-reason', $checksum, $applied);
                """;
            record.Parameters.AddWithValue("$checksum", StorageSchema.Version5Checksum);
            record.Parameters.AddWithValue("$applied", ToUnixMicroseconds(clock.GetUtcNow()));
            record.ExecuteNonQuery();
            transaction.Commit();
            version = 5;
        }

        VerifyMigrationChecksum(connection, 5, StorageSchema.Version5Checksum);
        if (version == 5)
        {
            ValidateVersionFiveShape(connection);
            using SqliteTransaction transaction = connection.BeginTransaction();
            Execute(connection, StorageSchema.Version6Sql, transaction);
            using SqliteCommand record = connection.CreateCommand();
            record.Transaction = transaction;
            record.CommandText = """
                INSERT INTO schema_migrations(version, name, checksum, applied_utc_us)
                VALUES (6, 'mouse-button-daily-aggregates', $checksum, $applied);
                """;
            record.Parameters.AddWithValue("$checksum", StorageSchema.Version6Checksum);
            record.Parameters.AddWithValue("$applied", ToUnixMicroseconds(clock.GetUtcNow()));
            record.ExecuteNonQuery();
            transaction.Commit();
            version = 6;
        }

        VerifyMigrationChecksum(connection, 6, StorageSchema.Version6Checksum);
        ValidateVersionSixShape(connection);
        if (version == 6)
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            Execute(connection, StorageSchema.Version7Sql, transaction);
            using SqliteCommand record = connection.CreateCommand();
            record.Transaction = transaction;
            record.CommandText = """
                INSERT INTO schema_migrations(version, name, checksum, applied_utc_us)
                VALUES (7, 'gamepad-physical-button-aggregates', $checksum, $applied);
                """;
            record.Parameters.AddWithValue("$checksum", StorageSchema.Version7Checksum);
            record.Parameters.AddWithValue("$applied", ToUnixMicroseconds(clock.GetUtcNow()));
            record.ExecuteNonQuery();
            transaction.Commit();
            version = 7;
        }

        VerifyMigrationChecksum(connection, 7, StorageSchema.Version7Checksum);
        ValidateVersionSevenShape(connection);
    }

    private static void ValidateVersionFourShape(SqliteConnection connection)
    {
        const string spoolColumns = "spool_segment_id:TEXT:1:1|device_id:TEXT:1:0|calibration_segment_id:INTEGER:1:0|relative_path:TEXT:1:0|first_sequence:INTEGER:1:0|last_sequence:INTEGER:1:0|first_monotonic_timestamp:INTEGER:1:0|last_monotonic_timestamp:INTEGER:1:0|record_count:INTEGER:1:0|committed_length:INTEGER:1:0|spool_file_sha256:TEXT:1:0|canonical_sha256:TEXT:1:0|created_utc_us:INTEGER:1:0";
        const string watermarkColumns = "device_id:TEXT:1:1|durable_sequence:INTEGER:1:0|updated_utc_us:INTEGER:1:0";
        string actualSpoolColumns = Scalar<string>(connection, """
            SELECT group_concat(name || ':' || type || ':' || "notnull" || ':' || pk, '|')
            FROM (SELECT name, type, "notnull", pk FROM pragma_table_info('mouse_spool_segments') ORDER BY cid);
            """);
        string actualWatermarkColumns = Scalar<string>(connection, """
            SELECT group_concat(name || ':' || type || ':' || "notnull" || ':' || pk, '|')
            FROM (SELECT name, type, "notnull", pk FROM pragma_table_info('mouse_device_watermarks') ORDER BY cid);
            """);
        string indexColumns = Scalar<string>(connection, """
            SELECT group_concat(name, '|')
            FROM (SELECT name FROM pragma_index_info('mouse_spool_segments_device_sequence') ORDER BY seqno);
            """);
        long spoolShape = Scalar<long>(connection, """
            SELECT COUNT(*) FROM pragma_table_list
            WHERE name = 'mouse_spool_segments' AND type = 'table' AND wr = 1 AND strict = 1;
            """);
        long watermarkShape = Scalar<long>(connection, """
            SELECT COUNT(*) FROM pragma_table_list
            WHERE name = 'mouse_device_watermarks' AND type = 'table' AND wr = 1 AND strict = 1;
            """);
        long indexShape = Scalar<long>(connection, """
            SELECT COUNT(*) FROM pragma_index_list('mouse_spool_segments')
            WHERE name = 'mouse_spool_segments_device_sequence' AND "unique" = 0 AND origin = 'c';
            """);
        long markerShape = Scalar<long>(connection, """
            SELECT COUNT(*) FROM store_metadata
            WHERE key = 'mouse.v3-adoption-complete' AND value IN ('0', '1');
            """);
        if (!string.Equals(actualSpoolColumns, spoolColumns, StringComparison.Ordinal)
            || !string.Equals(actualWatermarkColumns, watermarkColumns, StringComparison.Ordinal)
            || !string.Equals(indexColumns, "device_id|first_sequence|last_sequence", StringComparison.Ordinal)
            || spoolShape != 1
            || watermarkShape != 1
            || indexShape != 1
            || markerShape != 1)
        {
            throw new InvalidDataException("Storage schema version 4 shape or adoption marker is invalid.");
        }
    }

    private static void ValidateVersionFiveShape(SqliteConnection connection)
    {
        const string spoolColumns = "spool_segment_id:TEXT:1:1|device_id:TEXT:1:0|calibration_segment_id:INTEGER:1:0|relative_path:TEXT:1:0|first_sequence:INTEGER:1:0|last_sequence:INTEGER:1:0|first_monotonic_timestamp:INTEGER:1:0|last_monotonic_timestamp:INTEGER:1:0|record_count:INTEGER:1:0|committed_length:INTEGER:1:0|spool_file_sha256:TEXT:1:0|canonical_sha256:TEXT:1:0|created_utc_us:INTEGER:1:0|seal_reason:INTEGER:1:0";
        const string watermarkColumns = "device_id:TEXT:1:1|durable_sequence:INTEGER:1:0|updated_utc_us:INTEGER:1:0";
        string actualSpoolColumns = Scalar<string>(connection, """
            SELECT group_concat(name || ':' || type || ':' || "notnull" || ':' || pk, '|')
            FROM (SELECT name, type, "notnull", pk FROM pragma_table_info('mouse_spool_segments') ORDER BY cid);
            """);
        long reasonCheck = Scalar<long>(connection, """
            SELECT COUNT(*)
            FROM sqlite_schema
            WHERE type = 'table'
              AND name = 'mouse_spool_segments'
              AND sql LIKE '%seal_reason INTEGER NOT NULL DEFAULT 8%'
              AND sql LIKE '%seal_reason IN (0, 1, 2, 3, 4, 5, 6, 7, 8, 9)%';
            """);
        string actualWatermarkColumns = Scalar<string>(connection, """
            SELECT group_concat(name || ':' || type || ':' || "notnull" || ':' || pk, '|')
            FROM (SELECT name, type, "notnull", pk FROM pragma_table_info('mouse_device_watermarks') ORDER BY cid);
            """);
        string indexColumns = Scalar<string>(connection, """
            SELECT group_concat(name, '|')
            FROM (SELECT name FROM pragma_index_info('mouse_spool_segments_device_sequence') ORDER BY seqno);
            """);
        long spoolShape = Scalar<long>(connection, """
            SELECT COUNT(*) FROM pragma_table_list
            WHERE name = 'mouse_spool_segments' AND type = 'table' AND wr = 1 AND strict = 1;
            """);
        long watermarkShape = Scalar<long>(connection, """
            SELECT COUNT(*) FROM pragma_table_list
            WHERE name = 'mouse_device_watermarks' AND type = 'table' AND wr = 1 AND strict = 1;
            """);
        long indexShape = Scalar<long>(connection, """
            SELECT COUNT(*) FROM pragma_index_list('mouse_spool_segments')
            WHERE name = 'mouse_spool_segments_device_sequence' AND "unique" = 0 AND origin = 'c';
            """);
        long markerShape = Scalar<long>(connection, """
            SELECT COUNT(*) FROM store_metadata
            WHERE key = 'mouse.v3-adoption-complete' AND value IN ('0', '1');
            """);
        if (!string.Equals(actualSpoolColumns, spoolColumns, StringComparison.Ordinal)
            || !string.Equals(actualWatermarkColumns, watermarkColumns, StringComparison.Ordinal)
            || !string.Equals(indexColumns, "device_id|first_sequence|last_sequence", StringComparison.Ordinal)
            || reasonCheck != 1
            || spoolShape != 1
            || watermarkShape != 1
            || indexShape != 1
            || markerShape != 1)
        {
            throw new InvalidDataException("Storage schema version 5 mouse playback shape is invalid.");
        }
    }

    private static void ValidateVersionSixShape(SqliteConnection connection)
    {
        ValidateVersionFiveShape(connection);
        const string dailyColumns = "device_id:TEXT:1:1|button:INTEGER:1:2|capture_local_date:TEXT:1:3|time_zone_id:TEXT:1:4|press_count:INTEGER:1:0";
        const string segmentColumns = "spool_segment_id:TEXT:1:1";
        string actualDailyColumns = Scalar<string>(connection, """
            SELECT group_concat(name || ':' || type || ':' || "notnull" || ':' || pk, '|')
            FROM (SELECT name, type, "notnull", pk FROM pragma_table_info('mouse_button_daily') ORDER BY cid);
            """);
        string actualSegmentColumns = Scalar<string>(connection, """
            SELECT group_concat(name || ':' || type || ':' || "notnull" || ':' || pk, '|')
            FROM (SELECT name, type, "notnull", pk FROM pragma_table_info('mouse_button_aggregated_segments') ORDER BY cid);
            """);
        long dailyShape = Scalar<long>(connection, """
            SELECT COUNT(*) FROM pragma_table_list
            WHERE name = 'mouse_button_daily' AND type = 'table' AND wr = 1 AND strict = 1;
            """);
        long segmentShape = Scalar<long>(connection, """
            SELECT COUNT(*) FROM pragma_table_list
            WHERE name = 'mouse_button_aggregated_segments' AND type = 'table' AND wr = 1 AND strict = 1;
            """);
        long checks = Scalar<long>(connection, """
            SELECT COUNT(*) FROM sqlite_schema
            WHERE type = 'table' AND name = 'mouse_button_daily'
              AND sql LIKE '%button IN (0, 1, 2, 3, 4)%'
              AND sql LIKE '%press_count INTEGER NOT NULL CHECK (press_count > 0)%';
            """);
        if (!string.Equals(actualDailyColumns, dailyColumns, StringComparison.Ordinal)
            || !string.Equals(actualSegmentColumns, segmentColumns, StringComparison.Ordinal)
            || dailyShape != 1
            || segmentShape != 1
            || checks != 1)
        {
            throw new InvalidDataException("Storage schema version 6 mouse button aggregate shape is invalid.");
        }
    }

    private static void ValidateVersionSevenShape(SqliteConnection connection)
    {
        string[] expected = [
            "gamepad_configuration_revisions", "gamepad_configuration_current",
            "gamepad_hid_bindings", "gamepad_hid_axis_bindings", "gamepad_daily"];
        long tables = Scalar<long>(connection, """
            SELECT COUNT(*) FROM pragma_table_list
            WHERE name IN ('gamepad_configuration_revisions', 'gamepad_configuration_current',
                'gamepad_hid_bindings', 'gamepad_hid_axis_bindings', 'gamepad_daily')
              AND type = 'table' AND wr = 1 AND strict = 1;
            """);
        long buttonChecks = Scalar<long>(connection, """
            SELECT COUNT(*) FROM sqlite_schema
            WHERE type = 'table' AND name IN ('gamepad_hid_bindings', 'gamepad_hid_axis_bindings', 'gamepad_daily')
              AND sql LIKE '%button BETWEEN 0 AND 15%';
            """);
        if (tables != expected.Length || buttonChecks != 3)
            throw new InvalidDataException("Storage schema version 7 gamepad table shape is invalid.");
    }

    private static void VerifyMigrationChecksum(SqliteConnection connection, int version, string expected)
    {
        string actual = Scalar<string>(connection, $"SELECT checksum FROM schema_migrations WHERE version = {version};");
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Storage schema migration {version} checksum does not match the compiled migration.");
        }
    }

    private static long ToUnixMicroseconds(DateTimeOffset value) =>
        checked((value.UtcDateTime.Ticks - 621355968000000000L) / 10);

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static T Scalar<T>(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        object? value = command.ExecuteScalar();
        if (value is null or DBNull)
        {
            throw new InvalidDataException($"SQLite command returned no scalar value: {sql}");
        }

        return (T)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }
}
