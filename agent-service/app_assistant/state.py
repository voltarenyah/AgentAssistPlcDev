from typing import Annotated, Any, TypedDict

from langgraph.graph.message import add_messages
from langchain_core.messages import AnyMessage

from .decisions import AssistantRequestMode


class AppAssistantState(TypedDict, total=False):
    workbench_id: str
    request_mode: AssistantRequestMode
    context_revision: int
    runtime_snapshot: dict[str, Any]
    messages: Annotated[list[AnyMessage], add_messages]
    orientation_complete: bool
    orientation_proposal: dict[str, Any] | None
    decision: dict[str, Any] | None
    tool_request: dict[str, Any] | None
    tool_result: dict[str, Any] | None
    intent: str | None
    proposed_action: dict[str, Any] | None
    answer: str | None
    detail: dict[str, Any] | None
    pending_approval: dict[str, Any] | None
    assistant_metadata: dict[str, str]
