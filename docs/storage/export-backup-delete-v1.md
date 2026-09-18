# Storage export, backup, restore, and deletion v2

## Export

- Keyboard CSV contains only persistent device GUID, user-provided alias, physical make code/E0/E1, press count, and hold duration. It never exports text, key order, windows, processes, focus, or application identity.
- Keyboard heatmap PNG is a deterministic, lossless RGBA PNG produced with BCL code only. Each cell prints the physical make code and E0/E1 prefix using a built-in bitmap font; foreground switches between black and white according to cell luminance.
- Export destinations are relative to the program directory, must not already exist, and are written through a new temporary file followed by an atomic rename. Existing or newly encountered reparse points fail closed before data is written through them.

## Backup and restore

- A backup is a `.itbackup` ZIP with format version 1, the current storage schema version, and a manifest containing every declared file's exact length and SHA-256.
- SQLite is copied with the SQLite online backup API. Only top-level committed spool and chunk artifacts are copied. Every staged backup is reopened through `StorageService` before the archive is published.
- Backup is allowed only while Storage is Created, Paused, or Stopped and has no open holds, pending keyboard rows, or buffered mouse data.
- Restore accepts only a contained, non-reparse backup file. Before extraction it enforces at most 100,000 payload entries, 64 GiB per entry, 512 GiB total uncompressed bytes, a 1000:1 expansion ceiling with 64 MiB slack, and at least 2 GiB free-space reserve. It then extracts only the allowlisted database/spool/chunk paths, checks lengths, SHA-256, format/schema versions, and reopens the staged store before touching live data.
- Restore acquires the exclusive Storage writer lock before recovery or archive validation and creates a program-directory automatic pre-restore backup before swapping live files. The exact database/spool/chunk move plan is recorded in a write-through journal with a continuously persisted completed-move watermark. Any controlled failure rolls back the completed prefix; a later `StorageService.Open` obtains the same writer lock and rolls back a journal left by process termination before opening SQLite. Recovery failure is fail-closed and retains the journal for diagnosis.
- The restore commit point is deletion of the operation journal after the installed store reopens successfully. Resolved staging and rollback directories are then removed only after every traversed object has passed reparse checks.
- Unknown live files are not claimed as committed backup data and are not silently deleted by restore.

## Daily aggregate range deletion

- Mouse trajectory collection and playback remain removed. The user explicitly chooses either one keyboard's daily key statistics or one mouse's daily button statistics; mouse movement, coordinates, wheel data, and trajectory files are not restored or created.
- `PreviewDeletionAsync` accepts a UTC half-open range `[start,end)`. It resolves every overlapping `capture_local_date` with that row's stored Windows time-zone identifier. Execution is available only when every overlapping local-day bucket lies wholly inside the selected range; a partial day or mixed-zone boundary fails closed instead of estimating counts.
- The preview fingerprint covers the device, range, durable ordinals, every targeted physical-key or mouse-button row, its local date/time zone, press count, and keyboard hold duration when applicable. The 64-character confirmation token is tied to that fingerprint. Any data change after preview makes execution fail and requires a new preview.
- Execution requires Created, user-paused, or Stopped Storage with no accepted writes, open holds, or pending aggregates. The App narrows this to an explicit user pause and a final `Yes/No` warning.
- Before deletion, InputTracker creates a complete `.itbackup` inside the program directory. It then deletes only the confirmed device's fully contained daily buckets in one SQLite transaction and verifies the exact row count before commit. A process failure before commit leaves the database unchanged; after commit the pre-delete backup remains available for offline restore.
- An empty range, partial local day, unsupported device, stale preview, wrong token, unsafe lifecycle state, backup failure, or changed row count performs no deletion. Proportional subtraction is forbidden.
