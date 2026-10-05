# 008. The agent can find the source object id it stages, and a wrong id names the candidates

Status: done
Created: 2026-10-06
Depends on: 005

## Goal

In a device conversation, the agent no longer has to guess a `sourceObjectId`. It reads the device's
staged-comparable source objects — canonical `{deviceId}:{sourceId}` plus who stages each one — from a
read-tier tool, and passes that id to `stage_task_source_object`. When it still passes something that
does not resolve, the tool answers with its own `GRAPH_TARGET_NOT_FOUND` and the candidate ids instead
of a code-less dead end, and a knowledge-graph node id such as `block:202_VocDoorGeneral` is no longer
misdiagnosed as another device's object.

Before (observed in a live conversation): the agent had one write-only staging tool and no way to list
source objects. It took `block:202_VocDoorGeneral` from the knowledge base, got
`TASK_SOURCE_DEVICE_MISMATCH` ("belongs to device 'block'") whose remediation told it to add the
`{deviceId}:` prefix, then got `Source object was not registered.` on the prefixed value. The only tool
that exposes a stable id, `capture_source_evidence`, needs a live TIA session with exclusive access,
returns a whole-project snapshot, and its result was truncated to a field-name skeleton, so the id was
unreachable.

After: `list_source_objects` (read, no approval card) answers the question directly, and every failing
id produces a code, a reason and the candidates.

## Context

- `src/ApiHost/TaskSourceStagingTool.cs:224-262` — `CanonicalId`. Two defects:
  - `:237-244` treats any colon in the value as a claimed device id, so `block:202_VocDoorGeneral`
    is refused with `TASK_SOURCE_DEVICE_MISMATCH` for device `block`, and the remediation
    (`:243`) sends the caller to the prefixed form;
  - `:227-235` returns a value that already starts with `{deviceId}:` **unvalidated**, so the prefixed
    value reaches `EngineeringGraphService.StageSourceObject`
    (`src/Agent/Workbench/EngineeringGraph/EngineeringGraphService.cs:84-85`), which throws
    `EngineeringGraphConstraintException("Source object was not registered.", "GRAPH_TARGET_NOT_FOUND")`.
    That type is not a `ToolCallException`, so `src/Agent/Chat/AgentLoop.cs:694-704` reports it as
    `AGENT_TOOL_ERROR` with `remediation: null` — the graph's code and the tool's candidate advice are
    both lost.
- `src/ApiHost/TaskSourceStagingTool.cs:246-261` **already** resolves a bare id, a name, or a
  manifest-relative path against the device manifest. The tool description (`:41-47`) never says so.
- The id space is not the knowledge base's id space. The workbench id is
  `{deviceId}:{base64url(SHA256("{category}|{sourcePath}"))}`
  (`src/Mcp.Engineering/Export/StableId.cs:13-18`, `ExportManifest.cs:34-42,101`; the manifest's own
  `id`, falling back to `source:{relativePath}` — `src/Agent/Workbench/DeviceSnapshot.cs:322-335`).
  The knowledge base's node ids are `block:{name}`, `db:{name}`, `udt:{name}`, `type:{name}`,
  `symbol:{name}`, `io:{address}`, `udt-member:{udt}:{path}`, `db-member:{db}:{path}`
  (`src/Mcp.Knowledge/Graph/SemanticPlcGraph.cs:745-789`). ApiHost does not link the knowledge server.
- The device's source objects are already listed for the task page:
  `GET /api/workbenches/{id}/worktrees/{wt}/devices/{device}/source-objects`
  (`src/ApiHost/WorkbenchApiModels.cs:1792-1802`), comparable-only, and the picker posts
  `{deviceId}:{id}` to `POST .../tasks/{taskId}/stages` (`:953-979`). No agent tool exposes either.
- In-process registration point for workbench tools: `src/ApiHost/CompatibilityEndpoints.cs:945-957`.
  Sandbox tiers are fail-closed, so a new tool needs a tier: `src/Contracts/Sandbox/SandboxPolicy.cs`.
- Runtime evidence for the fix (recorded during the read-only investigation, this machine):
  device `5b7080b05d3b4ff69b1176209de193d8`, workbench `SWT2-PEI-N`, worktree `master`;
  `202_VocDoorGeneral` is manifest id `yHhBhWzZvclfN3RXWeg2F6PqswugvWCwiS55Y-wNpIk`, category `FB`,
  sourcePath `15_VOC_Door/202_VocDoorGeneral`, relativePath
  `Blocks/15_VOC_Door/202_VocDoorGeneral [FB170].xml`; 774 registrable components, no duplicate names,
  so the bare name resolves uniquely.
- 005's own residual risk (`005-agent-stages-source-objects-with-approval.md:155-156`) records that the
  live model's choice of `sourceObjectId` was never exercised at runtime. This item is that gap.

## Constraints

- Do not change the staging schema, the compare contract, or the meaning of any existing error code.
  `GRAPH_TARGET_NOT_FOUND` and `TASK_SOURCE_DEVICE_MISMATCH` keep their meanings; the new tool is the
  only added surface.
- The new tool is read-tier and must not write the graph: no entity registration, no projection
  invalidation, no stage. It reads the same manifest the staging path resolves against, so the id it
  reports is exactly the id staging accepts.
- The new tool lists the same object set the task page's picker offers (instance DBs excluded: they are
  outside the managed-source evidence domain and can never carry a baseline).
- Register the tool in-process in `CompatibilityEndpoints` (the MCP engineering server is a separate
  process with no workbench access), and give it a `SandboxTier` entry (unclassified tools fail closed).
- Do not add an ApiHost → Mcp.Knowledge assembly reference for the node-kind vocabulary; declare the
  cross-domain prefixes in the staging tool with a comment naming `SemanticPlcGraph` as their source.
- Keep the existing device-chat behavior with no active task, no selected device, and a foreign device
  object unchanged.
- No new dependency; follow the existing tool-result and colocated-test conventions.

## Done when

1. `TaskSourceStagingToolTests` prove: `block:Main` and `device-1:block:Main` both stage
   `device-1:block-main`; an unresolvable value returns `GRAPH_TARGET_NOT_FOUND` with the candidate ids
   in the message; `device-2:block-main` still returns `TASK_SOURCE_DEVICE_MISMATCH`.
2. A test proves a graph-level `EngineeringGraphConstraintException` reached through the coordinator is
   reported as a `ToolCallException` carrying the graph's code, not as a code-less failure.
3. Tests prove the list tool returns canonical ids, filters by `query`/`category`, pages with
   `limit`/`offset`, reports the staging owner of each object, and needs no active task.
4. `SandboxPolicyTests` classifies the new tool as `Read`; `SystemPromptTests` covers the staging rule.
5. `dotnet test tests/ApiHost.Tests`, `tests/Contracts.Tests` and `tests/Agent.Tests` pass.
6. Runtime check in the device chat: ask for the object, then stage it, and record which steps could
   not run.

## Evidence

Branch `codex/008-agent-finds-the-source-object-id-it-stages`.

### Files changed

- `src/ApiHost/TaskSourceObjectListTool.cs` (new) — `list_source_objects`: the device's comparable
  source objects with the canonical `{deviceId}:{sourceId}`, the staging owner of each, and the active
  task staging would add to. Read-tier and side-effect free: it reads the same manifest
  `CanonicalId` resolves against and opens the graph for one owner read.
- `src/ApiHost/TaskSourceStagingTool.cs` — `CanonicalId` no longer returns a device-prefixed value
  unvalidated and no longer reads every colon as a claimed device id:
  - `block:`/`db:`/`udt:`/`type:` (the knowledge base's object kinds) resolve through the manifest
    name they carry; `symbol:`/`io:`/`udt-member:`/`db-member:`/`edge:` are reported as element ids that
    are not source objects; anything else keeps `TASK_SOURCE_DEVICE_MISMATCH`;
  - a rooted path is reported as an absolute path instead of "belongs to device 'C'";
  - a value that resolves to nothing comes back as `GRAPH_TARGET_NOT_FOUND` with a bounded
    "Closest:" list (containment or a shared ≥3-character prefix, so `block-mian` finds `block-main`);
  - `TranslateGraphFailure` gives the graph's own constraint codes back as `ToolCallException`s, so
    `Source object was not registered.` arrives as `GRAPH_TARGET_NOT_FOUND` with a remediation instead
    of the agent loop's code-less `AGENT_TOOL_ERROR`.
- `src/ApiHost/CompatibilityEndpoints.cs` — the device conversation's catalogue appends the listing
  before the two existing in-process tools.
- `src/Contracts/Sandbox/SandboxPolicy.cs` — `list_source_objects` is `Read` (unclassified tools fail
  closed, so without this the listing would be refused).
- `src/Agent/Chat/SystemPrompt.cs` — one staging rule: where the id comes from, that a knowledge-base
  node id is not a source object id, that staging needs an active task, and that a take-over must name
  the owning task.
- `tests/ApiHost.Tests/TaskSourceObjectListToolTests.cs` (new),
  `tests/ApiHost.Tests/TaskSourceStagingToolTests.cs`, `tests/Contracts.Tests/SandboxPolicyTests.cs`,
  `tests/Agent.Tests/SystemPromptTests.cs` — the checks below.

### Commands and observed results

| Command | Result |
|---|---|
| `dotnet build src/ApiHost/ApiHost.csproj` | Build succeeded, 0 errors (see the note on the launcher below) |
| `dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj --no-build -v q` | Passed 222/222 (17 new) |
| `dotnet test tests/Contracts.Tests/Contracts.Tests.csproj --no-build -v q` | Passed 115/115 |
| `dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-build -v q` | Passed 508/508 (1 new; 1 pre-existing warning in `FeatureSvnBranchTests.cs`) |
| `cd studio && npm test -- --run` | Not run: no frontend file changed, and this item adds no UI surface |

The dev launcher's ApiHost holds `src/ApiHost/bin/Debug/net8.0/{Agent,Contracts}.dll`, so the build
fails with `MSB3027`/`MSB3021` while it runs (documented in the root `AGENTS.md`). The ApiHost process
was stopped for the build and test lanes and restarted afterwards; the Vite server was left alone.

### Done when

1. Passed. `AKnowledgeNodeIdResolvesToTheSourceObjectItNames` (`block:Main`, `device-1:block:Main`,
   `BLOCK:Main`) stages `device-1:block-main`; `AKnowledgeElementIdIsReportedAsSuchInsteadOfAsAForeignDevice`
   reports `symbol:`/`db-member:`/`udt-member:` as element ids with `GRAPH_TARGET_NOT_FOUND`;
   `AnUnresolvableIdKeepsItsCodeAndNamesTheCandidates` and `AShortenedNameComesBackWithTheCandidates`
   return the code plus the candidate ids; `AnAbsolutePathIsRefusedAsAPathNotAsAForeignDevice` covers the
   drive-letter case; the existing `AForeignDeviceObjectIsRefusedAndTheCurrentOwnerIsReportedBeforeATakeOver`
   still gets `TASK_SOURCE_DEVICE_MISMATCH` for `device-2:block-main`.
2. Passed. `AGraphConstraintFailureKeepsItsCodeAndPointsAtTheListing` proves the graph's
   `GRAPH_TARGET_NOT_FOUND` survives as a `ToolCallException` carrying both its code and a remediation
   naming `list_source_objects`, where it used to reach the model as `AGENT_TOOL_ERROR` with no code.
3. Passed. `TaskSourceObjectListToolTests`: canonical ids and the instance-DB exclusion with its count
   (`ListsTheComparableObjectsWithTheIdStagingAccepts`), `query`/`category` filtering including a slice
   of the exported path (`QueryAndCategoryNarrowTheList`), stable paging (`PagesWithLimitAndOffset`),
   the owner of each object (`ReportsWhoAlreadyStagesAnObject`), no active task required and the active
   one reported when present (`ReportsTheActiveTaskThatStagingWouldAddTo`), and
   `ListingIsReadTierAndRegistersNothing` proves the read path registers no entity and writes no stage.
4. Passed. `SandboxPolicyTests.KnownReadToolsClassifyAsRead` and `EveryCurrentMcpToolIsClassified` cover
   the tier; `SystemPromptTests.PromptExplainsStagingASourceObjectAndWhereItsIdComesFrom` covers the rule.
5. Passed — see the command table.
6. Passed for the read and card path, with the write itself deliberately not approved. Runtime check with
   `.\launch.ps1 -NoBuild` against the user's own workbench (`SWT2-PEI-N` / `master` / `Sino_PEI`):
   both services answered HTTP 200 and `/api/app-assistant/health` reported `modelConfigured: true`; the
   device selection was set with `POST .../devices/5b7080b0…/select` (204).
   - Turn 1 (read only): the model called
     `list_source_objects({"query": "202_VocDoorGeneral"})` and answered with
     `5b7080b05d3b4ff69b1176209de193d8:yHhBhWzZvclfN3RXWeg2F6PqswugvWCwiS55Y-wNpIk` — the id the
     manifest derives, reported by the tool instead of guessed.
   - Turn 2: the model refused to stage because the task was not yet active ("这个 worktree 当前没有活动任务"),
     reusing turn 1's `activeTask: null`. That is the new prompt rule behaving as intended, on stale
     context rather than a fresh read.
   - After `PUT .../active-task {taskId: 5efacc2e…}` (the user's `Cav_B 边沿触发条件用错`), turn 3 called
     `list_source_objects` and then
     `stage_task_source_object({"objects": [{"sourceObjectId": "5b7080b05d3b4ff69b1176209de193d8:yHhBhWzZvclfN3RXWeg2F6PqswugvWCwiS55Y-wNpIk"}]})`
     — the exact id, no invented prefix. The approval card was raised and the call was refused with
     `SANDBOX_USER_DENIED`; `GET .../tasks/5efacc2e…/stages` and `GET .../source-stages` both returned
     `[]`, so nothing was written.
   - Steps not run: the browser-side **Approve** click (no browser session was driven here) and the
     approved write itself. The card was left unanswered, so it expired to Deny after
     `PendingToolActions`' 3-minute lifetime (`src/ApiHost/DeviceToolSecurity.cs:236`); the approved path
     is covered by `TaskSourceStagingToolTests.ApprovingTheCallStagesTheRequestedObjectsForTheActiveTask`,
     which runs the real `AgentSandbox` gate and the real coordinator. A real stage of the user's task was
     deliberately not approved.

### Notes left for the reader

- The device selection and the active task were set through the API to reproduce the user's context and
  are left in place, so staging the block is now one click on the task page (or one approved card) away.
- The id spaces stay separate on purpose. `stableId = base64url(SHA256("{category}|{sourcePath}"))`, so
  renaming a block or moving it to another group changes its id and dangles any stage that named the old
  one. Nothing here addresses that; it is a consequence of a content-derived id, not of this change.
