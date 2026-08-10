import json

import pytest
from langgraph.checkpoint.memory import MemorySaver

from app_assistant.graph import build_graph, thread_id_for
from app_assistant.prompts import build_command_prompt, build_orientation_prompt
from langgraph.types import Command


class EvaluationDecisionModel:
    def __init__(self, responses):
        self.responses = list(responses)

    async def ainvoke(self, messages):
        return type("Response", (), {"content": self.responses.pop(0)})()


def decision(kind: str, *, tool_name: str | None = None, answer: str | None = None):
    value = {"kind": kind}
    if tool_name:
        value["toolName"] = tool_name
        value["toolReason"] = "The user requested this read."
    if answer:
        value["answer"] = answer
    return json.dumps(value)


class EvaluationGateway:
    def __init__(self):
        self.detail_calls = []

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
        self.detail_calls.append("todos")
        return {"tasks": [{"taskId": "task-1", "title": "Review changes"}]}

    async def get_history(self, workbench_id: str, worktree_id: str):
        self.detail_calls.append("history")
        return {"commits": [{"sha": "abc", "message": "Add feature"}]}

    async def get_svn(self, workbench_id: str, worktree_id: str):
        self.detail_calls.append("svn")
        return {"currentRevision": 42, "baseRevision": 40}

    async def create_worktree(self, workbench_id: str, **kwargs):
        return {"workbenchId": workbench_id, "name": kwargs["name"], "selected": False}


def test_history_prompt_exposes_bootstrap_evidence_and_depth_rules():
    prompt = build_command_prompt(
        runtime_snapshot={
            "runtime": {"workbenchRevision": 4, "focus": {"worktreeId": "wt-1"}},
            "history": {
                "worktreeId": "wt-1",
                "git": {"commits": [{"message": "Add feature"}]},
                "svn": {"entries": [{"revision": 42, "message": "Native update"}]},
            },
        },
        user_message="Analyze the recent changes.",
        messages=[],
    )

    assert "Add feature" in prompt
    assert "Native update" in prompt
    assert "historyDepth=all" in prompt
    assert "Summarize history normally" in prompt


def test_prompts_define_friendly_concise_and_non_reasoning_reply_style():
    command_prompt = build_command_prompt(
        runtime_snapshot={"runtime": {"workbenchRevision": 4}},
        user_message="What changed?",
        messages=[],
    )
    orientation_prompt = build_orientation_prompt({"runtime": {"workbenchRevision": 4}})

    for prompt in (command_prompt, orientation_prompt):
        assert "friendly" in prompt.lower()
        assert "concise" in prompt.lower()
        assert "user's level" in prompt.lower()
        assert "private chain-of-thought" in prompt.lower()
        assert "unnecessary" in prompt.lower()


@pytest.mark.parametrize(
    ("question", "responses", "detail_calls"),
    [
        ("What can I do now?", [decision("answer", answer="Review the focused worktree." )], []),
        ("Show the todo list.", [decision("read_tool", tool_name="read_worktree_todos"), "Todo items: Review changes."], ["todos"]),
        ("Show recent commit history.", [decision("read_tool", tool_name="read_commit_history"), "Recent commits: Add feature."], ["history"]),
        ("What is the current SVN revision?", [decision("read_tool", tool_name="read_svn_state"), "SVN state: current revision 42."], ["svn"]),
        ("Why does this PLC network fail?", [decision("answer", answer="Continue in the existing PLC Assistant." )], []),
    ],
)
async def test_supported_command_decisions_have_regression_coverage(question, responses, detail_calls):
    gateway = EvaluationGateway()
    graph = build_graph(gateway, model=EvaluationDecisionModel(responses))

    result = await graph.ainvoke(
        {"workbench_id": "wb-eval", "request_mode": "command", "messages": [{"role": "user", "content": question}]},
    )

    assert gateway.detail_calls == detail_calls
    assert result.get("answer")


async def test_mutation_intent_stops_at_explicit_approval_boundary():
    graph = build_graph(
        EvaluationGateway(),
        checkpointer=MemorySaver(),
        model=EvaluationDecisionModel([json.dumps({
            "kind": "mutation_proposal",
            "mutation": {
                "kind": "create_worktree",
                "name": "assistant-feature",
                "branch": "assistant/feature",
                "startPoint": "master",
            },
        })]),
    )

    result = await graph.ainvoke(
        {
            "workbench_id": "wb-eval-mutation",
            "request_mode": "command",
            "messages": [{"role": "user", "content": "Please prepare a new work area."}],
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
