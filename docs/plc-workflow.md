# PLC source workflow

1. Create a named workbench at the default or a user-selected absolute root.
2. Connect to the engineering project and persist its discovered devices.
3. Export one selected device completely into its ignored `staging` folder.
4. Preview staging against `exported-source`. Rejecting the preview makes no baseline
   or Git mutation.
5. Confirm the exact preview. The reconciler copies only added/changed files, removes
   only approved deletions, and leaves unchanged files untouched.
6. The changed baseline paths and metadata are staged and committed automatically.
   If the commit fails, the result explicitly reports that files changed but commit
   failed; it does not claim a rollback.
7. Prepare edits in `modified-source`. Existing baseline files are copied only once;
   new files may be created there. Direct writes to `exported-source` are refused.
8. Import only the overlay file, compile it, and record the result. The overlay is
   retained for the lifetime of its worktree.

Full source exports never write directly over tracked files. This preserves per-file
Git history across repeated PLC refreshes.

## Live TIA acceptance

Live TIA Portal V17 was not available in the automated test environment. The manual
acceptance checklist and pending status are recorded in
[`buildnote/verification/workbench-project-storage.md`](../buildnote/verification/workbench-project-storage.md).

