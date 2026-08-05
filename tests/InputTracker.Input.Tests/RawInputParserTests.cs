using InputTracker.Input.Win32;

namespace InputTracker.Input.Tests;

public sealed class RawInputParserTests
{
    [Theory]
    [InlineData(0x00, PhysicalKeyPrefix.None, PhysicalKeyTransition.Down)]
    [InlineData(0x01, PhysicalKeyPrefix.None, PhysicalKeyTransition.Up)]
    [InlineData(0x02, PhysicalKeyPrefix.E0, PhysicalKeyTransition.Down)]
    [InlineData(0x05, PhysicalKeyPrefix.E1, PhysicalKeyTransition.Up)]
    public void KeyboardUsesOnlyPhysicalScanCodeFlags(
        ushort flags,
        PhysicalKeyPrefix expectedPrefix,
        PhysicalKeyTransition expectedTransition)
    {
        bool parsed = RawInputParser.TryKeyboard(new nint(5), 0x1E, flags, 123, out InputIngressEvent input);

        Assert.True(parsed);
        Assert.Equal(IngressEventKind.Keyboard, input.Kind);
        Assert.Equal((ushort)0x1E, input.MakeCode);
        Assert.Equal(expectedPrefix, input.KeyPrefix);
        Assert.Equal(expectedTransition, input.KeyTransition);
        Assert.Equal(123, input.Timestamp);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0xFF)]
    public void KeyboardRejectsMissingAndOverrunScanCodes(ushort makeCode)
    {
        Assert.False(RawInputParser.TryKeyboard(1, makeCode, 0, 1, out _));
    }

    [Fact]
    public void MousePreservesRelativeMovementButtonsAndVerticalWheel()
    {
        InputIngressEvent input = RawInputParser.Mouse(
            new nint(7),
            motionFlags: 0,
            buttonFlags: 0x0001 | 0x0008 | 0x0020 | 0x0080 | 0x0200 | 0x0400,
            buttonData: unchecked((ushort)-120),
            deltaX: -12,
            deltaY: 34,
            timestamp: 456);

        Assert.Equal(MouseMotionKind.Relative, input.MotionKind);
        Assert.False(input.UsesVirtualDesktop);
        Assert.Equal(-12, input.DeltaX);
        Assert.Equal(34, input.DeltaY);
        Assert.Equal((short)-120, input.VerticalWheelDelta);
        Assert.Equal(0, input.HorizontalWheelDelta);
        Assert.Equal(
            MouseButtonChanges.LeftDown |
            MouseButtonChanges.RightUp |
            MouseButtonChanges.MiddleUp |
            MouseButtonChanges.X1Up |
            MouseButtonChanges.X2Up,
            input.ButtonChanges);
    }

    [Fact]
    public void MousePreservesAbsoluteVirtualDesktopAndHorizontalWheel()
    {
        InputIngressEvent input = RawInputParser.Mouse(
            new nint(8),
            motionFlags: 0x0001 | 0x0002,
            buttonFlags: 0x0002 | 0x0004 | 0x0010 | 0x0040 | 0x0100 | 0x0800,
            buttonData: 120,
            deltaX: 32767,
            deltaY: 65535,
            timestamp: 789);

        Assert.Equal(MouseMotionKind.Absolute, input.MotionKind);
        Assert.True(input.UsesVirtualDesktop);
        Assert.Equal((short)120, input.HorizontalWheelDelta);
        Assert.Equal(0, input.VerticalWheelDelta);
        Assert.Equal(
            MouseButtonChanges.LeftUp |
            MouseButtonChanges.RightDown |
            MouseButtonChanges.MiddleDown |
            MouseButtonChanges.X1Down |
            MouseButtonChanges.X2Down,
            input.ButtonChanges);
    }
}
