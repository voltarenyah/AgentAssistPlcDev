# 010. Task-only compare produces committable differences

Status: done
Created: 2026-10-06
Depends on: 002, 003

## Goal

A **Task only** comparison returns the same selectable, commit-ready difference rows a project-wide
**Full scan** produces, scoped to the active task's staged source objects, so the user can compare a
task in seconds, tick the object TIA changed, type a message, and commit it — with the commit
attributed to that task. The scoped result stays honest: it is never a project-wide verdict, and it
can never certify the managed-source baseline.

## Context

- Requirement source: `docs/design/task-scoped-tia-compare-design.md` (Outcome: "commit TIA source
  changes through one device-bound task without scanning unrelated task objects") and
  `docs/ui-spec/task-scoped-tia-compare-ui-spec.md`.
- What 003 shipped and deliberately left open: `VersionControlCompare.compareActiveTask` read the
  task route and rendered a read-only candidate list, calling `onSelectionChanged(null, [])`, so
  nothing was selectable and `Commit selected (0)` was permanently disabled. 003's own record names
  this as its residual risk.
- The full-scan pipeline already has everything the scoped path needs:
  `WorkbenchConsistencyService.CompareFingerprintFirstAsync` reads evidence under one TIA lock,
  exports XML only for nominated candidates, and turns each export into a `SourceDifference` through
  `CompareCandidateXml` (`FingerprintComponents` from `FingerprintComparison.Compare`). Comparisons
  are persisted under `.automation/comparisons/<id>.json`, and `ApplyTiaSynchronizationAsync`
  re-exports and commits through the guarded `CommitSourceAsync`, verifying the compared identity
  (`StagingMatchesComparison`, issue #111).
- `WorkbenchCoordinator.CompareTaskWithTiaAsync` already built the per-stage Git-bound baseline and
  called `compare_source_evidence` with `sourceObjectIds`; it returned a candidate DTO instead of a
  comparison.
- `CoversAllManagedSourceDifferences` certifies the managed-source baseline when every difference in
  a comparison was accepted. A scoped comparison covers one device's staged objects only, so that
  certificate must be unreachable from it (ADR-0003: "Task-clean means only that task's stage
  matches; it cannot claim the project is clean").

## Constraints

- One difference list, one selection, one accept path: reuse the project-wide rendering and commit
  machinery instead of adding a second one.
- Keep the project-wide compare's request and result shape unchanged; the scoped comparison is an
  additive field on the same contract.
- A task-scoped comparison records no hardware check, covers one device's staged objects, and can
  never set `ManagedSourceConsistent`.
- Stage problems stay visible: `TASK_STAGE_BASELINE_MISSING` / `_INVALID` (request-level) and
  `TASK_STAGE_MISSING` (per-object) are explained in place, and a result carrying one never renders
  a clean claim.
- Task-only is the default scope when the worktree's active task can scope one; a worktree with no
  such task falls back to Full scan, and a scope the user picks by hand is never overridden.
- Follow `docs/STYLEGUIDE.md`; add no dependency.

## Done when

1. A backend test proves the scoped comparison returns a normal difference row (relative path, kind,
   per-component fingerprint evidence, the compared identity the accept path verifies), carries
   `ComparedTaskId`, reports no hardware check, and reads only the staged ids from TIA.
2. A backend test proves a fully accepted task-scoped comparison never records the managed-source
   baseline as consistent.
3. Frontend tests prove: the scope selector reads `Full scan` / `Task only`; task-only is the default
   when the active task has staged objects and falls back to Full scan when it has none; a scoped
   difference renders a selectable row that reports its comparison for the commit; a clean task
   result keeps the `This task is in sync` wording with no project-clean claim; hardware verification
   is unavailable in task-only scope.
4. `cd studio && npm test -- --run`, `npm run build`, `npm run lint`, `dotnet build
   AgentAssistPlcDev.sln` and `dotnet test AgentAssistPlcDev.sln` pass.
5. Runtime check with `.\launch.ps1` on a worktree with a staged task: run Task only, tick the
   changed object, commit, and confirm the task page lists the commit. If TIA is unavailable, state
   that and what remains unverified.

## Evidence

Branch `codex/010-task-only-compare` on base `c0c7c88`. Implementation and docs commits are on that
branch; nothing was pushed and no PR was created (the branch carries the six unpushed local `master`
commits as ancestors, so publishing it is the human's call).

### What changed

- `src/Agent/Workbench/ConsistencyModels.cs` — `WorkbenchConsistencyResult` gains `ComparedTaskId`
  and `StageProblems` (both optional, so the persisted comparison JSON stays readable); the
  `TaskStageProblem` record moves here from the coordinator.
- `src/Agent/Workbench/WorkbenchConsistencyService.cs` — the per-device fingerprint-first work is
  extracted into `CaptureAndCompareEvidenceAsync` (unchanged behavior for the project-wide scan,
  which keeps its exact request shape) and reused by the new `CompareTaskScopeAsync`, which persists
  the task-scoped comparison with the shared difference rows, no hardware check, `ComparedTaskId`,
  and the stage problems. `TASK_STAGE_MISSING` is derived from the live identities.
- `src/Agent/Workbench/WorkbenchCoordinator.cs` — `CompareTaskWithTiaAsync` returns the comparison
  (the `TaskSourceComparisonResult` DTO is gone) and resolves the master commit the comparison is
  authorized against; `ApplyTiaSynchronizationAsync` refuses to certify the managed-source baseline
  for a comparison that has `ComparedTaskId`.
- `studio/src/api/client.ts` — `TaskStageProblem`, the two new result fields, and
  `compareTaskWithTia` retyped to `WorkbenchConsistencyResult`; the dead `TaskSourceComparison`
  types are removed.
- `studio/src/studio/version-control/VersionControlCompare.tsx` — one difference list for both
  scopes (the read-only candidate list, its reason labels, and the separate task result block are
  gone); the task scope keeps the `This task is in sync` wording and never renders it while a stage
  problem is on screen; a request-level stage problem still renders when there is no comparison.
- `studio/src/studio/version-control/VersionControlPanel.tsx` — `Full scan` / `Task only`, task-only
  as the default when the active task can scope it (falling back to Full scan otherwise, and never
  overriding a scope the user picked), the active task read on mount so the default can be decided,
  and the hardware checkbox disabled in task-only scope.
- `tests/Agent.Tests/TaskStageBaselineTests.cs`, `tests/Agent.Tests/MasterSynchronizationTests.cs`
  — the scoped-difference and no-certification tests.
- `VersionControlCompare.test.tsx`, `VersionControlPanel.test.tsx`, `VersionControlWorkflow.test.tsx`
  — the scope labels, default and fallback, a selectable scoped row reporting its comparison, and the
  task-clean wording without a project claim.
- `docs/ui-spec/task-scoped-tia-compare-ui-spec.md`, `docs/design/task-scoped-tia-compare-design.md`
  (new AC-006), `docs/user-workflow.md` — the contract this item implements.

### Validation run

| Command | Observed result |
|---|---|
| `cd studio && npx vitest run src/studio/version-control` | 8 files, 91 tests passed |
| `cd studio && npm test -- --run` | 89 files, 597 tests passed |
| `cd studio && npm run build` (`tsc -b && vite build`) | exit 0, built |
| `cd studio && npm run lint` (oxlint) | 0 errors, 16 warnings; none in a changed file |
| `dotnet build AgentAssistPlcDev.sln -v q` | 0 errors, 3 warnings |
| `dotnet test tests/Agent.Tests/Agent.Tests.csproj` | 514 passed |
| `dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj --no-build` | 226 passed |
| `dotnet test tests/E2E.Tests/E2E.Tests.csproj --no-build` | 14 failed, 2 passed — **pre-existing**: the same 14 fail at base `c0c7c88` in a clean `git worktree`, with `InvalidOperationException: close_session` from the test's own engineering stub during workbench creation, a path this item does not touch |

`dotnet test AgentAssistPlcDev.sln` reported 3 extra ApiHost failures and the same 14 E2E failures
while the projects ran in parallel; ApiHost.Tests passes 226/226 on its own, so those three are
parallel-run interference (shared ApiHost port/temp state), not this change.

### Done when checks

1. **Passed.** `TaskStageBaselineTests.CompareReportsTheStagedObjectsDifferenceAsACommitReadyRow`:
   one `Changed` difference at `devices/PLC_1/source/Blocks/Main.xml`, `Code` reported as changed
   and `Interface` as matching, a non-empty `TiaFingerprint` (the identity #111's accept path
   verifies), `ComparedTaskId`, `HardwareChecked == false`, no stage problems, and only
   `["block-main"]` sent to TIA under the committed-manifest baseline.
2. **Passed.** `MasterSynchronizationTests.ApplyNeverCertifiesTheManagedSourceBaselineFromATaskScopedComparison`:
   a scoped comparison whose every difference is accepted records `ManagedSourceConsistent` as not
   true, while the project-wide comparison in the neighbouring test still certifies it.
3. **Passed (component tests).** `VersionControlPanel.test.tsx`: the selector reads `Full scan` /
   `Task only`; task-only is the default scope with staged objects and falls back to Full scan
   without them; hardware verification is disabled in task-only scope.
   `VersionControlCompare.test.tsx`: a scoped difference renders a selectable row showing
   `Changed: Code` and reports `comparison-1` with its path; the task-clean wording appears only
   without stage problems, and a stage problem suppresses it.
4. **Passed**, except the pre-existing E2E failures recorded above (verified at base, not caused
   here).
5. **Partly verified live; a real TIA comparison is deferred.** `.\launch.ps1 -NoBuild` brought the
   rebuilt ApiHost (5239, `/api/status` 200) and Studio (5173) up with the in-process assistant
   healthy, and a headless browser check on the worktree landing page observed: the scope selector
   reading `Full scan` / `Task only`; the Full scan default with hardware verification enabled on a
   worktree with no eligible active task; switching to Task only keeping the selection, disabling
   the hardware checkbox, disabling Compare, and explaining "Select a task for this worktree to
   compare only its staged source objects."; and no console errors. Screenshots:
   `tmp/vc-runtime/04-task-only-unavailable.png` (and `01-home.png`, `02-workbench.png`,
   `03-dock.png`). Not verified live: a comparison against a real TIA project with a staged task,
   and the commit that follows it — the scoped accept/commit path is proven at the fake-TIA level
   (check 2), not against TIA.

### Notes, decisions and risks

- **Decision:** the scoped comparison returns the project-wide result type instead of a scoped DTO.
  That is what makes one difference list, one selection and one accept path possible; the alternative
  (a second commit route for task candidates) would have duplicated the guarded commit transaction
  and the identity check #111 added. The price is that `WorkbenchConsistencyResult` grows two
  optional fields and the route's response shape changes — an API change, recorded here.
- **Decision:** the default scope is decided from the active task's readiness, and a scope the user
  picked is never overridden. `refresh()` no longer bumps the task-read signal (the Refresh control
  does), so mounting the dock reads the active task once instead of twice.
- **Risk (unchanged by this item, recorded for 013):** `CreateNativeSavepointAsync` still guards only
  on Git status, so a TIA-only change outside every task stage is invisible to both the Changes list
  and a task-only compare, and does not block a savepoint. With task-only now the default scope, that
  gap matters more, not less. `docs/design/task-scoped-tia-compare-design.md` AC-004 already requires
  the full-scan gate; it is not implemented.
- **Risk (recorded for 012):** `TryRecordTaskStageEvidenceAsync` still refreshes every stage of the
  task rather than only the committed objects, and the accept path still passes no
  `taskEvidenceTaskId`, so a task-only commit does not advance the task's stage baseline. Committing
  through this item therefore leaves the next task-only compare reporting the same object until it
  is re-staged.
- **Adjacent, not fixed:** the test engineering boundary in
  `tests/E2E.Tests/WorkbenchLifecycleTests.cs` lacks `close_session`, which is why 14 E2E tests fail
  at base. It is unrelated to this item and wants its own fix.
