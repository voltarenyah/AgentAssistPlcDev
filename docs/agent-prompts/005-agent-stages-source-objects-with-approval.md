# 005. Agent stages source objects for the current task through an approval card

Status: done
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

Branch `codex/005-agent-stage-approval`; commits `db0c671` (implementation) and the documentation
commit that carries this update.

### Tool and approval contract

`stage_task_source_object(objects: [{ sourceObjectId, takeOverFromTaskId? }])`, classified
`Destructive`, so `AgentSandbox` parks the turn on the shared approval card and
`POST /api/chat/confirm/{id}` resolves it; the card receives the full argument JSON, not the
160-character audit summary. `sourceObjectId` is the stable `{deviceId}:{sourceId}`; a bare id, name,
or relative path is resolved against the device manifest. Errors (no existing code or schema changed):
`ACTIVE_TASK_REQUIRED`, `TASK_DEVICE_MISMATCH`, `TASK_SOURCE_DEVICE_MISMATCH`,
`SOURCE_ALREADY_STAGED` (owner not named, or named wrongly), `TASK_DEVICE_REQUIRED`,
`GRAPH_TARGET_NOT_FOUND`.

### Files changed

- `src/ApiHost/TaskSourceStagingTool.cs` (new) — the tool: active task via `ActiveTaskContextService`,
  device/owner validation before the first write, and the stage through
  `WorkbenchCoordinator.StageTaskSourceObjectAsync` (ADR-0003 baseline rule stays in one place).
- `src/ApiHost/CompatibilityEndpoints.cs` — the device conversation's catalogue appends the in-process
  spec (the MCP engineering server is a separate process with no workbench access).
- `src/Contracts/Sandbox/SandboxPolicy.cs` — the tool's `Destructive` tier (unclassified tools fail
  closed, so without this the call would be refused instead of confirmed).
- `src/Agent/Chat/AgentSandbox.cs` — full arguments on the card for this tool.
- `studio/src/studio/MainStudio.tsx` — approving this call reloads the open task detail and bumps a
  stage refresh token.
- `studio/src/studio/workbench/TaskSourceObjectsSection.tsx`, `TaskDetail.tsx` — the section re-reads
  its stages when that token changes.
- `tests/ApiHost.Tests/TaskSourceStagingToolTests.cs` (new),
  `studio/src/studio/MainStudio.stageApproval.test.tsx` (new),
  `studio/src/studio/workbench/TaskSourceObjectsSection.test.tsx` — the checks below.

### Commands and observed results

| Command | Result |
|---|---|
| `dotnet build AgentAssistPlcDev.sln -v q` | Build succeeded, 0 errors |
| `dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj -v q` | Passed 182/182 (5 new) |
| `dotnet test tests/Agent.Tests/Agent.Tests.csproj -v q` | Passed 465/465 |
| `dotnet test tests/Contracts.Tests/Contracts.Tests.csproj -v q` | Passed 112/112 (policy table changed) |
| `cd studio && npm test -- --run` | 85 files, 542 tests passed |
| `cd studio && npm run build` | `tsc -b` + `vite build` succeeded |

### Done when

1. Passed. `ApprovingTheCallStagesTheRequestedObjectsForTheActiveTask` runs the real `AgentSandbox` gate
   with AllowOnce and asserts both objects are staged with the committed manifest's fingerprint
   evidence; `RejectingTheCallLeavesTheStageListAndTheTaskUnchanged` runs the same gate with Deny,
   asserts the `SANDBOX_USER_DENIED` verdict, and asserts no stage exists for any task and the task row
   is unchanged.
2. Passed. `AForeignDeviceObjectIsRefusedAndTheCurrentOwnerIsReportedBeforeATakeOver` refuses
   `device-2:block-main` with `TASK_SOURCE_DEVICE_MISMATCH` and no write; reports the other active task's
   title and id with `SOURCE_ALREADY_STAGED`; refuses a wrong owner claim with the real owner; and only
   with the owner named releases it first and then stages. `WithoutAnActiveTaskTheToolReportsTheMissingContext`
   and `TheApprovalCardCarriesTheFullArgumentsSoTheOwnerIsNamedBeforeTheWrite` cover the missing-context
   rule and the card content.
3. Partly passed, limitation recorded. `MainStudio.stageApproval.test.tsx` proves the device
   conversation's approval card appears for this call (showing the object being approved) and that
   approving posts `confirmTool(id, 'allowOnce')`; the task page's Source objects section then shows the
   approved object. `TaskSourceObjectsSection.test.tsx` proves the page-level refresh token makes the
   section re-read its stages. A single-DOM end-to-end proof (card *and* an already-open task page) is
   impossible: `MainStudio` renders `TaskDetail` in place of the workspace host, so the two are never
   mounted together; the device path relies on the section's mount read after the approval.
4. Passed — see the command table.
5. Skipped, deferred in this unattended run. `.\launch.ps1` was not started (shared ports 5173/5239, no
   TIA available) and no live model call was made, so the real conversation, the live
   `/api/logs` → `POST /api/chat/confirm/{id}` round trip, and the browser task page were not
   exercised. That is the residual risk recorded below.

### Deviations

- The tool lives in-process in ApiHost, not in `src/Mcp.Engineering/Tools/EngineeringTools.cs` as the
  index row suggested: staging needs the workbench graph and `WorkbenchCoordinator`, which are in the
  ApiHost process. The index row was corrected.
- Exposing the same tool in the Workbench Assistant panel was considered and deliberately left out: it
  is a new surface and therefore a maintainer decision. Recommended follow-up if the panel should stage
  for the active task too.

### Residual risk

- A take-over releases the current owner and then stages; a failure in between leaves the object
  unstaged. That is the existing release-then-stage rule the task page already uses.
- A multi-object call validates every object before the first write, but the stages are not one
  transaction: a concurrent stage of the same object can fail a later object in the batch
  (`SOURCE_ALREADY_STAGED`) after earlier ones were staged.
- The live model's choice of `sourceObjectId` and the suspended-turn approval round trip are covered
  only by the tests above until the runtime step is run.

