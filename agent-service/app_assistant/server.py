from contextlib import asynccontextmanager
import os
from pathlib import Path
from typing import Any
from uuid import uuid4

from fastapi import FastAPI, Response
from langgraph.checkpoint.sqlite.aio import AsyncSqliteSaver
from langgraph.types import Command
from pydantic import BaseModel, ConfigDict, Field

from . import __version__
from .decisions import AssistantRequestMode
from .gateway import WorkbenchGateway
from .graph import build_graph, thread_id_for
from .model import build_model_from_env
from .observability import RunRecorder, build_run_metadata


class AssistantRequest(BaseModel):
    model_config = ConfigDict(populate_by_name=True)

    message: str = ""
    request_mode: AssistantRequestMode = Field(
        default=AssistantRequestMode.ORIENTATION,
        alias="requestMode",
    )
    approval: dict[str, Any] | None = None


class FeedbackRequest(BaseModel):
    model_config = ConfigDict(populate_by_name=True)

    category: str
    run_id: str | None = Field(default=None, alias="runId")


async def _setup_checkpointer(checkpointer: AsyncSqliteSaver) -> None:
    """Initialize checkpoints across the aiosqlite APIs supported by the pinned saver."""

    connection = checkpointer.conn
    if hasattr(connection, "is_alive"):
        await checkpointer.setup()
        return
    async with checkpointer.lock:
        if checkpointer.is_setup:
            return
        async with connection.executescript(
            """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS checkpoints (
                thread_id TEXT NOT NULL,
                checkpoint_ns TEXT NOT NULL DEFAULT '',
                checkpoint_id TEXT NOT NULL,
                parent_checkpoint_id TEXT,
                type TEXT,
                checkpoint BLOB,
                metadata BLOB,
                PRIMARY KEY (thread_id, checkpoint_ns, checkpoint_id)
            );
            CREATE TABLE IF NOT EXISTS writes (
                thread_id TEXT NOT NULL,
                checkpoint_ns TEXT NOT NULL DEFAULT '',
                checkpoint_id TEXT NOT NULL,
                task_id TEXT NOT NULL,
                idx INTEGER NOT NULL,
                channel TEXT NOT NULL,
                type TEXT,
                value BLOB,
                PRIMARY KEY (thread_id, checkpoint_ns, checkpoint_id, task_id, idx)
            );
            """
        ):
            await connection.commit()
        checkpointer.is_setup = True


def _response(result: dict[str, Any], workbench_id: str, run_id: str) -> dict[str, Any]:
    interrupts = result.get("__interrupt__") or []
    interrupt_values = [item.value if hasattr(item, "value") else item for item in interrupts]
    response = {
        "threadId": thread_id_for(workbench_id),
        "workbenchId": workbench_id,
        "contextRevision": result.get("context_revision", 0),
        "runtimeSnapshot": result.get("runtime_snapshot"),
        "intent": result.get("intent"),
        "decision": result.get("decision"),
        "proposedAction": result.get("proposed_action"),
        "assistantMetadata": result.get("assistant_metadata"),
        "answer": result.get("answer"),
        "interrupt": interrupt_values or None,
        "pendingApproval": interrupt_values[0] if interrupt_values else result.get("proposed_action"),
    }
    response["runMetadata"] = build_run_metadata(result, workbench_id=workbench_id, run_id=run_id)
    return response


@asynccontextmanager
async def lifespan(app: FastAPI):
    gateway = WorkbenchGateway.from_env()
    data_dir = Path(os.getenv("APP_ASSISTANT_DATA_DIR", ".assistant-data"))
    data_dir.mkdir(parents=True, exist_ok=True)
    checkpoint_path = data_dir / "checkpoints.sqlite"
    app.state.gateway = gateway
    app.state.recorder = RunRecorder(data_dir / "assistant-events.jsonl")
    model, model_metadata = build_model_from_env()
    app.state.model_metadata = model_metadata
    async with AsyncSqliteSaver.from_conn_string(str(checkpoint_path)) as checkpointer:
        await _setup_checkpointer(checkpointer)
        app.state.graph = build_graph(
            gateway,
            checkpointer=checkpointer,
            model=model,
            model_metadata=model_metadata,
        )
        yield
    await gateway.client.aclose()


app = FastAPI(title="Workbench App Assistant", version=__version__, lifespan=lifespan)


@app.get("/health")
async def health() -> dict[str, Any]:
    model_metadata = getattr(
        app.state,
        "model_metadata",
        {"model": "unknown", "mode": "unknown"},
    )
    return {
        "status": "ok",
        "graphVersion": __version__,
        "gateway": "configured",
        "modelConfigured": model_metadata.get("mode") == "llm",
        "modelMode": model_metadata.get("mode", "unknown"),
        "model": model_metadata.get("model", "unknown"),
    }


@app.post("/v1/workbenches/{workbench_id}/bootstrap")
async def bootstrap(workbench_id: str, request: AssistantRequest) -> dict[str, Any]:
    run_id = uuid4().hex
    input_value: Any = {
        "workbench_id": workbench_id,
        "request_mode": AssistantRequestMode.ORIENTATION,
    }
    if request.approval is not None:
        input_value = Command(resume=request.approval)
    result = await app.state.graph.ainvoke(
        input_value,
        config={"configurable": {"thread_id": thread_id_for(workbench_id)}},
    )
    response = _response(result, workbench_id, run_id)
    app.state.recorder.record_run(response["runMetadata"])
    return response


@app.post("/v1/workbenches/{workbench_id}/chat")
async def chat(workbench_id: str, request: AssistantRequest) -> dict[str, Any]:
    run_id = uuid4().hex
    input_value: Any = {
        "workbench_id": workbench_id,
        "request_mode": AssistantRequestMode.COMMAND,
        "messages": [{"role": "user", "content": request.message}],
    }
    if request.approval is not None:
        input_value = Command(resume=request.approval)
    result = await app.state.graph.ainvoke(
        input_value,
        config={"configurable": {"thread_id": thread_id_for(workbench_id)}},
    )
    response = _response(result, workbench_id, run_id)
    app.state.recorder.record_run(response["runMetadata"])
    return response


@app.post("/v1/workbenches/{workbench_id}/feedback", status_code=204)
async def feedback(workbench_id: str, request: FeedbackRequest) -> Response:
    app.state.recorder.record_feedback(workbench_id, request.run_id, request.category)
    return Response(status_code=204)
