# Storage service v1 contract

## Ownership and connection boundary

The production service owns the only SQLite writer/checkpointer connection for its entire lifetime. The connection uses `Pooling=false`. A process-local owner registry and an open `storage-service.writer.lock` file reject a second service for the same store. The legacy public `StorageDatabase.Open` path acquires and retains the same writer lock, so it cannot open or invoke public writers while a service owns the store; the service alone uses an internal already-owned open path. Disposal releases the handle but retains the lock file; it never deletes, moves, or overwrites spool, chunk, temporary, corrupt, or lock files. UI and query callers receive Storage-native results and never open another checkpoint-capable connection. A commit or checkpoint failure stops new acceptance and surfaces `StorageServiceHealth` with an `IntegrityFault` that cannot be resumed automatically.

The lifecycle is `Created`, `Running`, `Paused`, `IntegrityFault`, `Stopped`, and `Disposed`. User pause, session lock, and suspend are stacked flags. The first flag closes ingress and settles every open keyboard hold; later flags only stack, and only removal of the last flag returns to `Running`. Device removal settles only that device while preserving the current running or paused state. Calibration change settles every hold, commits the replacement calibration, and preserves that state. Stop settles every hold and enters `Stopped`. SQLite, monotonic-order, time-zone, or boundary failures fail closed.

`StorageDurableWatermark.Scope` is `SQLiteCommitted`. `CommitSequence` advances only after a transaction with a real persistent change commits: keyboard aggregate deltas or a new calibration segment in this slice. `KeyboardAggregateCommitSequence` advances only when that transaction contains keyboard deltas. The two scalar ordinals are stored in the existing `store_metadata` table and survive reopen; no schema v4 or event history is required. This watermark does not claim physical power durability, device release evidence, or that accepted in-memory work is committed.

## Device identity

`StorageDeviceObservation` is owned by Storage and has no reference to `InputTracker.Input`. The catalog hashes normalized interface and hardware identity material before persistence; raw interface paths, hardware identifiers, serial numbers, and container identifiers are not stored in SQLite.

The public kind enum preserves the existing schema encoding: mouse is `0` and keyboard is `1`. Legacy confidence `0` is exposed as `Unknown`; new observations use transient-interface or persistent-hardware confidence.

An unchanged interface fingerprint may reuse its prior persistent GUID only when the observed hardware fingerprint is unchanged. A changed interface may reuse a GUID only when the observation contains a serial number and is not Bluetooth, Remote Desktop, or virtual. A container identifier strengthens the fingerprint but never substitutes for a missing serial number. Missing serial numbers and those three transport/device classes remain explicitly ambiguous and receive transient-interface confidence; a changed interface therefore creates another GUID rather than claiming permanence.

Hardware ID order and casing do not affect matching. A reused interface with changed hardware fails closed. Device rows with unknown kind, confidence, or ambiguity bits fail closed at the common read boundary; migrations do not rewrite or silently repair those rows. Alias is deliberate user-authored visible metadata stored verbatim for display, but it never participates in identity matching. Interface paths, hardware IDs, serials, and container IDs remain hash-only identity inputs.

Automatic merge and split are not part of this checkpoint. Conflicting persistent mappings fail closed and are recorded in `BLOCKED.md`; no device history is rewritten.

## Keyboard privacy and aggregation

The only durable keyboard identity is device GUID plus packed physical make code and E0/E1 prefix bits. Schema v3 exposes generated `make_code`, `is_e0`, and `is_e1` columns without adding text, VKey, message, ordering, exact event time, window, focus, or process data. Daily rows contain capture-local date, the explicit Windows time-zone ID used at capture, press count, and aggregate hold duration.

The Storage-native transition contains only device GUID, make code, E0/E1, down/up, monotonic timestamp, and calibration session GUID. E0 and E1 together fail closed. A first down opens an in-memory hold and adds one pending press; repeated down and orphan up are ignored. Up adds pending duration rows and closes the in-memory hold. Acceptance and pending state do not advance the SQLite-committed watermark.

Keyboard changes commit at the earliest of 32 pending transitions, 64 distinct pending daily rows, an actively scheduled 250 ms window, explicit checkpoint, or a lifecycle boundary. The timer commits only already formed pending deltas: wall-clock/timer observations never advance the ordered event cursor and never synthesize duration for an open hold. A later queued up whose monotonic timestamp follows its down therefore remains valid even if the timer ran at a newer wall-clock observation. Daily duration splitting is lazy and each SQLite transaction contains at most 64 distinct rows. These fixed conservative limits preserve the B-006 bounded batch decision and avoid both a per-key SQLite transaction/fsync regression and unbounded materialization.

A single long up may require several 64-row transactions. Every successful transaction advances and publishes only its exact durable prefix. If a later transaction fails, the service enters `IntegrityFault`, rejects all later writes, never retries the remaining in-memory suffix, and reopen exposes only the already committed prefix without gaps inside that prefix or double counting. The watermark does not claim that the entire transition was atomic; callers see the write fail and must stop upstream acceptance.

Press count belongs to the local date obtained by mapping the first down through its calibration. Hold duration uses the calibrated UTC interval as truth. Calibration frequency must match the active monotonic clock, and reusing a clock session identifier with a different persisted frequency fails closed instead of being ignored. For each local date, Storage converts the explicit Windows time-zone interval `[local midnight, next local midnight)` to UTC and intersects it with the hold interval. Duration is allocated in UTC microseconds, so Pacific spring and fall days contribute 23 and 25 hours respectively; UTC and `China Standard Time` use the same algorithm. IANA IDs, unavailable Windows IDs, invalid local intervals, and time moving backwards fail closed.

Open holds are never persisted as exact events. `Dispose` without `Stop` does not synthesize an up: already committed press and duration aggregates survive, while the uncommitted tail after the last successful batch may be lost. `Stop` is the normal hold-settlement path. This is an explicit crash-loss boundary, not a durability claim.

## Read contracts reserved for the next checkpoint

Period queries use an explicit Windows time-zone ID and produce non-empty half-open UTC ranges. ISO weeks start on Monday. Keyboard results are per device and physical key. Mouse results are strict, unsampled, per-device sequence streams over a UTC range; monotonic timestamps are mapped through their persisted calibration segment. The implementation must stream records and must not materialize a million-event range as one object graph.

This A checkpoint implements the single-owner lifecycle and keyboard ingestion only. Mouse ingestion and every read-query/export implementation remain later checkpoints and are not implied by this contract.
