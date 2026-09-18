using InputTracker.Storage;
using System.IO;

namespace InputTracker;

internal interface IDashboardDataSource
{
    Task<IReadOnlyList<StorageDeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken);
    Task<StorageKeyboardPeriodResult> QueryKeyboardAsync(
        StoragePeriodKind kind,
        DateOnly date,
        string timeZoneId,
        Guid? deviceId,
        CancellationToken cancellationToken);
    Task<StorageMouseButtonPeriodResult> QueryMouseButtonsAsync(
        StoragePeriodKind kind,
        DateOnly date,
        string timeZoneId,
        Guid? deviceId,
        CancellationToken cancellationToken);
    Task<StorageKeyboardPeriodResult> QueryKeyboardRangeAsync(
        DateOnly inclusiveStartDate,
        DateOnly inclusiveEndDate,
        string timeZoneId,
        Guid? deviceId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Custom keyboard ranges are not implemented by this data source.");
    Task<StorageMouseButtonPeriodResult> QueryMouseButtonsRangeAsync(
        DateOnly inclusiveStartDate,
        DateOnly inclusiveEndDate,
        string timeZoneId,
        Guid? deviceId,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Custom mouse ranges are not implemented by this data source.");
    Task<StorageDeviceInfo> SetDeviceAliasAsync(
        Guid deviceId,
        string? alias,
        CancellationToken cancellationToken);
}

internal sealed class StorageDashboardDataSource(StorageService service) : IDashboardDataSource
{
    public Task<IReadOnlyList<StorageDeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(service.ListDevices());
    }

    public Task<StorageKeyboardPeriodResult> QueryKeyboardAsync(
        StoragePeriodKind kind,
        DateOnly date,
        string timeZoneId,
        Guid? deviceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(service.QueryKeyboardPeriod(kind, date, timeZoneId, deviceId));
    }

    public Task<StorageMouseButtonPeriodResult> QueryMouseButtonsAsync(
        StoragePeriodKind kind,
        DateOnly date,
        string timeZoneId,
        Guid? deviceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(service.QueryMouseButtonsPeriod(kind, date, timeZoneId, deviceId));
    }

    public Task<StorageKeyboardPeriodResult> QueryKeyboardRangeAsync(
        DateOnly inclusiveStartDate,
        DateOnly inclusiveEndDate,
        string timeZoneId,
        Guid? deviceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(service.QueryKeyboardRange(
            inclusiveStartDate, inclusiveEndDate, timeZoneId, deviceId));
    }

    public Task<StorageMouseButtonPeriodResult> QueryMouseButtonsRangeAsync(
        DateOnly inclusiveStartDate,
        DateOnly inclusiveEndDate,
        string timeZoneId,
        Guid? deviceId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(service.QueryMouseButtonsRange(
            inclusiveStartDate, inclusiveEndDate, timeZoneId, deviceId));
    }

    public Task<StorageDeviceInfo> SetDeviceAliasAsync(
        Guid deviceId,
        string? alias,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(service.SetDeviceAlias(deviceId, alias));
    }
}

internal sealed record HeatmapCell(
    ushort MakeCode,
    bool IsE0,
    bool IsE1,
    long PressCount,
    long HoldDurationMicroseconds);

internal sealed record MouseButtonCounts(
    long Left,
    long Right,
    long Middle,
    long X1,
    long X2)
{
    public static MouseButtonCounts Empty { get; } = new(0, 0, 0, 0, 0);
}

internal sealed class DashboardViewModel
{
    private readonly IDashboardDataSource _dataSource;
    private CancellationTokenSource? _refreshCancellation;

    public DashboardViewModel(IDashboardDataSource dataSource) =>
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public StoragePeriodKind Period { get; set; } = StoragePeriodKind.Day;
    public DateOnly AnchorDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public DateOnly? CustomStartDate { get; set; }
    public DateOnly? CustomEndDate { get; set; }
    public string TimeZoneId { get; set; } = TimeZoneInfo.Local.Id;
    public Guid? SelectedKeyboardDeviceId { get; set; }
    public Guid? SelectedMouseDeviceId { get; set; }
    public IReadOnlyList<StorageDeviceInfo> Devices { get; private set; } = [];
    public IReadOnlyList<HeatmapCell> Heatmap { get; private set; } = [];
    public MouseButtonCounts MouseButtons { get; private set; } = MouseButtonCounts.Empty;
    public StorageResolvedPeriod? ResolvedPeriod { get; private set; }
    public long TotalPressCount { get; private set; }
    public long TotalHoldDurationMicroseconds { get; private set; }
    public StorageKeyboardPeriodResult? KeyboardPeriodResult { get; private set; }

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken token = _refreshCancellation.Token;

        DashboardLoadResult loaded = await Task.Run(
            () => LoadAsync(
                Period, AnchorDate, TimeZoneId,
                CustomStartDate, CustomEndDate,
                SelectedKeyboardDeviceId, SelectedMouseDeviceId, token), token);

        token.ThrowIfCancellationRequested();
        Devices = loaded.Devices;
        ResolvedPeriod = loaded.Keyboard.Period;
        KeyboardPeriodResult = loaded.Keyboard;
        MouseButtons = ToMouseButtonCounts(loaded.MouseButtons.Aggregates, SelectedMouseDeviceId);
        Heatmap = loaded.Keyboard.Aggregates.Select(value => new HeatmapCell(
            value.MakeCode, value.IsE0, value.IsE1,
            value.PressCount, value.HoldDurationMicroseconds)).ToArray();
        TotalPressCount = loaded.Keyboard.Aggregates.Sum(value => value.PressCount);
        TotalHoldDurationMicroseconds = loaded.Keyboard.Aggregates.Sum(value => value.HoldDurationMicroseconds);
    }

    public async Task<StorageDeviceInfo> SetDeviceAliasAsync(
        Guid deviceId,
        string? alias,
        CancellationToken cancellationToken = default)
    {
        if (deviceId == Guid.Empty || !Devices.Any(device => device.DeviceId == deviceId))
        {
            throw new InvalidOperationException("Please select an existing device before changing its alias.");
        }

        StorageDeviceInfo updated = await Task.Run(
            () => _dataSource.SetDeviceAliasAsync(deviceId, alias, cancellationToken),
            cancellationToken);
        Devices = Devices.Select(device => device.DeviceId == deviceId ? updated : device).ToArray();
        return updated;
    }

    private async Task<DashboardLoadResult> LoadAsync(
        StoragePeriodKind period, DateOnly anchor, string zone,
        DateOnly? customStartDate, DateOnly? customEndDate,
        Guid? keyboardDevice, Guid? mouseDevice, CancellationToken token)
    {
        IReadOnlyList<StorageDeviceInfo> devices = await _dataSource.ListDevicesAsync(token).ConfigureAwait(false);
        if (customStartDate.HasValue != customEndDate.HasValue)
            throw new InvalidOperationException("Custom range requires both a start date and an end date.");
        StorageKeyboardPeriodResult keyboard;
        StorageMouseButtonPeriodResult mouseButtons;
        if (customStartDate is DateOnly start && customEndDate is DateOnly end)
        {
            keyboard = await _dataSource.QueryKeyboardRangeAsync(
                start, end, zone, keyboardDevice, token).ConfigureAwait(false);
            mouseButtons = await _dataSource.QueryMouseButtonsRangeAsync(
                start, end, zone, mouseDevice, token).ConfigureAwait(false);
        }
        else
        {
            keyboard = await _dataSource.QueryKeyboardAsync(
                period, anchor, zone, keyboardDevice, token).ConfigureAwait(false);
            mouseButtons = await _dataSource.QueryMouseButtonsAsync(
                period, anchor, zone, mouseDevice, token).ConfigureAwait(false);
        }
        return new DashboardLoadResult(devices, keyboard, mouseButtons);
    }

    private static MouseButtonCounts ToMouseButtonCounts(
        IReadOnlyList<StorageMouseButtonPeriodAggregate> aggregates,
        Guid? selectedDeviceId)
    {
        long left = 0;
        long right = 0;
        long middle = 0;
        long x1 = 0;
        long x2 = 0;
        foreach (StorageMouseButtonPeriodAggregate row in aggregates)
        {
            if (selectedDeviceId is Guid selected && row.DeviceId != selected)
            {
                continue;
            }

            switch (row.Button)
            {
                case StorageMouseButton.Left: left = checked(left + row.PressCount); break;
                case StorageMouseButton.Right: right = checked(right + row.PressCount); break;
                case StorageMouseButton.Middle: middle = checked(middle + row.PressCount); break;
                case StorageMouseButton.X1: x1 = checked(x1 + row.PressCount); break;
                case StorageMouseButton.X2: x2 = checked(x2 + row.PressCount); break;
                default: throw new InvalidDataException("Mouse button aggregate contains an unknown button.");
            }
        }

        return new MouseButtonCounts(left, right, middle, x1, x2);
    }

    private sealed record DashboardLoadResult(
        IReadOnlyList<StorageDeviceInfo> Devices,
        StorageKeyboardPeriodResult Keyboard,
        StorageMouseButtonPeriodResult MouseButtons);
}
