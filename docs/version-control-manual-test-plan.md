# PLC Version Control Manual Test Plan

Use this checklist in order. Mark each item only after the expected result is confirmed.

## 1. Open the workspace

- [ ] Open the Version Control page.
- [ ] Confirm the current worktree and branch are shown.
- [ ] Confirm only meaningful PLC source objects are listed.
- [ ] Confirm staging files, knowledge databases, and temporary metadata are not shown as source changes.
- [ ] Select an object and confirm its details appear in the right dock.

## 2. Verify a clean master baseline

- [ ] Switch to `master`.
- [ ] Confirm the working tree is clean.
- [ ] Connect to the TIA project.
- [ ] Run the TIA consistency check.
- [ ] Confirm the checksum matches master and the fast gate passes without a full scan.

## 3. Detect a direct TIA modification

- [ ] Modify one PLC block directly in TIA.
- [ ] Compare TIA with master.
- [ ] Confirm checksum mismatch triggers a full scan.
- [ ] Confirm the changed block is identified.
- [ ] Select that block only and confirm the source update.
- [ ] Enter a commit message and commit manually.

## 3a. Detect and accept a project hardware modification

- [ ] Compare TIA with master without changing the PLC source.
- [ ] Confirm the project-level hardware AML is exported and compared.
- [ ] Confirm a timestamp-only AML change is reported as unchanged.
- [ ] Modify the TIA project hardware configuration and compare again.
- [ ] Confirm the hardware difference is shown even when all PLC checksums match.
- [ ] Confirm the hardware acceptance action requires a non-empty commit message.
- [ ] Accept the staged hardware export and confirm a hardware commit is created with that message.
- [ ] Compare again and confirm the hardware state is in sync.
- [ ] Confirm the new commit appears in history.

## 4. Create and modify a feature worktree

- [ ] Create or switch to a feature worktree.
- [ ] Modify one or more PLC source XML files.
- [ ] Confirm only modified PLC objects are listed.
- [ ] Inspect the file-level diff and object details.
- [ ] Commit the feature changes manually.
- [ ] Confirm import and merge are unavailable before commit.

## 5. Import a feature change into TIA

- [ ] Connect the feature worktree to TIA.
- [ ] Run the consistency check against master.
- [ ] Confirm the checksum matches and the fast gate passes.
- [ ] Select individual feature blocks for import.
- [ ] Confirm the import.
- [ ] Verify the selected blocks are present in TIA.

## 6. Test TIA divergence before import

- [ ] Modify another block directly in TIA after the feature branch was created.
- [ ] Run the consistency check again.
- [ ] Confirm mismatch triggers a full scan.
- [ ] Confirm the changed TIA blocks are listed.
- [ ] Choose whether to record those TIA changes into master.
- [ ] Confirm the user decision is required before continuing.

## 7. Test overlapping changes

- [ ] Modify the same block in both the feature branch and TIA.
- [ ] Run the comparison.
- [ ] Confirm the overlap is detected.
- [ ] Confirm the conflicting block cannot be imported automatically.
- [ ] Confirm no semantic XML merge is attempted.

## 8. Validate the complete feature worktree

- [ ] Select the feature worktree for merge.
- [ ] Confirm validation covers all devices.
- [ ] Import the required feature blocks into TIA.
- [ ] Compile the entire PLC project.
- [ ] Confirm a valid project checksum is produced.
- [ ] Confirm the validation result and checksum are displayed.

## 9. Merge the validated worktree

- [ ] Merge the validated feature worktree into master.
- [ ] Confirm the merge includes all devices.
- [ ] Confirm the feature commit history is preserved.
- [ ] Confirm the validated project checksum is stored as the consistency label.
- [ ] Confirm permanent merge evidence appears in history.

## 10. Verify master after merge

- [ ] Switch to master.
- [ ] Compare master with TIA.
- [ ] Confirm matching checksum and fingerprint show the project as consistent.
- [ ] Modify TIA afterward and repeat the comparison.
- [ ] Confirm the mismatch and changed blocks are reported.

## 11. Test compile failure and rollback

- [ ] Import a feature change into TIA.
- [ ] Make the full project compile fail.
- [ ] Confirm the user is asked whether to roll back.
- [ ] Choose rollback and confirm master recovery blocks are imported.
- [ ] Repeat and choose to keep the change after confirmation.
- [ ] Confirm the decision is recorded.

## 12. Verify history and recovery

- [ ] Open version-control history.
- [ ] Inspect commits, changed objects, checksums, and validation evidence.
- [ ] Select an earlier master version.
- [ ] Start rollback.
- [ ] Confirm affected objects and devices are shown before confirmation.
- [ ] Confirm the rollback result appears in comparison.

## Deferred boundary

- [ ] Test TIA add, delete, and rename behavior separately after the supported TIA Openness operations are finalized.
