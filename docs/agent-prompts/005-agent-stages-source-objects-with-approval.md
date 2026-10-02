# 005. Agent stages source objects for the current task through an approval card

Status: pending
Created: 2026-09-30
Depends on: 002

## Goal

Inside a device conversation, the agent can propose adding PLC source objects to the active task's
stage, and the change happens only after the user approves it on the existing approval card. The
approved objects appear in the task page's Source objects section with a usable baseline; rejecting
the card leaves the task unchanged.

## Context

- No staging tool exists for the agent today: `src/Mcp.Engineering/Tools/EngineeringTools.cs` exposes
  engineering tools such as `capture_source_evidence` (`:144-149`) but nothing that stages a source
  object for a task.
- The approval mechanism is the existing `AgentSandbox` confirmation flow, not an `interrupt` frame: a
  destructive call suspends the turn, the card is read from the shared server log as
  `kind: confirmation`, and it is answered through `POST /api/chat/confirm/{id}` (documented in the
  root `AGENTS.md`, "Open and test the web application").
- The active task for the worktree is resolved by `ActiveTaskContextService`; the commit attribution
  path uses it the same way (`src/Agent/Workbench/WorkbenchCoordinator.cs:3922-3929`, and
  `EngineeringGraphCommitAttribution.cs:77-79`).
- The staging write path, its constraints, and the baseline rule are defined by item 002:
  `EngineeringGraphService.StageSourceObject`
  (`src/Agent/Workbench/EngineeringGraph/EngineeringGraphService.cs:66-95`), the route
  `POST /api/workbenches/{id}/worktrees/{wt}/tasks/{taskId}/stages`
  (`src/ApiHost/WorkbenchApiModels.cs:919-933`), and the one-active-owner-per-source-object rule
  (`EngineeringGraphSchema.cs:126-134`).
- Staging is device-scoped: the object id is `"{deviceId}:{sourceId}"` and a staged object must belong
  to the task's device, otherwise the compare rejects it with `TASK_STAGE_INVALID`
  (`WorkbenchCoordinator.cs:3631-3635`).
- Line numbers here are from commit `366bd16`; this item's anchors are backend and stable, but locate
  each by symbol name and confirm its current line before relying on it.

## Constraints

- The agent's staging must go through the `AgentSandbox` approval card. A silent write, or one
  performed before approval, is out of bounds for this item.
- An approved call stages exactly the objects the user approved. Rejecting the card leaves the stage
  list, the commit gate, and the task unchanged.
- The agent may only stage source objects of the active task's device in the active worktree. Objects
  owned by another active task follow the existing take-over rule (release, then stage) and the
  approval card must name the current owner before the write.
- Keep the non-task and no-active-task conversation behavior unchanged: with no active task the tool
  reports the missing context instead of guessing a task.
- Do not change the staging schema, the compare contract, or the existing error codes. Item 002 owns
  how a stage acquires its baseline; this item reuses that path.
- Follow the existing tool-result conventions of `EngineeringTools.cs` and the colocated-test
  convention in `studio/AGENTS.md`; add no dependency.

## Done when

1. A server test proves the tool stages the requested objects for the active task on approval and
   makes no change when the call is rejected.
2. A server test proves the tool refuses an object that does not belong to the active task's device,
   and reports the current owner when the object is owned by another active task.
3. A frontend test proves the approval card appears for this call and that approving it refreshes the
   task page's Source objects section.
4. `dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj --no-build -v q`, `cd studio && npm test --
   --run`, and `npm run build` pass.
5. Runtime check with `.\launch.ps1` in a device conversation: ask the agent to add a named block to
   the active task, confirm the card appears, and confirm the block appears in the task page after
   approval. Record whether TIA was available and which steps could not run.

## Evidence

Not yet executed.
