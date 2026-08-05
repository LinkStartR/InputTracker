namespace InputTracker.AcceptanceTests.InputCore;

public sealed class IntegrityFaultAcceptanceTests
{
    [Fact]
    public async Task OverflowClosesIngressUnregistersAndPublishesIntegrityFault()
    {
        var factory = new AcceptanceFakeHostFactory();
        var sink = new BlockingSink();
        await using var service = new InputCaptureService(
            sink,
            new InputCaptureOptions { QueueCapacity = 1 },
            factory);
        await service.StartAsync();

        Assert.True(factory.Host.Inject(Mouse(1)));
        await sink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(factory.Host.Inject(Mouse(2)));
        Assert.False(factory.Host.Inject(Mouse(3)));
        DiagnosticsSnapshot diagnostics = service.Diagnostics;

        Assert.Equal(InputCaptureState.IntegrityFault, service.CurrentStatus.State);
        Assert.False(diagnostics.IngressOpen);
        Assert.False(diagnostics.RawInputRegistered);
        Assert.Equal(1, diagnostics.Overflow);
        Assert.True(diagnostics.SuccessfulUnregistrations >= 1);
        InputCoreAcceptanceEvidence.Write("overflow", new
        {
            state = service.CurrentStatus.State.ToString(),
            overflow = diagnostics.Overflow,
            ingressOpen = diagnostics.IngressOpen,
            rawInputRegistered = diagnostics.RawInputRegistered,
            unregistrations = diagnostics.SuccessfulUnregistrations
        });
        sink.Release.TrySetResult(true);
    }

    [Fact]
    public async Task SinkFailureClosesIngressUnregistersAndPublishesIntegrityFault()
    {
        var factory = new AcceptanceFakeHostFactory();
        await using var service = new InputCaptureService(new ThrowingSink(), null, factory);
        var fault = new TaskCompletionSource<DiagnosticsSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.StatusChanged += (_, status) =>
        {
            if (status.State == InputCaptureState.IntegrityFault)
            {
                fault.TrySetResult(service.Diagnostics);
            }
        };
        await service.StartAsync();

        Assert.True(factory.Host.Inject(Mouse(1)));
        DiagnosticsSnapshot diagnostics = await fault.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(InputCaptureState.IntegrityFault, service.CurrentStatus.State);
        Assert.False(diagnostics.IngressOpen);
        Assert.False(diagnostics.RawInputRegistered);
        Assert.True(diagnostics.SuccessfulUnregistrations >= 1);
        Assert.True(factory.Host.StopAfterFaultCalled);
        InputCoreAcceptanceEvidence.Write("sink-failure", new
        {
            state = service.CurrentStatus.State.ToString(),
            ingressOpen = diagnostics.IngressOpen,
            rawInputRegistered = diagnostics.RawInputRegistered,
            unregistrations = diagnostics.SuccessfulUnregistrations,
            stopAfterFault = factory.Host.StopAfterFaultCalled
        });
    }

    private static InputIngressEvent Mouse(long timestamp) =>
        new(IngressEventKind.Mouse, 0, timestamp, DeltaX: 1, DeltaY: -1);

    private sealed class BlockingSink : IInputEventSink
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask WriteAsync(CapturedInput input, CancellationToken cancellationToken)
        {
            Entered.TrySetResult(true);
            await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private sealed class ThrowingSink : IInputEventSink
    {
        public ValueTask WriteAsync(CapturedInput input, CancellationToken cancellationToken) =>
            ValueTask.FromException(new IOException("acceptance sink failed"));
    }
}
