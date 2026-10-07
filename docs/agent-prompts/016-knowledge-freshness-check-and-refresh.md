# 016. The knowledge agent checks freshness and refreshes the database before answering

Status: done
Created: 2026-10-07
Depends on: none

## Goal

After a TIA program change is committed, the device knowledge agent answers questions from the
current program instead of from the stale database it happened to have. Before the change, the agent
had no way to learn the knowledge state, no way to repair it, and reported a committed edit as absent
because the knowledge database still held the old content. After the change, the agent reads the
state from its runtime context, checks it with a tool, refreshes it with a tool when it is not
current, and answers from the refreshed content — or says the state is not current instead of
presenting old content as the current program.

## Context

- The device chat runs `ApiChatService` (`src/ApiHost/CompatibilityEndpoints.cs:930-1000`). Its
  runtime context reported `Workbench`, `Worktree`, `Device`, `PLC source`, `Knowledge DB` and the
  active task — never the knowledge state, so the model could not know the database was stale.
- `SessionManager.BuildRuntimeContext` (`src/Agent/Chat/SessionManager.cs:266-292`) already knew the
  stale state but is not the live provider, and its advice was `run update_components before reuse`.
- Freshness is not just the flags: an in-app edit sets `knowledge.Stale`, and accepting TIA source
  sets `BaselineStale`, but an edit made outside the app sets no flag at all (ADR-0012). Only the
  applied hashes (`knowledge.AppliedOverlayHashes`) compared against the current source XML show it.
- The repair path already existed and was already guarded:
  `WorkbenchCoordinator.UpdateKnowledgeAsync` (`src/Agent/Workbench/WorkbenchCoordinator.cs:2481`)
  picks a full `ingest_source` for a missing database or a stale baseline and a partial
  `update_components` otherwise, then persists the applied hashes and clears the flags.
  `RebuildKnowledgeAsync` (`:2575`) always rebuilds. Both run under the device operation lock.
- The raw knowledge tools were reachable from the chat and bypassed that bookkeeping: a raw
  `update_components` left the device flagged stale even though the graph had changed.
- `mcp-knowledge`'s `update_components` refuses a component identity the database does not hold
  (`COMPONENT_NOT_IN_DATABASE`, `src/Mcp.Knowledge/Tools/KnowledgeTools.cs:394-402`), so an added or
  removed source file can only be repaired by a full rebuild.
- In-process device-chat tools already exist as the pattern for this:
  `src/ApiHost/TaskSourceObjectListTool.cs`, `src/ApiHost/TaskCreationTool.cs`, registered in the
  catalog at `src/ApiHost/CompatibilityEndpoints.cs:945-960` and classified in
  `src/Contracts/Sandbox/SandboxPolicy.cs`.

## Constraints

- The knowledge database is a derived, Git-ignored artifact: the refresh is a write, not a
  destructive action, so it needs no approval card and must not touch PLC source, TIA state, Git or
  `revision.json`.
- Every refresh goes through the coordinator's guarded knowledge operations; the raw
  `ingest_source`/`update_components` tools stay available to the servers but are not the device
  chat's path.
- The status read writes nothing and never moves the graph.
- Both tools resolve the conversation's own `DeviceContext` and take no device argument.
- The runtime context stays byte-stable between turns except for the marked context message the
  `AgentLoop` appends when it changes (ADR: context caching).

## Done when

1. `GET /api/workbenches/{id}/worktrees/{wt}/devices/{device}/knowledge/status` reports
   `missing`/`stale`/`current`, the database path and timestamp, the flagged state, whether a rebuild
   is required, and the changed/added/removed source paths.
2. The device chat's `knowledge_status` reports the same, and `refresh_knowledge` repairs the
   database and reports the state after the refresh.
3. The runtime context of every device-chat turn carries the knowledge state and what to do about it.
4. The system prompt requires the check before the first knowledge query of a turn in which the user
   reports a change, and forbids concluding that a change is absent from an unchecked database.
5. `dotnet test tests/Agent.Tests`, `tests/ApiHost.Tests` and `tests/Contracts.Tests` pass.

## Evidence

Branch `codex/knowledge-freshness-refresh`, worktree `.worktrees/knowledge-freshness-refresh`, based
on `7bd5052`.

Commands and results:

| Command | Result |
|---|---|
| `dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-build -v q` | 530 passed, 0 failed |
| `dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj --no-build -v q` | 238 passed, 0 failed |
| `dotnet test tests/Contracts.Tests/Contracts.Tests.csproj -v q` | 115 passed, 0 failed |

Not run: `tests/E2E.Tests` (drives real workbenches and needs a live TIA/SVN environment, which is
the recorded pre-existing failure in this sandbox) and browser validation (no UI change; the new
surface is an HTTP route and two agent tools).

New regression tests: `tests/Agent.Tests/KnowledgeFreshnessTests.cs` (11) and
`tests/ApiHost.Tests/KnowledgeToolTests.cs` (10); one prompt-rule test in
`tests/Agent.Tests/SystemPromptTests.cs`; the runtime-context wording case in
`tests/Agent.Tests/SessionManagerTests.cs` updated in place.

`WorkbenchCoordinator.UpdateKnowledgeAsync` now takes its repair decision from
`ReadKnowledgeStatus` instead of repeating the rule, which also removes two defects on the existing
"Update knowledge" path: an added component was sent to `update_components`, which refuses an
identity the database does not hold, and a removed component's hash was carried forward so the
device could never reach `current`. Two pre-existing coordinator tests
(`KnowledgeUpdateUsesDeviceDatabaseAndPersistsAppliedHashes`,
`SuccessfulIncrementalUpdateSkipsSecondUnchangedUpdate`) now record the applied hash of the file
before editing it, so they exercise the plain content change they intend to; their assertions are
unchanged.

Files: `src/Agent/Workbench/WorkbenchModels.cs`, `src/Agent/Workbench/WorkbenchCoordinator.cs`,
`src/ApiHost/KnowledgeStatusTool.cs`, `src/ApiHost/KnowledgeRefreshTool.cs`,
`src/ApiHost/CompatibilityEndpoints.cs`, `src/ApiHost/WorkbenchApiModels.cs`,
`src/Contracts/Sandbox/SandboxPolicy.cs`, `src/Agent/Chat/SystemPrompt.cs`,
`src/Agent/Chat/SessionManager.cs`, `tests/Agent.Tests/WorkbenchCoordinatorTests.cs`,
`docs/knowledge-workflow.md`, `docs/user-workflow.md`.
