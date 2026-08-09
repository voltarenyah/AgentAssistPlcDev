import os
from typing import Mapping
from typing import Any, Protocol

from langchain_core.messages import HumanMessage, SystemMessage
from langgraph.graph import END, START, StateGraph
from langgraph.types import interrupt

from . import __version__
from .gateway import WorkbenchGateway
from .model import DEFAULT_MODEL, build_model_from_env
from .prompts import PROMPT_VERSION, build_system_prompt
from .state import AppAssistantState


class Gateway(Protocol):
    async def get_context(self, workbench_id: str) -> Any: ...

    async def get_todos(self, workbench_id: str, worktree_id: str) -> Any: ...

    async def get_history(self, workbench_id: str, worktree_id: str) -> Any: ...

    async def get_svn(self, workbench_id: str, worktree_id: str) -> Any: ...

    async def create_worktree(
        self,
        workbench_id: str,
        *,
        name: str,
        branch: str,
        start_point: str | None,
        expected_revision: int,
        request_id: str,
    ) -> dict[str, Any]: ...


def thread_id_for(workbench_id: str) -> str:
    return f"app-assistant:{workbench_id}"


def _message_text(messages: list[Any]) -> str:
    if not messages:
        return ""
    message = messages[-1]
    if isinstance(message, dict):
        return str(message.get("content", ""))
    return str(getattr(message, "content", message))


def _as_dict(value: Any) -> dict[str, Any]:
    if hasattr(value, "model_dump"):
        return value.model_dump(by_alias=True)
    return value


def _metadata(model_metadata: Mapping[str, str]) -> dict[str, str]:
    return {
        **model_metadata,
        "graphVersion": __version__,
        "promptVersion": PROMPT_VERSION,
    }


async def _bootstrap_async(
    state: AppAssistantState,
    gateway: Gateway,
    assistant_metadata: Mapping[str, str],
) -> dict[str, Any]:
    context = _as_dict(await gateway.get_context(state["workbench_id"]))
    runtime = context.get("runtime", context)
    return {
        "runtime_snapshot": context,
        "context_revision": int(runtime.get("workbenchRevision", 0)),
        "assistant_metadata": dict(assistant_metadata),
    }


def _classify(state: AppAssistantState) -> dict[str, Any]:
    text = _message_text(state.get("messages", [])).lower()
    if any(word in text for word in ("create worktree", "new worktree", "create branch")):
        intent = "create_worktree"
    elif any(word in text for word in ("plc", "network", "block", "diagnos", "program")):
        intent = "plc_question"
    elif any(word in text for word in ("todo", "task", "next")):
        intent = "todos"
    elif any(word in text for word in ("commit", "history", "git")):
        intent = "history"
    elif any(word in text for word in ("svn", "revision")):
        intent = "svn"
    else:
        intent = "status"
    return {"intent": intent}


def _detail_worktree(state: AppAssistantState) -> str | None:
    runtime = state.get("runtime_snapshot", {}).get("runtime", state.get("runtime_snapshot", {}))
    focus = runtime.get("focus", {})
    return focus.get("worktreeId") or focus.get("worktree_id")


async def _read_detail(state: AppAssistantState, gateway: Gateway) -> dict[str, Any]:
    worktree_id = _detail_worktree(state)
    if not worktree_id:
        return {"detail": {"error": "Select a worktree first."}}
    workbench_id = state["workbench_id"]
    intent = state.get("intent")
    if intent == "todos":
        detail = _as_dict(await gateway.get_todos(workbench_id, worktree_id))
    elif intent == "history":
        detail = _as_dict(await gateway.get_history(workbench_id, worktree_id))
    else:
        detail = _as_dict(await gateway.get_svn(workbench_id, worktree_id))
    return {"detail": detail}


async def _propose_create_worktree(state: AppAssistantState, gateway: Gateway) -> dict[str, Any]:
    runtime = state.get("runtime_snapshot", {}).get("runtime", state.get("runtime_snapshot", {}))
    revision = int(state.get("context_revision", runtime.get("workbenchRevision", 0)))
    workbench_id = state["workbench_id"]
    proposal = {
        "kind": "create_worktree",
        "workbenchId": workbench_id,
        "name": "assistant-feature",
        "branch": "assistant/feature",
        "startPoint": "master",
        "expectedWorkbenchRevision": revision,
        "requestId": f"app-assistant:{workbench_id}:create:{revision}",
    }
    decision = interrupt(proposal)
    if not isinstance(decision, dict) or decision.get("decision") != "approve":
        return {"proposed_action": proposal, "answer": "The worktree creation proposal was cancelled."}
    try:
        result = await gateway.create_worktree(
            workbench_id,
            name=proposal["name"],
            branch=proposal["branch"],
            start_point=proposal["startPoint"],
            expected_revision=proposal["expectedWorkbenchRevision"],
            request_id=proposal["requestId"],
        )
        return {"proposed_action": proposal, "detail": {"mutation": result}}
    except Exception as exception:
        if exception.__class__.__name__ == "GatewayStaleError":
            try:
                context = _as_dict(await gateway.get_context(workbench_id))
                runtime = context.get("runtime", context)
                revision = int(runtime.get("workbenchRevision", 0))
                return {
                    "runtime_snapshot": context,
                    "context_revision": revision,
                    "proposed_action": None,
                    "answer": (
                        "The workbench changed before approval. I refreshed the context to "
                        f"runtime revision {revision}; please review the worktree state and request the action again."
                    ),
                }
            except Exception as refresh_exception:
                return {
                    "proposed_action": proposal,
                    "answer": f"The workbench changed before approval and the refresh failed: {refresh_exception}",
                }
        return {"proposed_action": proposal, "answer": f"Worktree creation was not completed: {exception}"}


def _fallback_answer(state: AppAssistantState) -> str:
    intent = state.get("intent")
    if intent == "create_worktree" and state.get("answer"):
        return state["answer"] or "The worktree creation proposal was cancelled."
    context = state.get("runtime_snapshot", {})
    runtime = context.get("runtime", context)
    if intent == "plc_question":
        answer = "This is a PLC-program question. Please continue in the existing PLC Assistant for knowledge-db-backed diagnosis."
    elif intent == "status":
        worktrees = runtime.get("worktrees", [])
        names = ", ".join(item.get("name", item.get("worktreeId", "unknown")) for item in worktrees)
        actions = [item.get("label", item.get("id")) for item in context.get("availableActions", runtime.get("availableActions", [])) if item.get("enabled")]
        answer = f"Workbench status is at runtime revision {state.get('context_revision', 0)}."
        answer += f" Registered worktrees: {names or 'none'}."
        if actions:
            answer += f" Available read actions: {', '.join(str(action) for action in actions)}."
    elif intent == "create_worktree":
        mutation = (state.get("detail") or {}).get("mutation", {})
        answer = f"Created worktree '{mutation.get('name', 'new worktree')}' on branch '{mutation.get('branch', 'unknown')}'. It remains unselected; you can select it from the workbench UI."
    elif intent == "todos":
        tasks = (state.get("detail") or {}).get("tasks", [])
        answer = "Todo items: " + (", ".join(item.get("title", item.get("taskId", "unnamed")) for item in tasks) if tasks else "none recorded.")
    elif intent == "history":
        commits = (state.get("detail") or {}).get("commits", [])
        answer = "Recent commits: " + (", ".join(item.get("message", item.get("sha", "unknown")) for item in commits) if commits else "none available.")
    else:
        detail = state.get("detail") or {}
        if detail.get("error"):
            answer = detail["error"]
        else:
            answer = f"SVN state: base revision {detail.get('baseRevision', 'unknown')}, current revision {detail.get('currentRevision', 'unknown')}."
    return answer


def _content_text(content: Any) -> str:
    if isinstance(content, str):
        return content
    if isinstance(content, list):
        return "".join(
            item if isinstance(item, str) else str(item.get("text", ""))
            for item in content
            if isinstance(item, (str, dict))
        )
    return str(content)


async def _compose_async(
    state: AppAssistantState,
    model: Any | None,
    prompt_append: str,
) -> dict[str, Any]:
    if state.get("intent") == "create_worktree" and state.get("answer"):
        return {}

    fallback = _fallback_answer(state)
    intent = state.get("intent")
    use_model = model is not None and intent in {"status", "todos", "history", "svn"}
    if not use_model:
        return {"answer": fallback}

    prompt = build_system_prompt(
        runtime_snapshot=state.get("runtime_snapshot", {}),
        detail=state.get("detail"),
        prompt_append=prompt_append,
    )
    question = _message_text(state.get("messages", []))
    try:
        response = await model.ainvoke(
            [SystemMessage(content=prompt), HumanMessage(content=question)]
        )
        answer = _content_text(getattr(response, "content", response)).strip()
        return {"answer": answer or fallback}
    except Exception:
        return {"answer": fallback}


def build_graph(
    gateway: Gateway,
    checkpointer: Any = None,
    *,
    model: Any | None = None,
    model_metadata: Mapping[str, str] | None = None,
    prompt_append: str | None = None,
):
    resolved_model = model
    resolved_metadata = dict(model_metadata or {})
    if not resolved_metadata:
        resolved_metadata = {
            "provider": "deepseek",
            "model": DEFAULT_MODEL,
            "mode": "deterministic-fallback",
        }
    assistant_metadata = _metadata(resolved_metadata)
    resolved_prompt_append = (
        prompt_append if prompt_append is not None else os.getenv("APP_ASSISTANT_PROMPT_APPEND", "")
    )
    builder = StateGraph(AppAssistantState)

    async def bootstrap_node(state: AppAssistantState) -> dict[str, Any]:
        return await _bootstrap_async(state, gateway, assistant_metadata)

    async def detail_node(state: AppAssistantState) -> dict[str, Any]:
        return await _read_detail(state, gateway)

    async def proposal_node(state: AppAssistantState) -> dict[str, Any]:
        return await _propose_create_worktree(state, gateway)

    async def compose_node(state: AppAssistantState) -> dict[str, Any]:
        return await _compose_async(state, resolved_model, resolved_prompt_append)

    builder.add_node("bootstrap", bootstrap_node)
    builder.add_node("classify_intent", _classify)
    builder.add_node("read_worktree_detail", detail_node)
    builder.add_node("propose_create_worktree", proposal_node)
    builder.add_node("compose_answer", compose_node)
    builder.add_edge(START, "bootstrap")
    builder.add_edge("bootstrap", "classify_intent")
    builder.add_conditional_edges(
        "classify_intent",
        lambda state: (
            "read_worktree_detail" if state.get("intent") in {"todos", "history", "svn"}
            else "propose_create_worktree" if state.get("intent") == "create_worktree"
            else "compose_answer"
        ),
        {
            "read_worktree_detail": "read_worktree_detail",
            "propose_create_worktree": "propose_create_worktree",
            "compose_answer": "compose_answer",
        },
    )
    builder.add_edge("read_worktree_detail", "compose_answer")
    builder.add_edge("propose_create_worktree", "compose_answer")
    builder.add_edge("compose_answer", END)
    return builder.compile(checkpointer=checkpointer)




def build_environment_graph() -> Any:
    model, model_metadata = build_model_from_env()
    return build_graph(
        WorkbenchGateway.from_env(),
        model=model,
        model_metadata=model_metadata,
    )


graph = build_environment_graph()
