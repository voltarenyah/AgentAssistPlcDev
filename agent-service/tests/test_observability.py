import json

import pytest

from app_assistant.observability import (
    FEEDBACK_CATEGORIES,
    RunRecorder,
    build_run_metadata,
)


def test_run_metadata_contains_trace_fields_without_user_content():
    metadata = build_run_metadata(
        {
            "intent": "todos",
            "context_revision": 12,
            "assistant_metadata": {
                "provider": "deepseek",
                "model": "deepseek-v4-flash",
                "graphVersion": "0.1.0",
                "promptVersion": "workbench-assistant-v1",
            },
            "runtime_snapshot": {
                "runtime": {"operation": {"operationId": "op-7"}},
            },
            "answer": "The secret user prompt must not be recorded.",
        },
        workbench_id="wb-1",
        run_id="run-1",
    )

    assert metadata == {
        "runId": "run-1",
        "threadId": "app-assistant:wb-1",
        "workbenchId": "wb-1",
        "contextRevision": 12,
        "intent": "todos",
        "actionId": None,
        "operationId": "op-7",
        "outcome": "completed",
        "graphVersion": "0.1.0",
        "promptVersion": "workbench-assistant-v1",
        "modelVersion": "deepseek-v4-flash",
    }
    assert "secret user prompt" not in json.dumps(metadata)


def test_recorder_writes_redacted_runs_and_feedback(tmp_path):
    recorder = RunRecorder(tmp_path / "assistant-events.jsonl")
    run = {"runId": "run-1", "workbenchId": "wb-1", "answer": "do not store"}

    recorder.record_run(run)
    recorder.record_feedback("wb-1", "run-1", "successful_completion")

    records = [json.loads(line) for line in (tmp_path / "assistant-events.jsonl").read_text().splitlines()]
    assert [record["event"] for record in records] == ["assistant_run", "assistant_feedback"]
    assert records[0]["runMetadata"] == {"runId": "run-1", "workbenchId": "wb-1"}
    assert records[1]["category"] == "successful_completion"
    assert "do not store" not in json.dumps(records)


def test_feedback_categories_are_explicit():
    assert FEEDBACK_CATEGORIES == {
        "wrong_worktree",
        "stale_status",
        "wrong_recommendation",
        "unavailable_action",
        "successful_completion",
    }

    with pytest.raises(ValueError):
        RunRecorder.validate_feedback_category("free-form-comment")
