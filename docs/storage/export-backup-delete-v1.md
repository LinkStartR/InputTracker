# Storage export, backup, restore, and deletion preview v1

## Export

- Keyboard CSV contains only persistent device GUID, user-provided alias, physical make code/E0/E1, press count, and hold duration. It never exports text, key order, windows, processes, focus, or application identity.
- Keyboard heatmap PNG is a deterministic, lossless RGBA PNG produced with BCL code only. Each cell prints the physical make code and E0/E1 prefix using a built-in bitmap font; foreground switches between black and white according to cell luminance.
- Export destinations are relative to the program directory, must not already exist, and are written through a new temporary file followed by an atomic rename. Existing or newly encountered reparse points fail closed before data is written through them.

## Backup and restore

- A backup is a `.itbackup` ZIP with format version 1, storage schema version 4, and a manifest containing every declared file's exact length and SHA-256.
- SQLite is copied with the SQLite online backup API. Only top-level committed spool and chunk artifacts are copied. Every staged backup is reopened through `StorageService` before the archive is published.
- Backup is allowed only while Storage is Created, Paused, or Stopped and has no open holds, pending keyboard rows, or buffered mouse data.
- Restore accepts only a contained, non-reparse backup file. Before extraction it enforces at most 100,000 payload entries, 64 GiB per entry, 512 GiB total uncompressed bytes, a 1000:1 expansion ceiling with 64 MiB slack, and at least 2 GiB free-space reserve. It then extracts only the allowlisted database/spool/chunk paths, checks lengths, SHA-256, format/schema versions, and reopens the staged store before touching live data.
- Restore acquires the exclusive Storage writer lock before recovery or archive validation and creates a program-directory automatic pre-restore backup before swapping live files. The exact database/spool/chunk move plan is recorded in a write-through journal with a continuously persisted completed-move watermark. Any controlled failure rolls back the completed prefix; a later `StorageService.Open` obtains the same writer lock and rolls back a journal left by process termination before opening SQLite. Recovery failure is fail-closed and retains the journal for diagnosis.
- The restore commit point is deletion of the operation journal after the installed store reopens successfully. Resolved staging and rollback directories are then removed only after every traversed object has passed reparse checks.
- Unknown live files are not claimed as committed backup data and are not silently deleted by restore.

## Deletion preview and current block

- `PreviewDeletionAsync` is read-only. It accepts one device and a UTC half-open range `[start,end)`, validates the complete committed mouse snapshot when applicable, and returns exact mouse record count or overlapping keyboard aggregate row count plus a state fingerprint and confirmation token.
- `ExecuteDeletion` always fails closed in format v4. The token is checked in fixed time first, but no user data is modified.
- Mouse v4 durability is one device-global continuous sequence prefix. Removing an interior time range would introduce a gap; renumbering would rewrite original identity and future append/recovery semantics.
- Keyboard data is stored per capture-local day. An arbitrary UTC sub-day deletion cannot recover the exact contribution from daily counts and durations.
- Safe execution therefore requires a later versioned rewrite protocol that can preserve mouse ordering/watermark recovery and finer-grained keyboard deletion semantics, with pre-delete backup, crash rollback, and Acceptance red-to-green evidence. Whole-block deletion or proportional keyboard subtraction is forbidden.
