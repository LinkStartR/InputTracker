namespace InputTracker.App.Tests;

using InputTracker.Storage;

public sealed class DashboardViewModelTests
{
    [Fact]
    public async Task RefreshUsesSelectedPeriodDeviceAndStreamsEveryMousePoint()
    {
        Guid keyboard = Guid.NewGuid();
        Guid mouse = Guid.NewGuid();
        var source = new FakeDashboardDataSource(keyboard, mouse);
        var model = new DashboardViewModel(source)
        {
            Period = StoragePeriodKind.Week,
            AnchorDate = new DateOnly(2026, 8, 13),
            TimeZoneId = "China Standard Time",
            SelectedKeyboardDeviceId = keyboard,
            SelectedMouseDeviceId = mouse
        };

        await model.RefreshAsync();

        Assert.Equal((StoragePeriodKind.Week, keyboard), source.KeyboardRequest);
        Assert.Equal(mouse, source.MouseRequest);
        Assert.Equal(7, model.TotalPressCount);
        Assert.Equal(2, model.Heatmap.Count);
        Assert.InRange(model.MousePoints.Count, 1, 3);
        Assert.Equal(3, model.TotalMousePointCount);
        Assert.Equal(3, model.MousePoints[^1].LastSequence);
    }

    [Fact]
    public async Task MillionPointStreamIsFullyReadButDisplayMemoryIsBounded()
    {
        Guid mouse = Guid.NewGuid();
        int callerThread = Environment.CurrentManagedThreadId;
        var source = new LargeMouseDataSource(mouse);
        var model = new DashboardViewModel(source)
        {
            SelectedMouseDeviceId = mouse,
            TimeZoneId = "UTC"
        };

        await model.RefreshAsync();

        Assert.Equal(1_000_000, model.TotalMousePointCount);
        Assert.Equal(DashboardViewModel.MaxRenderedMousePoints, model.MousePoints.Count);
        Assert.Equal(1_000_000, model.MousePoints[^1].LastSequence);
        Assert.Equal(1_000_000, model.MousePoints.Sum(point => point.DeltaX));
        Assert.Equal(1_000_000, model.MousePoints.Sum(point => point.DeltaY));
        Assert.Equal(1_000_000, source.EnumeratedCount);
        Assert.DoesNotContain(callerThread, source.EnumerationThreads);
        StorageUtcRange range = model.ResolvedPeriod!.UtcRange;
        long bucketWidth = (range.EndUtcMicroseconds - range.StartUtcMicroseconds) / DashboardViewModel.MaxRenderedMousePoints;
        Assert.InRange(model.MousePoints[0].LastUtcMicroseconds,
            range.StartUtcMicroseconds, range.StartUtcMicroseconds + bucketWidth);
        Assert.InRange(model.MousePoints[^1].LastUtcMicroseconds,
            range.EndUtcMicroseconds - bucketWidth - 1, range.EndUtcMicroseconds - 1);
    }

    [Fact]
    public void ProjectionKeepsAbsoluteCoordinatesSeparateFromRelativeMotion()
    {
        MouseDisplayPoint[] points =
        [
            new(1, 1, 10, -5, MotionKind: 0, UsesVirtualDesktop: false, StartsNewSegment: false),
            new(2, 2, 65_535, 0, MotionKind: 1, UsesVirtualDesktop: true, StartsNewSegment: true),
            new(3, 3, -10, 5, MotionKind: 0, UsesVirtualDesktop: false, StartsNewSegment: true)
        ];

        IReadOnlyList<IReadOnlyList<System.Windows.Point>> projected =
            MainWindow.ProjectMouseSegments(points, 100, 100);

        Assert.Equal(3, projected.Count);
        Assert.Equal(new System.Windows.Point(60, 45), Assert.Single(projected[0]));
        Assert.Equal(new System.Windows.Point(100, 0), Assert.Single(projected[1]));
        Assert.Equal(new System.Windows.Point(40, 55), Assert.Single(projected[2]));
    }

    private sealed class FakeDashboardDataSource(Guid keyboard, Guid mouse) : IDashboardDataSource
    {
        public (StoragePeriodKind, Guid?) KeyboardRequest { get; private set; }
        public Guid MouseRequest { get; private set; }

        public Task<IReadOnlyList<StorageDeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StorageDeviceInfo>>([
                new(keyboard, StorageDeviceKind.Keyboard, StorageDeviceIdentityConfidence.TransientInterface,
                    StorageDeviceIdentityAmbiguity.MissingPermanentIdentifier, "Keyboard", 0, 0),
                new(mouse, StorageDeviceKind.Mouse, StorageDeviceIdentityConfidence.PersistentHardware,
                    StorageDeviceIdentityAmbiguity.None, "Mouse", 0, 0)]);

        public Task<StorageKeyboardPeriodResult> QueryKeyboardAsync(
            StoragePeriodKind kind, DateOnly date, string zone, Guid? deviceId, CancellationToken cancellationToken)
        {
            KeyboardRequest = (kind, deviceId);
            return Task.FromResult(new StorageKeyboardPeriodResult(
                StoragePeriodResolver.Resolve(kind, date, zone),
                [new(keyboard, 30, false, false, 4, 10), new(keyboard, 31, true, false, 3, 20)], [zone]));
        }

        public async IAsyncEnumerable<StorageMouseRecord> StreamMouseAsync(
            Guid deviceId, StorageUtcRange range,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            MouseRequest = deviceId;
            for (long sequence = 1; sequence <= 3; sequence++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new StorageMouseRecord(deviceId, sequence, range.StartUtcMicroseconds + sequence,
                    1, 2, 0, false, 0, 0, 0);
                await Task.Yield();
            }
        }
    }

    private sealed class LargeMouseDataSource(Guid mouse) : IDashboardDataSource
    {
        public long EnumeratedCount { get; private set; }
        public HashSet<int> EnumerationThreads { get; } = [];
        public Task<IReadOnlyList<StorageDeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StorageDeviceInfo>>([]);
        public Task<StorageKeyboardPeriodResult> QueryKeyboardAsync(
            StoragePeriodKind kind, DateOnly date, string zone, Guid? deviceId, CancellationToken cancellationToken) =>
            Task.FromResult(new StorageKeyboardPeriodResult(
                StoragePeriodResolver.Resolve(kind, date, zone), [], []));
        public async IAsyncEnumerable<StorageMouseRecord> StreamMouseAsync(
            Guid deviceId, StorageUtcRange range,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (long sequence = 1; sequence <= 1_000_000; sequence++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnumeratedCount++;
                EnumerationThreads.Add(Environment.CurrentManagedThreadId);
                long utc = range.StartUtcMicroseconds
                    + (long)(((Int128)(range.EndUtcMicroseconds - range.StartUtcMicroseconds) * (sequence - 1)) / 1_000_000);
                yield return new StorageMouseRecord(mouse, sequence, utc, 1, 1, 0, false, 0, 0, 0);
            }
            await Task.CompletedTask;
        }
    }
}
