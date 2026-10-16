using InputTracker.Storage;

namespace InputTracker.Tests;

public sealed class GamepadStartupPolicyTests
{
    [Fact]
    public void SavedCalibrationAllowsDetectionButDoesNotStartCounting()
    {
        Guid deviceId = Guid.NewGuid();
        var settings = new PortableSettings(3, true, true, false, null);
        var gamepad = new StorageDeviceInfo(
            deviceId, StorageDeviceKind.Gamepad,
            StorageDeviceIdentityConfidence.ReconnectableHardware,
            StorageDeviceIdentityAmbiguity.MissingPermanentIdentifier,
            null, 0, 0);
        var configuration = new StorageGamepadConfiguration(
            deviceId, 1, StorageGamepadLayout.Xbox, false,
            [new StorageGamepadHidBinding(StorageGamepadButton.South, 0x09, 1, 0, 0, [1])]);

        Assert.True(GamepadStartupPolicy.ShouldAutoDetect(settings, [gamepad], _ => configuration));
        Assert.False(GamepadStartupPolicy.ShouldAutoStart(settings, [gamepad], _ => configuration));
    }

    [Fact]
    public void StaleOrNonGamepadSettingsIdSkipsConfigurationLookup()
    {
        Guid configuredId = Guid.NewGuid();
        var settings = new PortableSettings(2, true, true, true, configuredId);
        var keyboard = new StorageDeviceInfo(
            configuredId, StorageDeviceKind.Keyboard,
            StorageDeviceIdentityConfidence.ReconnectableHardware,
            StorageDeviceIdentityAmbiguity.MissingPermanentIdentifier,
            null, 0, 0);
        bool queried = false;

        bool shouldStart = GamepadStartupPolicy.ShouldAutoStart(
            settings, [keyboard], _ =>
            {
                queried = true;
                return null;
            });

        Assert.False(shouldStart);
        Assert.False(queried);
    }

    [Fact]
    public void EnabledConfiguredGamepadCanAutoStart()
    {
        Guid deviceId = Guid.NewGuid();
        var settings = new PortableSettings(2, true, true, true, deviceId);
        var gamepad = new StorageDeviceInfo(
            deviceId, StorageDeviceKind.Gamepad,
            StorageDeviceIdentityConfidence.ReconnectableHardware,
            StorageDeviceIdentityAmbiguity.MissingPermanentIdentifier,
            null, 0, 0);
        var configuration = new StorageGamepadConfiguration(
            deviceId, 1, StorageGamepadLayout.Xbox, true,
            [new StorageGamepadHidBinding(StorageGamepadButton.South, 0x09, 1, 0, 0, [1])]);

        bool shouldStart = GamepadStartupPolicy.ShouldAutoStart(settings, [gamepad], id => configuration);

        Assert.True(shouldStart);
    }
}
