using System.Diagnostics;
using InputTracker.Input;
using InputTracker.Storage;

namespace InputTracker.App.Tests;

public sealed class GamepadAutomaticCalibrationPersistenceTests
{
    [Fact]
    public async Task SavedAutomaticMappingSurvivesStorageReopenAndBaselinesNeverCount()
    {
        using var temporary = new TemporaryDirectory();
        var settings = new PortableSettingsStore(temporary.Path);
        settings.GrantCollectionConsent();
        settings.SetGamepadConsent(true);
        StorageLayout layout = StorageLayout.Create(temporary.Path);
        var observation = new StorageDeviceObservation(StorageDeviceKind.Gamepad,
            "HID#automatic-persistence", ["HID_DEVICE"], "test-only-stable-serial", null, false, false, false);
        RuntimeDeviceId runtime = new(76);
        var control = new GamepadControlId(0x09, 1, 0, 0);
        Guid id;
        StorageGamepadHidBinding expected;
        Guid firstSession = Guid.NewGuid();
        using (StorageService firstRun = OpenStorage(layout, firstSession))
        {
            var coordinator = new AppStorageCoordinator(new StorageServiceWriter(firstRun), firstSession, settings);
            coordinator.ObserveGamepadIdentity(runtime, observation);
            id = coordinator.GetGamepadStorageId(runtime)!.Value;
            await Arrive(coordinator, runtime, observation.InterfacePath);
            long start = Stopwatch.GetTimestamp();
            long step = Stopwatch.Frequency / 10;
            await coordinator.WriteAsync(CapturedInput.FromGamepad(new(runtime, control, 0, start, IsBaseline: true)), CancellationToken.None);
            coordinator.BeginGamepadAutomaticCalibration(runtime);
            await coordinator.WriteAsync(CapturedInput.FromGamepad(new(runtime, control, 1, start + step)), CancellationToken.None);
            await coordinator.WriteAsync(CapturedInput.FromGamepad(new(runtime, control, 0, start + 2 * step)), CancellationToken.None);
            coordinator.UpdateGamepadAutomaticCalibration(start + 3 * step);
            expected = coordinator.GamepadCalibrationBindings[StorageGamepadButton.South];
            coordinator.SaveGamepadCalibration(runtime);
            coordinator.SetGamepadEnabled(runtime, true);
        }

        Guid secondSession = Guid.NewGuid();
        using (StorageService secondRun = OpenStorage(layout, secondSession))
        {
            var writer = new RecordingWriter(secondRun);
            var coordinator = new AppStorageCoordinator(writer, secondSession, settings);
            coordinator.InitializeGamepadCapture(true);
            RuntimeDeviceId reconnected = new(77);
            coordinator.ObserveGamepadIdentity(reconnected, observation);
            await Arrive(coordinator, reconnected, observation.InterfacePath);
            Assert.Equal(id, coordinator.GetGamepadStorageId(reconnected));
            StorageGamepadHidBinding restored = Assert.Single(coordinator.GetGamepadConfiguration(reconnected)!.Bindings);
            Assert.Equal(expected.Button, restored.Button);
            Assert.Equal(expected.UsagePage, restored.UsagePage);
            Assert.Equal(expected.Usage, restored.Usage);
            Assert.Equal(expected.LinkCollection, restored.LinkCollection);
            Assert.Equal(expected.ElementIndex, restored.ElementIndex);
            Assert.Equal(expected.ActiveValues, restored.ActiveValues);
            Assert.True(coordinator.IsGamepadEnabled(reconnected));
            Assert.False(coordinator.IsGamepadCalibrating);
            long start = Stopwatch.GetTimestamp();
            await coordinator.WriteAsync(CapturedInput.FromGamepad(new(reconnected, control, 1, start, IsBaseline: true)), CancellationToken.None);
            await coordinator.WriteAsync(CapturedInput.FromGamepad(new(reconnected, control, 1, start + 1)), CancellationToken.None);
            Assert.Empty(writer.Presses);
            await coordinator.WriteAsync(CapturedInput.FromGamepad(new(reconnected, control, 0, start + 2)), CancellationToken.None);
            await coordinator.WriteAsync(CapturedInput.FromGamepad(new(reconnected, control, 1, start + 3)), CancellationToken.None);
            Assert.Equal(StorageGamepadButton.South, Assert.Single(writer.Presses).Button);
        }
    }

    private static async Task Arrive(AppStorageCoordinator coordinator, RuntimeDeviceId runtime, string path) =>
        await coordinator.WriteAsync(CapturedInput.FromDeviceChange(new(DeviceChangeKind.Arrived,
            new(runtime, 1, RawDeviceKind.Gamepad, path, null, DeviceIdentityConfidence.RequiresConfirmation),
            Stopwatch.GetTimestamp())), CancellationToken.None);

    private static StorageService OpenStorage(StorageLayout layout, Guid session)
    {
        StorageService service = StorageService.Open(layout);
        service.Start(TimeCalibrationSegment.Capture(session, SystemStorageClock.Instance));
        return service;
    }

    private sealed class RecordingWriter(StorageService service) : IAppStorageWriter
    {
        private readonly StorageServiceWriter _writer = new(service);
        public List<StorageGamepadPress> Presses { get; } = [];
        public StorageServiceHealth Health => _writer.Health;
        public void WriteKeyboard(StorageKeyboardTransition value) => _writer.WriteKeyboard(value);
        public void WriteMouseButtons(StorageMouseButtonTransition value) => _writer.WriteMouseButtons(value);
        public void Pause(StoragePauseReason reason, long timestamp) => _writer.Pause(reason, timestamp);
        public void Resume(StoragePauseReason reason) => _writer.Resume(reason);
        public void RemoveDevice(Guid deviceId, long timestamp) => _writer.RemoveDevice(deviceId, timestamp);
        public void Stop(long timestamp) => _writer.Stop(timestamp);
        public StorageDeviceResolution ObserveDevice(StorageDeviceObservation value) => _writer.ObserveDevice(value);
        public IReadOnlyList<StorageDeviceInfo> ListDevices() => _writer.ListDevices();
        public StorageDeviceResolution ObserveGamepad(StorageDeviceObservation value) => _writer.ObserveGamepad(value);
        public StorageGamepadConfiguration? GetGamepadConfiguration(Guid deviceId) => _writer.GetGamepadConfiguration(deviceId);
        public StorageGamepadConfiguration SetGamepadConfiguration(Guid deviceId, bool enabled, IReadOnlyList<StorageGamepadHidBinding> bindings) =>
            _writer.SetGamepadConfiguration(deviceId, enabled, bindings);
        public void WriteGamepadPress(StorageGamepadPress value) { Presses.Add(value); _writer.WriteGamepadPress(value); }
    }
}
