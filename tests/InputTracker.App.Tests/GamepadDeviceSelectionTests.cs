using InputTracker.Input;
using InputTracker.Storage;

namespace InputTracker.App.Tests;

public sealed class GamepadDeviceSelectionTests
{
    [Fact]
    public void ReconnectSelectsSavedStorageDeviceEvenWhenAnonymousDescriptorsHaveNoInterfacePath()
    {
        Guid firstStorageId = Guid.NewGuid();
        Guid rememberedStorageId = Guid.NewGuid();
        var firstDescriptor = new InputDeviceDescriptor(
            new RuntimeDeviceId(1), 1, RawDeviceKind.Gamepad, null, null, DeviceIdentityConfidence.RequiresConfirmation);
        var rememberedDescriptor = new InputDeviceDescriptor(
            new RuntimeDeviceId(2), 1, RawDeviceKind.Gamepad, null, null, DeviceIdentityConfidence.RequiresConfirmation);
        (RuntimeDeviceId RuntimeId, Guid? PersistentDeviceId)[] devices =
        [
            (firstDescriptor.RuntimeId, firstStorageId),
            (rememberedDescriptor.RuntimeId, rememberedStorageId)
        ];

        RuntimeDeviceId? selected = GamepadDeviceSelection.SelectRuntimeId(
            devices, rememberedStorageId, currentRuntimeId: null, userSelectedCurrentDevice: false);

        Assert.Equal(rememberedDescriptor.RuntimeId, selected);
        Assert.Null(firstDescriptor.InterfacePath);
        Assert.Null(rememberedDescriptor.InterfacePath);
    }

    [Fact]
    public void MissingRememberedDeviceWaitsInsteadOfSelectingAnotherDeviceAutomatically()
    {
        Guid rememberedStorageId = Guid.NewGuid();
        (RuntimeDeviceId RuntimeId, Guid? PersistentDeviceId)[] devices =
        [
            (new RuntimeDeviceId(1), Guid.NewGuid()),
            (new RuntimeDeviceId(2), Guid.NewGuid())
        ];

        Assert.Null(GamepadDeviceSelection.SelectRuntimeId(
            devices, rememberedStorageId, currentRuntimeId: null, userSelectedCurrentDevice: false));
    }

    [Fact]
    public void SameUncalibratedDeviceKeepsUsersInitialXboxLayoutSelectionDuringRefresh()
    {
        Guid deviceId = Guid.NewGuid();

        Assert.False(GamepadDeviceSelection.ShouldClearUnrememberedLayout(
            previousDeviceId: null, selectedDeviceId: deviceId, savedLayout: null));
        Assert.False(GamepadDeviceSelection.ShouldClearUnrememberedLayout(
            deviceId, deviceId, savedLayout: null));
        Assert.True(GamepadDeviceSelection.ShouldClearUnrememberedLayout(
            deviceId, Guid.NewGuid(), savedLayout: null));
        Assert.False(GamepadDeviceSelection.ShouldClearUnrememberedLayout(
            deviceId, deviceId, savedLayout: StorageGamepadLayout.Xbox));
    }
}
