namespace InputTracker.Storage;

public static class StoragePeriodResolver
{
    private const long UnixEpochTicks = 621355968000000000L;

    public static StorageResolvedPeriod Resolve(
        StoragePeriodKind kind,
        DateOnly containingDate,
        string timeZoneId)
    {
        TimeZoneInfo zone = ResolveWindowsTimeZone(timeZoneId);
        (DateOnly start, DateOnly end) = GetLocalDates(kind, containingDate);
        long startUtc = GetBoundaryUtcMicroseconds(start, zone);
        long endUtc = GetBoundaryUtcMicroseconds(end, zone);
        var range = new StorageUtcRange(startUtc, endUtc);
        try
        {
            range.Validate();
        }
        catch (ArgumentOutOfRangeException error)
        {
            throw new InvalidDataException("The resolved period does not form a non-empty UTC range.", error);
        }

        return new StorageResolvedPeriod(kind, start, end, zone.Id, range);
    }

    public static StorageResolvedPeriod ResolveRange(
        DateOnly inclusiveStartDate,
        DateOnly inclusiveEndDate,
        string timeZoneId)
    {
        if (inclusiveEndDate < inclusiveStartDate)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inclusiveEndDate),
                "The inclusive end date must not be earlier than the start date.");
        }

        DateOnly exclusiveEndDate;
        try
        {
            exclusiveEndDate = inclusiveEndDate.AddDays(1);
        }
        catch (ArgumentOutOfRangeException error)
        {
            throw new InvalidDataException("The custom range end is outside the supported date range.", error);
        }

        TimeZoneInfo zone = ResolveWindowsTimeZone(timeZoneId);
        var range = new StorageUtcRange(
            GetBoundaryUtcMicroseconds(inclusiveStartDate, zone),
            GetBoundaryUtcMicroseconds(exclusiveEndDate, zone));
        try
        {
            range.Validate();
        }
        catch (ArgumentOutOfRangeException error)
        {
            throw new InvalidDataException("The custom dates do not form a non-empty UTC range.", error);
        }

        return new StorageResolvedPeriod(
            StoragePeriodKind.Custom,
            inclusiveStartDate,
            exclusiveEndDate,
            zone.Id,
            range);
    }

    private static TimeZoneInfo ResolveWindowsTimeZone(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId)
            || timeZoneId.Contains('/', StringComparison.Ordinal))
        {
            throw new InvalidDataException("An explicit Windows time-zone identifier is required.");
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new InvalidDataException("The explicit Windows time-zone identifier is unavailable or invalid.", error);
        }
    }

    private static (DateOnly Start, DateOnly End) GetLocalDates(
        StoragePeriodKind kind,
        DateOnly containingDate)
    {
        try
        {
            DateOnly start = kind switch
            {
                StoragePeriodKind.Day => containingDate,
                StoragePeriodKind.Week => containingDate.AddDays(-GetIsoWeekOffset(containingDate.DayOfWeek)),
                StoragePeriodKind.Month => new DateOnly(containingDate.Year, containingDate.Month, 1),
                StoragePeriodKind.Year => new DateOnly(containingDate.Year, 1, 1),
                _ => throw new InvalidDataException("The storage period kind is invalid.")
            };
            DateOnly end = kind switch
            {
                StoragePeriodKind.Day => start.AddDays(1),
                StoragePeriodKind.Week => start.AddDays(7),
                StoragePeriodKind.Month => start.AddMonths(1),
                StoragePeriodKind.Year => start.AddYears(1),
                _ => throw new InvalidDataException("The storage period kind is invalid.")
            };
            return (start, end);
        }
        catch (ArgumentOutOfRangeException error)
        {
            throw new InvalidDataException("The requested local period is outside the supported date range.", error);
        }
    }

    private static int GetIsoWeekOffset(DayOfWeek dayOfWeek) =>
        ((int)dayOfWeek + 6) % 7;

    private static long GetBoundaryUtcMicroseconds(DateOnly date, TimeZoneInfo zone)
    {
        DateTime local = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local))
        {
            throw new InvalidDataException("A local period boundary is invalid or ambiguous in the selected time zone.");
        }

        try
        {
            DateTime utc = TimeZoneInfo.ConvertTimeToUtc(local, zone);
            return checked((utc.Ticks - UnixEpochTicks) / 10);
        }
        catch (Exception error) when (error is ArgumentException or OverflowException)
        {
            throw new InvalidDataException("A local period boundary cannot be represented as UTC microseconds.", error);
        }
    }
}
