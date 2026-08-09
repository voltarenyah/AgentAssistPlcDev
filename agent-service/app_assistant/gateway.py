import os
from typing import Any

import httpx

from .contracts import WorkbenchContext, WorktreeHistory, WorktreeSvn, WorktreeTodos


class GatewayUnavailableError(RuntimeError):
    pass


class WorkbenchGateway:
    def __init__(self, base_url: str, client: httpx.AsyncClient | None = None):
        self.base_url = base_url.rstrip("/")
        self.client = client or httpx.AsyncClient(timeout=30.0)

    @classmethod
    def from_env(cls) -> "WorkbenchGateway":
        return cls(os.getenv("APP_ASSISTANT_APIHOST_URL", "http://127.0.0.1:5000"))

    async def get_context(self, workbench_id: str) -> WorkbenchContext:
        response = await self._get(f"/internal/app-assistant/workbenches/{workbench_id}/context")
        return WorkbenchContext.model_validate(response)

    async def get_todos(self, workbench_id: str, worktree_id: str) -> WorktreeTodos:
        response = await self._get(
            f"/internal/app-assistant/workbenches/{workbench_id}/worktrees/{worktree_id}/todos"
        )
        return WorktreeTodos.model_validate(response)

    async def get_history(self, workbench_id: str, worktree_id: str) -> WorktreeHistory:
        response = await self._get(
            f"/internal/app-assistant/workbenches/{workbench_id}/worktrees/{worktree_id}/history"
        )
        return WorktreeHistory.model_validate(response)

    async def get_svn(self, workbench_id: str, worktree_id: str) -> WorktreeSvn:
        response = await self._get(
            f"/internal/app-assistant/workbenches/{workbench_id}/worktrees/{worktree_id}/svn"
        )
        return WorktreeSvn.model_validate(response)

    async def _get(self, path: str) -> dict[str, Any]:
        try:
            response = await self.client.get(f"{self.base_url}{path}")
            response.raise_for_status()
            payload = response.json()
            if not isinstance(payload, dict):
                raise GatewayUnavailableError("ApiHost returned a non-object gateway response.")
            return payload
        except GatewayUnavailableError:
            raise
        except (httpx.HTTPError, ValueError) as exc:
            raise GatewayUnavailableError(f"ApiHost gateway unavailable: {exc}") from exc
