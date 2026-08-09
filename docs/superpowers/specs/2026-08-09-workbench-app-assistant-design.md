# Workbench Runtime State and App Assistant Design

## Goal

Add a workbench-scoped App Assistant backed by LangGraph while preserving the current PLC-focused `AgentLoop` as an independent temporary solution.

## Scope

The App Assistant is activated when a workbench is selected. Its conversation and runtime state are scoped to the workbench, not to one device. It can inspect worktrees, todo lists, Git history, SVN revision information, and current readiness state. It may create a worktree after explicit user approval. It does not change the UI's selected worktree or device; the user remains responsible for UI selection.

The existing PLC AgentLoop remains responsible for PLC knowledge queries, program diagnosis, source editing, and PLC-specific tool workflows. Its session format, system prompt, tool catalog, and persistence are not migrated by this design.

## Architecture

```text
React
  ├── PLC chat ───────────────► /api/chat ───────────────► current AgentLoop
  │                                                          │
  │                                                          ▼
  │                                                   existing MCP runtime
  │
  └── Workbench App Assistant ► /api/app-assistant/chat ► ApiHost gateway
                                                              │
                                                              ▼
                                                     Python LangGraph service
                                                              │
                                                              ▼
                                                     C# app-action gateway
                                                              │
                                                              ▼
                                                   existing C# services/MCP
```

ApiHost is the authority for workbench identity, operation permissions, revision checks, and execution. Python never starts a second TIA or MCP runtime and never supplies unchecked paths or device identities to MCP tools.

## Canonical state

Introduce a workbench-scoped `WorkbenchRuntimeSnapshot` maintained by a C# `WorkbenchRuntimeStateCoordinator`. The snapshot contains current observed facts and action availability; Git and SVN remain the historical sources of truth rather than being copied into the state store.

```text
WorkbenchRuntimeSnapshot
  schemaVersion
  workbenchId
  workbenchRevision
  selectedFocus: worktreeId?, deviceId?
  worktrees[]
  operation
  availableActions[]
  observedAt
```

Each worktree summary may contain branch/head/status, todo counts, SVN base/current references, validation/readiness, and device summaries. Device details are loaded lazily when an operation needs them.

The snapshot uses composed state regions instead of one large enum:

- workbench lifecycle;
- worktree/source/version-control state;
- TIA connection/observation state;
- knowledge freshness state;
- active operation state.

Every accepted command produces a typed runtime event, increments the workbench revision, and publishes a new snapshot. Commands carry expected revisions for stale-context rejection.

## Commands and events

Commands represent requested intent:

```text
SelectWorkbench
RefreshWorkbenchState
CreateWorktree
ReadWorktreeTodos
ReadWorktreeHistory
ReadWorktreeSvnState
```

Events represent facts after validation or execution:

```text
WorkbenchSelected
WorkbenchRefreshed
WorktreeCreated
GitStateObserved
SvnStateObserved
TodoStateObserved
OperationStarted
OperationCompleted
OperationFailed
```

UI endpoints and App Assistant tools submit commands through shared C# services. They do not update snapshot fields directly.

## Action policy

The C# layer derives `availableActions` from state and prerequisites. The App Assistant receives action IDs, descriptions, targets, blockers, and approval requirements instead of inferring capabilities from raw tool names.

Read-only actions include worktree status, todo list, commit history, and SVN state. `create_worktree` is an approved mutation: the assistant can propose and execute it only after explicit user approval, with an idempotency key and expected workbench revision. The newly created worktree is reported but is not automatically selected in the UI.

## LangGraph state

LangGraph uses one thread per workbench:

```text
app-assistant:{workbenchId}
```

Its checkpoint contains conversation, current snapshot projection, proposed action, and pending approval. It is not the authoritative application state. Before consequential execution, the graph refreshes from ApiHost and checks the expected revision. A `CONTEXT_STALE` response forces refresh and re-planning.

## Lifecycle and rollout

1. Build and test the C# runtime-state foundation.
2. Expose a read-only App Assistant gateway.
3. Add a read-only LangGraph sidecar and experimental assistant panel.
4. Enable approved worktree creation.
5. Add runtime event streaming, packaging, observability, and regression hardening.

The PLC AgentLoop remains available throughout the rollout. No dual execution of mutations is allowed.

## Non-goals

- replacing or refactoring the current PLC AgentLoop;
- moving TIA/MCP process ownership to Python;
- changing UI selection behavior;
- duplicating complete Git or SVN history into a new database;
- implementing a distributed event bus or full event sourcing in the first release;
- allowing the App Assistant to diagnose PLC logic.

## Acceptance criteria

- The UI and App Assistant see the same workbench revision and action blockers.
- Direct UI changes refresh the workbench snapshot.
- The App Assistant can read todos, Git history, and SVN information for any worktree in the selected workbench.
- The App Assistant can create a worktree only after approval and cannot silently select it.
- Stale revisions are rejected before mutations.
- LangGraph failure does not break the existing PLC AgentLoop.
