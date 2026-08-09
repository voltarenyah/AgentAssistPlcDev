from typing import Any

from pydantic import BaseModel, ConfigDict, Field


class RuntimeFocus(BaseModel):
    model_config = ConfigDict(populate_by_name=True, extra="allow")

    worktree_id: str | None = Field(default=None, alias="worktreeId")
    device_id: str | None = Field(default=None, alias="deviceId")


class RuntimeSnapshot(BaseModel):
    model_config = ConfigDict(populate_by_name=True, extra="allow")

    workbench_revision: int = Field(alias="workbenchRevision")
    focus: RuntimeFocus = Field(default_factory=RuntimeFocus)
    worktrees: list[dict[str, Any]] = Field(default_factory=list)
    available_actions: list[dict[str, Any]] = Field(default_factory=list, alias="availableActions")
    observed_at: str | None = Field(default=None, alias="observedAt")


class WorkbenchContext(BaseModel):
    model_config = ConfigDict(populate_by_name=True, extra="allow")

    workbench_id: str = Field(alias="workbenchId")
    name: str = ""
    runtime: RuntimeSnapshot
    available_actions: list[dict[str, Any]] = Field(default_factory=list, alias="availableActions")
    observed_at: str | None = Field(default=None, alias="observedAt")


class WorktreeTodos(BaseModel):
    model_config = ConfigDict(populate_by_name=True, extra="allow")

    worktree_id: str = Field(alias="worktreeId")
    source_revision: int = Field(alias="sourceRevision")
    tasks: list[dict[str, Any]] = Field(default_factory=list)


class WorktreeHistory(BaseModel):
    model_config = ConfigDict(populate_by_name=True, extra="allow")

    worktree_id: str = Field(alias="worktreeId")
    source_revision: int = Field(alias="sourceRevision")
    commits: list[dict[str, Any]] = Field(default_factory=list)


class WorktreeSvn(BaseModel):
    model_config = ConfigDict(populate_by_name=True, extra="allow")

    worktree_id: str = Field(alias="worktreeId")
    source_revision: int = Field(alias="sourceRevision")
    base_revision: int | None = Field(default=None, alias="baseRevision")
    current_revision: int | None = Field(default=None, alias="currentRevision")

