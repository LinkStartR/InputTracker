namespace InputTracker.Input.Tests;

public sealed class InputPipelineTests
{
    [Fact]
    public async Task OneMillionMixedEventsPreserveProductionQueueOrderWithoutLoss()
    {
        const int total = 1_000_000;
        var counters = new InputPipelineCounters();
        var queue = new BoundedInputQueue(total, counters);
        var sink = new RollingHashSink();
        var provider = new FakeDeviceInfoProvider();
        Exception? fault = null;
        var worker = new InputWorker(queue, counters, sink, provider, error => fault = error);
        Task workerTask = worker.Start();

        nint[] handles = [new(11), new(12), new(21), new(22)];
        for (int index = 0; index < handles.Length; index++)
        {
            Assert.True(queue.TryWrite(new InputIngressEvent(
                IngressEventKind.DeviceArrived,
                handles[index],
                index + 1,
                DeviceKind: index < 2 ? RawDeviceKind.Keyboard : RawDeviceKind.Mouse)));
        }

        for (int index = handles.Length; index < total; index++)
        {
            int deviceIndex = index & 3;
            InputIngressEvent input = deviceIndex < 2
                ? new InputIngressEvent(
                    IngressEventKind.Keyboard,
                    handles[deviceIndex],
                    index + 1L,
                    MakeCode: (ushort)(30 + deviceIndex),
                    KeyPrefix: deviceIndex == 0 ? PhysicalKeyPrefix.None : PhysicalKeyPrefix.E0,
                    KeyTransition: ((index >> 2) & 1) == 1
                        ? PhysicalKeyTransition.Down
                        : PhysicalKeyTransition.Up)
                : new InputIngressEvent(
                    IngressEventKind.Mouse,
                    handles[deviceIndex],
                    index + 1L,
                    DeltaX: deviceIndex == 2 ? 1 : -1,
                    DeltaY: deviceIndex == 2 ? 2 : -2,
                    ButtonChanges: deviceIndex == 2
                        ? MouseButtonChanges.LeftDown
                        : MouseButtonChanges.RightUp,
                    VerticalWheelDelta: deviceIndex == 2 ? (short)120 : (short)0,
                    HorizontalWheelDelta: deviceIndex == 3 ? (short)-120 : (short)0);

            Assert.True(queue.TryWrite(input));
            if ((index & 1023) == 0)
            {
                await Task.Yield();
            }
        }

        queue.Complete();
        await workerTask.WaitAsync(TimeSpan.FromSeconds(60));

        InputPipelineDiagnostics diagnostics = counters.Snapshot(queue.Capacity);
        Assert.Null(fault);
        Assert.Equal(total, diagnostics.Produced);
        Assert.Equal(total, diagnostics.Accepted);
        Assert.Equal(total, diagnostics.Dequeued);
        Assert.Equal(total, diagnostics.SinkAcknowledged);
        Assert.Equal(0, diagnostics.Overflow);
        Assert.Equal(0, diagnostics.ParseErrors);
        Assert.Equal(0, diagnostics.OutOfOrder);
        Assert.Equal(total, sink.Count);
        Assert.NotEqual(0UL, sink.Hash);
    }

    [Fact]
    public void FullQueueNeverDropsSilently()
    {
        var counters = new InputPipelineCounters();
        var queue = new BoundedInputQueue(1, counters);
        var input = new InputIngressEvent(IngressEventKind.Mouse, new nint(1), 1);

        Assert.True(queue.TryWrite(input));
        Assert.False(queue.TryWrite(input with { Timestamp = 2 }));

        InputPipelineDiagnostics diagnostics = counters.Snapshot(queue.Capacity);
        Assert.Equal(2, diagnostics.Produced);
        Assert.Equal(1, diagnostics.Accepted);
        Assert.Equal(1, diagnostics.Overflow);
    }

    [Fact]
    public async Task SinkFailureIsObservableAndStopsWorker()
    {
        var counters = new InputPipelineCounters();
        var queue = new BoundedInputQueue(8, counters);
        var failure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new InputWorker(
            queue,
            counters,
            new ThrowingSink(),
            new FakeDeviceInfoProvider(),
            error => failure.TrySetResult(error));

        Task workerTask = worker.Start();
        Assert.True(queue.TryWrite(new InputIngressEvent(IngressEventKind.Mouse, new nint(21), 1)));

        Exception error = await failure.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await workerTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("sink failed", error.Message);
        Assert.Equal(0, counters.Snapshot(queue.Capacity).SinkAcknowledged);
    }

    private sealed class RollingHashSink : IInputEventSink
    {
        private ulong _hash = 14695981039346656037UL;
        private long _count;

        public long Count => Volatile.Read(ref _count);
        public ulong Hash => _hash;

        public ValueTask WriteAsync(CapturedInput input, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _hash ^= (ulong)input.Kind + 1;
            _hash *= 1099511628211UL;
            _hash ^= input.Kind switch
            {
                CapturedInputKind.Keyboard => (ulong)input.Keyboard.Timestamp,
                CapturedInputKind.Mouse => (ulong)input.Mouse.Timestamp,
                CapturedInputKind.DeviceChange => (ulong)input.DeviceChange.Timestamp,
                _ => (ulong)input.Boundary.Timestamp
            };
            _hash *= 1099511628211UL;
            Interlocked.Increment(ref _count);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingSink : IInputEventSink
    {
        public ValueTask WriteAsync(CapturedInput input, CancellationToken cancellationToken) =>
            ValueTask.FromException(new IOException("sink failed"));
    }

    private sealed class FakeDeviceInfoProvider : IRawDeviceInfoProvider
    {
        public RawDeviceMetadata Query(nint deviceHandle, RawDeviceKind hintedKind) => new(
            hintedKind,
            $"device-{deviceHandle}",
            "test device",
            DeviceIdentityConfidence.SessionOnly);
    }
}
