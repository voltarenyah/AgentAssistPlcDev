# PLC source workflow

1. Create a named workbench at the default or a user-selected absolute root from an
   existing `.ap17` origin project (or an attached TIA session). The origin is
   bootstrap-only.
2. The import opens the origin headless, saves a managed copy via TIA Save As into
   the plain empty `worktrees/master/tia/` directory (TIA refuses Save As into a
   non-empty directory, so the SVN checkout happens later), and verifies the
   managed copy independently. From here on, every TIA operation uses the managed
   project.
3. An optional compile on the managed copy records the aggregated PLC checksum;
   a compile failure is recorded as `FAILED` and never fails the import.
4. Each discovered device is exported completely into its ignored `staging` folder,
   previewed against the source tree, and applied: the reconciler copies only
   added/changed files into `devices/<plc>/source` and leaves unchanged files
   untouched.
5. TIA is disconnected (freeze). Only now is the saved project brought under SVN
   control: `native/main` is still empty, so an obstruction-allowing checkout into
   the non-empty `tia/` directory is safe, followed by the native baseline commit
   to `repository.svn` (`^/native/main`). `engineering-state/revision.json` links
   the Git commit to that SVN revision, and the Git baseline commit records the
   source XML plus `revision.json`. Any import failure rolls back the whole
   workbench.
6. Later edits happen in a feature worktree; committing on an SVN-managed workbench
   is one combined transaction (save → required compile → freeze → SVN commit →
   `revision.json` → Git commit), so the Git commit and the SVN revision always
   describe the same TIA state. A failed Git commit after a successful SVN commit
   leaves `.automation/pending-commit.json`; the next commit retries the Git side
   with the same SVN revision.
7. Any recorded state can be restored: the coordinator reads `revision.json` at a
   Git commit and checks out the referenced SVN revision into a chosen directory
   for opening in TIA.

Full source exports never write directly over tracked files. This preserves per-file
Git history across repeated PLC refreshes.

## Live TIA acceptance

Live TIA Portal V17 was not available in the automated test environment. The manual
acceptance checklists and pending status are recorded in
[`buildnote/verification/workbench-project-storage.md`](../buildnote/verification/workbench-project-storage.md)
and, for the hybrid native-store model,
[`buildnote/verification/hybrid-vc-v1-manual-acceptance.md`](../buildnote/verification/hybrid-vc-v1-manual-acceptance.md).
