using InputTracker.Storage;

namespace InputTracker.App.Tests;

public sealed class GamepadRecallPersistenceTests
{
    [Fact]
    public void RestartRestoresIdentityBindingsBrandAndKeepsDisabledCaptureOff()
    {
        using var temporary = new TemporaryDirectory();
        var settings = new PortableSettingsStore(temporary.Path);
        settings.GrantCollectionConsent();
        settings.SetGamepadConsent(true);
        StorageLayout layout = StorageLayout.Create(temporary.Path);
        StorageDeviceObservation observation = new(
            StorageDeviceKind.Gamepad,
            "HID#recall-test-interface",
            ["HID\\VID_1234&PID_5678"],
            "recall-test-serial",
            null,
            IsBluetooth: false,
            IsRemoteDesktop: false,
            IsVirtual: false);
        StorageGamepadHidBinding binding = new(
            StorageGamepadButton.South, 0x09, 1, 0, 0, [1]);
        Guid deviceId;

        using (StorageService firstRun = OpenStorage(layout))
        {
            deviceId = firstRun.ObserveDevice(observation).Device.DeviceId;
            firstRun.SetGamepadConfiguration(deviceId, StorageGamepadLayout.Xbox, enabled: false, [binding]);
            settings.SetGamepadConfiguration(enabled: false, deviceId);
            settings.SetGamepadBrand(deviceId, GamepadBrand.ThirdParty);
        }

        using (StorageService secondRun = OpenStorage(layout))
        {
            StorageDeviceResolution reconnected = secondRun.ObserveDevice(observation);
            StorageGamepadConfiguration? restored = secondRun.GetGamepadConfiguration(deviceId);
            PortableSettings loaded = settings.Load();

            Assert.True(reconnected.ReusedExisting);
            Assert.Equal(deviceId, reconnected.Device.DeviceId);
            AssertBinding(binding, Assert.Single(restored!.Bindings));
            Assert.Equal(GamepadBrand.ThirdParty, settings.GetGamepadBrand(deviceId));
            Assert.True(GamepadStartupPolicy.ShouldAutoDetect(
                loaded, secondRun.ListDevices(), secondRun.GetGamepadConfiguration));
            Assert.False(GamepadStartupPolicy.ShouldAutoStart(
                loaded, secondRun.ListDevices(), secondRun.GetGamepadConfiguration));

            secondRun.SetGamepadConfiguration(deviceId, StorageGamepadLayout.Xbox, enabled: true, restored.Bindings);
            settings.SetGamepadConfiguration(enabled: true, deviceId);
            Assert.True(GamepadStartupPolicy.ShouldAutoStart(
                settings.Load(), secondRun.ListDevices(), secondRun.GetGamepadConfiguration));

            secondRun.SetGamepadConfiguration(deviceId, StorageGamepadLayout.Xbox, enabled: false, restored.Bindings);
            settings.SetGamepadConfiguration(enabled: false);
        }

        using (StorageService thirdRun = OpenStorage(layout))
        {
            StorageDeviceResolution reconnected = thirdRun.ObserveDevice(observation);
            StorageGamepadConfiguration? restored = thirdRun.GetGamepadConfiguration(deviceId);

            Assert.Equal(deviceId, reconnected.Device.DeviceId);
            Assert.False(restored!.Enabled);
            AssertBinding(binding, Assert.Single(restored.Bindings));
            Assert.Equal(deviceId, settings.Load().GamepadDeviceId);
            Assert.Equal(GamepadBrand.ThirdParty, settings.GetGamepadBrand(deviceId));
            Assert.True(GamepadStartupPolicy.ShouldAutoDetect(
                settings.Load(), thirdRun.ListDevices(), thirdRun.GetGamepadConfiguration));
            Assert.False(GamepadStartupPolicy.ShouldAutoStart(
                settings.Load(), thirdRun.ListDevices(), thirdRun.GetGamepadConfiguration));
        }

        string settingsJson = File.ReadAllText(settings.SettingsPath);
        Assert.DoesNotContain(observation.InterfacePath, settingsJson, StringComparison.Ordinal);
        Assert.DoesNotContain(observation.HardwareIds[0], settingsJson, StringComparison.Ordinal);
        Assert.DoesNotContain(observation.SerialNumber!, settingsJson, StringComparison.Ordinal);
    }

    private static StorageService OpenStorage(StorageLayout layout)
    {
        StorageService service = StorageService.Open(layout);
        service.Start(TimeCalibrationSegment.Capture(Guid.NewGuid(), SystemStorageClock.Instance));
        return service;
    }

    private static void AssertBinding(StorageGamepadHidBinding expected, StorageGamepadHidBinding actual)
    {
        Assert.Equal(expected.Button, actual.Button);
        Assert.Equal(expected.UsagePage, actual.UsagePage);
        Assert.Equal(expected.Usage, actual.Usage);
        Assert.Equal(expected.LinkCollection, actual.LinkCollection);
        Assert.Equal(expected.ElementIndex, actual.ElementIndex);
        Assert.Equal(expected.ActiveValues, actual.ActiveValues);
        Assert.Equal(expected.AxisDirection, actual.AxisDirection);
        Assert.Equal(expected.ActivationThreshold, actual.ActivationThreshold);
        Assert.Equal(expected.ReleaseThreshold, actual.ReleaseThreshold);
    }
}
