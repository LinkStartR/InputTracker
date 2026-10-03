using System.Diagnostics;
using InputTracker.Input;
using InputTracker.Storage;

namespace InputTracker;

/// <summary>Collects one isolated press/release without assigning names to unknown HID controls.</summary>
internal sealed class GamepadAutomaticCalibration
{
    private static readonly long SettleTicks = Math.Max(1, Stopwatch.Frequency / 50);
    private readonly Dictionary<GamepadControlId, GamepadControlEvent> _values;
    private readonly Dictionary<GamepadControlId, GamepadControlEvent> _resting = [];
    private readonly Dictionary<GamepadControlId, int> _stepValues = [];
    private StorageGamepadButton _target;
    private Candidate? _candidate;
    private bool _blocked;
    private bool _restingConfirmed;
    private long _lastTimestamp;
    private long _targetAfter;

    internal GamepadAutomaticCalibration(IEnumerable<GamepadControlEvent> baseline)
    {
        _values = baseline.ToDictionary(value => value.Control);
        _lastTimestamp = _values.Count == 0 ? 0 : _values.Values.Max(value => value.Timestamp);
    }

    internal string Feedback { get; private set; } = "请只按提示的一个按键，然后完全松开。";
    internal bool AwaitingRestingConfirmation => IsTrigger(_target) && !_restingConfirmed;
    internal IReadOnlyList<GamepadControlEvent> LatestValues => _values.Values.ToArray();

    internal void BeginTarget(StorageGamepadButton target, long? after = null)
    {
        _target = target;
        _candidate = null;
        _targetAfter = Math.Max(_lastTimestamp, after ?? _lastTimestamp);
        _stepValues.Clear();
        foreach (var pair in _values)
            _stepValues[pair.Key] = _resting.TryGetValue(pair.Key, out GamepadControlEvent resting)
                ? resting.Value : pair.Value.Value;
        _blocked = !AllControlsResting();
        Feedback = _blocked ? "请先松开所有按键，再按提示的一个按键。"
            : AwaitingRestingConfirmation ? "请松开两个扳机并确认松开位置；数字扳机也可直接按下再松开。"
            : "请只按提示的一个按键，然后完全松开。";
    }

    internal bool ConfirmRestingPoint()
    {
        if (!IsTrigger(_target) || HasActiveDigitalControl()) return false;
        _resting.Clear();
        foreach (GamepadControlEvent value in _values.Values.Where(IsTriggerAxis))
        {
            // Centered axes could be sticks or shared triggers. Their meaning needs manual calibration.
            if (value.Value == value.LogicalMinimum || value.Value == value.LogicalMaximum)
                _resting[value.Control] = value;
        }
        _restingConfirmed = _resting.Count > 0;
        Feedback = _restingConfirmed ? "松开位置已确认，请按到底再完全松开提示的扳机。"
            : "未找到端点松开的独立扳机轴。数字扳机可直接按放；居中轴或共享轴请改用手动校准。";
        return _restingConfirmed;
    }

    internal void Observe(GamepadControlEvent value)
    {
        bool known = _values.TryGetValue(value.Control, out GamepadControlEvent previous);
        if (known && value.Timestamp < previous.Timestamp) return;
        _lastTimestamp = Math.Max(_lastTimestamp, value.Timestamp);
        _values[value.Control] = value;
        if (value.IsBaseline)
        {
            _stepValues[value.Control] = value.Value;
            if (IsDigital(value) && !IsNeutral(value)) Reject("请先松开所有按键，再按提示的一个按键。");
            else UnblockIfResting();
            return;
        }
        if (value.Timestamp <= _targetAfter)
        {
            if (!_stepValues.ContainsKey(value.Control)) _stepValues[value.Control] = value.Value;
            if (IsMeaningfulChange(value)) Reject("检测到上一提示期间按住的控制，请全部松开后重新按当前按键。");
            else UnblockIfResting();
            return;
        }
        if (!known)
        {
            _stepValues[value.Control] = value.Value;
            if (IsDigital(value) && !IsNeutral(value)) Reject("正在建立松开状态，请松开后重新按一次。");
            return;
        }
        if (previous.Value == value.Value) return;
        if (_blocked)
        {
            if (IsDigital(value) && IsNeutral(value)) _stepValues[value.Control] = value.Value;
            UnblockIfResting();
            return;
        }

        if (_candidate is { } candidate)
        {
            if (candidate.Control != value.Control)
            {
                if (IsMeaningfulChange(value)) Reject("检测到多个控制同时变化，请全部松开后重试当前按键。");
                return;
            }
            if (candidate.ReleasedAt.HasValue)
            {
                if (candidate.Binding.AxisDirection.HasValue && IsAxisResting(value)) return;
                Reject("检测到重复按下或抖动，请完全松开后重试当前按键。");
                return;
            }
            if (candidate.Binding.AxisDirection.HasValue)
            {
                int resting = _resting[value.Control].Value;
                long span = (long)value.LogicalMaximum!.Value - value.LogicalMinimum!.Value;
                long delta = (long)value.Value - resting;
                if (Math.Abs((long)delta) > Math.Abs((long)candidate.Peak - resting))
                {
                    candidate = candidate with { Peak = value.Value, Binding = CreateAxisBinding(value, resting) };
                    _candidate = candidate;
                }
                if (Math.Abs((long)delta) > Math.Max(1, span * .05)) return;
            }
            else if (!IsNeutral(value))
            {
                if (value.Value != candidate.Peak) Reject("方向发生变化，请松开后只按提示的一个方向。");
                return;
            }
            if (value.Timestamp - candidate.PressedAt < SettleTicks)
            {
                Reject("按放时间太短，请稍按住后再完全松开。");
                return;
            }
            _candidate = candidate with { ReleasedAt = value.Timestamp };
            Feedback = "已检测到按下和松开，正在确认。";
            return;
        }

        if (IsDigital(value) && !IsNeutral(value))
        {
            if (HasActiveDigitalControl(value.Control)
                || _values.Values.Any(other => other.Control != value.Control && IsMeaningfulChange(other)))
            {
                Reject("检测到多个按键，请全部松开后重试当前按键。");
                return;
            }
            if (IsHat(value) && !IsDpad(_target))
            {
                Reject("当前提示的是按键，请勿使用方向键；松开后重试。");
                return;
            }
            if (IsHat(value) && value.LogicalMaximum - value.LogicalMinimum == 7
                && (value.Value - value.LogicalMinimum) % 2 != 0)
            {
                Reject("检测到斜向，请松开后只按提示的一个方向。");
                return;
            }
            int[] active = IsHat(value) && value.LogicalMaximum - value.LogicalMinimum == 7
                ? new[] { value.Value, value.LogicalMinimum!.Value + (value.Value - value.LogicalMinimum.Value + 7) % 8,
                    value.LogicalMinimum.Value + (value.Value - value.LogicalMinimum.Value + 1) % 8 }.Order().ToArray()
                : [value.Value];
            var binding = new StorageGamepadHidBinding(_target, value.Control.UsagePage, value.Control.Usage,
                value.Control.LinkCollection, value.Control.ElementIndex, active);
            _candidate = new(value.Control, binding, value.Timestamp, value.Value, null);
            Feedback = "已检测到按下，请完全松开。";
            return;
        }

        if (IsTrigger(_target) && IsTriggerAxis(value)
            && _resting.TryGetValue(value.Control, out GamepadControlEvent restingPoint))
        {
            long span = (long)value.LogicalMaximum!.Value - value.LogicalMinimum!.Value;
            if (Math.Abs((long)value.Value - restingPoint.Value) < Math.Ceiling(span * .55)) return;
            if (HasActiveDigitalControl() || _values.Values.Any(other => other.Control != value.Control && IsMeaningfulChange(other)))
            {
                Reject("检测到多个控制同时变化，请全部松开后重试当前扳机。");
                return;
            }
            _candidate = new(value.Control, CreateAxisBinding(value, restingPoint.Value), value.Timestamp, value.Value, null);
            Feedback = "已检测到扳机按下，请完全松开。";
        }
        else if (IsMeaningfulChange(value))
        {
            Feedback = IsTrigger(_target)
                ? "该轴不能安全自动识别为独立扳机，请确认松开位置或改用手动校准。"
                : "检测到摇杆或其他轴，请只按提示的按键。";
        }
    }

    internal StorageGamepadHidBinding? TryComplete(long timestamp)
    {
        if (_candidate is not { ReleasedAt: long releasedAt } candidate || timestamp - releasedAt < SettleTicks)
            return null;
        _candidate = null;
        return candidate.Binding;
    }

    internal bool AffectsSettling(GamepadControlEvent value) => IsDigital(value)
        || _candidate is { } candidate && candidate.Control == value.Control
            && (!candidate.ReleasedAt.HasValue || !IsAxisResting(value))
        || IsMeaningfulChange(value);

    private bool IsAxisResting(GamepadControlEvent value) =>
        _resting.TryGetValue(value.Control, out GamepadControlEvent resting)
        && Math.Abs((long)value.Value - resting.Value) <= Math.Max(1, ((long)value.LogicalMaximum!.Value - value.LogicalMinimum!.Value) * .05);

    internal void RetryDuplicate(StorageGamepadButton target, long? after = null)
    {
        BeginTarget(target, after);
        Feedback = "这个控制已经用于其他按键，请按当前提示的另一个物理按键。";
    }

    private StorageGamepadHidBinding CreateAxisBinding(GamepadControlEvent value, int resting)
    {
        long delta = (long)value.Value - resting;
        return new(_target, value.Control.UsagePage, value.Control.Usage, value.Control.LinkCollection,
            value.Control.ElementIndex, [], (int)(resting + Math.Round(delta * .55)),
            (int)(resting + Math.Round(delta * .30)), delta > 0
                ? StorageGamepadAxisDirection.Increasing : StorageGamepadAxisDirection.Decreasing);
    }

    private bool IsMeaningfulChange(GamepadControlEvent value)
    {
        if (IsDigital(value)) return !IsNeutral(value);
        if (value.LogicalMinimum is not int min || value.LogicalMaximum is not int max || max <= min
            || !_stepValues.TryGetValue(value.Control, out int resting)) return false;
        return Math.Abs((long)value.Value - resting) >= ((long)max - min) * .20;
    }

    private bool AllControlsResting() => !_values.Values.Any(IsMeaningfulChange);
    private void UnblockIfResting()
    {
        if (!_blocked || !AllControlsResting()) return;
        _blocked = false;
        Feedback = "已松开，请只按提示的一个按键，然后完全松开。";
    }
    private bool HasActiveDigitalControl(GamepadControlId? except = null) =>
        _values.Values.Any(value => value.Control != except && IsDigital(value) && !IsNeutral(value));

    private void Reject(string feedback)
    {
        _candidate = null;
        _blocked = !AllControlsResting();
        Feedback = feedback;
    }

    private static bool IsTrigger(StorageGamepadButton button) =>
        button is StorageGamepadButton.LeftTrigger or StorageGamepadButton.RightTrigger;
    private static bool IsDpad(StorageGamepadButton button) =>
        button is StorageGamepadButton.DpadUp or StorageGamepadButton.DpadDown or StorageGamepadButton.DpadLeft or StorageGamepadButton.DpadRight;
    private static bool IsHat(GamepadControlEvent value) => value.Control.UsagePage == 0x01 && value.Control.Usage == 0x39;
    private static bool IsDigital(GamepadControlEvent value) => value.Control.UsagePage == 0x09 || IsHat(value);
    private static bool IsNeutral(GamepadControlEvent value) => value.Control.UsagePage == 0x09 ? value.Value == 0
        : value.Value == -1 || value.LogicalMinimum.HasValue && value.LogicalMaximum.HasValue
            && (value.Value < value.LogicalMinimum || value.Value > value.LogicalMaximum);
    private static bool IsTriggerAxis(GamepadControlEvent value) => value.Control.UsagePage == 0x01
        && value.Control.Usage is 0x32 or 0x35 or 0x36
        && value.LogicalMinimum.HasValue && value.LogicalMaximum > value.LogicalMinimum;

    private sealed record Candidate(GamepadControlId Control, StorageGamepadHidBinding Binding,
        long PressedAt, int Peak, long? ReleasedAt);
}
