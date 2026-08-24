using System.Runtime.CompilerServices;

namespace InputTracker.Storage;

/// <summary>
/// An immutable, fully validated view of one durable mouse prefix. The view owns a logical
/// read lease; it never owns the service writer or a SQLite transaction.
/// </summary>
public sealed class StorageMousePlaybackSnapshot : IAsyncDisposable
{
    private readonly StorageQuery.MouseQuerySnapshot _snapshot;
    private readonly CancellationTokenSource _disposed = new();
    private readonly MousePlaybackReadLease _lease;
    private bool _isDisposed;

    internal StorageMousePlaybackSnapshot(
        StorageQuery.MouseQuerySnapshot snapshot,
        StorageMousePlaybackSnapshotInfo info,
        MousePlaybackReadLease lease)
    {
        _snapshot = snapshot;
        Info = info;
        _lease = lease;
    }

    public StorageMousePlaybackSnapshotInfo Info { get; }

    internal static async Task<StorageMousePlaybackSnapshot> OpenAsync(
        StorageQuery.MouseQuerySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        return await Task.Run(
            () => CreateValidated(snapshot, cancellationToken),
            cancellationToken).ConfigureAwait(false);
    }

    public IAsyncEnumerable<StorageMousePlaybackRecord> StreamFromAsync(
        StorageMousePlaybackCursor? inclusiveCursor,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (inclusiveCursor is StorageMousePlaybackCursor cursor
            && cursor.SnapshotId != Info.SnapshotId)
        {
            throw new InvalidDataException("Mouse playback cursor does not belong to this snapshot.");
        }

        return StreamCore(inclusiveCursor, cancellationToken);
    }

    public async ValueTask<StorageMousePlaybackCursor?> SeekAsync(
        long targetUtcMicroseconds,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _disposed.Token);
        return await Task.Run(
            () => FindFirstAtOrAfter(targetUtcMicroseconds, linked.Token),
            linked.Token).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            _disposed.Cancel();
            _lease.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private static StorageMousePlaybackSnapshot CreateValidated(
        StorageQuery.MouseQuerySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        StorageQuery.ValidateAllSegments(snapshot, cancellationToken);
        long count = 0;
        StorageMousePlaybackCursor? first = null;
        StorageMousePlaybackCursor? last = null;
        Guid snapshotId = Guid.NewGuid();
        foreach (StorageQuery.MouseQuerySegment segment in snapshot.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MouseSpoolSegment spool = StorageQuery.ReadAndValidateSegment(snapshot, segment);
            foreach (MouseSpoolRecord record in spool.Records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long utc = segment.Calibration.ToUtcMicroseconds(record.MonotonicTimestamp);
                if (!Contains(snapshot.UtcRange, utc))
                {
                    continue;
                }

                var cursor = new StorageMousePlaybackCursor(snapshotId, record.Sequence, utc);
                first ??= cursor;
                last = cursor;
                count = checked(count + 1);
            }
        }

        MousePlaybackReadLease lease = MousePlaybackReadLease.Acquire(
            StorageQuery.GetReadLeasePaths(snapshot));
        return new StorageMousePlaybackSnapshot(
            snapshot,
            new StorageMousePlaybackSnapshotInfo(
                snapshotId,
                snapshot.DeviceId,
                snapshot.UtcRange,
                snapshot.Watermark,
                count,
                first,
                last),
            lease);
    }

    private async IAsyncEnumerable<StorageMousePlaybackRecord> StreamCore(
        StorageMousePlaybackCursor? inclusiveCursor,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _disposed.Token);
        if (inclusiveCursor is StorageMousePlaybackCursor requested)
        {
            await Task.Run(() => ValidateCursor(requested, linked.Token), linked.Token)
                .ConfigureAwait(false);
        }
        bool emit = inclusiveCursor is null;
        foreach (StorageQuery.MouseQuerySegment segment in _snapshot.Segments)
        {
            linked.Token.ThrowIfCancellationRequested();
            MouseSpoolSegment spool = await Task.Run(
                () => StorageQuery.ReadAndValidateSegment(_snapshot, segment),
                linked.Token).ConfigureAwait(false);
            bool segmentStarted = false;
            foreach (MouseSpoolRecord raw in spool.Records)
            {
                linked.Token.ThrowIfCancellationRequested();
                long utc = segment.Calibration.ToUtcMicroseconds(raw.MonotonicTimestamp);
                if (!Contains(_snapshot.UtcRange, utc))
                {
                    continue;
                }

                var cursor = new StorageMousePlaybackCursor(Info.SnapshotId, raw.Sequence, utc);
                if (!emit)
                {
                    if (cursor.Sequence != inclusiveCursor!.Value.Sequence)
                    {
                        continue;
                    }

                    emit = true;
                }

                bool startsNewSegment = !segmentStarted;
                StorageMousePlaybackBoundaryReason reason = raw.Sequence == segment.FirstSequence
                    ? segment.BoundaryReason
                    : StorageMousePlaybackBoundaryReason.RangeStart;
                segmentStarted = true;
                yield return new StorageMousePlaybackRecord(
                    ToStorageRecord(raw, utc),
                    startsNewSegment,
                    startsNewSegment ? reason : segment.BoundaryReason,
                    cursor);
            }
        }
    }

    private void ValidateCursor(StorageMousePlaybackCursor cursor, CancellationToken cancellationToken)
    {
        if (cursor.SnapshotId != Info.SnapshotId
            || cursor.Sequence <= 0
            || !Contains(_snapshot.UtcRange, cursor.UtcMicroseconds))
        {
            throw new InvalidDataException("Mouse playback cursor does not belong to this snapshot.");
        }

        foreach (StorageQuery.MouseQuerySegment segment in _snapshot.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cursor.Sequence < segment.FirstSequence || cursor.Sequence > segment.LastSequence)
            {
                continue;
            }

            MouseSpoolSegment spool = StorageQuery.ReadAndValidateSegment(_snapshot, segment);
            MouseSpoolRecord raw = spool.Records[checked((int)(cursor.Sequence - segment.FirstSequence))];
            long utc = segment.Calibration.ToUtcMicroseconds(raw.MonotonicTimestamp);
            if (utc == cursor.UtcMicroseconds && Contains(_snapshot.UtcRange, utc))
            {
                return;
            }

            break;
        }

        throw new InvalidDataException("Mouse playback cursor is missing or altered.");
    }

    private StorageMousePlaybackCursor? FindFirstAtOrAfter(long targetUtcMicroseconds, CancellationToken cancellationToken)
    {
        foreach (StorageQuery.MouseQuerySegment segment in _snapshot.Segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MouseSpoolSegment spool = StorageQuery.ReadAndValidateSegment(_snapshot, segment);
            foreach (MouseSpoolRecord raw in spool.Records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long utc = segment.Calibration.ToUtcMicroseconds(raw.MonotonicTimestamp);
                if (Contains(_snapshot.UtcRange, utc) && utc >= targetUtcMicroseconds)
                {
                    return new StorageMousePlaybackCursor(Info.SnapshotId, raw.Sequence, utc);
                }
            }
        }

        return null;
    }

    private static bool Contains(StorageUtcRange range, long utc) =>
        utc >= range.StartUtcMicroseconds && utc < range.EndUtcMicroseconds;

    private StorageMouseRecord ToStorageRecord(MouseSpoolRecord raw, long utc) => new(
        _snapshot.DeviceId,
        raw.Sequence,
        utc,
        raw.DeltaX,
        raw.DeltaY,
        raw.MotionKind,
        raw.UsesVirtualDesktop,
        raw.ButtonChanges,
        raw.VerticalWheelDelta,
        raw.HorizontalWheelDelta);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }
}

internal sealed class MousePlaybackReadLease : IDisposable
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, int> Counts = new(StringComparer.OrdinalIgnoreCase);
    private readonly string[] _paths;
    private bool _disposed;

    private MousePlaybackReadLease(string[] paths)
    {
        _paths = paths;
    }

    public static MousePlaybackReadLease Acquire(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        string[] distinct = paths.Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        lock (Gate)
        {
            foreach (string path in distinct)
            {
                Counts.TryGetValue(path, out int count);
                Counts[path] = checked(count + 1);
            }
        }

        return new MousePlaybackReadLease(distinct);
    }

    internal static bool IsLeased(string path)
    {
        string full = Path.GetFullPath(path);
        lock (Gate)
        {
            return Counts.TryGetValue(full, out int count) && count > 0;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (Gate)
        {
            foreach (string path in _paths)
            {
                int count = Counts[path] - 1;
                if (count == 0)
                {
                    Counts.Remove(path);
                }
                else
                {
                    Counts[path] = count;
                }
            }
        }
    }
}
