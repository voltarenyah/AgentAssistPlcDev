# LangGraph Workbench App Assistant Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a read-only Python LangGraph App Assistant that is independently testable beside the existing C# PLC AgentLoop.

**Architecture:** React continues to talk only to ApiHost. ApiHost forwards App Assistant messages to a Python service. The graph uses a workbench-scoped thread, receives C# context through the gateway, and may read todos/history/SVN state. Mutation tools are absent in this plan.

**Tech Stack:** Python 3.11+, LangGraph, FastAPI/Uvicorn, `httpx`, Pydantic, `langchain-openai` for the existing OpenAI-compatible model endpoint, `langgraph-checkpoint-sqlite` for local checkpoint persistence, pytest.

---

## Files

- Create: `agent-service/pyproject.toml` — pinned Python dependencies and test configuration.
- Create: `agent-service/langgraph.json` — local graph configuration.
- Create: `agent-service/app_assistant/contracts.py` — validated C# gateway contracts.
- Create: `agent-service/app_assistant/state.py` — graph state schema.
- Create: `agent-service/app_assistant/gateway.py` — async C# gateway client.
- Create: `agent-service/app_assistant/graph.py` — compiled LangGraph graph.
- Create: `agent-service/app_assistant/server.py` — FastAPI message/bootstrap/resume routes.
- Create: `agent-service/tests/test_graph.py` — graph routing and state tests.
- Create: `agent-service/tests/test_gateway.py` — mocked C# gateway tests.
- Create: `src/ApiHost/AppAssistant/AppAssistantClient.cs` — ApiHost client for Python streaming.
- Create: `src/ApiHost/AppAssistant/AppAssistantChatEndpoints.cs` — React-facing SSE endpoints.
- Modify: `src/ApiHost/Program.cs` — register client and configuration.
- Test: `tests/ApiHost.Tests/AppAssistantChatEndpointTests.cs` — Python-service failure and streaming tests.

## Task 1: Create the Python service skeleton

- [ ] Require Python 3.11 in `pyproject.toml`.
- [ ] Pin compatible versions of `langgraph`, `langgraph-checkpoint-sqlite`, `fastapi`, `uvicorn`, `httpx`, `pydantic`, `langchain-openai`, and pytest.
- [ ] Configure `langgraph.json` to expose the `app_assistant` graph.
- [ ] Add a health route returning graph version and gateway availability.
- [ ] Keep secrets in environment variables; do not store DeepSeek/API keys in graph state.

## Task 2: Define graph state and thread identity

- [ ] Define `AppAssistantState` with `workbench_id`, `context_revision`, `runtime_snapshot`, `messages`, `intent`, `proposed_action`, and `answer`.
- [ ] Define thread identity as `app-assistant:{workbenchId}`.
- [ ] Configure `AsyncSqliteSaver` using a service-owned database under the configured assistant data directory.
- [ ] Store only assistant checkpoints in this database; the C# runtime snapshot remains authoritative.

Target state:

```python
class AppAssistantState(TypedDict):
    workbench_id: str
    context_revision: int
    runtime_snapshot: dict
    messages: Annotated[list[AnyMessage], add_messages]
    intent: str | None
    proposed_action: dict | None
    answer: str | None
```

## Task 3: Implement gateway client and context bootstrap

- [ ] Implement async HTTP calls to the internal C# context/todos/history/SVN routes.
- [ ] Validate every response with Pydantic models.
- [ ] On bootstrap, fetch the workbench context and store its revision in graph state.
- [ ] Never let user text override `workbench_id` or context revision.
- [ ] Return a structured gateway error when ApiHost is unavailable.

## Task 4: Implement the read-only graph

- [ ] Add nodes: `bootstrap`, `classify_intent`, `read_context`, `read_worktree_detail`, `compose_answer`.
- [ ] Route status/capability questions to the snapshot without unnecessary tool calls.
- [ ] Route todo/history/SVN questions to the gateway client.
- [ ] Route PLC-program questions to a clear handoff response pointing to the existing PLC Assistant.
- [ ] Prevent mutation tool calls in this plan by omitting mutation tools from the graph entirely.

Graph shape:

```text
START → bootstrap → classify_intent
                    ├── status → compose_answer
                    ├── history/todos/svn → read_worktree_detail → compose_answer
                    └── plc_question → handoff_answer
```

## Task 5: Add ApiHost streaming bridge

- [ ] Add `POST /api/app-assistant/bootstrap` and `POST /api/app-assistant/chat`.
- [ ] Make ApiHost derive the workbench ID from the current selection; reject requests when no workbench is selected.
- [ ] Proxy Python graph events to React as `progress`, `answer`, `state`, `error`, and `interrupt` event kinds.
- [ ] Ensure Python unavailability produces an assistant-pane error without affecting `/api/chat`.
- [ ] Keep App Assistant sessions separate from `SessionManager` and existing PLC chat files.

## Task 6: Verify

- [ ] Run Python unit tests with mocked gateway responses.
- [ ] Start the local LangGraph service and exercise bootstrap/chat against a test ApiHost.
- [ ] Run `dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj`.
- [ ] Confirm the current PLC chat tests remain unchanged and pass.
- [ ] Commit with message `feat: add read-only langgraph workbench assistant`.
