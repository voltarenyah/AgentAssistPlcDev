# Design Document: Studio navigator sessions section

## Overview

- **Outcome**: the navigator shows the conversations each of the selected target's tasks is carrying,
  and starts a new one for that target.
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
| The navigator lists the selected target's task-bound conversations under the task that owns each one | `desired-future` — user decision, grouped by task |
| A conversation bound to no task is not listed | `desired-future` — user decision |
| The section's header starts a conversation for the selected target, binding to the worktree's active task when it belongs to that target and otherwise asking which task to use | `desired-future` — user decision |
| A session row menu offers the operations the repository performs | `desired-future` — required by AC-008; the menu offers opening, renaming and deleting |
| A conversation can be deleted from its row, and the delete keeps the two stores consistent | `desired-future` — user decision; the repository's delete path currently removes only the session file and leaves a `TaskSession` edge pointing at it |
| Whether the section needs a cap once a device accumulates many conversations | `speculative` — not in this scope |

Non-goals, each decided by the user:

- Conversations bound to no task are not listed, even though a worktree can hold them.
- No session-level scope below a task: selecting a conversation opens it, it does not become a cascade
  level of its own.
- Re-binding a conversation to another task is out of scope for now.
- A deleted conversation is not recoverable, and no archiving is added.
- No reordering of sections, and no persistence of a dragged height (unchanged from ADR-0008).
- The task surface's own conversation list is not changed, beyond agreeing with a delete.

Cost, as an early band with its structural evidence: **Medium**, now spanning both layers. One new
section reusing the existing shell, separators and row-menu pattern; one new load in `MainStudio` that
fans out over the devices the navigator already receives; one shared delete operation in `ApiHost` plus
a device-scoped route, because the existing path leaves the graph inconsistent; two new callbacks; an
ADR for the section and another for the delete semantics; a UI Spec revision; and roughly five existing
navigator assertions that gain a fifth section id. The remaining unknown is the row-menu surface, which
is bounded by the three operations the repository performs.

## Acceptance Criteria

- **AC-015** — **When** a task under the selected target has at least one bound conversation, **the**
  system **shall** list those conversations in the `SESSIONS` section under that task; a conversation
  bound to no task **shall** appear nowhere in the navigator; and the section **shall** be absent when
  the target has none, including the hardware target, which cannot own a conversation. Source: ADR-0009.
- **AC-016** — **When** the user activates the `SESSIONS` header action, **the** system **shall** start a
  conversation bound to the worktree's active task if that task belongs to the selected target, and
  otherwise to a task the user picks, then open it. The action **shall not** be offered for the hardware
  target or for a target with no task. Source: ADR-0009.
- **AC-017** — **When** the user chooses to delete a conversation from its row, **the** system **shall**
  ask for confirmation naming the conversation, and on confirmation **shall** remove it from both the
  section and the task's own conversation list; cancelling **shall** leave both unchanged. Source:
  ADR-0010.

## Existing Evidence

| Evidence | Location | Design effect |
|---|---|---|
| A conversation carries its task binding | `studio/src/api/client.ts:119-133` | The section needs no new data model: it groups what the API already returns. |
| Conversations are listed per device | `src/ApiHost/WorkbenchApiModels.cs:1850` | The load fans out over the worktree's devices, which the navigator already receives as `devicesByWorktree`. |
| The task panel already performs that fan-out and grouping | `studio/src/studio/workbench/WorktreeTasksPanel.tsx:292-301`, `:399` | The load and the grouping are copied patterns, not new ones. |
| A conversation row already exists, with relative time | `studio/src/studio/workbench/TaskSessionsDisclosure.tsx` (`formatRelativeTime`, title fallback, `Open conversation` label) | The row reuses that content and formatting so the same conversation looks the same in both places. |
| The create route already defaults to the worktree's active task | `src/ApiHost/CompatibilityEndpoints.cs:474-477` | "Prefer the active task" matches the existing default rather than inventing one. |
| The active task is readable per worktree | `studio/src/api/client.ts:1446` (`GET …/worktrees/{wt}/active-task`) | The header action resolves its task at click time; no new state is tracked. |
| `createChatSessionForTask` refuses a task with no device | `studio/src/studio/MainStudio.tsx:1391-1392` | A hardware target can never own a conversation, so the section and its action are absent there. |
| The section's own row menus are a contract | UI Spec AC-008 | A session row needs a 3-dots menu, so its contents are designed rather than omitted. |
| The deepest section grows into the dock's remaining height | ADR-0008 | `SESSIONS` becomes the section that grows, which the sizing tests must follow. |

## Design

### Selected Design

| Layer | Change | Why this shape |
|---|---|---|
| Data | `MainStudio` loads the selected worktree's conversations by fanning `listDeviceSessions` over `devicesByWorktree[key]`, and passes them to the navigator as `sessionsByWorktree` | Reuses the load the task panel already performs; needs no endpoint, no graph change, and no per-task fetch |
| Grouping | The navigator filters those conversations to the selected target's tasks and groups them by `taskId` | The content rule is one `filter` over data it already holds, next to the `targetTasks` it already computes |
| Visibility | The section renders when at least one target task has a bound conversation | Matches the cascade's rule that a section is absent when the selection does not reach it, and keeps the navigator free of an empty fifth section |
| Rows | A group heading per task, then one row per conversation: title (falling back to the first user message), relative time, and a 3-dots menu | The heading states the relationship the section exists to show; the rows stay uniform so every one can carry a menu |
| Row menu | Open conversation, Rename conversation, Delete conversation (behind a confirmation) | Exactly the three operations the repository performs; re-binding is out of scope for now, and no fourth item is invented |
| Delete | One shared `ApiHost` operation removes the graph entity and its edges first, then the session file; a device-scoped `DELETE` route and the existing compatibility route both call it | The graph edge is what the task detail reads, so removing it first keeps the readable state consistent with what can be loaded; the file delete stays best-effort and idempotent (ADR-0010) |
| Header action | Calls `onAddSession(workbench, worktree, target)`; absent for the hardware target and for a target with no task | The navigator does not need to know about active tasks; `MainStudio` resolves them where the create flow already lives |
| Task resolution | `MainStudio` reads the worktree's active task at click time; if it is one of the target's tasks it calls the existing `createChatSessionForTask` with it, otherwise it opens a task chooser first | Prefers the active task without tracking new state, and never binds a conversation to a task outside the selected target |
| Filter rule | `SESSIONS` is hidden while the tag filter is active, like `DEVICE` and `TASKS` | The tag filter is catalog-level; conversations carry no tags |
| Sizing | Nothing extra: the section is a `NavigatorSection` and becomes the deepest one, so ADR-0008's grow rule follows automatically | No second sizing rule to maintain |

### Change Surface

| File | Change | ACs | Preserved |
|---|---|---|---|
| `studio/src/studio/workbench/WorkbenchNavigator.tsx` | A fifth `NavigatorSection` with task groups and conversation rows, a row menu, and the header action; the separator list and the deepest-section rule follow from the existing `visibleSectionIds` | AC-015, AC-016 | The four existing sections, their header actions, the row menus, the collapse contract, the separator contract, the tag-filter rule |
| `studio/src/studio/MainStudio.tsx` | Load the worktree's conversations and pass them down; add `onOpenSession` and `onAddSession`, resolving the task as the active task or by asking | AC-015, AC-016 | The task surface's own conversation list and create flow |
| `studio/src/api/client.ts` | Reuse `listDeviceSessions`, `loadChatSession`, `renameChatSession`, `getActiveWorktreeTask`; add a typed device-scoped delete beside them | AC-017 | No new type; the existing legacy `deleteChatSession` keeps working and gains the graph cleanup through the shared operation |
| `src/ApiHost/WorkbenchApiModels.cs` | A device-scoped `DELETE …/devices/{device}/sessions/{session}` that resolves the device explicitly and performs the combined delete | AC-017 | The sibling `GET` routes and the device-scoped session list |
| `src/ApiHost/CompatibilityEndpoints.cs` | The existing `POST /api/chat/session/delete` delegates to the same combined delete, so it stops leaving a `TaskSession` edge behind | AC-017 | Its request and response shape, and the in-memory chat clearing it already does |
| `src/ApiHost/EngineeringGraphApi.cs` | `SessionGraphOperations` gains the removal half of the binding it already creates | AC-017 | `Register`, `SetTask`, `ApplyWithPersistence` |
| `tests/ApiHost.Tests/WorkbenchEndpointsTests.cs` | Cases asserting that a delete removes the graph binding as well as the file, that the task detail stops listing it, and that an unknown conversation is a 404 | AC-017 | The existing compatibility-route cases |
| `studio/src/studio/workbench/WorkbenchNavigator.test.tsx` | Five section-set assertions gain `sessions`; the deepest-section assertions move from `tasks` to `sessions`; new cases for AC-015, AC-016 and AC-017 | AC-015, AC-016, AC-017 | Every existing case |
| `docs/ui-spec/studio-information-architecture-ui-spec.md` | The `SESSIONS` rows, AC-015 and AC-016, the filter rule and the update history | — | Every other criterion |

No API, graph, route, entity kind, relation kind, or persisted shape changes.

### Components and Flow

| Component | Input | Interaction and response |
|---|---|---|
| `SESSIONS` section | the selected target's tasks, the worktree's conversations, the open conversation id | Renders one group per task that owns at least one conversation; renders nothing when none does |
| Conversation row | a conversation and its task | Opens the conversation through `onOpenSession(task, sessionId)`; its menu opens, renames, or re-binds it |
| Group heading | the owning task | A non-interactive label, so the section does not carry a second interactive task row |
| Header action | the selected target and its tasks | Starts a conversation for the target and opens it, or is not offered |

### Contracts, State, and Persistence

| Boundary | Input / exact format | Output | Error or state behaviour | Compatibility |
|---|---|---|---|---|
| Navigator props | `sessionsByWorktree: Record<worktreeKey, ChatSessionInfo[]>` | the section's rows | An absent key means "not loaded yet", so the section stays absent rather than showing an empty list | Additive prop; existing callers keep working |
| Header action | `onAddSession(workbench, worktree, target: TaskTarget)` | the conversation is created, bound and opened | Not called for the hardware target or a target with no task | Additive prop |
| Row menu delete | `DELETE /api/workbenches/{wb}/worktrees/{wt}/devices/{device}/sessions/{session}` | `204` when the conversation is removed, `404` when it does not exist under that device | Removes the graph entity and its edges, then the session file; the file delete stays idempotent and best-effort, so the route reports success once the graph binding is gone | New route beside the existing `GET`s; the compatibility route keeps its shape |
| Grouping | client-side over already-loaded data | — | A conversation whose task is not under the selected target is not shown | No stored shape |

### Repository-Owned Migration, Flag, or Deployment Behavior

None. No persisted shape exists, so there is nothing to migrate, and removing the section restores the
previous four-section cascade.

## Implementation Approach

- **Order**: the load and the grouping first (AC-015), because they are the user's request and need no
  new interaction; then the row menu and the header action (AC-016); then the browser pass.
- **Reuse over invention**: the row content and its relative time come from `TaskSessionsDisclosure`,
  the load and the grouping from `WorktreeTasksPanel`, the menu from the navigator's existing row-menu
  pattern, and the create flow from `createChatSessionForTask`. The only new interaction is the task
  chooser the header action needs when the active task is not usable.
- **Keep the rows always visible**: `TaskSessionsDisclosure` wraps its rows in a disclosure, but the
  section is already the container, so the rows render directly and the disclosure is not reused.
- **First observable checkpoint**: selecting a device whose task has a bound conversation shows a
  `SESSIONS` section with that task's group and conversation; a worktree whose conversations are all
  unbound shows no section at all.

## Verification Strategy

| Claim / AC | Level | Command or operation | Observable pass condition |
|---|---|---|---|
| The section lists a target's task-bound conversations under their task, and omits unbound ones | L1 | `npm test -- --run` in `studio/`, new cases in `WorkbenchNavigator.test.tsx` | The bound conversation appears under its task's group; an unbound conversation appears nowhere; the section is absent when nothing qualifies |
| The hardware target and a target with no task show no section and no action | L1 | the same lane | No `sessions` section id and no header action for either case |
| The header action resolves its task | L1 | `studio/src/studio/MainStudio.deviceSelect.test.tsx` or a new case | With a usable active task it calls the create flow with that task; otherwise it asks before creating |
| A delete removes the conversation from both stores | L1 | `dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj --no-build -v q` | After a delete the task detail no longer lists the conversation, the graph holds no `Session` entity or `TaskSession` edge for it, and an unknown conversation returns `404` |
| The row menu offers exactly open, rename and delete, and confirms before deleting | L1 | the studio lane | The menu's items are those three; choosing delete asks first, and cancelling calls nothing |
| The section is the one that grows | L1 | the same lane | The deepest-section hook moves from `tasks` to `sessions` |
| The dock behaves in the running app | L3 | the running app via `.\launch.ps1`, driven with Playwright | Selecting a device with bound conversations shows the section with its groups and rows; a row opens its conversation; a row menu offers exactly open, rename and re-bind; no console error |
| Nothing else regressed | L1 | `npx tsc -b`, `npm run lint`, the full `vitest` lane | Type check clean; no new lint warning; the suite passes as before |

## Material Risks

| Risk | Why it could happen | Mitigation |
|---|---|---|
| The section is absent for a worktree that visibly holds conversations | Unbound conversations are out of scope by decision, and the live `master` worktree holds two of them and no bound one | The empty state names the rule, so the absence reads as a rule rather than a failure |
| The load fans out per device and grows with the device count | The repository has no worktree-level conversation list | The fan-out is over the devices the navigator already receives (one to three in practice); a worktree-level endpoint is the follow-up if a device count makes it matter |
| A conversation is bound to a task outside the selected target | Bindings are manual and can be changed elsewhere | The section filters to the selected target's tasks, so such a conversation is simply not shown there, and its row menu can re-bind it from the target it belongs to |
| Re-binding a conversation to another task makes it vanish from the section | Re-binding is out of scope for now, so this cannot happen from the navigator; it can from the task surface | Out of scope by decision; the section simply shows the conversation under whichever task owns it |
| A delete leaves an orphan session file when the file write fails after the graph write | The two stores cannot be updated atomically | The graph write happens first, so the failure leaves something invisible and re-registerable rather than a task detail listing a conversation that cannot load; the orphan costs disk only and is recorded in ADR-0010 |
| A delete removes task evidence | The `TaskSession` edge is the record of which task a conversation belonged to | The confirmation states that the link is lost before the user commits |
| The header action creates a conversation the section then does not show | It binds to a task, so it appears; only a failure to resolve a task could break that | The action is not offered when there is no task to bind to, and the create flow validates the binding before opening |

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
