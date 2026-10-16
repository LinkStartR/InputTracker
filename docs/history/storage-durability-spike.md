# Storage durability spike

> Status: historical spike evidence, not the current product contract. The active service records privacy-preserving keyboard and mouse-button daily aggregates; it neither appends the mouse CRC spool nor publishes a mouse trajectory watermark. Current behavior is specified by [Storage service](../storage/storage-service-v1.md), [queries](../storage/storage-query-v1.md), and [data operations](../storage/export-backup-delete-v2.md).

## Scope and decision

This completed milestone measured the minimum durability contract and reusable storage foundation. Its strategy and measurements remain engineering evidence only; they do not imply current trajectory, query, export, backup/restore, or deletion behavior.

The measured direction was strategy C: keyboard aggregates were committed in bounded SQLite transactions while every mouse event was appended without sampling to a fixed-size CRC32C spool. A lifecycle boundary made that experimental spool durable with `Flush(true)` before a `synchronous=FULL` SQLite transaction recorded its directory watermark. Strategy A (one FULL SQLite autocommit per event) and strategy B (bounded SQLite transactions) were comparison paths.

`SinkAcknowledged` means that the storage sink accepted the event into its current SQLite transaction or appended the complete mouse record to the process-visible spool. It is not an fsync claim. The writer's internal `SpoolFlushResult` reports only that the file flush completed; it is not a final durability watermark and is not exposed to ordinary callers. `PowerDurable` is published only by `StorageDatabase.CommitSpoolBoundary` after both file flush and SQLite directory commit succeed, and remains a storage-layer guarantee rather than evidence from a physical power-cut test.

## SQLite foundation

- The package lock is `Microsoft.Data.Sqlite 10.0.10`; no additional package was added. Compression, CRC, and hashing use the BCL or repository code.
- Every product connection uses `Pooling=false`. One owner performs writes and checkpoints through one connection.
- On open, the connection requires `journal_mode=WAL`, `synchronous=FULL`, `foreign_keys=ON`, `secure_delete=ON`, and a 5000ms busy timeout.
- Schema v1 and the v2 mouse chunk catalog migration are each applied in a transaction and recorded with a SHA-256 checksum. A future `user_version` or either checksum mismatch fails closed.
- Program data lives below the executable directory in `data/storage`; the database, spool, and future chunk directories do not escape that root.

The final spike queried native SQLite `3.49.1`. That version is inside the officially reported WAL-reset affected range rather than a fixed line. The current single writer/checkpointer connection and `Pooling=false` avoid the known multi-connection concurrent writer/checkpoint trigger, but do not make the underlying version defect disappear. Production release remains blocked on an authorized native dependency update to a fixed line or an explicit reviewed risk decision.

## Privacy allowlist

Keyboard persistence is limited to:

- device ID;
- physical key;
- capture-local date and the explicit time-zone ID used to derive it;
- press count;
- hold duration in microseconds.

Keyboard event sequence, exact event timestamp, text/character, virtual key, key-down/up history, window, process, focus, and application identity are forbidden. Mouse spool records may preserve device-local sequence, monotonic timestamp, movement deltas, motion mode, virtual-desktop flag, button changes, and vertical/horizontal wheel deltas.

## Time basis

`CapturedInput.Timestamp` is a `Stopwatch.GetTimestamp()` value. It must never be interpreted as UTC, Unix time, `DateTime` ticks, or `TimeSpan` ticks.

At sink/session start, Storage takes a sandwich calibration sample: monotonic-before, UTC, monotonic-after. It persists the monotonic midpoint, `Stopwatch.Frequency`, UTC microseconds, sampling uncertainty, session ID, and Windows time-zone ID. Conversion uses the frequency and 128-bit intermediate arithmetic. A new calibration segment is required after resume, an observed wall-clock discontinuity, or a time-zone change. Local-date aggregation uses the segment's explicit zone, not the machine's later current zone.

Because Input Core and Storage currently run in the same process and therefore share the same `Stopwatch` domain and static frequency, Storage can establish the mapping reliably without changing the current event contract. A later compatible Input Core addition should expose a capture-session clock descriptor or checkpoint if capture can cross a process boundary, be replayed after restart, or use another monotonic source. Such a topology change is blocked without frequency, session/origin, and calibration metadata.

## Mouse spool v1

Each record is exactly 44 little-endian bytes:

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 4 | magic `MSPR` |
| 4 | 2 | version `1` |
| 6 | 2 | record length `44` |
| 8 | 8 | sequence |
| 16 | 8 | monotonic timestamp |
| 24 | 4 | delta X |
| 28 | 4 | delta Y |
| 32 | 1 | motion kind |
| 33 | 1 | virtual-desktop flag |
| 34 | 2 | button changes |
| 36 | 2 | vertical wheel delta |
| 38 | 2 | horizontal wheel delta |
| 40 | 4 | CRC32C of bytes 0–39 |

Sequence must be contiguous. A complete record with a header, CRC, or sequence failure is corruption and recovery fails closed without truncating it. An incomplete final record is the only automatically repairable case: recovery retains and optionally fsyncs the complete valid prefix, truncates the tail, and reports exactly one lost record. Exceptions are propagated.

This record spool was the append/durability foundation for the retired trajectory experiment. Its compressed-trajectory follow-on specification was removed with that feature; any prior implementation detail is available only through Git history. The former B-008 transition-cleanup decision is retired with the trajectory feature and is not a current release gate.

## Atomic ordering and crash seams

The only permitted boundary order is:

1. finish appending every accepted mouse record with its per-record CRC;
2. call `FileStream.Flush(flushToDisk: true)` and capture file length/record count;
3. begin a SQLite transaction and upsert that spool directory watermark;
4. commit the SQLite transaction;
5. publish the new durable watermark to callers.

If step 2 succeeds but the SQLite transaction fails, the file remains recoverable but no public `SpoolDurableWatermark` is returned and no catalog row exists. The writer never advances or exposes a separate final `PowerDurable` value.

SQLite metadata must never move ahead of the flushed file. A failure before step 2 leaves an unpromised tail that startup scans as a valid prefix or precisely reports as incomplete. A failure after step 2 but before step 4 may leave more valid file records than the catalog advertises; startup scans the spool and may rebuild a conservative directory entry, but the pre-crash published durable watermark remains the lower catalog value. A failure after step 4 must reopen with the complete cataloged prefix.

For keyboard aggregates, transaction rollback before commit leaves no partial aggregate. Commit completion makes the entire batch visible after reopen. No per-event keyboard order or precise time is introduced to improve recovery.

## Measurements and count semantics

The machine-readable result is written below ignored `.tools/storage-spike/<run>/storage-durability-spike.json`. It records the dependency graph, actual native SQLite version, workload, durability boundary, accepted/sink-acknowledged/SQLite-committed/spool-appended/power-durable/recovered/lost/corrupt counts, events/s, p50/p95 acknowledgement latency, total bytes, and bytes/event.

The spike runs 100,000 mixed keyboard/mouse events through A, B, and C, plus 30,000 unsampled mouse events as a 1000Hz-for-30-seconds equivalent load through C. This is a software load test, not the required 30-minute physical 1000Hz terminal run. `hardwareTerminalStatus` remains `not-run` and `releaseEligible` remains false.

Final semantic-split source snapshot: .NET runtime `10.0.10`, 16 logical processors, native SQLite `3.49.1`.

| Strategy / workload | accepted / ack / SQLite / durable / recovered | spool | lost / corrupt | events/s | p50 / p95 ack | bytes/event | elapsed | boundary flush |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| A autocommit, mixed 100k | 100000 / 100000 / 100000 / 100000 / 100000 | 0 | 0 / 0 | 419.71 | 2426.1us / 2997.3us | 13.025 | 238262.33ms | per-event SQLite FULL commit |
| B batch 1024, mixed 100k | 100000 / 100000 / 100000 / 100000 / 100000 | 0 | 0 / 0 | 96621.58 | 6.9us / 8.4us | 13.025 | 1034.97ms | batch SQLite FULL commit |
| C spool, mixed 100k | 100000 / 100000 / 100000 / 100000 / 100000 | 50000 | 0 / 0 | 114469.09 | 5.2us / 10.6us | 22.369 | 873.60ms | 11ms |
| C spool, mouse 30k equivalent | 30000 / 30000 / 30000 / 30000 / 30000 | 30000 | 0 / 0 | 185268.10 | 3.3us / 3.5us | 45.229 | 161.93ms | 10ms |

The source snapshot originally used `.tools/storage-spike/20260805-104501-272-18816/storage-durability-spike.json`, which is no longer present in this workspace. The first full run also records a red seam: after 4m15s, the A path completed but the benchmark incorrectly attempted a batch tail commit through a null transaction. Restricting the tail commit to `batchSize > 1` preserved every workload and durability setting. A 4m25s pre-seam green run was retained as historical performance evidence; the final semantic-split snapshot passed in 4m00s.

The historical watermark split had its own ordinary red→green seam. Those trajectory writer results are retained only as prior engineering evidence; the current product no longer initializes that writer or exposes its write/read/playback APIs.

## B-006 decision and remaining release gates

B-006 remains historical evidence for the retired trajectory writer. Current production uses privacy-preserving keyboard aggregation and direct mouse-button daily aggregation; it does not create a mouse CRC spool or publish a mouse trajectory watermark.

Physical sudden-power evidence and the native SQLite WAL-reset version gate remain separate release gates for active aggregate storage. The former compressed-block and 1000Hz trajectory gates were retired with the feature; current acceptance instead fails if mouse-button capture creates a trajectory artifact.
