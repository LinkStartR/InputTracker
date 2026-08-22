# Mouse Trajectory Playback

Status: design only. The existing bounded dashboard path is an overview and must not be presented as full raw playback.

## Existing Storage evidence

`StorageService.StreamMouseAsync` captures an internal fixed durable watermark, ordered committed spool segments and the requested half-open UTC range. Before yielding, the query validates every segment, chunk equivalence, sequence coverage, hashes and non-decreasing UTC mapping. It then yields every matching `StorageMouseRecord` in sequence order.

This is sufficient for a full forward replay from the beginning of one query snapshot. The public API does not expose the captured watermark, a stable `(UtcMicroseconds, Sequence)` cursor, calibration/session boundaries or a way to reopen the same immutable snapshot. Therefore arbitrary seek and seamless resume must remain disabled until a separate Storage seam proves zero duplicate and zero omission, including equal UTC timestamps.

## Honest display contracts

- `Trajectory overview`: fully enumerates Storage but merges drawing data into at most 2048 time buckets. It is explicitly labelled as an overview.
- `Full playback`: processes every yielded raw record in order. UI drawing may batch frames and keep bounded tail geometry, but processing counts, rolling hash, coordinate integration, button/wheel state and sequence checks include every record.
- If the renderer falls behind, it reports catching up and applies backpressure. It does not silently drop input points to meet UI or performance thresholds.

## State machine

```text
Empty -> Loading -> Ready -> Playing <-> Paused -> Completed
                     |          |
                     +-> Seeking+
selection/refresh: Cancelling -> Loading
errors: Faulted
shutdown: Disposed
```

Every device, period, time-zone or explicit refresh change increments a generation, cancels the old reader and clears old-device geometry before the new result can appear. Old continuations check both cancellation and generation before publishing. Cancellation is not a fault.

Pause is a reason set containing at least `User`, `WindowHidden` and `Seeking`. Removing one reason never clears another. Hiding the window stops the UI playback clock but not live capture; the bounded producer queue applies backpressure instead of accumulating Dispatcher work.

## Bounded architecture

- A producer reads the immutable Storage snapshot in sequence order into a bounded queue with capacity no greater than 4096.
- A monotonic playback clock advances by record UTC at `0.25x`, `0.5x`, `1x`, `2x` or `4x`. Changing speed resets the clock baseline without jumping the cursor.
- One UI frame consumes a bounded batch. Dispatcher work is per frame, never per raw record.
- The active tail contains no more than 2048 render points. Completed geometry is flattened into fixed-memory drawing data while total processed count and rolling hash continue to cover every record.
- Snapshot diagnostics expose generation, selection, cursor, processed count, sequence/order failures, queue depth, tail size, speed, pause reasons and catching-up state.

## Segmentation and coordinates

- Relative motion is a local integrated path only.
- Absolute primary-desktop and absolute virtual-desktop motion require their matching calibration before conversion to display coordinates.
- Motion kind, virtual-desktop flag, device generation, calibration session, explicit pause/resume, stop and any sequence/data gap begin a new segment.
- Missing calibration retains raw values and displays `absolute coordinates not calibrated`; it never fabricates desktop coordinates.

The current public record exposes motion kind and virtual-desktop state but not calibration/pause/stop segment metadata. A separate Storage snapshot contract must expose a safe boundary marker without adding keyboard text, window, process or application data.

## Required Storage seam

- Open an immutable mouse playback snapshot with device, UTC range and captured durable watermark.
- Stream from the beginning or a stable `(UtcMicroseconds, Sequence)` cursor within that exact snapshot.
- Preserve equal-timestamp records by sequence and reject cursors outside the snapshot.
- Expose only mouse-safe segment boundary/calibration identifiers required for projection; do not expose raw device interface identity.
- Closing the snapshot releases only read resources and never affects live capture or durable data.

Until this seam exists, UI may implement forward-only full playback and a read-only progress indicator. It must not enable arbitrary seek or claim seamless resume.

## Automated gates

- State transitions, pause-reason composition, generation races, cancellation and fault terminal states.
- One million records: produced = processed, rolling hash and sequence order close exactly; queue <=4096, tail <=2048 and Dispatcher submissions stay frame-bounded.
- Relative/absolute/virtual/calibration/pause/stop/gap segmentation and missing-calibration disclosure.
- Two-device isolation and stale-generation rejection.
- Equal-UTC cursor reopen with zero duplicate and zero omission after the Storage seam exists.
- Speed changes preserve a continuous cursor; hiding creates no unbounded work and does not pause capture.
- The existing two-second dashboard summary refresh does not cancel or restart playback.

Real two-mouse replay, absolute/virtual desktop devices, 1000 Hz for 30 minutes and CPU, memory and UI latency thresholds remain terminal hardware evidence.
