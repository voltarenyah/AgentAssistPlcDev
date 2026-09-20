# Task-scoped TIA Compare UI Specification

## Overview

- Outcome: users compare and commit only the source objects staged by the active task, while retaining an explicit full-project scan for reconciliation.
- Scope: task creation/detail, Version Control Changes, full-scan assignment prompts, and native-savepoint blocking states.
- Explicit exclusions: automatic assignment of discovered changes and project-clean claims from a task scan.

## UI Surface and Flow

| View or state | Entry / trigger | User-visible result |
|---|---|---|
| Create task | User creates a worktree task | Device selection is required; source objects are selected from registered objects, not typed as names. |
| Task stage | User opens a task | Shows staged objects and their current stage status; removing an item releases active ownership only. |
| Compare task with TIA | Active task has staged objects | Shows only task differences and labels a clean result `This task is in sync`. |
| Full scan | User selects full project scan | Shows every changed unassigned source object and lets the user assign each to an eligible task. |
| Create SVN savepoint | User requests a native savepoint | Shows the unresolved source objects and directs the user to full scan/assignment instead of creating a savepoint. |

## Components and Interactions

| Component responsibility | Reuse / extend | Interaction and response |
|---|---|---|
| Task device and stage picker | Extend `WorktreeTasksPanel` and existing Select/Dialog primitives | Requires one registered device and registered source object IDs. |
| Compare mode selector | Extend `VersionControlPanel` | Distinguishes `Compare task` from `Full scan`; full scan remains the only project-wide result. |
| Task comparison result | Extend `VersionControlCompare` | Shows stage problems, selected differences, and task-only wording. |
| Unassigned discovery | Extend existing differences tree | Assignment is explicit and unavailable for objects owned by another active task. |

## Acceptance Traceability

| Requirement | Observable UI proof |
|---|---|
| Task clean is not project clean | A successful scoped result says `This task is in sync` and has no project-clean label. |
| Missing work is assignable | A full scan exposes changed unassigned objects and an explicit task selector. |
| Savepoint is protected | A blocked savepoint explains that a full scan and resolution are required. |
