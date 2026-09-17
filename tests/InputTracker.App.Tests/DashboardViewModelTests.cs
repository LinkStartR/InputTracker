namespace InputTracker.App.Tests;

using InputTracker.Storage;

public sealed class DashboardViewModelTests
{
    [Fact]
    public async Task RefreshUsesSelectedPeriodAndDevicesWithoutReadingTrajectory()
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
        Assert.Equal(7, model.TotalPressCount);
        Assert.Equal(2, model.Heatmap.Count);
        Assert.Equal(new MouseButtonCounts(2, 1, 0, 1, 0), model.MouseButtons);
        Assert.Equal((StoragePeriodKind.Week, mouse), source.MouseButtonsRequest);
        Assert.Equal(
            "左键 2 次 · 右键 1 次 · 中键 0 次 · 侧键一 1 次 · 侧键二 0 次",
            MainWindow.FormatMouseSummary(model.MouseButtons));
    }

    [Fact]
    public async Task LargeButtonCountsAreQueriedOffTheCallingThread()
    {
        Guid mouse = Guid.NewGuid();
        int callerThread = 0;
        var source = new LargeMouseDataSource(mouse);
        var model = new DashboardViewModel(source)
        {
            SelectedMouseDeviceId = mouse,
            TimeZoneId = "UTC"
        };

        await RunOnDedicatedThreadAsync(async () =>
        {
            callerThread = Environment.CurrentManagedThreadId;
            Assert.False(Thread.CurrentThread.IsThreadPoolThread);
            await model.RefreshAsync();
        });

        Assert.Equal(1_000_000, model.MouseButtons.Left);
        Assert.DoesNotContain(callerThread, source.QueryThreads);
        Assert.True(source.AllQueryThreadsWereThreadPoolThreads);
    }

    private static Task RunOnDedicatedThreadAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action().GetAwaiter().GetResult();
                completion.SetResult();
            }
            catch (Exception error)
            {
                completion.SetException(error);
            }
        })
        {
            IsBackground = true,
            Name = "DashboardViewModelTests dedicated caller"
        };
        thread.Start();
        return completion.Task;
    }

    [Fact]
    public async Task RepeatedRefreshUsesOnlyAggregateQueries()
    {
        Guid keyboard = Guid.NewGuid();
        Guid mouse = Guid.NewGuid();
        var source = new FakeDashboardDataSource(keyboard, mouse);
        var model = new DashboardViewModel(source)
        {
            SelectedMouseDeviceId = mouse,
            TimeZoneId = "UTC"
        };

        await model.RefreshAsync();
        await model.RefreshAsync();

        Assert.Equal(2, source.MouseButtonQueryCount);
        Assert.DoesNotContain(
            typeof(IDashboardDataSource).GetMethods(),
            method => method.Name.Contains("StreamMouse", StringComparison.Ordinal));
    }

    [Fact]
    public void MouseSummaryContainsOnlyButtonCounts()
    {
        string summary = MainWindow.FormatMouseSummary(new MouseButtonCounts(1, 2, 3, 4, 5));

        Assert.Equal("左键 1 次 · 右键 2 次 · 中键 3 次 · 侧键一 4 次 · 侧键二 5 次", summary);
        Assert.DoesNotContain("移动", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ReconnectableDeviceLabelDoesNotClaimPermanentUniqueness()
    {
        var device = new StorageDeviceInfo(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            StorageDeviceKind.Mouse,
            StorageDeviceIdentityConfidence.ReconnectableHardware,
            StorageDeviceIdentityAmbiguity.MissingPermanentIdentifier,
            "Desk mouse",
            1,
            2);

        string label = MainWindow.FormatDeviceDisplayName(device);

        Assert.Equal("Desk mouse · 已记住，同型号设备可能混淆", label);
    }

    [Fact]
    public async Task RenameDeviceUpdatesDisplayWithoutChangingDeviceId()
    {
        Guid keyboard = Guid.NewGuid();
        var source = new FakeDashboardDataSource(keyboard, Guid.NewGuid());
        var model = new DashboardViewModel(source);
        await model.RefreshAsync();

        StorageDeviceInfo renamed = await model.SetDeviceAliasAsync(keyboard, "工作键盘");

        Assert.Equal((keyboard, "工作键盘"), source.AliasRequest);
        Assert.Equal(keyboard, renamed.DeviceId);
        Assert.Equal("工作键盘", renamed.Alias);
        Assert.Equal("工作键盘", Assert.Single(model.Devices, value => value.DeviceId == keyboard).Alias);
    }

    private sealed class FakeDashboardDataSource(Guid keyboard, Guid mouse) : IDashboardDataSource
    {
        public (StoragePeriodKind, Guid?) KeyboardRequest { get; private set; }
        public (StoragePeriodKind, Guid?) MouseButtonsRequest { get; private set; }
        public int MouseButtonQueryCount { get; private set; }
        public (Guid DeviceId, string? Alias)? AliasRequest { get; private set; }

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

        public Task<StorageMouseButtonPeriodResult> QueryMouseButtonsAsync(
            StoragePeriodKind kind, DateOnly date, string zone, Guid? deviceId, CancellationToken cancellationToken)
        {
            MouseButtonsRequest = (kind, deviceId);
            MouseButtonQueryCount++;
            return Task.FromResult(new StorageMouseButtonPeriodResult(
                StoragePeriodResolver.Resolve(kind, date, zone),
                [
                    new(mouse, StorageMouseButton.Left, 2),
                    new(mouse, StorageMouseButton.Right, 1),
                    new(mouse, StorageMouseButton.X1, 1)
                ],
                [zone]));
        }

        public Task<StorageDeviceInfo> SetDeviceAliasAsync(
            Guid deviceId, string? alias, CancellationToken cancellationToken)
        {
            AliasRequest = (deviceId, alias);
            StorageDeviceKind kind = deviceId == keyboard ? StorageDeviceKind.Keyboard : StorageDeviceKind.Mouse;
            return Task.FromResult(new StorageDeviceInfo(
                deviceId, kind, StorageDeviceIdentityConfidence.ReconnectableHardware,
                StorageDeviceIdentityAmbiguity.MissingPermanentIdentifier,
                alias, 0, 0));
        }

    }

    private sealed class LargeMouseDataSource(Guid mouse) : IDashboardDataSource
    {
        public HashSet<int> QueryThreads { get; } = [];
        public bool AllQueryThreadsWereThreadPoolThreads { get; private set; } = true;
        public Task<IReadOnlyList<StorageDeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<StorageDeviceInfo>>([]);
        public Task<StorageKeyboardPeriodResult> QueryKeyboardAsync(
            StoragePeriodKind kind, DateOnly date, string zone, Guid? deviceId, CancellationToken cancellationToken)
        {
            RecordThread();
            return Task.FromResult(new StorageKeyboardPeriodResult(
                StoragePeriodResolver.Resolve(kind, date, zone), [], []));
        }
        public Task<StorageMouseButtonPeriodResult> QueryMouseButtonsAsync(
            StoragePeriodKind kind, DateOnly date, string zone, Guid? deviceId, CancellationToken cancellationToken)
        {
            RecordThread();
            return Task.FromResult(new StorageMouseButtonPeriodResult(
                StoragePeriodResolver.Resolve(kind, date, zone),
                [new(mouse, StorageMouseButton.Left, 1_000_000)], []));
        }
        public Task<StorageDeviceInfo> SetDeviceAliasAsync(
            Guid deviceId, string? alias, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private void RecordThread()
        {
            QueryThreads.Add(Environment.CurrentManagedThreadId);
            AllQueryThreadsWereThreadPoolThreads &= Thread.CurrentThread.IsThreadPoolThread;
        }
    }
}
