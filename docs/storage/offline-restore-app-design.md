# Offline Restore App Integration

Status: design only. The current application validates a backup selection but must not execute a restore until every gate below is implemented and verified against disposable temporary stores.

## Safety boundary

- A restore requires an explicit user pause and a second confirmation. Session lock or suspend is not consent.
- The selected `.itbackup` must be a regular contained file under the program directory with no reparse point in its path.
- The active Raw Input and Storage owners must be fully released before the offline restore acquires `storage-service.writer.lock`.
- Success and failure are terminal for the current process. InputTracker must not reconstruct capture or automatically resume after a restore attempt.
- Range deletion uses its own pause, preview, backup, confirmation and SQLite transaction protocol; it does not reuse or weaken this offline-restore lifecycle.

## State machine

```text
IdleRunning
  -> UserPaused
  -> SelectionValidated
  -> FinalConfirmation
  -> RestoreLeaseAcquired
  -> Quiescing
  -> OwnerReleased
  -> RestoringOffline
  -> SucceededAwaitExit | FailedAwaitExit | RecoveryRequired
  -> ControlledExit
```

Before `FinalConfirmation`, cancellation returns to `IdleRunning` without stopping capture or modifying data. After the restore lease is acquired, export, backup, deletion preview, pause/resume, tray exit, session-ending cleanup and a second restore must not create another lifecycle owner.

## Required teardown order

1. Stop the dashboard refresh timer and disable data controls.
2. Unregister the system event bridge so lock and suspend callbacks cannot race teardown.
3. Await capture disposal so Raw Input is unregistered and every accepted event reaches its stop barrier.
4. Verify diagnostics show ingress closed and Raw Input registration closed.
5. Verify Storage is `Stopped` and not accepting writes.
6. Dispose the sole Storage owner and clear each released App field so `OnExit` cannot dispose it twice.
7. Call a narrow public offline restore facade that delegates to the existing journalled restore implementation.
8. Show the result and automatic pre-restore backup path, then request shutdown exactly once.

Any uncertain stop, diagnostics, Storage state or disposal result must prevent the restore call and enter a fail-closed terminal state. A restore exception must not automatically reopen Storage or resume capture. A retained journal is recovered by the next normal `StorageService.Open`.

## Minimum API seams

- Storage exposes a narrow `RestoreOffline(StorageLayout, relativeBackupPath)` facade. It accepts only a relative contained path, obtains the exact writer lock and does not expose fault injection publicly.
- App owns an `AppOfflineRestoreCoordinator` with injected teardown, restore, UI-state and shutdown collaborators.
- The data-tools operation gate produces a transferable restore lease so selection, confirmation and lifecycle teardown cannot race another operation.
- MainWindow raises a restore request and displays state; it never disposes capture or Storage directly.
- Collection status must truthfully expose restoring and restore-failed terminal states instead of continuing to display collecting or paused.

## Automated gates

All destructive-path tests use disposable stores under `.tools/verification/<timestamp>/restore-cases/<guid>` and have no skip path.

- Reject missing user pause, accepting Storage, missing confirmation, path escape, reparse paths and non-backup files with zero stop/restore/shutdown calls.
- Assert the exact teardown and restore order and exactly one shutdown request.
- Assert every uncertain or failed teardown prevents the restore call and never resumes capture.
- Prove an active owner makes the offline facade fail closed; releasing it allows the same fixture to restore.
- Restore temporary store B over A, reopen as a new owner and verify B exactly; restore the automatic pre-restore backup into another disposable root and verify A exactly.
- Exercise every existing journal move fault point. Controlled failures preserve A; simulated process termination leaves a journal that the next open rolls back without a mixed store.
- Prove capture, system event and Storage owners are each disposed at most once under explicit-exit and session-ending races.
- Keep process-crash evidence distinct from physical-power evidence.

## Manual gate

Manual testing is allowed only on a disposable copy of the complete green directory, never the sole user data directory. Create distinct snapshots A and B, restore A after explicit pause and confirmation, restart manually and verify exact statistics and trajectories. Then restore the automatic before-restore backup in another disposable copy and verify B. The execute button remains disabled until the automated, Acceptance and disposable-copy gates are all green.
