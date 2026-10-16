namespace InputTracker.Storage.Tests;

public sealed class StoragePeriodResolverTests
{
    [Theory]
    [InlineData(StoragePeriodKind.Day, "2026-08-13", "2026-08-13", "2026-08-14")]
    [InlineData(StoragePeriodKind.Week, "2026-08-13", "2026-08-10", "2026-08-17")]
    [InlineData(StoragePeriodKind.Month, "2026-08-13", "2026-08-01", "2026-09-01")]
    [InlineData(StoragePeriodKind.Year, "2026-08-13", "2026-01-01", "2027-01-01")]
    public void ResolvesLocalHalfOpenPeriodBoundaries(
        StoragePeriodKind kind,
        string containingDate,
        string expectedStart,
        string expectedEnd)
    {
        StorageResolvedPeriod period = StoragePeriodResolver.Resolve(
            kind,
            DateOnly.Parse(containingDate),
            "UTC");

        Assert.Equal(DateOnly.Parse(expectedStart), period.LocalStartDate);
        Assert.Equal(DateOnly.Parse(expectedEnd), period.LocalEndDate);
        Assert.Equal("UTC", period.TimeZoneId);
        Assert.True(period.UtcRange.EndUtcMicroseconds > period.UtcRange.StartUtcMicroseconds);
    }

    [Theory]
    [InlineData("2026-03-08", 23)]
    [InlineData("2026-11-01", 25)]
    public void PacificDayUsesActualDstUtcDuration(string date, int expectedHours)
    {
        StorageResolvedPeriod period = StoragePeriodResolver.Resolve(
            StoragePeriodKind.Day,
            DateOnly.Parse(date),
            "Pacific Standard Time");

        Assert.Equal(expectedHours * 60L * 60L * 1_000_000L,
            period.UtcRange.EndUtcMicroseconds - period.UtcRange.StartUtcMicroseconds);
    }

    [Fact]
    public void ChinaAndUtcUseExplicitWindowsZones()
    {
        DateOnly date = new(2026, 8, 13);
        StorageResolvedPeriod china = StoragePeriodResolver.Resolve(
            StoragePeriodKind.Day, date, "China Standard Time");
        StorageResolvedPeriod utc = StoragePeriodResolver.Resolve(
            StoragePeriodKind.Day, date, "UTC");

        Assert.Equal(24L * 60L * 60L * 1_000_000L,
            china.UtcRange.EndUtcMicroseconds - china.UtcRange.StartUtcMicroseconds);
        Assert.Equal(8L * 60L * 60L * 1_000_000L,
            utc.UtcRange.StartUtcMicroseconds - china.UtcRange.StartUtcMicroseconds);
    }

    [Fact]
    public void CustomRangeUsesInclusiveUiDatesAndActualDstBoundaries()
    {
        StorageResolvedPeriod period = StoragePeriodResolver.ResolveRange(
            new DateOnly(2026, 3, 7),
            new DateOnly(2026, 3, 9),
            "Pacific Standard Time");

        Assert.Equal(StoragePeriodKind.Custom, period.Kind);
        Assert.Equal(new DateOnly(2026, 3, 7), period.LocalStartDate);
        Assert.Equal(new DateOnly(2026, 3, 10), period.LocalEndDate);
        Assert.Equal(71L * 60L * 60L * 1_000_000L,
            period.UtcRange.EndUtcMicroseconds - period.UtcRange.StartUtcMicroseconds);
        Assert.Throws<ArgumentOutOfRangeException>(() => StoragePeriodResolver.ResolveRange(
            new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 9), "UTC"));
        Assert.Throws<InvalidDataException>(() => StoragePeriodResolver.Resolve(
            StoragePeriodKind.Custom, new DateOnly(2026, 3, 9), "UTC"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("America/Los_Angeles")]
    [InlineData("Missing Windows Zone")]
    public void RejectsBlankIanaAndUnavailableZoneIdentifiers(string timeZoneId)
    {
        Assert.Throws<InvalidDataException>(() => StoragePeriodResolver.Resolve(
            StoragePeriodKind.Day,
            new DateOnly(2026, 8, 13),
            timeZoneId));
    }

    [Fact]
    public void RejectsUnknownPeriodKind()
    {
        Assert.Throws<InvalidDataException>(() => StoragePeriodResolver.Resolve(
            (StoragePeriodKind)99,
            new DateOnly(2026, 8, 13),
            "UTC"));
    }
}
