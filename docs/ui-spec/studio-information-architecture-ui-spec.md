# Studio information architecture UI Specification

## Overview

- Outcome: the left navigator presents the current scope cascade as flat, independently collapsible
  sections — `PROJECTS`, `WORKTREE`, an unbound-task group when present, `DEVICE`, `TASKS`, `SESSIONS` —
  instead of one global project tree, and each section carries its own creation action in its header.
- Scope: the left navigator's sections, their appearance conditions, header actions, collapse and
  scroll behaviour, how their vertical space is allocated and overridden, and their interaction with
  the existing tag filter.
- PRD or requirement carrier: `docs/adr/ADR-0006-studio-navigator-ownership-and-shape.md`,
  `docs/adr/ADR-0009-navigator-sessions-section.md`, `docs/adr/ADR-0010-deleting-a-task-conversation.md`
- Explicit exclusions: main-area view or tab-strip ownership; relocating row action menus; automatic
  cross-section collapse; reordering sections; persisting a dragged height across sessions;
  showing more than one task's conversations at a time; a session-level scope below a task;
  recovering a deleted conversation; the contents of any feature page, other than the conversation
  creation a task's own page carries for the case the `SESSIONS` section cannot cover.

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
| Scope selection | The user selects a device, the `Hardware` row, a worktree or a project | That scope becomes the selection and no task is selected in it, so an open task detail closes and the main area shows the scope the user picked. `SESSIONS` returns to the selected device's conversations that no task owns. | AC-015 |
| `SESSIONS` section | A task is selected and it has at least one conversation | Lists that task's conversations under a heading naming the task; its header starts a conversation bound to that task. | AC-015, AC-016 |
| `SESSIONS` section | No task is selected, and the selected device has at least one task-less conversation | Lists the selected device's conversations that no task owns, under a heading stating that they belong to no task. | AC-015 |
| `SESSIONS` section | The selected target is the worktree's hardware, or the list the rule above yields is empty | Section absent, so the navigator does not reserve a row for a list that cannot exist. A conversation needs a device, so the hardware target cannot own one. | AC-015 |
| Session row | Click a conversation row | Opens that conversation in the chat surface of the scope already selected, and leaves the navigator's workbench, worktree, device and task selection unchanged, so the list the row was read from stays on screen. | AC-015, AC-018 |
| `SESSIONS` header action | Click it while a task is selected | Starts a conversation bound to that task and opens it. | AC-016 |
| `SESSIONS` header action | No task is selected, and the section is showing the device's task-less conversations | Starts a device-scoped conversation that no task owns and opens it: the list on screen is the device's task-less one, so that is the conversation the action creates. | AC-016 |
| Task detail `Sessions` header | The task is a device-bound worktree task, which is the only kind that can own a conversation | Starts a conversation bound to that task and opens it, and the detail yields the main area to it. A project-scope or hardware task offers no such action. | AC-015, AC-016 |
| Session row menu | Any conversation row's 3-dots | Offers opening, renaming, exporting, binding the conversation to one of the worktree's tasks or removing that binding, and deleting it. Binding opens a searchable picker over the worktree's tasks that can own a conversation; deleting asks for confirmation first. | AC-008, AC-015, AC-017, AC-019 |
| Tag filter active | The user types in the tag filter | Only `PROJECTS` and `WORKTREE` are shown; the unbound-task group, `DEVICE`, `TASKS` and `SESSIONS` are hidden. | AC-006 |
| Section collapse | The user activates a section header | That section alone collapses or expands; the others keep their state, and a collapsed section releases its height so the sections below move up. | AC-007, AC-013 |
| Section height | Always | Every section but the deepest is as tall as its content, so a section holding one row is one row tall and no section reserves height it does not use. The deepest section on screen takes the dock's remaining height, so its lower boundary is the dock's lower boundary. | AC-013 |
| Deepest section | A target or worktree is selected | The deepest section reaches the bottom of the dock, and its body shows no scrollbar while its rows fit that room. Folding it releases the room, so the dock's bottom is then unused. | AC-013 |
| Section separator | The user drags the separator between two adjacent sections, or focuses it and presses `ArrowUp`/`ArrowDown` | The upper section grows and the lower one shrinks by the same amount, each within its minimum height; the sections below the pair do not move. | AC-012 |
| Sections that do not fit | The sections' content is taller than the dock | The sections shrink and each scrolls its own body; every section header stays visible and the column itself does not scroll. | AC-014 |
| Row 3-dots menu | Any section row | Shows that object's operations (project, worktree, device, task, conversation) without navigating to it first, and is visible without hovering, like every other row's menu. | AC-008 |
| Worktree row | A worktree row in the `WORKTREE` section | The row carries its branch icon and name; it carries no expand toggle of its own, and a toggle appears only on a row that has an unbound-task group to open. | AC-003 |
| Task row | Any `TASKS` row | The row carries the task's type icon and its title; it carries no status dot. | AC-005 |
| Device row menu | A `DEVICE` row's 3-dots | Offers the device operations the removed subtree used to hold. | AC-008 |
| `Hardware` row | Selecting it in the `DEVICE` section | Opens the worktree's hardware configuration page and reveals its own `TASKS` list. | AC-009 |
| Worktree `Tasks` tab | A worktree is selected | Unchanged: the worktree tab strip keeps its Tasks view for judging and acting on many items at once. | Preserved behaviour |

## Components and Interactions

| Component responsibility | Reuse / extend / new | Inputs or state | Interaction and response | Governing source |
|---|---|---|---|---|
| Section shell (header, collapse, scroll region) | Extend, in `WorkbenchNavigator` | section id, title, header action, collapsed state, optional dragged height | Header toggles collapse; the section is content-tall until the user drags its separator; its body scrolls independently with a sticky header. | AC-007, AC-013 |
| Section separator | New, in `WorkbenchNavigator` | the two adjacent sections and their current heights | Dragging or pressing the arrow keys redistributes height between exactly those two, clamped to their minimums; the columns below the pair do not move. | AC-012 |
| `PROJECTS` list | Extend existing workbench rows | `workbenches`, selected workbench | Selecting a workbench sets the cascade level and clears deeper selections. | AC-001 |
| `WORKTREE` list | Extend existing worktree rows | worktrees of the selected workbench | Selecting a worktree sets the cascade level and clears deeper selections. | AC-002 |
| Unbound-task list | New group inside `WORKTREE` | selected worktree's tasks with `deviceId == null` | Selecting a task opens its detail; the group is absent when no such task exists. | AC-003 |
| `DEVICE` list | Restored as a flat list | devices of the selected worktree plus one hardware row | Selecting a PLC device or the hardware row sets the cascade level and reveals that target's tasks. | AC-004, AC-009 |
| `TASKS` list | Extend existing task rows | selected device's tasks | Selecting a task opens its detail; creation pre-binds the selected device. | AC-005 |
| `SESSIONS` list | New, reusing `TaskSessionsDisclosure`'s row content and relative time | the selected task and its conversations, or the selected device's task-less conversations | Lists one thing at a time with a heading naming it: the selected task's conversations, or the device's conversations that no task owns. Always expanded: the section is already the container, so a disclosure inside it would be a second expander. | AC-015 |
| `SESSIONS` header action | New | the selected task, or the task-less state | Starts a conversation in the scope the list is showing — bound to the selected task, or the device's own and owned by no task — and opens it. Not offered for the hardware target, which cannot own a conversation. | AC-016 |
| Task page conversation creation | New, in the task detail's `Sessions` header | the task, and whether it can own a conversation | Starts a conversation bound to that task and opens it. It exists because the section's own header action is absent exactly when a task owns no conversation, so a task's first one is started from the page that is about it. | AC-015, AC-016 |
| Session row menu | New | the conversation, its task, the worktree's tasks | Offers opening, renaming, exporting, binding the conversation to a task or removing that binding, and deleting it — every operation the repository performs, because the row is their only entry point now that the right dock's AI sessions page is retired. Binding picks from the worktree's tasks that can own a conversation; deleting asks for confirmation first. | AC-008, AC-015, AC-017, AC-019 |
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
| Any section | Holds fewer rows than an equal share of the dock | It occupies only its header plus those rows, and the sections below it move up. | A deeper selection adds a section, which takes its own content height. | AC-013 |
| Any section | Collapsed | Its header only. | Expanding restores its content height, or the height it was dragged to. | AC-007, AC-013 |
| Deepest section | Its rows fit the dock's remaining height | It fills that height, so its lower boundary is the dock's, and its body does not scroll. | A separator drag or a collapse above it changes how much room it has. | AC-013 |
| Any section | Its content is taller than the dock can give it | Its body scrolls; its header stays visible. | Give it more height by dragging its separator or by collapsing another section. | AC-014 |
| Any section | The user dragged its separator | The pair keeps the dragged split while the app stays open. | A reload returns every section to its content height. | AC-012 |
| `SESSIONS` section | The selected target's tasks are still loading | Section absent while the tasks load, so the task-less rule cannot be applied to a task list that has not arrived, then present or absent per its content rule. | The task load settles it; the section never shows a loading placeholder of its own. | AC-015 |
| `SESSIONS` section | The list its content rule yields is empty | Section absent, so the navigator does not reserve a row for a list that cannot exist. | Selecting a task that owns a conversation makes the section appear. For a task that owns none, or a device with no task-less conversation, the conversation is started from that scope's chat surface, because the section's header action is part of the section. | AC-015 |
| Session row | The conversation has no title | Falls back to its first user message, then to an untitled label, as the task surface already does. | Renaming it from the row menu gives it a title. | AC-015 |
| Session row | Delete is chosen from its menu | A confirmation names the conversation and states that its link to the task is lost. | Confirming removes the conversation from the section and from the task's own conversation list; cancelling changes nothing. | AC-017 |
| Session row | A binding operation is chosen from its menu | A searchable picker lists the worktree's tasks that can own a conversation; a task with no device is not offered, and no free-form task id is requested. | Choosing one moves the row into the list of the task it now names, or out of it when the binding is removed. | AC-019 |
| Session row | The conversation is renamed, deleted or re-bound in another surface — the chat panel, the task surface or another navigator row | The section follows it: the row shows the new name, moves to the list of the task it now names, or is gone once the conversation is deleted, without a reload. | The section is re-read from the same device list every surface changes. | AC-015 |
| Session row | A conversation is opened while its task's detail is in the main area | The detail yields the main area to the conversation, and the navigator keeps the device, the highlighted task row and the section's list. | The detail is still reachable from its task row, and the conversation is open in the chat view. | AC-018 |
| Session row | A conversation's device is not the selected one | It opens in the worktree-level chat view, which is the scope that has no device; the device selection is released with it. | Selecting the device the conversation belongs to opens it in that device's workspace instead. | AC-018 |
| Any reachable section | Section content fails to load | Existing error text for that collection; other sections keep rendering. | Retry through the existing refresh actions. | Preserved behaviour |

## Visual Constraints (When Applicable)

| Element / view | Constraint | Repository or approved design source | Acceptance observation |
|---|---|---|---|
| Section headers | Reuse the existing dense sidebar heading treatment and tokens from `studio/src/assets/main.css`. | `docs/STYLEGUIDE.md` | Headers are visually consistent with the current `PROJECTS` heading. |
| Section height | Every section's default height is its content height; only a drag sets a height that stops following content, and only the deepest section grows beyond its content to reach the dock's bottom. | ADR-0008 | A dock with one workbench and one device shows a short `PROJECTS` and a short `DEVICE`, with no gap reserved for a section that holds little, and the deepest section's boundary at the dock's bottom. |
| Section scroll regions | Each section keeps its own bounded scroll region with a sticky header; the column itself does not scroll its headers away, so a long section is squeezed and scrolls rather than pushing its neighbours' headers off screen. | ADR-0006, ADR-0008 | With all sections populated, every header stays visible while a single section scrolls. |
| Active section | The section containing the current scope keeps at least enough height to show its rows, and every section keeps a minimum height of its header plus one row. | ADR-0006, ADR-0008 | The selected row is visible without manual scrolling after a selection; no section can be dragged or squeezed to an unusable height. |
| Navigator rows | All five row kinds — `PROJECTS`, `WORKTREE`, `DEVICE` (its hardware row and its PLC rows), `TASKS` and `SESSIONS` — render one row treatment: the same geometry and row rhythm, `bg-accent/50` as the only current-row marker, and a row icon that stays `text-muted-foreground` in every state. Semantic status colours are not row identity: the knowledge dot and the unavailable badge keep their own colours. | `docs/agent-prompts/007-navigator-shared-row-treatment.md`, `docs/STYLEGUIDE.md` | Selecting a row anywhere in the navigator marks it the same way; no row's icon changes colour with its state, and no two row kinds differ in row height, padding or gap. |
| Session rows | The navigator's own row treatment — title and relative last response time — with one heading naming what the list is showing, and the conversation the workspace has open carrying the current-row marker. | `WorkbenchNavigator.tsx`, `docs/agent-prompts/007-navigator-shared-row-treatment.md` | A conversation row matches the navigator's other rows rather than the task surface's chrome, the heading distinguishes the list from the section's rows, and opening a conversation marks only that row. |
| Row menus | Every section row reveals its 3-dots menu the same way: it is visible without hovering, and its button keeps the row's own hover treatment. | `docs/STYLEGUIDE.md`, the `PROJECTS`/`WORKTREE`/`DEVICE` rows | No row's operations are hidden behind a hover the user has to discover. |
| Section separator | A one-pixel rule between adjacent sections, with a wider invisible hit area and the existing focus treatment. | `WorktreeTasksPanel` column resize | The separator is discoverable on hover and focusable by keyboard, and it is visually consistent with the task list's column separators. |

## Accessibility Requirements (When Applicable)

| Component / interaction | Keyboard, semantic, announcement, or contrast behavior | Source | Acceptance observation |
|---|---|---|---|
| Section headers | Native buttons exposing `aria-expanded` and `aria-controls`. | Preserved repository rule (native buttons with accessible labels) | Keyboard users can collapse and expand each section. |
| Section rows | Native buttons with accessible names; the current scope is marked with `aria-current`. | `WorkbenchNavigator.tsx` existing row pattern | Keyboard and screen-reader users can identify the selected item per section. |
| Session rows | Native buttons with an accessible name naming the conversation, and a 3-dots menu naming the conversation too. | `TaskSessionsDisclosure` row labels | Keyboard and screen-reader users can identify and open a conversation, and reach every operation on it — including export and its task binding — without opening it first. |
| Section separators | A window splitter: `role="separator"`, `aria-orientation="horizontal"`, an accessible name naming the section it resizes, `aria-valuenow`/`aria-valuemin`/`aria-valuemax` in pixels, and `ArrowUp`/`ArrowDown` to resize by a fixed step. | `WorktreeTasksPanel` column resize (preserved repository rule) | Keyboard users can resize every separator without a pointer, and a screen reader announces the section, its current height and its range. |
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
| AC-012 | Section separator | Dragging or arrowing a separator grows one section and shrinks its neighbour by the same amount, and neither moves a third section. |
| AC-013 | Section height | A section holding one row is one row tall, collapsing a section moves the sections below it up, and the deepest section's lower boundary is the dock's lower boundary with no scrollbar while its rows fit. |
| AC-014 | Sections that do not fit | Every section header stays visible while the section bodies scroll, and the column itself does not scroll. |
| AC-015 | `SESSIONS` section | The section shows the selected task's conversations under a heading naming that task, or — when no task is selected — the selected device's conversations that no task owns, under a heading stating that. Selecting a device, the `Hardware` row, a worktree or a project selects no task, so that is the state a scope selection starts in and the way back to a conversation no task owns. It is absent when its rule yields nothing, including for the hardware target, which cannot own a conversation. The list is the current one: a conversation renamed, deleted or re-bound in any surface is reflected in it, so a deleted conversation's row is gone rather than left unopenable. |
| AC-016 | `SESSIONS` header action | The header starts a conversation in the scope the list is showing and opens it: bound to the selected task, or the device's own and owned by no task while none is selected. The hardware target, which cannot own a conversation, offers no such action. |
| AC-017 | Deleting a conversation | Choosing delete on a row asks for confirmation naming the conversation; confirming removes it from the section and from the task's conversation list, and cancelling leaves both unchanged. |
| AC-018 | Opening a conversation from a row | Opening it leaves the navigator's workbench, worktree, device and task selection unchanged, with the section's list and the selected task row still on screen, and shows the conversation in the chat view of that scope; a conversation whose device is not the selected one opens in the worktree-level chat view instead. |
| AC-019 | Conversation row menu operations | The row's menu offers exporting the conversation and binding it to one of the worktree's tasks, and offers removing that binding while it is bound. Binding opens a searchable picker over the worktree's tasks, never a free-form task-id prompt, and a task that cannot own a conversation — one with no device — is not offered. |
| Preserved behaviour | Worktree `Tasks` tab | The main-area Tasks view still renders in the worktree tab strip and is unchanged. |

## Open User Decisions

| Decision | Effect on current UI |
|---|---|
| Automatic collapse of shallower sections, keeping one level above the active scope | Confirmed as desirable but deferred. The sections now size to their content and release their height when collapsed, so the deferred behaviour needs a rule for when to collapse, not a layout change. |
| Persisting a dragged section height across sessions | Deferred. The override is session state, which mirrors how the task list's dragged column widths behave; persistence would add a stored layout shape. |

## Update History

| Date | Version | Changes |
|---|---|---|
| 2026-09-30 | 1.0 | Initial specification |
| 2026-10-02 | 1.1 | Sections size to their content and release their height when collapsed (AC-013); they shrink and scroll their own bodies before the column scrolls (AC-014); a separator resizes two adjacent sections (AC-012). Per ADR-0008. |
| 2026-10-02 | 1.2 | The deepest section on screen takes the dock's remaining height, so its boundary is the dock's boundary and its body does not scroll before its rows fill that room; folding it releases the room. Per ADR-0008. |
| 2026-10-02 | 1.3 | A fifth `SESSIONS` section below `TASKS` lists the selected target's task-bound conversations under the task that owns each one, with a creation action in its header and a per-row menu; conversations bound to no task are out of scope, and the hardware target has none. Per ADR-0009. |
| 2026-10-02 | 1.4 | The conversation row menu offers opening, renaming and deleting; deleting confirms first and removes the conversation from the section and the task's list. Re-binding a conversation to another task is out of scope for now. Per ADR-0010. |
| 2026-10-02 | 1.5 | `SESSIONS` now shows one task's conversations at a time: the selected task's, or the selected device's conversations that no task owns while no task is selected; a heading names what the list is showing. The header action binds to the selected task and is offered only while one is selected. Per the revised ADR-0009. |
| 2026-10-02 | 1.6 | Opening a conversation from a row no longer changes the navigator: the workbench, worktree, device and task selection stay as they were, and the conversation opens in the chat view of that scope; a conversation whose device is not the selected one opens in the worktree-level chat view (AC-018). Per ADR-0009. |
| 2026-10-02 | 1.7 | The `SESSIONS` list follows a conversation renamed, deleted or re-bound in any surface (AC-015). Every section row's 3-dots menu is visible without hovering, a task row carries no status dot, and a worktree row carries an expand toggle only when it has an unbound-task group to open. |
| 2026-10-03 | 1.8 | The right dock's AI sessions page is retired, so the `SESSIONS` row menu becomes the conversation's only entry point: it gains export and the task binding operations, the latter from a searchable picker over the worktree's device-bound tasks rather than a free-form id (AC-017, AC-019). The header action starts a conversation in the scope the list on screen is showing, including the device-scoped, task-less state, instead of only while a task is selected (AC-016). Per the amended ADR-0009. |
| 2026-10-03 | 1.9 | One shared row treatment across the navigator's five row kinds: one geometry and row rhythm, `bg-accent/50` as the only current-row marker (the `DEVICE` and `WORKTREE` rows lose their stronger backgrounds), always-`text-muted-foreground` row icons (the `text-chart-2`/`text-chart-4` recolouring goes away), and `TASKS`/`SESSIONS` rows that keep the shared rhythm and reserve only the gutter for their own 3-dots menu instead of a bordered card. The group headings move with the row icon column, and the open conversation is marked in `SESSIONS` without moving any other row or selection (AC-018). Per item 007. |
| 2026-10-05 | 1.10 | Selecting a scope names no task: a device, the `Hardware` row, a worktree or a project clears the task selection — including when the scope already selected is chosen again — so `SESSIONS` returns to the selected device's conversations that no task owns and stays reachable after a task has been opened. The open task detail closes with that selection, so the picked scope is what the main area shows. A task that owns no conversation starts its first one from its own `Sessions` header (AC-015, AC-016). Per ADR-0009 v1.2. |
