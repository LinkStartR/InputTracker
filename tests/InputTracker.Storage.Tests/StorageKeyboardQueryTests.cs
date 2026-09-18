namespace InputTracker.Storage.Tests;

public sealed class StorageKeyboardQueryTests
{
    [Fact]
    public void CustomRangeIncludesBothUiEndpointDatesAndExcludesAdjacentDates()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid keyboard = Guid.NewGuid();
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock()))
        {
            database.RegisterDevice(keyboard, (int)StorageDeviceKind.Keyboard, 0, 1);
            database.UpsertKeyboardDaily(keyboard, 30, false, false, new DateOnly(2026, 9, 9), "UTC", 1, 10);
            database.UpsertKeyboardDaily(keyboard, 30, false, false, new DateOnly(2026, 9, 10), "UTC", 2, 20);
            database.UpsertKeyboardDaily(keyboard, 30, false, false, new DateOnly(2026, 9, 12), "UTC", 4, 40);
            database.UpsertKeyboardDaily(keyboard, 30, false, false, new DateOnly(2026, 9, 13), "UTC", 8, 80);
        }

        using StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock());
        StorageKeyboardPeriodResult result = service.QueryKeyboardRange(
            new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 12), "UTC", keyboard);

        Assert.Equal(StoragePeriodKind.Custom, result.Period.Kind);
        Assert.Equal(6, Assert.Single(result.Aggregates).PressCount);
    }

    [Fact]
    public void QueryAggregatesOnlyRequestedCaptureZoneAndKeepsDevicesAndPhysicalKeysSeparate()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        Guid mouse = Guid.NewGuid();
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock()))
        {
            database.RegisterDevice(first, (int)StorageDeviceKind.Keyboard, 0, 1);
            database.RegisterDevice(second, (int)StorageDeviceKind.Keyboard, 0, 1);
            database.RegisterDevice(mouse, (int)StorageDeviceKind.Mouse, 0, 1);
            database.UpsertKeyboardDaily(first, 30, false, false, new DateOnly(2026, 8, 10), "UTC", 2, 20);
            database.UpsertKeyboardDaily(first, 30, false, false, new DateOnly(2026, 8, 12), "UTC", 3, 30);
            database.UpsertKeyboardDaily(first, 30, true, false, new DateOnly(2026, 8, 12), "UTC", 5, 50);
            database.UpsertKeyboardDaily(second, 30, false, false, new DateOnly(2026, 8, 16), "UTC", 7, 70);
            database.UpsertKeyboardDaily(first, 30, false, false, new DateOnly(2026, 8, 12), "China Standard Time", 11, 110);
            database.UpsertKeyboardDaily(mouse, 30, false, false, new DateOnly(2026, 8, 12), "UTC", 13, 130);
        }

        using StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock());
        StorageKeyboardPeriodResult result = service.QueryKeyboardPeriod(
            StoragePeriodKind.Week,
            new DateOnly(2026, 8, 13),
            "UTC");

        Assert.Equal(new DateOnly(2026, 8, 10), result.Period.LocalStartDate);
        Assert.Equal(new DateOnly(2026, 8, 17), result.Period.LocalEndDate);
        Assert.Equal(["China Standard Time", "UTC"], result.AvailableCaptureTimeZoneIds);
        Assert.Equal(3, result.Aggregates.Count);
        StorageKeyboardPeriodAggregate firstPlain = Assert.Single(result.Aggregates,
            aggregate => aggregate.DeviceId == first && !aggregate.IsE0 && !aggregate.IsE1);
        Assert.Equal((ushort)30, firstPlain.MakeCode);
        Assert.Equal(5, firstPlain.PressCount);
        Assert.Equal(50, firstPlain.HoldDurationMicroseconds);
        StorageKeyboardPeriodAggregate firstExtended = Assert.Single(result.Aggregates,
            aggregate => aggregate.DeviceId == first && aggregate.IsE0 && !aggregate.IsE1);
        Assert.Equal(5, firstExtended.PressCount);
        Assert.Equal(50, firstExtended.HoldDurationMicroseconds);
        StorageKeyboardPeriodAggregate secondPlain = Assert.Single(result.Aggregates,
            aggregate => aggregate.DeviceId == second && !aggregate.IsE0 && !aggregate.IsE1);
        Assert.Equal(7, secondPlain.PressCount);
        Assert.Equal(70, secondPlain.HoldDurationMicroseconds);
    }

    [Fact]
    public void DeviceFilterReturnsOnlyThatKeyboardAndUnknownOrMouseFailsClosed()
    {
        using var directory = new TemporaryDirectory();
        StorageLayout layout = StorageLayout.Create(directory.Path);
        Guid keyboard = Guid.NewGuid();
        Guid other = Guid.NewGuid();
        Guid mouse = Guid.NewGuid();
        using (StorageDatabase database = StorageDatabase.Open(layout, new FakeClock()))
        {
            database.RegisterDevice(keyboard, (int)StorageDeviceKind.Keyboard, 0, 1);
            database.RegisterDevice(other, (int)StorageDeviceKind.Keyboard, 0, 1);
            database.RegisterDevice(mouse, (int)StorageDeviceKind.Mouse, 0, 1);
            database.UpsertKeyboardDaily(keyboard, 30, false, false, new DateOnly(2026, 8, 13), "UTC", 2, 20);
            database.UpsertKeyboardDaily(other, 31, false, false, new DateOnly(2026, 8, 13), "UTC", 3, 30);
        }

        using StorageService service = StorageService.OpenForTests(layout, new ControllableStorageClock());
        StorageKeyboardPeriodResult result = service.QueryKeyboardPeriod(
            StoragePeriodKind.Day,
            new DateOnly(2026, 8, 13),
            "UTC",
            keyboard);

        Assert.Equal(keyboard, Assert.Single(result.Aggregates).DeviceId);
        Assert.Throws<InvalidDataException>(() => service.QueryKeyboardPeriod(
            StoragePeriodKind.Day, new DateOnly(2026, 8, 13), "UTC", Guid.NewGuid()));
        Assert.Throws<InvalidDataException>(() => service.QueryKeyboardPeriod(
            StoragePeriodKind.Day, new DateOnly(2026, 8, 13), "UTC", mouse));
        Assert.Throws<ArgumentException>(() => service.QueryKeyboardPeriod(
            StoragePeriodKind.Day, new DateOnly(2026, 8, 13), "UTC", Guid.Empty));
    }

    [Fact]
    public void QueryRejectsDisposedService()
    {
        using var directory = new TemporaryDirectory();
        StorageService service = StorageService.OpenForTests(
            StorageLayout.Create(directory.Path),
            new ControllableStorageClock());
        service.Dispose();

        Assert.Throws<ObjectDisposedException>(() => service.QueryKeyboardPeriod(
            StoragePeriodKind.Day,
            new DateOnly(2026, 8, 13),
            "UTC"));
    }
}
