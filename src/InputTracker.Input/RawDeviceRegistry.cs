namespace InputTracker.Input;

internal sealed class RawDeviceRegistry
{
    private readonly Dictionary<nint, ActiveDevice> _active = [];
    private readonly Dictionary<nint, int> _generations = [];
    private ulong _nextRuntimeId;

    public InputDeviceDescriptor Resolve(nint handle, Func<RawDeviceMetadata> metadataFactory)
    {
        if (handle == 0)
        {
            return Unattributed();
        }

        if (_active.TryGetValue(handle, out ActiveDevice active))
        {
            return active.Descriptor;
        }

        return Arrive(handle, metadataFactory());
    }

    public InputDeviceDescriptor Arrive(nint handle, RawDeviceMetadata metadata)
    {
        if (handle == 0)
        {
            return Unattributed();
        }

        int generation = _generations.TryGetValue(handle, out int previous) ? previous + 1 : 1;
        _generations[handle] = generation;
        var descriptor = new InputDeviceDescriptor(
            new RuntimeDeviceId(++_nextRuntimeId),
            generation,
            metadata.Kind,
            metadata.InterfacePath,
            metadata.HardwareSummary,
            metadata.Confidence);
        _active[handle] = new ActiveDevice(descriptor);
        return descriptor;
    }

    public InputDeviceDescriptor Remove(nint handle, RawDeviceKind hintedKind)
    {
        if (handle == 0)
        {
            return Unattributed();
        }

        if (_active.Remove(handle, out ActiveDevice active))
        {
            return active.Descriptor;
        }

        int generation = _generations.TryGetValue(handle, out int previous) ? previous : 0;
        return new InputDeviceDescriptor(
            new RuntimeDeviceId(++_nextRuntimeId),
            generation,
            hintedKind,
            null,
            null,
            DeviceIdentityConfidence.RequiresConfirmation);
    }

    private static InputDeviceDescriptor Unattributed() => new(
        RuntimeDeviceId.Unattributed,
        0,
        RawDeviceKind.Unknown,
        null,
        null,
        DeviceIdentityConfidence.Unattributed);

    private readonly record struct ActiveDevice(InputDeviceDescriptor Descriptor);
}
