using System.Runtime.InteropServices;

namespace InputTracker.Input.Win32;

internal sealed class GamepadHidParser : IDisposable
{
    private const ushort HidInputReport = 0;
    private const int HidpStatusSuccess = 0x00110000;
    private const ushort UsagePageButton = 0x09;
    private const ushort UsagePageGenericDesktop = 0x01;
    private const ushort UsageHatSwitch = 0x39;
    private readonly nint _preparsedData;
    private readonly int _reportByteLength;
    private readonly ButtonControl[] _buttons;
    private readonly ValueControl[] _values;
    private bool _unsupportedDescriptor;
    private readonly GamepadControlStateTracker _state = new();

    private GamepadHidParser(nint preparsedData, RawInputInterop.HidpCaps caps)
    {
        _preparsedData = preparsedData;
        _reportByteLength = caps.InputReportByteLength;
        _buttons = ReadButtons(caps.NumberInputButtonCaps);
        _values = ReadValues(caps.NumberInputValueCaps);
    }

    public int ReportByteLength => _reportByteLength;

    public static bool TryCreate(nint deviceHandle, out GamepadHidParser? parser)
    {
        parser = null;
        uint size = 0;
        uint result = RawInputInterop.GetRawInputDevicePreparsedData(
            deviceHandle,
            RawInputInterop.RidiPreparsedData,
            0,
            ref size);
        if (size == 0 || result == uint.MaxValue)
        {
            return false;
        }

        nint data = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            uint copiedSize = size;
            result = RawInputInterop.GetRawInputDevicePreparsedData(
                deviceHandle,
                RawInputInterop.RidiPreparsedData,
                data,
                ref copiedSize);
            if (result == uint.MaxValue || RawInputInterop.HidPGetCaps(data, out RawInputInterop.HidpCaps caps) != HidpStatusSuccess
                || caps.InputReportByteLength == 0)
            {
                return false;
            }

            parser = new GamepadHidParser(data, caps);
            data = 0;
            if (!parser._unsupportedDescriptor && (parser._buttons.Length > 0 || parser._values.Length > 0))
            {
                return true;
            }

            parser.Dispose();
            parser = null;
            return false;
        }
        finally
        {
            if (data != 0)
            {
                Marshal.FreeHGlobal(data);
            }
        }
    }

    public bool TryReadReport(
        ReadOnlySpan<byte> report,
        long timestamp,
        List<GamepadControlEvent> changes,
        out string? error)
    {
        error = null;
        if (report.Length != _reportByteLength)
        {
            error = $"HID report length {report.Length} differs from descriptor length {_reportByteLength}.";
            return false;
        }

        nint reportBuffer = Marshal.AllocHGlobal(report.Length);
        try
        {
            byte[] copy = report.ToArray();
            Marshal.Copy(copy, 0, reportBuffer, copy.Length);
            byte reportId = report[0];
            bool parsedControl = false;
            foreach (IGrouping<ushort, ButtonControl> group in _buttons
                         .Where(control => control.ReportId == reportId)
                         .GroupBy(control => control.Id.LinkCollection))
            {
                const uint usageCapacity = 256;
                uint usageCount = usageCapacity;
                nint usages = Marshal.AllocHGlobal(checked((int)usageCapacity * sizeof(ushort)));
                try
                {
                    int status = RawInputInterop.HidPGetUsages(
                        HidInputReport,
                        UsagePageButton,
                        group.Key,
                        usages,
                        ref usageCount,
                        _preparsedData,
                        reportBuffer,
                        (uint)report.Length);
                    if (status != HidpStatusSuccess)
                    {
                        error = $"HidP_GetUsages failed with status 0x{status:X8} for button collection {group.Key} (buffer capacity {usageCapacity}, reported count {usageCount}).";
                        return false;
                    }

                    parsedControl = true;

                    var pressed = new HashSet<ushort>();
                    for (int index = 0; index < usageCount; index++)
                    {
                        pressed.Add(unchecked((ushort)Marshal.ReadInt16(usages, index * sizeof(ushort))));
                    }

                    foreach (ButtonControl control in group)
                    {
                        SetChangedValue(control.Id, pressed.Contains(control.Id.Usage) ? 1 : 0, timestamp, 0, 1, changes);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(usages);
                }
            }

            foreach (ValueControl control in _values.Where(control => control.ReportId == reportId))
            {
                int status = RawInputInterop.HidPGetUsageValue(
                    HidInputReport,
                    control.Id.UsagePage,
                    control.Id.LinkCollection,
                    control.Id.Usage,
                    out uint rawValue,
                    _preparsedData,
                    reportBuffer,
                    (uint)report.Length);
                if (status != HidpStatusSuccess)
                {
                    error = $"HidP_GetUsageValue failed with status 0x{status:X8} for usage page 0x{control.Id.UsagePage:X4}, usage 0x{control.Id.Usage:X4}.";
                    return false;
                }

                parsedControl = true;

                if (!TryNormalizeLogicalValue(rawValue, control.BitSize, control.LogicalMinimum, out int logicalValue))
                {
                    error = $"HID value usage 0x{control.Id.Usage:X4} has unsupported bit size {control.BitSize}.";
                    return false;
                }

                int value = control.IsHat && control.HasNull && (logicalValue < control.LogicalMinimum || logicalValue > control.LogicalMaximum)
                    ? -1
                    : logicalValue;
                SetChangedValue(control.Id, value, timestamp, control.LogicalMinimum, control.LogicalMaximum, changes);
            }

            if (!parsedControl)
            {
                error = $"HID report ID {reportId} contains no supported button or value controls.";
            }

            return parsedControl;
        }
        finally
        {
            Marshal.FreeHGlobal(reportBuffer);
        }
    }

    public void ResetBaseline() => _state.Reset();

    public void Dispose() => Marshal.FreeHGlobal(_preparsedData);

    private ButtonControl[] ReadButtons(ushort count)
    {
        if (count == 0)
        {
            return [];
        }

        nint buffer = Marshal.AllocHGlobal(checked(count * ButtonCapsSize));
        try
        {
            ushort returned = count;
            if (RawInputInterop.HidPGetButtonCaps(HidInputReport, buffer, ref returned, _preparsedData) != HidpStatusSuccess)
            {
                _unsupportedDescriptor = true;
                return [];
            }

            var controls = new List<ButtonControl>();
            var duplicateIndexes = new Dictionary<(ushort Page, ushort Usage, ushort Link), ushort>();
            for (int index = 0; index < returned; index++)
            {
                nint item = nint.Add(buffer, index * ButtonCapsSize);
                ushort page = unchecked((ushort)Marshal.ReadInt16(item));
                byte reportId = Marshal.ReadByte(item, 2);
                ushort link = unchecked((ushort)Marshal.ReadInt16(item, 6));
                bool isRange = Marshal.ReadByte(item, 12) != 0;
                ushort minimum = unchecked((ushort)Marshal.ReadInt16(item, ButtonCapsUnionOffset));
                ushort maximum = isRange
                    ? unchecked((ushort)Marshal.ReadInt16(item, ButtonCapsUnionOffset + sizeof(ushort)))
                    : minimum;
                if (page != UsagePageButton || maximum < minimum)
                {
                    _unsupportedDescriptor = true;
                    continue;
                }

                for (uint usage = minimum; usage <= maximum; usage++)
                {
                    var key = (page, (ushort)usage, link);
                    ushort element = duplicateIndexes.TryGetValue(key, out ushort previous) ? (ushort)(previous + 1) : (ushort)0;
                    duplicateIndexes[key] = element;
                    controls.Add(new ButtonControl(
                        new GamepadControlId(page, (ushort)usage, link, element),
                        reportId));
                }
            }

            return controls.ToArray();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private ValueControl[] ReadValues(ushort count)
    {
        if (count == 0)
        {
            return [];
        }

        nint buffer = Marshal.AllocHGlobal(checked(count * ValueCapsSize));
        try
        {
            ushort returned = count;
            if (RawInputInterop.HidPGetValueCaps(HidInputReport, buffer, ref returned, _preparsedData) != HidpStatusSuccess)
            {
                _unsupportedDescriptor = true;
                return [];
            }

            var controls = new List<ValueControl>();
            var duplicateIndexes = new Dictionary<(ushort Page, ushort Usage, ushort Link), ushort>();
            for (int index = 0; index < returned; index++)
            {
                nint item = nint.Add(buffer, index * ValueCapsSize);
                ushort page = unchecked((ushort)Marshal.ReadInt16(item));
                byte reportId = Marshal.ReadByte(item, 2);
                ushort link = unchecked((ushort)Marshal.ReadInt16(item, 6));
                bool hasNull = Marshal.ReadByte(item, 16) != 0;
                ushort bitSize = unchecked((ushort)Marshal.ReadInt16(item, 18));
                ushort reportCount = unchecked((ushort)Marshal.ReadInt16(item, 20));
                int logicalMinimum = Marshal.ReadInt32(item, 40);
                int logicalMaximum = Marshal.ReadInt32(item, 44);
                bool isRange = Marshal.ReadByte(item, 12) != 0;
                ushort minimum = unchecked((ushort)Marshal.ReadInt16(item, ValueCapsUnionOffset));
                ushort maximum = isRange
                    ? unchecked((ushort)Marshal.ReadInt16(item, ValueCapsUnionOffset + sizeof(ushort)))
                    : minimum;
                if (maximum < minimum)
                {
                    _unsupportedDescriptor = true;
                    continue;
                }

                uint usageCount = (uint)maximum - minimum + 1;
                if (bitSize is 0 or > 32 || reportCount > usageCount)
                {
                    _unsupportedDescriptor = true;
                    continue;
                }

                for (uint usage = minimum; usage <= maximum; usage++)
                {
                    var key = (page, (ushort)usage, link);
                    ushort element = duplicateIndexes.TryGetValue(key, out ushort previous) ? (ushort)(previous + 1) : (ushort)0;
                    duplicateIndexes[key] = element;
                    controls.Add(new ValueControl(
                        new GamepadControlId(page, (ushort)usage, link, element),
                        reportId,
                        page == UsagePageGenericDesktop && usage == UsageHatSwitch,
                        hasNull,
                        bitSize,
                        logicalMinimum,
                        logicalMaximum));
                }
            }

            return controls.ToArray();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private void SetChangedValue(
        GamepadControlId control,
        int value,
        long timestamp,
        int? logicalMinimum,
        int? logicalMaximum,
        List<GamepadControlEvent> changes)
    {
        if (_state.TryUpdate(control, value, logicalMinimum, logicalMaximum, timestamp, out GamepadControlEvent change))
        {
            changes.Add(change);
        }
    }

    private const int ButtonCapsSize = 72;
    private const int ValueCapsSize = 72;
    private const int ButtonCapsUnionOffset = 56;
    private const int ValueCapsUnionOffset = 56;

    internal static bool TryNormalizeLogicalValue(uint rawValue, ushort bitSize, int logicalMinimum, out int value)
    {
        value = 0;
        if (bitSize is 0 or > 32)
        {
            return false;
        }

        uint mask = bitSize == 32 ? uint.MaxValue : (1u << bitSize) - 1;
        uint field = rawValue & mask;
        if (logicalMinimum < 0 && (field & (1u << (bitSize - 1))) != 0)
        {
            field |= ~mask;
        }

        value = unchecked((int)field);
        return true;
    }

    private readonly record struct ButtonControl(GamepadControlId Id, byte ReportId);

    private readonly record struct ValueControl(
        GamepadControlId Id,
        byte ReportId,
        bool IsHat,
        bool HasNull,
        ushort BitSize,
        int LogicalMinimum,
        int LogicalMaximum);
}
