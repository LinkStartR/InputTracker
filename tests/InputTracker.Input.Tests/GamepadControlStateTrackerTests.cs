using InputTracker.Input.Win32;

namespace InputTracker.Input.Tests;

public sealed class GamepadControlStateTrackerTests
{
    [Fact]
    public void EmitsBaselineThenOnlyValueChangesAndRebaselinesAfterReset()
    {
        var tracker = new GamepadControlStateTracker();
        var button = new GamepadControlId(0x09, 1, 2, 0);
        var trigger = new GamepadControlId(0x01, 0x32, 2, 0);

        Assert.True(tracker.TryUpdate(button, 1, 0, 1, 10, out GamepadControlEvent baseline));
        Assert.True(baseline.IsBaseline);
        Assert.Equal(1, baseline.Value);
        Assert.False(tracker.TryUpdate(button, 1, 0, 1, 11, out _));
        Assert.True(tracker.TryUpdate(button, 0, 0, 1, 12, out GamepadControlEvent released));
        Assert.False(released.IsBaseline);
        Assert.Equal(0, released.Value);
        Assert.True(tracker.TryUpdate(button, 1, 0, 1, 13, out GamepadControlEvent pressedAgain));
        Assert.Equal(1, pressedAgain.Value);

        Assert.True(tracker.TryUpdate(trigger, 0x180, 0, 0x3FF, 14, out GamepadControlEvent axis));
        Assert.True(axis.IsBaseline);
        Assert.Equal(0x180, axis.Value);
        Assert.Equal(0, axis.LogicalMinimum);
        Assert.Equal(0x3FF, axis.LogicalMaximum);

        tracker.Reset();
        Assert.True(tracker.TryUpdate(button, 0, 0, 1, 15, out GamepadControlEvent afterReset));
        Assert.True(afterReset.IsBaseline);
    }

    [Theory]
    [InlineData(0xFF, 8, -127, -1)]
    [InlineData(0x800, 12, -2048, -2048)]
    [InlineData(0x7F, 8, 0, 127)]
    public void HidValuesUseDescriptorBitSizeAndSignedLogicalMinimum(
        uint rawValue,
        ushort bitSize,
        int logicalMinimum,
        int expected)
    {
        Assert.True(GamepadHidParser.TryNormalizeLogicalValue(rawValue, bitSize, logicalMinimum, out int value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData(0x05)]
    [InlineData(0x04)]
    public void ClassifiesGamepadAndJoystickTopLevelCollections(ushort usage)
    {
        Assert.Equal(
            RawDeviceKind.Gamepad,
            RawDeviceInfoProvider.ClassifyRawDeviceKind(
                RawInputInterop.RimTypeHid,
                RawInputInterop.UsagePageGenericDesktop,
                usage,
                RawDeviceKind.Other));
    }

    [Fact]
    public void RegistersOnlyGamepadAndJoystickTopLevelCollections()
    {
        const uint flags = RawInputInterop.RidevInputSink | RawInputInterop.RidevDevNotify;
        nint target = new(42);
        RawInputInterop.RawInputDevice[] registrations = RawInputInterop.GamepadRegistrations(flags, target);

        Assert.Equal(2, registrations.Length);
        Assert.All(registrations, device =>
        {
            Assert.Equal(RawInputInterop.UsagePageGenericDesktop, device.UsagePage);
            Assert.Equal(flags, device.Flags);
            Assert.Equal(target, device.Target);
        });
        Assert.Equal(
            new ushort[] { RawInputInterop.UsageGamepad, RawInputInterop.UsageJoystick },
            registrations.Select(device => device.Usage).ToArray());
        Assert.All(RawInputInterop.GamepadRemovals(), device => Assert.Equal(RawInputInterop.RidevRemove, device.Flags));
    }

    [Fact]
    public void ExistingDeviceDiscoveryFiltersHidGamepadsAndDeduplicatesHandles()
    {
        nint gamepad = new(11);
        nint joystick = new(12);
        nint mouse = new(13);
        var devices = new[]
        {
            Device(gamepad, RawInputInterop.RimTypeHid),
            Device(gamepad, RawInputInterop.RimTypeHid),
            Device(joystick, RawInputInterop.RimTypeHid),
            Device(mouse, RawInputInterop.RimTypeMouse),
            Device(0, RawInputInterop.RimTypeHid)
        };
        var queried = new List<nint>();

        nint[] handles = GamepadDeviceDiscovery.FindGamepadDeviceHandles(devices, handle =>
        {
            queried.Add(handle);
            return handle switch
            {
                var value when value == gamepad => DeviceInformation(
                    RawInputInterop.RimTypeHid,
                    RawInputInterop.UsagePageGenericDesktop,
                    RawInputInterop.UsageGamepad),
                var value when value == joystick => DeviceInformation(
                    RawInputInterop.RimTypeHid,
                    RawInputInterop.UsagePageGenericDesktop,
                    RawInputInterop.UsageJoystick),
                _ => null
            };
        });

        Assert.Equal(new[] { gamepad, joystick }, handles);
        Assert.Equal(new[] { gamepad, joystick }, queried);
    }

    [Fact]
    public void ExistingDeviceDiscoverySkipsMismatchedDescriptorsAndPerDeviceFailures()
    {
        nint wrongType = new(21);
        nint wrongPage = new(22);
        nint wrongUsage = new(23);
        nint missingInfo = new(24);
        nint failedInfo = new(25);
        nint valid = new(26);
        var devices = new[]
        {
            Device(wrongType, RawInputInterop.RimTypeHid),
            Device(wrongPage, RawInputInterop.RimTypeHid),
            Device(wrongUsage, RawInputInterop.RimTypeHid),
            Device(missingInfo, RawInputInterop.RimTypeHid),
            Device(failedInfo, RawInputInterop.RimTypeHid),
            Device(valid, RawInputInterop.RimTypeHid)
        };

        nint[] handles = GamepadDeviceDiscovery.FindGamepadDeviceHandles(devices, handle =>
        {
            if (handle == failedInfo)
            {
                throw new InvalidOperationException("Device was removed during enumeration.");
            }

            return handle switch
            {
                var value when value == wrongType => DeviceInformation(
                    RawInputInterop.RimTypeMouse,
                    RawInputInterop.UsagePageGenericDesktop,
                    RawInputInterop.UsageGamepad),
                var value when value == wrongPage => DeviceInformation(
                    RawInputInterop.RimTypeHid,
                    0x09,
                    RawInputInterop.UsageGamepad),
                var value when value == wrongUsage => DeviceInformation(
                    RawInputInterop.RimTypeHid,
                    RawInputInterop.UsagePageGenericDesktop,
                    RawInputInterop.UsageKeyboard),
                var value when value == valid => DeviceInformation(
                    RawInputInterop.RimTypeHid,
                    RawInputInterop.UsagePageGenericDesktop,
                    RawInputInterop.UsageGamepad),
                _ => null
            };
        });

        Assert.Equal(new[] { valid }, handles);
    }

    [Fact]
    public void ReportNormalizationPreservesAxisBoundsAndBaselineIntoIngress()
    {
        var change = new GamepadControlEvent(
            RuntimeDeviceId.Unattributed,
            new GamepadControlId(0x01, 0x32, 3, 0),
            127,
            99,
            IsBaseline: true,
            LogicalMinimum: 0,
            LogicalMaximum: 255);

        InputIngressEvent ingress = RawInputMessageWindow.ToGamepadIngressEvent(new nint(44), change);

        Assert.Equal(IngressEventKind.Gamepad, ingress.Kind);
        Assert.Equal(change.Control, ingress.GamepadControl);
        Assert.Equal(change.Value, ingress.GamepadValue);
        Assert.True(ingress.GamepadBaseline);
        Assert.Equal(change.LogicalMinimum, ingress.GamepadLogicalMinimum);
        Assert.Equal(change.LogicalMaximum, ingress.GamepadLogicalMaximum);
    }

    private static RawInputInterop.NativeRawInputDeviceList Device(nint handle, uint type) => new()
    {
        DeviceHandle = handle,
        Type = type
    };

    private static RawInputInterop.NativeRawDeviceInfo DeviceInformation(
        uint type,
        ushort usagePage,
        ushort usage) => new()
    {
        Type = type,
        Data = new RawInputInterop.NativeRawDeviceInfoData
        {
            Hid = new RawInputInterop.NativeHidDeviceInfo
            {
                UsagePage = usagePage,
                Usage = usage
            }
        }
    };
}
