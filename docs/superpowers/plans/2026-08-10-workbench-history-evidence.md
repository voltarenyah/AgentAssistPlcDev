# Workbench History Evidence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the LangGraph App Assistant focused-worktree Git/SVN history as bootstrap evidence and support recent, explicit-count, and complete history reads driven by the user’s prompt.

**Architecture:** ApiHost remains the repository authority. It enriches the focused workbench context with independent Git/SVN evidence and exposes scoped history responses. Python preserves that evidence, lets the model select Git/SVN history actions with a requested depth, and summarizes returned records without dumping them unless requested.

**Tech Stack:** .NET 8 / C#, LibGit2Sharp, SharpSvn, FastAPI/LangGraph, Pydantic 2, pytest, xUnit.

---

### Task 1: Define history contracts

**Files:** `agent-service/app_assistant/contracts.py`, `agent-service/app_assistant/decisions.py`, `agent-service/tests/test_graph.py`, `agent-service/tests/test_gateway.py`

- [ ] **Step 1: Write failing tests.** Assert that `AssistantDecision.model_validate` accepts `toolName="read_svn_history"` and `historyDepth="all"`, and that Python models preserve `worktreeId`, `sourceRevision`, `entries`, `complete`, and `unavailableReason` aliases.
- [ ] **Step 2: Run the focused tests.** Run `py -3.13 -m pytest agent-service/tests/test_graph.py agent-service/tests/test_gateway.py -q`; expect failures because the action, depth, and models do not exist.
- [ ] **Step 3: Implement the contracts.** Add `HistoryDepth = Literal["recent", "all"] | int`; add `history_depth` aliased as `historyDepth`; add `read_svn_history`; add `WorktreeSvnHistoryEntry`, `WorktreeSvnHistory`, and focused history evidence models with `extra="allow"` and API aliases.
- [ ] **Step 4: Re-run the focused tests.** Expect all existing and new contract tests to pass.
- [ ] **Step 5: Commit.** Run `git add agent-service/app_assistant/contracts.py agent-service/app_assistant/decisions.py agent-service/tests/test_graph.py agent-service/tests/test_gateway.py; git commit -m "feat: add assistant history depth contracts"`.

### Task 2: Support complete read-only logs at the version-control boundary

**Files:** `src/Mcp.VersionControl/Git/RepositoryService.cs`, `src/Mcp.VersionControl/Svn/SvnRepositoryService.cs`, `src/Mcp.VersionControl/Tools/VersionControlTools.cs`, `tests/Mcp.VersionControl.Tests/*`

- [ ] **Step 1: Write failing tests.** Add Git and SVN fixture tests that create more than the current default/cap and assert an explicit `allHistory: true` request returns every entry, while default calls retain bounded behavior.
- [ ] **Step 2: Run them.** Run `dotnet test tests/Mcp.VersionControl.Tests/Mcp.VersionControl.Tests.csproj --no-restore --filter "FullyQualifiedName~History"`; expect compile/assertion failures because complete-history arguments do not exist.
- [ ] **Step 3: Implement minimal support.** Add optional `allHistory=false` to `vc_log` and `svn_log`. Preserve existing defaults and caps; when true, enumerate all Git commits and use SharpSvn’s unlimited log setting for SVN. Keep path validation and read-only behavior unchanged.
- [ ] **Step 4: Verify.** Run the filtered tests, then the full `Mcp.VersionControl.Tests` project; expect green.
- [ ] **Step 5: Commit.** Run `git add src/Mcp.VersionControl tests/Mcp.VersionControl.Tests; git commit -m "feat: support complete read-only version history"`.

### Task 3: Enrich focused ApiHost context and expose SVN history

**Files:** `src/ApiHost/AppAssistant/AppAssistantContracts.cs`, `src/ApiHost/AppAssistant/AppAssistantGateway.cs`, `src/ApiHost/AppAssistant/AppAssistantEndpoints.cs`, `tests/ApiHost.Tests/AppAssistantEndpointsTests.cs`

- [ ] **Step 1: Write failing tests.** Prove focused context returns ten-or-fewer Git and SVN recent records; no focus returns `NO_FOCUSED_WORKTREE` without choosing an arbitrary worktree; explicit SVN `depth=all` returns complete history; Git/SVN failures remain source-local.
- [ ] **Step 2: Run them.** Run `dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj --no-restore --filter "FullyQualifiedName~AppAssistantEndpoints"`; expect missing contract/route failures.
- [ ] **Step 3: Implement contracts and gateway.** Add focused `AppAssistantHistoryContext`, SVN history entry/response records, and `GetSvnHistoryAsync`. Make `GetContextAsync` resolve the focused worktree, call Git/SVN readers with depth 10, and catch each source failure independently. Extend scoped Git history and add scoped SVN history routes with `recent`, positive numeric, and `all` depth semantics plus `complete`/`unavailableReason` metadata.
- [ ] **Step 4: Verify.** Run the filtered tests, then all `tests/ApiHost.Tests/ApiHost.Tests.csproj`; expect green.
- [ ] **Step 5: Commit.** Run `git add src/ApiHost/AppAssistant tests/ApiHost.Tests; git commit -m "feat: expose focused Git and SVN history evidence"`.

### Task 4: Wire Python gateway and graph read actions

**Files:** `agent-service/app_assistant/gateway.py`, `agent-service/app_assistant/graph.py`, `agent-service/app_assistant/state.py`, `agent-service/tests/test_gateway.py`, `agent-service/tests/test_graph.py`

- [ ] **Step 1: Write failing tests.** Assert bootstrap preserves `history`; recent/default, numeric, and `all` depths reach the gateway unchanged; `read_svn_history` calls its own gateway method; returned records and unavailable reasons are included in the model summary.
- [ ] **Step 2: Run them.** Run `py -3.13 -m pytest agent-service/tests/test_gateway.py agent-service/tests/test_graph.py -q`; expect failures because gateway methods and graph routing lack depth/SVN history.
- [ ] **Step 3: Implement.** Add typed `get_history(..., depth)` and `get_svn_history(..., depth)` methods; serialize `all` and numeric depth to ApiHost. Extend the Gateway protocol, `_read_detail`, `_fallback_tool_answer`, and state typing. Preserve todo, SVN state, mutation, and unavailable handling.
- [ ] **Step 4: Verify.** Run the focused tests, then `py -3.13 -m pytest agent-service/tests -q`; expect green.
- [ ] **Step 5: Commit.** Run `git add agent-service/app_assistant agent-service/tests/test_gateway.py agent-service/tests/test_graph.py; git commit -m "feat: let assistant read depth-aware Git and SVN history"`.

### Task 5: Teach the model when to retrieve and display history

**Files:** `agent-service/app_assistant/prompts.py`, `agent-service/tests/test_graph.py`, `agent-service/tests/test_evaluation.py`

- [ ] **Step 1: Write failing prompt/evaluation tests.** Assert prompts identify bootstrap Git/SVN records as evidence, select Git vs SVN history correctly, use `historyDepth="all"` only for explicit all/every/full requests, and summarize by default.
- [ ] **Step 2: Run them.** Run `py -3.13 -m pytest agent-service/tests/test_graph.py agent-service/tests/test_evaluation.py -q`; expect prompt assertion failures.
- [ ] **Step 3: Implement.** Update command JSON shapes and instructions for `read_commit_history`, `read_svn_history`, and `read_svn_state`; state that focused bootstrap history is observed evidence, normal answers extract useful findings, and complete retrieval is only for explicit requests. Update prompt context serialization so history fields are visible.
- [ ] **Step 4: Verify.** Run the focused tests and the full Python suite; expect green.
- [ ] **Step 5: Commit.** Run `git add agent-service/app_assistant/prompts.py agent-service/tests/test_graph.py agent-service/tests/test_evaluation.py; git commit -m "feat: guide assistant history retrieval and summaries"`.

### Task 6: End-to-end verification and handoff

**Files:** `agent-service/README.md` if operator documentation needs updating; this plan file for checkboxes/results.

- [ ] **Step 1: Run all suites.** Run `py -3.13 -m pytest agent-service/tests -q`, `dotnet test tests/Mcp.VersionControl.Tests/Mcp.VersionControl.Tests.csproj --no-restore`, and `dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj --no-restore`.
- [ ] **Step 2: Run a live read-only smoke test.** Select a workbench/worktree, call App Assistant bootstrap, verify focused Git/SVN recent evidence, then request recent analysis and explicit all-history display. Confirm no mutation endpoint is called and complete/partial metadata is truthful.
- [ ] **Step 3: Check hygiene.** Run `git diff --check` and `git status --short`; confirm no credentials, generated data, or unrelated edits are staged.
- [ ] **Step 4: Mark this plan complete and report exact results.** Record unavailable source behavior and full-history completeness.
- [ ] **Step 5: Commit only intended implementation files.** Run `git add agent-service src tests docs/superpowers/plans/2026-08-10-workbench-history-evidence.md; git commit -m "feat: ground app assistant in focused version history"`, excluding unrelated existing changes.

