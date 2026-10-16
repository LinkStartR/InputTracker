namespace InputTracker.Storage;

internal readonly record struct StorageServiceOrdinals(
    long CommitOrdinal,
    long KeyboardAggregateCommitOrdinal);

internal readonly record struct StorageKeyboardKey(
    Guid DeviceId,
    ushort MakeCode,
    bool IsE0,
    bool IsE1);

internal readonly record struct StorageKeyboardAggregateKey(
    Guid DeviceId,
    ushort MakeCode,
    bool IsE0,
    bool IsE1,
    DateOnly CaptureLocalDate,
    string TimeZoneId);

internal readonly record struct StorageKeyboardAggregateDelta(
    Guid DeviceId,
    ushort MakeCode,
    bool IsE0,
    bool IsE1,
    DateOnly CaptureLocalDate,
    string TimeZoneId,
    long PressCount,
    long HoldDurationMicroseconds)
{
    public StorageKeyboardAggregateKey Key => new(
        DeviceId,
        MakeCode,
        IsE0,
        IsE1,
        CaptureLocalDate,
        TimeZoneId);
}

internal sealed record StorageKeyboardHold(
    StorageKeyboardKey Key,
    long CursorMonotonicTimestamp,
    TimeCalibrationSegment Calibration);

internal static class StorageKeyboardTime
{
    private const long UnixEpochTicks = 621355968000000000L;

    public static TimeZoneInfo ValidateCalibration(TimeCalibrationSegment calibration)
    {
        if (calibration.SessionId == Guid.Empty
            || calibration.Frequency <= 0
            || calibration.MonotonicAnchor < 0
            || calibration.SamplingUncertaintyTicks < 0
            || string.IsNullOrWhiteSpace(calibration.TimeZoneId)
            || calibration.TimeZoneId.Contains('/', StringComparison.Ordinal))
        {
            throw new InvalidDataException("A calibration segment or Windows time-zone identifier is invalid.");
        }

        try
        {
            _ = FromUnixMicroseconds(calibration.UtcAnchorMicroseconds);
            return TimeZoneInfo.FindSystemTimeZoneById(calibration.TimeZoneId);
        }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new InvalidDataException("The explicit Windows time-zone identifier is unavailable or invalid.", error);
        }
    }

    public static DateOnly GetLocalDate(TimeCalibrationSegment calibration, long monotonicTimestamp)
    {
        TimeZoneInfo zone = ValidateCalibration(calibration);
        return GetLocalDate(calibration.ToUtcMicroseconds(monotonicTimestamp), zone);
    }

    public static DateOnly GetLocalDate(long utcMicroseconds, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        DateTimeOffset utc = FromUnixMicroseconds(utcMicroseconds);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, zone).DateTime);
    }

    public static IEnumerable<StorageKeyboardAggregateDelta> SplitDuration(
        StorageKeyboardHold hold,
        long endMonotonicTimestamp)
    {
        if (endMonotonicTimestamp < hold.CursorMonotonicTimestamp)
        {
            throw new InvalidDataException("A keyboard hold moved backwards in monotonic time.");
        }

        long startUtc = hold.Calibration.ToUtcMicroseconds(hold.CursorMonotonicTimestamp);
        long endUtc = hold.Calibration.ToUtcMicroseconds(endMonotonicTimestamp);
        if (endUtc < startUtc)
        {
            throw new InvalidDataException("A keyboard hold calibration moved backwards in UTC.");
        }

        if (endUtc == startUtc)
        {
            yield break;
        }

        TimeZoneInfo zone = ValidateCalibration(hold.Calibration);
        DateOnly firstDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(FromUnixMicroseconds(startUtc), zone).DateTime);
        DateOnly lastDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(FromUnixMicroseconds(endUtc - 1), zone).DateTime);
        long allocated = 0;
        for (DateOnly date = firstDate; ;)
        {
            DateOnly nextDate = GetNextDate(date);
            long dayStartUtc = GetLocalMidnightUtcMicroseconds(date, zone);
            long dayEndUtc = GetLocalMidnightUtcMicroseconds(nextDate, zone);
            if (dayEndUtc <= dayStartUtc)
            {
                throw new InvalidDataException("A Windows time-zone local date has an invalid UTC interval.");
            }

            long intersectionStart = Math.Max(startUtc, dayStartUtc);
            long intersectionEnd = Math.Min(endUtc, dayEndUtc);
            if (intersectionEnd > intersectionStart)
            {
                long duration = checked(intersectionEnd - intersectionStart);
                allocated = checked(allocated + duration);
                yield return new StorageKeyboardAggregateDelta(
                    hold.Key.DeviceId,
                    hold.Key.MakeCode,
                    hold.Key.IsE0,
                    hold.Key.IsE1,
                    date,
                    hold.Calibration.TimeZoneId,
                    PressCount: 0,
                    HoldDurationMicroseconds: duration);
            }

            if (date == lastDate)
            {
                break;
            }

            date = nextDate;
        }

        if (allocated != endUtc - startUtc)
        {
            throw new InvalidDataException("Keyboard hold daily intervals did not cover the complete UTC duration.");
        }

    }

    private static DateOnly GetNextDate(DateOnly date)
    {
        try
        {
            return date.AddDays(1);
        }
        catch (ArgumentOutOfRangeException error)
        {
            throw new InvalidDataException("A keyboard hold exceeds the supported local-date range.", error);
        }
    }

    private static long GetLocalMidnightUtcMicroseconds(DateOnly date, TimeZoneInfo zone)
    {
        DateTime localMidnight = DateTime.SpecifyKind(
            date.ToDateTime(TimeOnly.MinValue),
            DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(localMidnight))
        {
            throw new InvalidDataException("A Windows time-zone local midnight is invalid.");
        }

        try
        {
            DateTime utc = TimeZoneInfo.ConvertTimeToUtc(localMidnight, zone);
            return checked((utc.Ticks - UnixEpochTicks) / 10);
        }
        catch (ArgumentException error)
        {
            throw new InvalidDataException("A Windows time-zone local date cannot be mapped to UTC.", error);
        }
    }

    private static DateTimeOffset FromUnixMicroseconds(long value)
    {
        try
        {
            long ticks = checked(UnixEpochTicks + checked(value * 10));
            return new DateTimeOffset(new DateTime(ticks, DateTimeKind.Utc));
        }
        catch (ArgumentOutOfRangeException error)
        {
            throw new InvalidDataException("A calibrated UTC value is outside the supported DateTime range.", error);
        }
        catch (OverflowException error)
        {
            throw new InvalidDataException("A calibrated UTC value overflowed the supported range.", error);
        }
    }
}
