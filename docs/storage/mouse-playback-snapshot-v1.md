# Mouse Playback Snapshot v1

Status: API and migration design only. No Storage schema or product code has been changed.

## Goal

Expose one immutable, integrity-validated, device-isolated mouse snapshot that can stream from the beginning, seek to a UTC target and reopen from an exact cursor without duplicates or omissions. The API must preserve equal UTC timestamps by device-global sequence and must not expose raw interface paths, hardware IDs, keyboard data, windows, processes or applications.

## Public contract

```csharp
public sealed record StorageMousePlaybackSnapshotInfo(
    Guid SnapshotId,
    Guid DeviceId,
    StorageUtcRange UtcRange,
    long DurableWatermark,
    long RecordCount,
    StorageMousePlaybackCursor? First,
    StorageMousePlaybackCursor? Last);

public readonly record struct StorageMousePlaybackCursor(
    Guid SnapshotId,
    long Sequence,
    long UtcMicroseconds);

public enum StorageMousePlaybackBoundaryReason
{
    RangeStart,
    DurationLimit,
    EventLimit,
    CanonicalSizeLimit,
    DeviceBoundary,
    CalibrationBoundary,
    PauseBoundary,
    ExplicitFlush,
    LegacyUnknown,
    RecoveryUnknown
}

public readonly record struct StorageMousePlaybackRecord(
    StorageMouseRecord Record,
    bool StartsNewSegment,
    StorageMousePlaybackBoundaryReason BoundaryReason,
    StorageMousePlaybackCursor Cursor);

public sealed class StorageMousePlaybackSnapshot : IAsyncDisposable
{
    public StorageMousePlaybackSnapshotInfo Info { get; }
    public IAsyncEnumerable<StorageMousePlaybackRecord> StreamFromAsync(
        StorageMousePlaybackCursor? inclusiveCursor,
        CancellationToken cancellationToken = default);
    public ValueTask<StorageMousePlaybackCursor?> SeekAsync(
        long targetUtcMicroseconds,
        CancellationToken cancellationToken = default);
}
```

`StorageService.OpenMousePlaybackSnapshotAsync(deviceId, range, token)` performs full fail-closed validation before returning the snapshot. It captures the exact durable device watermark and ordered directory/chunk rows under the existing service lock, then validates every referenced file, sequence prefix, hash, chunk equivalence, calibration and non-decreasing UTC mapping on a worker thread. No record may be returned before later-segment corruption is ruled out.

The first cursor returned by `SeekAsync(target)` is the first tuple ordered by `(UtcMicroseconds, Sequence)` with UTC greater than or equal to the target. `StreamFromAsync(cursor)` is inclusive and accepts only a cursor carrying the same `SnapshotId` and an exact sequence/UTC tuple in the selected range. Cross-snapshot, missing, altered or out-of-range cursors fail closed. Equal UTC timestamps are never collapsed.

## Snapshot lifetime

- The snapshot owns an internal read lease over the exact spool/chunk paths in its directory image.
- Current Storage never deletes committed spool/chunk files, so the first implementation only tracks the lease. Any future B-008 cleanup must refuse to move or delete a leased file.
- Live capture may append new records, but they are above the captured durable watermark and never enter the existing snapshot.
- Disposing the snapshot cancels active enumeration, releases its read lease and does not stop capture, checkpoint or Storage.
- A snapshot does not keep a SQLite reader or writer transaction open during playback.

## Boundary persistence migration

The current `MouseChunkBuilder` produces `MouseChunkSealReason`, but `MouseIngestion.Publish` discards it when publishing the spool directory row. Crash recovery can observe a self-describing spool but cannot reconstruct the original lifecycle reason. The v1 playback contract therefore requires a versioned Storage schema migration:

- Add `mouse_spool_segments.seal_reason INTEGER NOT NULL` with an exact enum check.
- Normal publication passes the builder reason into the directory commit.
- Existing rows migrate to `LegacyUnknown`.
- A crash-recovered unpublished spool uses `RecoveryUnknown` unless a future spool format durably carries the reason.
- Every spool segment boundary starts a new display segment regardless of reason. The reason is diagnostic; unknown values never permit path continuity.
- If a selected UTC range begins inside a segment, the first included record uses `RangeStart`.

This migration does not rewrite spool/chunk payloads and does not change mouse event sequence identity. It must be atomic, checksummed and future-version fail closed under the existing schema rules.

## Internal implementation

- Extend the current internal query snapshot rather than create a second file decoder.
- Materialize only segment/chunk metadata and aggregate snapshot information. Do not retain mouse records in memory.
- Opening performs one validation/count scan. Streaming and seeking may rescan immutable files on a worker thread; correctness and bounded memory take priority over seek speed.
- Locate cursor ranges by segment sequence bounds, then verify the exact cursor tuple while scanning. Never trust UTC alone.
- Preserve existing half-open UTC filtering and device-kind validation.
- Cancellation never returns a partially verified snapshot as usable.

## Ordinary tests

- Snapshot excludes records committed after open and remains readable while live capture appends.
- One million records close exact count, sequence and rolling hash with bounded memory; no list of all records is retained.
- Same-UTC records seek and reopen in sequence order with zero duplicate and zero omission.
- Beginning, exact-middle, end and out-of-range seeks; inclusive cursor behavior is fixed.
- Cursor from another snapshot/device/range and forged sequence/UTC tuples fail closed.
- Corruption in the last segment fails during open before the snapshot is returned.
- Normal seal reasons round-trip; v4 migration yields `LegacyUnknown`; crash adoption yields `RecoveryUnknown`; every boundary starts a new segment.
- Relative, absolute-primary and absolute-virtual records retain every existing public mouse field.
- Snapshot dispose cancels enumeration, releases the lease and leaves live capture accepting.
- Future cleanup refuses leased paths without deleting or moving anything.
- Reflection and source scans prove no keyboard text, interface identity, window, process or application field crosses the public contract.

## Acceptance boundary

The UI may enable arbitrary seek only after these tests and App generation/cancellation tests are green. A process-crash test does not satisfy physical-power evidence. Real two-mouse, virtual desktop and 1000 Hz terminal tests remain mandatory.
