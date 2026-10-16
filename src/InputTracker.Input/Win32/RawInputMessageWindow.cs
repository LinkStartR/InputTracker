using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace InputTracker.Input.Win32;

internal sealed class RawInputMessageWindow : IRawInputHost
{
    private const int InitialRawBufferSize = 256;

    private readonly BoundedInputQueue _queue;
    private readonly InputPipelineCounters _counters;
    private readonly Action<string> _onIntegrityFault;
    private readonly Action<bool> _onRegistrationChanged;
    private readonly ConcurrentQueue<ControlRequest> _controls = new();
    private readonly Dictionary<nint, GamepadHidParser?> _gamepadParsers = [];
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _destroyed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private RawInputInterop.WindowProcedure? _procedure;
    private string? _className;
    private nint _instance;
    private nint _window;
    private nint _rawBuffer;
    private int _ingressOpen;
    private int _registered;
    private int _gamepadRegistered;
    private bool _gamepadCaptureEnabled;
    private int _rawBufferSize;
    private int _faulted;

    public RawInputMessageWindow(
        BoundedInputQueue queue,
        InputPipelineCounters counters,
        Action<string> onIntegrityFault,
        Action<bool> onRegistrationChanged)
    {
        _queue = queue;
        _counters = counters;
        _onIntegrityFault = onIntegrityFault;
        _onRegistrationChanged = onRegistrationChanged;
        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "InputTracker Raw Input"
        };
    }

    public bool IngressOpen => Volatile.Read(ref _ingressOpen) != 0;

    public async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        _thread.Start();
        await _ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void CloseIngress() => Volatile.Write(ref _ingressOpen, 0);

    public async ValueTask PauseAsync(PauseReason reasons, CancellationToken cancellationToken)
    {
        var barrier = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new ControlRequest(ControlKind.Pause, reasons, barrier);
        await SendAsync(request, cancellationToken).ConfigureAwait(false);
        await barrier.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask ResumeAsync(CancellationToken cancellationToken) =>
        SendAsync(new ControlRequest(ControlKind.Resume), cancellationToken);

    public async ValueTask SetGamepadCaptureEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        var barrier = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new ControlRequest(ControlKind.SetGamepadCapture, PauseReason.None, barrier, enabled);
        await SendAsync(request, cancellationToken).ConfigureAwait(false);
        await barrier.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask PrepareStopAsync(bool enqueueBarrier, CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool>? barrier = enqueueBarrier
            ? new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
            : null;
        var request = new ControlRequest(ControlKind.PrepareStop, PauseReason.None, barrier);
        await SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (barrier is not null)
        {
            await barrier.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DestroyAsync(CancellationToken cancellationToken)
    {
        if (!_destroyed.Task.IsCompleted)
        {
            await SendAsync(new ControlRequest(ControlKind.Destroy), cancellationToken).ConfigureAwait(false);
            await _destroyed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask StopAfterFaultAsync(CancellationToken cancellationToken)
    {
        CloseIngress();
        return SendAsync(new ControlRequest(ControlKind.StopAfterFault), cancellationToken);
    }

    private async ValueTask SendAsync(ControlRequest request, CancellationToken cancellationToken)
    {
        nint window = Volatile.Read(ref _window);
        if (window == 0)
        {
            throw new InvalidOperationException("Raw Input window is not available.");
        }

        _controls.Enqueue(request);
        if (!RawInputInterop.PostMessageW(window, RawInputInterop.WmAppControl, 0, 0))
        {
            request.Completion.TrySetException(new Win32Exception(Marshal.GetLastWin32Error(), "PostMessageW failed."));
        }

        await request.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ThreadMain()
    {
        try
        {
            _instance = RawInputInterop.GetModuleHandleW(null);
            _className = $"InputTracker.RawInput.{Guid.NewGuid():N}";
            _procedure = WindowProcedure;
            var windowClass = new RawInputInterop.WindowClass
            {
                Size = (uint)Marshal.SizeOf<RawInputInterop.WindowClass>(),
                Instance = _instance,
                WindowProcedure = Marshal.GetFunctionPointerForDelegate(_procedure),
                ClassName = _className
            };
            if (RawInputInterop.RegisterClassExW(ref windowClass) == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterClassExW failed.");
            }

            _rawBufferSize = InitialRawBufferSize;
            _rawBuffer = Marshal.AllocHGlobal(_rawBufferSize);
            _window = RawInputInterop.CreateWindowExW(
                0,
                _className,
                "InputTracker Raw Input",
                0,
                0,
                0,
                0,
                0,
                RawInputInterop.HwndMessage,
                0,
                _instance,
                0);
            if (_window == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowExW failed.");
            }

            Register();
            Volatile.Write(ref _ingressOpen, 1);
            _ready.TrySetResult(true);

            while (true)
            {
                int result = RawInputInterop.GetMessageW(out RawInputInterop.NativeWindowMessage message, 0, 0, 0);
                if (result == 0)
                {
                    break;
                }

                if (result == -1)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "GetMessageW failed.");
                }

                RawInputInterop.TranslateMessage(ref message);
                RawInputInterop.DispatchMessageW(ref message);
            }
        }
        catch (Exception error)
        {
            _ready.TrySetException(error);
            if (_ready.Task.IsCompletedSuccessfully)
            {
                FailIntegrity($"Raw Input message thread failed: {error.Message}");
            }
        }
        finally
        {
            Volatile.Write(ref _ingressOpen, 0);
            TryUnregister();
            foreach (GamepadHidParser? parser in _gamepadParsers.Values)
            {
                parser?.Dispose();
            }

            _gamepadParsers.Clear();
            if (_window != 0)
            {
                RawInputInterop.DestroyWindow(_window);
                _window = 0;
            }

            if (_rawBuffer != 0)
            {
                Marshal.FreeHGlobal(_rawBuffer);
                _rawBuffer = 0;
                _rawBufferSize = 0;
            }

            if (_className is not null)
            {
                RawInputInterop.UnregisterClassW(_className, _instance);
            }

            _destroyed.TrySetResult(true);
        }
    }

    private nint WindowProcedure(nint window, uint code, nuint word, nint data)
    {
        switch (code)
        {
            case RawInputInterop.WmInput:
                if (IngressOpen)
                {
                    ReadInput(data);
                }

                return word == RawInputInterop.RimInput
                    ? RawInputInterop.DefWindowProcW(window, code, word, data)
                    : 0;

            case RawInputInterop.WmInputDeviceChange:
                if (IngressOpen)
                {
                    IngressEventKind? kind = word switch
                    {
                        RawInputInterop.GidcArrival => IngressEventKind.DeviceArrived,
                        RawInputInterop.GidcRemoval => IngressEventKind.DeviceRemoved,
                        _ => null
                    };
                    if (kind is not null && !_queue.TryWrite(new InputIngressEvent(
                            kind.Value,
                            data,
                            Stopwatch.GetTimestamp())))
                    {
                        FailIntegrity("Raw Input queue capacity was exceeded while processing a device change.");
                    }

                    if (word == RawInputInterop.GidcRemoval && _gamepadParsers.Remove(data, out GamepadHidParser? parser))
                    {
                        parser?.Dispose();
                    }
                }

                return 0;

            case RawInputInterop.WmAppControl:
                ProcessControls();
                return 0;

            case RawInputInterop.WmClose:
                RawInputInterop.DestroyWindow(window);
                return 0;

            case RawInputInterop.WmDestroy:
                _window = 0;
                RawInputInterop.PostQuitMessage(0);
                return 0;

            default:
                return RawInputInterop.DefWindowProcW(window, code, word, data);
        }
    }

    private void ReadInput(nint rawInputHandle)
    {
        try
        {
            uint size = checked((uint)_rawBufferSize);
            uint copied = RawInputInterop.GetRawInputData(
                rawInputHandle,
                RawInputInterop.RidInput,
                _rawBuffer,
                ref size,
                (uint)Marshal.SizeOf<RawInputInterop.NativeRawInputHeader>());
            if (copied == 0 && size > _rawBufferSize)
            {
                EnsureRawBuffer(size);
                size = checked((uint)_rawBufferSize);
                copied = RawInputInterop.GetRawInputData(
                    rawInputHandle,
                    RawInputInterop.RidInput,
                    _rawBuffer,
                    ref size,
                    (uint)Marshal.SizeOf<RawInputInterop.NativeRawInputHeader>());
            }

            if (copied == uint.MaxValue || copied < Marshal.SizeOf<RawInputInterop.NativeRawInputHeader>())
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetRawInputData failed.");
            }

            RawInputInterop.NativeRawInput raw = Marshal.PtrToStructure<RawInputInterop.NativeRawInput>(_rawBuffer);
            long timestamp = Stopwatch.GetTimestamp();
            InputIngressEvent input;
            if (raw.Header.Type == RawInputInterop.RimTypeKeyboard)
            {
                if (!RawInputParser.TryKeyboard(
                        raw.Header.Device,
                        raw.Data.Keyboard.MakeCode,
                        raw.Data.Keyboard.Flags,
                        timestamp,
                        out input))
                {
                    throw new InvalidDataException("Raw keyboard packet did not contain a usable physical scan code.");
                }
            }
            else if (raw.Header.Type == RawInputInterop.RimTypeMouse)
            {
                input = RawInputParser.Mouse(
                    raw.Header.Device,
                    raw.Data.Mouse.Flags,
                    raw.Data.Mouse.ButtonFlags,
                    raw.Data.Mouse.ButtonData,
                    raw.Data.Mouse.LastX,
                    raw.Data.Mouse.LastY,
                    timestamp);
            }
            else if (raw.Header.Type == RawInputInterop.RimTypeHid)
            {
                ReadGamepadReports(raw.Header.Device, copied, timestamp);
                return;
            }
            else
            {
                return;
            }

            if (!_queue.TryWrite(input))
            {
                FailIntegrity("Raw Input queue capacity was exceeded; capture stopped to prevent silent loss.");
            }
        }
        catch (Exception error)
        {
            _counters.ParseError();
            FailIntegrity($"Raw Input packet could not be normalized: {error.Message}");
        }
    }

    private void ReadGamepadReports(nint deviceHandle, uint packetLength, long timestamp)
    {
        GamepadHidParser? parser;
        if (!_gamepadParsers.TryGetValue(deviceHandle, out parser))
        {
            try
            {
                GamepadHidParser.TryCreate(deviceHandle, out parser);
            }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
            {
                parser = null;
            }

            _gamepadParsers[deviceHandle] = parser;
            if (parser is null)
            {
                ReportGamepadStatus(deviceHandle, GamepadCaptureStatus.Unsupported, "The HID report descriptor is unavailable or contains controls the current parser cannot fully decode.", timestamp);
                return;
            }

            ReportGamepadStatus(deviceHandle, GamepadCaptureStatus.Ready, null, timestamp);
        }

        if (parser is null)
        {
            return;
        }

        int payloadOffset = Marshal.SizeOf<RawInputInterop.NativeRawInputHeader>();
        if (packetLength < payloadOffset + 8)
        {
            FaultGamepadParser(deviceHandle, "Raw HID packet is shorter than its report header.", timestamp);
            return;
        }

        uint reportLength = unchecked((uint)Marshal.ReadInt32(_rawBuffer, payloadOffset));
        uint reportCount = unchecked((uint)Marshal.ReadInt32(_rawBuffer, payloadOffset + 4));
        ulong payloadBytes = (ulong)reportLength * reportCount;
        if (reportLength == 0 || reportCount == 0 || payloadBytes > packetLength - (uint)(payloadOffset + 8)
            || reportLength > int.MaxValue)
        {
            FaultGamepadParser(deviceHandle, "Raw HID packet has an invalid report length.", timestamp);
            return;
        }

        byte[] report = new byte[checked((int)reportLength)];
        var changes = new List<GamepadControlEvent>();
        int reportOffset = payloadOffset + 8;
        for (uint index = 0; index < reportCount; index++)
        {
            Marshal.Copy(nint.Add(_rawBuffer, checked(reportOffset + (int)(index * reportLength))), report, 0, report.Length);
            changes.Clear();
            try
            {
                if (!parser.TryReadReport(report, timestamp, changes, out string? parseError))
                {
                    FaultGamepadParser(deviceHandle, parseError ?? "The HID report could not be decoded with its descriptor.", timestamp);
                    return;
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException and not StackOverflowException)
            {
                FaultGamepadParser(deviceHandle, $"The HID report parser failed: {error.Message}", timestamp);
                return;
            }

            foreach (GamepadControlEvent change in changes)
            {
                InputIngressEvent input = ToGamepadIngressEvent(deviceHandle, change);
                if (!_queue.TryWrite(input))
                {
                    FailIntegrity("Raw Input queue capacity was exceeded while processing a gamepad report.");
                    return;
                }
            }
        }
    }

    internal static InputIngressEvent ToGamepadIngressEvent(nint deviceHandle, GamepadControlEvent change) =>
        new(
            IngressEventKind.Gamepad,
            deviceHandle,
            change.Timestamp,
            GamepadControl: change.Control,
            GamepadValue: change.Value,
            GamepadBaseline: change.IsBaseline,
            GamepadLogicalMinimum: change.LogicalMinimum,
            GamepadLogicalMaximum: change.LogicalMaximum);

    private void FaultGamepadParser(nint deviceHandle, string error, long timestamp)
    {
        if (_gamepadParsers.Remove(deviceHandle, out GamepadHidParser? parser))
        {
            parser?.Dispose();
        }

        _gamepadParsers[deviceHandle] = null;
        ReportGamepadStatus(deviceHandle, GamepadCaptureStatus.Faulted, error, timestamp);
    }

    private void ReportGamepadStatus(nint deviceHandle, GamepadCaptureStatus status, string? error, long timestamp)
    {
        if (!_queue.TryWrite(new InputIngressEvent(
                IngressEventKind.GamepadStatus,
                deviceHandle,
                timestamp,
                GamepadStatus: status,
                GamepadError: error)))
        {
            FailIntegrity("Raw Input queue capacity was exceeded while reporting gamepad status.");
        }
    }

    private void EnsureRawBuffer(uint requiredSize)
    {
        int size = checked((int)requiredSize);
        if (size <= _rawBufferSize)
        {
            return;
        }

        nint buffer = Marshal.ReAllocHGlobal(_rawBuffer, new nint(size));
        if (buffer == 0)
        {
            throw new OutOfMemoryException("Could not allocate the required Raw Input report buffer.");
        }

        _rawBuffer = buffer;
        _rawBufferSize = size;
    }

    private void ProcessControls()
    {
        while (_controls.TryDequeue(out ControlRequest? request))
        {
            try
            {
                switch (request.Kind)
                {
                    case ControlKind.Pause:
                        CloseIngress();
                        Unregister();
                        ResetGamepadBaselines();
                        EnqueueBoundary(request, AggregationBoundaryKind.Paused);
                        break;
                    case ControlKind.Resume:
                        ResetGamepadBaselines();
                        Register();
                        Volatile.Write(ref _ingressOpen, 1);
                        break;
                    case ControlKind.SetGamepadCapture:
                        SetGamepadCaptureEnabled(request.GamepadCaptureEnabled);
                        EnqueueGamepadBarrier(request);
                        break;
                    case ControlKind.PrepareStop:
                        CloseIngress();
                        TryUnregister();
                        if (request.Barrier is not null)
                        {
                            EnqueueBoundary(request, AggregationBoundaryKind.Stopping);
                        }

                        break;
                    case ControlKind.StopAfterFault:
                        CloseIngress();
                        TryUnregister();
                        break;
                    case ControlKind.Destroy:
                        RawInputInterop.DestroyWindow(_window);
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown control kind {request.Kind}.");
                }

                request.Completion.TrySetResult(true);
            }
            catch (Exception error)
            {
                request.Barrier?.TrySetException(error);
                request.Completion.TrySetException(error);
                if (request.Kind != ControlKind.SetGamepadCapture)
                {
                    FailIntegrity($"Raw Input control operation failed: {error.Message}");
                }
            }
        }
    }

    private void EnqueueBoundary(ControlRequest request, AggregationBoundaryKind kind)
    {
        if (!_queue.TryWrite(new InputIngressEvent(
                IngressEventKind.Boundary,
                0,
                Stopwatch.GetTimestamp(),
                BoundaryKind: kind,
                PauseReasons: request.PauseReasons,
                Barrier: request.Barrier)))
        {
            var error = new InvalidOperationException("Raw Input queue capacity was exceeded before the aggregation boundary.");
            request.Barrier?.TrySetException(error);
            throw error;
        }
    }

    private void Register()
    {
        if (Volatile.Read(ref _registered) != 0)
        {
            return;
        }

        uint flags = RawInputInterop.RidevInputSink | RawInputInterop.RidevDevNotify;
        RawInputInterop.RawInputDevice[] devices =
        [
            new(RawInputInterop.UsagePageGenericDesktop, RawInputInterop.UsageMouse, flags, _window),
            new(RawInputInterop.UsagePageGenericDesktop, RawInputInterop.UsageKeyboard, flags, _window)
        ];
        if (!RawInputInterop.RegisterRawInputDevices(
                devices,
                (uint)devices.Length,
                (uint)Marshal.SizeOf<RawInputInterop.RawInputDevice>()))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterRawInputDevices failed.");
        }

        Volatile.Write(ref _registered, 1);
        _onRegistrationChanged(true);
        if (_gamepadCaptureEnabled)
        {
            try
            {
                RegisterGamepad();
            }
            catch (Exception error)
            {
                ReportGamepadStatus(0, GamepadCaptureStatus.Faulted, $"Gamepad Raw Input registration failed: {error.Message}", Stopwatch.GetTimestamp());
            }
        }
    }

    private void Unregister()
    {
        if (Volatile.Read(ref _registered) == 0)
        {
            return;
        }

        UnregisterGamepad();
        RawInputInterop.RawInputDevice[] devices =
        [
            new(RawInputInterop.UsagePageGenericDesktop, RawInputInterop.UsageMouse, RawInputInterop.RidevRemove, 0),
            new(RawInputInterop.UsagePageGenericDesktop, RawInputInterop.UsageKeyboard, RawInputInterop.RidevRemove, 0)
        ];
        if (!RawInputInterop.RegisterRawInputDevices(
                devices,
                (uint)devices.Length,
                (uint)Marshal.SizeOf<RawInputInterop.RawInputDevice>()))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterRawInputDevices(RIDEV_REMOVE) failed.");
        }

        Volatile.Write(ref _registered, 0);
        _onRegistrationChanged(false);
    }

    private void SetGamepadCaptureEnabled(bool enabled)
    {
        if (_gamepadCaptureEnabled == enabled)
        {
            return;
        }

        if (enabled)
        {
            _gamepadCaptureEnabled = true;
            if (Volatile.Read(ref _registered) != 0)
            {
                try
                {
                    RegisterGamepad();
                }
                catch
                {
                    _gamepadCaptureEnabled = false;
                    throw;
                }
            }
        }
        else
        {
            _gamepadCaptureEnabled = false;
            UnregisterGamepad();
            ClearGamepadParsers();
        }

        ResetGamepadBaselines();
    }

    private void RegisterGamepad()
    {
        if (Volatile.Read(ref _gamepadRegistered) != 0)
        {
            return;
        }

        RawInputInterop.RawInputDevice[] devices = RawInputInterop.GamepadRegistrations(
            RawInputInterop.RidevInputSink | RawInputInterop.RidevDevNotify,
            _window);
        if (!RawInputInterop.RegisterRawInputDevices(
                devices,
                (uint)devices.Length,
                (uint)Marshal.SizeOf<RawInputInterop.RawInputDevice>()))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Gamepad Raw Input registration failed.");
        }

        Volatile.Write(ref _gamepadRegistered, 1);
        EnqueueExistingGamepadArrivals();
    }

    private void EnqueueExistingGamepadArrivals()
    {
        foreach (nint deviceHandle in GamepadDeviceDiscovery.EnumerateDeviceHandles())
        {
            if (!_queue.TryWrite(new InputIngressEvent(
                    IngressEventKind.DeviceArrived,
                    deviceHandle,
                    Stopwatch.GetTimestamp(),
                    DeviceKind: RawDeviceKind.Gamepad)))
            {
                FailIntegrity("Raw Input queue capacity was exceeded while enumerating connected gamepads.");
                return;
            }
        }
    }

    private void UnregisterGamepad()
    {
        if (Volatile.Read(ref _gamepadRegistered) == 0)
        {
            return;
        }

        RawInputInterop.RawInputDevice[] devices = RawInputInterop.GamepadRemovals();
        if (!RawInputInterop.RegisterRawInputDevices(
                devices,
                (uint)devices.Length,
                (uint)Marshal.SizeOf<RawInputInterop.RawInputDevice>()))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Gamepad Raw Input unregistration failed.");
        }

        Volatile.Write(ref _gamepadRegistered, 0);
        ClearGamepadParsers();
    }

    private void EnqueueGamepadBarrier(ControlRequest request)
    {
        if (!_queue.TryWrite(new InputIngressEvent(
                IngressEventKind.GamepadBarrier,
                0,
                Stopwatch.GetTimestamp(),
                GamepadCaptureEnabled: request.GamepadCaptureEnabled,
                Barrier: request.Barrier)))
        {
            throw new InvalidOperationException("Raw Input queue capacity was exceeded before the gamepad capture boundary.");
        }
    }

    private void ResetGamepadBaselines()
    {
        foreach (GamepadHidParser? parser in _gamepadParsers.Values)
        {
            parser?.ResetBaseline();
        }
    }

    private void ClearGamepadParsers()
    {
        foreach (GamepadHidParser? parser in _gamepadParsers.Values)
        {
            parser?.Dispose();
        }

        _gamepadParsers.Clear();
    }

    private void TryUnregister()
    {
        try
        {
            Unregister();
        }
        catch (Exception error)
        {
            FailIntegrity($"Raw Input could not be unregistered: {error.Message}");
        }
    }

    private void FailIntegrity(string error)
    {
        if (Interlocked.Exchange(ref _faulted, 1) != 0)
        {
            return;
        }

        CloseIngress();
        try
        {
            Unregister();
        }
        catch (Exception unregisterError)
        {
            error = $"{error} Unregister also failed: {unregisterError.Message}";
        }

        _onIntegrityFault(error);
    }

    private enum ControlKind : byte
    {
        Pause,
        Resume,
        PrepareStop,
        StopAfterFault,
        Destroy,
        SetGamepadCapture
    }

    private sealed class ControlRequest(
        ControlKind kind,
        PauseReason pauseReasons = PauseReason.None,
        TaskCompletionSource<bool>? barrier = null,
        bool gamepadCaptureEnabled = false)
    {
        public ControlKind Kind { get; } = kind;
        public PauseReason PauseReasons { get; } = pauseReasons;
        public TaskCompletionSource<bool>? Barrier { get; } = barrier;
        public bool GamepadCaptureEnabled { get; } = gamepadCaptureEnabled;
        public TaskCompletionSource<bool> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
