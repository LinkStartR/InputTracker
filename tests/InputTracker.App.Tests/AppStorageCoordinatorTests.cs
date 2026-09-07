namespace InputTracker.App.Tests;

using InputTracker.Input;
using InputTracker.Storage;
using System.IO;

public sealed class AppStorageCoordinatorTests
{
    [Fact]
    public void StorageMustBeRunningBeforeInputStarts()
    {
        var store = new FakeWriter(StorageServiceState.Created);
        var coordinator = new AppStorageCoordinator(store, Guid.NewGuid());

        Assert.Throws<InvalidOperationException>(() => coordinator.EnsureReady());
    }

    [Fact]
    public async Task UnknownRuntimeDeviceFailsBeforeStorageMutation()
    {
        var store = new FakeWriter(StorageServiceState.Running);
        var coordinator = new AppStorageCoordinator(store, Guid.NewGuid());

        await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.WriteAsync(
            CapturedInput.FromKeyboard(new KeyboardPhysicalEvent(
                new RuntimeDeviceId(7), 30, PhysicalKeyPrefix.None,
                PhysicalKeyTransition.Down, 10)), CancellationToken.None).AsTask());
        Assert.Equal(0, store.KeyboardWrites);
    }

    [Fact]
    public async Task StorageWriteFailurePropagatesToInputWorker()
    {
        var store = new FakeWriter(StorageServiceState.Running) { WriteError = new IOException("disk fault") };
        var coordinator = new AppStorageCoordinator(store, Guid.NewGuid());
        coordinator.MapRuntimeDevice(new RuntimeDeviceId(9), Guid.NewGuid());

        IOException error = await Assert.ThrowsAsync<IOException>(() => coordinator.WriteAsync(
            CapturedInput.FromMouse(new MouseRawEvent(
                new RuntimeDeviceId(9), 1, -1, MouseMotionKind.Relative, false,
                MouseButtonChanges.LeftDown, 0, 0, 20)), CancellationToken.None).AsTask());
        Assert.Equal("disk fault", error.Message);
    }

    [Fact]
    public async Task PureMovementAndWheelDoNotReachStorage()
    {
        var store = new FakeWriter(StorageServiceState.Running);
        var coordinator = new AppStorageCoordinator(store, Guid.NewGuid());
        coordinator.MapRuntimeDevice(new RuntimeDeviceId(10), Guid.NewGuid());

        await coordinator.WriteAsync(CapturedInput.FromMouse(new MouseRawEvent(
            new RuntimeDeviceId(10), 300, -200, MouseMotionKind.Relative, false,
            MouseButtonChanges.None, 120, -120, 21)), CancellationToken.None);

        Assert.Null(store.Mouse);
    }

    [Fact]
    public async Task KeyboardAndMouseButtonMapOnlyPersistedFields()
    {
        var store = new FakeWriter(StorageServiceState.Running);
        Guid calibration = Guid.NewGuid();
        Guid device = Guid.NewGuid();
        var coordinator = new AppStorageCoordinator(store, calibration);
        coordinator.MapRuntimeDevice(new RuntimeDeviceId(4), device);

        await coordinator.WriteAsync(CapturedInput.FromKeyboard(new KeyboardPhysicalEvent(
            new RuntimeDeviceId(4), 0x1d, PhysicalKeyPrefix.E0,
            PhysicalKeyTransition.Up, 30)), CancellationToken.None);
        await coordinator.WriteAsync(CapturedInput.FromMouse(new MouseRawEvent(
            new RuntimeDeviceId(4), int.MinValue, int.MaxValue, MouseMotionKind.Absolute, true,
            MouseButtonChanges.LeftDown | MouseButtonChanges.X2Up,
            short.MinValue, short.MaxValue, 31)), CancellationToken.None);

        Assert.Equal(new StorageKeyboardTransition(device, 0x1d, true, false, false, 30, calibration), store.Keyboard);
        Assert.Equal(new StorageMouseButtonTransition(
            device, calibration, 31, (ushort)MouseButtonChanges.LeftDown), store.Mouse);
    }

    [Fact]
    public async Task StackedPauseReasonsReachStorageAndResumeInOrder()
    {
        var store = new FakeWriter(StorageServiceState.Running);
        var coordinator = new AppStorageCoordinator(store, Guid.NewGuid());
        var capture = new FakeCaptureControl(coordinator);
        var control = new PersistingCaptureControl(capture, coordinator);

        await control.SetPauseReasonAsync(PauseReason.User, true);
        await control.SetPauseReasonAsync(PauseReason.SessionLocked, true);
        await control.SetPauseReasonAsync(PauseReason.User, false);
        await control.SetPauseReasonAsync(PauseReason.SessionLocked, false);

        Assert.Equal(
            new[] { "pause:User", "pause:SessionLock", "resume:User", "resume:SessionLock" },
            store.Controls);
        Assert.Equal(InputCaptureState.Running, control.CurrentStatus.State);
    }

    [Fact]
    public async Task CaptureResumeFailureRollsStorageBackToPaused()
    {
        var store = new FakeWriter(StorageServiceState.Running);
        var coordinator = new AppStorageCoordinator(store, Guid.NewGuid());
        var capture = new FakeCaptureControl(coordinator) { FailResume = true };
        var control = new PersistingCaptureControl(capture, coordinator);
        await control.SetPauseReasonAsync(PauseReason.User, true);

        await Assert.ThrowsAsync<IOException>(() =>
            control.SetPauseReasonAsync(PauseReason.User, false).AsTask());

        Assert.Equal(new[] { "pause:User", "resume:User", "pause:User" }, store.Controls);
    }

    [Fact]
    public async Task StorageResumeFailureLeavesCapturePaused()
    {
        var store = new FakeWriter(StorageServiceState.Running) { FailResume = true };
        var coordinator = new AppStorageCoordinator(store, Guid.NewGuid());
        var capture = new FakeCaptureControl(coordinator);
        var control = new PersistingCaptureControl(capture, coordinator);
        await control.SetPauseReasonAsync(PauseReason.User, true);

        await Assert.ThrowsAsync<IOException>(() =>
            control.SetPauseReasonAsync(PauseReason.User, false).AsTask());

        Assert.Equal(PauseReason.User, capture.CurrentStatus.PauseReasons);
        Assert.Equal(1, capture.SetCalls);
    }

    [Fact]
    public async Task UnknownRemovalIsNoOpAndKnownRemovalSealsBeforeReuse()
    {
        var store = new FakeWriter(StorageServiceState.Running);
        var coordinator = new AppStorageCoordinator(store, Guid.NewGuid());
        RuntimeDeviceId runtime = new(77);
        Guid first = Guid.NewGuid();
        coordinator.MapRuntimeDevice(runtime, first);

        await coordinator.WriteAsync(CapturedInput.FromBoundary(new AggregationBoundaryEvent(
            AggregationBoundaryKind.DeviceRemoved, runtime, PauseReason.None, 5)), CancellationToken.None);
        await coordinator.WriteAsync(CapturedInput.FromBoundary(new AggregationBoundaryEvent(
            AggregationBoundaryKind.DeviceRemoved, runtime, PauseReason.None, 6)), CancellationToken.None);
        Guid second = Guid.NewGuid();
        coordinator.MapRuntimeDevice(runtime, second);
        await coordinator.WriteAsync(CapturedInput.FromMouse(new MouseRawEvent(
            runtime, 1, 1, MouseMotionKind.Relative, false, MouseButtonChanges.LeftDown, 0, 0, 7)), CancellationToken.None);

        Assert.Equal(new[] { first }, store.RemovedDevices);
        Assert.Equal(second, store.Mouse!.Value.DeviceId);
    }

    [Fact]
    public async Task IndeterminateIdentityIsTransientAndNeverCarriesSerial()
    {
        var store = new FakeWriter(StorageServiceState.Running);
        var coordinator = new AppStorageCoordinator(store, Guid.NewGuid());

        coordinator.ObserveIdentity(
            new RuntimeDeviceId(12),
            RawDeviceKind.Mouse, @"\\?\HID#session", ["HID_DEVICE"], "untrusted-serial",
            null, false, false, false, true);

        Assert.NotNull(store.Observation);
        Assert.Null(store.Observation.SerialNumber);
        Assert.False(store.Observation.IsBluetooth);
        Assert.False(store.Observation.IsRemoteDesktop);
        Assert.False(store.Observation.IsVirtual);
        Assert.Equal(StorageDeviceIdentityConfidence.TransientInterface, store.LastResolution!.Device.IdentityConfidence);
    }

    [Fact]
    public async Task UnattributedKeyboardAndMouseUseSeparateSessionTransientDevices()
    {
        var store = new FakeWriter(StorageServiceState.Running);
        var coordinator = new AppStorageCoordinator(store, Guid.NewGuid());

        await coordinator.WriteAsync(CapturedInput.FromKeyboard(new KeyboardPhysicalEvent(
            RuntimeDeviceId.Unattributed, 30, PhysicalKeyPrefix.None,
            PhysicalKeyTransition.Down, 1)), CancellationToken.None);
        await coordinator.WriteAsync(CapturedInput.FromMouse(new MouseRawEvent(
            RuntimeDeviceId.Unattributed, 1, 2, MouseMotionKind.Relative, false,
            MouseButtonChanges.LeftDown, 0, 0, 2)), CancellationToken.None);

        Assert.Equal(2, store.Observations.Count);
        Assert.Equal(StorageDeviceKind.Keyboard, store.Observations[0].Kind);
        Assert.Equal(StorageDeviceKind.Mouse, store.Observations[1].Kind);
        Assert.NotEqual(store.Keyboard!.Value.DeviceId, store.Mouse!.Value.DeviceId);
        Assert.All(store.Observations, observation =>
        {
            Assert.Null(observation.SerialNumber);
            Assert.Null(observation.ContainerId);
            Assert.False(observation.IsBluetooth || observation.IsRemoteDesktop || observation.IsVirtual);
        });
    }

    private sealed class FakeWriter(StorageServiceState state) : IAppStorageWriter
    {
        public StorageServiceHealth Health { get; } = new(
            state, state == StorageServiceState.Running,
            new StorageDurableWatermark(0, 0, StorageDurabilityScope.SQLiteCommitted), null);
        public int KeyboardWrites { get; private set; }
        public StorageKeyboardTransition? Keyboard { get; private set; }
        public StorageMouseButtonTransition? Mouse { get; private set; }
        public Exception? WriteError { get; init; }
        public List<string> Controls { get; } = [];
        public StorageDeviceObservation? Observation { get; private set; }
        public StorageDeviceResolution? LastResolution { get; private set; }
        public List<StorageDeviceObservation> Observations { get; } = [];
        public List<Guid> RemovedDevices { get; } = [];
        public bool FailResume { get; init; }
        public void WriteKeyboard(StorageKeyboardTransition value) { Throw(); KeyboardWrites++; Keyboard = value; }
        public void WriteMouseButtons(StorageMouseButtonTransition value) { Throw(); Mouse = value; }
        public void Pause(StoragePauseReason reason, long timestamp) { Throw(); Controls.Add($"pause:{reason}"); }
        public void Resume(StoragePauseReason reason) { Throw(); if (FailResume) throw new IOException("resume failed"); Controls.Add($"resume:{reason}"); }
        public void RemoveDevice(Guid deviceId, long timestamp) { Throw(); RemovedDevices.Add(deviceId); }
        public void Stop(long timestamp) => Throw();
        public StorageDeviceResolution ObserveDevice(StorageDeviceObservation value)
        {
            Observation = value;
            Observations.Add(value);
            LastResolution = new(new StorageDeviceInfo(Guid.NewGuid(), value.Kind,
                StorageDeviceIdentityConfidence.TransientInterface,
                StorageDeviceIdentityAmbiguity.MissingPermanentIdentifier, null, 0, 0), false);
            return LastResolution;
        }
        public IReadOnlyList<StorageDeviceInfo> ListDevices() => [];
        private void Throw() { if (WriteError is not null) throw WriteError; }
    }

    private sealed class FakeCaptureControl(AppStorageCoordinator coordinator) : IInputCaptureControl
    {
        public bool FailResume { get; init; }
        public int SetCalls { get; private set; }
        public InputCaptureStatus CurrentStatus { get; private set; } =
            new(InputCaptureState.Running, PauseReason.None, null);
        public DiagnosticsSnapshot Diagnostics => new(
            0, 0, 0, 0, 0, 0, 0, 1, CurrentStatus.State, CurrentStatus.PauseReasons,
            CurrentStatus.State == InputCaptureState.Running,
            CurrentStatus.State == InputCaptureState.Running, 0, 0, 0, null);
        public event EventHandler<InputCaptureStatus>? StatusChanged;

        public ValueTask SetPauseReasonAsync(PauseReason reason, bool active, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetCalls++;
            if (!active && FailResume) throw new IOException("capture resume failed");
            PauseReason before = CurrentStatus.PauseReasons;
            PauseReason after = active ? before | reason : before & ~reason;
            if (before == PauseReason.None && after != PauseReason.None)
            {
                coordinator.WriteAsync(CapturedInput.FromBoundary(new AggregationBoundaryEvent(
                    AggregationBoundaryKind.Paused, RuntimeDeviceId.Unattributed, after, 10)), cancellationToken)
                    .GetAwaiter().GetResult();
            }

            CurrentStatus = new InputCaptureStatus(
                after == PauseReason.None ? InputCaptureState.Running : InputCaptureState.Paused,
                after, null);
            StatusChanged?.Invoke(this, CurrentStatus);
            return ValueTask.CompletedTask;
        }
    }
}
