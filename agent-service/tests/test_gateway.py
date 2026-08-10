import httpx
import pytest

from app_assistant.gateway import GatewayUnavailableError, WorkbenchGateway


def test_gateway_defaults_to_the_development_api_host(monkeypatch):
    monkeypatch.delenv("APP_ASSISTANT_APIHOST_URL", raising=False)

    gateway = WorkbenchGateway.from_env()

    assert gateway.base_url == "http://127.0.0.1:5239"


@pytest.mark.asyncio
async def test_gateway_validates_context_and_preserves_revision():
    transport = httpx.MockTransport(
        lambda request: httpx.Response(
            200,
            json={
                "workbenchId": "wb-1",
                "runtime": {"workbenchRevision": 7, "focus": {"worktreeId": "wt-1"}},
                "observedAt": "2026-08-09T00:00:00Z",
            },
        )
    )
    async with httpx.AsyncClient(transport=transport) as client:
        gateway = WorkbenchGateway("http://localhost:5000", client=client)
        context = await gateway.get_context("wb-1")

    assert context.workbench_id == "wb-1"
    assert context.runtime.workbench_revision == 7


@pytest.mark.asyncio
async def test_gateway_wraps_api_host_failure():
    transport = httpx.MockTransport(lambda request: httpx.Response(503, json={"error": "down"}))
    async with httpx.AsyncClient(transport=transport) as client:
        gateway = WorkbenchGateway("http://localhost:5000", client=client)
        with pytest.raises(GatewayUnavailableError):
            await gateway.get_context("wb-1")


@pytest.mark.asyncio
async def test_gateway_passes_history_depth_to_api_host():
    paths = []

    def handler(request):
        paths.append(str(request.url))
        if request.url.path.endswith("svn-history"):
            return httpx.Response(200, json={
                "workbenchId": "wb-1",
                "worktreeId": "wt-1",
                "sourceRevision": 7,
                "entries": [],
                "complete": True,
            })
        return httpx.Response(200, json={
            "workbenchId": "wb-1",
            "worktreeId": "wt-1",
            "sourceRevision": 7,
            "commits": [],
            "complete": True,
        })

    transport = httpx.MockTransport(handler)
    async with httpx.AsyncClient(transport=transport) as client:
        gateway = WorkbenchGateway("http://localhost:5000", client=client)
        await gateway.get_history("wb-1", "wt-1", "all")
        await gateway.get_svn_history("wb-1", "wt-1", 25)

    assert paths == [
        "http://localhost:5000/internal/app-assistant/workbenches/wb-1/worktrees/wt-1/history?depth=all",
        "http://localhost:5000/internal/app-assistant/workbenches/wb-1/worktrees/wt-1/svn-history?depth=25",
    ]
