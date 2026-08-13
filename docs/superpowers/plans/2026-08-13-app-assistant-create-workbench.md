# App Assistant Create Workbench Implementation Plan

> **For agentic workers:** Execute this plan task-by-task with tests first. Preserve unrelated uncommitted work.

**Goal:** Let a user ask the LangGraph Workbench Assistant to create a new workbench project from an existing TIA `.ap17` file, with explicit clarification for missing inputs and approval before creation.

**Architecture:** Extend the existing assistant mutation protocol with a `create_workbench` proposal. The sidecar pauses on a structured proposal; ApiHost exposes a loopback-only internal mutation endpoint that reuses `WorkbenchCoordinator.CreateWorkbenchAsync`; the Studio panel renders approval details and refreshes the workbench list after approval.

**Tech Stack:** Python LangGraph/FastAPI, ASP.NET ApiHost, React/Vite Studio, Vitest, pytest, xUnit.

---

### Task 1: Define the assistant project mutation contract

**Files:**
- Modify: `agent-service/app_assistant/decisions.py`
- Modify: `agent-service/app_assistant/graph.py`
- Modify: `agent-service/app_assistant/prompts.py`
- Test: `agent-service/tests/test_graph.py`
- Test: `agent-service/tests/test_mutations.py`

- [ ] Add a `CreateWorkbenchMutation` model with `name`, `engineeringProjectPath`, and optional `rootPath`.
- [ ] Allow `create_workbench` in the decision model and prompt schema.
- [ ] Ask a clarification question when the assistant request lacks a name or `.ap17` path.
- [ ] Build an approval proposal containing the workbench name, source project path, optional root, and deterministic request ID.
- [ ] Resume the graph only after `{decision: "approve"}` and call a gateway method to create the workbench.
- [ ] Add failing tests for proposal generation, missing-input clarification, and approval execution.

### Task 2: Expose the approved mutation through ApiHost

**Files:**
- Modify: `src/ApiHost/AppAssistant/AppAssistantGateway.cs`
- Modify: `src/ApiHost/AppAssistant/AppAssistantEndpoints.cs`
- Modify: `src/ApiHost/AppAssistant/AppAssistantContracts.cs`
- Test: `tests/ApiHost.Tests/AppAssistantTests.cs` or the existing assistant endpoint test file

- [ ] Add an internal `create-workbench` endpoint protected by the existing assistant access policy.
- [ ] Validate the request scope, revision, duplicate names, `.ap17` path, and existing sandbox rules.
- [ ] Reuse `WorkbenchCoordinator.CreateWorkbenchAsync` with `engineeringProjectPath` and return the created workbench metadata.
- [ ] Add an endpoint regression test proving the mutation request reaches the coordinator contract and rejects invalid scope.

### Task 3: Connect the Studio proposal UI

**Files:**
- Modify: `studio/src/studio/appAssistant/AppAssistantPanel.tsx`
- Modify: `studio/src/studio/appAssistant/appAssistantState.ts`
- Test: `studio/src/studio/appAssistant/AppAssistantPanel.test.tsx`

- [ ] Render a workbench-creation approval card distinct from the existing worktree card.
- [ ] Keep the new proposal visible until approval or rejection.
- [ ] Send approval to the assistant endpoint and refresh the project list after successful creation.
- [ ] Add a component test for the approval card and post-approval refresh callback.

### Task 4: Verify the complete flow

- [ ] Run focused pytest, Vitest, and ApiHost tests.
- [ ] Restart with `launch.ps1 -NoBuild` and verify ports 5173, 5239, and 8787.
- [ ] Open `http://localhost:5173/` in the in-app browser.
- [ ] Ask the assistant to create a uniquely named project using the existing `TestPLCExportDemo.ap17` file.
- [ ] Verify the assistant asks for approval, approve it, and confirm the new project appears in the project tree and `GET /api/workbenches`.
- [ ] Remove only the test-created project if the user explicitly authorizes cleanup; otherwise leave it documented and unselected.
