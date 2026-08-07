# PLC source version-control workflow

A workbench keeps two stores side by side. Git is the readable history: exported
PLC source XML plus one metadata file per worktree. A local SVN repository is the
native store: the complete TIA project, byte-exact, one branch per worktree.
`engineering-state/revision.json` is the link between them — every engineering
savepoint is one Git commit naming the SVN revision that holds the same TIA state.

```text
Git commit → revision.json → SVN revision → TIA project
```

## Workbench layout

```text
<workbenchRoot>/
  workbench.json                  # identity, registrations, SVN store path, provenance
  repository.git/                 # bare Git — semantic store
  repository.svn/                 # local SVN repo (file://) — native store
  worktrees/
    master/
      engineering-state/
        revision.json             # GIT-TRACKED: svn url+revision, checksums, compile status
      devices/<plc>/source/**     # git-tracked exported PLC XML
      tia/                        # SVN working copy of ^/native/main (never git-tracked)
    feature-x/
      engineering-state/revision.json
      devices/<plc>/source/**
      tia/                        # SVN working copy of ^/native/branches/feature-x
```

The SVN layout is `native/main` plus `native/branches/<feature>` only — no tags,
no `svn merge`, no branch cleanup; branches simply remain. `worktree.json`,
`device.json`, staging, knowledge databases, `.automation/`, `repository.svn/`,
and `tia/` are runtime artifacts and are excluded from Git.

`revision.json` (schemaVersion 1, deterministic property order, nulls explicit):

```json
{ "schemaVersion": 1,
  "svn": { "url": "^/native/main", "revision": 25 },
  "tia": { "projectChecksum": "PLC_1:...;PLC_2:..." },
  "safety": { "fSignature": null },
  "validation": { "compileStatus": "SUCCESS" } }
```

`compileStatus` is `SUCCESS`, `FAILED`, or `NOT_RUN`. The F-signature is recorded
as null in V1: no stable safety signature is readable through the wrapped Openness
surface yet (marked extension point in the bootstrap and commit code).

## Bootstrap import

Creating a workbench from an existing `.ap17` imports it into managed storage:

1. Validate the origin path (sandbox jail + must exist). The origin is
   bootstrap-only and is never needed again after step 4.
2. Create the catalog entry, the bare Git repository, and the SVN native store;
   create `worktrees/master/tia/` as a plain empty directory. TIA refuses Save As
   into a non-empty directory, so no SVN checkout happens yet.
3. Open the origin project headless (`withUI: false`); a session attach works too.
4. TIA Save As into the empty `tia/` directory; verify the managed copy
   independently (the active project must match the path TIA reported). Failure
   aborts.
5. Optional compile of every PLC on the managed copy. Success records the
   aggregated per-PLC software checksum; failure records `FAILED` and continues —
   a compile failure never fails the import.
6. Export the semantic PLC source into `devices/<plc>/source` (staging → preview →
   apply, same reconciliation path as a refresh).
7. Disconnect TIA, so no TIA process can still write the managed tree while it is
   committed (the freeze rule).
8. Strip legacy app export caches: TIA Save As copies the whole origin project
   folder, which may contain `export/`/`Exports/` directories written by older app
   versions. A candidate is removed only when its `metadata.json` is recognizably
   ours (schemaVersion plus exportRoot/components); anything unrecognized is kept
   and a removal failure never aborts the import. Everything TIA-native (`System`,
   `IM`, `UserFiles`, `Vci`, `XRef`, `TMP`, `Logs`, `AdditionalFiles`, …) stays.
9. Bring the saved project under SVN control: `native/main` is still empty, so an
   obstruction-allowing checkout into the now non-empty `tia/` directory is safe
   and only adds the `.svn` metadata. Then commit the native baseline, write
   `revision.json`, and create the Git baseline commit containing the source XML
   and `revision.json`.

Any failure rolls the workbench back completely (Git repo, SVN store, worktrees).
The origin path and import time are kept as provenance (`originProjectPath`,
`originImportedAt`); the operational path is `managedTiaProjectPath` inside the
worktree's `tia/` store.

## Combined commit

On a workbench with an SVN native store, committing is one transaction: TIA Save →
compile (success is required; a compile failure aborts before anything is
committed) → read the aggregated project checksum → read the F-signature (null in
V1) → disconnect TIA (freeze) → SVN commit of the worktree's `tia/` copy → write
`revision.json` → Git commit of the selected source paths plus `revision.json`.

The SVN message carries the change classification, never a Git SHA:
`"<message> [semantic, native]"`. Classification compares the fresh state against
the base `revision.json`: `semanticChanged` (committed source paths),
`safetyChanged` (F-signature difference), `nativeChanged` (dirty SVN working copy
or changed project checksum). A commit with no semantic, safety, or native change
is rejected. A safety-only change — unchanged XML, changed signature — still
produces a Git commit containing only `revision.json`.

The master's existing write gates are unchanged: only TIA-compared and accepted
source paths may be committed there. The SVN side joins the transaction after
those gates pass. The TIA session stays disconnected after a commit; the next TIA
operation reopens the managed project on demand.

### Pending commit recovery

If the SVN commit succeeded but the Git commit fails, the worktree records
`.automation/pending-commit.json` (Git-ignored) with `{ svnUrl, svnRevision,
status: "PENDING_GIT_COMMIT" }`, and the caller sees a `GIT_COMMIT_PENDING` error
naming the recorded SVN revision. The next commit on that worktree retries the Git
side only — same SVN revision, then the pending file is deleted. No second SVN
snapshot is ever taken for the same savepoint.

## Feature worktrees

A feature worktree gets its own SVN native branch in addition to its Git branch:
`svn copy ^/native/main@<base> → ^/native/branches/<feature>`, checked out into
`worktrees/<feature>/tia/`. The base revision comes from master's
`engineering-state/revision.json`. The branch name is sanitized to a single SVN
path segment; an existing branch is rejected with `SVN_BRANCH_EXISTS` before
anything is created. Worktree metadata records the branch, the base Git commit,
the SVN URL, and the base SVN revision; `managedTiaProjectPath` points at the
feature's own project copy, so TIA operations and the combined commit run against
the feature branch. Feature and master then evolve independently — commits on one
never advance the other's SVN branch. Removing a worktree deletes its `tia/`
working copy; the SVN branch remains in the repository. Rollback of a failed
creation removes the Git worktree and any partial `tia/` copy; the SVN branch is
never deleted.

## Restore

`RestoreTiaProjectAsync` reads `revision.json` at a Git commit (default HEAD, via
`vc_show_file` — the working tree is never switched), resolves the recorded SVN URL
and revision, and runs `svn_export` at exactly that revision — a lean tree with no
`.svn` metadata. The target is deterministic: `<workbenchRoot>/export/<checksum>/`
(the revision's TIA project checksum, sanitized; `rev-<N>` when the commit recorded
no checksum). A non-empty existing target is refused. The Version Control page's
"Native (SVN)" tab lists savepoints as `revision · checksum · commit` from the
`savepoints` endpoint; the restored path opens in TIA as an independent inspection
copy and the live `tia/` working copy is never touched.

## Schema 1.1 and 1.2

New workbenches are schema `1.2` with an SVN native store. Workbenches created
before (`1.0`/`1.1`) still load without migration: `svnRepositoryPath` and the
provenance/managed-path fields are null, the combined commit and restore report
`SVN_HISTORY_UNAVAILABLE`, and their commits keep the previous Git-only behavior.

## Master and feature write rules

Direct source edits are allowed on any worktree including `master` (the
`MASTER_EDIT_NOT_ALLOWED` policy is disabled). Master commits of direct local
edits are accepted and committed as unlabeled savepoints; only TIA-accepted files
keep staleness checks (a file or HEAD that moved after its recorded authorization
is still rejected). Feature worktrees remain the isolated form for longer-running
changes: a newly created feature inherits the master device metadata and source
snapshot plus its own SVN branch and native project copy.

## Refresh and commit

TIA refresh is a two-step operation: stage an export, then compare it with the
source tree and approve individual XML paths. Applying approval writes those
files to the selected worktree but does not stage or commit automatically. The
version-control page selects individual changed XML paths and commits them with
a user-supplied message through the worktree-level commit endpoint.

XML bytes define whether a file is touched. Diff summaries suppress only the
known TIA creation timestamp; protected logic and structure changes remain
visible, while safe header and multilingual text changes are summarized.

## Validation evidence

Validation is permanent evidence attached to a commit as an annotated tag named
`tia-validation/<full-commit-sha>`. Evidence records use schema `1.0` and are
immutable. History marks commits as `Validated`, `Unlabeled`, or `Invalid` and
records whether the evidence came from `tia-sync` or `feature-merge`.

## Unauthorized master changes

If master contains source changes outside the editor policy, the user may move
selected XML files into a newly created feature worktree, with byte hashes
stored under ignored `.automation/recovery`, or explicitly discard selected
paths back to `HEAD`. Neither recovery action creates a master commit. Discard
requires an explicit confirmation.

## Version control workspace

The Version control tab is worktree-scoped and covers every registered PLC.
Changes shows only source XML objects, grouped by PLC and category. Select
individual objects and enter a commit message; there is no staging concept in
the UI. Direct master edits are commitable from this screen (unlabeled).

Compare with TIA first checks the saved checksum evidence. A checksum match is
shown immediately. A mismatch runs a full source scan and presents individual
block, DB, UDT, and tag-table differences. Selected supported TIA changes can
be accepted into master, but remain uncommitted until the user records the
change.

For a feature worktree, Prepare feature import creates a three-way import plan.
Objects changed in both TIA and the feature are disabled individually; unrelated
objects remain selectable. Importing is followed by compiling every device and
confirming that the complete PLC software was tested on the machine. Only the
server-issued validation ID can publish the no-fast-forward merge and its
permanent evidence.

History displays changed PLC objects, validation state, checksums, and evidence.
Historical recovery creates a new rollback feature containing selected XML; it
never resets or directly restores master.
