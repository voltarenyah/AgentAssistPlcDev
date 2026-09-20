# Task-device navigation UI specification

## Overview

- Outcome: Engineers select a device-bound worktree task from the left project tree; the selected task opens its traceability and device workspace.
- Scope: left navigator, task creation, task detail, and task selection in Studio.
- Explicit exclusions: project-scoped tasks and changing a task's device are not presented in this navigation.

## UI Surface and Flow

| View or state | Entry / trigger | User-visible result |
| --- | --- | --- |
| Expanded worktree | Click the worktree row | The row contains its worktree tasks instead of hardware and PLC-device rows. |
| Empty worktree task list | The loaded worktree has no device-bound tasks | An `Add task` button is visible below the worktree. |
| Add task | Click `Add task` | A dialog requires a title and a device selected from that worktree. |
| Task selection | Click a task row | Studio selects the task's device, makes the task active, and displays the task's sessions, commits, source objects, and SVN revisions. |

## Components and Interactions

| Component responsibility | Reuse / extend / new | Inputs or state | Interaction and response |
| --- | --- | --- | --- |
| `WorkbenchNavigator` tree | Extend | worktree tasks and their loading state | Shows a compact task child list and task-context menu; does not show device children. |
| Task creation dialog | Extend existing `Dialog` | title, type, device list | Cannot submit without a device. |
| `TaskDetail` | Extend | selected engineering task and task detail | Shows the bound PLC name and preserves category navigation. |

## Visual Constraints

| Element | Constraint | Acceptance observation |
| --- | --- | --- |
| Navigator task rows | Reuse the existing dense tree row, `ListTodo` icon, sidebar tokens, and active-row treatment. | Task rows fit the existing left dock and an active task is distinguishable from its worktree. |
| Empty state | Use the existing compact `Button` pattern. | `Add task` is visible without opening the main worktree page. |

## Accessibility Requirements

| Component / interaction | Requirement | Acceptance observation |
| --- | --- | --- |
| Task rows | Native buttons with accessible task labels. | Keyboard users can select a task. |
| Device picker | Labelled required select control. | Submit remains disabled until a PLC is selected. |

## Acceptance Traceability

| Requirement | Observable UI proof |
| --- | --- |
| Each worktree shows its tasks in the left dock. | Expanding a worktree lists its tasks or an Add task action. |
| A task chooses one PLC. | Creating a task requires selecting an available worktree device. |
| Selecting a task selects its PLC context. | The task opens with the device workspace and its traceability. |

