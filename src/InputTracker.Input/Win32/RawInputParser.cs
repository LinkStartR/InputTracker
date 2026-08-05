namespace InputTracker.Input.Win32;

internal static class RawInputParser
{
    private const ushort KeyBreak = 0x0001;
    private const ushort KeyE0 = 0x0002;
    private const ushort KeyE1 = 0x0004;
    private const ushort KeyboardOverrun = 0x00FF;

    private const ushort MouseMoveAbsolute = 0x0001;
    private const ushort MouseVirtualDesktop = 0x0002;
    private const ushort LeftDown = 0x0001;
    private const ushort LeftUp = 0x0002;
    private const ushort RightDown = 0x0004;
    private const ushort RightUp = 0x0008;
    private const ushort MiddleDown = 0x0010;
    private const ushort MiddleUp = 0x0020;
    private const ushort X1Down = 0x0040;
    private const ushort X1Up = 0x0080;
    private const ushort X2Down = 0x0100;
    private const ushort X2Up = 0x0200;
    private const ushort VerticalWheel = 0x0400;
    private const ushort HorizontalWheel = 0x0800;

    internal static bool TryKeyboard(
        nint handle,
        ushort makeCode,
        ushort flags,
        long timestamp,
        out InputIngressEvent input)
    {
        if (makeCode is 0 or KeyboardOverrun)
        {
            input = default;
            return false;
        }

        PhysicalKeyPrefix prefix = (flags & KeyE1) != 0
            ? PhysicalKeyPrefix.E1
            : (flags & KeyE0) != 0
                ? PhysicalKeyPrefix.E0
                : PhysicalKeyPrefix.None;
        input = new InputIngressEvent(
            IngressEventKind.Keyboard,
            handle,
            timestamp,
            MakeCode: makeCode,
            KeyPrefix: prefix,
            KeyTransition: (flags & KeyBreak) != 0
                ? PhysicalKeyTransition.Up
                : PhysicalKeyTransition.Down);
        return true;
    }

    internal static InputIngressEvent Mouse(
        nint handle,
        ushort motionFlags,
        ushort buttonFlags,
        ushort buttonData,
        int deltaX,
        int deltaY,
        long timestamp)
    {
        MouseButtonChanges changes = MouseButtonChanges.None;
        if ((buttonFlags & LeftDown) != 0) changes |= MouseButtonChanges.LeftDown;
        if ((buttonFlags & LeftUp) != 0) changes |= MouseButtonChanges.LeftUp;
        if ((buttonFlags & RightDown) != 0) changes |= MouseButtonChanges.RightDown;
        if ((buttonFlags & RightUp) != 0) changes |= MouseButtonChanges.RightUp;
        if ((buttonFlags & MiddleDown) != 0) changes |= MouseButtonChanges.MiddleDown;
        if ((buttonFlags & MiddleUp) != 0) changes |= MouseButtonChanges.MiddleUp;
        if ((buttonFlags & X1Down) != 0) changes |= MouseButtonChanges.X1Down;
        if ((buttonFlags & X1Up) != 0) changes |= MouseButtonChanges.X1Up;
        if ((buttonFlags & X2Down) != 0) changes |= MouseButtonChanges.X2Down;
        if ((buttonFlags & X2Up) != 0) changes |= MouseButtonChanges.X2Up;

        short signedData = unchecked((short)buttonData);
        return new InputIngressEvent(
            IngressEventKind.Mouse,
            handle,
            timestamp,
            DeltaX: deltaX,
            DeltaY: deltaY,
            MotionKind: (motionFlags & MouseMoveAbsolute) != 0
                ? MouseMotionKind.Absolute
                : MouseMotionKind.Relative,
            UsesVirtualDesktop: (motionFlags & MouseVirtualDesktop) != 0,
            ButtonChanges: changes,
            VerticalWheelDelta: (buttonFlags & VerticalWheel) != 0 ? signedData : (short)0,
            HorizontalWheelDelta: (buttonFlags & HorizontalWheel) != 0 ? signedData : (short)0);
    }
}
