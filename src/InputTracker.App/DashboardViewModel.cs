using InputTracker.Storage;
using System.Collections.ObjectModel;
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
    IAsyncEnumerable<StorageMouseRecord> StreamMouseAsync(
        Guid deviceId,
        StorageUtcRange range,
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

    public IAsyncEnumerable<StorageMouseRecord> StreamMouseAsync(
        Guid deviceId,
        StorageUtcRange range,
        CancellationToken cancellationToken) =>
        service.StreamMouseAsync(deviceId, range, cancellationToken);
}

internal sealed record HeatmapCell(
    ushort MakeCode,
    bool IsE0,
    bool IsE1,
    long PressCount,
    long HoldDurationMicroseconds);

internal sealed record MouseDisplayPoint(
    long LastSequence,
    long LastUtcMicroseconds,
    long DeltaX,
    long DeltaY,
    byte MotionKind,
    bool UsesVirtualDesktop,
    bool StartsNewSegment);

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
    internal const int MaxRenderedMousePoints = 2048;
    private readonly IDashboardDataSource _dataSource;
    private CancellationTokenSource? _refreshCancellation;

    public DashboardViewModel(IDashboardDataSource dataSource) =>
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));

    public StoragePeriodKind Period { get; set; } = StoragePeriodKind.Day;
    public DateOnly AnchorDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public string TimeZoneId { get; set; } = TimeZoneInfo.Local.Id;
    public Guid? SelectedKeyboardDeviceId { get; set; }
    public Guid? SelectedMouseDeviceId { get; set; }
    public IReadOnlyList<StorageDeviceInfo> Devices { get; private set; } = [];
    public IReadOnlyList<HeatmapCell> Heatmap { get; private set; } = [];
    public IReadOnlyList<MouseDisplayPoint> MousePoints { get; private set; } = [];
    public long TotalMousePointCount { get; private set; }
    public MouseButtonCounts MouseButtons { get; private set; } = MouseButtonCounts.Empty;
    public StorageResolvedPeriod? ResolvedPeriod { get; private set; }
    public long TotalPressCount { get; private set; }
    public long TotalHoldDurationMicroseconds { get; private set; }
    public StorageKeyboardPeriodResult? KeyboardPeriodResult { get; private set; }

    public async Task RefreshAsync(bool includeMouse = true, CancellationToken cancellationToken = default)
    {
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken token = _refreshCancellation.Token;

        DashboardLoadResult loaded = await Task.Run(
            () => LoadAsync(
                Period, AnchorDate, TimeZoneId,
                SelectedKeyboardDeviceId, SelectedMouseDeviceId, includeMouse, token), token);

        token.ThrowIfCancellationRequested();
        Devices = loaded.Devices;
        ResolvedPeriod = loaded.Keyboard.Period;
        KeyboardPeriodResult = loaded.Keyboard;
        MouseButtons = ToMouseButtonCounts(loaded.MouseButtons.Aggregates, SelectedMouseDeviceId);
        Heatmap = loaded.Keyboard.Aggregates.Select(value => new HeatmapCell(
            value.MakeCode, value.IsE0, value.IsE1,
            value.PressCount, value.HoldDurationMicroseconds)).ToArray();
        if (loaded.Mouse is { } mouse)
        {
            MousePoints = new ReadOnlyCollection<MouseDisplayPoint>(mouse.Points);
            TotalMousePointCount = mouse.Total;
        }
        TotalPressCount = loaded.Keyboard.Aggregates.Sum(value => value.PressCount);
        TotalHoldDurationMicroseconds = loaded.Keyboard.Aggregates.Sum(value => value.HoldDurationMicroseconds);
    }

    private async Task<DashboardLoadResult> LoadAsync(
        StoragePeriodKind period, DateOnly anchor, string zone,
        Guid? keyboardDevice, Guid? mouseDevice, bool includeMouse, CancellationToken token)
    {
        IReadOnlyList<StorageDeviceInfo> devices = await _dataSource.ListDevicesAsync(token).ConfigureAwait(false);
        StorageKeyboardPeriodResult keyboard = await _dataSource.QueryKeyboardAsync(
            period, anchor, zone, keyboardDevice, token).ConfigureAwait(false);
        StorageMouseButtonPeriodResult mouseButtons = await _dataSource.QueryMouseButtonsAsync(
            period, anchor, zone, mouseDevice, token).ConfigureAwait(false);
        MouseDisplayResult? mouse = includeMouse && mouseDevice is Guid device
            ? await BucketMouseAsync(device, keyboard.Period.UtcRange, token).ConfigureAwait(false)
            : includeMouse ? new MouseDisplayResult(0, []) : null;
        return new DashboardLoadResult(devices, keyboard, mouseButtons, mouse);
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

    private async Task<MouseDisplayResult> BucketMouseAsync(
        Guid deviceId, StorageUtcRange range, CancellationToken token)
    {
        var buckets = new MouseBucket[MaxRenderedMousePoints];
        long total = 0;
        Int128 duration = range.EndUtcMicroseconds - range.StartUtcMicroseconds;
        await foreach (StorageMouseRecord point in _dataSource.StreamMouseAsync(
            deviceId, range, token).WithCancellation(token).ConfigureAwait(false))
        {
            total++;
            Int128 offset = Math.Clamp(point.UtcMicroseconds, range.StartUtcMicroseconds, range.EndUtcMicroseconds - 1)
                - range.StartUtcMicroseconds;
            int index = (int)((offset * MaxRenderedMousePoints) / duration);
            ref MouseBucket bucket = ref buckets[Math.Clamp(index, 0, MaxRenderedMousePoints - 1)];
            bucket.Add(point);
        }

        MouseDisplayPoint[] points = buckets.Where(value => value.Count > 0)
            .Select(value => value.ToPoint()).ToArray();
        return new MouseDisplayResult(total, points);
    }

    private struct MouseBucket
    {
        public long Count;
        public long DeltaX;
        public long DeltaY;
        public long LastSequence;
        public long LastUtc;
        public byte MotionKind;
        public bool UsesVirtualDesktop;
        public bool StartsNewSegment;
        public void Add(StorageMouseRecord point)
        {
            bool sameSegment = Count == 0
                || (MotionKind == point.MotionKind
                    && UsesVirtualDesktop == point.UsesVirtualDesktop);
            if (Count > 0 && sameSegment && point.MotionKind == 0)
            {
                DeltaX = checked(DeltaX + point.DeltaX);
                DeltaY = checked(DeltaY + point.DeltaY);
            }
            else
            {
                DeltaX = point.DeltaX;
                DeltaY = point.DeltaY;
            }

            StartsNewSegment |= Count > 0 && !sameSegment;
            Count++;
            MotionKind = point.MotionKind;
            UsesVirtualDesktop = point.UsesVirtualDesktop;
            LastSequence = point.Sequence;
            LastUtc = point.UtcMicroseconds;
        }
        public readonly MouseDisplayPoint ToPoint() =>
            new(LastSequence, LastUtc, DeltaX, DeltaY, MotionKind, UsesVirtualDesktop, StartsNewSegment);
    }

    private sealed record MouseDisplayResult(long Total, MouseDisplayPoint[] Points);
    private sealed record DashboardLoadResult(
        IReadOnlyList<StorageDeviceInfo> Devices,
        StorageKeyboardPeriodResult Keyboard,
        StorageMouseButtonPeriodResult MouseButtons,
        MouseDisplayResult? Mouse);
}
