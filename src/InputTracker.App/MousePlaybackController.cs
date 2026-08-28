using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Channels;
using InputTracker.Storage;

namespace InputTracker;

internal interface IMousePlaybackDataSource
{
    Task<IMousePlaybackSnapshot> OpenAsync(
        Guid deviceId,
        StorageUtcRange range,
        CancellationToken cancellationToken);
}

internal interface IMousePlaybackSnapshot : IAsyncDisposable
{
    StorageMousePlaybackSnapshotInfo Info { get; }
    IAsyncEnumerable<StorageMousePlaybackRecord> StreamFromAsync(
        StorageMousePlaybackCursor? inclusiveCursor,
        CancellationToken cancellationToken);
    ValueTask<StorageMousePlaybackCursor?> SeekAsync(
        long targetUtcMicroseconds,
        CancellationToken cancellationToken);
}

internal sealed class StorageMousePlaybackDataSource(StorageService service) : IMousePlaybackDataSource
{
    public async Task<IMousePlaybackSnapshot> OpenAsync(
        Guid deviceId,
        StorageUtcRange range,
        CancellationToken cancellationToken) =>
        new StorageMousePlaybackSnapshotAdapter(
            await service.OpenMousePlaybackSnapshotAsync(deviceId, range, cancellationToken)
                .ConfigureAwait(false));
}

internal sealed class StorageMousePlaybackSnapshotAdapter(StorageMousePlaybackSnapshot snapshot) : IMousePlaybackSnapshot
{
    public StorageMousePlaybackSnapshotInfo Info => snapshot.Info;

    public IAsyncEnumerable<StorageMousePlaybackRecord> StreamFromAsync(
        StorageMousePlaybackCursor? inclusiveCursor,
        CancellationToken cancellationToken) => snapshot.StreamFromAsync(inclusiveCursor, cancellationToken);

    public ValueTask<StorageMousePlaybackCursor?> SeekAsync(
        long targetUtcMicroseconds,
        CancellationToken cancellationToken) => snapshot.SeekAsync(targetUtcMicroseconds, cancellationToken);

    public ValueTask DisposeAsync() => snapshot.DisposeAsync();
}

internal enum MousePlaybackState
{
    Empty,
    Loading,
    Ready,
    Playing,
    Paused,
    Completed,
    Seeking,
    Faulted,
    Disposed
}

[Flags]
internal enum MousePlaybackPauseReason
{
    None = 0,
    User = 1,
    WindowHidden = 2,
    Seeking = 4
}

internal enum MousePlaybackSpeed
{
    Quarter,
    Half,
    One,
    Double,
    Four
}

internal sealed record MousePlaybackDiagnostics(
    long Generation,
    MousePlaybackState State,
    Guid? DeviceId,
    StorageMousePlaybackSnapshotInfo? Snapshot,
    StorageMousePlaybackCursor? Cursor,
    long ProducedCount,
    long ProcessedCount,
    long PositionCount,
    ulong RollingHash,
    long OrderFailures,
    int QueueDepth,
    int ActiveTailCount,
    MousePlaybackSpeed Speed,
    MousePlaybackPauseReason PauseReasons,
    bool IsCatchingUp,
    bool HasUncalibratedAbsolute,
    string? Fault);

internal sealed record MousePlaybackRenderSegment(
    IReadOnlyList<System.Windows.Point> Points,
    bool IsUncalibratedAbsolute,
    string? Disclosure);

/// <summary>
/// Playback projection never invents a desktop transform for absolute mouse input. Relative records
/// are integrated only within their explicit segment; absolute records remain visible as a disclosure.
/// </summary>
internal static class MousePlaybackRenderer
{
    internal const string UncalibratedAbsoluteDisclosure = "绝对坐标未校准，保留原始记录但不伪造屏幕位置。";

    public static IReadOnlyList<MousePlaybackRenderSegment> BuildSegments(
        IReadOnlyList<MouseDisplayPoint> points,
        double width = 320,
        double height = 180)
    {
        var result = new List<MousePlaybackRenderSegment>();
        List<System.Windows.Point>? current = null;
        double x = width / 2;
        double y = height / 2;
        byte? previousMotion = null;
        bool? previousVirtualDesktop = null;
        foreach (MouseDisplayPoint point in points)
        {
            bool newSegment = current is null || point.StartsNewSegment
                || previousMotion != point.MotionKind
                || previousVirtualDesktop != point.UsesVirtualDesktop;
            if (newSegment)
            {
                current = [];
                x = width / 2;
                y = height / 2;
            }

            if (point.MotionKind != 0)
            {
                result.Add(new MousePlaybackRenderSegment(
                    [], true, UncalibratedAbsoluteDisclosure));
                current = null;
                previousMotion = point.MotionKind;
                previousVirtualDesktop = point.UsesVirtualDesktop;
                continue;
            }

            x = Math.Clamp(x + point.DeltaX, 0, Math.Max(0, width));
            y = Math.Clamp(y + point.DeltaY, 0, Math.Max(0, height));
            current!.Add(new System.Windows.Point(x, y));
            if (newSegment)
            {
                result.Add(new MousePlaybackRenderSegment(current, false, null));
            }
            previousMotion = point.MotionKind;
            previousVirtualDesktop = point.UsesVirtualDesktop;
        }
        return result;
    }
}

/// <summary>
/// Reads every raw playback record through a bounded producer/consumer channel. Render geometry is
/// deliberately bounded, while counters, order and hash cover every record.
/// </summary>
internal sealed class MousePlaybackController : IAsyncDisposable
{
    internal const int MaxPrefetchRecords = 4096;
    internal const int MaxTailPoints = 2048;
    private readonly IMousePlaybackDataSource _source;
    private readonly object _gate = new();
    private readonly List<MouseDisplayPoint> _tail = [];
    private readonly List<long> _processedSequences = [];
    private CancellationTokenSource? _generationCancellation;
    private Task? _producer;
    private Task? _readerOperation;
    private IMousePlaybackSnapshot? _snapshot;
    private Channel<StorageMousePlaybackRecord>? _channel;
    private StorageMousePlaybackRecord? _pending;
    private long _generation;
    private long _producedCount;
    private long _processedCount;
    private long _processedOffset;
    private ulong _rollingHash;
    private long _orderFailures;
    private long _clockUtc;
    private long _lastSequence;
    private Guid? _deviceId;
    private MousePlaybackPauseReason _pauseReasons;
    private MousePlaybackSpeed _speed = MousePlaybackSpeed.One;
    private MousePlaybackState _state = MousePlaybackState.Empty;
    private string? _fault;
    private bool _producerCompleted;
    private bool _lastFrameHitLimit;
    private bool _hasUncalibratedAbsolute;
    private bool _disposed;

    public MousePlaybackController(IMousePlaybackDataSource source) =>
        _source = source ?? throw new ArgumentNullException(nameof(source));

    public event EventHandler? Changed;

    public MousePlaybackDiagnostics Diagnostics
    {
        get
        {
            lock (_gate)
            {
                return CreateDiagnostics();
            }
        }
    }

    internal IReadOnlyList<MouseDisplayPoint> ActiveTail
    {
        get
        {
            lock (_gate)
            {
                return new ReadOnlyCollection<MouseDisplayPoint>(_tail.ToArray());
            }
        }
    }

    internal IReadOnlyList<long> ProcessedSequences
    {
        get
        {
            lock (_gate)
            {
                return new ReadOnlyCollection<long>(_processedSequences.ToArray());
            }
        }
    }

    // Playback only controls the replay clock. It never calls the live capture pause API.
    internal int CapturePauseRequests => 0;

    public async Task LoadAsync(Guid deviceId, StorageUtcRange range, CancellationToken cancellationToken = default)
    {
        if (deviceId == Guid.Empty) throw new ArgumentOutOfRangeException(nameof(deviceId));
        ThrowIfDisposed();
        (long generation, CancellationToken token) = await BeginReplacementAsync(MousePlaybackState.Loading)
            .ConfigureAwait(false);
        RaiseChanged();

        IMousePlaybackSnapshot snapshot;
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
        try
        {
            snapshot = await _source.OpenAsync(deviceId, range, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception error)
        {
            SetFault(generation, error);
            throw;
        }

        bool accepted;
        lock (_gate)
        {
            accepted = !_disposed && generation == _generation;
            if (accepted)
            {
                _snapshot = snapshot;
                _channel = CreateChannel();
                _pending = null;
                _deviceId = deviceId;
                _producedCount = 0;
                _processedCount = 0;
                _processedOffset = 0;
                _rollingHash = 0;
                _orderFailures = 0;
                _lastSequence = 0;
                _tail.Clear();
                _processedSequences.Clear();
                _pauseReasons = MousePlaybackPauseReason.None;
                _fault = null;
                _producer = null;
                _producerCompleted = false;
                _lastFrameHitLimit = false;
                _clockUtc = snapshot.Info.First?.UtcMicroseconds ?? range.StartUtcMicroseconds;
                _state = MousePlaybackState.Ready;
            }
        }

        if (!accepted)
        {
            await snapshot.DisposeAsync().ConfigureAwait(false);
        }
        RaiseChanged();
    }

    public Task PlayAsync()
    {
        ThrowIfDisposed();
        lock (_gate)
        {
            if (_snapshot is null) throw new InvalidOperationException("Mouse playback has not been loaded.");
            if (_state is MousePlaybackState.Completed or MousePlaybackState.Faulted)
            {
                throw new InvalidOperationException("Mouse playback cannot continue from its current terminal state.");
            }

            EnsureProducerStarted();
            if (_pauseReasons == MousePlaybackPauseReason.None)
            {
                _state = MousePlaybackState.Playing;
            }
            else
            {
                _state = MousePlaybackState.Paused;
            }
        }

        RaiseChanged();
        return Task.CompletedTask;
    }

    public void Pause(MousePlaybackPauseReason reason)
    {
        ValidatePauseReason(reason);
        lock (_gate)
        {
            if (_state is MousePlaybackState.Disposed or MousePlaybackState.Empty) return;
            _pauseReasons |= reason;
            if (_state == MousePlaybackState.Playing) _state = MousePlaybackState.Paused;
        }
        RaiseChanged();
    }

    public void Resume(MousePlaybackPauseReason reason)
    {
        ValidatePauseReason(reason);
        lock (_gate)
        {
            if (_state is MousePlaybackState.Disposed or MousePlaybackState.Empty) return;
            _pauseReasons &= ~reason;
            if (_state == MousePlaybackState.Paused && _pauseReasons == MousePlaybackPauseReason.None)
            {
                _state = MousePlaybackState.Playing;
            }
        }
        RaiseChanged();
    }

    public void SetSpeed(MousePlaybackSpeed speed)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _speed = speed;
        }
        RaiseChanged();
    }

    public async Task SeekAsync(StorageMousePlaybackCursor cursor, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        IMousePlaybackSnapshot snapshot;
        lock (_gate)
        {
            snapshot = _snapshot ?? throw new InvalidOperationException("Mouse playback has not been loaded.");
            if (cursor.SnapshotId != snapshot.Info.SnapshotId)
            {
                throw new InvalidDataException("Mouse playback cursor does not belong to the loaded snapshot.");
            }
        }
        (long generation, CancellationToken generationToken) = await BeginSeekAsync(cursor).ConfigureAwait(false);
        RaiseChanged();

        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, generationToken);
        Task<long>? countTask = null;
        try
        {
            countTask = Task.Run(
                () => CountBeforeCursorAsync(snapshot, cursor, linked.Token), linked.Token);
            lock (_gate)
            {
                if (generation == _generation && !_disposed) _readerOperation = countTask;
            }
            long preceding = await countTask.ConfigureAwait(false);
            lock (_gate)
            {
                if (generation != _generation || _disposed) return;
                _processedOffset = preceding;
                _producer = StartProducer(generation, cursor);
                _pauseReasons &= ~MousePlaybackPauseReason.Seeking;
                _state = _pauseReasons == MousePlaybackPauseReason.None
                    ? MousePlaybackState.Playing
                    : MousePlaybackState.Paused;
            }
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            return;
        }
        catch (Exception error)
        {
            SetFault(generation, error);
            throw;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_readerOperation, countTask)) _readerOperation = null;
            }
            RaiseChanged();
        }
    }

    public async Task SeekToUtcAsync(long targetUtcMicroseconds, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        IMousePlaybackSnapshot snapshot;
        lock (_gate)
        {
            snapshot = _snapshot ?? throw new InvalidOperationException("Mouse playback has not been loaded.");
        }
        StorageMousePlaybackCursor? cursor = await snapshot.SeekAsync(targetUtcMicroseconds, cancellationToken)
            .ConfigureAwait(false);
        if (cursor is null)
        {
            throw new InvalidDataException("The requested playback time is outside the loaded snapshot.");
        }
        await SeekAsync(cursor.Value, cancellationToken).ConfigureAwait(false);
    }

    public async Task AdvanceByAsync(TimeSpan elapsed, int maxRecords = 512, CancellationToken cancellationToken = default)
    {
        if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        if (maxRecords <= 0) throw new ArgumentOutOfRangeException(nameof(maxRecords));
        bool canConsume;
        lock (_gate)
        {
            ThrowIfDisposed();
            canConsume = _state == MousePlaybackState.Playing && _pauseReasons == MousePlaybackPauseReason.None;
            if (canConsume)
            {
                long advance = checked((long)(elapsed.TotalMilliseconds * 1_000d * GetSpeedMultiplier(_speed)));
                _clockUtc = checked(_clockUtc + advance);
            }
        }
        if (canConsume) await ConsumeAsync(maxRecords, respectClock: true, cancellationToken).ConfigureAwait(false);
    }

    public Task ProcessFrameAsync(int maxRecords = 512, CancellationToken cancellationToken = default) =>
        AdvanceByAsync(TimeSpan.FromMilliseconds(16), maxRecords, cancellationToken);

    internal async Task DrainAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ConsumeAsync(MaxPrefetchRecords, respectClock: false, cancellationToken).ConfigureAwait(false);
            Task? producer;
            bool complete;
            bool terminalFault;
            lock (_gate)
            {
                producer = _producer;
                complete = _producerCompleted && _pending is null && (_channel?.Reader.Completion.IsCompleted ?? true);
                terminalFault = _state is MousePlaybackState.Faulted or MousePlaybackState.Disposed;
            }
            if (terminalFault) return;
            if (complete)
            {
                MarkCompleted();
                return;
            }
            if (producer is not null) await Task.WhenAny(producer, Task.Delay(1, cancellationToken)).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        CancellationTokenSource? cts;
        Task? producer;
        Task? readerOperation;
        IMousePlaybackSnapshot? snapshot;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _state = MousePlaybackState.Disposed;
            cts = _generationCancellation;
            _generationCancellation = null;
            producer = _producer;
            readerOperation = _readerOperation;
            _readerOperation = null;
            snapshot = _snapshot;
            _snapshot = null;
        }
        cts?.Cancel();
        await AwaitProducerAsync(readerOperation).ConfigureAwait(false);
        if (producer is not null)
        {
            try { await producer.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        cts?.Dispose();
        if (snapshot is not null) await snapshot.DisposeAsync().ConfigureAwait(false);
        RaiseChanged();
    }

    internal static ulong AddToRollingHash(ulong hash, StorageMousePlaybackRecord value)
    {
        unchecked
        {
            ulong current = hash == 0 ? 14695981039346656037UL : hash;
            current = (current ^ (ulong)value.Cursor.Sequence) * 1099511628211UL;
            current = (current ^ (ulong)value.Cursor.UtcMicroseconds) * 1099511628211UL;
            current = (current ^ (uint)value.Record.DeltaX) * 1099511628211UL;
            current = (current ^ (uint)value.Record.DeltaY) * 1099511628211UL;
            current = (current ^ value.Record.MotionKind) * 1099511628211UL;
            current = (current ^ (value.Record.UsesVirtualDesktop ? 1UL : 0UL)) * 1099511628211UL;
            current = (current ^ value.Record.ButtonChanges) * 1099511628211UL;
            current = (current ^ (ushort)value.Record.VerticalWheelDelta) * 1099511628211UL;
            current = (current ^ (ushort)value.Record.HorizontalWheelDelta) * 1099511628211UL;
            return current;
        }
    }

    private async Task<(long Generation, CancellationToken Token)> BeginReplacementAsync(MousePlaybackState state)
    {
        CancellationTokenSource? oldCts;
        Task? oldProducer;
        Task? oldReaderOperation;
        IMousePlaybackSnapshot? oldSnapshot;
        long generation;
        CancellationToken token;
        lock (_gate)
        {
            ThrowIfDisposed();
            oldCts = _generationCancellation;
            oldProducer = _producer;
            oldReaderOperation = _readerOperation;
            oldSnapshot = _snapshot;
            _generationCancellation = new CancellationTokenSource();
            _producer = null;
            _readerOperation = null;
            _snapshot = null;
            _channel = null;
            _pending = null;
            _generation = checked(_generation + 1);
            generation = _generation;
            _deviceId = null;
            ResetProcessedState();
            _producerCompleted = false;
            _lastFrameHitLimit = false;
            _state = state;
            token = _generationCancellation.Token;
        }
        oldCts?.Cancel();
        try
        {
            await AwaitProducerAsync(oldProducer).ConfigureAwait(false);
            await AwaitProducerAsync(oldReaderOperation).ConfigureAwait(false);
            if (oldSnapshot is not null) await oldSnapshot.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            SetFault(generation, error);
            throw;
        }
        finally
        {
            oldCts?.Dispose();
        }
        return (generation, token);
    }

    private async Task<(long Generation, CancellationToken Token)> BeginSeekAsync(StorageMousePlaybackCursor cursor)
    {
        CancellationTokenSource? oldCts;
        Task? oldProducer;
        Task? oldReaderOperation;
        long generation;
        CancellationToken token;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_snapshot is null || cursor.SnapshotId != _snapshot.Info.SnapshotId)
            {
                throw new InvalidDataException("Mouse playback cursor does not belong to the loaded snapshot.");
            }
            oldCts = _generationCancellation;
            oldProducer = _producer;
            oldReaderOperation = _readerOperation;
            _generationCancellation = new CancellationTokenSource();
            _producer = null;
            _readerOperation = null;
            _channel = null;
            _pending = null;
            _generation = checked(_generation + 1);
            generation = _generation;
            _pauseReasons |= MousePlaybackPauseReason.Seeking;
            _state = MousePlaybackState.Seeking;
            ResetProcessedState();
            _clockUtc = cursor.UtcMicroseconds;
            _producerCompleted = false;
            _lastFrameHitLimit = false;
            token = _generationCancellation.Token;
        }
        oldCts?.Cancel();
        try
        {
            await AwaitProducerAsync(oldProducer).ConfigureAwait(false);
            await AwaitProducerAsync(oldReaderOperation).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            SetFault(generation, error);
            throw;
        }
        finally
        {
            oldCts?.Dispose();
        }
        lock (_gate)
        {
            if (!_disposed && generation == _generation)
            {
                _channel = CreateChannel();
            }
        }
        return (generation, token);
    }

    private static async Task AwaitProducerAsync(Task? producer)
    {
        if (producer is null) return;
        try
        {
            await producer.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void EnsureProducerStarted()
    {
        if (_producer is not null) return;
        _producer = StartProducer(_generation, cursor: null);
    }

    private Task StartProducer(long generation, StorageMousePlaybackCursor? cursor)
    {
        ChannelWriter<StorageMousePlaybackRecord> writer = _channel!.Writer;
        IMousePlaybackSnapshot snapshot = _snapshot!;
        CancellationToken token = _generationCancellation!.Token;
        return Task.Run(async () =>
        {
            try
            {
                await foreach (StorageMousePlaybackRecord record in snapshot.StreamFromAsync(cursor, token)
                    .WithCancellation(token).ConfigureAwait(false))
                {
                    token.ThrowIfCancellationRequested();
                    await writer.WriteAsync(record, token).ConfigureAwait(false);
                    lock (_gate)
                    {
                        if (generation != _generation || _disposed) return;
                        _producedCount = checked(_producedCount + 1);
                    }
                }
                writer.TryComplete();
                lock (_gate)
                {
                    if (generation == _generation && !_disposed) _producerCompleted = true;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                writer.TryComplete();
            }
            catch (Exception error)
            {
                writer.TryComplete(error);
                SetFault(generation, error);
            }
            finally
            {
                RaiseChanged();
            }
        });
    }

    private async Task ConsumeAsync(int maxRecords, bool respectClock, CancellationToken cancellationToken)
    {
        int consumed = 0;
        bool completed = false;
        for (int count = 0; count < maxRecords; count++)
        {
            StorageMousePlaybackRecord? next;
            lock (_gate)
            {
                if (_state is MousePlaybackState.Paused or MousePlaybackState.Seeking
                    or MousePlaybackState.Faulted or MousePlaybackState.Disposed)
                {
                    return;
                }
                next = _pending;
                if (next is null && _channel?.Reader.TryRead(out StorageMousePlaybackRecord read) == true)
                {
                    next = read;
                }
                if (next is null)
                {
                    completed = _producerCompleted && (_channel?.Reader.Completion.IsCompleted ?? true);
                    break;
                }
                if (respectClock && next.Value.Cursor.UtcMicroseconds > _clockUtc)
                {
                    _pending = next;
                    _lastFrameHitLimit = false;
                    break;
                }
                _pending = null;
                Process(next.Value);
                if (_state == MousePlaybackState.Faulted) break;
                consumed++;
            }
        }
        lock (_gate)
        {
            if (_state != MousePlaybackState.Faulted && consumed == maxRecords)
            {
                if (_pending is null && _channel?.Reader.TryRead(out StorageMousePlaybackRecord afterBatch) == true)
                {
                    _pending = afterBatch;
                }
                _lastFrameHitLimit = _pending is { } pending
                    && (!respectClock || pending.Cursor.UtcMicroseconds <= _clockUtc);
            }
            else
            {
                _lastFrameHitLimit = false;
            }
        }
        if (completed) MarkCompleted();
        if (consumed > 0 || completed) RaiseChanged();
        await Task.CompletedTask;
    }

    private void Process(StorageMousePlaybackRecord playback)
    {
        if (_snapshot is null || playback.Record.DeviceId != _snapshot.Info.DeviceId
            || playback.Cursor.SnapshotId != _snapshot.Info.SnapshotId
            || playback.Cursor.Sequence <= _lastSequence
            || (_lastSequence != 0 && playback.Cursor.Sequence != _lastSequence + 1))
        {
            _orderFailures = checked(_orderFailures + 1);
            _state = MousePlaybackState.Faulted;
            _fault = "回放记录不属于当前设备/快照，或顺序不连续。";
            _generationCancellation?.Cancel();
            return;
        }
        _lastSequence = playback.Cursor.Sequence;
        _processedCount = checked(_processedCount + 1);
        _rollingHash = AddToRollingHash(_rollingHash, playback);
        _hasUncalibratedAbsolute |= playback.Record.MotionKind != 0;
        _tail.Add(new MouseDisplayPoint(
            playback.Cursor.Sequence,
            playback.Cursor.UtcMicroseconds,
            playback.Record.DeltaX,
            playback.Record.DeltaY,
            playback.Record.MotionKind,
            playback.Record.UsesVirtualDesktop,
            playback.StartsNewSegment));
        if (_tail.Count > MaxTailPoints) _tail.RemoveRange(0, _tail.Count - MaxTailPoints);
        _processedSequences.Add(playback.Cursor.Sequence);
        if (_processedSequences.Count > MaxTailPoints)
        {
            _processedSequences.RemoveRange(0, _processedSequences.Count - MaxTailPoints);
        }
    }

    private void ResetProcessedState()
    {
        _producedCount = 0;
        _processedCount = 0;
        _processedOffset = 0;
        _rollingHash = 0;
        _orderFailures = 0;
        _lastSequence = 0;
        _hasUncalibratedAbsolute = false;
        _tail.Clear();
        _processedSequences.Clear();
    }

    private static Channel<StorageMousePlaybackRecord> CreateChannel() =>
        Channel.CreateBounded<StorageMousePlaybackRecord>(new BoundedChannelOptions(MaxPrefetchRecords)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });

    private void MarkCompleted()
    {
        lock (_gate)
        {
            if (_state is MousePlaybackState.Playing or MousePlaybackState.Ready)
            {
                _state = MousePlaybackState.Completed;
            }
        }
        RaiseChanged();
    }

    private void SetFault(long generation, Exception error)
    {
        lock (_gate)
        {
            if (generation != _generation || _disposed) return;
            _state = MousePlaybackState.Faulted;
            _fault = error.Message;
        }
        RaiseChanged();
    }

    private MousePlaybackDiagnostics CreateDiagnostics() => new(
        _generation,
        _state,
        _deviceId,
        _snapshot?.Info,
        _lastSequence == 0 || _snapshot is null ? null : new StorageMousePlaybackCursor(
            _snapshot.Info.SnapshotId, _lastSequence, _tail[^1].LastUtcMicroseconds),
        _producedCount,
        _processedCount,
        checked(_processedOffset + _processedCount),
        _rollingHash,
        _orderFailures,
        _channel?.Reader.Count ?? 0,
        _tail.Count,
        _speed,
        _pauseReasons,
        _state == MousePlaybackState.Playing && _lastFrameHitLimit,
        _hasUncalibratedAbsolute,
        _fault);

    private static void ValidatePauseReason(MousePlaybackPauseReason reason)
    {
        if (reason == MousePlaybackPauseReason.None || (reason & ~(
            MousePlaybackPauseReason.User | MousePlaybackPauseReason.WindowHidden | MousePlaybackPauseReason.Seeking)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }
    }

    private static async Task<long> CountBeforeCursorAsync(
        IMousePlaybackSnapshot snapshot,
        StorageMousePlaybackCursor cursor,
        CancellationToken cancellationToken)
    {
        long count = 0;
        await foreach (StorageMousePlaybackRecord record in snapshot.StreamFromAsync(null, cancellationToken)
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (record.Cursor.Sequence == cursor.Sequence
                && record.Cursor.UtcMicroseconds == cursor.UtcMicroseconds
                && record.Cursor.SnapshotId == cursor.SnapshotId)
            {
                return count;
            }
            if (record.Cursor.Sequence > cursor.Sequence)
            {
                break;
            }
            count = checked(count + 1);
        }
        throw new InvalidDataException("Mouse playback cursor is missing or altered.");
    }

    private static double GetSpeedMultiplier(MousePlaybackSpeed speed) => speed switch
    {
        MousePlaybackSpeed.Quarter => 0.25,
        MousePlaybackSpeed.Half => 0.5,
        MousePlaybackSpeed.One => 1,
        MousePlaybackSpeed.Double => 2,
        MousePlaybackSpeed.Four => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(speed))
    };

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(MousePlaybackController));
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
