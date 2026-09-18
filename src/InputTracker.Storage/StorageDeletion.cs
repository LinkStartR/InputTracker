using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace InputTracker.Storage;

public enum StorageDeletionCapability
{
    BlockedByCurrentFormat = 0,
    ExactKeyboardDailyAggregates = 1,
    ExactMouseButtonDailyAggregates = 2,
    ExactKeyboardAndMouseDailyAggregates = 3
}

public sealed record StorageDeletionPreview(
    Guid DeviceId,
    StorageUtcRange UtcRange,
    long KeyboardAggregateCount,
    long KeyboardPressCount,
    long KeyboardHoldDurationMicroseconds,
    long MouseButtonAggregateCount,
    long MouseButtonPressCount,
    StorageDeletionCapability Capability,
    string? BlockedReason,
    string Token,
    string StateFingerprint);

public sealed record StorageDeletionResult(
    Guid DeviceId,
    StorageUtcRange UtcRange,
    long DeletedKeyboardAggregateCount,
    long DeletedKeyboardPressCount,
    long DeletedKeyboardHoldDurationMicroseconds,
    long DeletedMouseButtonAggregateCount,
    long DeletedMouseButtonPressCount,
    StorageBackupArtifact PreDeletionBackup);

public sealed record StorageDeletionBatchPreview(
    Guid KeyboardDeviceId,
    Guid MouseDeviceId,
    StorageUtcRange UtcRange,
    long KeyboardAggregateCount,
    long KeyboardPressCount,
    long KeyboardHoldDurationMicroseconds,
    long MouseButtonAggregateCount,
    long MouseButtonPressCount,
    StorageDeletionCapability Capability,
    string? BlockedReason,
    string Token,
    string StateFingerprint);

public sealed record StorageDeletionBatchResult(
    Guid KeyboardDeviceId,
    Guid MouseDeviceId,
    StorageUtcRange UtcRange,
    long DeletedKeyboardAggregateCount,
    long DeletedKeyboardPressCount,
    long DeletedKeyboardHoldDurationMicroseconds,
    long DeletedMouseButtonAggregateCount,
    long DeletedMouseButtonPressCount,
    StorageBackupArtifact PreDeletionBackup);

internal sealed record StorageKeyboardDeletionRow(
    int PhysicalKey,
    DateOnly CaptureLocalDate,
    string TimeZoneId,
    long PressCount,
    long HoldDurationMicroseconds);

internal sealed record StorageMouseButtonDeletionRow(
    StorageMouseButton Button,
    DateOnly CaptureLocalDate,
    string TimeZoneId,
    long PressCount);

internal sealed record StorageDeletionImpact(
    StorageDeviceKind Kind,
    IReadOnlyList<StorageKeyboardDeletionRow> KeyboardRows,
    IReadOnlyList<StorageMouseButtonDeletionRow> MouseButtonRows,
    bool HasPartialDayOverlap)
{
    public long AggregateCount => checked(KeyboardRows.Count + MouseButtonRows.Count);
    public long PressCount => Kind == StorageDeviceKind.Keyboard
        ? KeyboardRows.Sum(row => row.PressCount)
        : MouseButtonRows.Sum(row => row.PressCount);
    public long HoldDurationMicroseconds => KeyboardRows.Sum(row => row.HoldDurationMicroseconds);
}

internal static class StorageDeletion
{
    public static StorageDeletionImpact DescribeImpact(
        SqliteConnection connection,
        Guid deviceId,
        StorageUtcRange range)
    {
        StorageDeviceKind kind = ReadDeviceKind(connection, deviceId);
        return kind == StorageDeviceKind.Keyboard
            ? DescribeKeyboardImpact(connection, deviceId, range)
            : DescribeMouseButtonImpact(connection, deviceId, range);
    }

    private static StorageDeletionImpact DescribeKeyboardImpact(
        SqliteConnection connection,
        Guid deviceId,
        StorageUtcRange range)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT physical_key, capture_local_date, time_zone_id, press_count, hold_duration_us
            FROM keyboard_daily
            WHERE device_id = $device
            ORDER BY capture_local_date, time_zone_id, physical_key;
            """;
        command.Parameters.AddWithValue("$device", deviceId.ToString("N"));
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<StorageKeyboardDeletionRow>();
        bool partial = false;
        while (reader.Read())
        {
            DateOnly date = ParseDate(reader.GetString(1));
            string timeZoneId = reader.GetString(2);
            switch (ClassifyDay(date, timeZoneId, range))
            {
                case DayRangeRelation.Outside: continue;
                case DayRangeRelation.Partial: partial = true; continue;
            }
            rows.Add(new StorageKeyboardDeletionRow(
                checked((int)reader.GetInt64(0)), date, timeZoneId,
                reader.GetInt64(3), reader.GetInt64(4)));
        }
        return new StorageDeletionImpact(StorageDeviceKind.Keyboard, rows, [], partial);
    }

    private static StorageDeletionImpact DescribeMouseButtonImpact(
        SqliteConnection connection,
        Guid deviceId,
        StorageUtcRange range)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT button, capture_local_date, time_zone_id, press_count
            FROM mouse_button_daily
            WHERE device_id = $device
            ORDER BY capture_local_date, time_zone_id, button;
            """;
        command.Parameters.AddWithValue("$device", deviceId.ToString("N"));
        using SqliteDataReader reader = command.ExecuteReader();
        var rows = new List<StorageMouseButtonDeletionRow>();
        bool partial = false;
        while (reader.Read())
        {
            long rawButton = reader.GetInt64(0);
            if (rawButton is < (long)StorageMouseButton.Left or > (long)StorageMouseButton.X2)
                throw new InvalidDataException("Mouse button deletion found an invalid button.");
            DateOnly date = ParseDate(reader.GetString(1));
            string timeZoneId = reader.GetString(2);
            switch (ClassifyDay(date, timeZoneId, range))
            {
                case DayRangeRelation.Outside: continue;
                case DayRangeRelation.Partial: partial = true; continue;
            }
            long pressCount = reader.GetInt64(3);
            if (pressCount <= 0) throw new InvalidDataException("Mouse button deletion found an invalid count.");
            rows.Add(new StorageMouseButtonDeletionRow(
                (StorageMouseButton)rawButton, date, timeZoneId, pressCount));
        }
        return new StorageDeletionImpact(StorageDeviceKind.Mouse, [], rows, partial);
    }

    public static long DeleteRows(
        SqliteConnection connection,
        Guid deviceId,
        StorageDeletionImpact impact)
    {
        return DeleteRowsBatch(connection, [(deviceId, impact)]);
    }

    public static long DeleteRowsBatch(
        SqliteConnection connection,
        IReadOnlyList<(Guid DeviceId, StorageDeletionImpact Impact)> targets)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(targets);
        long expected = targets.Sum(target => target.Impact.AggregateCount);
        if (expected == 0)
            throw new InvalidOperationException("Deletion preview contains no matching records.");

        using SqliteTransaction transaction = connection.BeginTransaction();
        long deleted = 0;
        foreach ((Guid deviceId, StorageDeletionImpact impact) in targets)
        {
            string table = impact.Kind == StorageDeviceKind.Keyboard ? "keyboard_daily" : "mouse_button_daily";
            IEnumerable<(DateOnly CaptureLocalDate, string TimeZoneId)> buckets = impact.Kind == StorageDeviceKind.Keyboard
                ? impact.KeyboardRows.Select(row => (row.CaptureLocalDate, row.TimeZoneId))
                : impact.MouseButtonRows.Select(row => (row.CaptureLocalDate, row.TimeZoneId));
            foreach ((DateOnly date, string timeZoneId) in buckets.Distinct())
            {
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"""
                    DELETE FROM {table}
                    WHERE device_id = $device
                      AND capture_local_date = $date
                      AND time_zone_id = $zone;
                    """;
                command.Parameters.AddWithValue("$device", deviceId.ToString("N"));
                command.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$zone", timeZoneId);
                deleted = checked(deleted + command.ExecuteNonQuery());
            }
        }

        if (deleted != expected)
            throw new InvalidDataException("Deletion changed a different number of rows than the confirmed preview.");
        transaction.Commit();
        return deleted;
    }

    public static StorageDeviceKind ReadDeviceKind(SqliteConnection connection, Guid deviceId)
    {
        if (deviceId == Guid.Empty) throw new ArgumentException("Deletion device must be non-empty.", nameof(deviceId));
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT kind FROM devices WHERE device_id = $device;";
        command.Parameters.AddWithValue("$device", deviceId.ToString("N"));
        object? value = command.ExecuteScalar();
        if (value is not long raw || !Enum.IsDefined(typeof(StorageDeviceKind), checked((int)raw)))
            throw new InvalidDataException("Deletion device is missing or has an invalid kind.");
        return (StorageDeviceKind)raw;
    }

    public static string CreateStateFingerprint(
        Guid deviceId,
        StorageUtcRange range,
        StorageDeletionImpact impact,
        StorageDurableWatermark watermark)
    {
        var text = new StringBuilder();
        text.Append(deviceId.ToString("N")).Append('|')
            .Append(range.StartUtcMicroseconds).Append('|').Append(range.EndUtcMicroseconds).Append('|')
            .Append((int)impact.Kind).Append('|').Append(impact.HasPartialDayOverlap ? 1 : 0).Append('|')
            .Append(watermark.CommitSequence).Append('|')
            .Append(watermark.KeyboardAggregateCommitSequence);
        foreach (StorageKeyboardDeletionRow row in impact.KeyboardRows)
        {
            text.Append("|K|").Append(row.PhysicalKey)
                .Append('|').Append(FormatDate(row.CaptureLocalDate))
                .Append('|').Append(row.TimeZoneId)
                .Append('|').Append(row.PressCount)
                .Append('|').Append(row.HoldDurationMicroseconds);
        }
        foreach (StorageMouseButtonDeletionRow row in impact.MouseButtonRows)
        {
            text.Append("|M|").Append((int)row.Button)
                .Append('|').Append(FormatDate(row.CaptureLocalDate))
                .Append('|').Append(row.TimeZoneId)
                .Append('|').Append(row.PressCount);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    public static string CreateBatchStateFingerprint(
        Guid keyboardDeviceId,
        StorageDeletionImpact keyboardImpact,
        Guid mouseDeviceId,
        StorageDeletionImpact mouseImpact,
        StorageUtcRange range,
        StorageDurableWatermark watermark)
    {
        string keyboard = CreateStateFingerprint(keyboardDeviceId, range, keyboardImpact, watermark);
        string mouse = CreateStateFingerprint(mouseDeviceId, range, mouseImpact, watermark);
        string state = $"InputTracker.delete-batch.v1|{keyboardDeviceId:N}|{keyboard}|{mouseDeviceId:N}|{mouse}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state)));
    }

    public static string CreateToken(string stateFingerprint) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("InputTracker.delete-preview.v3|" + stateFingerprint)));

    public static bool FixedTimeTokenEquals(string supplied, string expected)
    {
        int difference = supplied.Length ^ expected.Length;
        int count = Math.Min(supplied.Length, expected.Length);
        for (int index = 0; index < count; index++) difference |= supplied[index] ^ expected[index];
        return difference == 0;
    }

    private static DayRangeRelation ClassifyDay(DateOnly date, string timeZoneId, StorageUtcRange range)
    {
        StorageUtcRange day = StoragePeriodResolver.Resolve(StoragePeriodKind.Day, date, timeZoneId).UtcRange;
        bool overlaps = day.StartUtcMicroseconds < range.EndUtcMicroseconds
            && day.EndUtcMicroseconds > range.StartUtcMicroseconds;
        if (!overlaps) return DayRangeRelation.Outside;
        return day.StartUtcMicroseconds >= range.StartUtcMicroseconds
            && day.EndUtcMicroseconds <= range.EndUtcMicroseconds
            ? DayRangeRelation.Contained
            : DayRangeRelation.Partial;
    }

    private static DateOnly ParseDate(string value) =>
        DateOnly.ParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string FormatDate(DateOnly value) =>
        value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private enum DayRangeRelation
    {
        Outside,
        Partial,
        Contained
    }
}
