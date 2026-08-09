import json

from langgraph.checkpoint.memory import MemorySaver

from app_assistant.graph import build_graph, thread_id_for


class FakeGateway:
    def __init__(self):
        self.context_calls = 0
        self.detail_calls = []

    async def get_context(self, workbench_id: str):
        self.context_calls += 1
        return {
            "workbenchId": workbench_id,
            "runtime": {
                "workbenchRevision": 4,
                "focus": {"worktreeId": "wt-1", "deviceId": None},
                "availableActions": [{"id": "read_worktree_todos", "enabled": True}],
                "worktrees": [{"worktreeId": "wt-1", "name": "Feature A", "todoCount": 2}],
            },
            "availableActions": [{"id": "read_worktree_todos", "enabled": True}],
            "observedAt": "2026-08-09T00:00:00Z",
        }

    async def get_todos(self, workbench_id: str, worktree_id: str):
        self.detail_calls.append("todos")
        return {"tasks": [{"taskId": "task-1", "title": "Review changes", "status": "todo"}]}

    async def get_history(self, workbench_id: str, worktree_id: str):
        self.detail_calls.append("history")
        return {"commits": [{"sha": "abc", "message": "Add feature"}]}

    async def get_svn(self, workbench_id: str, worktree_id: str):
        self.detail_calls.append("svn")
        return {"currentRevision": 42, "baseRevision": 40}


class FakeModel:
    def __init__(self):
        self.calls = []

    async def ainvoke(self, messages):
        self.calls.append(messages)
        return type("Response", (), {"content": "Review the focused worktree todo list first."})()


class StructuredFakeModel:
    def __init__(self, *responses):
        self.responses = list(responses)
        self.calls = []

    async def ainvoke(self, messages):
        self.calls.append(messages)
        return type("Response", (), {"content": self.responses.pop(0)})()


async def test_status_question_uses_bootstrap_without_detail_reads():
    gateway = FakeGateway()
    model = StructuredFakeModel(json.dumps({
        "kind": "answer",
        "answer": "Workbench status: Feature A is the focused worktree.",
    }))
    graph = build_graph(gateway, model=model)

    result = await graph.ainvoke(
        {
            "workbench_id": "wb-1",
            "request_mode": "command",
            "messages": [{"role": "user", "content": "What can I do now?"}],
        },
        config={"configurable": {"thread_id": thread_id_for("wb-1")}},
    )

    assert gateway.context_calls == 1
    assert gateway.detail_calls == []
    assert result["context_revision"] == 4
    assert result["decision"]["kind"] == "answer"
    assert "Feature A" in result["answer"]


async def test_todo_question_reads_focused_worktree():
    gateway = FakeGateway()
    model = StructuredFakeModel(
        json.dumps({
            "kind": "read_tool",
            "toolName": "read_worktree_todos",
            "toolReason": "The user requested open tasks.",
        }),
        "Todo items: Review changes.",
    )
    graph = build_graph(gateway, model=model)

    result = await graph.ainvoke(
        {
            "workbench_id": "wb-1",
            "request_mode": "command",
            "messages": [{"role": "user", "content": "Show me the todo list."}],
        }
    )

    assert gateway.detail_calls == ["todos"]
    assert result["decision"]["toolName"] == "read_worktree_todos"
    assert "Review changes" in result["answer"]


async def test_plc_question_hands_off_to_existing_agent():
    gateway = FakeGateway()
    model = StructuredFakeModel(json.dumps({
        "kind": "answer",
        "answer": "This belongs in the existing PLC Assistant.",
    }))
    graph = build_graph(gateway, model=model)

    result = await graph.ainvoke(
        {
            "workbench_id": "wb-1",
            "request_mode": "command",
            "messages": [{"role": "user", "content": "Why does this PLC network fail?"}],
        }
    )

    assert result["decision"]["kind"] == "answer"
    assert "PLC Assistant" in result["answer"]


async def test_configured_model_composes_read_only_answer_with_versioned_metadata():
    gateway = FakeGateway()
    model = StructuredFakeModel(json.dumps({
        "kind": "answer",
        "answer": "Review the focused worktree todo list first.",
    }))
    graph = build_graph(
        gateway,
        model=model,
        model_metadata={"provider": "deepseek", "model": "deepseek-v4-flash", "mode": "llm"},
        prompt_append="Prefer one concrete next step.",
    )

    result = await graph.ainvoke(
        {
            "workbench_id": "wb-1",
            "request_mode": "command",
            "messages": [{"role": "user", "content": "What should I do next?"}],
        }
    )

    assert result["answer"] == "Review the focused worktree todo list first."
    assert result["assistant_metadata"] == {
        "provider": "deepseek",
        "model": "deepseek-v4-flash",
        "mode": "llm",
        "graphVersion": "0.1.0",
        "promptVersion": "workbench-assistant-command-v1",
        "orientationPromptVersion": "workbench-assistant-orientation-v1",
    }
    assert len(model.calls) == 1
    prompt = "\n".join(str(message.content) for message in model.calls[0])
    assert "Feature A" in prompt
    assert "Prefer one concrete next step." in prompt


async def test_status_facts_do_not_allow_model_to_override_runtime_worktree_membership():
    gateway = FakeGateway()
    async def get_context(workbench_id):
        return _status_context(workbench_id)

    gateway.get_context = get_context
    model = StructuredFakeModel(json.dumps({
        "kind": "answer",
        "answer": "Registered worktrees: Feature A, Feature B.",
    }))
    graph = build_graph(gateway, model=model)

    result = await graph.ainvoke({
        "workbench_id": "wb-1",
        "request_mode": "command",
        "messages": [{"role": "user", "content": "How many worktrees are there?"}],
    })

    assert "Feature A" in result["answer"]
    assert "Feature B" in result["answer"]
    assert "only master" not in result["answer"].lower()
    assert len(model.calls) == 1


async def test_svn_facts_use_the_authoritative_worktree_detail():
    gateway = FakeGateway()
    model = StructuredFakeModel(
        json.dumps({
            "kind": "read_tool",
            "toolName": "read_svn_state",
            "toolReason": "The user asked for the current revision.",
        }),
        "SVN state: base revision 40, current revision 42.",
    )
    graph = build_graph(gateway, model=model)

    result = await graph.ainvoke({
        "workbench_id": "wb-1",
        "request_mode": "command",
        "messages": [{"role": "user", "content": "What is the current SVN revision?"}],
    })

    assert "current revision 42" in result["answer"]
    assert "revision 1" not in result["answer"]
    assert gateway.detail_calls == ["svn"]
    assert len(model.calls) == 2


async def test_orientation_proposes_one_next_step_without_tools():
    gateway = FakeGateway()
    model = StructuredFakeModel(json.dumps({
        "likelyIntent": "review the focused worktree",
        "observations": ["Feature A has two open tasks"],
        "proposedNextStep": "Read the focused worktree todo list.",
        "confirmationQuestion": "Would you like me to read it?",
    }))
    graph = build_graph(gateway, model=model)

    result = await graph.ainvoke({
        "workbench_id": "wb-1",
        "request_mode": "orientation",
    })

    assert result["orientation_complete"] is True
    assert result["orientation_proposal"]["confirmationQuestion"] == "Would you like me to read it?"
    assert gateway.context_calls == 1
    assert gateway.detail_calls == []
    assert len(model.calls) == 1


async def test_command_model_can_choose_read_tool_then_summarize():
    gateway = FakeGateway()
    model = StructuredFakeModel(
        json.dumps({
            "kind": "read_tool",
            "toolName": "read_worktree_todos",
            "toolReason": "The user needs the outstanding work items.",
        }),
        "The focused worktree has one todo: Review changes.",
    )
    graph = build_graph(gateway, model=model)

    result = await graph.ainvoke({
        "workbench_id": "wb-1",
        "request_mode": "command",
        "messages": [{"role": "user", "content": "Please inspect the outstanding work items."}],
    })

    assert gateway.detail_calls == ["todos"]
    assert result["decision"]["kind"] == "read_tool"
    assert result["answer"] == "The focused worktree has one todo: Review changes."
    assert len(model.calls) == 2


async def test_command_model_can_ask_clarification_without_tool_call():
    gateway = FakeGateway()
    model = StructuredFakeModel(json.dumps({
        "kind": "clarification",
        "question": "Which worktree should I inspect?",
    }))
    graph = build_graph(gateway, model=model)

    result = await graph.ainvoke({
        "workbench_id": "wb-1",
        "request_mode": "command",
        "messages": [{"role": "user", "content": "Inspect the work."}],
    })

    assert gateway.detail_calls == []
    assert result["answer"] == "Which worktree should I inspect?"


async def test_command_model_mutation_waits_for_approval():
    gateway = FakeGateway()
    model = StructuredFakeModel(json.dumps({
        "kind": "mutation_proposal",
        "mutation": {
            "kind": "create_worktree",
            "name": "assistant-feature",
            "branch": "assistant/feature",
            "startPoint": "master",
        },
    }))
    graph = build_graph(gateway, model=model, checkpointer=MemorySaver())

    result = await graph.ainvoke(
        {
            "workbench_id": "wb-1",
            "request_mode": "command",
            "messages": [{"role": "user", "content": "Please prepare a new work area."}],
        },
        config={"configurable": {"thread_id": thread_id_for("wb-1")}},
    )

    assert result["__interrupt__"]
    assert gateway.detail_calls == []


def _status_context(workbench_id: str):
    return {
        "workbenchId": workbench_id,
        "runtime": {
            "workbenchRevision": 4,
            "focus": {"worktreeId": "wt-1", "deviceId": None},
            "availableActions": [],
            "worktrees": [
                {"worktreeId": "wt-1", "name": "Feature A", "todoCount": 2},
                {"worktreeId": "wt-2", "name": "Feature B", "todoCount": 0},
            ],
        },
        "availableActions": [],
        "observedAt": "2026-08-09T00:00:00Z",
    }


async def _wrong_model_answer(messages):
    raise AssertionError("authoritative facts must not be delegated to the model")
