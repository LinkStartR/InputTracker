using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace InputTracker.Storage;

public enum StorageDeletionCapability
{
    BlockedByCurrentFormat = 0
}

public sealed record StorageDeletionPreview(
    Guid DeviceId,
    StorageUtcRange UtcRange,
    long KeyboardAggregateCount,
    StorageDeletionCapability Capability,
    string? BlockedReason,
    string Token,
    string StateFingerprint);

internal static class StorageDeletion
{
    public static (StorageDeviceKind Kind, long KeyboardAggregateCount) DescribeKeyboardImpact(
        SqliteConnection connection,
        Guid deviceId,
        StorageUtcRange range)
    {
        StorageDeviceKind kind = ReadDeviceKind(connection, deviceId);
        if (kind != StorageDeviceKind.Keyboard) return (kind, 0);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT capture_local_date, time_zone_id
            FROM keyboard_daily
            WHERE device_id = $device;
            """;
        command.Parameters.AddWithValue("$device", deviceId.ToString("N"));
        using SqliteDataReader reader = command.ExecuteReader();
        long count = 0;
        while (reader.Read())
        {
            DateOnly date = DateOnly.ParseExact(
                reader.GetString(0), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            StorageUtcRange day = StoragePeriodResolver.Resolve(
                StoragePeriodKind.Day, date, reader.GetString(1)).UtcRange;
            if (day.StartUtcMicroseconds < range.EndUtcMicroseconds
                && day.EndUtcMicroseconds > range.StartUtcMicroseconds)
            {
                count = checked(count + 1);
            }
        }
        return (kind, count);
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
        StorageDeviceKind kind,
        long keyboardCount,
        StorageDurableWatermark watermark)
    {
        var text = new StringBuilder();
        text.Append(deviceId.ToString("N")).Append('|')
            .Append(range.StartUtcMicroseconds).Append('|').Append(range.EndUtcMicroseconds).Append('|')
            .Append((int)kind).Append('|').Append(keyboardCount).Append('|')
            .Append(watermark.CommitSequence).Append('|')
            .Append(watermark.KeyboardAggregateCommitSequence);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    public static string CreateToken(string stateFingerprint) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("InputTracker.delete-preview.v1|" + stateFingerprint)));

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
