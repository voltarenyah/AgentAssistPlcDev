# 011. The commit says which task it belongs to

Status: done
Created: 2026-10-06
Depends on: 010

## Goal

The version-control commit controls name the task the commit will be linked to — the worktree's
active task by default — and let the user change it. Changing it makes that task the worktree's
active task, so the commit's attribution, the Task only compare scope, and the agent's task context
all follow the same choice. A refused task commit says which selected objects are not staged on that
task and which task currently stages them.

## Context

- Requirement (human, 2026-10-06): "提交页面我希望增加一个task选择框，默认选中当前的active task，至少让用户看到这个commit会归到哪里，如果不对用户还可以修改 … 保留一定灵活性的基础上力求准确". Decision 5c from that discussion: an object another active task owns is refused rather than silently taken over.
- Attribution already follows the worktree's active task: `ApplyTiaSynchronizationAsync` commits through `CommitSourceAsync` → `AssociateCommit` → `EngineeringGraphCommitAttribution.Associate`, whose `activeTaskId` is null there, so `ActiveTaskContextService.Get(graph, worktreeId)` decides (`EngineeringGraphCommitAttribution.cs:77-79`). `/vc/commit` derives its task the same way (`WorkbenchApiModels.cs:1237-1240`). The active task is therefore the single existing mechanism for "the task this work is part of", and the picker can use it instead of adding a second one.
- `GET .../worktrees/{wt}/engineering-tasks` (`listGraphWorktreeTasks`) already returns the worktree's tasks with `deviceId`/`targetKind`; `PUT .../worktrees/{wt}/active-task` (`setActiveWorktreeTask`) already persists the choice, and `VersionControlPanel` already reads the active task to decide the Task only scope.
- Two defects found in the guard this item touches:
  1. `/vc/commit` rejected a message-only commit (an untrackable or safety change, `paths: []`) whenever the worktree had an active task, because the guard tested `requestedPaths.Length == 0 || …` (`WorkbenchApiModels.cs:1252`). The control has existed since item 003's surface, and the commit it starts is a legitimate task-attributed commit.
  2. The refusal said only "A task commit may contain only active staged source objects.", so a user could not tell which object was wrong or whose it was.

## Constraints

- One mechanism for the commit's task: the worktree's active task. Do not add a second task field to the commit request, a second attribution parameter, or a task-scoped commit route.
- Keep the accuracy rules: a task commit may contain only objects staged on that task, and taking an object over from another active task stays an explicit action, never a side effect of committing.
- A comparison belongs to the task it covered: when the worktree's task changes, its selection must not stay available for a commit.
- Follow `docs/STYLEGUIDE.md` and the local `select` convention in `VersionControlChanges`' own directory (`NativeStorePanel`); add no dependency.

## Done when

1. The commit controls show the worktree's device-bound tasks in a selector, defaulting to the active task, and the note says the commit is linked to it.
2. Changing the selector persists the choice as the worktree's active task and re-reads the task context; the selector cannot be used while that change is in flight.
3. A comparison's selection is withdrawn when the covered task changes.
4. A message-only commit is accepted while a task is active, and a task commit naming an object outside its stages is refused with the path and, when another task stages it, that task's title.
5. `cd studio && npm test -- --run`, `npm run build`, `npm run lint`, `dotnet build AgentAssistPlcDev.sln`, `dotnet test tests/Agent.Tests/ApiHost.Tests`-level checks pass.
6. Runtime check with `.\launch.ps1`: the selector is visible in the dock on a worktree with an active task and lists that task first.

## Evidence

Branch `codex/010-task-only-compare` (item 010's branch, continued). Both defects above were
pre-existing and are fixed here because this item makes the commit's task explicit; nothing was
pushed and no PR was created.

### What changed

- `studio/src/studio/version-control/VersionControlPanel.tsx` — reads the same worktree's
  device-bound tasks (`listGraphWorktreeTasks`) alongside the active task, and
  `switchCommitTask` persists a chosen target through `setActiveWorktreeTask` and re-reads the task
  context, so attribution, the Task only scope and the agent's task context all follow it.
- `studio/src/studio/version-control/VersionControlChanges.tsx` — a **Commit to task** selector in
  the commit controls, defaulting to the active task, saying what the commit is linked to, disabled
  while a switch is in flight, and absent when no worktree task can take a commit.
- `studio/src/studio/version-control/VersionControlCompare.tsx` — a scope or covered-task change now
  withdraws the comparison's selection (`onSelectionChanged(null, [])`), so a commit control can
  never offer another task's comparison.
- `src/ApiHost/WorkbenchApiModels.cs` — the task guard no longer rejects an empty path list (a
  message-only commit belongs to its task), and its refusal names the offending paths through the new
  `DescribeUnstagedPaths`, which adds the owning task's title when another task of this worktree
  stages the object.
- Tests: `VersionControlChanges.test.tsx` (+3: the selector, the in-flight state, the absent
  selector), `VersionControlPanel.test.tsx` (+2: the target list and the switch),
  `VersionControlCompare.test.tsx` (the covered-task change now also asserts the withdrawn
  selection), `WorkbenchEndpointsTests.cs` (the refusal names the path and the owning task; a new
  `MessageOnlyCommitStillBelongsToTheActiveTask`).
- `docs/ui-spec/task-scoped-tia-compare-ui-spec.md` — the commit-target row.

### Validation run

| Command | Observed result |
|---|---|
| `cd studio && npx vitest run src/studio/version-control` | 8 files, 96 tests passed (91 before, +5) |
| `cd studio && npm test -- --run` | 89 files, 602 tests passed. A concurrent run (while the .NET suite was also running) reported 8 failures; the clean re-run passed 602/602, so those were load-induced, not caused by this change |
| `cd studio && npm run build` | exit 0, built |
| `cd studio && npm run lint` | 0 errors, 16 warnings; none in a changed file |
| `dotnet build AgentAssistPlcDev.sln -v q` | 0 errors, 8 warnings |
| `dotnet test tests/Agent.Tests/Agent.Tests.csproj` | 514 passed |
| `dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj` | 227 passed (226 before, +1) |
| `dotnet test … --filter ActiveTaskCommitRejectsFilesOutsideItsStages\|MessageOnlyCommitStillBelongsToTheActiveTask` | 2 passed |

### Done when checks

1. **Passed (component tests).** `VersionControlChanges`: the selector lists the tasks with the
   active one selected and the note says the commit is linked to it; it is absent when no worktree
   task can take the commit. `VersionControlPanel`: the worktree's device-bound tasks are read and
   offered.
2. **Passed.** Changing the selector calls `setActiveWorktreeTask('wb-1','wt-1','task-2')` and
   re-reads the active task (the panel test asserts both); while `switchingCommitTask` the select is
   disabled and the note says it is switching.
3. **Passed.** `VersionControlCompare` withdraws the selection when the covered task changes.
4. **Passed.** `MessageOnlyCommitStillBelongsToTheActiveTask` proves a `paths: []` untrackable commit
   with an active task reaches the coordinator's commit (it failed with `TASK_COMMIT_STAGE_MISMATCH`
   before), and `ActiveTaskCommitRejectsFilesOutsideItsStages` proves the refusal names the path and
   `staged on 'Other task'` when another task owns it.
5. **Passed** — see the table.
6. **Partly verified live.** `.\launch.ps1 -NoBuild` rebuilt the app; with the worktree's
   `Cav_B 边沿触发条件用错` temporarily active, the dock defaulted to **Task only** with hardware
   verification disabled and Compare enabled — the "task can scope it" branch item 010 could not
   reach — and the active task was then restored to none (`activeTask: null`), with no console errors.
   The selector's own rendering was **not** observed live: the commit controls only render once the
   Changes list has entries or a compare ran, and I did not trigger a compare against the live TIA to
   avoid attaching to your session. Screenshot: `tmp/vc-runtime/05-task-scope-ready.png`.

### Notes, decisions and risks

- **Decision:** the commit's task is the worktree's active task, chosen through the existing
  `setActiveWorktreeTask`, rather than a new commit-request field plus a second attribution
  parameter. The explicit-field design would also have had to relax `TASK_COMMIT_TASK_MISMATCH` and
  let a commit attach to a task the dock is not comparing, which is less accurate, not more.
- **Deviation from decision 5c:** the row-level treatment is the *refusal*, not a greyed-out row. The
  backend names the path and the owning task, which is the same accuracy signal; greying the row in
  the dock would need a path → source-object → owning-task read on every render. Worth its own item
  if the toast proves too late in practice.
- **Risk:** the commit target is persisted state, so picking a task changes the worktree's task for
  every surface (the panel re-reads and the Task only scope follows). The note states this; there is
  no "switch back" affordance beyond picking the other task again.
- **Not verified live:** the selector rendering, and a real commit attributed to a switched task.
