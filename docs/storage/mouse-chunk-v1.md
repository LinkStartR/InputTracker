# Mouse chunk v1

This slice adds the production mouse trajectory chunk format and its recoverable SQLite catalog. It preserves every spool event in strict sequence. It does not sample, aggregate, delete, move, or overwrite spool, chunk, temporary, or corrupt user data.

## Field contract

The device identifier is stored in the header. `CalibrationSegmentId` is the positive SQLite `time_calibration_segments.segment_id`; it is not an arbitrary chunk segmentation UUID. This foreign key binds every monotonic timestamp to the frequency, UTC anchor, uncertainty, and time-zone calibration needed for later date aggregation.

The canonical event payload contains, in order, sequence delta, monotonic timestamp delta, X/Y deltas, motion kind, virtual-desktop flag, all ten left/right/middle/X1/X2 down/up bits, signed vertical wheel, and signed horizontal wheel. Integer deltas use zigzag plus minimal unsigned varints. The reader rejects gaps, non-minimal varints, unknown bits, trailing payload, range overflow, count mismatch, and backward time.

The payload is compressed with BCL Brotli `CompressionLevel.Fastest`. This choice keeps background CPU bounded; it does not change the canonical hash or permit event loss.

## File layout

All integer fields are little-endian. Header size is 108 bytes.

| Offset | Size | Field |
| ---: | ---: | --- |
| 0 | 4 | magic `MCHK` |
| 4 | 2 | format version `1` |
| 6 | 2 | codec `1` (delta/zigzag/minimal-varint + Brotli) |
| 8 | 4 | header length `108` |
| 12 | 16 | chunk UUID |
| 28 | 16 | persistent device UUID |
| 44 | 8 | calibration segment INTEGER FK |
| 52 | 8 | first sequence |
| 60 | 8 | last sequence |
| 68 | 8 | first monotonic timestamp |
| 76 | 8 | last monotonic timestamp |
| 84 | 8 | event count |
| 92 | 8 | canonical payload length |
| 100 | 8 | compressed payload length |
| 108 | variable | compressed canonical payload |
| body end | 4 | CRC32C of compressed payload |
| +4 | 32 | SHA-256 of canonical uncompressed payload |
| +36 | 4 | end magic `MEND` |

The reader validates exact total length, version, codec, identifiers, positive calibration segment, sequence/count equality, timestamp range, compressed CRC32C, exact Brotli consumption/output, canonical SHA-256, decoded count, last values, and end magic before returning any records. Arithmetic overflow from untrusted bytes is converted to controlled invalid-data failure; system failures such as out-of-memory are not swallowed.

## Production sealing

`MouseChunkBuilder` retains every accepted event and seals at the first applicable boundary:

- 4096 events;
- 256 KiB canonical payload safety ceiling;
- one second in the supplied monotonic clock frequency;
- device change or explicit matching device removal;
- calibration-segment change or explicit matching segment close;
- pause;
- explicit stop/flush.

Crossing boundaries seal the prior batch and accept the current event into the next batch. Explicit device/segment methods only seal the currently matching single-builder buffer. Tests compare the builder's incremental canonical byte count with the real codec header and prove sequence concatenation has no loss, duplication, or reordering.

## Catalog and atomic publication

Schema v2 adds `mouse_chunks`. It stores only chunk/device/calibration identity, the generated relative filename, sequence/timestamp ranges, lengths, CRC/hash, creation time, and recovered flag. The schema enforces event-count equality and exact-range uniqueness. Product code additionally rejects any partial, contained, containing, or identical overlap for the same device and calibration segment.

The only commit order is:

1. encode to a same-directory `<chunk-id>.tmp` using create-new;
2. write the complete header/body/footer and call `Flush(true)`;
3. atomically rename to `<chunk-id>.mchunk` without overwrite;
4. begin the single-writer SQLite transaction, recheck foreign keys and sequence overlap, then insert the catalog row;
5. commit SQLite;
6. return `MouseChunkDurableWatermark`.

No final watermark is constructed before step 5. A database failure after rename leaves a valid final orphan and no catalog row. Recovery can index it later after all constraints become valid.

Catalog `relative_path` is untrusted input. Public reads and recovery require a single generated UUID filename, reject absolute paths, `..`, nested paths, and prefix-collision escapes using `GetFullPath`, and never use an invalid catalog value for file access. Diagnostics use controlled categories/messages and sanitized UUID filenames; they do not echo exception messages or absolute program/device paths.

## Recovery

Recovery retains every file in place.

- valid `.tmp`: report an uncommitted observation; do not publish or rename;
- corrupt/truncated `.tmp`: report `IntegrityFault`; retain;
- valid final without index: validate all bytes and metadata, precheck device/calibration/range, then insert in its own SQLite transaction;
- valid indexed final: require path containment plus exact header/catalog/length/CRC/hash match before counting durable;
- indexed missing/corrupt/mismatched final: report `IntegrityFault` and exclude from durable;
- overlapping catalog rows or overlapping orphan candidates: report every involved range and exclude them from durable/recovery;
- malformed catalog row: return a global fail-closed catalog fault with zero durable publication;
- failed orphan insertion: roll back only that orphan, report it, and continue independent valid candidates.

Repeated recovery is idempotent. The B-007 single writer/checkpointer and `Pooling=false` restriction remains in force because native SQLite 3.49.1 is still on the affected WAL-reset line.

## Next slice boundary

The next Storage slice, not this commit, may add the single-owner serialized ingestion/query service agreed with the App/UI adapter: storage-native DTOs, device observation, keyboard transitions, full mouse writes and lifecycle boundaries, health/watermark reads, device/range aggregates, strict unsampled mouse streaming, explicit-zone periods, and physical-key make-code/prefix encoding. Storage must continue not to reference Input, and UI must not open a second checkpoint-capable SQLite connection.
