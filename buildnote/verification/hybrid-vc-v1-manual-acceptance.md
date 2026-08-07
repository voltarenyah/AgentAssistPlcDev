# Hybrid version-control storage V1 — manual live-TIA acceptance

## Automated status

The `E2E.Tests` lifecycle drives `WorkbenchCoordinator` with temporary roots, real
bare Git storage, real SharpSvn `repository.svn` stores on `file://`, and a mocked
engineering boundary. It verifies the bootstrap import layout (`repository.svn` +
`tia/` checkout + committed `revision.json`), compile-failure-still-imports,
SaveAs-failure rollback, the combined commit (Git HEAD's `revision.json` matches
the SVN HEAD revision; classification in the SVN message), pending-commit
recovery, safety-only commits, restore at HEAD and at an older Git commit, feature
SVN branch isolation in both directions, and worktree removal that keeps the SVN
branch. `Agent.Tests` covers the writer determinism, classification truth table,
schema 1.1/1.2 compatibility, base-revision resolution, branch
sanitization/collision, and rollback paths. `Mcp.VersionControl.Tests` covers the
SVN service and tools offline.

## Live TIA Portal V17 status

**Pending — TIA Portal V17 and a live project were unavailable in this
environment.** No live acceptance result, screenshot, device path, or revision is
claimed.

Run this checklist on a workstation with TIA Portal V17 and a real project. It
covers the eleven V1 completion criteria. Record every observed path, revision,
and SHA; any mismatch stops the run.

1. **Import an existing .ap17 project.**
   Create a workbench with a custom root and the origin project path.
   Expected: creation succeeds; the connect runs headless (no TIA UI opens);
   `<root>/repository.svn/` and `<root>/worktrees/master/tia/` exist; the `tia/`
   copy contains the managed `.ap17`; `workbench.json` records
   `svnRepositoryPath`, `originProjectPath`, `originImportedAt`, and
   `managedTiaProjectPath`; `worktrees/master/engineering-state/revision.json`
   exists with `svn.url = "^/native/main"` and `svn.revision ≥ 1`.
2. **Delete/disconnect the original.**
   Close TIA and rename the origin project file temporarily.
   Expected: the workbench remains fully usable; `svn log` on
   `repository.svn/native/main` shows the baseline commit
   `native: initial managed TIA project baseline`.
3. **Continue working from the managed project.**
   Open the project in TIA from the workbench and make a small block edit.
   Expected: TIA opens the managed copy under `worktrees/master/tia/`, not the
   (renamed) origin; `Compare with TIA` works against the managed copy.
4. **Commit a normal PLC modification.**
   Compile succeeds; accept the change and commit on master with a message.
   Expected: exactly one new Git commit containing the changed XML and
   `engineering-state/revision.json`; `svn log` on `native/main` shows a new
   revision whose message is `<message> [semantic, native]`; the new
   `revision.json` `svn.revision` equals the SVN HEAD revision;
   `validation.compileStatus = "SUCCESS"` and `tia.projectChecksum` is set.
5. **Commit a safety-only modification.**
   Make a safety-relevant change in TIA that leaves the exported block XML
   unchanged, then commit with no selected source paths.
   Expected: a Git commit containing only `engineering-state/revision.json`;
   the exported XML is byte-identical before/after; `safety.fSignature` records
   the signature when the Openness probe lands (null in V1 — the commit must
   still exist and classify as `[safety]`).
6. **See both in Git history.**
   Open the History view / `git log`.
   Expected: both commits from steps 4 and 5 are visible with their messages;
   each commit's `revision.json` names a distinct or equal SVN revision that
   resolves (step 7).
7. **Restore either native state from SVN.**
   From the Native (SVN) tab's savepoint dropdown, restore the baseline commit and
   the step-4 commit; each lands in `<workbenchRoot>/export/<checksum>/`.
   Expected: each target contains a complete TIA project at exactly the recorded
   SVN revision with no `.svn` metadata; both open in TIA; the live
   `worktrees/master/tia/` working copy is untouched; the step-4 restore contains
   the step-4 edit, the baseline restore does not.
8. **Create a feature worktree from master.**
   Expected: `worktrees/<feature>/tia/` holds a checkout of
   `^/native/branches/<feature>`; `svn log` on that branch URL shows the copy
   from `native/main@<base>`; `worktree.json` records `branch`, `baseCommit`,
   `svnUrl`, `baseSvnRevision`, and a `managedTiaProjectPath` inside the
   feature's own `tia/` copy.
9. **Modify/commit master independently.**
   Expected: `svn log native/main` shows the new master commit; `svn log
   native/branches/<feature>` does not contain it and its HEAD revision does not
   advance.
10. **Modify/commit feature independently.**
    Expected: `svn log native/branches/<feature>` shows the feature commit;
    `svn log native/main` does not contain it; the feature's `revision.json`
    records `svn.url = "^/native/branches/<feature>"` and the new branch HEAD
    revision.
11. **Restore both histories correctly.**
    Restore master's HEAD state and the feature's HEAD state (two separate
    `export/<checksum>/` targets).
    Expected: each restore pins its own branch revision; the master restore
    contains the step-9 change and not the step-10 change, and vice versa.

Additional spot checks during the run:

- Kill the app between the SVN commit and the Git commit of a savepoint (or
  block Git): expected `.automation/pending-commit.json` with
  `PENDING_GIT_COMMIT`; the next commit completes with the same SVN revision and
  deletes the file.
- Remove the feature worktree: expected `worktrees/<feature>/` (incl. `tia/`) is
  gone; `svn log native/branches/<feature>` still resolves.
- Record `repository.svn` growth across the run for the deferred delta benchmark.
