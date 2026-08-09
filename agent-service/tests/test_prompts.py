from app_assistant.prompts import build_command_prompt, build_orientation_prompt


CONTEXT = {
    "runtime": {
        "worktrees": [{"name": "master", "todoCount": 2}],
        "focus": {"worktreeId": "wt-1", "deviceId": None},
    },
    "availableActions": [{"id": "read_worktree_todos", "enabled": True}],
}


def test_orientation_prompt_describes_workflow_and_forbids_execution():
    prompt = build_orientation_prompt(CONTEXT)

    assert "workbench" in prompt.lower()
    assert "worktree" in prompt.lower()
    assert "PLC Assistant" in prompt
    assert "Do not call tools" in prompt
    assert "ask whether the user wants to proceed" in prompt.lower()
    assert "Return only one JSON object" in prompt
    assert '"likelyIntent"' in prompt


def test_command_prompt_requires_one_decision_kind_and_includes_context():
    prompt = build_command_prompt(
        runtime_snapshot=CONTEXT,
        user_message="Read the todo list",
        messages=[],
    )

    assert "read_tool" in prompt
    assert "master" in prompt
    assert "exactly one" in prompt.lower()
    assert "Return only JSON" in prompt
    assert '"toolName"' in prompt
