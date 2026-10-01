# Studio information architecture UI Specification

## Overview

- Outcome: the left navigator presents the current scope cascade as flat, independently collapsible
  sections — `PROJECTS`, `WORKTREE`, an unbound-task group when present, `DEVICE`, `TASKS` — instead of
  one global project tree, and each section carries its own creation action in its header.
- Scope: the left navigator's sections, their appearance conditions, header actions, collapse and
  scroll behaviour, and their interaction with the existing tag filter.
- PRD or requirement carrier: `docs/adr/ADR-0006-studio-navigator-ownership-and-shape.md`
- Explicit exclusions: main-area view or tab-strip ownership; relocating row action menus; automatic
  cross-section collapse; the contents of any feature page.

## Design Evidence

| Source | Path / identifier | Decision supplied |
|---|---|---|
| Existing UI | `studio/src/studio/workbench/WorkbenchNavigator.tsx:114-116` (`showLegacyDeviceTree = false`) | The device subtree was already removed from the tree; devices return as a flat section, not a subtree. |
| Existing UI | `studio/src/studio/workbench/WorkbenchNavigator.tsx:58-102` (24 callbacks, 10 data props) | The navigator's contract shrinks to a worktree-scoped surface. |
| Existing UI | `studio/src/studio/workbench/WorktreeLandingPage.tsx` worktree tab strip | Confirms scope selection already exists outside the navigator; the navigator must not duplicate it. |
| Existing API | `GET …/worktrees/{wt}/engineering-tasks` returns `deviceId` on every task | The `DEVICE → TASKS` level needs no new data or fetch. |
| Preserved behaviour | `filterControl` plus server-owned `filteredResults` | The tag filter is catalog-level and already server-owned; only its visible sections change. |

## UI Surface and Flow

| View or state | Entry / trigger | User-visible result | Governing requirement / AC |
|---|---|---|---|
| Fixed header | Always | `Automation Workbench` title, landing-page button, refresh button stay above the scrolling sections. | ADR-0006 |
| Tag filter | Always | The existing filter control stays directly below the header and keeps its current behaviour. | ADR-0006 |
| `PROJECTS` section | Always visible | Lists every workbench; its header carries a create-workbench action. | AC-001 |
| `WORKTREE` section | A workbench is selected | Lists that workbench's worktrees; its header carries a create-worktree action. | AC-002 |
| Unbound-task group | The selected worktree has at least one task with no `deviceId` | Lists those tasks under the `WORKTREE` section; carries no creation action. | AC-003 |
| `DEVICE` section | A worktree is selected | Lists that worktree's PLC devices plus one `Hardware` row for the worktree's hardware target; its header carries a refresh action only. | AC-004 |
| `TASKS` section | A PLC device or the `Hardware` row is selected | Lists the tasks bound to that device, or the worktree's hardware tasks; its header creates a task already bound to that target. | AC-005 |
| Tag filter active | The user types in the tag filter | Only `PROJECTS` and `WORKTREE` are shown; the unbound-task group, `DEVICE` and `TASKS` are hidden. | AC-006 |
| Section collapse | The user activates a section header | That section alone collapses or expands; the others keep their state. | AC-007 |
| Row 3-dots menu | Any section row | Shows that object's operations (project, worktree, device, task) without navigating to it first. | AC-008 |
| Device row menu | A `DEVICE` row's 3-dots | Offers the device operations the removed subtree used to hold. | AC-008 |
| `Hardware` row | Selecting it in the `DEVICE` section | Opens the worktree's hardware configuration page and reveals its own `TASKS` list. | AC-009 |
| Worktree `Tasks` tab | A worktree is selected | Unchanged: the worktree tab strip keeps its Tasks view for judging and acting on many items at once. | Preserved behaviour |

## Components and Interactions

| Component responsibility | Reuse / extend / new | Inputs or state | Interaction and response | Governing source |
|---|---|---|---|---|
| Section shell (header, collapse, scroll region) | New, in `WorkbenchNavigator` | section id, title, header action, collapsed state | Header toggles collapse; region scrolls independently with a sticky header. | AC-007 |
| `PROJECTS` list | Extend existing workbench rows | `workbenches`, selected workbench | Selecting a workbench sets the cascade level and clears deeper selections. | AC-001 |
| `WORKTREE` list | Extend existing worktree rows | worktrees of the selected workbench | Selecting a worktree sets the cascade level and clears deeper selections. | AC-002 |
| Unbound-task list | New group inside `WORKTREE` | selected worktree's tasks with `deviceId == null` | Selecting a task opens its detail; the group is absent when no such task exists. | AC-003 |
| `DEVICE` list | Restored as a flat list | devices of the selected worktree plus one hardware row | Selecting a PLC device or the hardware row sets the cascade level and reveals that target's tasks. | AC-004, AC-009 |
| `TASKS` list | Extend existing task rows | selected device's tasks | Selecting a task opens its detail; creation pre-binds the selected device. | AC-005 |
| Project / worktree / task row menus | Preserved unchanged | existing per-row actions | Existing menus keep their current behaviour and placement. | Preserved behaviour |
| Device row menu | Restored | selected device, existing device callbacks | The device operations the removed subtree held become reachable again from the `DEVICE` section row menu. | AC-008 |
| Hardware row | Restored | selected worktree | Adds the hardware target the navigator lost: it opens the shell's hardware pages and lists that target's tasks. | AC-009 |
| Hardware row 3-dots | Restored | existing hardware callbacks | Carries the select / reload / compare hardware operations the removed subtree held. | AC-009 |
| Worktree `Tasks` view | Preserved unchanged | existing worktree tab strip | The navigator is a quick-selection aid; it does not replace the main view, which shows fields the navigator does not. | Preserved behaviour |

### State / Display Detail (When Applicable)

| Component | State or condition | Display | Recovery / transition | Source |
|---|---|---|---|---|
| `WORKTREE` section | No worktree selected | Section absent; `PROJECTS` is the only list. | Selecting a workbench reveals it. | AC-002 |
| `TASKS` section | No device selected | Section absent; `DEVICE` prompts selection. | Selecting a device reveals the bound tasks. | AC-005 |
| `TASKS` section | Selected device has no task | Empty list with the existing `Add task` affordance. | Creating a task populates the list. | AC-005 |
| `DEVICE` section | Selected worktree has no PLC device | Only the `Hardware` row is listed; the refresh action remains available. | Refresh re-reads the device list. | AC-004 |
| `TASKS` section | The `Hardware` row is selected and the worktree has no hardware task | Empty list with the existing `Add task` affordance. | Creating a task populates the list. | AC-009 |
| Any reachable section | Section content fails to load | Existing error text for that collection; other sections keep rendering. | Retry through the existing refresh actions. | Preserved behaviour |

## Visual Constraints (When Applicable)

| Element / view | Constraint | Repository or approved design source | Acceptance observation |
|---|---|---|---|
| Section headers | Reuse the existing dense sidebar heading treatment and tokens from `studio/src/assets/main.css`. | `docs/STYLEGUIDE.md` | Headers are visually consistent with the current `PROJECTS` heading. |
| Section scroll regions | Each section gets its own bounded scroll region with a sticky header; the column itself does not scroll its headers away. | ADR-0006 | With all sections populated, every header stays visible while a single section scrolls. |
| Active section | The section containing the current scope keeps at least enough height to show its rows. | ADR-0006 | The selected row is visible without manual scrolling after a selection. |

## Accessibility Requirements (When Applicable)

| Component / interaction | Keyboard, semantic, announcement, or contrast behavior | Source | Acceptance observation |
|---|---|---|---|
| Section headers | Native buttons exposing `aria-expanded` and `aria-controls`. | Preserved repository rule (native buttons with accessible labels) | Keyboard users can collapse and expand each section. |
| Section rows | Native buttons with accessible names; the current scope is marked with `aria-current`. | `WorkbenchNavigator.tsx` existing row pattern | Keyboard and screen-reader users can identify the selected item per section. |
| Header actions | Each action keeps a distinct accessible name that names its collection. | Existing `Create workbench` / `Refresh workbenches` pattern | Two header actions are distinguishable by name alone. |

## Acceptance Traceability

| AC / requirement | View, component, or interaction | Observable UI proof |
|---|---|---|
| AC-001 | `PROJECTS` section | Every workbench is listed, and its header action opens workbench creation. |
| AC-002 | `WORKTREE` section | Selecting a workbench reveals its worktrees and nothing deeper. |
| AC-003 | Unbound-task group | A worktree with a device-less task shows that task under `WORKTREE`; a worktree without one shows no such group. |
| AC-004 | `DEVICE` section | Selecting a worktree reveals its devices, and the header exposes only refresh. |
| AC-005 | `TASKS` section | Selecting a device reveals its bound tasks, and its header action opens creation with that device preselected. |
| AC-006 | Tag filter | While filtering, only `PROJECTS` and `WORKTREE` are present in the navigator. |
| AC-007 | Section collapse | Collapsing one section leaves the others' expanded state and scroll position unchanged. |
| AC-008 | Row 3-dots menus | Every section row exposes a 3-dots menu with that object's operations, and the device menu offers the operations the removed subtree held. |
| AC-009 | `Hardware` row in `DEVICE` | The hardware row opens the hardware configuration page (unreachable today), and its `TASKS` list shows only hardware-bound tasks. |
| AC-010 | Task creation from the `Hardware` row | The created task appears under the `Hardware` row's `TASKS` list, not in the unbound-task group. |
| AC-011 | Task creation without a resolvable target | Creation stays blocked and no task appears in any section. |
| Preserved behaviour | Worktree `Tasks` tab | The main-area Tasks view still renders in the worktree tab strip and is unchanged. |

## Open User Decisions

| Decision | Effect on current UI |
|---|---|
| Automatic collapse of shallower sections, keeping one level above the active scope | Confirmed as desirable but deferred; sections must already support independent collapse and a bounded scroll region so it can be added without relayout. |

## Update History

| Date | Version | Changes |
|---|---|---|
| 2026-09-30 | 1.0 | Initial specification |
