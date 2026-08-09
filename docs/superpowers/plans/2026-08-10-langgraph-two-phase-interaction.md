# LangGraph Two-Phase Interaction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace keyword-routed App Assistant turns with a proposal-only orientation phase followed by LLM-decided answers, read-only tools, clarification, and approved mutations.

**Architecture:** Python receives explicit `orientation` or `command` modes. Both modes refresh context through the C# gateway. Orientation calls the LLM and ends without tools; command mode validates an LLM decision before routing to answer, clarification, read-only gateway execution, or an approval interrupt.

**Tech Stack:** Python 3.11+, LangGraph 0.6.6, Pydantic 2, LangChain OpenAI-compatible ChatOpenAI, FastAPI, SQLite checkpoints, C# ApiHost, React/Vitest.

---

## Task 1: Add request and decision contracts

**Files:** Create `agent-service/app_assistant/decisions.py`; modify `agent-service/app_assistant/state.py`; test `agent-service/tests/test_decisions.py`.

- [ ] **Step 1: Write failing tests.** Test that `AssistantRequestMode` accepts only `orientation` and `command`; `OrientationProposal` requires `likelyIntent`, `observations`, `proposedNextStep`, and `confirmationQuestion`; and `AssistantDecision` rejects unsupported tool names.

```python
assert AssistantRequestMode.ORIENTATION.value == "orientation"
assert AssistantRequestMode.COMMAND.value == "command"
```

- [ ] **Step 2: Verify failure.** Run `py -3 -m pytest agent-service/tests/test_decisions.py -q`; expect import/validation failure because the contracts do not exist.

- [ ] **Step 3: Implement.** Use Pydantic camelCase aliases. Restrict decision kinds to `answer`, `clarification`, `read_tool`, and `mutation_proposal`; restrict read tools to `read_worktree_todos`, `read_commit_history`, and `read_svn_state`. Add `request_mode`, `orientation_complete`, `orientation_proposal`, `decision`, `tool_request`, and `tool_result` to `AppAssistantState`.

- [ ] **Step 4: Verify and commit.** Run `py -3 -m pytest agent-service/tests/test_decisions.py -q`; then run `git add agent-service/app_assistant/decisions.py agent-service/app_assistant/state.py agent-service/tests/test_decisions.py` and `git commit -m "feat: add app assistant decision contracts"`.

## Task 2: Define the two system prompts

**Files:** Modify `agent-service/app_assistant/prompts.py`; create `agent-service/tests/test_prompts.py`.

- [ ] **Step 1: Write failing tests.** Assert the orientation prompt mentions workbench, worktree, UI selection, todos, Git/SVN, PLC Assistant handoff, approval, and explicitly says not to call tools. Assert the command prompt contains runtime context, conversation/user message, allowlisted decisions, and an exactly-one-decision instruction.

```python
assert "Do not call tools" in build_orientation_prompt(context)
assert "read_tool" in build_command_prompt(context, "Read tasks", [])
```

- [ ] **Step 2: Verify failure.** Run `py -3 -m pytest agent-service/tests/test_prompts.py -q`; expect failure because the builders are absent.

- [ ] **Step 3: Implement.** Add `build_orientation_prompt` and `build_command_prompt` with versions `workbench-assistant-orientation-v1` and `workbench-assistant-command-v1`. The orientation prompt explains the typical workflow and prohibits tools, mutations, UI selection, and claims of completed work. The command prompt separates observed facts from recommendations and requires clarification for ambiguous targets.

- [ ] **Step 4: Verify and commit.** Run `py -3 -m pytest agent-service/tests/test_prompts.py agent-service/tests/test_graph.py -q`; then commit the prompt source and tests as `feat: add workflow-oriented assistant prompts`.

## Task 3: Replace keyword routing with explicit graph phases

**Files:** Modify `agent-service/app_assistant/graph.py` and `agent-service/tests/test_graph.py`.

- [ ] **Step 1: Write failing orientation tests.** A structured fake model must prove `request_mode=orientation` reads context once, calls the model once, returns an orientation proposal, and makes zero detail or mutation gateway calls.

- [ ] **Step 2: Write failing command tests.** Cover direct answer, clarification with zero gateway calls, read-only tool followed by grounded summary, mutation proposal paused at `interrupt`, invalid tool output with zero calls, and stale-revision re-plan. Use a non-keyword user message to prove `_classify` is no longer used.

- [ ] **Step 3: Verify failure.** Run `py -3 -m pytest agent-service/tests/test_graph.py -k "orientation or decision or clarification or tool or mutation" -q`; expect failures because the current graph has no orientation or LLM decision nodes.

- [ ] **Step 4: Implement.** Replace `_classify` with `bootstrap_context`, `orient_with_llm`, `decide_with_llm`, `execute_read_tool`, `summarize_tool_result`, `propose_mutation`, and `execute_mutation`. Route by request mode, then validated decision kind. Malformed model output returns deterministic clarification and cannot execute a tool. Preserve C# expected-revision and idempotency checks.

- [ ] **Step 5: Verify and commit.** Run `py -3 -m pytest agent-service/tests/test_graph.py -q`; update metadata with prompt versions and decision kind; commit as `feat: route app assistant through two-phase langgraph workflow`.

## Task 4: Propagate semantic request modes through APIs

**Files:** Modify `agent-service/app_assistant/server.py`, `src/ApiHost/AppAssistant/AppAssistantClient.cs`, and `src/ApiHost/AppAssistant/AppAssistantChatEndpoints.cs`; test `agent-service/tests/test_live_sidecar.py` and `tests/ApiHost.Tests/AppAssistantChatEndpointTests.cs`.

- [ ] **Step 1: Write failing API tests.** Assert bootstrap defaults to `requestMode=orientation` with an empty message, chat defaults to `requestMode=command`, and approval resumes remain command operations.

- [ ] **Step 2: Verify failure.** Run `py -3 -m pytest agent-service/tests/test_live_sidecar.py -k request_mode -q`; expect failure because `AssistantRequest` has no request-mode field.

- [ ] **Step 3: Implement.** Add `request_mode` to `AssistantRequest`. Bootstrap invokes orientation with no user message; chat invokes command with the supplied message. Update `AppAssistantClient.SendAsync` to send `requestMode`, `message`, and `approval`, selecting orientation for bootstrap. Preserve public routes and unavailable-service errors.

- [ ] **Step 4: Verify and commit.** Run `py -3 -m pytest agent-service/tests -q` and `dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj --no-restore --filter FullyQualifiedName~AppAssistant`; commit as `feat: separate orientation and command assistant requests`.

## Task 5: Make the React panel wait for a command

**Files:** Modify `studio/src/api/client.ts`, `studio/src/studio/appAssistant/AppAssistantPanel.tsx`, and `studio/src/studio/appAssistant/appAssistantState.ts`; test the corresponding panel/state files.

- [ ] **Step 1: Write failing UI tests.** Assert opening the panel calls `bootstrapAppAssistant()` with no message, displays the orientation proposal, does not call `chatAppAssistant`, and does not set `busy`, `pendingApproval`, or automatic refresh.

```tsx
expect(api.bootstrapAppAssistant).toHaveBeenCalledWith()
expect(api.chatAppAssistant).not.toHaveBeenCalled()
```

- [ ] **Step 2: Verify failure.** Run `cd studio; npm test -- --run src/studio/appAssistant/AppAssistantPanel.test.tsx src/studio/appAssistant/appAssistantState.test.ts`; expect failure because bootstrap currently sends the old default command.

- [ ] **Step 3: Implement.** Make bootstrap send an empty orientation request. Keep chat for explicit messages and approval decisions. Display orientation as a normal assistant message and leave the input available. Do not submit confirmation automatically.

- [ ] **Step 4: Verify and commit.** Run the focused assistant tests plus `src/studio/MainStudio.contract.test.ts`; commit as `feat: wait for user command after assistant orientation`.

## Task 6: Add safety, observability, and acceptance coverage

**Files:** Modify `agent-service/tests/test_evaluation.py`, `agent-service/tests/test_mutations.py`, and `agent-service/tests/test_observability.py`; update `tests/ApiHost.Tests/AppAssistantGatewayTests.cs` only if a runtime fixture is needed.

- [ ] **Step 1: Add evaluation cases.** Cover empty worktree, multiple worktrees, stale SVN, missing focus, and PLC questions. Orientation must propose no executable action and distinguish uncertainty.

- [ ] **Step 2: Add safety assertions.** Verify invalid tool names, ambiguous targets, model failures, stale revisions, mutation rejection, and mutation approval. Assert no mutation gateway call occurs before approval.

- [ ] **Step 3: Extend observability.** Record request mode, decision kind, tool name, and prompt versions without API keys or sensitive paths.

- [ ] **Step 4: Run all suites.** Run `py -3 -m pytest agent-service/tests -q`, `dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj --no-restore`, and `cd studio; npm test -- --run`. UI tests requiring the separate port-3000 mock service must be reported separately.

- [ ] **Step 5: Live smoke test.** Open the assistant and verify proposal-only orientation; verify no MCP call before a user command; test non-keyword read selection, clarification, mutation approval, project switching, and factual worktree/todo/SVN answers.

- [ ] **Step 6: Commit final verification updates.** Run `git add agent-service/tests tests/ApiHost.Tests` and commit as `test: cover two-phase assistant safety and grounding`.
