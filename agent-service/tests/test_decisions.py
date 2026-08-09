import pytest

from app_assistant.decisions import AssistantDecision, AssistantRequestMode, OrientationProposal


def test_orientation_contract_requires_confirmation_question():
    proposal = OrientationProposal.model_validate({
        "likelyIntent": "review the focused worktree",
        "observations": ["master has two open tasks"],
        "proposedNextStep": "Read the focused worktree todo list.",
        "confirmationQuestion": "Would you like me to read it?",
    })

    assert proposal.confirmation_question == "Would you like me to read it?"


def test_decision_contract_rejects_unknown_tool_names():
    with pytest.raises(ValueError):
        AssistantDecision.model_validate({
            "kind": "read_tool",
            "toolName": "delete_everything",
            "toolReason": "invalid",
        })


def test_request_mode_has_only_orientation_and_command_values():
    assert AssistantRequestMode.ORIENTATION.value == "orientation"
    assert AssistantRequestMode.COMMAND.value == "command"
