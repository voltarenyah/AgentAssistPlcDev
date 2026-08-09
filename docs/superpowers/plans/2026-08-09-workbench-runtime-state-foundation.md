# Workbench Runtime State Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Establish a workbench-scoped, revisioned C# runtime snapshot that can be consumed by React, the current PLC agent, MCP operations, and the future LangGraph assistant.

**Architecture:** Commands enter `WorkbenchRuntimeStateCoordinator`. The coordinator validates guards, delegates external reads/operations to existing services, reduces typed events into an immutable snapshot, increments the workbench revision, and publishes state changes. Git/SVN/TIA remain authoritative external sources; the snapshot records observed current facts and references.

**Tech Stack:** .NET 8, existing `WorkbenchApiState`, `WorkbenchCoordinator`, `OperationStatusRegistry`, xUnit.

---

## Files

- Create: `src/Agent/Workbench/RuntimeStateModels.cs` — snapshot, child summaries, action capabilities, revisions, and operation records.
- Create: `src/Agent/Workbench/RuntimeStateEvents.cs` — typed command/event records.
- Create: `src/Agent/Workbench/WorkbenchRuntimeStateCoordinator.cs` — reducer, revision checks, snapshot access, and event publication.
- Create: `src/ApiHost/RuntimeStateEndpoints.cs` — workbench snapshot and revision endpoints.
- Modify: `src/ApiHost/Program.cs` — register the coordinator and endpoint services.
- Modify: `src/ApiHost/WorkbenchApiModels.cs` — publish selection changes and refreshes through the coordinator.
- Test: `tests/Agent.Tests/WorkbenchRuntimeStateTests.cs` — reducer and guard tests.
- Test: `tests/ApiHost.Tests/RuntimeStateEndpointsTests.cs` — API contract tests.

## Task 1: Define the snapshot contract

- [ ] Add immutable records for `WorkbenchRuntimeSnapshot`, `WorktreeRuntimeSummary`, `DeviceRuntimeSummary`, `ActionCapability`, `RuntimeOperation`, and `RuntimeRevision`.

Use this minimum shape:

```csharp
public sealed record WorkbenchRuntimeSnapshot(
    int SchemaVersion,
    string WorkbenchId,
    long WorkbenchRevision,
    WorkbenchFocus Focus,
    IReadOnlyList<WorktreeRuntimeSummary> Worktrees,
    RuntimeOperation Operation,
    IReadOnlyList<ActionCapability> AvailableActions,
    DateTimeOffset ObservedAt);

public sealed record WorkbenchFocus(string? WorktreeId, string? DeviceId);

public sealed record ActionCapability(
    string Id,
    string Label,
    string Scope,
    bool Enabled,
    bool RequiresApproval,
    IReadOnlyList<string> BlockedBy);
```

- [ ] Keep Git/SVN history out of the snapshot. Store only current head/status, revision references, and counts needed for recommendations.
- [ ] Add JSON serialization tests proving absent optional focus/device values remain valid.

## Task 2: Define commands and events

- [ ] Add commands for `SelectWorkbench`, `SetFocus`, `RefreshWorkbench`, `ObserveWorktree`, `ObserveTodos`, `ObserveHistory`, `ObserveSvnState`, `StartOperation`, `CompleteOperation`, and `FailOperation`.
- [ ] Add events for `WorkbenchSelected`, `FocusChanged`, `WorkbenchRefreshed`, `WorktreeObserved`, `TodosObserved`, `HistoryObserved`, `SvnStateObserved`, `OperationStarted`, `OperationCompleted`, and `OperationFailed`.
- [ ] Require `WorkbenchId` on every command and an optional `ExpectedWorkbenchRevision` on commands that can mutate state.

Target command contract:

```csharp
public sealed record RuntimeCommand(
    string WorkbenchId,
    string RequestId,
    long? ExpectedWorkbenchRevision,
    string RequestedBy);
```

## Task 3: Implement the reducer and revision guard

- [ ] Write failing tests for revision increments, immutable snapshots, focus changes, and stale revision rejection.
- [ ] Implement `WorkbenchRuntimeStateCoordinator` with a concurrent dictionary keyed by `WorkbenchId`.
- [ ] Make event reduction deterministic and side-effect-free.
- [ ] Increment `WorkbenchRevision` once per accepted state event.
- [ ] Return a structured `CONTEXT_STALE` error containing expected and actual revisions.
- [ ] Publish `RuntimeStateChanged` through an in-process `Channel<WorkbenchRuntimeSnapshot>` or equivalent subscriber abstraction.

Required behavior:

```text
same command + same requestId → same result, no duplicate transition
old expected revision         → CONTEXT_STALE
accepted event                 → revision + 1
snapshot read                  → no state mutation
```

## Task 4: Connect existing selection state

- [ ] Modify `WorkbenchApiState.Select` so it publishes `FocusChanged` for the selected workbench/worktree/device.
- [ ] Do not let this new event automatically select a worktree or device on behalf of the App Assistant.
- [ ] Preserve all existing selection endpoint behavior and tests.
- [ ] Add a test proving selecting a workbench changes assistant focus but does not alter any worktree status.

## Task 5: Add read-only API endpoints

- [ ] Add `GET /api/workbenches/{workbenchId}/runtime-state` returning the current snapshot.
- [ ] Add `GET /api/workbenches/{workbenchId}/runtime-state/revision` returning only the revision and observed timestamp.
- [ ] Return `404 WORKBENCH_NOT_FOUND` for unknown workbenches.
- [ ] Return `409 CONTEXT_STALE` only for commands, never for snapshot reads.

## Task 6: Integrate refresh points

- [ ] Identify current endpoints that already call `s.Refresh(id)` or complete workbench operations.
- [ ] Add a coordinator refresh event after worktree creation/deletion and other existing workbench metadata changes.
- [ ] Do not refactor every operation in this plan; establish the shared hook and cover worktree create/delete plus selection.
- [ ] Keep `OperationStatusRegistry` as the progress source while the coordinator stores the aggregate operation state.

## Task 7: Verify

- [ ] Run `dotnet test tests/Agent.Tests/Agent.Tests.csproj`.
- [ ] Run `dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj`.
- [ ] Run the complete solution test command used by the repository.
- [ ] Confirm no changes were made to `AgentLoop`, `SystemPrompt`, existing chat session serialization, or MCP child-process ownership.
- [ ] Commit only the files listed in this plan with message `feat: add workbench runtime state foundation`.
