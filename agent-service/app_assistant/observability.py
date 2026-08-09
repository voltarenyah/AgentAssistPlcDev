import json
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

from .graph import thread_id_for


FEEDBACK_CATEGORIES = {
    "wrong_worktree",
    "stale_status",
    "wrong_recommendation",
    "unavailable_action",
    "successful_completion",
}

_RUN_METADATA_KEYS = {
    "runId",
    "threadId",
    "workbenchId",
    "contextRevision",
    "intent",
    "actionId",
    "operationId",
    "outcome",
    "graphVersion",
    "promptVersion",
    "modelVersion",
}


def _operation_id(result: dict[str, Any]) -> str | None:
    runtime = result.get("runtime_snapshot", {}).get("runtime", result.get("runtime_snapshot", {}))
    operation = runtime.get("operation", {}) if isinstance(runtime, dict) else {}
    return operation.get("operationId") or operation.get("operation_id")


def build_run_metadata(result: dict[str, Any], *, workbench_id: str, run_id: str) -> dict[str, Any]:
    assistant_metadata = result.get("assistant_metadata") or {}
    interrupts = result.get("__interrupt__") or []
    proposed_action = result.get("proposed_action")
    if interrupts:
        outcome = "awaiting_approval"
    elif result.get("answer"):
        outcome = "completed"
    else:
        outcome = "no_answer"
    return {
        "runId": run_id,
        "threadId": thread_id_for(workbench_id),
        "workbenchId": workbench_id,
        "contextRevision": result.get("context_revision", 0),
        "intent": result.get("intent"),
        "actionId": proposed_action.get("kind") if isinstance(proposed_action, dict) else None,
        "operationId": _operation_id(result),
        "outcome": outcome,
        "graphVersion": assistant_metadata.get("graphVersion"),
        "promptVersion": assistant_metadata.get("promptVersion"),
        "modelVersion": assistant_metadata.get("model"),
    }


class RunRecorder:
    def __init__(self, path: Path):
        self.path = path

    @staticmethod
    def validate_feedback_category(category: str) -> str:
        if category not in FEEDBACK_CATEGORIES:
            raise ValueError(f"Unsupported assistant feedback category: {category}")
        return category

    def record_run(self, run_metadata: dict[str, Any]) -> None:
        safe_metadata = {
            key: run_metadata[key]
            for key in _RUN_METADATA_KEYS
            if key in run_metadata
        }
        self._append({"event": "assistant_run", "recordedAt": _now(), "runMetadata": safe_metadata})

    def record_feedback(self, workbench_id: str, run_id: str | None, category: str) -> None:
        self.validate_feedback_category(category)
        self._append({
            "event": "assistant_feedback",
            "recordedAt": _now(),
            "workbenchId": workbench_id,
            "runId": run_id,
            "category": category,
        })

    def _append(self, record: dict[str, Any]) -> None:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        with self.path.open("a", encoding="utf-8") as stream:
            stream.write(json.dumps(record, sort_keys=True) + "\n")


def _now() -> str:
    return datetime.now(timezone.utc).isoformat()
