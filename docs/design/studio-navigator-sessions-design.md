# Design Document: Studio navigator sessions section

## Overview

- **Outcome**: the navigator shows the conversations of the task the user has selected — or, while no
  task is selected, the selected device's conversations that no task owns — and starts a new one in the
  scope that list is showing, bound to the selected task or to no task at all. Each row's menu carries
  every operation the repository performs on a conversation, because the right dock's AI sessions page
  that used to hold some of them is retired.
- The worktree task surface already lists a task's conversations (`WorktreeTasksPanel.tsx:399` filters a
  device's sessions by `session.taskId`, and `TaskSessionsDisclosure` renders them with a relative
  timestamp). The navigator does not: its cascade is the four sections ADR-0006 fixed, so a task's
  conversations are reachable only by opening a task's card or detail.
- This adds a fifth section below `TASKS`. Governing decisions:
  [ADR-0009](../adr/ADR-0009-navigator-sessions-section.md) (the section and its content rule),
  [ADR-0006](../adr/ADR-0006-studio-navigator-ownership-and-shape.md) (the cascade it extends),
  [ADR-0008](../adr/ADR-0008-navigator-section-sizing.md) (the sizing it joins).

## Requirement Boundary

Requirements, labelled by state:

| Requirement | State |
|---|---|
| A conversation records the task it belongs to, and a task has many conversations | `current-state` — `ChatSessionInfo.taskId`/`taskProvenance` (`studio/src/api/client.ts:119-133`) and the `TaskSession` graph edge (`src/ApiHost/EngineeringGraphApi.cs:140-148`) |
| A worktree's conversations are listed per device, not per worktree | `current-state` — `WorkbenchApiModels.cs:1850`; the task panel fans out over the devices it needs |
| A task's conversations are listed and openable in the task surface | `current-state` — `TaskSessionsDisclosure`, `WorktreeTasksPanel.tsx:399` |
| The navigator shows the conversations of the task the user has selected in `TASKS` | `desired-future` — user decision, revised after the first rule showed a device selection listing every task's conversations |
| While no task is selected, the navigator shows the selected device's conversations that no task owns | `desired-future` — user decision |
| The section's header starts a conversation in the scope the list is showing | `desired-future` — user decision; bound to the selected task, or device-scoped and owned by no task while none is selected, and never offered for the hardware target |
| A session row menu offers the operations the repository performs | `desired-future` — required by AC-008; the menu offers opening, renaming, exporting, binding the conversation to a task or clearing that binding, and deleting |
| The right dock's AI sessions page is retired, and nothing it offered is lost | `desired-future` — user decision; the operations only that page held become navigator row operations, and a device on a chat or source view resolves to no dock at all |
| A conversation can be deleted from its row, and the delete keeps the two stores consistent | `desired-future` — user decision; the repository's delete path currently removes only the session file and leaves a `TaskSession` edge pointing at it |
| Whether the section needs a cap once a task accumulates many conversations | `speculative` — not in this scope |

Non-goals, each decided by the user:

- No more than one task's conversations are shown at a time: the section is the selected task's list, not
  a list of the target's tasks and their conversations.
- No session-level scope below a task: selecting a conversation opens it, it does not become a cascade
  level of its own.
- A deleted conversation is not recoverable, and no archiving is added.
- No reordering of sections, and no persistence of a dragged height (unchanged from ADR-0008).
- The task surface's own conversation list is not changed, beyond agreeing with a delete.

Cost, as an early band with its structural evidence: **Medium**, now spanning both layers. One new
section reusing the existing shell, separators and row-menu pattern; one new load in `MainStudio` that
fans out over the devices the navigator already receives; one shared delete operation in `ApiHost` plus
a device-scoped route, because the existing path leaves the graph inconsistent; three session callbacks
that take a conversation instead of its task; an ADR for the section and another for the delete
semantics; a UI Spec revision; and roughly five existing navigator assertions that gain a fifth section
id. Retiring the dock page adds no new interaction — it moves two operations into the row menu that
already exists — and its remaining unknown is how many tasks a binding picker has to search, which the
worktree's task list already bounds.

## Acceptance Criteria

- **AC-015** — **When** a task is selected, **the** system **shall** list that task's conversations in the
  `SESSIONS` section under a heading naming the task; **when** no task is selected, **the** system
  **shall** list the selected device's conversations that no task owns under a heading stating that; and
  the section **shall** be absent when its rule yields nothing, including the hardware target, which
  cannot own a conversation. Source: ADR-0009.
- **AC-016** — **When** the user activates the `SESSIONS` header action, **the** system **shall** start a
  conversation in the scope the section's list is showing — bound to the selected task, or the selected
  device's own and owned by no task while none is selected — then open it. The action **shall not** be
  offered for the hardware target. Source: ADR-0009.
- **AC-017** — **When** the user chooses to delete a conversation from its row, **the** system **shall**
  ask for confirmation naming the conversation, and on confirmation **shall** remove it from both the
  section and the task's own conversation list; cancelling **shall** leave both unchanged. Source:
  ADR-0010.
- **AC-018** — **When** the user opens a conversation from its row, **the** system **shall** leave the
  navigator's workbench, worktree, device and task selection unchanged — with the section's list and the
  selected task row still on screen — and **shall** show the conversation in the chat view of that scope;
  a conversation whose device is not the selected one **shall** open in the worktree-level chat view.
  Source: ADR-0009.
- **AC-019** — **The** conversation row's menu **shall** offer exporting the conversation and binding it
  to one of the worktree's tasks, and **shall** offer clearing that binding while the conversation is
  bound. Binding **shall** choose from the worktree's tasks that can own a conversation — never a prompt
  for a free-form task id — and **shall not** offer a task with no device. Source: ADR-0009.

## Existing Evidence

| Evidence | Location | Design effect |
|---|---|---|
| A conversation carries its task binding | `studio/src/api/client.ts:119-133` | The section needs no new data model: it groups what the API already returns. |
| Conversations are listed per device | `src/ApiHost/WorkbenchApiModels.cs:1850` | The load fans out over the worktree's devices, which the navigator already receives as `devicesByWorktree`. |
| The task panel already performs that fan-out and grouping | `studio/src/studio/workbench/WorktreeTasksPanel.tsx:292-301`, `:399` | The load and the grouping are copied patterns, not new ones. |
| A conversation row already exists, with relative time | `studio/src/studio/workbench/TaskSessionsDisclosure.tsx` (`formatRelativeTime`, title fallback, `Open conversation` label) | The row reuses that content and formatting so the same conversation looks the same in both places. |
| The create route already defaults to the worktree's active task when no task is named | `src/ApiHost/WorkbenchApiModels.cs:2143-2144` | A conversation created without an explicit task binding can silently belong to the active task, so the header action names the task it binds to — and clears the active task for the scope it starts task-less — instead of relying on that default. |
| The task selection already exists in the navigator | `studio/src/studio/workbench/WorkbenchNavigator.tsx:826` (`activeTaskId ?? clickedTaskId`) | The content rule reuses the same expression that marks the selected task row, so the list and the row can never disagree. |
| `createChatSessionForTask` refuses a task with no device | `studio/src/studio/MainStudio.tsx:1391-1392` | A hardware target can never own a conversation, so the section and its action are absent there. |
| The section's own row menus are a contract | UI Spec AC-008 | A session row needs a 3-dots menu, so its contents are designed rather than omitted. |
| The deepest section grows into the dock's remaining height | ADR-0008 | `SESSIONS` becomes the section that grows, which the sizing tests must follow. |

## Design

### Selected Design

| Layer | Change | Why this shape |
|---|---|---|
| Data | `MainStudio` loads the selected worktree's conversations by fanning `listDeviceSessions` over `devicesByWorktree[key]`, stamps each result with the device it came from, and passes them to the navigator as `sessionsByWorktree` | Reuses the load the task panel already performs; needs no endpoint and no graph change. The device is stamped because a conversation that no task owns has no task to carry it, and its row still needs the device to open, rename or delete it |
| Content | The navigator selects from those conversations with one rule: the selected task's conversations when a task is selected, otherwise the selected device's conversations with no `taskId` | One `filter` over data it already holds, next to the `targetTasks` it already computes, and it uses the same selected-task expression as the task row highlight |
| Visibility | The section renders when that selection is not empty | Matches the cascade's rule that a section is absent when the selection does not reach it, and keeps the navigator free of an empty fifth section |
| Rows | One heading naming what the list shows — the task's title, or that the conversations belong to no task — then one row per conversation: title (falling back to the first user message), relative time, and a 3-dots menu | The heading states which list is on screen now that the section holds one task's conversations at a time; the rows stay uniform so every one can carry a menu |
| Row menu | Open conversation, Rename conversation, Export conversation, Attach task / Reassign task (a picker over the worktree's tasks), Remove task while one is bound, and Delete conversation (behind a confirmation) | The row is the conversation's only entry point now that the dock page is retired, so every operation the repository performs has to be here; binding offers the worktree's device-bound tasks rather than asking for an id |
| Delete | One shared `ApiHost` operation removes the graph entity and its edges first, then the session file; a device-scoped `DELETE` route and the existing compatibility route both call it | The graph edge is what the task detail reads, so removing it first keeps the readable state consistent with what can be loaded; the file delete stays best-effort and idempotent (ADR-0010) |
| Header action | Calls `onAddSession(selectedTask)` with the selected task in the task list, and `onAddSession(null)` in the task-less state, where `MainStudio` clears the worktree's active task before creating so the fallback cannot bind it; absent for the hardware target | The action starts the conversation the list on screen is about, so the new one appears in that same list instead of silently joining another |
| Row identity | A row carries its whole `ChatSessionInfo`, and the callbacks take that conversation rather than its task | A conversation that no task owns has no task carrying its workbench, worktree and device, so the row states its own context; a task owner was only ever a carrier for those three ids |
| List ownership | The section reads the device list from the one refresh every chat surface and row operation calls, and that refresh replaces only the device's slice of the worktree's list | The section is a view, not a copy: refreshing it only from its own rows left a rename made elsewhere showing the old name and a delete made elsewhere showing a row that could no longer be opened |
| Row menu | Visible without hovering, like the `PROJECTS`, `WORKTREE` and `DEVICE` rows' menus | A hover-revealed menu hid the only way to rename or delete a conversation, and every navigator row now reveals its operations the same way |
| Task row | Carries the task's type icon and title, and no status dot | The navigator's rows stay uniform; the status is still editable from the row menu and shown in the task detail |
| Row open | Opening a row loads the conversation and shows it in the chat view of the scope already selected: the device workspace's chat view when the conversation's device is selected, the worktree-level chat view otherwise. It leaves the workbench, worktree, device and task selection as it found them, and only closes the task detail, which renders ahead of the device workspace | A row is read in the list it was listed in, so opening it must not delete that list from under the user; the detail is the one thing that has to yield, and the task the section is scoped to therefore has to outlive the detail |
| Task selection | The navigator remembers the task it is showing — the row the user picked, or the task a detail opened from elsewhere is showing — instead of deriving it from the open detail alone | The detail closes when a conversation opens, so a derived-only selection would silently move the section to the task-less list at that moment |
| Filter rule | `SESSIONS` is hidden while the tag filter is active, like `DEVICE` and `TASKS` | The tag filter is catalog-level; conversations carry no tags |
| Sizing | Nothing extra: the section is a `NavigatorSection` and becomes the deepest one, so ADR-0008's grow rule follows automatically | No second sizing rule to maintain |

### Change Surface

| File | Change | ACs | Preserved |
|---|---|---|---|
| `studio/src/studio/workbench/WorkbenchNavigator.tsx` | A fifth `NavigatorSection` with a heading and conversation rows, a row menu, the header action, and the binding picker; the separator list and the deepest-section rule follow from the existing `visibleSectionIds`; the session callbacks take a conversation instead of its task; the selected task is remembered rather than derived from the open detail | AC-015, AC-016, AC-018, AC-019 | The four existing sections, their header actions, the row menus, the collapse contract, the separator contract, the tag-filter rule |
| `studio/src/studio/MainStudio.tsx` | Load the worktree's conversations, stamp each with its device, and pass them down; add `onOpenSession`, `onRenameSession`, `onExportSession`, `onSetSessionTask`, `onDeleteSession` and `onAddSession`, each working from the conversation; export and re-bind through the device the conversation names; start a task-bound or device-scoped, task-less conversation; drop the target-resolution path the header action no longer needs | AC-015, AC-016, AC-018, AC-019 | The task surface's own conversation list and create flow; the task detail's own conversation links, which still move the scope they belong to |
| `studio/src/studio/chat/SessionDock.tsx` | Removed, with its test: the right dock no longer hosts an AI sessions page, and its operations are row operations now | AC-019 | The device chat surface, which creates, opens and streams conversations itself |
| `studio/src/studio/workspace/contextDock.ts` | The `sessions` content kind is removed, so a selected device on a chat or source view resolves to no dock at all and neither the dock shell nor its resize handle renders | AC-019 | The hardware, device, knowledge and version-control docks |
| `studio/src/api/client.ts` | Reuse `listDeviceSessions`, `loadDeviceChatSession`, `renameChatSession`; add a typed device-scoped delete beside them | AC-017 | No new type; the existing legacy `deleteChatSession` keeps working and gains the graph cleanup through the shared operation |
| `studio/src/studio/workbench/ChooseConversationTaskDialog.tsx` | Removed, with its test: the header action binds to the selected task, so the chooser has no caller | AC-016 | Nothing else used it |
| `src/ApiHost/WorkbenchApiModels.cs` | A device-scoped `DELETE …/devices/{device}/sessions/{session}` that resolves the device explicitly and performs the combined delete | AC-017 | The sibling `GET` routes and the device-scoped session list |
| `src/ApiHost/CompatibilityEndpoints.cs` | The existing `POST /api/chat/session/delete` delegates to the same combined delete, so it stops leaving a `TaskSession` edge behind | AC-017 | Its request and response shape, and the in-memory chat clearing it already does |
| `src/ApiHost/EngineeringGraphApi.cs` | `SessionGraphOperations` gains the removal half of the binding it already creates | AC-017 | `Register`, `SetTask`, `ApplyWithPersistence` |
| `tests/ApiHost.Tests/WorkbenchEndpointsTests.cs` | Cases asserting that a delete removes the graph binding as well as the file, that the task detail stops listing it, and that an unknown conversation is a 404 | AC-017 | The existing compatibility-route cases |
| `studio/src/studio/workbench/WorkbenchNavigator.test.tsx` | The section-set assertions gain `sessions`; the deepest-section assertions move from `tasks` to `sessions`; cases for AC-015, AC-016, AC-017 and AC-019, including the task-less rule, the binding picker and the reachability of every row operation | AC-015, AC-016, AC-017, AC-019 | Every existing case |
| `studio/src/studio/workbench/WorkbenchNavigator.contract.test.ts` | Unchanged: it asserts source text the change does not touch | — | Every assertion |
| `docs/ui-spec/studio-information-architecture-ui-spec.md` | The `SESSIONS` rows, the header action, the row menu, AC-015, AC-016, AC-019, the filter rule and the update history | — | Every other criterion |

No API, graph, route, entity kind, relation kind, or persisted shape changes.

### Components and Flow

| Component | Input | Interaction and response |
|---|---|---|
| `SESSIONS` section | the selected task, the selected device, the worktree's conversations, the open conversation id | Renders one heading and the rows of the list its content rule selects; renders nothing when that list is empty |
| Conversation row | a conversation | Opens the conversation through `onOpenSession(session)` in the chat view of the selected scope, leaving that scope alone; its menu opens, renames, exports, binds or unbinds its task, and deletes it |
| Heading | the selected task, or the task-less case | A non-interactive label naming what the list shows, so the section does not carry a second interactive task row |
| Binding picker | the conversation and the selected worktree's device-bound tasks | A `CommandDialog` over that task list; choosing one binds the conversation, and the device-bound filter keeps a task that cannot own a conversation out of it |
| Header action | the selected task, or the task-less state | Starts a conversation in the scope the list is showing and opens it, or is not offered for the hardware target |

### Contracts, State, and Persistence

| Boundary | Input / exact format | Output | Error or state behaviour | Compatibility |
|---|---|---|---|---|
| Navigator props | `sessionsByWorktree: Record<worktreeKey, ChatSessionInfo[]>` | the section's rows | An absent key means "not loaded yet", so the section stays absent rather than showing an empty list | Additive prop; existing callers keep working. Each entry carries its device, stamped by the load |
| Header action | `onAddSession(task: EngineeringTask \| null)` | the conversation is created and opened, bound to `task` or owned by no task | Not called for the hardware target; the task-less call clears the worktree's active task first, so the create route's fallback cannot bind it | Changed signature; the navigator's only caller is `MainStudio` |
| Session callbacks | `onOpenSession(session)`, `onRenameSession(session, title)`, `onExportSession(session)`, `onSetSessionTask(session, taskId)`, `onDeleteSession(session)` | the conversation is opened, renamed, exported, bound or unbound, or deleted | The row always carries its own workbench, worktree and device, so a task-less conversation is operable, and `taskId: null` clears the binding with the API's existing semantics | Changed signatures; the navigator's only caller is `MainStudio` |
| Row menu binding | `PUT /api/chat/session/task` with `{ sessionId, taskId }` | the conversation's `taskId` and `taskProvenance` are rewritten and the section moves the row | `taskId: null` clears both; the picker never offers a task with no device, which cannot own a conversation | The existing route and its shape are unchanged |
| Row menu delete | `DELETE /api/workbenches/{wb}/worktrees/{wt}/devices/{device}/sessions/{session}` | `204` when the conversation is removed, `404` when it does not exist under that device | Removes the graph entity and its edges, then the session file; the file delete stays idempotent and best-effort, so the route reports success once the graph binding is gone | New route beside the existing `GET`s; the compatibility route keeps its shape |
| Content rule | client-side over already-loaded data | — | Shows the selected task's conversations, or the device's task-less ones while no task is selected; never two tasks at once | No stored shape |
| Right context dock | `resolveContextDock` inputs | a dock state | A selected device on a chat or source view resolves to `visible: false`, so no dock shell and no resize handle render | The `sessions` content kind is removed from the union; the other kinds are unchanged |

### Repository-Owned Migration, Flag, or Deployment Behavior

None. No persisted shape exists, so there is nothing to migrate, and the retired dock page removes a
content kind rather than adding one.

## Implementation Approach

- **Order**: the load and the content rule first (AC-015), because they are the user's request and need no
  new interaction; then the row menu and the header action (AC-016, AC-019); then the browser pass.
- **Reuse over invention**: the row content and its relative time come from `TaskSessionsDisclosure`,
  the per-device load from `WorktreeTasksPanel`, the menu from the navigator's existing row-menu
  pattern, the picker from `TagPicker`'s `CommandDialog`, and the create flows from `createChatSession`
  and `createChatSessionForTask`.
- **Keep the rows always visible**: `TaskSessionsDisclosure` wraps its rows in a disclosure, but the
  section is already the container, so the rows render directly and the disclosure is not reused.
- **Retire rather than duplicate**: the dock page is deleted with its test instead of left unreferenced,
  and the operations it alone held move into the row menu that replaces it.
- **First observable checkpoint**: selecting a task with a conversation shows a `SESSIONS` section with
  that task's heading and rows; selecting a device in a worktree whose conversations are all bound
  shows the device's task-less conversations, or no section at all when it has none.

## Verification Strategy

| Claim / AC | Level | Command or operation | Observable pass condition |
|---|---|---|---|
| The section shows the selected task's conversations, and the device's task-less ones while no task is selected | L1 | `npm test -- --run` in `studio/`, cases in `WorkbenchNavigator.test.tsx` | With a task selected only its conversations appear, under a heading naming it; with none selected only the device's task-less conversations appear; never both, and the section is absent when the list is empty |
| The hardware target shows no section and no action | L1 | the same lane | No `sessions` section id and no header action for the hardware target |
| The header action starts the conversation the section is showing | L1 | the studio lane (`WorkbenchNavigator.test.tsx`) | With a task selected it calls `onAddSession` with that task; with none it calls it with `null`, and the not-rendered case is the hardware target, which cannot own a conversation |
| Opening a conversation leaves the navigator's selection alone | L1 | `studio/src/studio/MainStudio.taskChat.test.tsx` (a navigator row opened with a device and a task selected) and the navigator lane | The device row keeps `aria-current`, the task row stays selected, the section keeps its list, and the conversation renders in the device workspace's chat view |
| A conversation renamed or deleted from its row is reflected in the section | L1 | `studio/src/studio/MainStudio.taskChat.test.tsx` (a rename and a delete driven from the row's own menu) | The row shows the new name, and a deleted conversation's row is gone rather than left unopenable; the delete goes through the device-scoped route |
| Every row menu is visible without hovering | L1 | the studio lane, and the running app | A conversation row's trigger has neither `opacity-0` nor a bare `pointer-events-none`, and a plain click opens its menu |
| The task selection outlives the task detail | L1 | the studio lane (`WorkbenchNavigator.test.tsx`) | With the detail closed — `activeTaskId` gone — the row stays selected and the section keeps that task's conversations |
| A delete removes the conversation from both stores | L1 | `dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj --no-build -v q` | After a delete the task detail no longer lists the conversation, the graph holds no `Session` entity or `TaskSession` edge for it, and an unknown conversation returns `404` |
| The row menu offers open, rename, export, bind, unbind and delete, and confirms before deleting | L1 | the studio lane (`WorkbenchNavigator.test.tsx`) | The menu's items are those, with the binding wording following whether the conversation is bound; choosing delete asks first, and cancelling calls nothing |
| Binding picks from the worktree's tasks and never from a prompt | L1 | the same lane | The picker's options are the worktree's device-bound tasks; a hardware or untargeted task is not offered, and the binding call carries the chosen id; `window.prompt` is never called |
| Binding or clearing moves the conversation between the section's two lists | L1 | the same lane | A conversation whose `taskId` becomes the selected task's appears under that task's heading and leaves the task-less list, and the reverse when the binding is cleared |
| A device on a chat or source view has no right dock | L1 | `studio/src/studio/workspace/contextDock.test.ts` and `studio/src/studio/MainStudio.taskChat.test.tsx` | `resolveContextDock` returns `visible: false` for those focuses, and no `[data-dock="right"]` or context-dock resize handle is rendered |
| The section is the one that grows | L1 | the same lane | The deepest-section hook moves from `tasks` to `sessions` |
| The navigator behaves in the running app | L3 | the running app via `.\launch.ps1`, driven with Playwright | Selecting a task with conversations shows the section with its heading and rows; selecting a device with only bound conversations shows either that device's task-less list or no section; a device chat shows no right dock; a row opens its conversation; a row menu offers open, rename, export, bind, unbind and delete; no console error |
| Nothing else regressed | L1 | `npx tsc -b`, `npm run lint`, the full `vitest` lane | Type check clean; no new lint warning; the suite passes as before |

## Material Risks

| Risk | Why it could happen | Mitigation |
|---|---|---|
| The section is absent in a worktree that visibly holds conversations | The selected task owns none, or the device's conversations are all bound to tasks, so its rule yields nothing | The heading names the list the section is about, so the empty case reads as "nothing for this task" rather than a failure; the count of a task's conversations is visible in its own detail |
| The section is absent for a worktree whose conversations all belong to no task until a device is selected | The task-less rule is device-scoped and needs the device target | Selecting the device the conversations belong to shows them; the cascade already requires that selection to reach a device's work |
| The section changes content when the task selection changes | The rule follows the task selection | The heading states what is shown, so the change is visible rather than silent |
| The load fans out per device and grows with the device count | The repository has no worktree-level conversation list | The fan-out is over the devices the navigator already receives (one to three in practice); a worktree-level endpoint is the follow-up if a device count makes it matter |
| A conversation is bound to a task under another device | Bindings are manual and can be changed elsewhere | The section shows the selected task's conversations, and the task belongs to the selected device, so the two cannot disagree |
| Reading a conversation hides the list it came from | The task detail is rendered ahead of the device workspace, so opening a conversation closes it | The conversation opens in the chat view of the scope that is already selected, so the navigator keeps the device, the task row and the section's list; the detail is reachable again from its task row |
| A stale row outlives the conversation it names | The navigator holds its own copy of the list, and only its own rows used to refresh it | The list is re-read from the device list in the one refresh every surface calls, so a rename or delete made anywhere lands in the section |
| Removing the status dot hides a task's status | The dot was the navigator's only status indicator | The status stays editable from the task row's menu and visible in the task detail; the navigator shows the type icon and title only, by the user's decision |
| Re-binding a conversation to another task makes it move in the section | Re-binding is a row operation now, so the row leaves the list it was read in | The move is the operation's outcome, and the heading names the list that now holds it, so the change is visible rather than silent |
| The task-less header action binds the new conversation to the worktree's active task | The create route falls back to the active task when no task is named, and clearing the navigator's selection does not clear the server's | The task-less start clears the active task for the worktree before creating, so the new conversation lands in the list it was started from |
| The header action creates a conversation the section then does not show | The action starts a conversation in the scope the list on screen is about, so the new one belongs in it | The two scopes the action covers are exactly the section's two lists, and the create flow validates a task binding before opening |
| A delete leaves an orphan session file when the file write fails after the graph write | The two stores cannot be updated atomically | The graph write happens first, so the failure leaves something invisible and re-registerable rather than a task detail listing a conversation that cannot load; the orphan costs disk only and is recorded in ADR-0010 |
| A delete removes task evidence | The `TaskSession` edge is the record of which task a conversation belonged to | The confirmation states that the link is lost before the user commits |
| The task-less start cannot be reached when the device has no task-less conversation | The action lives in the section, and the section is absent when its list is empty | The limitation is recorded in ADR-0009's negative consequences; the chat surface's empty state creates a device conversation without the navigator |

## References

- `docs/adr/ADR-0009-navigator-sessions-section.md`
- `docs/adr/ADR-0010-deleting-a-task-conversation.md`
- `docs/adr/ADR-0006-studio-navigator-ownership-and-shape.md`
- `docs/adr/ADR-0008-navigator-section-sizing.md`
- `docs/ui-spec/studio-information-architecture-ui-spec.md`
- `studio/src/studio/workbench/WorkbenchNavigator.tsx`
- `studio/src/studio/workbench/TaskSessionsDisclosure.tsx`
- `studio/src/studio/workbench/WorktreeTasksPanel.tsx`
- `studio/src/studio/workbench/tags/TagPicker.tsx`
- `studio/src/studio/workspace/contextDock.ts`
- `src/ApiHost/CompatibilityEndpoints.cs`, `src/ApiHost/EngineeringGraphApi.cs`

## Update History

| Date | Version | Changes |
|---|---|---|
| 2026-10-02 | 1.0 | Initial design for the navigator's conversations section. |
| 2026-10-02 | 1.1 | The row menu offers open, rename and delete instead of re-binding; the delete adds a shared `ApiHost` operation and a device-scoped route, because the existing path removed only the session file and left its `TaskSession` edge behind. Per ADR-0010. |
| 2026-10-02 | 1.2 | The section lists one task's conversations at a time — the selected task's, or the selected device's task-less ones while no task is selected — and a heading names what it shows. The header action binds to the selected task and is offered only while one is selected, which removes the task chooser; the session callbacks take the conversation instead of its task, because a task-less conversation has no task to carry its context. Per the revised ADR-0009. |
| 2026-10-02 | 1.3 | Opening a conversation from a row no longer changes the navigator: the scope stays selected and the conversation opens in that scope's chat view, with the device released only when the conversation belongs to another one. The navigator therefore remembers the task it is showing, so the section survives the task detail yielding the main area (AC-018). Per ADR-0009. |
| 2026-10-02 | 1.4 | The section reads its list from the one refresh every chat surface calls, so a conversation renamed or deleted in the session dock is reflected in it (AC-015). Every row menu is visible without hovering, a task row no longer carries a status dot, and a worktree row shows an expand toggle only when it has an unbound-task group to open. |
| 2026-10-03 | 1.5 | The right dock's AI sessions page is retired, so the section is the only surface listing a device's conversations. Its row menu gains the export and task binding operations that page alone offered — binding from a picker over the worktree's device-bound tasks, never a prompt (AC-019) — and its header action starts a conversation in the scope the list is showing, including the device-scoped, task-less state, so AC-016 is no longer limited to a selected task. The `sessions` content kind is removed, and a device on a chat or source view resolves to no dock at all. Per the amended ADR-0009. |
