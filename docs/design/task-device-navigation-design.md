# Task-device navigation design

## Overview

- Outcome: A worktree task has one persisted device binding and becomes the normal Studio selection entry point.
- Scope: engineering graph task persistence and validation, task API, Studio navigator and task views.
- UI Spec: `docs/ui-spec/task-device-navigation-ui-spec.md`

## Requirement Boundary

- Current requirements: a new worktree task requires a device; selecting it opens the task without loading a device snapshot; starting its chat resolves that device from the task; sessions and source-object links cannot cross the task's device.
- Non-goals: changing the device of an existing task; converting a Git commit into a single-device record; deleting historical unbound tasks.

## Acceptance Criteria

- **AC-001** — When a worktree task is created, the system shall require a device registered to that worktree and persist its device ID.
- **AC-002** — When a device-bound task is selected, Studio shall open its task detail in the worktree without loading a device snapshot. Starting a chat on that task shall use its bound device without requiring a separate device selection.
- **AC-003** — If a task is linked to a session or source object from another device, then the API shall reject the link.
- **AC-004** — When a worktree is expanded, the left navigator shall show its tasks, or Add task when none exist, instead of a device subtree.

## Existing Evidence

| Evidence | Location | Design effect |
| --- | --- | --- |
| Tasks and graph entities are already persisted separately. | `EngineeringGraphService`, `EngineeringGraphSchema` | Add a nullable `device_id` to tasks and mirror it on the task graph entity. |
| Sessions and source objects already have `device_id`. | `SessionGraphOperations`, `EngineeringGraphEvidenceIndexer` | Enforce matching IDs at task relationship boundaries. |
| Studio loads graph worktree tasks and task detail. | `WorktreeLandingPage`, `TaskDetail` | Reuse those API contracts for navigator rows and task selection. |

## Design

### Selected Design

`GraphTask.DeviceId` is nullable for legacy and project-scoped tasks, but required for all new worktree tasks. The API checks the selected device belongs to the worktree before creating the graph task. A schema migration adds the task column without rewriting existing records.

Tasks retain worktree scope. A device-bound task may link only to target graph entities from its same device when the target has a device ID. Git commits remain worktree-level records because one commit can change multiple PLCs; a commit can therefore link to several device-bound tasks.

Studio receives `deviceId` in every engineering task response. The navigator loads a worktree's graph tasks lazily, displays only bound worktree tasks, and opens task detail without loading the device page. Starting chat on the task resolves its device from the persisted task binding and keeps the worktree page open.

### Change Surface

| Responsibility | Change | Unaffected boundary to preserve |
| --- | --- | --- |
| Engineering graph | Task device persistence and same-device relationship validation. | Existing legacy/unbound tasks remain readable. |
| API | Create and list device-bound worktree tasks. | Project task routes and worktree device validation. |
| Studio | Task-first navigator and selection. | Device workspace's existing source/chat behavior. |

## Verification Strategy

| Claim | Level | Proof |
| --- | --- | --- |
| Task device is persisted and cross-device session/source links fail. | .NET API and graph tests | Focused `Agent.Tests` and `ApiHost.Tests`. |
| Navigator offers task creation and task selection. | Vitest | Navigator/task component tests, then Studio build. |
