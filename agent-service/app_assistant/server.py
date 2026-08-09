from contextlib import asynccontextmanager
import os
from pathlib import Path
from typing import Any

from fastapi import FastAPI
from langgraph.checkpoint.sqlite.aio import AsyncSqliteSaver
from langgraph.types import Command
from pydantic import BaseModel

from . import __version__
from .gateway import WorkbenchGateway
from .graph import build_graph, thread_id_for
from .model import build_model_from_env


class AssistantRequest(BaseModel):
    message: str = "Inspect the current workbench and suggest the first useful move."
    approval: dict[str, Any] | None = None


def _response(result: dict[str, Any], workbench_id: str) -> dict[str, Any]:
    interrupts = result.get("__interrupt__") or []
    interrupt_values = [item.value if hasattr(item, "value") else item for item in interrupts]
    return {
        "threadId": thread_id_for(workbench_id),
        "workbenchId": workbench_id,
        "contextRevision": result.get("context_revision", 0),
        "runtimeSnapshot": result.get("runtime_snapshot"),
        "intent": result.get("intent"),
        "proposedAction": result.get("proposed_action"),
        "assistantMetadata": result.get("assistant_metadata"),
        "answer": result.get("answer"),
        "interrupt": interrupt_values or None,
        "pendingApproval": interrupt_values[0] if interrupt_values else result.get("proposed_action"),
    }


@asynccontextmanager
async def lifespan(app: FastAPI):
    gateway = WorkbenchGateway.from_env()
    data_dir = Path(os.getenv("APP_ASSISTANT_DATA_DIR", ".assistant-data"))
    data_dir.mkdir(parents=True, exist_ok=True)
    checkpoint_path = data_dir / "checkpoints.sqlite"
    app.state.gateway = gateway
    model, model_metadata = build_model_from_env()
    async with AsyncSqliteSaver.from_conn_string(str(checkpoint_path)) as checkpointer:
        await checkpointer.setup()
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
async def health() -> dict[str, str]:
    return {
        "status": "ok",
        "graphVersion": __version__,
        "gateway": "configured",
    }


@app.post("/v1/workbenches/{workbench_id}/bootstrap")
async def bootstrap(workbench_id: str, request: AssistantRequest) -> dict[str, Any]:
    input_value: Any = {
        "workbench_id": workbench_id,
        "messages": [{"role": "user", "content": request.message}],
    }
    if request.approval is not None:
        input_value = Command(resume=request.approval)
    result = await app.state.graph.ainvoke(
        input_value,
        config={"configurable": {"thread_id": thread_id_for(workbench_id)}},
    )
    return _response(result, workbench_id)


@app.post("/v1/workbenches/{workbench_id}/chat")
async def chat(workbench_id: str, request: AssistantRequest) -> dict[str, Any]:
    return await bootstrap(workbench_id, request)
