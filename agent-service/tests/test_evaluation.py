import pytest
from langgraph.checkpoint.memory import MemorySaver

from app_assistant.graph import build_graph, thread_id_for
from langgraph.types import Command


class EvaluationGateway:
    async def get_context(self, workbench_id: str):
        return {
            "workbenchId": workbench_id,
            "runtime": {
                "workbenchRevision": 4,
                "focus": {"worktreeId": "wt-1", "deviceId": None},
                "worktrees": [{"worktreeId": "wt-1", "name": "Feature A", "todoCount": 1}],
                "availableActions": [],
            },
            "availableActions": [],
        }

    async def get_todos(self, workbench_id: str, worktree_id: str):
        return {"tasks": [{"taskId": "task-1", "title": "Review changes"}]}

    async def get_history(self, workbench_id: str, worktree_id: str):
        return {"commits": [{"sha": "abc", "message": "Add feature"}]}

    async def get_svn(self, workbench_id: str, worktree_id: str):
        return {"currentRevision": 42, "baseRevision": 40}

    async def create_worktree(self, workbench_id: str, **kwargs):
        return {"workbenchId": workbench_id, "name": kwargs["name"], "selected": False}


@pytest.mark.parametrize(
    ("question", "intent"),
    [
        ("What can I do now?", "status"),
        ("Show the todo list.", "todos"),
        ("Show recent commit history.", "history"),
        ("What is the current SVN revision?", "svn"),
        ("Why does this PLC network fail?", "plc_question"),
    ],
)
async def test_supported_read_only_intents_have_regression_coverage(question: str, intent: str):
    graph = build_graph(EvaluationGateway())

    result = await graph.ainvoke(
        {"workbench_id": "wb-eval", "messages": [{"role": "user", "content": question}]},
    )

    assert result["intent"] == intent
    assert result.get("answer")


async def test_mutation_intent_stops_at_explicit_approval_boundary():
    graph = build_graph(EvaluationGateway(), checkpointer=MemorySaver())

    result = await graph.ainvoke(
        {
            "workbench_id": "wb-eval-mutation",
            "messages": [{"role": "user", "content": "Create a new worktree."}],
        },
        config={"configurable": {"thread_id": thread_id_for("wb-eval-mutation")}},
    )

    assert result["__interrupt__"]
    assert result["__interrupt__"][0].value["kind"] == "create_worktree"

    rejected = await graph.ainvoke(
        Command(resume={"decision": "reject"}),
        config={"configurable": {"thread_id": thread_id_for("wb-eval-mutation")}},
    )
    assert "cancelled" in rejected["answer"]
