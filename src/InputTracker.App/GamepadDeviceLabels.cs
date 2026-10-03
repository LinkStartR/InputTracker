using InputTracker.Storage;

namespace InputTracker;

internal static class GamepadDeviceLabels
{
    internal static IReadOnlyDictionary<Guid, string> Create(IReadOnlyList<StorageDeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var labels = new Dictionary<Guid, string>();
        int ordinal = 0;
        foreach (StorageDeviceInfo device in devices
                     .Where(value => value.Kind == StorageDeviceKind.Gamepad)
                     .OrderBy(value => value.CreatedUtcMicroseconds)
                     .ThenBy(value => value.DeviceId))
        {
            ordinal++;
            labels[device.DeviceId] = string.IsNullOrWhiteSpace(device.Alias)
                ? $"手柄 {ordinal}"
                : device.Alias;
        }

        return labels;
    }
}
