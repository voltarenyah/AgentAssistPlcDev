import json
import os
from typing import Any, Mapping, Protocol

from langchain_core.messages import HumanMessage, SystemMessage
from langgraph.graph import END, START, StateGraph
from langgraph.types import interrupt

from . import __version__
from .contracts import HistoryDepth
from .decisions import AssistantDecision, AssistantRequestMode, OrientationProposal
from .gateway import GatewayStaleError, WorkbenchGateway
from .model import DEFAULT_MODEL, build_model_from_env
from .prompts import (
    COMMAND_PROMPT_VERSION,
    ORIENTATION_PROMPT_VERSION,
    build_command_prompt,
    build_orientation_prompt,
)
from .state import AppAssistantState


class Gateway(Protocol):
    async def get_context(self, workbench_id: str) -> Any: ...

    async def get_todos(self, workbench_id: str, worktree_id: str) -> Any: ...

    async def get_history(
        self, workbench_id: str, worktree_id: str, depth: HistoryDepth = "recent"
    ) -> Any: ...

    async def get_svn_history(
        self, workbench_id: str, worktree_id: str, depth: HistoryDepth = "recent"
    ) -> Any: ...

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
        "promptVersion": COMMAND_PROMPT_VERSION,
        "orientationPromptVersion": ORIENTATION_PROMPT_VERSION,
    }


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


def _json_content(content: Any) -> dict[str, Any]:
    text = _content_text(content).strip()
    if text.startswith("```"):
        lines = text.splitlines()
        text = "\n".join(lines[1:-1]).strip()
    value = json.loads(text)
    if not isinstance(value, dict):
        raise ValueError("The assistant model must return a JSON object.")
    return value


async def _invoke_model_json(model: Any, prompt: str, user_message: str) -> dict[str, Any]:
    response = await model.ainvoke([
        SystemMessage(content=prompt),
        HumanMessage(content=user_message),
    ])
    return _json_content(getattr(response, "content", response))


async def _invoke_model_text(model: Any, prompt: str, user_message: str) -> str:
    response = await model.ainvoke([
        SystemMessage(content=prompt),
        HumanMessage(content=user_message),
    ])
    content = getattr(response, "content", response)
    text = _content_text(content).strip()
    try:
        payload = _json_content(text)
    except (TypeError, ValueError, json.JSONDecodeError):
        return text
    if payload.get("kind") == "answer" and payload.get("answer"):
        return str(payload["answer"]).strip()
    return text


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


def _runtime(state: AppAssistantState) -> dict[str, Any]:
    context = state.get("runtime_snapshot", {})
    return context.get("runtime", context)


def _detail_worktree(state: AppAssistantState) -> str | None:
    focus = _runtime(state).get("focus", {})
    return focus.get("worktreeId") or focus.get("worktree_id")


def _fallback_orientation(state: AppAssistantState) -> OrientationProposal:
    runtime = _runtime(state)
    worktrees = runtime.get("worktrees", [])
    focus = runtime.get("focus", {})
    focused_id = focus.get("worktreeId") or focus.get("worktree_id")
    focused = next((item for item in worktrees if item.get("worktreeId") == focused_id), None)
    observations = [
        f"The workbench has {len(worktrees)} registered worktree(s).",
        f"The selected worktree is {focused.get('name', focused_id)}." if focused else "No worktree is currently selected.",
    ]
    if focused:
        next_step = f"Review the todo list for the selected worktree '{focused.get('name', focused_id)}'."
    else:
        next_step = "Select a worktree in the UI so I can inspect its current work items."
    return OrientationProposal(
        likelyIntent="understand the current workbench and choose the next useful move",
        observations=observations,
        proposedNextStep=next_step,
        confirmationQuestion="Would you like me to proceed with that next step?",
    )


def _orientation_answer(proposal: OrientationProposal) -> str:
    observations = " ".join(proposal.observations)
    return (
        f"{observations} Likely intention: {proposal.likely_intent}. "
        f"Suggested next step: {proposal.proposed_next_step} {proposal.confirmation_question}"
    )


async def _orient_async(
    state: AppAssistantState,
    model: Any | None,
) -> dict[str, Any]:
    proposal = _fallback_orientation(state)
    if model is not None:
        try:
            proposal = OrientationProposal.model_validate(
                await _invoke_model_json(model, build_orientation_prompt(state["runtime_snapshot"]), "Orient me to this workbench.")
            )
        except Exception:
            pass
    serialized = proposal.model_dump(by_alias=True)
    return {
        "orientation_complete": True,
        "orientation_proposal": serialized,
        "answer": _orientation_answer(proposal),
    }


async def _decide_async(
    state: AppAssistantState,
    model: Any | None,
    prompt_append: str,
) -> dict[str, Any]:
    if model is None:
        decision = AssistantDecision(
            kind="clarification",
            question="The assistant model is unavailable. What would you like me to inspect or explain?",
        )
    else:
        try:
            decision = AssistantDecision.model_validate(
                await _invoke_model_json(
                    model,
                    build_command_prompt(
                        runtime_snapshot=state["runtime_snapshot"],
                        user_message=_message_text(state.get("messages", [])),
                        messages=state.get("messages", []),
                        prompt_append=prompt_append,
                    ),
                    _message_text(state.get("messages", [])),
                )
            )
        except Exception:
            decision = AssistantDecision(
                kind="clarification",
                question="I could not safely interpret that request. Which worktree and action should I use?",
            )
    return {"decision": decision.model_dump(by_alias=True, exclude_none=True)}


def _decision_answer(state: AppAssistantState) -> str:
    decision = state.get("decision") or {}
    return str(decision.get("answer") or decision.get("question") or "Please tell me what you would like to do next.")


async def _read_detail(state: AppAssistantState, gateway: Gateway) -> dict[str, Any]:
    worktree_id = _detail_worktree(state)
    if not worktree_id:
        return {"tool_result": {"error": "Select a worktree first."}}
    workbench_id = state["workbench_id"]
    tool_name = (state.get("decision") or {}).get("toolName")
    history_depth = (state.get("decision") or {}).get("historyDepth") or "recent"
    if tool_name == "read_worktree_todos":
        detail = _as_dict(await gateway.get_todos(workbench_id, worktree_id))
    elif tool_name == "read_commit_history":
        detail = _as_dict(
            await gateway.get_history(workbench_id, worktree_id, history_depth)
            if history_depth != "recent"
            else await gateway.get_history(workbench_id, worktree_id)
        )
    elif tool_name == "read_svn_history":
        detail = _as_dict(
            await gateway.get_svn_history(workbench_id, worktree_id, history_depth)
            if history_depth != "recent"
            else await gateway.get_svn_history(workbench_id, worktree_id)
        )
    elif tool_name == "read_svn_state":
        detail = _as_dict(await gateway.get_svn(workbench_id, worktree_id))
    else:
        return {"tool_result": {"error": "The requested read action is not available."}}
    return {"tool_result": detail, "detail": detail}


def _fallback_tool_answer(state: AppAssistantState) -> str:
    result = state.get("tool_result") or {}
    if result.get("error"):
        return str(result["error"])
    tool_name = (state.get("decision") or {}).get("toolName")
    if tool_name == "read_worktree_todos":
        tasks = result.get("tasks", [])
        titles = ", ".join(item.get("title", item.get("taskId", "unnamed")) for item in tasks)
        return "Todo items: " + (titles or "none recorded.")
    if tool_name == "read_commit_history":
        commits = result.get("commits", [])
        messages = ", ".join(item.get("message", item.get("sha", "unknown")) for item in commits)
        label = "All commits" if result.get("complete") else "Recent commits"
        if result.get("unavailableReason"):
            return f"Git history unavailable: {result['unavailableReason']}."
        return label + ": " + (messages or "none available.")
    if tool_name == "read_svn_history":
        if result.get("unavailableReason"):
            return f"SVN history unavailable: {result['unavailableReason']}."
        entries = result.get("entries", [])
        revisions = ", ".join(
            f"r{item.get('revision', 'unknown')}: {item.get('message', '').strip()}" for item in entries
        )
        label = "All SVN history" if result.get("complete") else "Recent SVN history"
        return label + ": " + (revisions or "none available.")
    return f"SVN state: base revision {result.get('baseRevision', 'unknown')}, current revision {result.get('currentRevision', 'unknown')}."


async def _summarize_async(
    state: AppAssistantState,
    model: Any | None,
    prompt_append: str,
) -> dict[str, Any]:
    fallback = _fallback_tool_answer(state)
    if model is None:
        return {"answer": fallback}
    prompt = build_command_prompt(
        runtime_snapshot=state["runtime_snapshot"],
        user_message="Summarize the observed tool result and give one practical next step.",
        messages=state.get("messages", []),
        detail=state.get("tool_result"),
        prompt_append=prompt_append,
    )
    try:
        answer = await _invoke_model_text(model, prompt, "Summarize the observed result without inventing facts.")
        return {"answer": answer or fallback}
    except Exception:
        return {"answer": fallback}


async def _propose_mutation(state: AppAssistantState, gateway: Gateway) -> dict[str, Any]:
    decision = AssistantDecision.model_validate(state.get("decision") or {})
    mutation = decision.mutation
    if mutation is None:
        return {"answer": "I need a complete worktree proposal before asking for approval."}
    revision = int(state.get("context_revision", _runtime(state).get("workbenchRevision", 0)))
    workbench_id = state["workbench_id"]
    proposal = {
        "kind": "create_worktree",
        "workbenchId": workbench_id,
        "name": mutation.name,
        "branch": mutation.branch,
        "startPoint": mutation.start_point,
        "expectedWorkbenchRevision": revision,
        "requestId": f"app-assistant:{workbench_id}:create:{revision}",
    }
    approval = interrupt(proposal)
    if not isinstance(approval, dict) or approval.get("decision") != "approve":
        return {"proposed_action": proposal, "answer": "The worktree creation proposal was cancelled."}
    try:
        result = await gateway.create_worktree(
            workbench_id,
            name=mutation.name,
            branch=mutation.branch,
            start_point=mutation.start_point,
            expected_revision=revision,
            request_id=proposal["requestId"],
        )
        return {
            "proposed_action": proposal,
            "detail": {"mutation": result},
            "answer": f"Created worktree '{mutation.name}' on branch '{mutation.branch}'. It remains unselected; you can select it from the workbench UI.",
        }
    except GatewayStaleError:
        try:
            context = _as_dict(await gateway.get_context(workbench_id))
            runtime = context.get("runtime", context)
            refreshed_revision = int(runtime.get("workbenchRevision", 0))
            return {
                "runtime_snapshot": context,
                "context_revision": refreshed_revision,
                "proposed_action": None,
                "answer": (
                    "The workbench changed before approval. I refreshed the context to "
                    f"runtime revision {refreshed_revision}; please review the state and request the action again."
                ),
            }
        except Exception as exception:
            return {"proposed_action": proposal, "answer": f"The workbench changed before approval and refresh failed: {exception}"}
    except Exception as exception:
        return {"proposed_action": proposal, "answer": f"Worktree creation was not completed: {exception}"}


def _route_request(state: AppAssistantState) -> str:
    return "orient_with_llm" if state.get("request_mode", AssistantRequestMode.COMMAND) == AssistantRequestMode.ORIENTATION else "decide_with_llm"


def _route_decision(state: AppAssistantState) -> str:
    kind = (state.get("decision") or {}).get("kind")
    return {
        "answer": "answer_decision",
        "clarification": "answer_decision",
        "read_tool": "execute_read_tool",
        "mutation_proposal": "propose_mutation",
    }.get(kind, "answer_decision")


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
    resolved_prompt_append = prompt_append if prompt_append is not None else os.getenv("APP_ASSISTANT_PROMPT_APPEND", "")
    builder = StateGraph(AppAssistantState)

    async def bootstrap_node(state: AppAssistantState) -> dict[str, Any]:
        return await _bootstrap_async(state, gateway, assistant_metadata)

    async def orientation_node(state: AppAssistantState) -> dict[str, Any]:
        return await _orient_async(state, resolved_model)

    async def decision_node(state: AppAssistantState) -> dict[str, Any]:
        return await _decide_async(state, resolved_model, resolved_prompt_append)

    async def read_node(state: AppAssistantState) -> dict[str, Any]:
        return await _read_detail(state, gateway)

    async def summary_node(state: AppAssistantState) -> dict[str, Any]:
        return await _summarize_async(state, resolved_model, resolved_prompt_append)

    async def answer_node(state: AppAssistantState) -> dict[str, Any]:
        return {"answer": _decision_answer(state)}

    async def mutation_node(state: AppAssistantState) -> dict[str, Any]:
        return await _propose_mutation(state, gateway)

    builder.add_node("bootstrap_context", bootstrap_node)
    builder.add_node("orient_with_llm", orientation_node)
    builder.add_node("decide_with_llm", decision_node)
    builder.add_node("answer_decision", answer_node)
    builder.add_node("execute_read_tool", read_node)
    builder.add_node("summarize_tool_result", summary_node)
    builder.add_node("propose_mutation", mutation_node)
    builder.add_edge(START, "bootstrap_context")
    builder.add_conditional_edges(
        "bootstrap_context",
        _route_request,
        {"orient_with_llm": "orient_with_llm", "decide_with_llm": "decide_with_llm"},
    )
    builder.add_edge("orient_with_llm", END)
    builder.add_conditional_edges(
        "decide_with_llm",
        _route_decision,
        {
            "answer_decision": "answer_decision",
            "execute_read_tool": "execute_read_tool",
            "propose_mutation": "propose_mutation",
        },
    )
    builder.add_edge("answer_decision", END)
    builder.add_edge("execute_read_tool", "summarize_tool_result")
    builder.add_edge("summarize_tool_result", END)
    builder.add_edge("propose_mutation", END)
    return builder.compile(checkpointer=checkpointer)


def build_environment_graph() -> Any:
    model, model_metadata = build_model_from_env()
    return build_graph(WorkbenchGateway.from_env(), model=model, model_metadata=model_metadata)


graph = build_environment_graph()
