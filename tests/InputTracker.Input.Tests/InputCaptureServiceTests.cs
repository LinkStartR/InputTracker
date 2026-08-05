namespace InputTracker.Input.Tests;

public sealed class InputCaptureServiceTests
{
    [Fact]
    public async Task NativeHostRegistersPausesResumesAndStopsWithoutHardwareMock()
    {
        await using var service = new InputCaptureService(new CountingSink());

        await service.StartAsync();
        Assert.Equal(InputCaptureState.Running, service.CurrentStatus.State);
        Assert.True(service.Diagnostics.IngressOpen);
        Assert.True(service.Diagnostics.RawInputRegistered);

        await service.SetPauseReasonAsync(PauseReason.User, true);
        Assert.Equal(InputCaptureState.Paused, service.CurrentStatus.State);
        Assert.False(service.Diagnostics.RawInputRegistered);
        Assert.Equal(1, service.Diagnostics.CompletedPauseBarriers);

        await service.SetPauseReasonAsync(PauseReason.User, false);
        Assert.Equal(InputCaptureState.Running, service.CurrentStatus.State);
        Assert.True(service.Diagnostics.RawInputRegistered);

        await service.StopAsync();
        Assert.Equal(InputCaptureState.Stopped, service.CurrentStatus.State);
        Assert.False(service.Diagnostics.IngressOpen);
        Assert.False(service.Diagnostics.RawInputRegistered);
        Assert.Equal(2, service.Diagnostics.SuccessfulRegistrations);
        Assert.Equal(2, service.Diagnostics.SuccessfulUnregistrations);
    }

    [Fact]
    public async Task PauseReasonsStackAndUnlockDoesNotOverrideUserPause()
    {
        var factory = new FakeHostFactory();
        await using var service = new InputCaptureService(new CountingSink(), null, factory);

        await service.StartAsync();
        Assert.Equal(InputCaptureState.Running, service.CurrentStatus.State);
        Assert.True(service.Diagnostics.IngressOpen);
        Assert.True(service.Diagnostics.RawInputRegistered);

        await service.SetPauseReasonAsync(PauseReason.User, true);
        Assert.Equal(InputCaptureState.Paused, service.CurrentStatus.State);
        Assert.Equal(PauseReason.User, service.CurrentStatus.PauseReasons);
        Assert.False(service.Diagnostics.IngressOpen);
        Assert.False(service.Diagnostics.RawInputRegistered);
        Assert.Equal(1, service.Diagnostics.CompletedPauseBarriers);

        await service.SetPauseReasonAsync(PauseReason.SessionLocked, true);
        await service.SetPauseReasonAsync(PauseReason.SessionLocked, false);
        Assert.Equal(InputCaptureState.Paused, service.CurrentStatus.State);
        Assert.Equal(PauseReason.User, service.CurrentStatus.PauseReasons);
        Assert.Equal(1, service.Diagnostics.CompletedPauseBarriers);

        await service.SetPauseReasonAsync(PauseReason.User, false);
        Assert.Equal(InputCaptureState.Running, service.CurrentStatus.State);
        Assert.True(service.Diagnostics.IngressOpen);
        Assert.True(service.Diagnostics.RawInputRegistered);
        Assert.Equal(2, service.Diagnostics.SuccessfulRegistrations);

        await service.StopAsync();
        Assert.Equal(InputCaptureState.Stopped, service.CurrentStatus.State);
        Assert.False(service.Diagnostics.IngressOpen);
        Assert.False(service.Diagnostics.RawInputRegistered);
        Assert.True(factory.Host.Destroyed);
        Assert.True(factory.Host.StopBarrierCompleted);
    }

    [Fact]
    public async Task QueueOverflowClosesIngressAndEntersIntegrityFault()
    {
        var factory = new FakeHostFactory();
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

        Assert.Equal(InputCaptureState.IntegrityFault, service.CurrentStatus.State);
        Assert.False(service.Diagnostics.IngressOpen);
        Assert.Equal(1, service.Diagnostics.Overflow);
        sink.Release.TrySetResult(true);
    }

    [Fact]
    public async Task SinkFailureClosesIngressAndEntersIntegrityFault()
    {
        var factory = new FakeHostFactory();
        await using var service = new InputCaptureService(new ThrowingSink(), null, factory);
        var fault = new TaskCompletionSource<InputCaptureStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.StatusChanged += (_, status) =>
        {
            if (status.State == InputCaptureState.IntegrityFault)
            {
                fault.TrySetResult(status);
            }
        };
        await service.StartAsync();

        Assert.True(factory.Host.Inject(Mouse(1)));
        InputCaptureStatus status = await fault.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(InputCaptureState.IntegrityFault, status.State);
        Assert.Contains("sink failed", status.Error);
        Assert.False(service.Diagnostics.IngressOpen);
        Assert.True(factory.Host.StopAfterFaultCalled);
    }

    private static InputIngressEvent Mouse(long timestamp) =>
        new(IngressEventKind.Mouse, 0, timestamp, DeltaX: 1, DeltaY: -1);

    private sealed class CountingSink : IInputEventSink
    {
        public ValueTask WriteAsync(CapturedInput input, CancellationToken cancellationToken) =>
            ValueTask.CompletedTask;
    }

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
            ValueTask.FromException(new IOException("sink failed"));
    }

    private sealed class FakeHostFactory : IRawInputHostFactory
    {
        public FakeHost Host { get; private set; } = null!;

        public IRawInputHost Create(
            BoundedInputQueue queue,
            InputPipelineCounters counters,
            Action<string> onIntegrityFault,
            Action<bool> onRegistrationChanged)
        {
            Host = new FakeHost(queue, onIntegrityFault, onRegistrationChanged);
            return Host;
        }
    }

    private sealed class FakeHost(
        BoundedInputQueue queue,
        Action<string> onIntegrityFault,
        Action<bool> onRegistrationChanged) : IRawInputHost
    {
        private bool _registered;

        public bool IngressOpen { get; private set; }
        public bool Destroyed { get; private set; }
        public bool StopBarrierCompleted { get; private set; }
        public bool StopAfterFaultCalled { get; private set; }

        public ValueTask StartAsync(CancellationToken cancellationToken)
        {
            Register();
            IngressOpen = true;
            return ValueTask.CompletedTask;
        }

        public void CloseIngress() => IngressOpen = false;

        public async ValueTask PauseAsync(PauseReason reasons, CancellationToken cancellationToken)
        {
            Unregister();
            await EnqueueBoundaryAsync(AggregationBoundaryKind.Paused, reasons, cancellationToken);
        }

        public ValueTask ResumeAsync(CancellationToken cancellationToken)
        {
            Register();
            IngressOpen = true;
            return ValueTask.CompletedTask;
        }

        public async ValueTask PrepareStopAsync(bool enqueueBarrier, CancellationToken cancellationToken)
        {
            Unregister();
            if (enqueueBarrier)
            {
                await EnqueueBoundaryAsync(AggregationBoundaryKind.Stopping, PauseReason.None, cancellationToken);
                StopBarrierCompleted = true;
            }
        }

        public ValueTask DestroyAsync(CancellationToken cancellationToken)
        {
            Destroyed = true;
            return ValueTask.CompletedTask;
        }

        public void StopAfterFault()
        {
            StopAfterFaultCalled = true;
            IngressOpen = false;
            Unregister();
        }

        public bool Inject(InputIngressEvent input)
        {
            bool accepted = queue.TryWrite(input);
            if (!accepted)
            {
                IngressOpen = false;
                Unregister();
                onIntegrityFault("test queue overflow");
            }

            return accepted;
        }

        private async ValueTask EnqueueBoundaryAsync(
            AggregationBoundaryKind kind,
            PauseReason reasons,
            CancellationToken cancellationToken)
        {
            var barrier = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!queue.TryWrite(new InputIngressEvent(
                    IngressEventKind.Boundary,
                    0,
                    1,
                    BoundaryKind: kind,
                    PauseReasons: reasons,
                    Barrier: barrier)))
            {
                throw new InvalidOperationException("test boundary overflow");
            }

            await barrier.Task.WaitAsync(cancellationToken);
        }

        private void Register()
        {
            if (_registered)
            {
                return;
            }

            _registered = true;
            onRegistrationChanged(true);
        }

        private void Unregister()
        {
            if (!_registered)
            {
                return;
            }

            _registered = false;
            onRegistrationChanged(false);
        }
    }
}
