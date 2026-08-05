using System.Reflection;

namespace InputTracker.AcceptanceTests.InputCore;

public sealed class ProductionPipelineAcceptanceTests
{
    [Fact]
    public async Task OneMillionMixedEventsPreserveExactProductionCounters()
    {
        const int total = 1_000_000;
        var counters = new InputPipelineCounters();
        var queue = new BoundedInputQueue(total, counters);
        var sink = new CountingHashSink();
        Exception? fault = null;
        var worker = new InputWorker(
            queue,
            counters,
            sink,
            new AcceptanceDeviceInfoProvider(),
            error =>
            {
                fault = error;
                return ValueTask.CompletedTask;
            });
        Task workerTask = worker.Start();
        nint[] handles = [new(11), new(12), new(21), new(22)];
        int[] keyboardTransitions = [0, 0];

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
            int device = index & 3;
            InputIngressEvent input = device switch
            {
                0 or 1 => new InputIngressEvent(
                    IngressEventKind.Keyboard,
                    handles[device],
                    index + 1L,
                    MakeCode: (ushort)(30 + device),
                    KeyPrefix: device == 0 ? PhysicalKeyPrefix.None : PhysicalKeyPrefix.E0,
                    KeyTransition: (keyboardTransitions[device]++ & 1) == 0
                        ? PhysicalKeyTransition.Down
                        : PhysicalKeyTransition.Up),
                2 => new InputIngressEvent(
                    IngressEventKind.Mouse,
                    handles[device],
                    index + 1L,
                    DeltaX: 1,
                    DeltaY: -1,
                    ButtonChanges: MouseButtonChanges.LeftDown,
                    VerticalWheelDelta: 120),
                _ => new InputIngressEvent(
                    IngressEventKind.Mouse,
                    handles[device],
                    index + 1L,
                    DeltaX: -2,
                    DeltaY: 3,
                    MotionKind: MouseMotionKind.Absolute,
                    UsesVirtualDesktop: true,
                    ButtonChanges: MouseButtonChanges.X2Up,
                    HorizontalWheelDelta: -120)
            };
            Assert.True(queue.TryWrite(input));
        }

        queue.Complete();
        await workerTask.WaitAsync(TimeSpan.FromSeconds(45));
        InputPipelineDiagnostics diagnostics = counters.Snapshot(queue.Capacity);

        Assert.Null(fault);
        Assert.Equal(total, diagnostics.Produced);
        Assert.Equal(total, diagnostics.Accepted);
        Assert.Equal(total, diagnostics.Dequeued);
        Assert.Equal(total, diagnostics.SinkAcknowledged);
        Assert.Equal(total, sink.Count);
        Assert.Equal(0, diagnostics.Overflow);
        Assert.Equal(0, diagnostics.ParseErrors);
        Assert.Equal(0, diagnostics.OutOfOrder);
        InputCoreAcceptanceEvidence.Write("pipeline", new
        {
            produced = diagnostics.Produced,
            accepted = diagnostics.Accepted,
            dequeued = diagnostics.Dequeued,
            sinkAcknowledged = diagnostics.SinkAcknowledged,
            overflow = diagnostics.Overflow,
            parseErrors = diagnostics.ParseErrors,
            outOfOrder = diagnostics.OutOfOrder,
            capacity = diagnostics.QueueCapacity,
            rollingHash = sink.Hash.ToString("X16")
        });
    }

    [Fact]
    public async Task OutOfOrderInjectionIsObservedWithoutChangingProductionCode()
    {
        bool injected = InputCoreAcceptanceEvidence.IsOutOfOrderInjectionEnabled;
        var counters = new InputPipelineCounters();
        var queue = new BoundedInputQueue(8, counters);
        var sink = new FirstWriteBlockingSink();
        var worker = new InputWorker(
            queue,
            counters,
            sink,
            new AcceptanceDeviceInfoProvider(),
            error => ValueTask.FromException(error));
        Task workerTask = worker.Start();

        Assert.True(queue.TryWrite(new InputIngressEvent(
            IngressEventKind.Mouse,
            0,
            1,
            DeltaX: 1)));
        await sink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (injected)
        {
            FieldInfo sequence = typeof(BoundedInputQueue).GetField(
                "_nextSequence",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(typeof(BoundedInputQueue).FullName, "_nextSequence");
            sequence.SetValue(queue, 0L);
        }

        Assert.True(queue.TryWrite(new InputIngressEvent(
            IngressEventKind.Mouse,
            0,
            2,
            DeltaX: 2)));
        sink.Release.TrySetResult(true);
        queue.Complete();
        await workerTask.WaitAsync(TimeSpan.FromSeconds(5));
        InputPipelineDiagnostics diagnostics = counters.Snapshot(queue.Capacity);

        Assert.Equal(injected ? 1 : 0, diagnostics.OutOfOrder);
        InputCoreAcceptanceEvidence.Write("out-of-order", new
        {
            injection = injected ? "out-of-order" : "none",
            outOfOrder = diagnostics.OutOfOrder,
            produced = diagnostics.Produced,
            accepted = diagnostics.Accepted,
            dequeued = diagnostics.Dequeued,
            sinkAcknowledged = diagnostics.SinkAcknowledged
        });
    }

    private sealed class FirstWriteBlockingSink : IInputEventSink
    {
        private int _writes;

        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask WriteAsync(CapturedInput input, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _writes) == 1)
            {
                Entered.TrySetResult(true);
                await Release.Task.WaitAsync(cancellationToken);
            }
        }
    }
}
