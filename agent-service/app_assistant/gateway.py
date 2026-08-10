import os
from typing import Any

import httpx

from .contracts import WorkbenchContext, WorktreeHistory, WorktreeSvn, WorktreeTodos


class GatewayUnavailableError(RuntimeError):
    pass


class GatewayStaleError(GatewayUnavailableError):
    pass


class WorkbenchGateway:
    def __init__(self, base_url: str, client: httpx.AsyncClient | None = None):
        self.base_url = base_url.rstrip("/")
        self.client = client or httpx.AsyncClient(timeout=30.0)

    @classmethod
    def from_env(cls) -> "WorkbenchGateway":
        return cls(os.getenv("APP_ASSISTANT_APIHOST_URL", "http://127.0.0.1:5239"))

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

    async def create_worktree(
        self,
        workbench_id: str,
        *,
        name: str,
        branch: str,
        start_point: str | None,
        expected_revision: int,
        request_id: str,
    ) -> dict[str, Any]:
        path = f"/internal/app-assistant/workbenches/{workbench_id}/mutations/create-worktree"
        payload = {
            "workbenchId": workbench_id,
            "name": name,
            "branch": branch,
            "startPoint": start_point,
            "expectedWorkbenchRevision": expected_revision,
            "requestId": request_id,
        }
        try:
            response = await self.client.post(
                f"{self.base_url}{path}",
                json=payload,
                headers=self._headers(),
            )
            if response.status_code == 409:
                raise GatewayStaleError("ApiHost runtime context is stale.")
            response.raise_for_status()
            body = response.json()
            if not isinstance(body, dict):
                raise GatewayUnavailableError("ApiHost returned a non-object mutation response.")
            return body
        except GatewayUnavailableError:
            raise
        except (httpx.HTTPError, ValueError) as exc:
            raise GatewayUnavailableError(f"ApiHost mutation unavailable: {exc}") from exc

    async def _get(self, path: str) -> dict[str, Any]:
        try:
            response = await self.client.get(f"{self.base_url}{path}", headers=self._headers())
            response.raise_for_status()
            payload = response.json()
            if not isinstance(payload, dict):
                raise GatewayUnavailableError("ApiHost returned a non-object gateway response.")
            return payload
        except GatewayUnavailableError:
            raise
        except (httpx.HTTPError, ValueError) as exc:
            raise GatewayUnavailableError(f"ApiHost gateway unavailable: {exc}") from exc

    def _headers(self) -> dict[str, str]:
        token = os.getenv("APP_ASSISTANT_INTERNAL_TOKEN")
        return {"X-App-Assistant-Token": token} if token else {}
