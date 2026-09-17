using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace InputTracker.Storage;

public enum StorageDeletionCapability
{
    BlockedByCurrentFormat = 0,
    ExactKeyboardDailyAggregates = 1
}

public sealed record StorageDeletionPreview(
    Guid DeviceId,
    StorageUtcRange UtcRange,
    long KeyboardAggregateCount,
    long KeyboardPressCount,
    long KeyboardHoldDurationMicroseconds,
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
    StorageBackupArtifact PreDeletionBackup);

internal sealed record StorageKeyboardDeletionRow(
    int PhysicalKey,
    DateOnly CaptureLocalDate,
    string TimeZoneId,
    long PressCount,
    long HoldDurationMicroseconds);

internal sealed record StorageDeletionImpact(
    StorageDeviceKind Kind,
    IReadOnlyList<StorageKeyboardDeletionRow> Rows,
    bool HasPartialDayOverlap)
{
    public long PressCount => Rows.Sum(row => row.PressCount);
    public long HoldDurationMicroseconds => Rows.Sum(row => row.HoldDurationMicroseconds);
}

internal static class StorageDeletion
{
    public static StorageDeletionImpact DescribeKeyboardImpact(
        SqliteConnection connection,
        Guid deviceId,
        StorageUtcRange range)
    {
        StorageDeviceKind kind = ReadDeviceKind(connection, deviceId);
        if (kind != StorageDeviceKind.Keyboard)
            return new StorageDeletionImpact(kind, [], HasPartialDayOverlap: false);

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
            DateOnly date = DateOnly.ParseExact(
                reader.GetString(1), "yyyy-MM-dd", CultureInfo.InvariantCulture);
            string timeZoneId = reader.GetString(2);
            StorageUtcRange day = StoragePeriodResolver.Resolve(
                StoragePeriodKind.Day, date, timeZoneId).UtcRange;
            bool overlaps = day.StartUtcMicroseconds < range.EndUtcMicroseconds
                && day.EndUtcMicroseconds > range.StartUtcMicroseconds;
            if (!overlaps) continue;

            bool fullyContained = day.StartUtcMicroseconds >= range.StartUtcMicroseconds
                && day.EndUtcMicroseconds <= range.EndUtcMicroseconds;
            if (!fullyContained)
            {
                partial = true;
                continue;
            }

            rows.Add(new StorageKeyboardDeletionRow(
                checked((int)reader.GetInt64(0)),
                date,
                timeZoneId,
                reader.GetInt64(3),
                reader.GetInt64(4)));
        }
        return new StorageDeletionImpact(kind, rows, partial);
    }

    public static long DeleteKeyboardRows(
        SqliteConnection connection,
        Guid deviceId,
        IReadOnlyList<StorageKeyboardDeletionRow> rows)
    {
        if (rows.Count == 0) throw new InvalidOperationException("Deletion preview contains no matching keyboard records.");
        using SqliteTransaction transaction = connection.BeginTransaction();
        long deleted = 0;
        foreach (var bucket in rows
            .Select(row => (row.CaptureLocalDate, row.TimeZoneId))
            .Distinct())
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM keyboard_daily
                WHERE device_id = $device
                  AND capture_local_date = $date
                  AND time_zone_id = $zone;
                """;
            command.Parameters.AddWithValue("$device", deviceId.ToString("N"));
            command.Parameters.AddWithValue("$date", bucket.CaptureLocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$zone", bucket.TimeZoneId);
            deleted = checked(deleted + command.ExecuteNonQuery());
        }

        if (deleted != rows.Count)
        {
            throw new InvalidDataException("Keyboard deletion changed a different number of rows than the confirmed preview.");
        }
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
        {
            throw new InvalidDataException("Deletion device is missing or has an invalid kind.");
        }
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
        foreach (StorageKeyboardDeletionRow row in impact.Rows)
        {
            text.Append('|').Append(row.PhysicalKey)
                .Append('|').Append(row.CaptureLocalDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                .Append('|').Append(row.TimeZoneId)
                .Append('|').Append(row.PressCount)
                .Append('|').Append(row.HoldDurationMicroseconds);
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    public static string CreateToken(string stateFingerprint) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("InputTracker.delete-preview.v2|" + stateFingerprint)));

    public static bool FixedTimeTokenEquals(string supplied, string expected)
    {
        int difference = supplied.Length ^ expected.Length;
        int count = Math.Min(supplied.Length, expected.Length);
        for (int index = 0; index < count; index++)
        {
            difference |= supplied[index] ^ expected[index];
        }
        return difference == 0;
    }
}
