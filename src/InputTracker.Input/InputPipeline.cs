using System.Threading.Channels;
using InputTracker.Input.Win32;

namespace InputTracker.Input;

internal enum IngressEventKind : byte
{
    Keyboard,
    Mouse,
    Gamepad,
    GamepadStatus,
    GamepadBarrier,
    DeviceArrived,
    DeviceRemoved,
    Boundary
}

internal readonly record struct InputIngressEvent(
    IngressEventKind Kind,
    nint DeviceHandle,
    long Timestamp,
    ushort MakeCode = 0,
    PhysicalKeyPrefix KeyPrefix = PhysicalKeyPrefix.None,
    PhysicalKeyTransition KeyTransition = PhysicalKeyTransition.Down,
    int DeltaX = 0,
    int DeltaY = 0,
    MouseMotionKind MotionKind = MouseMotionKind.Relative,
    bool UsesVirtualDesktop = false,
    MouseButtonChanges ButtonChanges = MouseButtonChanges.None,
    short VerticalWheelDelta = 0,
    short HorizontalWheelDelta = 0,
    GamepadControlId GamepadControl = default,
    int GamepadValue = 0,
    bool GamepadBaseline = false,
    bool GamepadCaptureEnabled = false,
    int? GamepadLogicalMinimum = null,
    int? GamepadLogicalMaximum = null,
    GamepadCaptureStatus GamepadStatus = GamepadCaptureStatus.Ready,
    string? GamepadError = null,
    RawDeviceKind DeviceKind = RawDeviceKind.Unknown,
    AggregationBoundaryKind BoundaryKind = AggregationBoundaryKind.Paused,
    PauseReason PauseReasons = PauseReason.None,
    TaskCompletionSource<bool>? Barrier = null);

internal readonly record struct SequencedIngressEvent(long Sequence, InputIngressEvent Input);

internal sealed class InputPipelineCounters
{
    private long _produced;
    private long _accepted;
    private long _dequeued;
    private long _sinkAcknowledged;
    private long _overflow;
    private long _parseErrors;
    private long _outOfOrder;

    public void Produced() => Interlocked.Increment(ref _produced);
    public void Accepted() => Interlocked.Increment(ref _accepted);
    public void Dequeued() => Interlocked.Increment(ref _dequeued);
    public void SinkAcknowledged() => Interlocked.Increment(ref _sinkAcknowledged);
    public void Overflow() => Interlocked.Increment(ref _overflow);
    public void ParseError() => Interlocked.Increment(ref _parseErrors);
    public void OutOfOrder() => Interlocked.Increment(ref _outOfOrder);

    public InputPipelineDiagnostics Snapshot(int capacity) => new(
        Volatile.Read(ref _produced),
        Volatile.Read(ref _accepted),
        Volatile.Read(ref _dequeued),
        Volatile.Read(ref _sinkAcknowledged),
        Volatile.Read(ref _overflow),
        Volatile.Read(ref _parseErrors),
        Volatile.Read(ref _outOfOrder),
        capacity);
}

internal sealed class BoundedInputQueue
{
    private readonly Channel<SequencedIngressEvent> _channel;
    private readonly InputPipelineCounters _counters;
    private long _nextSequence;

    public BoundedInputQueue(int capacity, InputPipelineCounters counters)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        Capacity = capacity;
        _counters = counters;
        _channel = Channel.CreateBounded<SequencedIngressEvent>(new BoundedChannelOptions(capacity)
        {
            SingleWriter = true,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        });
    }

    public int Capacity { get; }
    public ChannelReader<SequencedIngressEvent> Reader => _channel.Reader;

    public bool TryWrite(InputIngressEvent input)
    {
        _counters.Produced();
        long sequence = Interlocked.Increment(ref _nextSequence);
        if (_channel.Writer.TryWrite(new SequencedIngressEvent(sequence, input)))
        {
            _counters.Accepted();
            return true;
        }

        _counters.Overflow();
        return false;
    }

    public void Complete(Exception? error = null) => _channel.Writer.TryComplete(error);

    public void FaultPendingBarriers(Exception error)
    {
        _channel.Writer.TryComplete(error);
        while (_channel.Reader.TryRead(out SequencedIngressEvent pending))
        {
            pending.Input.Barrier?.TrySetException(error);
        }
    }
}

internal interface IRawDeviceInfoProvider
{
    RawDeviceMetadata Query(nint deviceHandle, RawDeviceKind hintedKind);
}

internal readonly record struct RawDeviceMetadata(
    RawDeviceKind Kind,
    string? InterfacePath,
    string? HardwareSummary,
    DeviceIdentityConfidence Confidence,
    RawDeviceIdentityObservation? Identity = null);

internal sealed class InputWorker
{
    private readonly BoundedInputQueue _queue;
    private readonly InputPipelineCounters _counters;
    private readonly IInputEventSink _sink;
    private readonly IRawDeviceInfoProvider _deviceInfo;
    private readonly IRawDeviceIdentityObserver _identityObserver;
    private readonly RawDeviceRegistry _devices = new();
    private readonly HashSet<PressedKey> _pressedKeys = [];
    private readonly Func<Exception, ValueTask> _onFault;
    private readonly CancellationToken _cancellationToken;
    private Task? _runTask;
    private long _lastSequence;
    private int _canAcceptBarrier;

    public InputWorker(
        BoundedInputQueue queue,
        InputPipelineCounters counters,
        IInputEventSink sink,
        IRawDeviceInfoProvider deviceInfo,
        Func<Exception, ValueTask> onFault,
        CancellationToken cancellationToken = default,
        IRawDeviceIdentityObserver? identityObserver = null)
    {
        _queue = queue;
        _counters = counters;
        _sink = sink;
        _deviceInfo = deviceInfo;
        _identityObserver = identityObserver ?? NullRawDeviceIdentityObserver.Instance;
        _onFault = onFault;
        _cancellationToken = cancellationToken;
    }

    public Task Start()
    {
        if (_runTask is null)
        {
            Volatile.Write(ref _canAcceptBarrier, 1);
            _runTask = Task.Run(RunAsync, CancellationToken.None);
        }

        return _runTask;
    }

    public bool CanAcceptBarrier => Volatile.Read(ref _canAcceptBarrier) != 0;

    private async Task RunAsync()
    {
        try
        {
            await foreach (SequencedIngressEvent item in _queue.Reader.ReadAllAsync(_cancellationToken))
            {
                _counters.Dequeued();
                if (item.Sequence != _lastSequence + 1)
                {
                    _counters.OutOfOrder();
                }

                _lastSequence = item.Sequence;
                try
                {
                    await ProcessAsync(item.Input).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    item.Input.Barrier?.TrySetException(error);
                    throw;
                }
            }
        }
        catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            Volatile.Write(ref _canAcceptBarrier, 0);
            _queue.FaultPendingBarriers(error);
            await _onFault(error).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _canAcceptBarrier, 0);
        }
    }

    private async ValueTask ProcessAsync(InputIngressEvent input)
    {
        switch (input.Kind)
        {
            case IngressEventKind.DeviceArrived:
            {
                if (input.DeviceHandle == 0)
                {
                    break;
                }

                RawDeviceMetadata metadata = default;
                InputDeviceDescriptor descriptor = _devices.Resolve(input.DeviceHandle, () =>
                {
                    metadata = _deviceInfo.Query(input.DeviceHandle, input.DeviceKind);
                    return metadata;
                }, out bool created);
                if (created)
                {
                    await ObserveIdentityAsync(descriptor, metadata).ConfigureAwait(false);
                    await WriteAsync(CapturedInput.FromDeviceChange(new DeviceChangeEvent(
                        DeviceChangeKind.Arrived,
                        descriptor,
                        input.Timestamp))).ConfigureAwait(false);
                }

                break;
            }
            case IngressEventKind.DeviceRemoved:
            {
                InputDeviceDescriptor descriptor = _devices.Remove(input.DeviceHandle, input.DeviceKind);
                await WriteBoundaryForDeviceAsync(
                    descriptor.RuntimeId,
                    AggregationBoundaryKind.DeviceRemoved,
                    input.Timestamp).ConfigureAwait(false);
                await WriteAsync(CapturedInput.FromDeviceChange(new DeviceChangeEvent(
                    DeviceChangeKind.Removed,
                    descriptor,
                    input.Timestamp))).ConfigureAwait(false);
                break;
            }
            case IngressEventKind.Keyboard:
            {
                InputDeviceDescriptor descriptor = await ResolveAsync(
                    input.DeviceHandle,
                    RawDeviceKind.Keyboard,
                    input.Timestamp).ConfigureAwait(false);
                var pressed = new PressedKey(descriptor.RuntimeId, input.MakeCode, input.KeyPrefix);
                bool isTransition = input.KeyTransition == PhysicalKeyTransition.Down
                    ? _pressedKeys.Add(pressed)
                    : _pressedKeys.Remove(pressed);
                if (isTransition)
                {
                    await WriteAsync(CapturedInput.FromKeyboard(new KeyboardPhysicalEvent(
                        descriptor.RuntimeId,
                        input.MakeCode,
                        input.KeyPrefix,
                        input.KeyTransition,
                        input.Timestamp))).ConfigureAwait(false);
                }

                break;
            }
            case IngressEventKind.Mouse:
            {
                InputDeviceDescriptor descriptor = await ResolveAsync(
                    input.DeviceHandle,
                    RawDeviceKind.Mouse,
                    input.Timestamp).ConfigureAwait(false);
                await WriteAsync(CapturedInput.FromMouse(new MouseRawEvent(
                    descriptor.RuntimeId,
                    input.DeltaX,
                    input.DeltaY,
                    input.MotionKind,
                    input.UsesVirtualDesktop,
                    input.ButtonChanges,
                    input.VerticalWheelDelta,
                    input.HorizontalWheelDelta,
                    input.Timestamp))).ConfigureAwait(false);
                break;
            }
            case IngressEventKind.Gamepad:
            {
                InputDeviceDescriptor descriptor = await ResolveAsync(
                    input.DeviceHandle,
                    RawDeviceKind.Gamepad,
                    input.Timestamp).ConfigureAwait(false);
                await WriteAsync(CapturedInput.FromGamepad(new GamepadControlEvent(
                    descriptor.RuntimeId,
                    input.GamepadControl,
                    input.GamepadValue,
                    input.Timestamp,
                    input.GamepadBaseline,
                    input.GamepadLogicalMinimum,
                    input.GamepadLogicalMaximum))).ConfigureAwait(false);
                break;
            }
            case IngressEventKind.GamepadStatus:
            {
                InputDeviceDescriptor descriptor = await ResolveAsync(
                    input.DeviceHandle,
                    RawDeviceKind.Gamepad,
                    input.Timestamp).ConfigureAwait(false);
                await WriteAsync(CapturedInput.FromGamepadStatus(new GamepadInputStatusEvent(
                    descriptor.RuntimeId,
                    input.GamepadStatus,
                    input.GamepadError,
                    input.Timestamp))).ConfigureAwait(false);
                break;
            }
            case IngressEventKind.GamepadBarrier:
            {
                if (!input.GamepadCaptureEnabled)
                {
                    await RemoveGamepadDevicesAsync(input.Timestamp).ConfigureAwait(false);
                }

                input.Barrier?.TrySetResult(true);
                _counters.SinkAcknowledged();
                break;
            }
            case IngressEventKind.Boundary:
            {
                _pressedKeys.Clear();
                await WriteAsync(CapturedInput.FromBoundary(new AggregationBoundaryEvent(
                    input.BoundaryKind,
                    RuntimeDeviceId.Unattributed,
                    input.PauseReasons,
                    input.Timestamp))).ConfigureAwait(false);
                if (input.BoundaryKind == AggregationBoundaryKind.Paused)
                {
                    await RemoveGamepadDevicesAsync(input.Timestamp).ConfigureAwait(false);
                }

                input.Barrier?.TrySetResult(true);
                break;
            }
            default:
                throw new InvalidOperationException($"Unsupported ingress event kind: {input.Kind}");
        }
    }

    private async ValueTask<InputDeviceDescriptor> ResolveAsync(
        nint handle,
        RawDeviceKind kind,
        long timestamp)
    {
        RawDeviceMetadata metadata = default;
        InputDeviceDescriptor descriptor = _devices.Resolve(handle, () =>
        {
            metadata = _deviceInfo.Query(handle, kind);
            return metadata;
        }, out bool created);
        if (created)
        {
            await ObserveIdentityAsync(descriptor, metadata).ConfigureAwait(false);
            await WriteAsync(CapturedInput.FromDeviceChange(new DeviceChangeEvent(
                DeviceChangeKind.Arrived,
                descriptor,
                timestamp))).ConfigureAwait(false);
        }

        return descriptor;
    }

    private ValueTask ObserveIdentityAsync(InputDeviceDescriptor descriptor, RawDeviceMetadata metadata) =>
        metadata.Identity is { } identity
            ? _identityObserver.ObserveAsync(descriptor.RuntimeId, identity, _cancellationToken)
            : ValueTask.CompletedTask;

    private async ValueTask WriteBoundaryForDeviceAsync(
        RuntimeDeviceId deviceId,
        AggregationBoundaryKind kind,
        long timestamp)
    {
        _pressedKeys.RemoveWhere(key => key.DeviceId == deviceId);
        await WriteAsync(CapturedInput.FromBoundary(new AggregationBoundaryEvent(
            kind,
            deviceId,
            PauseReason.None,
            timestamp))).ConfigureAwait(false);
    }

    private async ValueTask RemoveGamepadDevicesAsync(long timestamp)
    {
        foreach (InputDeviceDescriptor descriptor in _devices.RemoveKind(RawDeviceKind.Gamepad))
        {
            await WriteBoundaryForDeviceAsync(
                descriptor.RuntimeId,
                AggregationBoundaryKind.DeviceRemoved,
                timestamp).ConfigureAwait(false);
            await WriteAsync(CapturedInput.FromDeviceChange(new DeviceChangeEvent(
                DeviceChangeKind.Removed,
                descriptor,
                timestamp))).ConfigureAwait(false);
        }
    }

    private async ValueTask WriteAsync(CapturedInput input)
    {
        await _sink.WriteAsync(input, _cancellationToken).ConfigureAwait(false);
        _counters.SinkAcknowledged();
    }

    private readonly record struct PressedKey(
        RuntimeDeviceId DeviceId,
        ushort MakeCode,
        PhysicalKeyPrefix Prefix);
}
