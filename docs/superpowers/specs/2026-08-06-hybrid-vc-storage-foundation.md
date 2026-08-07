# Hybrid Version-Control Storage Foundation — V1

## Goal

Prove one thing: a TIA project can be imported into the Workbench, edited
independently in multiple worktrees, committed with readable Git history and
restorable SVN native history, and restored reliably.

This V1 sits alongside and extends
[2026-08-03-plc-version-control-design.md](2026-08-03-plc-version-control-design.md).
That design defines the semantic Git model (tracked source scope, master/feature
write rules, validation evidence); this foundation adds the missing other half —
the complete native TIA project is no longer only wherever the user happened to
leave it, but lives in a versioned local SVN store, linked to Git history.

## Mental model

```text
Git commit → revision.json → SVN revision → TIA project
Git = what happened. SVN = exact project containing it. revision.json = link between them.
```

Responsibilities: Git = readable history (exported PLC source + revision
metadata); SVN = complete native TIA project history; TIA = actual editable
project.

## V1 rules (binding)

1. Origin project = bootstrap only.
2. TIA native project = never Git tracked.
3. PLC semantic source = Git tracked.
4. `engineering-state/revision.json` = Git tracked.
5. Complete TIA project = SVN tracked.
6. One engineering savepoint = one Git commit (safety-only changes still appear in
   Git history).
7. Safety change = F-signature metadata change + SVN native revision + Git history
   entry.
8. Never commit the native project while TIA may still be writing to it.
9. Feature worktrees use independent SVN branches.
10. Never use SVN to merge TIA projects.

## Layout

```text
<workbenchRoot>/
  workbench.json
  repository.git/               bare git — semantic store
  repository.svn/               local SVN repo (file://) — native store
  worktrees/
    master/
      worktree.json             ignored app config
      engineering-state/
        revision.json           GIT-TRACKED (single metadata file — do not split)
      devices/<plc>/source/**   git-tracked exported XML
      tia/                      SVN working copy of ^/native/main
    feature-x/
      worktree.json
      engineering-state/revision.json
      devices/<plc>/source/**
      tia/                      SVN working copy of ^/native/branches/feature-x
```

SVN layout: `repository.svn/native/{main,branches/}` only. No SVN tags, no
checksum-named tags, no `svn merge`, no branch cleanup policy — branches simply
remain.

Git excludes cover `repository.svn/`, `tia/`, `worktree.json`, `device.json`,
staging, knowledge databases, `.automation/`, and export manifests.
`SourcePathPolicy` allows exactly `devices/<device>/source/**/*.xml` and
`engineering-state/revision.json` through the version-control read/write surfaces.

## revision.json

Schema version 1, one Git-tracked file per worktree holding everything, written
deterministically (stable property order, nulls explicit):

```json
{ "schemaVersion": 1,
  "svn": { "url": "^/native/main", "revision": 25 },
  "tia": { "projectChecksum": "PLC_1:...;PLC_2:..." },
  "safety": { "fSignature": null },
  "validation": { "compileStatus": "SUCCESS" } }
```

- `svn.url` is repository-relative (`^/native/main`, `^/native/branches/<feature>`).
- `tia.projectChecksum` aggregates the per-PLC compiled software checksums
  (`<plc>:<checksum>`, ordinal-sorted, `;`-joined); null when no compile succeeded.
- `safety.fSignature` is null in V1 — no stable safety signature is readable
  through the wrapped Openness surface yet; the capture point is a marked
  extension point in the bootstrap and commit flows.
- `validation.compileStatus` is `SUCCESS` | `FAILED` | `NOT_RUN`.

An engineering revision in V1 = Git commit + revision.json + referenced SVN
revision. Git is the readable revision ledger. There is no app.db, no
EngineeringRevisionStore, no revision IDs, no transaction table.

## Flows

### Bootstrap import

```text
validate origin .ap17 (sandbox jail + exists)
 → catalog.Create (schema 1.2, records repository.svn path)
 → vc_init_shared + svn_init_shared
 → create worktrees/master/tia/ as a plain EMPTY directory
   (TIA refuses SaveAs into a non-empty dir — no SVN checkout yet)
 → connect origin headless (withUI: false; session attach also supported)
 → TIA SaveAs → worktrees/master/tia/        (failure → abort + full rollback)
 → verify managed project independently      (failure → abort; origin dependency ends)
 → optional compile ON MANAGED COPY: success → record checksum; failure → FAILED, continue
 → read F-signature if available (else null — extension point)
 → export PLC semantic files → devices/<plc>/source
   (hardware CAx export is best-effort: failures are recorded as non-fatal
   hardware warnings on the export result and never abort the import — AML
   artifacts are auxiliary data, not PLC semantic source)
 → disconnect TIA (freeze, rule 8)
 → strip recognized legacy app export caches (export/, Exports/ with our
   metadata.json manifest — app state copied along by SaveAs, not TIA data;
   unrecognized content is kept, removal failure never aborts)
 → svn checkout ^/native/main (allowObstructions) into the now non-empty tia/
   (safe: native/main is still empty; only adds the .svn metadata)
 → SVN commit native baseline
 → write revision.json
 → git commit baseline (semantic XML + revision.json)
 → READY
```

Compile failure during import is not an import failure. Provenance is
`originProjectPath` + `originImportedAt`; the operational path is
`managedTiaProjectPath`. Schema 1.1 workbenches keep `sourceProjectPath` as the
operational path; readers fall back automatically.

### Combined commit (Phase 3)

```text
(master: TIA-accepted files keep staleness checks; direct local edits allowed —
MASTER_EDIT_NOT_ALLOWED disabled, commits are unlabeled savepoints)
TIA Save → Compile (success REQUIRED — failure aborts before anything commits)
 → read project checksum → read F-signature (null in V1)
 → disconnect TIA (freeze, rule 8; the session stays closed, next operation reopens)
 → svn_status → classify change
 → SVN commit — message carries the classification, NOT a git sha
 → write revision.json → git commit (selected paths + revision.json)
```

Classification: `semanticChanged` (committed source paths), `safetyChanged`
(F-signature difference, null-safe), `nativeChanged` (dirty SVN working copy or
changed checksum). Empty commits are rejected; safety-only commits produce a Git
commit containing only `revision.json`.

Minimal recovery only: SVN committed but Git failed → write ignored
`.automation/pending-commit.json` `{ svnUrl, svnRevision, status:
"PENDING_GIT_COMMIT" }` and surface `GIT_COMMIT_PENDING`. The next commit on that
worktree retries the Git side only with the SAME SVN revision, then deletes the
pending file. No second SVN snapshot, no state machine.

### Feature worktrees (Phase 4)

```text
Git:  branch feature-x + git worktree            (unchanged vc_add_worktree)
SVN:  svn copy ^/native/main@<baseRev> → ^/native/branches/feature-x
      checkout → worktrees/feature-x/tia/
```

The base revision is read from master's `engineering-state/revision.json`; the
branch name is sanitized to one SVN path segment; an existing branch fails with
`SVN_BRANCH_EXISTS` before anything is created. Worktree metadata stores only
`branch`, `baseCommit`, `svnUrl`, `baseSvnRevision`, plus the operational
`managedTiaProjectPath` pointing at the feature's own tia/ copy. Feature commits
run the same combined flow against their own SVN branch and Git branch.
`vc_remove_worktree` also deletes the tia/ working copy; the SVN branch stays.
A failed creation rolls back the Git worktree and any partial tia/ copy.

### Restore

`RestoreTiaProjectAsync(workbenchId, worktreeId, gitCommit?)`: read `revision.json`
at the given Git commit (default HEAD, via `vc_show_file` — the working tree is
never switched), resolve the SVN url+revision, and `svn_export` exactly that
revision — a lean tree with no `.svn` metadata — into the deterministic target
`<workbenchRoot>/export/<checksum>/` (`rev-<N>` when no checksum was recorded).
Existing non-empty targets are refused; the live tia/ copy is never touched. The
`savepoints` endpoint lists each commit's revision/checksum for the restore
dropdown. Workbenches without an SVN store report `SVN_HISTORY_UNAVAILABLE`.

## Schema 1.1 vs 1.2

New workbenches are `1.2`. Readers accept `1.0`/`1.1` without migration: the SVN
and provenance fields deserialize as null, SVN features report
`SVN_HISTORY_UNAVAILABLE` cleanly, and commits keep the Git-only behavior.
Trusted-root registry grants accept all three schemas.

## Deferred (explicitly NOT in V1)

- **Merge phase**: no feature→master merge of any kind (semantic three-way,
  safety merge, native merge). Rule 10 stands: SVN never merges TIA projects.
- **app.db / EngineeringRevisionStore**: no revision IDs, parent-revision tables,
  or query indexes; the Git log + revision.json are the ledger.
- **Transaction state machine**: only `normal` + `PENDING_GIT_COMMIT`.
- **Detailed safety model**: one F-signature slot only; no collective/software/
  hardware/group modeling. The Openness probe for a readable signature is pending.
- **Native file classification/optimization**: the complete TIA project is
  committed to SVN as-is (only the app's own legacy export caches — `export/`,
  `Exports/` with our manifest — are stripped at import; they are app state, not
  TIA project data).
- **SVN delta benchmarks**: repo growth is inspected manually during development.
- **Installer/packaging** verification of the SharpSvn native binaries.
- **Remote Git/SVN** and **existing workbench migration**.

## Acceptance

The eleven V1 completion criteria (import, disconnect origin, work from managed
copy, normal commit, safety-only commit, both in Git history, restore either
native state, feature worktree, independent commits on both, restore both
histories) are covered by automated E2E tests with a fake engineering boundary and
real Git + SharpSvn storage, and by the manual live-TIA checklist in
[`../../../buildnote/verification/hybrid-vc-v1-manual-acceptance.md`](../../../buildnote/verification/hybrid-vc-v1-manual-acceptance.md).
