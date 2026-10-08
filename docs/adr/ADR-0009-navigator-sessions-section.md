# ADR-0009 Navigator section for a task's conversations

## Status

Accepted

## Context

A worktree task has one-to-many chat sessions, and the link already exists in both stores: the session
header carries `taskId` and `taskProvenance` (`ChatSessionInfo`, `studio/src/api/client.ts:119-133`),
and each bound session is also a graph entity joined by a `TaskSession` edge
(`src/ApiHost/EngineeringGraphApi.cs:140-148`). The worktree task surface already lists them per task
(`WorktreeTasksPanel.tsx:399` filters a device's sessions by `session.taskId`, and
`TaskSessionsDisclosure` renders them with a relative timestamp).

The left navigator does not show them at all. Its cascade is the four sections ADR-0006 fixed —
`PROJECTS`, `WORKTREE`, `DEVICE`, `TASKS` — and a task's conversations are reachable only by opening a
task's card or detail view. The user asked for a section below `TASKS` that shows the conversations
each task is carrying.

Repository evidence that constrains the choice:

- **No worktree-level session list exists.** Sessions are listed per device
  (`GET …/worktrees/{wt}/devices/{device}/sessions`, `WorkbenchApiModels.cs:1850`) and per task (the
  task detail's `sessions`). A worktree-wide list would need a new endpoint or a fan-out over the
  worktree's devices, which the task panel already performs (`WorktreeTasksPanel.tsx:292-301`).
- **A hardware task can never own a conversation.** `createChatSessionForTask` refuses a task with no
  `deviceId` (`MainStudio.tsx:1391-1392`), because a session needs a device context to resolve.
- **Conversations without a task are normal, not exceptional.** In the live workbench, `master` holds
  two sessions and neither is task-bound, while `testagentcreate` holds three and all three are. So a
  section that lists only task-bound conversations can legitimately be empty in a worktree that has
  conversations.
- **A conversation can already be deleted, but only through the compatibility route, and that route
  leaves the graph inconsistent.** `POST /api/chat/session/delete` removes the session file and never
  removes the `Session` entity or its `TaskSession` edge, so a task detail keeps listing a conversation
  that can no longer be loaded. [ADR-0010](ADR-0010-deleting-a-task-conversation.md) decides the delete
  itself; this ADR only decides where the conversations are shown and what a row offers.
- **Every navigator section row owes a 3-dots menu** (UI Spec AC-008), so a session row needs one too —
  and the menus the navigator's other rows carry are visible without hovering, so a session row's menu
  has to match them rather than hiding behind a hover the user has to discover.
- **The deepest section grows into the dock's remaining height** (ADR-0008), so a fifth section becomes
  the one that grows and takes that role from `TASKS`.

The content rule below was first decided as target-scoped: every conversation bound to a task under the
selected device, grouped by that task. Using it showed the defect the rule has: selecting a device —
that is, selecting no task — listed conversations belonging to tasks the user had not opened, so the
list had no relation to the task being worked on, and a worktree whose conversations are all task-bound
showed every one of them at once. This ADR records the revised rule; the earlier one is not kept.

The row's open behaviour was inherited from the task detail's own conversation links, which had to
switch the whole scope to the worktree-level chat view. Reusing that path for a navigator row meant
reading a conversation dropped the selected device and emptied the `TASKS` and `SESSIONS` sections, so
the list the row came from disappeared as it was opened. The rule below separates the two: a row shows a
conversation, and the task detail's link still moves the scope it belongs to.

The section also listed a conversation that had been renamed or deleted in another surface, because the
navigator kept its own copy of the list and only its own rows refreshed it: a renamed conversation kept
its old name, and a deleted one kept a row that could no longer be opened. The list is therefore read
from the same device list every surface changes, in one place, rather than owned by the section.

The section is now also the only surface that lists a device's conversations. The right context dock's
"AI sessions" page was removed: it reserved a panel for a list the navigator already showed, and it
duplicated the list's operations behind a second copy that could disagree with it. That page was,
however, the only entry point for two operations — exporting a conversation, and binding one to a task
through a prompt for a raw task id — so the removal moves them into the row menu instead of deleting
them. The page's creation action also served the device-scoped, task-less state, so the section's
header action has to cover that state too rather than being offered only while a task is selected.

A conversation can now be related to several tasks at once, and the relation belongs to the engineering
graph ([ADR-0014](ADR-0014-session-task-relations-belong-to-the-graph.md)). The content rule below does
not change — the section still lists one task's conversations — but a conversation several tasks share
is listed by each of them, and the row menu's binding is a set rather than a single choice. This is the
first condition this ADR recorded under **Reconsider when**, met by a user's requirement, and ADR-0014
is what answers it.

## Decision Point

- **Question**: where does a task's conversation live in the navigator, and which conversations does
  the section show?
- **Why a decision exists**: at least three placements fit the repository and each implies a different
  section model, scroll owner, and row contract, so the choice cannot be settled inside the component.
- **Scope boundary**: the navigator's treatment of a task's conversations — their placement, the
  content rule, the header action and the row menu. It does not change how a conversation is created,
  stored, bound to a task, or rendered in the task surface, and it does not add a session-level scope.

## Decision

A fifth navigator section, `SESSIONS`, sits below `TASKS`. It shows the conversations of the task the
user has selected in `TASKS`. While no task is selected, it shows the selected device's conversations
that no task owns. Its header starts a new conversation in the scope the list itself is showing: bound
to the selected task, or — while no task is selected — the device's own and owned by no task, which is
exactly the list on screen then. The action is never offered for the worktree's hardware, which cannot
own a conversation. Each row's menu carries every operation the repository performs on a conversation —
open, rename, export, bind it to one or more of the worktree's tasks and clear any of those bindings,
and delete under ADR-0010 — because the row, not the removed dock page, is now their only entry point.
Opening a row shows that conversation and changes nothing else: the navigator keeps the selection the
row was listed under, so the list the user just read stays where it was.

Selecting a device, the hardware row, a worktree or a workbench names no task, so it clears the task
selection: that state is the selected device's task-less list, which is what the section shows until a
task row is picked. The open task detail yields with it, because the detail renders ahead of every other
view and would otherwise show nothing of the scope the user picked while keeping the task it names as the
selection. That reset is what keeps a conversation no task owns reachable once any task has been opened,
and a task that owns no conversation therefore no longer takes the whole section off screen behind it.

### Decision Details

| Item | Content |
|------|---------|
| **Decision** | A fifth flat section listing the conversations of the selected task, or the selected device's task-less conversations while no task is selected, with a creation action in its header and a per-row menu carrying every operation the repository performs on a conversation. Opening a row is a content action: it does not change what the navigator is showing. |
| **Header action** | Starts a conversation in the scope the section is showing: bound to the selected task, or the selected device's own and owned by no task while none is selected. Never offered for the hardware target, which cannot own a conversation. A task that owns no conversation starts its first one from its own detail page, whose `Sessions` header carries the same action. |
| **Target selection** | Selecting a device, the hardware row, a worktree or a workbench clears the task selection, so the section returns to the selected device's task-less conversations. Activating the target that is already selected clears it too, because it is the same statement about the scope. The open task detail closes with the selection, so the scope the user picked is what the main area shows. |
| **Row menu** | The conversation's only entry point, so it holds open, rename, export, bind it to one or more tasks or clear any of those bindings, and delete. Binding is a searchable picker over the worktree's tasks that can own a conversation — never a prompt for a raw task id — with one check per task: a click sets that binding, a second click clears it, and the resulting set is applied in one operation (ADR-0014). |
| **Row open** | Opens the conversation in the chat view of the scope that is already selected, and leaves the workbench, worktree, device and task selection untouched. A conversation whose device is not the selected one — which the worktree's own task surface can ask for — has no device workspace to open in, so it opens in the worktree-level chat view, the one scope without a device. |
| **Row contents** | The section is a view of the device's conversation list, not a copy of it: it is re-read wherever that list changes, so a conversation renamed, deleted or re-bound in any surface is reflected in the section without a reload. Its menu is visible without hovering, like the navigator's other row menus. |
| **Why this** | It keeps the cascade one section per scope level, keeps every list bounded and scrollable on its own, and makes the list say exactly what the row above it says is selected, without reopening the nesting ADR-0006 removed. |
| **Known unknowns** | Whether a conversation ever needs to be reachable without its task outside the task-less case this rule now covers, whether the section needs a cap once a task accumulates many conversations, and whether a conversation related to several tasks needs an indication of that in its row. |
| **Reconsider when** | Users ask to reach a conversation from the navigator without selecting the task that owns it. The first condition this list originally carried — conversations belonging to several tasks at once — was met and answered by ADR-0014 v1.0; the section still lists one task's conversations at a time. |

### Placement

| Option | Fit | Cost |
|---|---|---|
| Fifth flat `SESSIONS` section below `TASKS` | Matches the section shell, the per-section scroll region, the row menu contract and the collapse model that already exist | The cascade gains a fifth level, and the deepest-section role moves from `TASKS` to `SESSIONS` |
| Expand each `TASKS` row to reveal its conversations | Puts the conversations beside the task that owns them | Reintroduces the nested subtree ADR-0006 removed, and a list that appears inside a section cannot be bounded by that section's scroll region or resized by the separators |
| Leave the conversations out of the navigator | No change at all | The user asked for them, and they stay reachable only through a task's card or detail |

**Selected**: the fifth section. Nesting would undo the decision this cascade exists to make.

### Content rule

| Option | Fit | Cost |
|---|---|---|
| The selected task's conversations; the device's task-less conversations while no task is selected | Answers "what is this task carrying", which is what the section is for, and keeps the answer true when no task is selected | Two rules to state, and the section's content changes when the task selection changes |
| The selected target's task-bound conversations, grouped by task (the rule as first decided, replaced) | Shows every conversation the target holds without needing a task selection | Lists conversations of tasks the user is not working on, so selecting a device shows a list unrelated to the selected task |
| Only the selected task's conversations, with nothing when no task is selected | One rule, and the section is only ever about one task | A worktree's task-less conversations stay unreachable from the navigator, and the live `master` worktree is exactly that case |
| Every conversation in the worktree, ignoring the target | One list to scan | Stops being target-scoped, so it would contradict the cascade it sits in |

**Selected**: the first, on the user's decision after using the second. The section's heading names the
task it is showing, or that the conversations shown belong to no task, so the list never has to be
guessed at.

## Rationale

### Options Considered

| Option | Requirement and repository fit | Current-scope benefit | Lifecycle cost | Maintainability | Material trade-offs |
|---|---|---|---|---|---|
| A. Fifth flat section | Reuses the section shell, separators, scroll region, row menu and the deepest-section rule unchanged | Conversations become reachable where tasks are | The section set is no longer the four ADR-0006 named, so that decision needs this amendment | One more instance of an established section pattern | The cascade reads as five levels, one of which is a leaf list rather than a scope |
| B. Nested under each task row | Conversations sit beside their task | Reads naturally per task | Undoes ADR-0006's flat sections and has no bounded scroll region of its own | Two section models to reason about | The nesting, not the conversations, becomes the maintenance cost |
| C. Leave them out | Zero change | None | The gap the user reported stays | — | Rejects a direct user request |

**Selected**: A. It is the only option that adds the conversations without reopening the section model.

## Consequences

### Positive Consequences

- A task's conversations are visible and openable from the navigator, next to the task they belong to.
- The section reuses the shell, the separators, the per-section scroll region, the row menu contract and
  the deepest-section rule, so it adds no new interaction to learn.
- The section shows the conversations of the task the `TASKS` section is showing as selected, so the two
  sections can never disagree about which task the user is working in.
- A conversation that belongs to no task is reachable from the navigator, which is the only state the
  live `master` worktree's conversations are in.
- It stays reachable after a task has been opened: selecting a target names no task, so the device's
  task-less list is one selection away, and a task that owns no conversation can no longer hide the
  section behind an empty list of its own.
- Reading a conversation leaves the navigator where it was, so the list a row was read from is still
  there when the user comes back to it, and the task selection is not lost with the task detail.
- Retiring the right dock's "AI sessions" page loses no capability: export and task binding, which only
  that page offered, are row-menu operations now, and binding asks for a choice from the worktree's
  tasks instead of requiring a task id the user had to know.
- A conversation related to several tasks is listed by each of them, so a finding recorded in one
  conversation is reachable from every task that conversation produced, and both surfaces that list
  conversations read the same relation (ADR-0014).

### Negative Consequences

- The navigator's cascade is no longer the four sections ADR-0006 fixed, so that ADR's shape needs this
  amendment rather than standing alone.
- The section holds one thing at a time: it cannot show two tasks' conversations side by side, and a
  worktree with many task-less conversations shows them together, with the heading as the only signal
  that they are not one task's.
- A conversation several tasks share is listed under each of them, so the same row can be read under
  two headings with nothing in the row itself saying it belongs to both.
- Selecting the hardware row leaves the section with nothing to show, because a hardware task cannot own
  a conversation at all, and the device scope the task-less rule needs does not exist there either.
- The section is absent, rather than empty, whenever its rule yields nothing, so its absence can mean
  "this task has no conversations", "this device has none that no task owns", or "the tasks are still
  loading"; the heading the section shows when it is present is what tells the first two apart.
- The header action lives in the section, so a state whose list is empty shows no section and therefore
  no action: a selected task that owns no conversation cannot start its first one from here, and neither
  can a device that has no task-less conversation. The task's own page offers creation for the first case
  — its `Sessions` header starts the conversation and then yields the main area to it — and the chat
  surface's empty state covers the second. Rendering the section whenever a device is selected would
  contradict the content rule above, which is why the second gap is stated rather than closed.

### Neutral Consequences

This section itself adds no route, entity kind, relation kind, or persisted shape. The task surface keeps rendering its
own conversations exactly as it does today, and a conversation is still created, bound, exported and deleted through the
operations that already exist — the row menu only gives them their entry point back where the retired dock page held it.

## Architecture Impact

`WorkbenchNavigator` gains a section, and `MainStudio` gains the load and the callbacks it needs — open,
rename, export, bind, delete, and one to start a conversation. The session data itself is already
available per device, so showing the conversations involves no API, graph, or storage change; the delete
those rows offer is a separate decision with its own architecture impact (ADR-0010), and the retired dock
page takes its `sessions` content kind with it, so `contextDock` resolves a device on a chat or source
view to no dock at all.

## Implementation Guidance

Load the conversations the way the task surface already does: fan out the existing per-device list over
the selected worktree's devices, then select from that fan-out by the task the `TASKS` section shows as
selected — the task whose detail is open, or the task row last clicked, the same expression that
highlights the row — and fall back to the selected device's task-less conversations when that selection
is empty. Keep the device on each loaded conversation, because a row's open, export, re-binding and
delete need the device that owns it and a task-less conversation has no task to carry it. Reuse
`TaskSessionsDisclosure`'s row content and its relative-time formatting for the row itself, but keep the
rows always visible: the section is the container, so a disclosure inside it would be a second,
redundant expander. A row menu offers the operations the repository performs — open, rename, export,
bind to a task or clear that binding, and delete — and the delete follows ADR-0010 rather than being
reimplemented here. Binding a conversation is a choice from the worktree's tasks that can own one — a
session resolves through a device, so a hardware or untargeted task is not offered — presented as a
searchable picker with one check per task rather than an id prompt, where a second click on a checked
task clears that binding and the resulting set is applied in one operation (ADR-0014). Starting a
conversation from the header follows the list the
section is showing: bound to the selected task, or the device's own and owned by no task while none is
selected, in which case the create route's fallback to the worktree's active task is cleared first so the
new conversation cannot silently acquire a binding the list it appeared in does not show.

Opening a row is not navigation. It loads the conversation into the chat surface of the scope that is
already selected — the device workspace's chat view when a device is selected, the worktree-level chat
view otherwise — and it leaves the workbench, worktree, device and task selection as it found them. The
one thing it must clear is the task detail, because the detail renders ahead of the device workspace and
would otherwise stay in front of the conversation; the task the navigator treats as selected therefore
has to outlive the detail it was opened from, which is why the navigator remembers the task it is
showing rather than deriving it from the detail alone.

The task selection is not sticky. The navigator remembers the task the user picked, or the one the open
detail is showing, and that memory is cleared whenever the selected target changes — including when the
target that is already selected is activated again, which is the same statement about the scope the user
is working in. The open detail closes with it, because the detail renders ahead of every other view:
leaving it up showed nothing of the scope the user had picked and kept the task it names as the
selection. A task that owns no conversation starts its first one from its own page, whose `Sessions`
header carries the section's creation action and then yields the main area to the conversation it
started, exactly as that page's own session rows do.

The section is not the owner of the conversations it lists. Re-read the device's list in the one place
every conversation change already goes through — the refresh every chat surface and every row operation
calls after a create, rename, export, re-binding or delete — and let the section read that, rather than
refreshing it only from its own rows. Otherwise a rename made elsewhere leaves the old name in the
navigator, and a delete made elsewhere leaves a row that can no longer be opened. Its row menu is visible
without hovering, like the menus on the navigator's other rows.

Membership in that list is a set, not a field (ADR-0014): a conversation is in the section while the
selected task is among its relations, and the task-less case below it is the conversation with no
relation at all. The device list therefore has to carry every relation of a conversation, not one id,
and the projection that fills it belongs wherever both the graph and the conversation file are
reachable.

## Update History

| Date | Version | Changes |
|---|---|---|
| 2026-10-02 | 1.0 | The fifth `SESSIONS` section, its content rule, the header action bound to the selected task, and the row menu with open, rename and delete. The earlier revisions of those decisions are recorded in `docs/design/studio-navigator-sessions-design.md`. |
| 2026-10-03 | 1.1 | The right dock's "AI sessions" page is retired, so this section becomes the only surface listing a device's conversations. Its row menu gains the export and task binding operations that page alone offered — binding from a picker over the worktree's tasks rather than an id prompt — and its header action starts a conversation in the scope the section is showing, including the device-scoped, task-less state, instead of only while a task is selected. |
| 2026-10-05 | 1.2 | The task selection is cleared whenever the selected target changes, and activating the target that is already selected clears it too: a target selection names no task, so the section returns to the selected device's task-less conversations and a conversation no task owns stays reachable after a task has been opened. The open task detail closes with that selection, so the picked scope is what the main area shows. A task that owns no conversation starts its first one from its own detail page, whose `Sessions` header carries the section's creation action — closing the first of the two gaps the negative consequences recorded. |
| 2026-10-08 | 1.3 | A conversation may be related to several tasks, so the row menu's binding becomes a set: a searchable picker with one check per task, where a second click clears that binding and the set is applied in one operation. The content rule is unchanged — the section still lists the selected task's conversations, and a conversation several tasks share is now listed by each of them — and the task-less case remains the conversation with no relation at all. This answers the first condition this ADR recorded under **Reconsider when**; the relation itself, its ownership by the engineering graph and the primary relation are [ADR-0014](ADR-0014-session-task-relations-belong-to-the-graph.md). |

## Related Information

- `docs/adr/ADR-0010-deleting-a-task-conversation.md` — the delete a conversation row offers
- `docs/adr/ADR-0014-session-task-relations-belong-to-the-graph.md` — where a conversation's task
  relations live, the primary relation, and the set the row menu now edits
- `docs/adr/ADR-0006-studio-navigator-ownership-and-shape.md` — the section set this amends
- `docs/adr/ADR-0008-navigator-section-sizing.md` — the sizing and deepest-section rule this section joins
- `docs/ui-spec/studio-information-architecture-ui-spec.md` — the surface this extends
- `studio/src/studio/workbench/TaskSessionsDisclosure.tsx` — the existing conversation row
- `studio/src/studio/workbench/WorktreeTasksPanel.tsx:292-301, :399` — the existing load and grouping
- `src/ApiHost/WorkbenchApiModels.cs:1850` — the per-device conversation list
