# Storage read query v1 contract

## Scope and ownership

This checkpoint adds read-only period and trajectory queries to the existing single-owner `StorageService`. It does not add a second SQLite connection, schema migration, export, deletion, backup, cleanup, App adapter, or hardware claim. A query captures its SQLite directory, calibration, and device-watermark snapshot while holding the service gate, then releases the gate before immutable file validation and streaming. The snapshot excludes every later committed tail.

## Explicit period semantics

`StoragePeriodResolver` accepts only an explicit, available Windows `TimeZoneInfo` identifier. Blank identifiers, IANA identifiers containing `/`, unknown period kinds, unavailable zones, and invalid or ambiguous local boundaries fail closed. Day, ISO Monday week, month, and year use adjacent local calendar boundaries and convert those boundaries to a non-empty UTC half-open range `[start,end)`. The Pacific spring and fall days therefore last 23 and 25 UTC hours; `China Standard Time` and `UTC` use the same algorithm without consulting the machine's later current zone.

Keyboard period queries include only durable `keyboard_daily` rows whose `time_zone_id` exactly equals the selected capture zone and whose local date is in `[localStart,localEnd)`. Results aggregate by persistent device and physical make/E0/E1 identity with checked non-negative counts. The result separately lists all capture-zone identifiers present for the selected keyboard scope. Daily privacy aggregates cannot be accurately reprojected from another capture zone, so the query never merges those rows and the UI must label the selected capture zone rather than claim a cross-zone total.

## Authoritative mouse stream

Mouse queries require one persistent mouse GUID and a non-empty UTC range. The only count source is the exact contiguous `[1..device watermark]` prefix in `mouse_spool_segments`. Directory gaps, overlaps, malformed rows, missing files, path escapes, reparse points, invalid headers or record CRCs, length/file-hash/canonical-hash mismatches, calibration/session mismatches, backward UTC mapping, and any non-equivalent chunk representation fail closed.

Every cataloged chunk inside the captured watermark must map to exactly one spool range. If present, its catalog fields, final file, decoded records, and canonical SHA-256 must be point-for-point equivalent. A chunk is a secondary integrity witness and is never emitted as a second copy. A valid spool does not require a chunk.

The first asynchronous enumeration step performs a complete validation pass on a worker thread before returning the first record. It discards each decoded segment after validation. The second pass rereads one immutable segment at a time, maps monotonic timestamps through the persisted calibration, applies the UTC half-open filter, and yields unsampled records in device-global sequence order. Cancellation is observed during both passes. This deliberately trades additional read I/O for data correctness, fail-before-partial-output, bounded managed memory, and UI responsiveness.

## Evidence and limits

Ordinary tests cover four periods, ISO Monday, Pacific 23/25-hour days, China/UTC, capture-zone isolation, keyboard device/key isolation, exact spool/chunk equivalence, corruption before first output, snapshot tail exclusion, and invalid inputs. The scale test streams 1,000,000 committed records with exact count and rolling hash while bounding observed managed-memory growth to 128 MiB. This is software evidence only; it is not the required two-device or physical 1000 Hz terminal acceptance.
