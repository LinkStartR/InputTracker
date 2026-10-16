using InputTracker.Input.Win32;

namespace InputTracker.Input.Tests;

public sealed class InputCaptureServiceTests
{
    [Fact]
    public async Task PrivateIdentityObserverFlowsThroughServiceOncePerArrivalGeneration()
    {
        var factory = new FakeHostFactory();
        var observer = new RecordingIdentityObserver();
        var provider = new IdentityProvider();
        await using var service = new InputCaptureService(
            new CountingSink(),
            options: null,
            factory,
            provider,
            observer);
        await service.StartAsync();
        nint handle = new(71);

        Assert.True(factory.Host.Inject(Mouse(handle, 1)));
        Assert.True(factory.Host.Inject(Mouse(handle, 2)));
        Assert.True(factory.Host.Inject(new InputIngressEvent(
            IngressEventKind.DeviceRemoved,
            handle,
            3,
            DeviceKind: RawDeviceKind.Mouse)));
        Assert.True(factory.Host.Inject(Mouse(handle, 4)));

        await service.StopAsync();

        Assert.Equal(2, provider.QueryCount);
        Assert.Equal(2, observer.Items.Count);
        Assert.NotEqual(observer.Items[0].RuntimeId, observer.Items[1].RuntimeId);
        Assert.All(observer.Items, item => Assert.Equal("PRIVATE-SERIAL", item.Identity.SerialNumber));
        Assert.False(typeof(IRawDeviceIdentityObserver).IsPublic);
        Assert.False(typeof(RawDeviceIdentityObservation).IsPublic);
        Assert.DoesNotContain(
            typeof(InputCaptureService).GetConstructors(),
            constructor => constructor.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(IRawDeviceIdentityObserver)));
    }

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
    public async Task GamepadCaptureIsOptInAndRemainsDisabledAcrossPauseUntilEnabled()
    {
        var factory = new FakeHostFactory();
        await using var service = new InputCaptureService(new CountingSink(), null, factory);

        await service.StartAsync();
        Assert.False(factory.Host.GamepadCaptureEnabled);
        Assert.False(factory.Host.GamepadRegistered);

        await service.SetGamepadCaptureEnabledAsync(true);
        Assert.True(factory.Host.GamepadCaptureEnabled);
        Assert.True(factory.Host.GamepadRegistered);

        await service.SetPauseReasonAsync(PauseReason.User, true);
        Assert.False(factory.Host.GamepadRegistered);
        Assert.True(factory.Host.GamepadCaptureEnabled);

        await service.SetPauseReasonAsync(PauseReason.User, false);
        Assert.True(factory.Host.GamepadRegistered);
        await service.SetGamepadCaptureEnabledAsync(false);
        Assert.False(factory.Host.GamepadCaptureEnabled);
        Assert.False(factory.Host.GamepadRegistered);
        await service.StopAsync();
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
        var fault = new TaskCompletionSource<(InputCaptureStatus Status, DiagnosticsSnapshot Diagnostics)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.StatusChanged += (_, status) =>
        {
            if (status.State == InputCaptureState.IntegrityFault)
            {
                fault.TrySetResult((status, service.Diagnostics));
            }
        };
        await service.StartAsync();

        Assert.True(factory.Host.Inject(Mouse(1)));
        (InputCaptureStatus status, DiagnosticsSnapshot diagnostics) =
            await fault.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(InputCaptureState.IntegrityFault, status.State);
        Assert.Contains("sink failed", status.Error);
        Assert.False(diagnostics.IngressOpen);
        Assert.False(diagnostics.RawInputRegistered);
        Assert.Equal(1, diagnostics.SuccessfulUnregistrations);
        Assert.True(factory.Host.StopAfterFaultCalled);
    }

    [Fact]
    public async Task NativeSinkFailureIsPublishedOnlyAfterUnregistrationCompletes()
    {
        await using var service = new InputCaptureService(new BoundaryThrowingSink());
        var fault = new TaskCompletionSource<(InputCaptureStatus Status, DiagnosticsSnapshot Diagnostics)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        service.StatusChanged += (_, status) =>
        {
            if (status.State == InputCaptureState.IntegrityFault)
            {
                fault.TrySetResult((status, service.Diagnostics));
            }
        };
        await service.StartAsync();

        await Assert.ThrowsAsync<IOException>(async () =>
            await service.SetPauseReasonAsync(PauseReason.User, true));
        (InputCaptureStatus status, DiagnosticsSnapshot diagnostics) =
            await fault.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(InputCaptureState.IntegrityFault, status.State);
        Assert.Contains("sink failed", status.Error);
        Assert.False(diagnostics.IngressOpen);
        Assert.False(diagnostics.RawInputRegistered);
        Assert.Equal(1, diagnostics.SuccessfulUnregistrations);
    }

    [Fact]
    public async Task StopAttemptsWorkerCompletionAndDestroyWhenUnregistrationFails()
    {
        var factory = new FakeHostFactory();
        await using var service = new InputCaptureService(new CountingSink(), null, factory);
        await service.StartAsync();
        factory.Host.PrepareStopError = new IOException("unregister failed");

        IOException error = await Assert.ThrowsAsync<IOException>(async () => await service.StopAsync());

        Assert.Equal("unregister failed", error.Message);
        Assert.False(service.Diagnostics.IngressOpen);
        Assert.True(factory.Host.PrepareStopCalled);
        Assert.True(factory.Host.Destroyed);
        Assert.Equal(InputCaptureState.IntegrityFault, service.CurrentStatus.State);
        Assert.Contains("unregister failed", service.CurrentStatus.Error);
        factory.Host.PrepareStopError = null;
    }

    [Fact]
    public async Task StopDuringWorkerFaultDoesNotEnqueueAnUnreadBarrier()
    {
        var factory = new FakeHostFactory();
        await using var service = new InputCaptureService(new ThrowingSink(), null, factory);
        factory.Host.DelayStopAfterFault = true;
        await service.StartAsync();

        Assert.True(factory.Host.Inject(Mouse(1)));
        await factory.Host.StopAfterFaultEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task stop = service.StopAsync().AsTask();
        await factory.Host.PrepareStopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(factory.Host.LastPrepareStopRequestedBarrier);

        factory.Host.ReleaseStopAfterFault.TrySetResult(true);
        await stop.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(factory.Host.Destroyed);
        Assert.False(service.Diagnostics.IngressOpen);
        Assert.False(service.Diagnostics.RawInputRegistered);
    }

    private static InputIngressEvent Mouse(long timestamp) => Mouse(0, timestamp);

    private static InputIngressEvent Mouse(nint handle, long timestamp) =>
        new(IngressEventKind.Mouse, handle, timestamp, DeltaX: 1, DeltaY: -1);

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

    private sealed class BoundaryThrowingSink : IInputEventSink
    {
        public ValueTask WriteAsync(CapturedInput input, CancellationToken cancellationToken) =>
            input.Kind == CapturedInputKind.AggregationBoundary
                ? ValueTask.FromException(new IOException("sink failed"))
                : ValueTask.CompletedTask;
    }

    private sealed class RecordingIdentityObserver : IRawDeviceIdentityObserver
    {
        public List<(RuntimeDeviceId RuntimeId, RawDeviceIdentityObservation Identity)> Items { get; } = [];

        public ValueTask ObserveAsync(
            RuntimeDeviceId runtimeId,
            RawDeviceIdentityObservation identity,
            CancellationToken cancellationToken)
        {
            Items.Add((runtimeId, identity));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class IdentityProvider : IRawDeviceInfoProvider
    {
        public int QueryCount { get; private set; }

        public RawDeviceMetadata Query(nint deviceHandle, RawDeviceKind hintedKind)
        {
            QueryCount++;
            return new RawDeviceMetadata(
                hintedKind,
                InterfacePath: null,
                HardwareSummary: "mouse",
                DeviceIdentityConfidence.RequiresConfirmation,
                new RawDeviceIdentityObservation(
                    hintedKind,
                    "PRIVATE-INTERFACE",
                    ["HID\\PRIVATE-HARDWARE-ID"],
                    "PRIVATE-SERIAL",
                    Guid.NewGuid(),
                    DeviceTransportAssessment.Physical,
                    DeviceIdentityEvidence.RawInputInterfacePath |
                    DeviceIdentityEvidence.DevNodeHardwareIds |
                    DeviceIdentityEvidence.ContainerId));
        }
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
        public bool GamepadCaptureEnabled { get; private set; }
        public bool GamepadRegistered { get; private set; }
        public bool Destroyed { get; private set; }
        public bool StopBarrierCompleted { get; private set; }
        public bool StopAfterFaultCalled { get; private set; }
        public bool PrepareStopCalled { get; private set; }
        public Exception? PrepareStopError { get; set; }
        public bool DelayStopAfterFault { get; set; }
        public bool LastPrepareStopRequestedBarrier { get; private set; }
        public TaskCompletionSource<bool> StopAfterFaultEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseStopAfterFault { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> PrepareStopEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

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
            GamepadRegistered = GamepadCaptureEnabled;
            IngressOpen = true;
            return ValueTask.CompletedTask;
        }

        public async ValueTask SetGamepadCaptureEnabledAsync(bool enabled, CancellationToken cancellationToken)
        {
            GamepadCaptureEnabled = enabled;
            GamepadRegistered = _registered && enabled;
            var barrier = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!queue.TryWrite(new InputIngressEvent(
                    IngressEventKind.GamepadBarrier,
                    0,
                    1,
                    GamepadCaptureEnabled: enabled,
                    Barrier: barrier)))
            {
                throw new InvalidOperationException("test gamepad barrier overflow");
            }

            await barrier.Task.WaitAsync(cancellationToken);
        }

        public async ValueTask PrepareStopAsync(bool enqueueBarrier, CancellationToken cancellationToken)
        {
            PrepareStopCalled = true;
            LastPrepareStopRequestedBarrier = enqueueBarrier;
            PrepareStopEntered.TrySetResult(true);
            if (PrepareStopError is not null)
            {
                throw PrepareStopError;
            }

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

        public async ValueTask StopAfterFaultAsync(CancellationToken cancellationToken)
        {
            StopAfterFaultCalled = true;
            IngressOpen = false;
            Unregister();
            StopAfterFaultEntered.TrySetResult(true);
            if (DelayStopAfterFault)
            {
                await ReleaseStopAfterFault.Task.WaitAsync(cancellationToken);
            }
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
            GamepadRegistered = GamepadCaptureEnabled;
            onRegistrationChanged(true);
        }

        private void Unregister()
        {
            if (!_registered)
            {
                return;
            }

            _registered = false;
            GamepadRegistered = false;
            onRegistrationChanged(false);
        }
    }
}
