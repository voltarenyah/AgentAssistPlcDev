from typing import Any, Literal

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


HistoryDepth = Literal["recent", "all"] | int


class WorktreeSvnHistoryEntry(BaseModel):
    model_config = ConfigDict(populate_by_name=True, extra="allow")

    revision: int
    message: str = ""
    author: str = ""
    timestamp: str | None = None


class WorktreeSvnHistory(BaseModel):
    model_config = ConfigDict(populate_by_name=True, extra="allow")

    workbench_id: str = Field(alias="workbenchId")
    worktree_id: str = Field(alias="worktreeId")
    source_revision: int = Field(alias="sourceRevision")
    entries: list[WorktreeSvnHistoryEntry] = Field(default_factory=list)
    complete: bool = False
    unavailable_reason: str | None = Field(default=None, alias="unavailableReason")


class HistoryEvidence(BaseModel):
    model_config = ConfigDict(populate_by_name=True, extra="allow")

    worktree_id: str | None = Field(default=None, alias="worktreeId")
    git: WorktreeHistory | None = None
    svn: WorktreeSvnHistory | None = None
    unavailable_reason: str | None = Field(default=None, alias="unavailableReason")


class WorktreeSvn(BaseModel):
    model_config = ConfigDict(populate_by_name=True, extra="allow")

    worktree_id: str = Field(alias="worktreeId")
    source_revision: int = Field(alias="sourceRevision")
    base_revision: int | None = Field(default=None, alias="baseRevision")
    current_revision: int | None = Field(default=None, alias="currentRevision")

