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
}

internal sealed class AppStorageCoordinator : IInputEventSink, IRawDeviceIdentityObserver
{
    private readonly IAppStorageWriter _storage;
    private readonly Guid _calibrationSessionId;
    private readonly Dictionary<RuntimeDeviceId, Guid> _devices = [];
    private Guid? _unattributedKeyboard;
    private Guid? _unattributedMouse;

    public AppStorageCoordinator(IAppStorageWriter storage, Guid calibrationSessionId)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
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
            isVirtual));
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
            case CapturedInputKind.AggregationBoundary:
                WriteBoundary(input.Boundary);
                break;
            case CapturedInputKind.DeviceChange:
                if (input.DeviceChange.Change == DeviceChangeKind.Removed)
                {
                    _devices.Remove(input.DeviceChange.Device.RuntimeId);
                }
                break;
            default:
                throw new InvalidDataException("Captured input kind is unsupported.");
        }

        return ValueTask.CompletedTask;
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
}
