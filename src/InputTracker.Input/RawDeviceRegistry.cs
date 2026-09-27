namespace InputTracker.Input;

internal sealed class RawDeviceRegistry
{
    private readonly Dictionary<nint, ActiveDevice> _active = [];
    private readonly Dictionary<nint, int> _generations = [];
    private ulong _nextRuntimeId;

    public InputDeviceDescriptor Resolve(nint handle, Func<RawDeviceMetadata> metadataFactory) =>
        Resolve(handle, metadataFactory, out _);

    public InputDeviceDescriptor Resolve(
        nint handle,
        Func<RawDeviceMetadata> metadataFactory,
        out bool created)
    {
        if (handle == 0)
        {
            created = false;
            return Unattributed();
        }

        if (_active.TryGetValue(handle, out ActiveDevice active))
        {
            created = false;
            return active.Descriptor;
        }

        return Arrive(handle, metadataFactory(), out created);
    }

    public InputDeviceDescriptor Arrive(nint handle, RawDeviceMetadata metadata) =>
        Arrive(handle, metadata, out _);

    public InputDeviceDescriptor Arrive(
        nint handle,
        RawDeviceMetadata metadata,
        out bool created)
    {
        if (handle == 0)
        {
            created = false;
            return Unattributed();
        }

        if (_active.TryGetValue(handle, out ActiveDevice active))
        {
            created = false;
            return active.Descriptor;
        }

        created = true;
        int generation = _generations.TryGetValue(handle, out int previous) ? previous + 1 : 1;
        _generations[handle] = generation;
        var descriptor = new InputDeviceDescriptor(
            new RuntimeDeviceId(++_nextRuntimeId),
            generation,
            metadata.Kind,
            null,
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

    public IReadOnlyList<InputDeviceDescriptor> RemoveKind(RawDeviceKind kind)
    {
        var removed = new List<InputDeviceDescriptor>();
        foreach ((nint handle, ActiveDevice active) in _active.ToArray())
        {
            if (active.Descriptor.Kind == kind)
            {
                _active.Remove(handle);
                removed.Add(active.Descriptor);
            }
        }

        return removed;
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
