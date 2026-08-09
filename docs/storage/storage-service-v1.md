# Storage service v1 contract

## Ownership and connection boundary

The production service owns the only SQLite writer/checkpointer connection for its entire lifetime. The connection uses `Pooling=false`; UI and query callers receive Storage-native results and never open another checkpoint-capable connection. A commit or checkpoint failure must stop new acceptance and surface `StorageServiceHealth` with an `IntegrityFault`.

Pause, session lock, suspend, device removal, calibration change, and stop are fail-closed boundaries. Later ingestion work must seal and durably publish all accepted keyboard aggregates and mouse chunks before acknowledging a boundary. Resume requires a new calibration segment when the monotonic-to-UTC mapping may have changed.

## Device identity

`StorageDeviceObservation` is owned by Storage and has no reference to `InputTracker.Input`. The catalog hashes normalized interface and hardware identity material before persistence; raw interface paths, hardware identifiers, serial numbers, and container identifiers are not stored in SQLite.

The public kind enum preserves the existing schema encoding: mouse is `0` and keyboard is `1`. Legacy confidence `0` is exposed as `Unknown`; new observations use transient-interface or persistent-hardware confidence.

An unchanged interface fingerprint may reuse its prior persistent GUID only when the observed hardware fingerprint is unchanged. A changed interface may reuse a GUID only when the observation contains a serial number and is not Bluetooth, Remote Desktop, or virtual. A container identifier strengthens the fingerprint but never substitutes for a missing serial number. Missing serial numbers and those three transport/device classes remain explicitly ambiguous and receive transient-interface confidence; a changed interface therefore creates another GUID rather than claiming permanence.

Hardware ID order and casing do not affect matching. A reused interface with changed hardware fails closed. Device rows with unknown kind, confidence, or ambiguity bits fail closed at the common read boundary; migrations do not rewrite or silently repair those rows. Alias is deliberate user-authored visible metadata stored verbatim for display, but it never participates in identity matching. Interface paths, hardware IDs, serials, and container IDs remain hash-only identity inputs.

Automatic merge and split are not part of this checkpoint. Conflicting persistent mappings fail closed and are recorded in `BLOCKED.md`; no device history is rewritten.

## Keyboard privacy and aggregation

The only durable keyboard identity is device GUID plus packed physical make code and E0/E1 prefix bits. Schema v3 exposes generated `make_code`, `is_e0`, and `is_e1` columns without adding text, VKey, message, ordering, exact event time, window, focus, or process data. Daily rows contain capture-local date, the explicit Windows time-zone ID used at capture, press count, and aggregate hold duration.

Later ingestion tests must freeze the policy for duplicate transitions, midnight and DST boundaries, and crash-open holds before the service accepts production keyboard input.

## Read contracts reserved for the next checkpoint

Period queries use an explicit Windows time-zone ID and produce non-empty half-open UTC ranges. ISO weeks start on Monday. Keyboard results are per device and physical key. Mouse results are strict, unsampled, per-device sequence streams over a UTC range; monotonic timestamps are mapped through their persisted calibration segment. The implementation must stream records and must not materialize a million-event range as one object graph.

This checkpoint defines the Storage-native DTO surface only. Ingestion and query implementations begin only after the main task reports the focused Release build and all non-`StorageSpike` tests green.
