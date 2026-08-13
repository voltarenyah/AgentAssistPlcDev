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


class CreateWorkbenchDecisionModel:
    async def ainvoke(self, messages):
        return type("Response", (), {"content": json.dumps({
            "kind": "mutation_proposal",
            "mutation": {
                "kind": "create_workbench",
                "name": "Assistant Project",
                "engineeringProjectPath": "C:\\Projects\\Line.ap17",
                "rootPath": "C:\\Automation\\Assistant Project",
            },
        })})()


class MutationGateway:
    def __init__(self):
        self.create_kwargs = None

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
        self.create_kwargs = kwargs
        return {"workbenchId": workbench_id, "name": kwargs["name"], "branch": kwargs["branch"], "selected": False}

    async def create_workbench(self, workbench_id: str, **kwargs):
        self.create_workbench_kwargs = kwargs
        return {
            "workbenchId": "created-wb",
            "name": kwargs["name"],
            "sourceProjectPath": kwargs["engineering_project_path"],
        }


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


async def test_create_workbench_pauses_with_tia_project_details_for_explicit_approval():
    graph = build_graph(MutationGateway(), checkpointer=MemorySaver(), model=CreateWorkbenchDecisionModel())
    config = {"configurable": {"thread_id": thread_id_for("wb-project")}}

    result = await graph.ainvoke(
        {
            "workbench_id": "wb-project",
            "request_mode": "command",
            "messages": [{"role": "user", "content": "Create a project from C:\\Projects\\Line.ap17."}],
        },
        config=config,
    )

    proposal = result["__interrupt__"][0].value
    assert proposal["kind"] == "create_workbench"
    assert proposal["name"] == "Assistant Project"
    assert proposal["engineeringProjectPath"] == "C:\\Projects\\Line.ap17"


async def test_create_workbench_approval_calls_gateway_with_project_details():
    gateway = MutationGateway()
    graph = build_graph(gateway, checkpointer=MemorySaver(), model=CreateWorkbenchDecisionModel())
    config = {"configurable": {"thread_id": thread_id_for("wb-project-approved")}}
    await graph.ainvoke(
        {
            "workbench_id": "wb-project-approved",
            "request_mode": "command",
            "messages": [{"role": "user", "content": "Create a project from C:\\Projects\\Line.ap17."}],
        },
        config=config,
    )

    result = await graph.ainvoke(Command(resume={"decision": "approve"}), config=config)

    assert gateway.create_workbench_kwargs["name"] == "Assistant Project"
    assert gateway.create_workbench_kwargs["engineering_project_path"] == "C:\\Projects\\Line.ap17"
    assert result["detail"]["mutation"]["workbenchId"] == "created-wb"


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


async def test_approval_uses_inferred_baseline_when_model_omits_start_point():
    gateway = MutationGateway()

    class OmittedBaselineModel(MutationDecisionModel):
        async def ainvoke(self, messages):
            return type("Response", (), {"content": json.dumps({
                "kind": "mutation_proposal",
                "mutation": {
                    "kind": "create_worktree",
                    "name": "assistant-feature",
                    "branch": "assistant/feature",
                },
            })})()

    async def get_context(workbench_id: str):
        return {
            "workbenchId": workbench_id,
            "runtime": {
                "workbenchRevision": 8,
                "focus": {"worktreeId": "wt-1"},
                "worktrees": [{"worktreeId": "wt-1", "name": "master", "branch": "master"}],
                "availableActions": [],
            },
            "availableActions": [],
        }

    gateway.get_context = get_context
    graph = build_graph(gateway, checkpointer=MemorySaver(), model=OmittedBaselineModel())
    config = {"configurable": {"thread_id": thread_id_for("wb-inferred-baseline")}}
    await graph.ainvoke(
        {"workbench_id": "wb-inferred-baseline", "request_mode": "command", "messages": [{"role": "user", "content": "Please prepare a new work area."}]},
        config=config,
    )

    await graph.ainvoke(Command(resume={"decision": "approve"}), config=config)

    assert gateway.create_kwargs["start_point"] == "master"


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
