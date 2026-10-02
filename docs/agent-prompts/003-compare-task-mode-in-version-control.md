# 003. Compare task mode in the version-control surface

Status: pending
Created: 2026-09-30
Depends on: 002

## Goal

The version-control surface distinguishes **Compare task** from a project-wide **Full scan**. Compare
task compares only the active task's staged source objects against TIA and says `This task is in
sync` when the task matches, never claiming the project is clean. Full scan keeps its current
project-wide result.

## Context

- Required by `docs/ui-spec/task-scoped-tia-compare-ui-spec.md`: row "Compare task with TIA | Active
  task has staged objects | Shows only task differences and labels a clean result `This task is in
  sync`", component row "Compare mode selector | Extend `VersionControlPanel` | Distinguishes
  `Compare task` from `Full scan`", and the acceptance row "Task clean is not project clean".
- `docs/design/task-scoped-tia-compare-design.md` AC-002 — a clean scoped result is labelled
  task-clean and cannot permit a project-wide claim or savepoint.
- The client function already exists with **no caller**: `compareTaskWithTia`
  (`studio/src/api/client.ts:1411-1412`, route `POST /workbenches/{id}/worktrees/{wt}/tasks/{taskId}/compare-tia`,
  response type `TaskSourceComparison` at `:1410`).
- The backend is implemented and correctly scoped:
  `WorkbenchCoordinator.CompareTaskWithTiaAsync` (`src/Agent/Workbench/WorkbenchCoordinator.cs:3595-3656`)
  reads only `ListActiveStages`, calls `compare_source_evidence` with `sourceObjectIds`, and returns
  `candidates`, `candidateExports`, `problems` and `observedSoftwareChecksum`. Its codes are
  `TASK_STAGE_EMPTY`, `TASK_STAGE_BASELINE_MISSING`, `TASK_STAGE_BASELINE_INVALID`,
  `TASK_STAGE_MISSING`, `TASK_STAGE_INVALID`.
- What the UI does today: `studio/src/studio/version-control/VersionControlCompare.tsx:58` always
  compares the whole project with operation type `compare-tia`;
  `VersionControlWorkflow.test.tsx:12,41-53` asserts the `/vc/compare-tia` request path and
  `includeHardware=false`.
- The active task for a worktree comes from `ActiveTaskContextService`; the task detail state lives in
  `MainStudio` (`taskDetail*`, `studio/src/studio/MainStudio.tsx:497-501`) and the version-control
  surface receives its context from `MainStudio.tsx:2659-2669`.

## Constraints

- A task compare must never produce or enable a project-clean label, validation evidence for the
  whole project, or an SVN savepoint.
- Keep the existing project-wide compare behavior and its request shape unchanged; the mode selector
  adds a path, it does not replace one.
- Render the task-compare problems instead of hiding them: `TASK_STAGE_BASELINE_MISSING` and
  `TASK_STAGE_BASELINE_INVALID` tell the user the object has no committed baseline yet,
  `TASK_STAGE_MISSING` means the object is gone or unreadable in TIA.
- With no active task, or a task with no stages (`TASK_STAGE_EMPTY`), the Compare task mode is
  unavailable and explains why (add source objects to the task first) instead of failing a request.
- Follow `docs/STYLEGUIDE.md` and the colocated-test convention in `studio/AGENTS.md`; add no
  dependency. Do not change the project-wide compare wording used elsewhere.

## Done when

1. Component tests prove: the mode selector offers Compare task and Full scan; Compare task calls the
   task route for the active task and not `/vc/compare-tia`; a clean result renders `This task is in
   sync` and no project-clean wording; each problem code above renders an explanatory state.
2. A test proves Compare task is unavailable with a clear reason when there is no active task or the
   task has no staged objects.
3. `cd studio && npm test -- --run` and `npm run build` pass.
4. Runtime check with `.\launch.ps1` on a worktree with a staged task: run Compare task and confirm
   the result describes only that task's objects. Record whether TIA was available; if the compare
   itself could not run, state that and what remains unverified.

## Evidence

Not yet executed.
