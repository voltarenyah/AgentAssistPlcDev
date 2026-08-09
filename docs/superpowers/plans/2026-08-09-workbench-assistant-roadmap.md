# Workbench App Assistant Roadmap

> **For agentic workers:** Implement the plans in dependency order. Use the plan-specific tests and commit each plan independently. Do not remove or rewrite the existing PLC `AgentLoop`.

**Goal:** Add a workbench-scoped LangGraph App Assistant with shared C# runtime state while preserving the current PLC-focused agent.

**Architecture:** ApiHost owns the authoritative workbench snapshot, revisions, action guards, and C# execution. A Python LangGraph service owns App Assistant orchestration and checkpointed conversation state. React and both agents consume projections of the C# snapshot.

**Tech Stack:** .NET 8 / ASP.NET Core, existing C# workbench services and MCP runtime, React/Vite, Python 3.11+, LangGraph, FastAPI, SQLite checkpointer.

---

## Plan order

### Plan 1: Workbench runtime-state foundation

File: `docs/superpowers/plans/2026-08-09-workbench-runtime-state-foundation.md`

Depends on: none.

Produces: versioned workbench snapshot, command/event reducer, revision validation, read-only API, unit tests.

### Plan 2: Workbench App Assistant C# gateway

File: `docs/superpowers/plans/2026-08-09-workbench-assistant-gateway.md`

Depends on: Plan 1.

Produces: read-only assistant context/action contracts and internal HTTP endpoints for todos, Git history, SVN state, and capability discovery.

### Plan 3: Read-only LangGraph App Assistant

File: `docs/superpowers/plans/2026-08-09-langgraph-workbench-assistant.md`

Depends on: Plans 1 and 2.

Produces: Python sidecar, workbench-scoped graph/thread, bootstrap orientation, read-only tools, and streaming bridge through ApiHost.

### Plan 4: Approved worktree creation

File: `docs/superpowers/plans/2026-08-09-app-assistant-worktree-mutation.md`

Depends on: Plans 1–3.

Produces: approval interrupt, revision-checked/idempotent `create_worktree`, result refresh, and mutation tests.

### Plan 5: UI/runtime integration and hardening

File: `docs/superpowers/plans/2026-08-09-workbench-assistant-ui-hardening.md`

Depends on: Plans 1–4.

Produces: React assistant panel, runtime event stream, sidecar lifecycle, observability, and end-to-end regression coverage.

## Execution rule

Complete and verify one plan before starting the next. Parallel work is appropriate only for isolated tests or documentation after the shared contracts in Plan 1 are accepted. Do not execute plans 3–5 against an unstable state contract.
