# Workbench project storage verification

## Automated status

The `E2E.Tests` lifecycle drives `WorkbenchCoordinator` with temporary roots, real
bare Git storage and linked worktrees, real reconciliation, real device SQLite
ingestion/partial replacement, and a mocked engineering boundary. It verifies
stage/preview/reject/stale-approval/apply behavior, exact auto-staging, no-op refresh
history and timestamp preservation, post-edit refresh, device-isolated knowledge
state, full and batched partial updates, import-then-compile ordering, retained sparse
overlays, saved/loaded ignored sessions, complete two-device checkouts, shared
history/merge visibility, default-root injection, traversal rejection, and an
untouched/unlisted legacy sentinel.

The earlier proposed `scripts/e2e-workbench.json` invocation is not implemented or
claimed. `tests/E2E.Tests` supersedes it because the coordinator approval and session
boundaries plus mocked TIA behavior cannot be represented safely by the existing
standalone stdio-server scenario runner.

Sandbox integration tests additionally verify that a catalog-backed custom root is
usable after trusted host registration, while an arbitrary unregistered root and a
root containing a reparse point remain denied.

## Live TIA Portal V17 status

**Pending — TIA Portal V17 and a live two-device project were unavailable in this
environment.** No live acceptance result, screenshot, device path, or commit SHA is
claimed.

Run this checklist on a workstation with the target project:

1. Record the TIA project path, two PLC names, application version, and start time.
2. Create a custom-root workbench and record its `workbench.json` path.
3. Stage both device exports; capture each preview and approve its initial baseline.
4. Record the automatic baseline commit SHA and both device paths/databases.
5. Create a feature worktree and confirm both devices have complete source checkouts.
6. Prepare and modify one block overlay on one device; confirm only that device is stale.
7. Batch-update that device DB once and capture the tool result/applied hashes.
8. Import and compile the overlay; capture TIA output and confirm the overlay remains.
9. Refresh the device, approve the preview, and record the automatic commit SHA.
10. Merge the feature branch into `master`; record source and merge SHAs.
11. In both worktrees, record `git log --oneline --all --decorate` and source-tree paths.
12. Capture screenshots for device selection, preview approval, compile result, and history.
13. Confirm a legacy `%LOCALAPPDATA%\PlcAiAssistant\exports` sentinel is unchanged.
