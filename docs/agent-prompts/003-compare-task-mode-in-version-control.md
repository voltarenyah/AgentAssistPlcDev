# 003. Compare task mode in the version-control surface

Status: done
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
  (`studio/src/api/client.ts:1430`, route `POST /workbenches/{id}/worktrees/{wt}/tasks/{taskId}/compare-tia`,
  response type `TaskSourceComparison` at `:1429`).
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
  `MainStudio` (`taskDetail*`, `studio/src/studio/MainStudio.tsx:505-509`) and the version-control
  surface receives its context from `MainStudio.tsx:2797-2809`.
- Line numbers here are from commit `366bd16`. `client.ts` and `MainStudio.tsx` shift often; locate
  every anchor by symbol name and confirm its current line before relying on it.

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

Status `done`. Done when 4 (the runtime TIA check) is deferred as an explicit policy of this
unattended run — `.\launch.ps1` must not be started (shared ports 5173/5239, no TIA available) — so a
real task comparison was not executed. Every other check below was run.

- Branch: `codex/003-compare-task-mode`
- Implementation commits: `3010877` — `feat: add compare-task mode to the version-control surface (003)`;
  `4d3c489` — `fix: take the compare-task target from the worktree active task (003)`
  (this documentation update is a follow-up commit on the same branch)

### Files changed

- `studio/src/studio/version-control/VersionControlCompare.tsx` — `mode`/`taskId`/`taskTitle` props;
  `compareActiveTask` calls `api.compareTaskWithTia` and clears the project commit selection;
  task-result rendering with the `TASK_STAGE_*` explanatory states, candidate/exports list, and the
  `This task is in sync` clean state; scope/task change clears the other scope's result. The
  project-wide path and its request shape are untouched.
- `studio/src/studio/version-control/VersionControlPanel.tsx` — the `Full scan` / `Compare task`
  `ToggleGroup` selector (Full scan stays the default); it reads the worktree's active task through
  `getActiveWorktreeTask` (`GET …/worktrees/{wt}/active-task`, the `ActiveTaskContextService` value
  MainStudio records on task selection) and that task's staged objects through `listTaskSourceStages`;
  renders the unavailable reason. The active task is read only in task mode.
- `studio/src/studio/version-control/VersionControlChanges.tsx` — threads `compareMode`/`activeTaskId`/
  `activeTaskTitle` to `VersionControlCompare`; default stays `full`.
- `studio/src/studio/MainStudio.tsx` — unchanged by the final commit: the panel reads the active task
  itself, so the earlier open-detail wiring was removed.
- `VersionControlCompare.test.tsx` (+10 tests), `VersionControlPanel.test.tsx` (+8 tests),
  `VersionControlWorkflow.test.tsx` (+1 test) — colocated component tests plus the route-shape test.

### Validation run

Final run, after `4d3c489` (commands from `studio/`):

| Command | Observed result |
|---|---|
| `npm test -- src/studio/version-control` | 8 files, 89 tests passed (70 before this item; +19) |
| `npm test -- --run` | 83 files, 550 tests passed (baseline 531; +19 for this item) |
| `npm run build` (`tsc -b && vite build`) | exit 0, `✓ built in 555ms`; only the pre-existing >500 kB chunk warning |
| `npm run lint` (oxlint) | 0 errors, 15 warnings — all pre-existing, none in the changed files |

The earlier commits were validated the same way (84 / 547 tests, build exit 0, 0 lint errors) before
`4d3c489` changed the active-task source.

### Done when checks

1. Passed (component tests): the selector offers `Full scan` and `Compare task`; Compare task calls
   `compareTaskWithTia('wb-1','wt-1','task-1', …)` for the worktree's active task and
   `compareMasterWithTia` is never called (`VersionControlPanel.test.tsx`,
   `VersionControlCompare.test.tsx`); `VersionControlWorkflow.test.tsx` proves the request path
   contains `/workbenches/wb-1/worktrees/wt-1/tasks/task-1/compare-tia` and **not** `/vc/compare-tia`;
   a clean result renders `This task is in sync` with no project-clean wording, no project clean hero,
   no project-only affordance and no project comparison; each of `TASK_STAGE_EMPTY`,
   `TASK_STAGE_BASELINE_MISSING`, `TASK_STAGE_BASELINE_INVALID` and `TASK_STAGE_MISSING` renders an
   explanatory state.
2. Passed: with no active task the mode shows "Select a task for this worktree…" and Compare is
   disabled; with a task whose stage list is empty it shows "This task has no staged source objects
   yet. Add source objects to the task first."; a project-scope active task and a task with no PLC
   binding each render their own reason and never reach the stage read; no request is fired in any of
   these states.
3. Passed: see the validation table.
4. **Skipped (deferred by the unattended-run policy).** `.\launch.ps1` was not started and TIA was not
   available, so no real task comparison ran. Unverified: the live ApiHost round-trip for the task
   route, the actual `TASK_STAGE_*` payloads from a real TIA project, and the rendered result against
   real candidate/export data.

### Deviations and decisions

- `client.ts` was **not** changed; `compareTaskWithTia`, `getActiveWorktreeTask` and
  `TaskSourceComparison` are reused as-is.
- The active task comes from the worktree-level `GET …/active-task` (`ActiveTaskContextService`), as
  the item's Context specifies, not from the open task detail; `MainStudio.tsx` therefore needs no
  change and the final diff does not touch it.
- The task comparison reports the operation as kind `compare-tia`, so the existing title-bar operation
  status and the commit-control hiding in `VersionControlChanges` keep working without changing
  MainStudio's operation filter.
- A task result calls `onSelectionChanged(null, [])`, so a task-clean result clears any project
  comparison selection and cannot feed a project commit.
- `TASK_STAGE_INVALID` (also in the backend's code list) is mapped to an explanatory state even though
  the item only required the four codes above.
- The savepoint guard for unresolved full-scan results is out of this item's scope; the snapshot area
  is unchanged.

### Residual risk

- No runtime/TIA verification (Done when 4, deferred by the run's policy): the task route has never been
  called against a real ApiHost or TIA project in this worktree.
- The active task is re-read when Compare task is selected, when the worktree changes, and when
  "Refresh version control" is used. If the user switches tasks while the mode already reads
  `Compare task` and compares without a refresh, the previous task is compared; the result names the
  compared task in its heading, and selecting the mode again re-reads.
- If the stage list changes between the availability check and the click, `TASK_STAGE_EMPTY` still
  arrives as a request failure and is rendered as the explanatory state rather than a raw error.
- The task result does not offer a commit path; committing staged task sources remains with the
  project compare flow and is unchanged by this item.


