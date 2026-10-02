# Task-device navigation UI specification

## Overview

- Outcome: Engineers select a worktree task from the left navigator's scope cascade, and the selected task opens its traceability and device workspace.
- Scope: left navigator, task creation, task detail, and task selection in Studio.
- Explicit exclusions: project-scoped tasks and changing a task's target are not presented in this navigation.

> **Superseded in part.** Two later decisions replace the navigator shape this specification assumes.
> The criteria they replace are revised in place below; every other line still describes shipped
> behaviour.
>
> - [ADR-0006](../adr/ADR-0006-studio-navigator-ownership-and-shape.md) makes Studio navigation a
>   scope cascade, so the device subtree returns as a flat `DEVICE` section and a worktree's tasks
>   move out of the worktree row into a `TASKS` section keyed by the selected target.
> - [ADR-0007](../adr/ADR-0007-task-target-model.md) gives a worktree task an explicit target kind,
>   so a task may target the worktree's hardware configuration instead of a PLC device, and only a
>   device-targeted task requires one.
>
> Target surface: [studio-information-architecture-ui-spec.md](studio-information-architecture-ui-spec.md).

## UI Surface and Flow

| View or state | Entry / trigger | User-visible result |
| --- | --- | --- |
| Expanded worktree | Click the worktree row | The row reveals only the worktree's tasks that resolve to no target, and only while it holds one; its PLC devices and hardware target are rows of `DEVICE`. |
| Empty task list | The loaded worktree has no task bound to the selected target | A `TASKS` section is present and an `Add task` affordance is visible in it. |
| Add task | Click `Add task` | A dialog requires a title and a target: one of that worktree's devices, or its hardware configuration. |
| Task selection | Click a task row | Studio selects the task's target, makes the task active, and displays the task's sessions, commits, source objects, and SVN revisions. |

## Components and Interactions

| Component responsibility | Reuse / extend / new | Inputs or state | Interaction and response |
| --- | --- | --- | --- |
| `WorkbenchNavigator` sections | Extend | selected workbench, worktree, and target; worktree tasks and their loading state | Lists a worktree's untargeted tasks in `WORKTREE`, its PLC devices and hardware target as `DEVICE` rows, and the selected target's tasks in `TASKS`, each with its own row menu. |
| Task creation dialog | Extend existing `Dialog` | title, type, target list | Cannot submit without a target; the hardware target is complete without a PLC device. |
| `TaskDetail` | Extend | selected engineering task and task detail | Shows the selected target — the bound PLC name, or the worktree's hardware configuration — and preserves category navigation. |

## Visual Constraints

| Element | Constraint | Acceptance observation |
| --- | --- | --- |
| Navigator task rows | Reuse the existing dense row treatment, `ListTodo` icon, sidebar tokens, and active-row treatment. | Task rows fit the existing left dock and an active task is distinguishable from the worktree it belongs to. |
| Empty state | Use the existing compact `Button` pattern. | `Add task` is visible without opening the main worktree page. |

## Accessibility Requirements

| Component / interaction | Requirement | Acceptance observation |
| --- | --- | --- |
| Task rows | Native buttons with accessible task labels. | Keyboard users can select a task. |
| Target picker | Labelled required select control. | Submit remains disabled until a PLC device or the hardware target is selected. |

## Acceptance Traceability

| Requirement | Observable UI proof |
| --- | --- |
| Each worktree shows its tasks in the left dock. | `WORKTREE` lists the worktree's untargeted tasks and `TASKS` lists the selected target's tasks. |
| A task chooses one target. | Creating a task requires selecting a device or the worktree's hardware configuration. |
| Selecting a task selects its target context. | The task opens with its target workspace and its traceability. |
