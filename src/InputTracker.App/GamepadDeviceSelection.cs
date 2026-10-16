using InputTracker.Input;
using InputTracker.Storage;

namespace InputTracker;

internal static class GamepadDeviceSelection
{
    internal static bool ShouldClearUnrememberedLayout(
        Guid? previousDeviceId,
        Guid? selectedDeviceId,
        StorageGamepadLayout? savedLayout) =>
        savedLayout != StorageGamepadLayout.Xbox
        && previousDeviceId is Guid previous
        && selectedDeviceId is Guid selected
        && previous != selected;

    internal static RuntimeDeviceId? SelectRuntimeId(
        IReadOnlyList<(RuntimeDeviceId RuntimeId, Guid? PersistentDeviceId)> devices,
        Guid? preferredDeviceId,
        RuntimeDeviceId? currentRuntimeId,
        bool userSelectedCurrentDevice)
    {
        ArgumentNullException.ThrowIfNull(devices);

        if (userSelectedCurrentDevice && currentRuntimeId is RuntimeDeviceId current
            && devices.Any(device => device.RuntimeId == current))
            return current;

        if (preferredDeviceId is Guid preferred)
        {
            foreach ((RuntimeDeviceId runtimeId, Guid? persistentDeviceId) in devices)
            {
                if (persistentDeviceId == preferred) return runtimeId;
            }
            return null;
        }

        if (currentRuntimeId is RuntimeDeviceId existing
            && devices.Any(device => device.RuntimeId == existing))
            return existing;

        return devices.Count > 0 ? devices[0].RuntimeId : null;
    }
}
