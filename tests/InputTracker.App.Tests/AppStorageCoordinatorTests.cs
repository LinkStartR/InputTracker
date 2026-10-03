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
        Assert.True(store.Observation.IsIndeterminate);
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

    [Fact]
    public async Task GamepadCaptureRequiresSeparateConsentAndDoesNotPersistIdentityBeforeIt()
    {
        using var temporary = new TemporaryDirectory();
        var settings = new PortableSettingsStore(temporary.Path);
        settings.GrantCollectionConsent();
        var writer = new FakeWriter(StorageServiceState.Running);
        var coordinator = new AppStorageCoordinator(writer, Guid.NewGuid(), settings);

        coordinator.ObserveGamepadIdentity(new RuntimeDeviceId(51), new StorageDeviceObservation(
            StorageDeviceKind.Gamepad, @"\\?\HID#private", ["HID_DEVICE"], null, null,
            false, false, false));

        Assert.Empty(writer.Observations);
        Assert.False(settings.Load().GamepadConsentGranted);
    }

    [Fact]
    public async Task QueuedGamepadInputBeforeConfigurationCutoffCannotUseNewRevision()
    {
        using var temporary = new TemporaryDirectory();
        var settings = new PortableSettingsStore(temporary.Path);
        settings.GrantCollectionConsent();
        settings.SetGamepadConsent(true);
        var writer = new FakeWriter(StorageServiceState.Running);
        Guid calibration = Guid.NewGuid();
        var coordinator = new AppStorageCoordinator(writer, calibration, settings);
        RuntimeDeviceId runtime = new(52);
        const string path = @"\\?\HID#pad";
        coordinator.ObserveGamepadIdentity(runtime, new StorageDeviceObservation(
            StorageDeviceKind.Gamepad, path, ["HID_DEVICE"], null, null, false, false, false));
        Guid deviceId = writer.LastResolution!.Device.DeviceId;
        await coordinator.WriteAsync(CapturedInput.FromDeviceChange(new DeviceChangeEvent(
            DeviceChangeKind.Arrived,
            new InputDeviceDescriptor(runtime, 1, RawDeviceKind.Gamepad, path, null, DeviceIdentityConfidence.RequiresConfirmation),
            1)), CancellationToken.None);
        var binding = new StorageGamepadHidBinding(StorageGamepadButton.South, 0x09, 1, 0, 0, [1]);
        writer.SetGamepadConfiguration(deviceId, false, [binding]);

        coordinator.SetGamepadEnabled(runtime, enabled: true);
        coordinator.SetGamepadCaptureEnabled(true);
        long baseline = System.Diagnostics.Stopwatch.GetTimestamp() + 1;
        await coordinator.WriteAsync(CapturedInput.FromGamepad(new GamepadControlEvent(
            runtime, new GamepadControlId(0x09, 1, 0, 0), 0, baseline, IsBaseline: true)), CancellationToken.None);
        await coordinator.WriteAsync(CapturedInput.FromGamepad(new GamepadControlEvent(
            runtime, new GamepadControlId(0x09, 1, 0, 0), 1, 1)), CancellationToken.None);
        await coordinator.WriteAsync(CapturedInput.FromGamepad(new GamepadControlEvent(
            runtime, new GamepadControlId(0x09, 1, 0, 0), 1, baseline + 1)), CancellationToken.None);

        Assert.Single(writer.GamepadPresses);
        Assert.Equal(baseline + 1, writer.GamepadPresses[0].MonotonicTimestamp);
        Assert.Equal(deviceId, writer.GamepadPresses[0].DeviceId);
    }

    [Fact]
    public async Task HatSwitchUpValueZeroCanBeConfirmedDuringCalibration()
    {
        using var temporary = new TemporaryDirectory();
        var settings = new PortableSettingsStore(temporary.Path);
        settings.GrantCollectionConsent();
        settings.SetGamepadConsent(true);
        var writer = new FakeWriter(StorageServiceState.Running);
        var coordinator = new AppStorageCoordinator(writer, Guid.NewGuid(), settings);
        RuntimeDeviceId runtime = new(53);
        const string path = @"\\?\HID#hat";
        coordinator.ObserveGamepadIdentity(runtime, new StorageDeviceObservation(
            StorageDeviceKind.Gamepad, path, ["HID_DEVICE"], null, null, false, false, false));
        await coordinator.WriteAsync(CapturedInput.FromDeviceChange(new DeviceChangeEvent(
            DeviceChangeKind.Arrived,
            new InputDeviceDescriptor(runtime, 1, RawDeviceKind.Gamepad, path, null, DeviceIdentityConfidence.RequiresConfirmation),
            1)), CancellationToken.None);
        var hat = new GamepadControlId(0x01, 0x39, 0, 0);
        await coordinator.WriteAsync(CapturedInput.FromGamepad(new GamepadControlEvent(
            runtime, hat, -1, 2, IsBaseline: true, LogicalMinimum: 0, LogicalMaximum: 7)), CancellationToken.None);
        coordinator.BeginGamepadCalibration(runtime, StorageGamepadButton.DpadUp);

        await coordinator.WriteAsync(CapturedInput.FromGamepad(new GamepadControlEvent(
            runtime, hat, 0, 3, LogicalMinimum: 0, LogicalMaximum: 7)), CancellationToken.None);

        Assert.True(coordinator.HasGamepadCalibrationCandidate);
        coordinator.AcceptGamepadCalibrationCandidate();
        Assert.Equal(new[] { 0, 1, 7 }, coordinator.GamepadCalibrationBindings[StorageGamepadButton.DpadUp].ActiveValues);
    }

    [Fact]
    public async Task TriggerCalibrationRequiresExplicitRestingPointAfterRelease()
    {
        using var temporary = new TemporaryDirectory();
        var settings = new PortableSettingsStore(temporary.Path);
        settings.GrantCollectionConsent();
        settings.SetGamepadConsent(true);
        var writer = new FakeWriter(StorageServiceState.Running);
        var coordinator = new AppStorageCoordinator(writer, Guid.NewGuid(), settings);
        RuntimeDeviceId runtime = new(54);
        const string path = @"\\?\HID#trigger";
        coordinator.ObserveGamepadIdentity(runtime, new StorageDeviceObservation(
            StorageDeviceKind.Gamepad, path, ["HID_DEVICE"], null, null, false, false, false));
        await ArriveGamepad(coordinator, runtime, path);
        var trigger = new GamepadControlId(0x01, 0x32, 0, 0);

        await coordinator.WriteAsync(CapturedInput.FromGamepad(new GamepadControlEvent(
            runtime, trigger, 255, 1, IsBaseline: true, LogicalMinimum: 0, LogicalMaximum: 255)), CancellationToken.None);
        coordinator.BeginGamepadCalibration(runtime, StorageGamepadButton.LeftTrigger);
        await coordinator.WriteAsync(CapturedInput.FromGamepad(new GamepadControlEvent(
            runtime, trigger, 0, 2, LogicalMinimum: 0, LogicalMaximum: 255)), CancellationToken.None);

        Assert.False(coordinator.HasGamepadCalibrationCandidate);
        Assert.True(coordinator.IsGamepadTriggerAwaitingRestingConfirmation);
        Assert.True(coordinator.ConfirmGamepadTriggerRestingPoint());
        await coordinator.WriteAsync(CapturedInput.FromGamepad(new GamepadControlEvent(
            runtime, trigger, 255, 3, LogicalMinimum: 0, LogicalMaximum: 255)), CancellationToken.None);

        Assert.True(coordinator.HasGamepadCalibrationCandidate);
        StorageGamepadHidBinding binding = Assert.IsType<StorageGamepadHidBinding>(coordinator.CurrentGamepadCalibrationCandidate);
        Assert.Equal(StorageGamepadAxisDirection.Increasing, binding.AxisDirection);
    }

    [Fact]
    public async Task OnlyOneGamepadCanBeEnabledAndDisablingStaleDeviceKeepsCurrentDeviceEnabled()
    {
        using var temporary = new TemporaryDirectory();
        var settings = new PortableSettingsStore(temporary.Path);
        settings.GrantCollectionConsent();
        settings.SetGamepadConsent(true);
        var writer = new FakeWriter(StorageServiceState.Running);
        var coordinator = new AppStorageCoordinator(writer, Guid.NewGuid(), settings);
        RuntimeDeviceId a = new(55);
        RuntimeDeviceId b = new(56);
        coordinator.ObserveGamepadIdentity(a, GamepadObservation(@"\\?\HID#A"));
        Guid idA = writer.LastResolution!.Device.DeviceId;
        coordinator.ObserveGamepadIdentity(b, GamepadObservation(@"\\?\HID#B"));
        Guid idB = writer.LastResolution!.Device.DeviceId;
        var bindingA = new StorageGamepadHidBinding(StorageGamepadButton.South, 0x09, 1, 0, 0, [1]);
        var bindingB = new StorageGamepadHidBinding(StorageGamepadButton.East, 0x09, 2, 0, 0, [1]);
        writer.SetGamepadConfiguration(idA, false, [bindingA]);
        writer.SetGamepadConfiguration(idB, false, [bindingB]);

        coordinator.SetGamepadEnabled(a, true);
        InvalidOperationException blocked = Assert.Throws<InvalidOperationException>(() => coordinator.SetGamepadEnabled(b, true));
        Assert.Contains("请先停用当前手柄", blocked.Message);
        Assert.True(writer.GetGamepadConfiguration(idA)!.Enabled);
        Assert.False(writer.GetGamepadConfiguration(idB)!.Enabled);
        Assert.True(coordinator.IsGamepadEnabled(a));
        Assert.False(coordinator.IsGamepadEnabled(b));

        coordinator.SetGamepadEnabled(a, false);
        coordinator.SetGamepadEnabled(b, true);
        coordinator.SetGamepadEnabled(a, false);
        Assert.False(writer.GetGamepadConfiguration(idA)!.Enabled);
        Assert.True(writer.GetGamepadConfiguration(idB)!.Enabled);
        Assert.True(coordinator.IsGamepadEnabled(b));
    }

    [Fact]
    public async Task RemovingCalibratingGamepadCancelsSessionAndRequiresRestart()
    {
        using var temporary = new TemporaryDirectory();
        var settings = new PortableSettingsStore(temporary.Path);
        settings.GrantCollectionConsent();
        settings.SetGamepadConsent(true);
        var coordinator = new AppStorageCoordinator(new FakeWriter(StorageServiceState.Running), Guid.NewGuid(), settings);
        RuntimeDeviceId runtime = new(57);
        const string path = @"\\?\HID#remove";
        coordinator.ObserveGamepadIdentity(runtime, GamepadObservation(path));
        await ArriveGamepad(coordinator, runtime, path);
        coordinator.BeginGamepadCalibration(runtime, StorageGamepadButton.South);

        await coordinator.WriteAsync(CapturedInput.FromDeviceChange(new DeviceChangeEvent(
            DeviceChangeKind.Removed,
            new InputDeviceDescriptor(runtime, 1, RawDeviceKind.Gamepad, path, null, DeviceIdentityConfidence.RequiresConfirmation),
            10)), CancellationToken.None);

        Assert.False(coordinator.IsGamepadCalibrating);
        Assert.True(coordinator.GamepadCalibrationInterrupted);
    }

    [Fact]
    public async Task PausingCalibratingGamepadCancelsSessionAndRequiresRestart()
    {
        using var temporary = new TemporaryDirectory();
        var settings = new PortableSettingsStore(temporary.Path);
        settings.GrantCollectionConsent();
        settings.SetGamepadConsent(true);
        var coordinator = new AppStorageCoordinator(new FakeWriter(StorageServiceState.Running), Guid.NewGuid(), settings);
        RuntimeDeviceId runtime = new(58);
        const string path = @"\\?\HID#pause";
        coordinator.ObserveGamepadIdentity(runtime, GamepadObservation(path));
        await ArriveGamepad(coordinator, runtime, path);
        coordinator.BeginGamepadCalibration(runtime, StorageGamepadButton.South);

        await coordinator.WriteAsync(CapturedInput.FromBoundary(new AggregationBoundaryEvent(
            AggregationBoundaryKind.Paused, runtime, PauseReason.User, 10)), CancellationToken.None);

        Assert.False(coordinator.IsGamepadCalibrating);
        Assert.True(coordinator.GamepadCalibrationInterrupted);
    }

    [Fact]
    public async Task CalibrationCannotBeSavedForDifferentRuntimeDevice()
    {
        using var temporary = new TemporaryDirectory();
        var settings = new PortableSettingsStore(temporary.Path);
        settings.GrantCollectionConsent();
        settings.SetGamepadConsent(true);
        var coordinator = new AppStorageCoordinator(new FakeWriter(StorageServiceState.Running), Guid.NewGuid(), settings);
        RuntimeDeviceId runtimeA = new(59);
        const string path = @"\\?\HID#calibration";
        coordinator.ObserveGamepadIdentity(runtimeA, GamepadObservation(path));
        await ArriveGamepad(coordinator, runtimeA, path);
        coordinator.BeginGamepadCalibration(runtimeA, StorageGamepadButton.South);

        Assert.Throws<InvalidOperationException>(() => coordinator.SaveGamepadCalibration(new RuntimeDeviceId(60)));
    }

    [Fact]
    public void PreviouslyDisabledGamepadDoesNotRemainActiveAcrossRestart()
    {
        using var temporary = new TemporaryDirectory();
        var settings = new PortableSettingsStore(temporary.Path);
        settings.GrantCollectionConsent();
        settings.SetGamepadConsent(true);
        Guid oldDeviceId = Guid.NewGuid();
        settings.SetGamepadConfiguration(true, oldDeviceId);
        settings.SetGamepadConfiguration(false);
        Assert.Equal(oldDeviceId, settings.Load().GamepadDeviceId);

        var writer = new FakeWriter(StorageServiceState.Running);
        var coordinator = new AppStorageCoordinator(writer, Guid.NewGuid(), settings);
        RuntimeDeviceId newRuntime = new(61);
        const string path = @"\\?\HID#new-controller";
        coordinator.ObserveGamepadIdentity(newRuntime, GamepadObservation(path));
        Guid newDeviceId = writer.LastResolution!.Device.DeviceId;
        writer.SetGamepadConfiguration(newDeviceId, false,
            [new StorageGamepadHidBinding(StorageGamepadButton.South, 0x09, 1, 0, 0, [1])]);

        coordinator.SetGamepadEnabled(newRuntime, true);

        Assert.True(writer.GetGamepadConfiguration(newDeviceId)!.Enabled);
        Assert.Equal(newDeviceId, settings.Load().GamepadDeviceId);
    }

    [Fact]
    public async Task AutomaticCalibrationAdvancesOnReleaseAndNeverCountsUnsavedInput()
    {
        using var temporary = new TemporaryDirectory();
        var settings = new PortableSettingsStore(temporary.Path);
        settings.GrantCollectionConsent();
        settings.SetGamepadConsent(true);
        var writer = new FakeWriter(StorageServiceState.Running);
        var coordinator = new AppStorageCoordinator(writer, Guid.NewGuid(), settings);
        RuntimeDeviceId runtime = new(72);
        const string path = @"\\?\HID#auto";
        coordinator.ObserveGamepadIdentity(runtime, GamepadObservation(path));
        await ArriveGamepad(coordinator, runtime, path);
        var control = new GamepadControlId(0x09, 1, 0, 0);
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        long step = System.Diagnostics.Stopwatch.Frequency / 10;
        await coordinator.WriteAsync(CapturedInput.FromGamepad(new(runtime, control, 0, start, IsBaseline: true)), CancellationToken.None);
        coordinator.BeginGamepadAutomaticCalibration(runtime);
        await coordinator.WriteAsync(CapturedInput.FromGamepad(new(runtime, control, 1, start + step)), CancellationToken.None);
        coordinator.UpdateGamepadAutomaticCalibration(start + 2 * step);
        Assert.Equal(StorageGamepadButton.South, coordinator.CurrentCalibrationTarget);
        await coordinator.WriteAsync(CapturedInput.FromGamepad(new(runtime, control, 0, start + 3 * step)), CancellationToken.None);
        coordinator.UpdateGamepadAutomaticCalibration(start + 4 * step);
        Assert.Equal(StorageGamepadButton.East, coordinator.CurrentCalibrationTarget);
        Assert.Single(coordinator.GamepadCalibrationBindings);
        Assert.Empty(writer.GamepadPresses);
        Assert.Null(writer.GamepadConfiguration);

        // Reusing the same physical button cannot silently assign it to East as well.
        await coordinator.WriteAsync(CapturedInput.FromGamepad(new(runtime, control, 1, start + 5 * step)), CancellationToken.None);
        await coordinator.WriteAsync(CapturedInput.FromGamepad(new(runtime, control, 0, start + 6 * step)), CancellationToken.None);
        coordinator.UpdateGamepadAutomaticCalibration(start + 7 * step);
        Assert.Equal(StorageGamepadButton.East, coordinator.CurrentCalibrationTarget);
        Assert.Single(coordinator.GamepadCalibrationBindings);
        Assert.Contains("已经用于", coordinator.GamepadAutomaticCalibrationFeedback);

        coordinator.SaveGamepadCalibration(runtime);
        Assert.False(coordinator.IsGamepadAutomaticCalibration);
        Assert.Single(writer.GamepadConfiguration!.Bindings);
        Assert.False(writer.GamepadConfiguration.Enabled);
    }

    [Fact]
    public async Task AutomaticAdvanceUsesCompletionTimeToExcludeLateReportsFromNextTarget()
    {
        using var temporary = new TemporaryDirectory();
        var settings = new PortableSettingsStore(temporary.Path);
        settings.GrantCollectionConsent();
        settings.SetGamepadConsent(true);
        var writer = new FakeWriter(StorageServiceState.Running);
        var coordinator = new AppStorageCoordinator(writer, Guid.NewGuid(), settings);
        RuntimeDeviceId runtime = new(79);
        const string path = @"\\?\HID#lateReports";
        coordinator.ObserveGamepadIdentity(runtime, GamepadObservation(path));
        await ArriveGamepad(coordinator, runtime, path);
        var first = new GamepadControlId(0x09, 1, 0, 0);
        var second = new GamepadControlId(0x09, 2, 0, 0);
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        long millisecond = System.Diagnostics.Stopwatch.Frequency / 1000;
        await coordinator.WriteAsync(CapturedInput.FromGamepad(new(runtime, first, 0, start, IsBaseline: true)), CancellationToken.None);
        await coordinator.WriteAsync(CapturedInput.FromGamepad(new(runtime, second, 0, start, IsBaseline: true)), CancellationToken.None);
        coordinator.BeginGamepadAutomaticCalibration(runtime);
        await coordinator.WriteAsync(CapturedInput.FromGamepad(new(runtime, first, 1, start + 100 * millisecond)), CancellationToken.None);
        await coordinator.WriteAsync(CapturedInput.FromGamepad(new(runtime, first, 0, start + 250 * millisecond)), CancellationToken.None);
        coordinator.UpdateGamepadAutomaticCalibration(start + 400 * millisecond);
        Assert.Equal(StorageGamepadButton.East, coordinator.CurrentCalibrationTarget);
        await coordinator.WriteAsync(CapturedInput.FromGamepad(new(runtime, second, 1, start + 251 * millisecond)), CancellationToken.None);
        await coordinator.WriteAsync(CapturedInput.FromGamepad(new(runtime, second, 0, start + 351 * millisecond)), CancellationToken.None);
        coordinator.UpdateGamepadAutomaticCalibration(start + 500 * millisecond);
        Assert.Equal(StorageGamepadButton.East, coordinator.CurrentCalibrationTarget);
        Assert.Single(coordinator.GamepadCalibrationBindings);

        await coordinator.WriteAsync(CapturedInput.FromGamepad(new(runtime, second, 1, start + 600 * millisecond)), CancellationToken.None);
        await coordinator.WriteAsync(CapturedInput.FromGamepad(new(runtime, second, 0, start + 750 * millisecond)), CancellationToken.None);
        coordinator.UpdateGamepadAutomaticCalibration(start + 800 * millisecond);
        Assert.Equal(StorageGamepadButton.West, coordinator.CurrentCalibrationTarget);
        Assert.Equal(2, coordinator.GamepadCalibrationBindings.Count);
        Assert.Empty(writer.GamepadPresses);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("pause")]
    [InlineData("remove")]
    [InlineData("captureOff")]
    [InlineData("switch")]
    public async Task AutomaticCalibrationCannotAcceptPendingInputAcrossSessionBoundary(string boundary)
    {
        using var temporary = new TemporaryDirectory();
        var settings = new PortableSettingsStore(temporary.Path);
        settings.GrantCollectionConsent();
        settings.SetGamepadConsent(true);
        var writer = new FakeWriter(StorageServiceState.Running);
        var coordinator = new AppStorageCoordinator(writer, Guid.NewGuid(), settings);
        RuntimeDeviceId runtime = new(73);
        const string path = @"\\?\HID#autoBoundary";
        coordinator.ObserveGamepadIdentity(runtime, GamepadObservation(path));
        await ArriveGamepad(coordinator, runtime, path);
        var control = new GamepadControlId(0x09, 1, 0, 0);
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        long step = System.Diagnostics.Stopwatch.Frequency / 10;
        await coordinator.WriteAsync(CapturedInput.FromGamepad(new(runtime, control, 0, start, IsBaseline: true)), CancellationToken.None);
        coordinator.BeginGamepadAutomaticCalibration(runtime);
        await coordinator.WriteAsync(CapturedInput.FromGamepad(new(runtime, control, 1, start + step)), CancellationToken.None);
        await coordinator.WriteAsync(CapturedInput.FromGamepad(new(runtime, control, 0, start + 2 * step)), CancellationToken.None);
        switch (boundary)
        {
            case "cancel": coordinator.CancelGamepadCalibration(); break;
            case "captureOff": coordinator.SetGamepadCaptureEnabled(false); break;
            case "pause":
                await coordinator.WriteAsync(CapturedInput.FromBoundary(new AggregationBoundaryEvent(
                    AggregationBoundaryKind.Paused, RuntimeDeviceId.Unattributed, PauseReason.User,
                    start + 3 * step)), CancellationToken.None);
                break;
            case "remove":
                await coordinator.WriteAsync(CapturedInput.FromDeviceChange(new DeviceChangeEvent(DeviceChangeKind.Removed,
                    new InputDeviceDescriptor(runtime, 1, RawDeviceKind.Gamepad, path, null, DeviceIdentityConfidence.RequiresConfirmation),
                    start + 3 * step)), CancellationToken.None);
                break;
            case "switch":
                RuntimeDeviceId other = new(74);
                const string otherPath = @"\\?\HID#autoOther";
                coordinator.ObserveGamepadIdentity(other, GamepadObservation(otherPath));
                await ArriveGamepad(coordinator, other, otherPath);
                coordinator.BeginGamepadAutomaticCalibration(other);
                break;
        }
        coordinator.UpdateGamepadAutomaticCalibration(start + 4 * step);
        Assert.Empty(coordinator.GamepadCalibrationBindings);
        if (boundary == "switch") Assert.Equal(StorageGamepadButton.South, coordinator.CurrentCalibrationTarget);
        else Assert.False(coordinator.IsGamepadCalibrating);
        Assert.Empty(writer.GamepadPresses);
        Assert.Null(writer.GamepadConfiguration);
        Assert.Throws<InvalidOperationException>(() => coordinator.SaveGamepadCalibration(runtime));
    }

    [Fact]
    public async Task CompletedAutomaticDraftStillBlocksCountingUntilSave()
    {
        using var temporary = new TemporaryDirectory();
        var settings = new PortableSettingsStore(temporary.Path);
        settings.GrantCollectionConsent();
        settings.SetGamepadConsent(true);
        var writer = new FakeWriter(StorageServiceState.Running);
        var coordinator = new AppStorageCoordinator(writer, Guid.NewGuid(), settings);
        RuntimeDeviceId runtime = new(75);
        const string path = @"\\?\HID#autoCompleted";
        coordinator.ObserveGamepadIdentity(runtime, GamepadObservation(path));
        await ArriveGamepad(coordinator, runtime, path);
        var control = new GamepadControlId(0x09, 1, 0, 0);
        Guid id = writer.LastResolution!.Device.DeviceId;
        writer.SetGamepadConfiguration(id, false, [new(StorageGamepadButton.South, 0x09, 1, 0, 0, [1])]);
        coordinator.SetGamepadEnabled(runtime, true);
        long start = System.Diagnostics.Stopwatch.GetTimestamp() + 1;
        long step = System.Diagnostics.Stopwatch.Frequency / 10;
        await coordinator.WriteAsync(CapturedInput.FromGamepad(new(runtime, control, 0, start, IsBaseline: true)), CancellationToken.None);
        coordinator.BeginGamepadAutomaticCalibration(runtime, StorageGamepadButton.RightTrigger);
        await coordinator.WriteAsync(CapturedInput.FromGamepad(new(runtime, control, 1, start + step)), CancellationToken.None);
        await coordinator.WriteAsync(CapturedInput.FromGamepad(new(runtime, control, 0, start + 2 * step)), CancellationToken.None);
        coordinator.UpdateGamepadAutomaticCalibration(start + 3 * step);
        Assert.Null(coordinator.CurrentCalibrationTarget);
        Assert.True(coordinator.IsGamepadCalibrating);
        await coordinator.WriteAsync(CapturedInput.FromGamepad(new(runtime, control, 1, start + 4 * step)), CancellationToken.None);
        Assert.Empty(writer.GamepadPresses);
        Assert.True(writer.GamepadConfiguration!.Enabled);
        coordinator.SaveGamepadCalibration(runtime);
        Assert.False(writer.GamepadConfiguration!.Enabled);
    }

    [Fact]
    public async Task ManualFallbackKeepsAutomaticDraftAndCalibratesCenteredTriggerWithExplicitConfirmation()
    {
        using var temporary = new TemporaryDirectory();
        var settings = new PortableSettingsStore(temporary.Path);
        settings.GrantCollectionConsent();
        settings.SetGamepadConsent(true);
        var writer = new FakeWriter(StorageServiceState.Running);
        var coordinator = new AppStorageCoordinator(writer, Guid.NewGuid(), settings);
        RuntimeDeviceId runtime = new(78);
        const string path = @"\\?\HID#manualFallback";
        coordinator.ObserveGamepadIdentity(runtime, GamepadObservation(path));
        await ArriveGamepad(coordinator, runtime, path);
        var button = new GamepadControlId(0x09, 1, 0, 0);
        var trigger = new GamepadControlId(0x01, 0x32, 0, 0);
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        long step = System.Diagnostics.Stopwatch.Frequency / 10;
        await coordinator.WriteAsync(CapturedInput.FromGamepad(new(runtime, button, 0, start, IsBaseline: true)), CancellationToken.None);
        await coordinator.WriteAsync(CapturedInput.FromGamepad(new(runtime, trigger, 0, start, IsBaseline: true,
            LogicalMinimum: -100, LogicalMaximum: 100)), CancellationToken.None);
        coordinator.BeginGamepadAutomaticCalibration(runtime);
        await coordinator.WriteAsync(CapturedInput.FromGamepad(new(runtime, button, 1, start + step)), CancellationToken.None);
        await coordinator.WriteAsync(CapturedInput.FromGamepad(new(runtime, button, 0, start + 2 * step)), CancellationToken.None);
        coordinator.UpdateGamepadAutomaticCalibration(start + 3 * step);
        while (coordinator.CurrentCalibrationTarget != StorageGamepadButton.LeftTrigger) coordinator.SkipGamepadCalibrationTarget();
        Assert.Single(coordinator.GamepadCalibrationBindings);

        coordinator.SwitchGamepadCalibrationToManual();
        Assert.False(coordinator.IsGamepadAutomaticCalibration);
        Assert.True(coordinator.IsGamepadCalibrating);
        Assert.Equal(StorageGamepadButton.LeftTrigger, coordinator.CurrentCalibrationTarget);
        Assert.Single(coordinator.GamepadCalibrationBindings);
        Assert.True(coordinator.IsGamepadTriggerAwaitingRestingConfirmation);
        Assert.True(coordinator.ConfirmGamepadTriggerRestingPoint());
        await coordinator.WriteAsync(CapturedInput.FromGamepad(new(runtime, trigger, 40, start + 4 * step,
            LogicalMinimum: -100, LogicalMaximum: 100)), CancellationToken.None);
        Assert.False(coordinator.HasGamepadCalibrationCandidate);
        await coordinator.WriteAsync(CapturedInput.FromGamepad(new(runtime, trigger, 100, start + 5 * step,
            LogicalMinimum: -100, LogicalMaximum: 100)), CancellationToken.None);
        Assert.True(coordinator.HasGamepadCalibrationCandidate);
        Assert.Equal(55, coordinator.CurrentGamepadCalibrationCandidate!.ActivationThreshold);
        Assert.Equal(30, coordinator.CurrentGamepadCalibrationCandidate.ReleaseThreshold);
        coordinator.AcceptGamepadCalibrationCandidate();
        Assert.Equal(2, coordinator.GamepadCalibrationBindings.Count);
        Assert.Contains(StorageGamepadButton.South, coordinator.GamepadCalibrationBindings.Keys);
        Assert.Contains(StorageGamepadButton.LeftTrigger, coordinator.GamepadCalibrationBindings.Keys);
        Assert.Empty(writer.GamepadPresses);
        Assert.Null(writer.GamepadConfiguration);
        coordinator.SaveGamepadCalibration(runtime);
        Assert.Equal(2, writer.GamepadConfiguration!.Bindings.Count);
    }

    private static StorageDeviceObservation GamepadObservation(string path) => new(
        StorageDeviceKind.Gamepad, path, ["HID_DEVICE"], null, null, false, false, false);

    private static async Task ArriveGamepad(AppStorageCoordinator coordinator, RuntimeDeviceId runtime, string path) =>
        await coordinator.WriteAsync(CapturedInput.FromDeviceChange(new DeviceChangeEvent(
            DeviceChangeKind.Arrived,
            new InputDeviceDescriptor(runtime, 1, RawDeviceKind.Gamepad, path, null, DeviceIdentityConfidence.RequiresConfirmation),
            1)), CancellationToken.None);

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
        public List<StorageGamepadPress> GamepadPresses { get; } = [];
        public StorageGamepadConfiguration? GamepadConfiguration { get; private set; }
        private readonly Dictionary<Guid, StorageGamepadConfiguration> _gamepadConfigurations = [];
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
        public StorageDeviceResolution ObserveGamepad(StorageDeviceObservation value) => ObserveDevice(value);
        public StorageGamepadConfiguration? GetGamepadConfiguration(Guid deviceId) =>
            _gamepadConfigurations.GetValueOrDefault(deviceId);
        public StorageGamepadConfiguration SetGamepadConfiguration(
            Guid deviceId, bool enabled, IReadOnlyList<StorageGamepadHidBinding> bindings)
        {
            _gamepadConfigurations.TryGetValue(deviceId, out StorageGamepadConfiguration? prior);
            GamepadConfiguration = new StorageGamepadConfiguration(
                deviceId, (prior?.Revision ?? 0) + 1, StorageGamepadLayout.Xbox, enabled, bindings);
            _gamepadConfigurations[deviceId] = GamepadConfiguration;
            return GamepadConfiguration;
        }
        public void WriteGamepadPress(StorageGamepadPress value) { Throw(); GamepadPresses.Add(value); }
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

        public ValueTask SetGamepadCaptureEnabledAsync(bool enabled, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
}
