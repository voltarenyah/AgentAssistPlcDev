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
      hardware/                   # git-tracked project hardware export (project.aml + network-configuration.txt)
      tia/                        # SVN working copy of ^/native/main (never git-tracked)
    feature-x/
      engineering-state/revision.json
      devices/<plc>/source/**
      tia/                        # SVN working copy of ^/native/branches/feature-x
```

The SVN layout is `native/main` plus `native/branches/<feature>` only — no tags,
no `svn merge`, no branch cleanup; branches simply remain. The native baseline
commit is the repository's first revision (r1); workbenches created before this
numbering change carry a `Create native store layout` scaffolding commit at r1
and their baseline at r2 — both forms work identically at runtime. `worktree.json`,
`device.json`, staging (including `hardware/staging/`), knowledge databases,
`.automation/`, `repository.svn/`, and `tia/` are runtime artifacts and are
excluded from Git. Hardware exports produce the project-level `project.aml`
plus `network-configuration.txt`, the canonical communication/network
fingerprint (subnets, PROFINET names/IPs, IO-system assignments, port topology,
MRP domains, OPC UA server interfaces — issue #69); per-device CAx is skipped —
slow on big projects.

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
2. Create the catalog entry, the bare Git repository, and the SVN native store
   (an empty repository at r0 — no scaffolding commit, so the baseline lands as
   r1; `native/main` and `native/branches` are created on demand); create
   `worktrees/master/tia/` as a plain empty directory. TIA refuses Save As
   into a non-empty directory, so no SVN operation happens yet.
3. Open the origin project headless (`withUI: false`); a session attach works too.
4. TIA Save As into the empty `tia/` directory; verify the managed copy
   independently (the active project must match the path TIA reported). Failure
   aborts.
5. Optional compile of every PLC on the managed copy. Success records the
   aggregated per-PLC software checksum; failure records `FAILED` and continues —
   a compile failure never fails the import.
6. Export the semantic PLC source into `devices/<plc>/source` (staging → preview →
   apply, same reconciliation path as a refresh).
7. Export the project-level hardware configuration into `hardware/project.aml`
   and rebuild each device's ignored `plc-knowledge.db` from the initial source.
   The project is not returned to the caller until both derived artifacts exist.
8. Disconnect TIA, so no TIA process can still write the managed tree while it is
   committed (the freeze rule).
9. Strip legacy app export caches: TIA Save As copies the whole origin project
   folder, which may contain `export/`/`Exports/` directories written by older app
   versions. A candidate is removed only when its `metadata.json` is recognizably
   ours (schemaVersion plus exportRoot/components); anything unrecognized is kept
   and a removal failure never aborts the import. Everything TIA-native (`System`,
   `IM`, `UserFiles`, `Vci`, `XRef`, `TMP`, `Logs`, `AdditionalFiles`, …) stays.
10. Commit the native baseline as r1: `svn import` cannot create the missing
   `native/` parent, so `tia/` is staged through a scratch working copy of the
   repository root — `native/main` and the project content land in a single
   commit. A fresh checkout of `^/native/main` then restores `tia/` as a clean
   working copy at its original path. On failure the tree is moved back before
   the workbench rollback runs. Finally write `revision.json` and create the
   Git baseline commit containing the source XML, `hardware/project.aml`, and
   `revision.json`.

Any failure rolls the workbench back completely (Git repo, SVN store, worktrees).
The origin path and import time are kept as provenance (`originProjectPath`,
`originImportedAt`); the operational path is `managedTiaProjectPath` inside the
worktree's `tia/` store.

## Commits are git-only; native snapshots are explicit

Ordinary commits (VC panel, refresh apply-and-commit, feature commits) write Git
history only — they never touch SVN, compile, or `revision.json`. The combined
transaction runs only for the explicit **Create SVN savepoint** action (Native tab)
and the workbench baseline: TIA Save → compile (success is required; a compile
failure aborts before anything is committed) → read the aggregated project
checksum → read the F-signature (null in V1) → disconnect TIA (freeze) → SVN
commit of the worktree's `tia/` copy → write `revision.json` → Git commit of
`revision.json` (plus any selected source paths) — binding the TIA state to a
restorable SVN revision.

The SVN message carries the change classification, never a Git SHA:
`"<message> [native]"`. Classification compares the fresh state against the base
`revision.json`: `safetyChanged` (F-signature difference), `nativeChanged` (dirty
SVN working copy or changed project checksum). A savepoint with no safety or
native change is rejected as nothing-to-commit. A safety-only change still
produces a Git commit containing only `revision.json`.

### Untrackable-change commits

Not every TIA change produces a git-file diff (hardware AML and most XML are
covered, and the software checksum reflects software changes — but some changes
are not detectable). The VC panel's **Untrackable change** checkbox lets the
user commit a message with zero selected paths: the result is an empty Git
commit carrying an annotated marker tag `untrackable-change/{sha}`. Both
timelines show an amber marker on such commits, and the savepoint area warns
that the change is not covered by any SVN savepoint until one is created —
because nothing in git records the TIA-side change, only a native snapshot makes
it restorable. Without the checkbox, empty commits are still rejected.

Master write rules: TIA-accepted files keep staleness checks; direct local edits
commit freely as unlabeled savepoints (see Master and feature write rules). The
TIA session stays disconnected after a savepoint; the next TIA operation reopens
the managed project on demand.

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

Version control is a worktree-level concept. The panel lives in the right dock
of the worktree page (select a worktree without selecting a device) and covers
every registered PLC. It is a quick-access surface with two pages, switched
from an icon tab bar:

- **Changes** — a Compare with TIA action on top, then the commit message area
  and split commit button, then the modified-files tree (collapsible
  PLC · category folders; clicking a row toggles its selection). At the
  bottom, the snapshot area shows the last SVN savepoint revision, how many
  commits happened since, a hardware-different label when `project.aml`
  changed, and a description input with a Snapshot button that records a new
  TIA savepoint.
- **History** — a single timeline merging git commits (blue circles) and SVN
  savepoints (violet squares), newest first. Expanding a commit shows author,
  time, linked SVN revision, TIA checksum, validation state, and its changed
  files; selecting files there offers creating a rollback feature (master is
  never reset). Right-clicking a savepoint exports that saved project as a
  lean copy — the live project is never touched.

The Compare with TIA action runs the comparison immediately and renders the
result inline at the top of the Changes page — no separate view. Accepting TIA
changes and accepting hardware differences reuse the page's commit message as
the commit title.

Compare with TIA first checks the saved checksum evidence for each PLC, then
also exports and compares the project-level hardware AML. A checksum match is
not sufficient to declare the project clean when hardware differs. An
untrackable-change commit is also never treated as source evidence, even when
its recorded checksum matches, so Compare with TIA performs a full source scan
to find any pending trackable diff. Other checksum mismatches run the same full
source scan and present individual block, DB, UDT, and tag-table differences.
Hardware differences are shown separately with the staged AML;
accepting them requires an explanatory commit message and creates a hardware
commit. Both source directions are offered:
accepting selected TIA changes into the local repo (with a commit title), or
pushing selected local objects back into TIA (per-object import outcomes;
compile and snapshot afterwards). When no source differences exist but the TIA
checksum differs from the last savepoint, the result points to the TIA
snapshot area to record the remaining untrackable change; when source and hardware
match, it reports that no commit is needed.

For a feature worktree, Prepare feature import creates a three-way import plan.
Objects changed in both TIA and the feature are disabled individually; unrelated
objects remain selectable. Importing is followed by compiling every device and
confirming that the complete PLC software was tested on the machine. Only the
server-issued validation ID can publish the no-fast-forward merge and its
permanent evidence.

History displays changed PLC objects, validation state, checksums, and evidence.
Historical recovery creates a new rollback feature containing selected XML; it
never resets or directly restores master.
