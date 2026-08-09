# App Assistant Worktree Mutation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Allow the Workbench App Assistant to create a worktree only after explicit approval, while keeping UI selection under user control.

**Architecture:** LangGraph proposes a typed operation and pauses at an interrupt. ApiHost validates the approval, target workbench, expected revision, request id, and worktree parameters before invoking the existing worktree coordinator. After success, ApiHost refreshes runtime state and reports the new worktree without selecting it.

**Tech Stack:** Python LangGraph interrupts/checkpoints, ASP.NET Core, existing `WorkbenchCoordinator`, `WorkbenchApiState`, `OperationStatusRegistry`, xUnit/pytest.

---

## Files

- Modify: `src/ApiHost/AppAssistant/AppAssistantContracts.cs` — mutation request/result and approval contracts.
- Modify: `src/ApiHost/AppAssistant/AppAssistantGateway.cs` — validated create-worktree command.
- Modify: `src/ApiHost/AppAssistant/AppAssistantEndpoints.cs` — internal mutation route.
- Modify: `src/ApiHost/WorkbenchApiModels.cs` — reuse the coordinator operation path without changing selection.
- Modify: `agent-service/app_assistant/state.py` — pending approval state.
- Modify: `agent-service/app_assistant/graph.py` — proposal, interrupt, execute, refresh nodes.
- Modify: `agent-service/app_assistant/gateway.py` — mutation call and stale-error mapping.
- Test: `tests/ApiHost.Tests/AppAssistantWorktreeMutationTests.cs` — guards, idempotency, selection behavior.
- Test: `agent-service/tests/test_mutations.py` — interrupt/resume/stale behavior.

## Task 1: Define the mutation contract

- [ ] Add `CreateWorktreeRequest` with workbench ID, name, branch, start point, expected revision, and request ID.
- [ ] Reject absolute paths and client-supplied repository roots.
- [ ] Require a non-empty deterministic request ID for retry safety.
- [ ] Add `CreateWorktreeResult` containing worktree ID, branch, new workbench revision, and `selected: false`.

Contract:

```json
{
  "workbenchId": "wb-1",
  "name": "feature-a",
  "branch": "feature/a",
  "startPoint": "master",
  "expectedWorkbenchRevision": 84,
  "requestId": "assistant-run-123-operation-1"
}
```

## Task 2: Implement C# guards and idempotency

- [ ] Require the workbench to exist and be writable.
- [ ] Require a non-empty worktree name and reject duplicate worktree IDs/names.
- [ ] Reject the command when another conflicting workbench operation is running.
- [ ] Validate `ExpectedWorkbenchRevision` before starting the operation.
- [ ] Store completed request IDs and results in the existing ignored operation state or an equivalent scoped operation record.
- [ ] Return the original result for a repeated request ID instead of creating a second worktree.
- [ ] Call the existing `WorkbenchCoordinator.CreateWorktreeAsync` and then `WorkbenchApiState.Refresh`.
- [ ] Do not call `WorkbenchApiState.Select`.

## Task 3: Add LangGraph approval interrupt

- [ ] Add `propose_create_worktree` that produces a structured proposal rather than executing.
- [ ] Pause with `interrupt()` containing workbench, name, branch, start point, and expected revision.
- [ ] Resume only for an explicit approval value such as `{ "decision": "approve" }`.
- [ ] Treat rejection and malformed approval as a normal cancelled operation.
- [ ] After approval, call the C# gateway using the revision captured in the proposal.

## Task 4: Handle stale context and failure

- [ ] Map `CONTEXT_STALE` to a graph refresh and new proposal.
- [ ] Do not automatically retry a worktree creation after an unknown network failure unless the same request ID is reused.
- [ ] Report duplicate-name, invalid-branch, unavailable-source, and operation-busy errors without inventing alternate paths.
- [ ] Refresh the full workbench snapshot after success or known failure.

## Task 5: Verify selection and UI behavior

- [ ] Add a test proving successful creation returns `selected: false`.
- [ ] Add a test proving the existing selected worktree remains unchanged.
- [ ] Add a test proving a stale revision never invokes `CreateWorktreeAsync`.
- [ ] Add a test proving repeated request IDs do not create duplicate worktrees.
- [ ] Run both C# and Python test suites.
- [ ] Commit with message `feat: allow approved app assistant worktree creation`.
