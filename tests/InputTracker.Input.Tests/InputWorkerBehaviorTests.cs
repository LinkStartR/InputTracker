namespace InputTracker.Input.Tests;

public sealed class InputWorkerBehaviorTests
{
    [Theory]
    [InlineData((int)IngressEventKind.Keyboard, CapturedInputKind.Keyboard)]
    [InlineData((int)IngressEventKind.Mouse, CapturedInputKind.Mouse)]
    public async Task UnknownDeviceArrivalIsAcknowledgedBeforeFirstInput(
        int ingressKindValue,
        CapturedInputKind capturedKind)
    {
        var ingressKind = (IngressEventKind)ingressKindValue;
        var counters = new InputPipelineCounters();
        var queue = new BoundedInputQueue(8, counters);
        var sink = new ArrivalBlockingSink();
        var worker = new InputWorker(
            queue,
            counters,
            sink,
            new FixedProvider(ingressKind == IngressEventKind.Keyboard
                ? RawDeviceKind.Keyboard
                : RawDeviceKind.Mouse),
            error => ValueTask.FromException(error));
        Task run = worker.Start();
        nint handle = new(20);
        const long timestamp = 101;

        Assert.True(queue.TryWrite(ingressKind == IngressEventKind.Keyboard
            ? new InputIngressEvent(
                ingressKind,
                handle,
                timestamp,
                MakeCode: 30,
                KeyTransition: PhysicalKeyTransition.Down)
            : new InputIngressEvent(ingressKind, handle, timestamp, DeltaX: 1, DeltaY: -1)));
        queue.Complete();

        await sink.ArrivalEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        CapturedInput arrival = Assert.Single(sink.Items);
        Assert.Equal(CapturedInputKind.DeviceChange, arrival.Kind);
        Assert.Equal(DeviceChangeKind.Arrived, arrival.DeviceChange.Change);
        Assert.Equal(timestamp, arrival.DeviceChange.Timestamp);
        Assert.Null(arrival.DeviceChange.Device.InterfacePath);
        Assert.Equal("test", arrival.DeviceChange.Device.HardwareSummary);
        Assert.False(run.IsCompleted);

        sink.ReleaseArrival.TrySetResult(true);
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        CapturedInput[] items = sink.Items.ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal(capturedKind, items[1].Kind);
        Assert.Equal(arrival.DeviceChange.Device.RuntimeId, DeviceId(items[1]));
        Assert.Equal(timestamp, Timestamp(items[1]));
        Assert.Equal(2, counters.Snapshot(queue.Capacity).SinkAcknowledged);
    }

    [Fact]
    public async Task ExplicitArrivalIsNotRepeatedBeforeInput()
    {
        var counters = new InputPipelineCounters();
        var queue = new BoundedInputQueue(8, counters);
        var sink = new RecordingSink();
        var worker = CreateWorker(queue, counters, sink, new FixedProvider(RawDeviceKind.Mouse));
        Task run = worker.Start();
        nint handle = new(21);

        Assert.True(queue.TryWrite(new InputIngressEvent(
            IngressEventKind.DeviceArrived,
            handle,
            1,
            DeviceKind: RawDeviceKind.Mouse)));
        Assert.True(queue.TryWrite(new InputIngressEvent(IngressEventKind.Mouse, handle, 2, DeltaX: 4)));
        queue.Complete();
        await run;

        Assert.Single(sink.Items, item =>
            item.Kind == CapturedInputKind.DeviceChange &&
            item.DeviceChange.Change == DeviceChangeKind.Arrived);
        Assert.Equal(2, sink.Items.Count);
    }

    [Fact]
    public async Task ExplicitArrivalAfterImplicitArrivalKeepsOneRuntimeIdentity()
    {
        var counters = new InputPipelineCounters();
        var queue = new BoundedInputQueue(8, counters);
        var sink = new RecordingSink();
        var worker = CreateWorker(queue, counters, sink, new FixedProvider(RawDeviceKind.Mouse));
        Task run = worker.Start();
        nint handle = new(23);

        Assert.True(queue.TryWrite(new InputIngressEvent(IngressEventKind.Mouse, handle, 1, DeltaX: 1)));
        Assert.True(queue.TryWrite(new InputIngressEvent(
            IngressEventKind.DeviceArrived,
            handle,
            2,
            DeviceKind: RawDeviceKind.Mouse)));
        Assert.True(queue.TryWrite(new InputIngressEvent(IngressEventKind.Mouse, handle, 3, DeltaX: 2)));
        queue.Complete();
        await run;

        CapturedInput arrival = Assert.Single(sink.Items, item =>
            item.Kind == CapturedInputKind.DeviceChange &&
            item.DeviceChange.Change == DeviceChangeKind.Arrived);
        CapturedInput[] inputs = sink.Items.Where(item => item.Kind == CapturedInputKind.Mouse).ToArray();
        Assert.Equal(2, inputs.Length);
        Assert.All(inputs, input => Assert.Equal(arrival.DeviceChange.Device.RuntimeId, input.Mouse.DeviceId));
        Assert.Equal(1, arrival.DeviceChange.Device.ArrivalGeneration);
    }

    [Fact]
    public async Task ZeroHandleInputStaysUnattributedWithoutArrival()
    {
        var counters = new InputPipelineCounters();
        var queue = new BoundedInputQueue(8, counters);
        var sink = new RecordingSink();
        var provider = new CountingProvider();
        var worker = CreateWorker(queue, counters, sink, provider);
        Task run = worker.Start();

        Assert.True(queue.TryWrite(new InputIngressEvent(
            IngressEventKind.DeviceArrived,
            0,
            1,
            DeviceKind: RawDeviceKind.Mouse)));
        Assert.True(queue.TryWrite(new InputIngressEvent(IngressEventKind.Mouse, 0, 2, DeltaX: 2)));
        queue.Complete();
        await run;

        CapturedInput input = Assert.Single(sink.Items);
        Assert.Equal(CapturedInputKind.Mouse, input.Kind);
        Assert.Equal(RuntimeDeviceId.Unattributed, input.Mouse.DeviceId);
        Assert.Equal(0, provider.QueryCount);
    }

    [Fact]
    public async Task RemovedHandleImplicitReappearanceGetsNewArrivalGeneration()
    {
        var counters = new InputPipelineCounters();
        var queue = new BoundedInputQueue(16, counters);
        var sink = new RecordingSink();
        var worker = CreateWorker(queue, counters, sink, new FixedProvider(RawDeviceKind.Mouse));
        Task run = worker.Start();
        nint handle = new(22);

        Assert.True(queue.TryWrite(new InputIngressEvent(
            IngressEventKind.DeviceArrived,
            handle,
            1,
            DeviceKind: RawDeviceKind.Mouse)));
        Assert.True(queue.TryWrite(new InputIngressEvent(IngressEventKind.Mouse, handle, 2, DeltaX: 1)));
        Assert.True(queue.TryWrite(new InputIngressEvent(
            IngressEventKind.DeviceRemoved,
            handle,
            3,
            DeviceKind: RawDeviceKind.Mouse)));
        Assert.True(queue.TryWrite(new InputIngressEvent(IngressEventKind.Mouse, handle, 4, DeltaX: 2)));
        queue.Complete();
        await run;

        CapturedInput[] arrivals = sink.Items.Where(item =>
            item.Kind == CapturedInputKind.DeviceChange &&
            item.DeviceChange.Change == DeviceChangeKind.Arrived).ToArray();
        Assert.Equal(2, arrivals.Length);
        Assert.Equal(1, arrivals[0].DeviceChange.Device.ArrivalGeneration);
        Assert.Equal(2, arrivals[1].DeviceChange.Device.ArrivalGeneration);
        Assert.NotEqual(
            arrivals[0].DeviceChange.Device.RuntimeId,
            arrivals[1].DeviceChange.Device.RuntimeId);

        int secondArrivalIndex = sink.Items.IndexOf(arrivals[1]);
        Assert.Equal(CapturedInputKind.Mouse, sink.Items[secondArrivalIndex + 1].Kind);
        Assert.Equal(4, arrivals[1].DeviceChange.Timestamp);
        Assert.Equal(4, sink.Items[secondArrivalIndex + 1].Mouse.Timestamp);
        Assert.Equal(
            arrivals[1].DeviceChange.Device.RuntimeId,
            sink.Items[secondArrivalIndex + 1].Mouse.DeviceId);
    }

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
            error => ValueTask.FromException(error));
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

    private sealed class ArrivalBlockingSink : IInputEventSink
    {
        public System.Collections.Concurrent.ConcurrentQueue<CapturedInput> Items { get; } = new();
        public TaskCompletionSource<bool> ArrivalEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseArrival { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask WriteAsync(CapturedInput input, CancellationToken cancellationToken)
        {
            Items.Enqueue(input);
            if (input.Kind == CapturedInputKind.DeviceChange &&
                input.DeviceChange.Change == DeviceChangeKind.Arrived)
            {
                ArrivalEntered.TrySetResult(true);
                await ReleaseArrival.Task.WaitAsync(cancellationToken);
            }
        }
    }

    private sealed class CountingProvider : IRawDeviceInfoProvider
    {
        public int QueryCount { get; private set; }

        public RawDeviceMetadata Query(nint deviceHandle, RawDeviceKind hintedKind)
        {
            QueryCount++;
            return new RawDeviceMetadata(
                hintedKind,
                $"path-{deviceHandle}",
                "test",
                DeviceIdentityConfidence.SessionOnly);
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

    private static InputWorker CreateWorker(
        BoundedInputQueue queue,
        InputPipelineCounters counters,
        IInputEventSink sink,
        IRawDeviceInfoProvider provider) =>
        new(queue, counters, sink, provider, error => ValueTask.FromException(error));

    private static RuntimeDeviceId DeviceId(CapturedInput input) => input.Kind switch
    {
        CapturedInputKind.Keyboard => input.Keyboard.DeviceId,
        CapturedInputKind.Mouse => input.Mouse.DeviceId,
        _ => throw new InvalidOperationException("Expected an input event.")
    };

    private static long Timestamp(CapturedInput input) => input.Kind switch
    {
        CapturedInputKind.Keyboard => input.Keyboard.Timestamp,
        CapturedInputKind.Mouse => input.Mouse.Timestamp,
        _ => throw new InvalidOperationException("Expected an input event.")
    };
}
