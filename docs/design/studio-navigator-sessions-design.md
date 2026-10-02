# Design Document: Studio navigator sessions section

## Overview

- **Outcome**: the navigator shows the conversations of the task the user has selected — or, while no
  task is selected, the selected device's conversations that no task owns — and starts a new one for the
  selected task.
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
| The section's header starts a conversation bound to the selected task | `desired-future` — user decision; the action is offered only while a task is selected |
| A session row menu offers the operations the repository performs | `desired-future` — required by AC-008; the menu offers opening, renaming and deleting |
| A conversation can be deleted from its row, and the delete keeps the two stores consistent | `desired-future` — user decision; the repository's delete path currently removes only the session file and leaves a `TaskSession` edge pointing at it |
| Whether the section needs a cap once a task accumulates many conversations | `speculative` — not in this scope |

Non-goals, each decided by the user:

- No more than one task's conversations are shown at a time: the section is the selected task's list, not
  a list of the target's tasks and their conversations.
- No session-level scope below a task: selecting a conversation opens it, it does not become a cascade
  level of its own.
- Re-binding a conversation to another task is out of scope for now.
- A deleted conversation is not recoverable, and no archiving is added.
- No reordering of sections, and no persistence of a dragged height (unchanged from ADR-0008).
- The task surface's own conversation list is not changed, beyond agreeing with a delete.

Cost, as an early band with its structural evidence: **Medium**, now spanning both layers. One new
section reusing the existing shell, separators and row-menu pattern; one new load in `MainStudio` that
fans out over the devices the navigator already receives; one shared delete operation in `ApiHost` plus
a device-scoped route, because the existing path leaves the graph inconsistent; three session callbacks
that take a conversation instead of its task; an ADR for the section and another for the delete
semantics; a UI Spec revision; and roughly five existing navigator assertions that gain a fifth section
id. The remaining unknown is the row-menu surface, which is bounded by the three operations the
repository performs.

## Acceptance Criteria

- **AC-015** — **When** a task is selected, **the** system **shall** list that task's conversations in the
  `SESSIONS` section under a heading naming the task; **when** no task is selected, **the** system
  **shall** list the selected device's conversations that no task owns under a heading stating that; and
  the section **shall** be absent when its rule yields nothing, including the hardware target, which
  cannot own a conversation. Source: ADR-0009.
- **AC-016** — **When** the user activates the `SESSIONS` header action, **the** system **shall** start a
  conversation bound to the selected task, then open it. The action **shall not** be offered for the
  hardware target or while no task is selected. Source: ADR-0009.
- **AC-017** — **When** the user chooses to delete a conversation from its row, **the** system **shall**
  ask for confirmation naming the conversation, and on confirmation **shall** remove it from both the
  section and the task's own conversation list; cancelling **shall** leave both unchanged. Source:
  ADR-0010.
- **AC-018** — **When** the user opens a conversation from its row, **the** system **shall** leave the
  navigator's workbench, worktree, device and task selection unchanged — with the section's list and the
  selected task row still on screen — and **shall** show the conversation in the chat view of that scope;
  a conversation whose device is not the selected one **shall** open in the worktree-level chat view.
  Source: ADR-0009.

## Existing Evidence

| Evidence | Location | Design effect |
|---|---|---|
| A conversation carries its task binding | `studio/src/api/client.ts:119-133` | The section needs no new data model: it groups what the API already returns. |
| Conversations are listed per device | `src/ApiHost/WorkbenchApiModels.cs:1850` | The load fans out over the worktree's devices, which the navigator already receives as `devicesByWorktree`. |
| The task panel already performs that fan-out and grouping | `studio/src/studio/workbench/WorktreeTasksPanel.tsx:292-301`, `:399` | The load and the grouping are copied patterns, not new ones. |
| A conversation row already exists, with relative time | `studio/src/studio/workbench/TaskSessionsDisclosure.tsx` (`formatRelativeTime`, title fallback, `Open conversation` label) | The row reuses that content and formatting so the same conversation looks the same in both places. |
| The create route already defaults to the worktree's active task when no task is named | `src/ApiHost/WorkbenchApiModels.cs:2143-2144` | A conversation created without a task binding can silently belong to the active task, so the header action names the task it binds to instead of relying on that default. |
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
| Row menu | Open conversation, Rename conversation, Delete conversation (behind a confirmation) | Exactly the three operations the repository performs; re-binding is out of scope for now, and no fourth item is invented |
| Delete | One shared `ApiHost` operation removes the graph entity and its edges first, then the session file; a device-scoped `DELETE` route and the existing compatibility route both call it | The graph edge is what the task detail reads, so removing it first keeps the readable state consistent with what can be loaded; the file delete stays best-effort and idempotent (ADR-0010) |
| Header action | Calls `onAddSession(task)` with the selected task; absent for the hardware target and while no task is selected | A conversation must bind to a task to appear in the section, so the only state that has a correct binding is a selected task; the navigator already holds it and `MainStudio` needs no lookup |
| Row identity | A row carries its whole `ChatSessionInfo`, and the callbacks take that conversation rather than its task | A conversation that no task owns has no task carrying its workbench, worktree and device, so the row states its own context; a task owner was only ever a carrier for those three ids |
| Row open | Opening a row loads the conversation and shows it in the chat view of the scope already selected: the device workspace's chat view when the conversation's device is selected, the worktree-level chat view otherwise. It leaves the workbench, worktree, device and task selection as it found them, and only closes the task detail, which renders ahead of the device workspace | A row is read in the list it was listed in, so opening it must not delete that list from under the user; the detail is the one thing that has to yield, and the task the section is scoped to therefore has to outlive the detail |
| Task selection | The navigator remembers the task it is showing — the row the user picked, or the task a detail opened from elsewhere is showing — instead of deriving it from the open detail alone | The detail closes when a conversation opens, so a derived-only selection would silently move the section to the task-less list at that moment |
| Filter rule | `SESSIONS` is hidden while the tag filter is active, like `DEVICE` and `TASKS` | The tag filter is catalog-level; conversations carry no tags |
| Sizing | Nothing extra: the section is a `NavigatorSection` and becomes the deepest one, so ADR-0008's grow rule follows automatically | No second sizing rule to maintain |

### Change Surface

| File | Change | ACs | Preserved |
|---|---|---|---|
| `studio/src/studio/workbench/WorkbenchNavigator.tsx` | A fifth `NavigatorSection` with a heading and conversation rows, a row menu, and the header action; the separator list and the deepest-section rule follow from the existing `visibleSectionIds`; the session callbacks take a conversation instead of its task; the selected task is remembered rather than derived from the open detail | AC-015, AC-016, AC-018 | The four existing sections, their header actions, the row menus, the collapse contract, the separator contract, the tag-filter rule |
| `studio/src/studio/MainStudio.tsx` | Load the worktree's conversations, stamp each with its device, and pass them down; add `onOpenSession`, `onRenameSession`, `onDeleteSession` and `onAddSession`, each working from the conversation; open a conversation in the scope that is already selected, releasing the device only when the conversation belongs to another one; drop the target-resolution path the header action no longer needs | AC-015, AC-016, AC-018 | The task surface's own conversation list and create flow; the task detail's own conversation links, which still move the scope they belong to |
| `studio/src/api/client.ts` | Reuse `listDeviceSessions`, `loadDeviceChatSession`, `renameChatSession`; add a typed device-scoped delete beside them | AC-017 | No new type; the existing legacy `deleteChatSession` keeps working and gains the graph cleanup through the shared operation |
| `studio/src/studio/workbench/ChooseConversationTaskDialog.tsx` | Removed, with its test: the header action binds to the selected task, so the chooser has no caller | AC-016 | Nothing else used it |
| `src/ApiHost/WorkbenchApiModels.cs` | A device-scoped `DELETE …/devices/{device}/sessions/{session}` that resolves the device explicitly and performs the combined delete | AC-017 | The sibling `GET` routes and the device-scoped session list |
| `src/ApiHost/CompatibilityEndpoints.cs` | The existing `POST /api/chat/session/delete` delegates to the same combined delete, so it stops leaving a `TaskSession` edge behind | AC-017 | Its request and response shape, and the in-memory chat clearing it already does |
| `src/ApiHost/EngineeringGraphApi.cs` | `SessionGraphOperations` gains the removal half of the binding it already creates | AC-017 | `Register`, `SetTask`, `ApplyWithPersistence` |
| `tests/ApiHost.Tests/WorkbenchEndpointsTests.cs` | Cases asserting that a delete removes the graph binding as well as the file, that the task detail stops listing it, and that an unknown conversation is a 404 | AC-017 | The existing compatibility-route cases |
| `studio/src/studio/workbench/WorkbenchNavigator.test.tsx` | The section-set assertions gain `sessions`; the deepest-section assertions move from `tasks` to `sessions`; cases for AC-015, AC-016 and AC-017, including the task-less rule | AC-015, AC-016, AC-017 | Every existing case |
| `docs/ui-spec/studio-information-architecture-ui-spec.md` | The `SESSIONS` rows, AC-015 and AC-016, the filter rule and the update history | — | Every other criterion |

No API, graph, route, entity kind, relation kind, or persisted shape changes.

### Components and Flow

| Component | Input | Interaction and response |
|---|---|---|
| `SESSIONS` section | the selected task, the selected device, the worktree's conversations, the open conversation id | Renders one heading and the rows of the list its content rule selects; renders nothing when that list is empty |
| Conversation row | a conversation | Opens the conversation through `onOpenSession(session)` in the chat view of the selected scope, leaving that scope alone; its menu opens, renames or deletes it |
| Heading | the selected task, or the task-less case | A non-interactive label naming what the list shows, so the section does not carry a second interactive task row |
| Header action | the selected task | Starts a conversation for that task and opens it, or is not offered |

### Contracts, State, and Persistence

| Boundary | Input / exact format | Output | Error or state behaviour | Compatibility |
|---|---|---|---|---|
| Navigator props | `sessionsByWorktree: Record<worktreeKey, ChatSessionInfo[]>` | the section's rows | An absent key means "not loaded yet", so the section stays absent rather than showing an empty list | Additive prop; existing callers keep working. Each entry carries its device, stamped by the load |
| Header action | `onAddSession(task: EngineeringTask)` | the conversation is created, bound and opened | Not called for the hardware target or while no task is selected | Additive prop |
| Session callbacks | `onOpenSession(session)`, `onRenameSession(session, title)`, `onDeleteSession(session)` | the conversation is opened, renamed or deleted | The row always carries its own workbench, worktree and device, so a task-less conversation is operable | Changed signatures; the navigator's only caller is `MainStudio` |
| Row menu delete | `DELETE /api/workbenches/{wb}/worktrees/{wt}/devices/{device}/sessions/{session}` | `204` when the conversation is removed, `404` when it does not exist under that device | Removes the graph entity and its edges, then the session file; the file delete stays idempotent and best-effort, so the route reports success once the graph binding is gone | New route beside the existing `GET`s; the compatibility route keeps its shape |
| Content rule | client-side over already-loaded data | — | Shows the selected task's conversations, or the device's task-less ones while no task is selected; never two tasks at once | No stored shape |

### Repository-Owned Migration, Flag, or Deployment Behavior

None. No persisted shape exists, so there is nothing to migrate, and removing the section restores the
previous four-section cascade.

## Implementation Approach

- **Order**: the load and the content rule first (AC-015), because they are the user's request and need no
  new interaction; then the row menu and the header action (AC-016); then the browser pass.
- **Reuse over invention**: the row content and its relative time come from `TaskSessionsDisclosure`,
  the per-device load from `WorktreeTasksPanel`, the menu from the navigator's existing row-menu
  pattern, and the create flow from `createChatSessionForTask`.
- **Keep the rows always visible**: `TaskSessionsDisclosure` wraps its rows in a disclosure, but the
  section is already the container, so the rows render directly and the disclosure is not reused.
- **First observable checkpoint**: selecting a task with a conversation shows a `SESSIONS` section with
  that task's heading and rows; selecting a device in a worktree whose conversations are all bound
  shows the device's task-less conversations, or no section at all when it has none.

## Verification Strategy

| Claim / AC | Level | Command or operation | Observable pass condition |
|---|---|---|---|
| The section shows the selected task's conversations, and the device's task-less ones while no task is selected | L1 | `npm test -- --run` in `studio/`, cases in `WorkbenchNavigator.test.tsx` | With a task selected only its conversations appear, under a heading naming it; with none selected only the device's task-less conversations appear; never both, and the section is absent when the list is empty |
| The hardware target shows no section and no action | L1 | the same lane | No `sessions` section id and no header action for the hardware target |
| The header action binds to the selected task | L1 | `studio/src/studio/MainStudio.deviceSelect.test.tsx` or a new case | Activating it calls the create flow with the selected task; with no task selected the action is not rendered |
| Opening a conversation leaves the navigator's selection alone | L1 | `studio/src/studio/MainStudio.taskChat.test.tsx` (a navigator row opened with a device and a task selected) and the navigator lane | The device row keeps `aria-current`, the task row stays selected, the section keeps its list, and the conversation renders in the device workspace's chat view |
| The task selection outlives the task detail | L1 | the studio lane (`WorkbenchNavigator.test.tsx`) | With the detail closed — `activeTaskId` gone — the row stays selected and the section keeps that task's conversations |
| A delete removes the conversation from both stores | L1 | `dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj --no-build -v q` | After a delete the task detail no longer lists the conversation, the graph holds no `Session` entity or `TaskSession` edge for it, and an unknown conversation returns `404` |
| The row menu offers exactly open, rename and delete, and confirms before deleting | L1 | the studio lane | The menu's items are those three; choosing delete asks first, and cancelling calls nothing |
| The section is the one that grows | L1 | the same lane | The deepest-section hook moves from `tasks` to `sessions` |
| The dock behaves in the running app | L3 | the running app via `.\launch.ps1`, driven with Playwright | Selecting a task with conversations shows the section with its heading and rows; selecting a device with only bound conversations shows either that device's task-less list or no section; a row opens its conversation; a row menu offers exactly open, rename and delete; no console error |
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
| Re-binding a conversation to another task makes it vanish from the section | Re-binding is out of scope for now, so this cannot happen from the navigator; it can from the task surface | Out of scope by decision; the section simply shows the conversation under whichever task owns it |
| A delete leaves an orphan session file when the file write fails after the graph write | The two stores cannot be updated atomically | The graph write happens first, so the failure leaves something invisible and re-registerable rather than a task detail listing a conversation that cannot load; the orphan costs disk only and is recorded in ADR-0010 |
| A delete removes task evidence | The `TaskSession` edge is the record of which task a conversation belonged to | The confirmation states that the link is lost before the user commits |
| The header action creates a conversation the section then does not show | The action binds to the selected task and the section then shows that task's list, so the new conversation is in it | The action is offered only when a task is selected, and the create flow validates the binding before opening |

## References

- `docs/adr/ADR-0009-navigator-sessions-section.md`
- `docs/adr/ADR-0010-deleting-a-task-conversation.md`
- `docs/adr/ADR-0006-studio-navigator-ownership-and-shape.md`
- `docs/adr/ADR-0008-navigator-section-sizing.md`
- `docs/ui-spec/studio-information-architecture-ui-spec.md`
- `studio/src/studio/workbench/WorkbenchNavigator.tsx`
- `studio/src/studio/workbench/TaskSessionsDisclosure.tsx`
- `studio/src/studio/workbench/WorktreeTasksPanel.tsx`
- `src/ApiHost/CompatibilityEndpoints.cs`, `src/ApiHost/EngineeringGraphApi.cs`

## Update History

| Date | Version | Changes |
|---|---|---|
| 2026-10-02 | 1.0 | Initial design for the navigator's conversations section. |
| 2026-10-02 | 1.1 | The row menu offers open, rename and delete instead of re-binding; the delete adds a shared `ApiHost` operation and a device-scoped route, because the existing path removed only the session file and left its `TaskSession` edge behind. Per ADR-0010. |
| 2026-10-02 | 1.2 | The section lists one task's conversations at a time — the selected task's, or the selected device's task-less ones while no task is selected — and a heading names what it shows. The header action binds to the selected task and is offered only while one is selected, which removes the task chooser; the session callbacks take the conversation instead of its task, because a task-less conversation has no task to carry its context. Per the revised ADR-0009. |
| 2026-10-02 | 1.3 | Opening a conversation from a row no longer changes the navigator: the scope stays selected and the conversation opens in that scope's chat view, with the device released only when the conversation belongs to another one. The navigator therefore remembers the task it is showing, so the section survives the task detail yielding the main area (AC-018). Per ADR-0009. |
