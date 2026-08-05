namespace InputTracker.Input.Tests;

public sealed class RawDeviceRegistryTests
{
    [Fact]
    public void ReusedHandleGetsNewRuntimeIdentityAndArrivalGeneration()
    {
        var registry = new RawDeviceRegistry();
        nint handle = new(42);
        var metadata = new RawDeviceMetadata(
            RawDeviceKind.Keyboard,
            "keyboard-path",
            "keyboard",
            DeviceIdentityConfidence.SessionOnly);

        InputDeviceDescriptor first = registry.Arrive(handle, metadata);
        InputDeviceDescriptor removed = registry.Remove(handle, RawDeviceKind.Keyboard);
        InputDeviceDescriptor second = registry.Arrive(handle, metadata);

        Assert.Equal(first.RuntimeId, removed.RuntimeId);
        Assert.NotEqual(first.RuntimeId, second.RuntimeId);
        Assert.Equal(1, first.ArrivalGeneration);
        Assert.Equal(2, second.ArrivalGeneration);
    }

    [Fact]
    public void ZeroHandleIsAlwaysExplicitlyUnattributed()
    {
        var registry = new RawDeviceRegistry();

        InputDeviceDescriptor descriptor = registry.Resolve(0, () => throw new InvalidOperationException());

        Assert.Equal(RuntimeDeviceId.Unattributed, descriptor.RuntimeId);
        Assert.Equal(DeviceIdentityConfidence.Unattributed, descriptor.Confidence);
    }
}
