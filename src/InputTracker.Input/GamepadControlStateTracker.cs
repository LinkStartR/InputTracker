namespace InputTracker.Input;

internal sealed class GamepadControlStateTracker
{
    private readonly Dictionary<GamepadControlId, TrackedValue> _values = [];

    public bool TryUpdate(
        GamepadControlId control,
        int value,
        int? logicalMinimum,
        int? logicalMaximum,
        long timestamp,
        out GamepadControlEvent change)
    {
        bool baseline = !_values.TryGetValue(control, out TrackedValue previous);
        if (!baseline && previous.Value == value)
        {
            change = default;
            return false;
        }

        _values[control] = new TrackedValue(value);
        change = new GamepadControlEvent(
            RuntimeDeviceId.Unattributed,
            control,
            value,
            timestamp,
            baseline,
            logicalMinimum,
            logicalMaximum);
        return true;
    }

    public void Reset() => _values.Clear();

    private readonly record struct TrackedValue(int Value);
}
