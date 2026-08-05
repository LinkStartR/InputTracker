using InputTracker.Input.Win32;

namespace InputTracker.Input;

public enum InputCaptureState : byte
{
    Stopped,
    Starting,
    Running,
    Paused,
    Stopping,
    IntegrityFault,
    StartFailed
}

public sealed record InputCaptureStatus(
    InputCaptureState State,
    PauseReason PauseReasons,
    string? Error);

public sealed record DiagnosticsSnapshot(
    long Produced,
    long Accepted,
    long Dequeued,
    long SinkAcknowledged,
    long Overflow,
    long ParseErrors,
    long OutOfOrder,
    int QueueCapacity,
    InputCaptureState State,
    PauseReason PauseReasons,
    bool IngressOpen,
    bool RawInputRegistered,
    long SuccessfulRegistrations,
    long SuccessfulUnregistrations,
    long CompletedPauseBarriers,
    string? LastError);

public sealed record InputCaptureOptions
{
    public int QueueCapacity { get; init; } = 262_144;
}

public interface IInputCaptureControl
{
    InputCaptureStatus CurrentStatus { get; }
    DiagnosticsSnapshot Diagnostics { get; }
    event EventHandler<InputCaptureStatus>? StatusChanged;
    ValueTask SetPauseReasonAsync(PauseReason reason, bool active, CancellationToken cancellationToken = default);
}

public sealed class InputCaptureService : IInputCaptureControl, IAsyncDisposable
{
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly InputPipelineCounters _counters = new();
    private readonly BoundedInputQueue _queue;
    private readonly InputWorker _worker;
    private readonly IRawInputHost _host;
    private Task? _workerTask;
    private InputCaptureState _state = InputCaptureState.Stopped;
    private PauseReason _pauseReasons;
    private string? _lastError;
    private int _rawInputRegistered;
    private long _successfulRegistrations;
    private long _successfulUnregistrations;
    private long _completedPauseBarriers;
    private int _disposed;

    public InputCaptureService(IInputEventSink sink, InputCaptureOptions? options = null)
        : this(sink, options, RawInputHostFactory.Instance)
    {
    }

    internal InputCaptureService(
        IInputEventSink sink,
        InputCaptureOptions? options,
        IRawInputHostFactory hostFactory)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(hostFactory);
        options ??= new InputCaptureOptions();
        _queue = new BoundedInputQueue(options.QueueCapacity, _counters);
        _worker = new InputWorker(
            _queue,
            _counters,
            sink,
            new RawDeviceInfoProvider(),
            OnWorkerFault);
        _host = hostFactory.Create(
            _queue,
            _counters,
            OnIngressFault,
            OnRegistrationChanged);
    }

    public event EventHandler<InputCaptureStatus>? StatusChanged;

    public InputCaptureStatus CurrentStatus
    {
        get
        {
            lock (_stateLock)
            {
                return new InputCaptureStatus(_state, _pauseReasons, _lastError);
            }
        }
    }

    public DiagnosticsSnapshot Diagnostics
    {
        get
        {
            InputPipelineDiagnostics pipeline = _counters.Snapshot(_queue.Capacity);
            lock (_stateLock)
            {
                return new DiagnosticsSnapshot(
                    pipeline.Produced,
                    pipeline.Accepted,
                    pipeline.Dequeued,
                    pipeline.SinkAcknowledged,
                    pipeline.Overflow,
                    pipeline.ParseErrors,
                    pipeline.OutOfOrder,
                    pipeline.QueueCapacity,
                    _state,
                    _pauseReasons,
                    _host.IngressOpen,
                    Volatile.Read(ref _rawInputRegistered) != 0,
                    Volatile.Read(ref _successfulRegistrations),
                    Volatile.Read(ref _successfulUnregistrations),
                    Volatile.Read(ref _completedPauseBarriers),
                    _lastError);
            }
        }
    }

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (CurrentStatus.State != InputCaptureState.Stopped)
            {
                throw new InvalidOperationException("Input capture can only start from the stopped state.");
            }

            SetStatus(InputCaptureState.Starting, PauseReason.None, null);
            _workerTask = _worker.Start();
            try
            {
                await _host.StartAsync(cancellationToken).ConfigureAwait(false);
                SetStatus(InputCaptureState.Running, PauseReason.None, null);
            }
            catch (Exception error)
            {
                _host.CloseIngress();
                _queue.Complete(error);
                if (_workerTask is not null)
                {
                    await _workerTask.ConfigureAwait(false);
                }

                SetStatus(InputCaptureState.StartFailed, PauseReason.None, error.Message);
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask SetPauseReasonAsync(
        PauseReason reason,
        bool active,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (reason == PauseReason.None || (reason & (reason - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(reason), "Specify exactly one pause reason.");
        }

        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            InputCaptureStatus status = CurrentStatus;
            if (status.State is InputCaptureState.IntegrityFault or InputCaptureState.StartFailed or InputCaptureState.Stopped)
            {
                return;
            }

            PauseReason updated = active ? status.PauseReasons | reason : status.PauseReasons & ~reason;
            if (updated == status.PauseReasons)
            {
                return;
            }

            if (status.PauseReasons == PauseReason.None && updated != PauseReason.None)
            {
                _host.CloseIngress();
                await _host.PauseAsync(updated, cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _completedPauseBarriers);
                SetStatus(InputCaptureState.Paused, updated, null);
                return;
            }

            if (status.PauseReasons != PauseReason.None && updated == PauseReason.None)
            {
                await _host.ResumeAsync(cancellationToken).ConfigureAwait(false);
                SetStatus(InputCaptureState.Running, PauseReason.None, null);
                return;
            }

            SetStatus(InputCaptureState.Paused, updated, null);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            InputCaptureState state = CurrentStatus.State;
            if (state == InputCaptureState.Stopped)
            {
                return;
            }

            SetStatus(InputCaptureState.Stopping, CurrentStatus.PauseReasons, null);
            _host.CloseIngress();
            bool workerCanDrain = _workerTask is { IsCompleted: false };
            await _host.PrepareStopAsync(workerCanDrain, cancellationToken).ConfigureAwait(false);
            _queue.Complete();
            if (_workerTask is not null)
            {
                await _workerTask.ConfigureAwait(false);
            }

            await _host.DestroyAsync(cancellationToken).ConfigureAwait(false);
            SetStatus(InputCaptureState.Stopped, PauseReason.None, null);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsyncCore().ConfigureAwait(false);
        _lifecycle.Dispose();
    }

    private async ValueTask StopAsyncCore()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (CurrentStatus.State != InputCaptureState.Stopped)
            {
                SetStatus(InputCaptureState.Stopping, CurrentStatus.PauseReasons, null);
                _host.CloseIngress();
                bool workerCanDrain = _workerTask is { IsCompleted: false };
                await _host.PrepareStopAsync(workerCanDrain, CancellationToken.None).ConfigureAwait(false);
                _queue.Complete();
                if (_workerTask is not null)
                {
                    await _workerTask.ConfigureAwait(false);
                }

                await _host.DestroyAsync(CancellationToken.None).ConfigureAwait(false);
                SetStatus(InputCaptureState.Stopped, PauseReason.None, null);
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private void OnIngressFault(string error)
    {
        _host.CloseIngress();
        SetStatus(InputCaptureState.IntegrityFault, CurrentStatus.PauseReasons, error);
    }

    private void OnWorkerFault(Exception error)
    {
        _host.CloseIngress();
        _host.StopAfterFault();
        SetStatus(InputCaptureState.IntegrityFault, CurrentStatus.PauseReasons, error.Message);
    }

    private void OnRegistrationChanged(bool registered)
    {
        Volatile.Write(ref _rawInputRegistered, registered ? 1 : 0);
        if (registered)
        {
            Interlocked.Increment(ref _successfulRegistrations);
        }
        else
        {
            Interlocked.Increment(ref _successfulUnregistrations);
        }
    }

    private void SetStatus(InputCaptureState state, PauseReason reasons, string? error)
    {
        InputCaptureStatus status;
        lock (_stateLock)
        {
            _state = state;
            _pauseReasons = reasons;
            _lastError = error;
            status = new InputCaptureStatus(state, reasons, error);
        }

        StatusChanged?.Invoke(this, status);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}

internal interface IRawInputHostFactory
{
    IRawInputHost Create(
        BoundedInputQueue queue,
        InputPipelineCounters counters,
        Action<string> onIntegrityFault,
        Action<bool> onRegistrationChanged);
}

internal interface IRawInputHost
{
    bool IngressOpen { get; }
    ValueTask StartAsync(CancellationToken cancellationToken);
    void CloseIngress();
    ValueTask PauseAsync(PauseReason reasons, CancellationToken cancellationToken);
    ValueTask ResumeAsync(CancellationToken cancellationToken);
    ValueTask PrepareStopAsync(bool enqueueBarrier, CancellationToken cancellationToken);
    ValueTask DestroyAsync(CancellationToken cancellationToken);
    void StopAfterFault();
}

internal sealed class RawInputHostFactory : IRawInputHostFactory
{
    public static RawInputHostFactory Instance { get; } = new();

    public IRawInputHost Create(
        BoundedInputQueue queue,
        InputPipelineCounters counters,
        Action<string> onIntegrityFault,
        Action<bool> onRegistrationChanged) =>
        new RawInputMessageWindow(queue, counters, onIntegrityFault, onRegistrationChanged);
}
