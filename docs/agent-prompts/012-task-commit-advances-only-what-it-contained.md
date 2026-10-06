# 012. A task commit advances only what it contained

Status: done
Created: 2026-10-06
Depends on: 010, 011

## Goal

A commit that belongs to a task advances the stage baseline of exactly the source objects that commit
contained — on the task-commit path and on the TIA-accept path alike — so the next **Task only**
compare measures against what was committed. A commit that contained none of the task's staged
objects reads nothing from TIA and records nothing, and a failure to record the evidence is visible
to the user.

## Context

- ADR-0003: "A task commit records fingerprint evidence only for source objects included in that
  commit, bound to their committed source content. It never records a full live TIA fingerprint
  snapshot."
- The implemented task-commit path did the opposite of the first sentence:
  `TryRecordTaskStageEvidenceAsync` refreshed **every** active stage of the task
  (`WorkbenchCoordinator.cs:4562-4590` before this item). Committing one object therefore marked
  another staged object's uncommitted live work as clean — the AC-003 failure mode, inside one task.
- The TIA-accept path had no task context at all: `ApplyTiaSynchronizationAsync` called
  `CommitSourceAsync` without `taskEvidenceTaskId`, so accepting a task-scoped comparison never
  advanced the task's stage baselines. Observed consequence: after committing the object a task-only
  compare reported, the same object was reported as changed again until it was re-staged.
- ADR-0001 requires every commit of a new project to attempt a commit-bound v2 validation tag
  ("Each later commit shall make a best-effort attempt to write an immutable v2 validation tag bound
  to its exact SHA"), and the verification section expects "a complete v2 validation tag". ADR-0003
  says a task commit never records a full live snapshot. For a commit that belongs to a task these
  two cannot both hold, and the accept path currently satisfies ADR-0001 while `/vc/commit`
  satisfies ADR-0003. **This item does not resolve that conflict**: it leaves each path's recorded
  evidence kind exactly as it was and only fixes which stage baselines advance. Choosing between
  ADR-0001 and ADR-0003 for task commits is a maintainer decision and is recorded here rather than
  decided locally.

## Constraints

- Do not change which validation evidence each commit path records; keep ADR-0001's attempt intact
  and ADR-0003's sparse record intact. Only the stage baselines change.
- Read nothing from TIA when the commit contained none of the task's staged objects.
- A failed evidence write must not fail the commit (ADR-0001) and must be observable: the accept
  result already carries `EvidenceWarnings`; `/vc/commit` warnings keep travelling the existing
  `WorkbenchCommitResult.EvidenceWarnings` route.
- Reuse the existing stage/evidence machinery; add no new persisted state.

## Done when

1. A test proves a commit that contained one of a task's two staged objects advances only that
   object's stage baseline, and that the scoped TIA read asked for exactly that object.
2. A test proves a commit with no task leaves stage baselines alone and performs no scoped read.
3. The commit result's evidence warnings reach the user in the version-control dock.
4. `cd studio && npm test -- --run`, `npm run build`, `npm run lint`, `dotnet build
   AgentAssistPlcDev.sln` and the `Agent.Tests` / `ApiHost.Tests` suites pass.
5. Runtime check with `.\launch.ps1` on a worktree with a staged task: commit a compared object with
   Task only and confirm the next Task only compare no longer reports it. Requires TIA.

## Evidence

Branch `codex/010-task-only-compare` (item 010's branch, continued). Nothing pushed; no PR.

### What changed

- `src/Agent/Workbench/WorkbenchCoordinator.cs` — `TryRecordTaskStageEvidenceAsync` is replaced by
  `TryRecordCommittedStageEvidenceAsync`, which intersects the task's active stages with the commit's
  paths (through each stage's registered `SourceObject` path) before it reads TIA, updates only those
  stages, and returns without reading TIA when the intersection is empty. Its unused `commitSha`
  parameter is gone. The `/vc/commit` path passes the commit's `selected` paths.
- `ApplyTiaSynchronizationAsync` gains `commitTaskId` and, after the commit and its state write,
  refreshes that task's baselines for the accepted paths only — leaving the ADR-0001 commit-bound
  validation tag `CommitSourceAsync` records untouched. The warning, if any, is returned in the
  result.
- `src/Agent/Workbench/WorkbenchModels.cs` — `TiaSynchronizationResult` gains the optional
  `EvidenceWarnings` that `WorkbenchCommitResult` already has, so a failed evidence write is
  observable instead of silent.
- `src/ApiHost/WorkbenchApiModels.cs` — the accept route resolves the worktree's active task exactly
  as `/vc/commit` does and passes it as `commitTaskId`.
- `studio/src/studio/version-control/VersionControlChanges.tsx` (+ `client.ts` types) — evidence
  warnings from an accept or a commit are shown as warnings instead of dropped.
- `tests/Agent.Tests/MasterSynchronizationTests.cs` — the two proofs below, plus the fixture's ids as
  constants.

### Validation run

| Command | Observed result |
|---|---|
| `dotnet build AgentAssistPlcDev.sln -v q` | 0 errors, 7 warnings |
| `dotnet test tests/Agent.Tests/Agent.Tests.csproj` | 516 passed (514 before, +2) |
| `dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj` | 227 passed |
| `dotnet test … --filter TaskCommitAdvancesOnlyTheCommittedObjectsStageBaseline\|AcceptWithoutATaskLeavesStageBaselinesAlone` | 2 passed |
| `cd studio && npx vitest run src/studio/version-control` | 8 files, 97 passed (96 before, +1) |
| `cd studio && npm test -- --run` | 89 files, 603 passed |
| `cd studio && npm run build` | exit 0, built |
| `cd studio && npm run lint` | 0 errors, 16 warnings; none in a changed file |

### Done when checks

1. **Passed.** `TaskCommitAdvancesOnlyTheCommittedObjectsStageBaseline`: after accepting only
   `Blocks/A.xml` with `commitTaskId` set, stage `a` carries the captured `LIVE-a` evidence, stage
   `b` still carries its staged baseline, and the scoped capture was asked for exactly `["a"]`.
2. **Passed.** `AcceptWithoutATaskLeavesStageBaselinesAlone`: the same accept without a task leaves
   both baselines untouched and performs a single capture — the commit-bound one.
3. **Passed.** `VersionControlChanges.test.tsx` proves a result carrying `evidenceWarnings` shows the
   warning (`toast.warning` called with the warning text).
4. **Passed** — see the table.
5. **Not verified.** Requires TIA: commit a compared object with Task only, then confirm the next
   Task only compare no longer reports it. The app is rebuilt, relaunched and healthy, so this is a
   two-click check for a human with TIA attached.

### Notes, decisions and risks

- **Deliberately not decided:** whether a commit that belongs to a task should record the sparse
  per-object evidence ADR-0003 describes instead of ADR-0001's whole-project snapshot. Both paths
  keep their current evidence kind; only the stage baselines changed. Resolving it changes what the
  project-wide compare's fast gate can trust, so it belongs to a maintainer with the two ADRs in hand.
- **Behaviour change worth naming:** `/vc/commit` with a task and no matching staged object no longer
  fails with `TASK_STAGE_EMPTY`; it records nothing. That is the intended reading of "records
  fingerprint evidence only for source objects included in that commit", and the commit itself was
  already allowed to proceed.
- **Cost:** an accept commit on a worktree with an active task now performs a second, small TIA
  capture (the committed objects only) in addition to the whole-project snapshot. It is skipped
  entirely when the commit contained none of the task's staged objects.
- **Pre-existing gap, not closed:** the dock now shows warnings from the commit result, but other
  surfaces that return `EvidenceWarnings` still do not surface them.
