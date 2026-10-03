using InputTracker.Input;
using InputTracker.Input.Win32;
using InputTracker.Storage;
using System.IO;
using System.Diagnostics;

namespace InputTracker;

internal interface IAppStorageWriter
{
    StorageServiceHealth Health { get; }
    void WriteKeyboard(StorageKeyboardTransition value);
    void WriteMouseButtons(StorageMouseButtonTransition value);
    void Pause(StoragePauseReason reason, long timestamp);
    void Resume(StoragePauseReason reason);
    void RemoveDevice(Guid deviceId, long timestamp);
    void Stop(long timestamp);
    StorageDeviceResolution ObserveDevice(StorageDeviceObservation value);
    IReadOnlyList<StorageDeviceInfo> ListDevices();
    StorageDeviceResolution ObserveGamepad(StorageDeviceObservation value);
    StorageGamepadConfiguration? GetGamepadConfiguration(Guid deviceId);
    StorageGamepadConfiguration SetGamepadConfiguration(Guid deviceId, bool enabled, IReadOnlyList<StorageGamepadHidBinding> bindings);
    void WriteGamepadPress(StorageGamepadPress value);
}

internal sealed class StorageServiceWriter(StorageService service) : IAppStorageWriter
{
    public StorageServiceHealth Health => service.Health;
    public void WriteKeyboard(StorageKeyboardTransition value) => service.WriteKeyboardTransition(value);
    public void WriteMouseButtons(StorageMouseButtonTransition value) => service.WriteMouseButtonTransition(value);
    public void Pause(StoragePauseReason reason, long timestamp) => service.Pause(reason, timestamp);
    public void Resume(StoragePauseReason reason) => service.Resume(reason);
    public void RemoveDevice(Guid deviceId, long timestamp) => service.RemoveDevice(deviceId, timestamp);
    public void Stop(long timestamp) => service.Stop(timestamp);
    public StorageDeviceResolution ObserveDevice(StorageDeviceObservation value) => service.ObserveDevice(value);
    public IReadOnlyList<StorageDeviceInfo> ListDevices() => service.ListDevices();
    public StorageDeviceResolution ObserveGamepad(StorageDeviceObservation value) => service.ObserveDevice(value);
    public StorageGamepadConfiguration? GetGamepadConfiguration(Guid deviceId) => service.GetGamepadConfiguration(deviceId);
    public StorageGamepadConfiguration SetGamepadConfiguration(Guid deviceId, bool enabled, IReadOnlyList<StorageGamepadHidBinding> bindings) =>
        service.SetGamepadConfiguration(deviceId, StorageGamepadLayout.Xbox, enabled, bindings);
    public void WriteGamepadPress(StorageGamepadPress value) => service.WriteGamepadPress(value);
}

internal sealed class AppStorageCoordinator : IInputEventSink, IRawDeviceIdentityObserver
{
    private readonly IAppStorageWriter _storage;
    private readonly Guid _calibrationSessionId;
    private readonly object _gamepadGate = new();
    private readonly Dictionary<RuntimeDeviceId, Guid> _devices = [];
    private readonly Dictionary<RuntimeDeviceId, Guid> _gamepads = [];
    private readonly PortableSettingsStore? _settings;
    private readonly Dictionary<RuntimeDeviceId, InputDeviceDescriptor> _gamepadDescriptors = [];
    private readonly Dictionary<RuntimeDeviceId, string> _gamepadStatuses = [];
    private readonly Dictionary<(RuntimeDeviceId Device, GamepadControlId Control), int> _gamepadValues = [];
    private readonly Dictionary<RuntimeDeviceId, long> _gamepadCutoffs = [];
    private readonly Dictionary<GamepadControlId, int> _calibrationRestingValues = [];
    private readonly Dictionary<(RuntimeDeviceId Device, GamepadControlId Control), GamepadControlEvent> _gamepadLatestEvents = [];
    private readonly Dictionary<GamepadControlId, GamepadControlEvent> _triggerRestingPoints = [];
    private readonly Dictionary<(RuntimeDeviceId Device, StorageGamepadButton Button), bool> _gamepadActive = [];
    private readonly Dictionary<RuntimeDeviceId, StorageGamepadConfiguration> _gamepadConfigurations = [];
    private readonly Dictionary<StorageGamepadButton, (GamepadControlId Control, StorageGamepadHidBinding Binding)> _calibrationBindings = [];
    private StorageGamepadButton? _calibrationTarget;
    private bool _gamepadEnabled;
    private Guid? _enabledGamepadId;
    private RuntimeDeviceId? _calibrationRuntimeId;
    private (StorageGamepadButton Button, StorageGamepadHidBinding Binding)? _calibrationPending;
    private bool _calibrationInterrupted;
    private bool _triggerRestingConfirmed;
    private GamepadAutomaticCalibration? _automaticCalibration;
    private long _automaticCalibrationLastInputAt;

    internal IReadOnlyList<InputDeviceDescriptor> GamepadDevices { get { lock (_gamepadGate) return _gamepadDescriptors.Values.ToArray(); } }
    internal string? GetGamepadStatus(RuntimeDeviceId runtimeId) { lock (_gamepadGate) return _gamepadStatuses.GetValueOrDefault(runtimeId); }
    internal InputDeviceDescriptor? FindGamepad(string? interfacePath) =>
        FindGamepadCore(interfacePath);
    private InputDeviceDescriptor? FindGamepadCore(string? interfacePath) { lock (_gamepadGate) return _gamepadDescriptors.Values.FirstOrDefault(value => value.InterfacePath == interfacePath); }
    internal Guid? GetGamepadStorageId(RuntimeDeviceId runtimeId) { lock (_gamepadGate) return _gamepads.TryGetValue(runtimeId, out Guid id) ? id : null; }
    internal StorageGamepadConfiguration? GetGamepadConfiguration(RuntimeDeviceId runtimeId) { lock (_gamepadGate) return _gamepads.TryGetValue(runtimeId, out Guid id) ? _storage.GetGamepadConfiguration(id) : null; }
    private Guid? _unattributedKeyboard;
    private Guid? _unattributedMouse;

    public AppStorageCoordinator(IAppStorageWriter storage, Guid calibrationSessionId, PortableSettingsStore? settings = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _settings = settings;
        if (calibrationSessionId == Guid.Empty)
        {
            throw new ArgumentException("Calibration session must be non-empty.", nameof(calibrationSessionId));
        }

        _calibrationSessionId = calibrationSessionId;
    }

    public void EnsureReady()
    {
        StorageServiceHealth health = _storage.Health;
        if (health.State != StorageServiceState.Running || !health.AcceptingWrites)
        {
            throw new InvalidOperationException("Storage must be running and accepting writes before Raw Input starts.");
        }
    }

    public void MapRuntimeDevice(RuntimeDeviceId runtimeId, Guid persistentDeviceId)
    {
        if (runtimeId == RuntimeDeviceId.Unattributed || persistentDeviceId == Guid.Empty)
        {
            throw new ArgumentException("Runtime and persistent device identifiers must be attributed.");
        }

        _devices[runtimeId] = persistentDeviceId;
    }

    public void Resume(PauseReason reason) => _storage.Resume(ToStorageReasons(reason));

    public void AddStackedPauseReason(PauseReason reason, long timestamp) =>
        _storage.Pause(ToStorageReasons(reason), timestamp);

    public void RollBackResume(PauseReason reason, long timestamp) =>
        _storage.Pause(ToStorageReasons(reason), timestamp);

    public ValueTask ObserveAsync(
        RuntimeDeviceId runtimeId,
        RawDeviceIdentityObservation identity,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (identity.Kind == RawDeviceKind.Gamepad)
        {
            ObserveGamepadIdentity(runtimeId, new StorageDeviceObservation(
                StorageDeviceKind.Gamepad, identity.InterfacePath, identity.HardwareIds,
                identity.SerialNumber, identity.ContainerId,
                identity.Transport == DeviceTransportAssessment.Bluetooth,
                identity.Transport == DeviceTransportAssessment.RemoteDesktop,
                identity.Transport == DeviceTransportAssessment.KnownVirtual,
                identity.Transport == DeviceTransportAssessment.Indeterminate));
            return ValueTask.CompletedTask;
        }
        if (runtimeId == RuntimeDeviceId.Unattributed
            || identity.Kind is not RawDeviceKind.Keyboard and not RawDeviceKind.Mouse)
        {
            throw new InvalidDataException("An attributable keyboard or mouse identity is required.");
        }

        ObserveIdentity(
            runtimeId,
            identity.Kind,
            identity.InterfacePath,
            identity.HardwareIds,
            identity.SerialNumber,
            identity.ContainerId,
            identity.Transport == DeviceTransportAssessment.Bluetooth,
            identity.Transport == DeviceTransportAssessment.RemoteDesktop,
            identity.Transport == DeviceTransportAssessment.KnownVirtual,
            identity.Transport == DeviceTransportAssessment.Indeterminate);
        return ValueTask.CompletedTask;
    }

    internal void ObserveGamepadIdentity(RuntimeDeviceId runtimeId, StorageDeviceObservation identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.Kind != StorageDeviceKind.Gamepad || runtimeId == RuntimeDeviceId.Unattributed)
            throw new InvalidDataException("An attributable gamepad identity is required.");
        if (_settings?.Load().GamepadConsentGranted != true) return;
        lock (_gamepadGate)
        {
            StorageDeviceResolution gamepad = _storage.ObserveGamepad(identity);
            _gamepads[runtimeId] = gamepad.Device.DeviceId;
        }
    }

    internal void ObserveIdentity(
        RuntimeDeviceId runtimeId,
        RawDeviceKind kind,
        string interfacePath,
        IReadOnlyList<string> hardwareIds,
        string? serialNumber,
        Guid? containerId,
        bool isBluetooth,
        bool isRemoteDesktop,
        bool isVirtual,
        bool isIndeterminate)
    {
        if (runtimeId == RuntimeDeviceId.Unattributed
            || kind is not RawDeviceKind.Keyboard and not RawDeviceKind.Mouse)
        {
            throw new InvalidDataException("An attributable keyboard or mouse identity is required.");
        }

        StorageDeviceResolution resolution = _storage.ObserveDevice(new StorageDeviceObservation(
            kind == RawDeviceKind.Keyboard ? StorageDeviceKind.Keyboard : StorageDeviceKind.Mouse,
            interfacePath,
            hardwareIds,
            isIndeterminate ? null : serialNumber,
            containerId,
            isBluetooth,
            isRemoteDesktop,
            isVirtual,
            isIndeterminate));
        MapRuntimeDevice(runtimeId, resolution.Device.DeviceId);
    }

    public ValueTask WriteAsync(CapturedInput input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (input.Kind)
        {
            case CapturedInputKind.Keyboard:
            {
                KeyboardPhysicalEvent value = input.Keyboard;
                _storage.WriteKeyboard(new StorageKeyboardTransition(
                    Resolve(value.DeviceId, StorageDeviceKind.Keyboard), value.MakeCode,
                    value.Prefix == PhysicalKeyPrefix.E0,
                    value.Prefix == PhysicalKeyPrefix.E1,
                    value.Transition == PhysicalKeyTransition.Down,
                    value.Timestamp, _calibrationSessionId));
                break;
            }
            case CapturedInputKind.Mouse:
            {
                MouseRawEvent value = input.Mouse;
                const MouseButtonChanges downChanges = MouseButtonChanges.LeftDown
                    | MouseButtonChanges.RightDown
                    | MouseButtonChanges.MiddleDown
                    | MouseButtonChanges.X1Down
                    | MouseButtonChanges.X2Down;
                MouseButtonChanges pressed = value.ButtonChanges & downChanges;
                if (pressed != MouseButtonChanges.None)
                {
                    _storage.WriteMouseButtons(new StorageMouseButtonTransition(
                        Resolve(value.DeviceId, StorageDeviceKind.Mouse),
                        _calibrationSessionId,
                        value.Timestamp,
                        (ushort)pressed));
                }
                break;
            }
            case CapturedInputKind.DeviceChange:
                lock (_gamepadGate)
                {
                if (input.DeviceChange.Device.Kind == RawDeviceKind.Gamepad)
                {
                    if (input.DeviceChange.Change == DeviceChangeKind.Arrived)
                        _gamepadDescriptors[input.DeviceChange.Device.RuntimeId] = input.DeviceChange.Device;
                    if (input.DeviceChange.Change == DeviceChangeKind.Arrived)
                        _gamepadStatuses[input.DeviceChange.Device.RuntimeId] = "已连接";
                    else
                    {
                        RuntimeDeviceId removed = input.DeviceChange.Device.RuntimeId;
                        _gamepadDescriptors.Remove(removed);
                        _gamepadStatuses.Remove(removed);
                        _gamepads.Remove(removed);
                        ClearGamepadState(removed);
                        if (_calibrationRuntimeId == removed) CancelGamepadCalibration(interrupted: true);
                    }
                }
                else if (input.DeviceChange.Change == DeviceChangeKind.Removed)
                {
                    _devices.Remove(input.DeviceChange.Device.RuntimeId);
                }
                }
                break;
            case CapturedInputKind.GamepadStatus:
                lock (_gamepadGate)
                {
                if (_gamepadDescriptors.ContainsKey(input.GamepadStatus.DeviceId))
                {
                    _gamepadStatuses[input.GamepadStatus.DeviceId] = input.GamepadStatus.Status switch
                    {
                        GamepadCaptureStatus.Unsupported => "不支持此设备",
                        GamepadCaptureStatus.Faulted => $"设备故障：{input.GamepadStatus.Error ?? "未知错误"}",
                        _ => "已连接"
                    };
                    ClearGamepadState(input.GamepadStatus.DeviceId);
                    if (input.GamepadStatus.Status is GamepadCaptureStatus.Unsupported or GamepadCaptureStatus.Faulted
                        && _calibrationRuntimeId == input.GamepadStatus.DeviceId)
                        CancelGamepadCalibration(interrupted: true);
                }
                }
                break;
            case CapturedInputKind.Gamepad:
                lock (_gamepadGate) ProcessGamepadInput(input.Gamepad);
                break;
            case CapturedInputKind.AggregationBoundary:
                if (input.Boundary.Boundary is AggregationBoundaryKind.Paused or AggregationBoundaryKind.Stopping)
                {
                    lock (_gamepadGate)
                    {
                        if (input.Boundary.DeviceId == RuntimeDeviceId.Unattributed)
                        {
                            foreach (RuntimeDeviceId deviceId in _gamepadDescriptors.Keys.ToArray())
                            {
                                _gamepadCutoffs[deviceId] = input.Boundary.Timestamp;
                                ClearGamepadState(deviceId);
                            }
                            if (_calibrationRuntimeId.HasValue) CancelGamepadCalibration(interrupted: true);
                        }
                        else
                        {
                            _gamepadCutoffs[input.Boundary.DeviceId] = input.Boundary.Timestamp;
                            ClearGamepadState(input.Boundary.DeviceId);
                            if (_calibrationRuntimeId == input.Boundary.DeviceId) CancelGamepadCalibration(interrupted: true);
                        }
                    }
                }
                WriteBoundary(input.Boundary);
                break;
            default:
                throw new InvalidDataException("Captured input kind is unsupported.");
        }

        return ValueTask.CompletedTask;
    }

    internal void SetGamepadCaptureEnabled(bool enabled)
    {
        lock (_gamepadGate)
        {
            _gamepadEnabled = enabled;
            if (!enabled && _calibrationRuntimeId.HasValue) CancelGamepadCalibration(interrupted: true);
        }
    }
    internal void InitializeGamepadCapture(bool enabled)
    {
        lock (_gamepadGate)
        {
            PortableSettings settings = _settings?.Load() ?? new PortableSettings(2, false);
            _enabledGamepadId = enabled && settings.GamepadConsentGranted && settings.GamepadCaptureEnabled
                ? settings.GamepadDeviceId : null;
            _gamepadEnabled = enabled;
        }
    }
    internal bool HasEnabledGamepad { get { lock (_gamepadGate) return _enabledGamepadId.HasValue; } }
    internal bool IsGamepadEnabled(RuntimeDeviceId runtimeId)
    {
        lock (_gamepadGate)
            return _enabledGamepadId.HasValue && _gamepads.TryGetValue(runtimeId, out Guid id)
                && _enabledGamepadId == id && _storage.GetGamepadConfiguration(id)?.Enabled == true;
    }

    internal void BeginGamepadCalibration(RuntimeDeviceId runtimeId, StorageGamepadButton button)
    {
        lock (_gamepadGate)
        {
        if (!_gamepadDescriptors.ContainsKey(runtimeId) || !_gamepads.ContainsKey(runtimeId))
            throw new InvalidOperationException("请先选择已检测到的手柄。");
        _automaticCalibration = null;
        _calibrationBindings.Clear();
        _calibrationPending = null;
        _calibrationRestingValues.Clear();
        _calibrationInterrupted = false;
        foreach (var pair in _gamepadValues.Where(pair => pair.Key.Device == runtimeId))
            _calibrationRestingValues[pair.Key.Control] = pair.Value;
        _calibrationTarget = button;
        _calibrationRuntimeId = runtimeId;
        _triggerRestingPoints.Clear();
        _triggerRestingConfirmed = button is not StorageGamepadButton.LeftTrigger and not StorageGamepadButton.RightTrigger;
        _gamepadConfigurations.Remove(runtimeId);
        ClearGamepadState(runtimeId);
        }
    }

    internal void BeginGamepadAutomaticCalibration(RuntimeDeviceId runtimeId, StorageGamepadButton button = StorageGamepadButton.South)
    {
        lock (_gamepadGate)
        {
            GamepadControlEvent[] baseline = _gamepadLatestEvents.Values.Where(value => value.DeviceId == runtimeId).ToArray();
            BeginGamepadCalibration(runtimeId, button);
            _automaticCalibration = new GamepadAutomaticCalibration(baseline);
            _automaticCalibration.BeginTarget(button, Stopwatch.GetTimestamp());
        }
    }

    internal bool IsGamepadAutomaticCalibration { get { lock (_gamepadGate) return _automaticCalibration is not null; } }
    internal string? GamepadAutomaticCalibrationFeedback { get { lock (_gamepadGate) return _automaticCalibration?.Feedback; } }

    internal void UpdateGamepadAutomaticCalibration(long? timestamp = null)
    {
        lock (_gamepadGate)
        {
            if (_automaticCalibration is null || _calibrationTarget is not StorageGamepadButton target) return;
            long now = timestamp ?? Stopwatch.GetTimestamp();
            if (!timestamp.HasValue && now - _automaticCalibrationLastInputAt < Math.Max(1, Stopwatch.Frequency / 50)) return;
            StorageGamepadHidBinding? binding = _automaticCalibration.TryComplete(now);
            if (binding is null) return;
            if (_calibrationBindings.Values.Any(existing => existing.Binding.UsagePage == binding.UsagePage
                && existing.Binding.Usage == binding.Usage && existing.Binding.LinkCollection == binding.LinkCollection
                && existing.Binding.ElementIndex == binding.ElementIndex
                && (binding.AxisDirection.HasValue || existing.Binding.ActiveValues.SequenceEqual(binding.ActiveValues))))
            {
                _automaticCalibration.RetryDuplicate(target, now);
                return;
            }
            _calibrationBindings[target] = (default, binding);
            _calibrationTarget = NextUncalibrated(target);
            if (_calibrationTarget is StorageGamepadButton next) _automaticCalibration.BeginTarget(next, now);
        }
    }

    internal void CancelGamepadCalibration()
    {
        lock (_gamepadGate) CancelGamepadCalibration(interrupted: false);
    }

    internal void SwitchGamepadCalibrationToManual()
    {
        lock (_gamepadGate)
        {
            if (_automaticCalibration is null || !_calibrationRuntimeId.HasValue || !_calibrationTarget.HasValue) return;
            _calibrationRestingValues.Clear();
            foreach (GamepadControlEvent value in _automaticCalibration.LatestValues)
            {
                _calibrationRestingValues[value.Control] = value.Value;
                _gamepadLatestEvents[(value.DeviceId, value.Control)] = value;
            }
            _automaticCalibration = null;
            _calibrationPending = null;
            _triggerRestingPoints.Clear();
            _triggerRestingConfirmed = _calibrationTarget is not StorageGamepadButton.LeftTrigger and not StorageGamepadButton.RightTrigger;
        }
    }

    internal IReadOnlyDictionary<StorageGamepadButton, StorageGamepadHidBinding> GamepadCalibrationBindings =>
        GetCalibrationBindings();

    private IReadOnlyDictionary<StorageGamepadButton, StorageGamepadHidBinding> GetCalibrationBindings()
    { lock (_gamepadGate) return _calibrationBindings.ToDictionary(pair => pair.Key, pair => pair.Value.Binding); }

    internal bool IsGamepadCalibrating { get { lock (_gamepadGate) return _calibrationTarget.HasValue || _calibrationRuntimeId.HasValue; } }
    internal bool GamepadCalibrationInterrupted { get { lock (_gamepadGate) return _calibrationInterrupted; } }
    internal bool HasGamepadCalibrationCandidate { get { lock (_gamepadGate) return _calibrationPending.HasValue; } }
    internal StorageGamepadHidBinding? CurrentGamepadCalibrationCandidate
    {
        get { lock (_gamepadGate) return _calibrationPending?.Binding; }
    }
    internal void AcceptGamepadCalibrationCandidate()
    {
        lock (_gamepadGate)
        {
            if (_calibrationPending is not { } pending) return;
            _calibrationBindings[pending.Button] = (default, pending.Binding);
            _calibrationTarget = NextUncalibrated(pending.Button);
            _triggerRestingPoints.Clear();
            _triggerRestingConfirmed = _calibrationTarget is not StorageGamepadButton.LeftTrigger and not StorageGamepadButton.RightTrigger;
            _calibrationPending = null;
        }
    }

    internal void RetryGamepadCalibrationTarget()
    {
        lock (_gamepadGate)
        {
            _calibrationPending = null;
            if (_calibrationTarget is StorageGamepadButton target) _automaticCalibration?.BeginTarget(target, Stopwatch.GetTimestamp());
        }
    }

    internal void SkipGamepadCalibrationTarget()
    {
        lock (_gamepadGate)
        {
        if (_calibrationTarget is not StorageGamepadButton current) return;
        _calibrationPending = null;
        _calibrationTarget = NextUncalibrated(current);
        if (_calibrationTarget is StorageGamepadButton next) _automaticCalibration?.BeginTarget(next, Stopwatch.GetTimestamp());
        }
    }

    internal void SaveGamepadCalibration(RuntimeDeviceId runtimeId)
    {
        lock (_gamepadGate)
        {
        if (_calibrationRuntimeId != runtimeId)
            throw new InvalidOperationException("校准设备已变化，请重新开始校准。");
        if (_calibrationBindings.Count == 0 || !_gamepads.TryGetValue(runtimeId, out Guid id))
            throw new InvalidOperationException("尚未校准任何按键。");
        StorageGamepadConfiguration configuration = _storage.SetGamepadConfiguration(
            id, false, _calibrationBindings.Values.Select(item => item.Binding).ToArray());
        _gamepadCutoffs[runtimeId] = Stopwatch.GetTimestamp();
        _gamepadConfigurations[runtimeId] = configuration;
        _calibrationTarget = null;
        _calibrationRuntimeId = null;
        _calibrationPending = null;
        _calibrationRestingValues.Clear();
        _triggerRestingPoints.Clear();
        _triggerRestingConfirmed = false;
        _automaticCalibration = null;
        }
    }

    internal void SetGamepadEnabled(RuntimeDeviceId runtimeId, bool enabled)
    {
        lock (_gamepadGate)
        {
        if (!_gamepads.TryGetValue(runtimeId, out Guid id))
            throw new InvalidOperationException("所选手柄尚未完成身份识别。");
        if (enabled && _enabledGamepadId is Guid enabledId && enabledId != id)
            throw new InvalidOperationException("请先停用当前手柄，再启用另一只手柄。");
        StorageGamepadConfiguration? configuration = _storage.GetGamepadConfiguration(id);
        if (configuration is null || configuration.Bindings.Count == 0)
            throw new InvalidOperationException("请先逐键校准并保存手柄布局。");
        _storage.SetGamepadConfiguration(id, enabled, configuration.Bindings);
        try
        {
            if (enabled || _enabledGamepadId == id || _enabledGamepadId is null)
                _settings?.SetGamepadConfiguration(enabled, enabled ? id : null);
        }
        catch
        {
            _storage.SetGamepadConfiguration(id, configuration.Enabled, configuration.Bindings);
            throw;
        }
        long cutoff = Stopwatch.GetTimestamp();
        if (enabled) _enabledGamepadId = id;
        else if (_enabledGamepadId == id) _enabledGamepadId = null;
        _gamepadCutoffs[runtimeId] = cutoff;
        _gamepadConfigurations.Remove(runtimeId);
        _gamepadEnabled = _enabledGamepadId.HasValue;
        ClearGamepadState(runtimeId);
        }
    }

    internal StorageGamepadButton? CurrentCalibrationTarget { get { lock (_gamepadGate) return _calibrationTarget; } }

    internal bool IsGamepadTriggerAwaitingRestingConfirmation
    {
        get
        {
            lock (_gamepadGate) return (_calibrationTarget is StorageGamepadButton.LeftTrigger or StorageGamepadButton.RightTrigger)
                && (_automaticCalibration?.AwaitingRestingConfirmation ?? !_triggerRestingConfirmed);
        }
    }

    internal bool ConfirmGamepadTriggerRestingPoint()
    {
        lock (_gamepadGate)
        {
            if (_calibrationRuntimeId is not RuntimeDeviceId runtimeId
                || _calibrationTarget is not StorageGamepadButton.LeftTrigger and not StorageGamepadButton.RightTrigger)
                return false;
            if (_automaticCalibration is not null) return _automaticCalibration.ConfirmRestingPoint();
            _triggerRestingPoints.Clear();
            foreach (GamepadControlEvent value in _gamepadLatestEvents.Values.Where(value =>
                value.DeviceId == runtimeId && value.Control.UsagePage == 0x01
                && value.LogicalMinimum.HasValue && value.LogicalMaximum.HasValue
                && value.LogicalMinimum != value.LogicalMaximum))
                _triggerRestingPoints[value.Control] = value;
            _triggerRestingConfirmed = _triggerRestingPoints.Count > 0;
            return _triggerRestingConfirmed;
        }
    }

    private void ProcessGamepadInput(GamepadControlEvent value)
    {
        if (!_gamepadDescriptors.ContainsKey(value.DeviceId)) return;
        if (_gamepadCutoffs.TryGetValue(value.DeviceId, out long cutoff) && value.Timestamp <= cutoff) return;
        bool hasPreviousValue = _gamepadValues.TryGetValue((value.DeviceId, value.Control), out int previousValue);
        _gamepadValues[(value.DeviceId, value.Control)] = value.Value;
        _gamepadLatestEvents[(value.DeviceId, value.Control)] = value;
        if (_calibrationRuntimeId is RuntimeDeviceId calibrationDevice && value.DeviceId != calibrationDevice) return;
        if (_calibrationTarget is StorageGamepadButton target)
        {
            if (_automaticCalibration is not null)
            {
                if ((!hasPreviousValue || previousValue != value.Value) && _automaticCalibration.AffectsSettling(value))
                    _automaticCalibrationLastInputAt = Stopwatch.GetTimestamp();
                _automaticCalibration.Observe(value);
                return;
            }
            if (value.IsBaseline || _calibrationPending.HasValue) return;
            if (target is StorageGamepadButton.LeftTrigger or StorageGamepadButton.RightTrigger)
            {
                if (!value.LogicalMinimum.HasValue || !value.LogicalMaximum.HasValue
                    || value.LogicalMinimum == value.LogicalMaximum
                    || value.Control.UsagePage != 0x01
                    || !_triggerRestingConfirmed
                    || !_triggerRestingPoints.TryGetValue(value.Control, out GamepadControlEvent restingPoint)) return;
                int min = value.LogicalMinimum.Value;
                int max = value.LogicalMaximum.Value;
                int resting = restingPoint.Value;
                if (max <= min || resting < min || resting > max || value.Value < min || value.Value > max) return;
                long delta = (long)value.Value - resting;
                if (delta == 0) return;
                bool increasing = delta > 0;
                long travel = increasing ? (long)max - resting : (long)resting - min;
                if (travel <= 0 || Math.Abs(delta) < Math.Ceiling(travel * .55)) return;
                int activation = (int)(resting + Math.Round(delta * .55));
                int release = (int)(resting + Math.Round(delta * .30));
                var binding = new StorageGamepadHidBinding(target, value.Control.UsagePage,
                    value.Control.Usage, value.Control.LinkCollection, value.Control.ElementIndex,
                    [], activation, release, increasing ? StorageGamepadAxisDirection.Increasing : StorageGamepadAxisDirection.Decreasing);
                _calibrationPending = (target, binding);
            }
            else if (value.Control.UsagePage == 0x09
                || value.Control.UsagePage == 0x01 && value.Control.Usage == 0x39)
            {
                bool hat = value.Control.UsagePage == 0x01 && value.Control.Usage == 0x39;
                bool hasPrevious = _calibrationRestingValues.TryGetValue(value.Control, out int restingValue);
                if (hat ? value.Value == -1 || hasPrevious && value.Value == restingValue
                    : value.Value == 0 || value.Value == previousValue) return;
                IReadOnlyList<int> activeValues = GetButtonActiveValues(target, value);
                var binding = new StorageGamepadHidBinding(target, value.Control.UsagePage,
                    value.Control.Usage, value.Control.LinkCollection, value.Control.ElementIndex, activeValues);
                _calibrationPending = (target, binding);
            }
            return;
        }

        if (_calibrationRuntimeId.HasValue) return;

        if (!_gamepadEnabled || !_gamepads.TryGetValue(value.DeviceId, out Guid deviceId)
            || _enabledGamepadId != deviceId) return;
        StorageGamepadConfiguration? configuration = _gamepadConfigurations.GetValueOrDefault(value.DeviceId)
            ?? _storage.GetGamepadConfiguration(deviceId);
        if (configuration is null || !configuration.Enabled) return;
        _gamepadConfigurations[value.DeviceId] = configuration;
        foreach (StorageGamepadHidBinding binding in configuration.Bindings.Where(binding =>
            binding.UsagePage == value.Control.UsagePage && binding.Usage == value.Control.Usage
            && binding.LinkCollection == value.Control.LinkCollection && binding.ElementIndex == value.Control.ElementIndex))
        {
            var key = (value.DeviceId, binding.Button);
            bool wasActive = _gamepadActive.GetValueOrDefault(key);
            bool isActive = binding.AxisDirection is null
                ? binding.ActiveValues.Contains(value.Value)
                : binding.AxisDirection == StorageGamepadAxisDirection.Increasing
                    ? wasActive ? value.Value > binding.ReleaseThreshold : value.Value >= binding.ActivationThreshold
                    : wasActive ? value.Value < binding.ReleaseThreshold : value.Value <= binding.ActivationThreshold;
            _gamepadActive[key] = isActive;
            if (!value.IsBaseline && !wasActive && isActive)
                _storage.WriteGamepadPress(new StorageGamepadPress(deviceId, binding.Button, value.Timestamp, _calibrationSessionId));
        }
    }

    private static IReadOnlyList<int> GetButtonActiveValues(StorageGamepadButton button, GamepadControlEvent value)
    {
        if (button is not (StorageGamepadButton.DpadUp or StorageGamepadButton.DpadDown
            or StorageGamepadButton.DpadLeft or StorageGamepadButton.DpadRight)
            || !value.LogicalMinimum.HasValue || !value.LogicalMaximum.HasValue) return [value.Value];
        int count = value.LogicalMaximum.Value - value.LogicalMinimum.Value + 1;
        if (count is not 4 and not 8) return [value.Value];
        int direction = (value.Value - value.LogicalMinimum.Value) % count;
        int[] matching = count == 8 ? [direction, (direction + count - 1) % count, (direction + 1) % count] : [direction];
        return matching.Select(index => value.LogicalMinimum.Value + index).Distinct().Order().ToArray();
    }

    private StorageGamepadButton? NextUncalibrated(StorageGamepadButton current)
    {
        StorageGamepadButton[] buttons = Enum.GetValues<StorageGamepadButton>();
        int index = Array.IndexOf(buttons, current);
        foreach (StorageGamepadButton button in buttons.Skip(index + 1))
            if (!_calibrationBindings.ContainsKey(button)) return button;
        return null;
    }

    private void ClearGamepadState(RuntimeDeviceId deviceId)
    {
        foreach (var key in _gamepadActive.Keys.Where(key => key.Device == deviceId).ToArray()) _gamepadActive.Remove(key);
        foreach (var key in _gamepadValues.Keys.Where(key => key.Device == deviceId).ToArray()) _gamepadValues.Remove(key);
        foreach (var key in _gamepadLatestEvents.Keys.Where(key => key.Device == deviceId).ToArray()) _gamepadLatestEvents.Remove(key);
    }

    private void CancelGamepadCalibration(bool interrupted)
    {
        _calibrationBindings.Clear();
        _calibrationPending = null;
        _calibrationTarget = null;
        _calibrationRuntimeId = null;
        _calibrationRestingValues.Clear();
        _triggerRestingPoints.Clear();
        _triggerRestingConfirmed = false;
        _calibrationInterrupted = interrupted;
        _automaticCalibration = null;
    }

    private Guid Resolve(RuntimeDeviceId runtimeId, StorageDeviceKind kind)
    {
        if (runtimeId == RuntimeDeviceId.Unattributed)
        {
            Guid? existing = kind == StorageDeviceKind.Keyboard
                ? _unattributedKeyboard
                : _unattributedMouse;
            if (existing is Guid value) return value;

            StorageDeviceResolution resolution = _storage.ObserveDevice(new StorageDeviceObservation(
                kind,
                $"inputtracker:session:{_calibrationSessionId:N}:unattributed:{kind}",
                [], null, null, false, false, false));
            if (resolution.Device.IdentityConfidence != StorageDeviceIdentityConfidence.TransientInterface)
            {
                throw new InvalidDataException("An unattributed device was not mapped as transient.");
            }

            if (kind == StorageDeviceKind.Keyboard) _unattributedKeyboard = resolution.Device.DeviceId;
            else _unattributedMouse = resolution.Device.DeviceId;
            return resolution.Device.DeviceId;
        }

        return _devices.TryGetValue(runtimeId, out Guid mapped)
            ? mapped
            : throw new InvalidDataException("Input arrived before its private device identity was mapped.");
    }

    private void WriteBoundary(AggregationBoundaryEvent value)
    {
        switch (value.Boundary)
        {
            case AggregationBoundaryKind.Paused:
                _storage.Pause(ToStorageReasons(value.PauseReasons), value.Timestamp);
                break;
            case AggregationBoundaryKind.DeviceRemoved:
                if (_devices.TryGetValue(value.DeviceId, out Guid mapped))
                {
                    _storage.RemoveDevice(mapped, value.Timestamp);
                    _devices.Remove(value.DeviceId);
                }
                break;
            case AggregationBoundaryKind.Stopping:
                _storage.Stop(value.Timestamp);
                break;
            default:
                throw new InvalidDataException("Aggregation boundary is unsupported.");
        }
    }

    private static StoragePauseReason ToStorageReasons(PauseReason value)
    {
        StoragePauseReason result = StoragePauseReason.None;
        if ((value & PauseReason.User) != 0) result |= StoragePauseReason.User;
        if ((value & PauseReason.SessionLocked) != 0) result |= StoragePauseReason.SessionLock;
        if ((value & PauseReason.SystemSuspend) != 0) result |= StoragePauseReason.Suspend;
        if (result == StoragePauseReason.None) throw new InvalidDataException("Pause boundary has no reason.");
        return result;
    }

}

internal sealed class PersistingCaptureControl(
    IInputCaptureControl capture,
    AppStorageCoordinator storage) : IInputCaptureControl
{
    public InputCaptureStatus CurrentStatus => capture.CurrentStatus;
    public DiagnosticsSnapshot Diagnostics => capture.Diagnostics;
    public event EventHandler<InputCaptureStatus>? StatusChanged
    {
        add => capture.StatusChanged += value;
        remove => capture.StatusChanged -= value;
    }

    public async ValueTask SetPauseReasonAsync(
        PauseReason reason,
        bool active,
        CancellationToken cancellationToken = default)
    {
        if (active)
        {
            bool alreadyPaused = capture.CurrentStatus.PauseReasons != PauseReason.None;
            await capture.SetPauseReasonAsync(reason, true, cancellationToken);
            if (alreadyPaused)
            {
                storage.AddStackedPauseReason(reason, Stopwatch.GetTimestamp());
            }

            return;
        }

        if ((capture.CurrentStatus.PauseReasons & reason) == 0) return;
        storage.Resume(reason);
        try
        {
            await capture.SetPauseReasonAsync(reason, false, cancellationToken);
        }
        catch
        {
            storage.RollBackResume(reason, Stopwatch.GetTimestamp());
            throw;
        }
    }

    public async ValueTask SetGamepadCaptureEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        await capture.SetGamepadCaptureEnabledAsync(enabled, cancellationToken).ConfigureAwait(false);
        storage.SetGamepadCaptureEnabled(enabled);
    }
}
