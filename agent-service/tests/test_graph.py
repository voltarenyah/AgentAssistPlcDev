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


async def test_status_question_uses_bootstrap_without_detail_reads():
    gateway = FakeGateway()
    graph = build_graph(gateway)

    result = await graph.ainvoke(
        {
            "workbench_id": "wb-1",
            "messages": [{"role": "user", "content": "What can I do now?"}],
        },
        config={"configurable": {"thread_id": thread_id_for("wb-1")}},
    )

    assert gateway.context_calls == 1
    assert gateway.detail_calls == []
    assert result["context_revision"] == 4
    assert result["intent"] == "status"
    assert "Feature A" in result["answer"]


async def test_todo_question_reads_focused_worktree():
    gateway = FakeGateway()
    graph = build_graph(gateway)

    result = await graph.ainvoke(
        {
            "workbench_id": "wb-1",
            "messages": [{"role": "user", "content": "Show me the todo list."}],
        }
    )

    assert gateway.detail_calls == ["todos"]
    assert result["intent"] == "todos"
    assert "Review changes" in result["answer"]


async def test_plc_question_hands_off_to_existing_agent():
    gateway = FakeGateway()
    graph = build_graph(gateway)

    result = await graph.ainvoke(
        {
            "workbench_id": "wb-1",
            "messages": [{"role": "user", "content": "Why does this PLC network fail?"}],
        }
    )

    assert result["intent"] == "plc_question"
    assert "PLC Assistant" in result["answer"]


async def test_configured_model_composes_read_only_answer_with_versioned_metadata():
    gateway = FakeGateway()
    model = FakeModel()
    graph = build_graph(
        gateway,
        model=model,
        model_metadata={"provider": "deepseek", "model": "deepseek-v4-flash", "mode": "llm"},
        prompt_append="Prefer one concrete next step.",
    )

    result = await graph.ainvoke(
        {
            "workbench_id": "wb-1",
            "messages": [{"role": "user", "content": "What should I do next?"}],
        }
    )

    assert result["answer"] == "Review the focused worktree todo list first."
    assert result["assistant_metadata"] == {
        "provider": "deepseek",
        "model": "deepseek-v4-flash",
        "mode": "llm",
        "graphVersion": "0.1.0",
        "promptVersion": "workbench-assistant-v1",
    }
    assert len(model.calls) == 1
    prompt = "\n".join(str(message.content) for message in model.calls[0])
    assert "Feature A" in prompt
    assert "Prefer one concrete next step." in prompt
