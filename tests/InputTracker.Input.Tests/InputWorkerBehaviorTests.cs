namespace InputTracker.Input.Tests;

public sealed class InputWorkerBehaviorTests
{
    [Fact]
    public async Task KeyboardRepeatIsFilteredAndRemovalClosesPressedState()
    {
        var counters = new InputPipelineCounters();
        var queue = new BoundedInputQueue(32, counters);
        var sink = new RecordingSink();
        var worker = new InputWorker(
            queue,
            counters,
            sink,
            new FixedProvider(RawDeviceKind.Keyboard),
            error => throw error);
        Task run = worker.Start();
        nint handle = new(10);

        Assert.True(queue.TryWrite(new InputIngressEvent(IngressEventKind.DeviceArrived, handle, 1, DeviceKind: RawDeviceKind.Keyboard)));
        Assert.True(queue.TryWrite(new InputIngressEvent(IngressEventKind.Keyboard, handle, 2, MakeCode: 30, KeyTransition: PhysicalKeyTransition.Down)));
        Assert.True(queue.TryWrite(new InputIngressEvent(IngressEventKind.Keyboard, handle, 3, MakeCode: 30, KeyTransition: PhysicalKeyTransition.Down)));
        Assert.True(queue.TryWrite(new InputIngressEvent(IngressEventKind.DeviceRemoved, handle, 4, DeviceKind: RawDeviceKind.Keyboard)));
        Assert.True(queue.TryWrite(new InputIngressEvent(IngressEventKind.DeviceArrived, handle, 5, DeviceKind: RawDeviceKind.Keyboard)));
        Assert.True(queue.TryWrite(new InputIngressEvent(IngressEventKind.Keyboard, handle, 6, MakeCode: 30, KeyTransition: PhysicalKeyTransition.Down)));
        queue.Complete();
        await run;

        CapturedInput[] keyboard = sink.Items.Where(item => item.Kind == CapturedInputKind.Keyboard).ToArray();
        CapturedInput[] arrivals = sink.Items.Where(item =>
            item.Kind == CapturedInputKind.DeviceChange &&
            item.DeviceChange.Change == DeviceChangeKind.Arrived).ToArray();
        Assert.Equal(2, keyboard.Length);
        Assert.Equal(2, arrivals.Length);
        Assert.NotEqual(arrivals[0].DeviceChange.Device.RuntimeId, arrivals[1].DeviceChange.Device.RuntimeId);
        Assert.Contains(sink.Items, item =>
            item.Kind == CapturedInputKind.AggregationBoundary &&
            item.Boundary.Boundary == AggregationBoundaryKind.DeviceRemoved);
    }

    private sealed class RecordingSink : IInputEventSink
    {
        public List<CapturedInput> Items { get; } = [];

        public ValueTask WriteAsync(CapturedInput input, CancellationToken cancellationToken)
        {
            Items.Add(input);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedProvider(RawDeviceKind kind) : IRawDeviceInfoProvider
    {
        public RawDeviceMetadata Query(nint deviceHandle, RawDeviceKind hintedKind) => new(
            kind,
            $"path-{deviceHandle}",
            "test",
            DeviceIdentityConfidence.SessionOnly);
    }
}
