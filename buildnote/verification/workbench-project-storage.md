# Workbench project storage verification

## Automated status

The `E2E.Tests` lifecycle uses temporary roots, real bare Git storage and linked
worktrees, real reconciliation, real device SQLite ingestion/partial replacement, and
mocked engineering boundaries. It verifies custom/default roots, two devices,
rejected preview safety, approved baseline creation, unchanged refresh behavior,
sparse retained overlays, independent databases, worktree sessions, shared history,
merge visibility, traversal rejection, and an untouched legacy sentinel.

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

