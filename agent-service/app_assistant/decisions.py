from enum import Enum
from typing import Literal

from pydantic import BaseModel, ConfigDict, Field


class AssistantRequestMode(str, Enum):
    ORIENTATION = "orientation"
    COMMAND = "command"


class OrientationProposal(BaseModel):
    model_config = ConfigDict(populate_by_name=True, extra="forbid")

    likely_intent: str = Field(alias="likelyIntent")
    observations: list[str]
    proposed_next_step: str = Field(alias="proposedNextStep")
    confirmation_question: str = Field(alias="confirmationQuestion")


class MutationProposal(BaseModel):
    model_config = ConfigDict(populate_by_name=True, extra="forbid")

    kind: Literal["create_worktree"]
    name: str
    branch: str
    start_point: str | None = Field(default=None, alias="startPoint")


class AssistantDecision(BaseModel):
    model_config = ConfigDict(populate_by_name=True, extra="forbid")

    kind: Literal["answer", "clarification", "read_tool", "mutation_proposal"]
    answer: str | None = None
    question: str | None = None
    tool_name: Literal[
        "read_worktree_todos",
        "read_commit_history",
        "read_svn_state",
    ] | None = Field(default=None, alias="toolName")
    tool_reason: str | None = Field(default=None, alias="toolReason")
    mutation: MutationProposal | None = None
