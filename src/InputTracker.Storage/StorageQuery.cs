using System.Globalization;
using Microsoft.Data.Sqlite;

namespace InputTracker.Storage;

internal static class StorageQuery
{
    public static StorageKeyboardPeriodResult QueryKeyboardPeriod(
        SqliteConnection connection,
        StoragePeriodKind kind,
        DateOnly containingDate,
        string timeZoneId,
        Guid? deviceId)
    {
        StorageResolvedPeriod period = StoragePeriodResolver.Resolve(kind, containingDate, timeZoneId);
        return QueryKeyboardResolved(connection, period, deviceId);
    }

    public static StorageKeyboardPeriodResult QueryKeyboardRange(
        SqliteConnection connection,
        DateOnly inclusiveStartDate,
        DateOnly inclusiveEndDate,
        string timeZoneId,
        Guid? deviceId)
    {
        StorageResolvedPeriod period = StoragePeriodResolver.ResolveRange(
            inclusiveStartDate, inclusiveEndDate, timeZoneId);
        return QueryKeyboardResolved(connection, period, deviceId);
    }

    private static StorageKeyboardPeriodResult QueryKeyboardResolved(
        SqliteConnection connection,
        StorageResolvedPeriod period,
        Guid? deviceId)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (deviceId == Guid.Empty)
        {
            throw new ArgumentException("A device filter must be non-empty when supplied.", nameof(deviceId));
        }

        if (deviceId is Guid requested)
        {
            EnsureDeviceKind(connection, requested, StorageDeviceKind.Keyboard);
        }

        IReadOnlyList<string> zones = ReadCaptureTimeZones(connection, deviceId);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT k.device_id, k.make_code, k.is_e0, k.is_e1,
                   SUM(k.press_count), SUM(k.hold_duration_us)
            FROM keyboard_daily k
            JOIN devices d ON d.device_id = k.device_id
            WHERE d.kind = $keyboard
              AND k.time_zone_id = $zone
              AND k.capture_local_date >= $start
              AND k.capture_local_date < $end
              AND ($device IS NULL OR k.device_id = $device)
            GROUP BY k.device_id, k.make_code, k.is_e0, k.is_e1
            ORDER BY k.device_id, k.make_code, k.is_e0, k.is_e1;
            """;
        command.Parameters.AddWithValue("$keyboard", (int)StorageDeviceKind.Keyboard);
        command.Parameters.AddWithValue("$zone", period.TimeZoneId);
        command.Parameters.AddWithValue("$start", FormatDate(period.LocalStartDate));
        command.Parameters.AddWithValue("$end", FormatDate(period.LocalEndDate));
        command.Parameters.AddWithValue("$device", deviceId is Guid value
            ? value.ToString("N")
            : DBNull.Value);

        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<StorageKeyboardPeriodAggregate>();
        while (reader.Read())
        {
            if (!Guid.TryParseExact(reader.GetString(0), "N", out Guid rowDevice)
                || rowDevice == Guid.Empty
                || reader.GetInt64(1) is < 0 or > ushort.MaxValue)
            {
                throw new InvalidDataException("A keyboard aggregate identity is invalid.");
            }

            long isE0 = reader.GetInt64(2);
            long isE1 = reader.GetInt64(3);
            long presses = reader.GetInt64(4);
            long duration = reader.GetInt64(5);
            if ((isE0 != 0 && isE0 != 1)
                || (isE1 != 0 && isE1 != 1)
                || (isE0 == 1 && isE1 == 1)
                || presses < 0
                || duration < 0)
            {
                throw new InvalidDataException("A keyboard aggregate value is invalid.");
            }

            rows.Add(new StorageKeyboardPeriodAggregate(
                rowDevice,
                checked((ushort)reader.GetInt64(1)),
                isE0 == 1,
                isE1 == 1,
                presses,
                duration));
        }

        return new StorageKeyboardPeriodResult(period, rows, zones);
    }

    public static StorageMouseButtonPeriodResult QueryMouseButtonsPeriod(
        SqliteConnection connection,
        StoragePeriodKind kind,
        DateOnly containingDate,
        string timeZoneId,
        Guid? deviceId)
    {
        StorageResolvedPeriod period = StoragePeriodResolver.Resolve(kind, containingDate, timeZoneId);
        return QueryMouseButtonsResolved(connection, period, deviceId);
    }

    public static StorageMouseButtonPeriodResult QueryMouseButtonsRange(
        SqliteConnection connection,
        DateOnly inclusiveStartDate,
        DateOnly inclusiveEndDate,
        string timeZoneId,
        Guid? deviceId)
    {
        StorageResolvedPeriod period = StoragePeriodResolver.ResolveRange(
            inclusiveStartDate, inclusiveEndDate, timeZoneId);
        return QueryMouseButtonsResolved(connection, period, deviceId);
    }

    private static StorageMouseButtonPeriodResult QueryMouseButtonsResolved(
        SqliteConnection connection,
        StorageResolvedPeriod period,
        Guid? deviceId)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (deviceId == Guid.Empty)
        {
            throw new ArgumentException("A device filter must be non-empty when supplied.", nameof(deviceId));
        }

        if (deviceId is Guid requested)
        {
            EnsureDeviceKind(connection, requested, StorageDeviceKind.Mouse);
        }

        IReadOnlyList<string> zones = ReadMouseButtonCaptureTimeZones(connection, deviceId);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT m.device_id, m.button, SUM(m.press_count)
            FROM mouse_button_daily m
            JOIN devices d ON d.device_id = m.device_id
            WHERE d.kind = $mouse
              AND m.time_zone_id = $zone
              AND m.capture_local_date >= $start
              AND m.capture_local_date < $end
              AND ($device IS NULL OR m.device_id = $device)
            GROUP BY m.device_id, m.button
            ORDER BY m.device_id, m.button;
            """;
        command.Parameters.AddWithValue("$mouse", (int)StorageDeviceKind.Mouse);
        command.Parameters.AddWithValue("$zone", period.TimeZoneId);
        command.Parameters.AddWithValue("$start", FormatDate(period.LocalStartDate));
        command.Parameters.AddWithValue("$end", FormatDate(period.LocalEndDate));
        command.Parameters.AddWithValue("$device", deviceId is Guid value
            ? value.ToString("N")
            : DBNull.Value);

        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<StorageMouseButtonPeriodAggregate>();
        while (reader.Read())
        {
            long button = reader.GetInt64(1);
            long presses = reader.GetInt64(2);
            if (!Guid.TryParseExact(reader.GetString(0), "N", out Guid rowDevice)
                || rowDevice == Guid.Empty
                || button is < (long)StorageMouseButton.Left or > (long)StorageMouseButton.X2
                || presses <= 0)
            {
                throw new InvalidDataException("A mouse button aggregate is invalid.");
            }

            rows.Add(new StorageMouseButtonPeriodAggregate(
                rowDevice,
                (StorageMouseButton)button,
                presses));
        }

        return new StorageMouseButtonPeriodResult(period, rows, zones);
    }

    private static IReadOnlyList<string> ReadCaptureTimeZones(
        SqliteConnection connection,
        Guid? deviceId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT k.time_zone_id
            FROM keyboard_daily k
            JOIN devices d ON d.device_id = k.device_id
            WHERE d.kind = $keyboard
              AND ($device IS NULL OR k.device_id = $device)
            ORDER BY k.time_zone_id;
            """;
        command.Parameters.AddWithValue("$keyboard", (int)StorageDeviceKind.Keyboard);
        command.Parameters.AddWithValue("$device", deviceId is Guid value
            ? value.ToString("N")
            : DBNull.Value);
        using SqliteDataReader reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            string zone = reader.GetString(0);
            _ = StoragePeriodResolver.Resolve(StoragePeriodKind.Day, new DateOnly(2000, 1, 1), zone);
            result.Add(zone);
        }

        return result;
    }

    private static IReadOnlyList<string> ReadMouseButtonCaptureTimeZones(
        SqliteConnection connection,
        Guid? deviceId)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT m.time_zone_id
            FROM mouse_button_daily m
            JOIN devices d ON d.device_id = m.device_id
            WHERE d.kind = $mouse
              AND ($device IS NULL OR m.device_id = $device)
            ORDER BY m.time_zone_id;
            """;
        command.Parameters.AddWithValue("$mouse", (int)StorageDeviceKind.Mouse);
        command.Parameters.AddWithValue("$device", deviceId is Guid value
            ? value.ToString("N")
            : DBNull.Value);
        using SqliteDataReader reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            string zone = reader.GetString(0);
            _ = StoragePeriodResolver.Resolve(StoragePeriodKind.Day, new DateOnly(2000, 1, 1), zone);
            result.Add(zone);
        }

        return result;
    }

    private static void EnsureDeviceKind(
        SqliteConnection connection,
        Guid deviceId,
        StorageDeviceKind expected)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT kind FROM devices WHERE device_id = $device;";
        command.Parameters.AddWithValue("$device", deviceId.ToString("N"));
        object? value = command.ExecuteScalar();
        if (value is not long kind || kind != (long)expected)
        {
            throw new InvalidDataException("The requested device is missing or has the wrong kind.");
        }
    }

    private static string FormatDate(DateOnly value) =>
        value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
