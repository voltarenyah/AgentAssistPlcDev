import json

from langgraph.checkpoint.memory import MemorySaver
from langgraph.types import Command

from app_assistant.gateway import GatewayStaleError
from app_assistant.graph import build_graph, thread_id_for


class MutationDecisionModel:
    async def ainvoke(self, messages):
        return type("Response", (), {"content": json.dumps({
            "kind": "mutation_proposal",
            "mutation": {
                "kind": "create_worktree",
                "name": "assistant-feature",
                "branch": "assistant/feature",
                "startPoint": "master",
            },
        })})()


class MutationGateway:
    async def get_context(self, workbench_id: str):
        return {
            "workbenchId": workbench_id,
            "runtime": {
                "workbenchRevision": 8,
                "focus": {"worktreeId": "wt-1"},
                "worktrees": [],
                "availableActions": [],
            },
            "availableActions": [],
        }

    async def get_todos(self, workbench_id: str, worktree_id: str):
        return {"tasks": []}

    async def get_history(self, workbench_id: str, worktree_id: str):
        return {"commits": []}

    async def get_svn(self, workbench_id: str, worktree_id: str):
        return {}

    async def create_worktree(self, workbench_id: str, **kwargs):
        return {"workbenchId": workbench_id, "name": kwargs["name"], "branch": kwargs["branch"], "selected": False}


class StaleMutationGateway(MutationGateway):
    def __init__(self):
        self.context_calls = 0

    async def get_context(self, workbench_id: str):
        self.context_calls += 1
        revision = 8 if self.context_calls == 1 else 9
        return {
            "workbenchId": workbench_id,
            "runtime": {
                "workbenchRevision": revision,
                "focus": {"worktreeId": "wt-1"},
                "worktrees": [],
                "availableActions": [],
            },
            "availableActions": [],
        }

    async def create_worktree(self, workbench_id: str, **kwargs):
        raise GatewayStaleError("ApiHost runtime context is stale.")


async def test_create_worktree_pauses_for_explicit_approval():
    graph = build_graph(MutationGateway(), checkpointer=MemorySaver(), model=MutationDecisionModel())
    config = {"configurable": {"thread_id": thread_id_for("wb-1")}}

    result = await graph.ainvoke(
        {"workbench_id": "wb-1", "request_mode": "command", "messages": [{"role": "user", "content": "Please prepare a new work area."}]},
        config=config,
    )

    assert result["__interrupt__"]
    proposal = result["__interrupt__"][0].value
    assert proposal["kind"] == "create_worktree"
    assert proposal["expectedWorkbenchRevision"] == 8


async def test_rejection_is_cancelled_without_mutation():
    graph = build_graph(MutationGateway(), checkpointer=MemorySaver(), model=MutationDecisionModel())
    config = {"configurable": {"thread_id": thread_id_for("wb-2")}}
    await graph.ainvoke(
        {"workbench_id": "wb-2", "request_mode": "command", "messages": [{"role": "user", "content": "Please prepare a new work area."}]},
        config=config,
    )

    result = await graph.ainvoke(Command(resume={"decision": "reject"}), config=config)

    assert result["answer"] == "The worktree creation proposal was cancelled."


async def test_approval_calls_gateway_with_captured_revision():
    gateway = MutationGateway()
    graph = build_graph(gateway, checkpointer=MemorySaver(), model=MutationDecisionModel())
    config = {"configurable": {"thread_id": thread_id_for("wb-3")}}
    await graph.ainvoke(
        {"workbench_id": "wb-3", "request_mode": "command", "messages": [{"role": "user", "content": "Please prepare a new work area."}]},
        config=config,
    )

    result = await graph.ainvoke(Command(resume={"decision": "approve"}), config=config)

    assert result["detail"]["mutation"]["selected"] is False
    assert "assistant-feature" in result["answer"]


async def test_stale_approval_refreshes_runtime_context_before_responding():
    gateway = StaleMutationGateway()
    graph = build_graph(gateway, checkpointer=MemorySaver(), model=MutationDecisionModel())
    config = {"configurable": {"thread_id": thread_id_for("wb-stale")}}
    await graph.ainvoke(
        {"workbench_id": "wb-stale", "request_mode": "command", "messages": [{"role": "user", "content": "Please prepare a new work area."}]},
        config=config,
    )

    result = await graph.ainvoke(Command(resume={"decision": "approve"}), config=config)

    assert gateway.context_calls == 2
    assert result["context_revision"] == 9
    assert result["runtime_snapshot"]["runtime"]["workbenchRevision"] == 9
    assert result["proposed_action"] is None
    assert "changed before approval" in result["answer"]
