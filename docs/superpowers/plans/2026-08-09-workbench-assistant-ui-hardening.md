# Workbench App Assistant UI and Runtime Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the workbench runtime snapshot and experimental App Assistant reliable in the desktop application without changing current PLC chat behavior.

**Architecture:** React renders a separate App Assistant panel and subscribes to workbench runtime events. The desktop host manages ApiHost and the Python sidecar lifecycle. ApiHost remains the only browser-facing authority and degrades the App Assistant independently when the sidecar is unavailable.

**Tech Stack:** React/Vite, ASP.NET Core SSE, existing `BackendProcessHost`, Python Uvicorn/LangGraph service, xUnit, Vitest.

---

## Files

- Create: `src/ApiHost/RuntimeStateEventEndpoints.cs` — workbench runtime SSE stream.
- Modify: `src/ApiHost/AppAssistant/AppAssistantChatEndpoints.cs` — include runtime-state events and reconnect behavior.
- Modify: `src/AutomationWorkbench.Desktop/BackendProcessHost.cs` — optional Python sidecar lifecycle after explicit configuration.
- Modify: `src/AutomationWorkbench.Desktop/RuntimePaths.cs` — resolve sidecar executable/service path.
- Create: `studio/src/studio/appAssistant/AppAssistantPanel.tsx` — render the workbench-scoped assistant and approval cards.
- Create: `studio/src/studio/appAssistant/appAssistantState.ts` — manage assistant messages, runtime snapshot, and pending operations.
- Modify: `studio/src/api/client.ts` — add App Assistant bootstrap/chat and runtime-event clients.
- Modify: `studio/src/studio/MainStudio.tsx` — mount the App Assistant when a workbench is selected.
- Modify: `studio/src/studio/chat/ChatWorkspace.tsx` — preserve the existing PLC chat surface while the new assistant is active.
- Test: `tests/ApiHost.Tests/RuntimeStateEventEndpointTests.cs`.
- Test: `tests/AutomationWorkbench.Desktop.Tests/BackendProcessHostTests.cs`.
- Test: `studio/src/studio/appAssistant/AppAssistantPanel.test.tsx` — panel, approval, and selection-preservation tests.

## Task 1: Add runtime-state SSE

- [ ] Add `GET /api/workbenches/{workbenchId}/runtime-events`.
- [ ] Emit the initial snapshot immediately after connection.
- [ ] Emit only events for the requested workbench.
- [ ] Include `revision`, `kind`, `timestamp`, and compact snapshot/projection data.
- [ ] Close cleanly when the client disconnects.
- [ ] Add bounded subscriber behavior so a slow React client cannot block ApiHost state updates.

## Task 2: Add the App Assistant panel

- [ ] Add a separate assistant mode/panel labeled `Workbench Assistant`.
- [ ] Activate it only when a workbench is selected.
- [ ] Keep the existing PLC Assistant session and controls independent.
- [ ] Display initial orientation, worktree status, todo/history answers, operation progress, and approval cards.
- [ ] Do not add a UI action that changes selected worktree/device as a side effect of assistant output.
- [ ] After the assistant creates a worktree, show an explicit `Select worktree` user action.

## Task 3: Refresh assistant context from UI events

- [ ] Have the ApiHost client update the LangGraph thread context after runtime events.
- [ ] Treat ordinary focus changes as context updates without invoking the LLM automatically.
- [ ] Trigger a new suggestion only after worktree creation/deletion or significant Git/SVN/TIA state changes.
- [ ] If the assistant is processing a request when a new revision arrives, mark the run stale and require refresh before mutation.

## Task 4: Manage the Python sidecar

- [ ] Add configuration for enabled flag, executable/command, port, startup timeout, and shutdown timeout.
- [ ] In development, support an externally started service.
- [ ] In packaged mode, start the sidecar hidden, capture logs, poll its health endpoint, and stop it during desktop shutdown.
- [ ] If startup fails, keep ApiHost and the existing PLC AgentLoop available.
- [ ] Never log API keys or internal approval payload secrets.

## Task 5: Add observability and evaluation hooks

- [ ] Log workbench ID, graph thread ID, runtime revision, action ID, operation ID, and outcome.
- [ ] Record graph version, prompt version, and model version in assistant metadata.
- [ ] Add user feedback categories for wrong worktree, stale status, wrong recommendation, unavailable action, and successful completion.
- [ ] Add regression scenarios for every supported read-only question and worktree creation flow.

## Task 6: Verify

- [ ] Run all .NET tests.
- [ ] Run all Python tests.
- [ ] Run the Studio typecheck/build and relevant Vitest tests.
- [ ] Run a desktop lifecycle test with the sidecar disabled.
- [ ] Run a desktop lifecycle test with the sidecar available.
- [ ] Manually verify that PLC chat still uses the existing `AgentLoop` and current confirmation behavior.
- [ ] Commit with message `feat: harden workbench app assistant runtime`.
