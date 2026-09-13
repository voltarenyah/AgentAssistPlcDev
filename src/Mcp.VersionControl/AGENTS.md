# Version control domain rules

`docs/version-control-workflow.md` is authoritative for this subsystem — read it before changing
commit, savepoint, restore, or worktree paths. The two-store invariant itself lives in the root
`AGENTS.md`.

## Native (SVN) layout

- `native/main` plus `native/branches/<feature>` only: no tags, no `svn merge`, no branch cleanup.
  Never delete an SVN branch — removing a worktree deletes its `tia/` working copy only.
- A feature worktree gets its own SVN branch based on master's `revision.json`; the branch name is
  sanitized to a single path segment and an existing branch is rejected before anything is created.
- Never take a second SVN snapshot for the same savepoint: after `GIT_COMMIT_PENDING` the retry
  commits the Git side only, for the already-recorded revision.

## Savepoints and the TIA session

- TIA refuses Save As into a non-empty directory, so `tia/` stays empty before Save As; TIA is
  disconnected (the freeze rule) before the managed tree is committed.
- Keep staging directory names short (`.st-<12hex>`): TIA export fails past 260 characters on
  Windows, and the previous staging name measured 273 on deep PLC group exports.
- Never capture `UtcNow` in a static initializer: a frozen `static readonly` commit timestamp once
  shipped for `DefaultAuthor`.

## Restore and recovery

- Restore is read-only: a deterministic target (`export/<checksum>/`), never switching the working
  tree and never touching the live `tia/` copy. A non-empty target is refused.
- Historical recovery creates a rollback feature; master is never reset or directly restored.

## Evidence and cleanliness

- An untrackable-change commit is never source evidence, even when its recorded checksum matches.
  A checksum match also cannot declare a project clean while hardware differs.
- The safety F-signature baseline lives in the git-tracked per-device source manifest
  (`devices/<plc>/source/metadata.json`), not in `revision.json`.
- Do not reintroduce an aggregate PLC content fingerprint: it was deliberately removed. Extend the
  existing per-change channels instead.
