using InputTracker.Input.Win32;

namespace InputTracker.AcceptanceTests.InputCore;

public sealed class MouseAndDeviceAcceptanceTests
{
    [Fact]
    public void MouseFidelityAndDeviceGenerationPreventCrossDeviceAttribution()
    {
        InputIngressEvent relative = RawInputParser.Mouse(
            20,
            motionFlags: 0,
            buttonFlags: 0,
            buttonData: 0,
            deltaX: 9,
            deltaY: -8,
            timestamp: 1233);
        Assert.Equal(MouseMotionKind.Relative, relative.MotionKind);
        Assert.False(relative.UsesVirtualDesktop);
        Assert.Equal(9, relative.DeltaX);
        Assert.Equal(-8, relative.DeltaY);

        InputIngressEvent mouse = RawInputParser.Mouse(
            21,
            motionFlags: 0x0001 | 0x0002,
            buttonFlags: 0x0001 | 0x0002 | 0x0004 | 0x0008 | 0x0010 | 0x0020 | 0x0040 | 0x0080 | 0x0100 | 0x0200 | 0x0400 | 0x0800,
            buttonData: unchecked((ushort)-120),
            deltaX: -32768,
            deltaY: 32767,
            timestamp: 1234);
        Assert.Equal(MouseMotionKind.Absolute, mouse.MotionKind);
        Assert.True(mouse.UsesVirtualDesktop);
        Assert.Equal(-32768, mouse.DeltaX);
        Assert.Equal(32767, mouse.DeltaY);
        Assert.Equal((short)-120, mouse.VerticalWheelDelta);
        Assert.Equal((short)-120, mouse.HorizontalWheelDelta);
        Assert.Equal(
            MouseButtonChanges.LeftDown | MouseButtonChanges.LeftUp |
            MouseButtonChanges.RightDown | MouseButtonChanges.RightUp |
            MouseButtonChanges.MiddleDown | MouseButtonChanges.MiddleUp |
            MouseButtonChanges.X1Down | MouseButtonChanges.X1Up |
            MouseButtonChanges.X2Down | MouseButtonChanges.X2Up,
            mouse.ButtonChanges);

        var registry = new RawDeviceRegistry();
        var metadata = new RawDeviceMetadata(
            RawDeviceKind.Mouse,
            "acceptance-mouse",
            "acceptance mouse",
            DeviceIdentityConfidence.SessionOnly);
        InputDeviceDescriptor first = registry.Arrive(21, metadata);
        InputDeviceDescriptor removed = registry.Remove(21, RawDeviceKind.Mouse);
        InputDeviceDescriptor reused = registry.Arrive(21, metadata);
        InputDeviceDescriptor unattributed = registry.Resolve(0, () => throw new InvalidOperationException());

        Assert.Equal(first.RuntimeId, removed.RuntimeId);
        Assert.NotEqual(first.RuntimeId, reused.RuntimeId);
        Assert.Equal(1, first.ArrivalGeneration);
        Assert.Equal(2, reused.ArrivalGeneration);
        Assert.Equal(RuntimeDeviceId.Unattributed, unattributed.RuntimeId);
        Assert.Equal(DeviceIdentityConfidence.Unattributed, unattributed.Confidence);
        InputCoreAcceptanceEvidence.Write("mouse-device", new
        {
            relative = true,
            absolute = true,
            left = true,
            right = true,
            middle = true,
            x1 = true,
            x2 = true,
            verticalWheel = mouse.VerticalWheelDelta,
            horizontalWheel = mouse.HorizontalWheelDelta,
            signedButtonData = true,
            firstRuntimeId = first.RuntimeId.Value,
            reusedRuntimeId = reused.RuntimeId.Value,
            firstGeneration = first.ArrivalGeneration,
            reusedGeneration = reused.ArrivalGeneration,
            unattributed = unattributed.RuntimeId.Value == 0
        });
    }
}
