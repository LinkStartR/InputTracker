using InputTracker.Storage;

namespace InputTracker;

internal static class GamepadStartupPolicy
{
    internal static bool ShouldAutoDetect(
        PortableSettings settings,
        IReadOnlyList<StorageDeviceInfo> devices,
        Func<Guid, StorageGamepadConfiguration?> getConfiguration)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(getConfiguration);

        return settings.GamepadConsentGranted && devices.Any(device =>
            device.Kind == StorageDeviceKind.Gamepad
            && getConfiguration(device.DeviceId) is { Bindings.Count: > 0 });
    }

    internal static bool ShouldAutoStart(
        PortableSettings settings,
        IReadOnlyList<StorageDeviceInfo> devices,
        Func<Guid, StorageGamepadConfiguration?> getConfiguration)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(getConfiguration);

        if (!settings.GamepadConsentGranted || !settings.GamepadCaptureEnabled
            || settings.GamepadDeviceId is not Guid deviceId
            || !devices.Any(device => device.DeviceId == deviceId && device.Kind == StorageDeviceKind.Gamepad))
            return false;

        return getConfiguration(deviceId) is { Enabled: true, Bindings.Count: > 0 };
    }
}
