from typing import Annotated, Any, TypedDict

from langgraph.graph.message import add_messages
from langchain_core.messages import AnyMessage


class AppAssistantState(TypedDict, total=False):
    workbench_id: str
    context_revision: int
    runtime_snapshot: dict[str, Any]
    messages: Annotated[list[AnyMessage], add_messages]
    intent: str | None
    proposed_action: dict[str, Any] | None
    answer: str | None
    detail: dict[str, Any] | None
    pending_approval: dict[str, Any] | None
