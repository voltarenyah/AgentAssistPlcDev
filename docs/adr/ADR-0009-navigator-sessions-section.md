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

A worktree can also accumulate more conversations than any device-scoped cascade can present: they are
one directory, they outlive the task they were recorded against, and a worktree that is lived in has
dozens of them. A worktree surface therefore carries its own conversation list — the `Sessions` tab of
the worktree's tab strip — read from a worktree-level list route. That surface is not a scope in this
cascade and does not replace the section: it is what a user browses and searches a worktree's
conversations from, and the section stays what the currently selected scope is working in.

The left navigator does not show them at all. Its cascade is the four sections ADR-0006 fixed —
`PROJECTS`, `WORKTREE`, `DEVICE`, `TASKS` — and a task's conversations are reachable only by opening a
task's card or detail view. The user asked for a section below `TASKS` that shows the conversations
each task is carrying.

Repository evidence that constrains the choice:

- **No worktree-level session list existed when this section was decided.** Sessions were listed per
  device (`GET …/worktrees/{wt}/devices/{device}/sessions`, `WorkbenchApiModels.cs:1850`) and per task
  (the task detail's `sessions`), so a worktree-wide list needed either a new endpoint or a fan-out over
  the worktree's devices, which the task panel already performs
  (`WorktreeTasksPanel.tsx:292-301`). The endpoint was added with the worktree surface's own
  conversation tab: `GET …/worktrees/{wt}/sessions` reads the worktree's session directory itself, which
  is the one list that can also reach a conversation whose header names no device.
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

A fifth navigator section, `SESSIONS`, sits below `TASKS`. Its membership rule is the conversation's
**assignment** — the primary relation, at most one per conversation — and never its relation set. It
shows the conversations assigned to the task the user has selected in `TASKS`. While no task is
selected and a PLC device is the selected target, it shows **every** conversation that device owns,
grouped by assignment: the conversations working on no task first, under `No task`, then one group per
task that at least one of them is working on. A conversation related to several tasks is therefore
listed once, under the one it works on, and a conversation related to tasks but assigned to none is
listed under `No task` — which is what "no task" means here, not "no relation". Its header
starts a new conversation in the scope the list itself is showing: bound to the selected task, or —
while no task is selected — the device's own and working on no task, which is exactly the state the
list is in then. The action is never offered for the worktree's hardware, which cannot
own a conversation. Each row's menu carries every operation the repository performs on a conversation —
open, rename, export, edit the set of tasks it is related to and the one it is assigned to, and delete
under ADR-0010 — because the row, not the removed dock page, is now their only entry point.
Opening a row shows that conversation and changes nothing else: the navigator keeps the selection the
row was listed under, so the list the user just read stays where it was.

The section is present whenever a PLC device is the selected target, including when the list it would
show is empty, and it says which of the two states it is in. Its absence previously meant "this task
owns no conversation", "this device has none that no task owns" and "the tasks are still loading" at
once, and it was the only place a device's conversations could be started or reached from.

Selecting a device, the hardware row, a worktree or a workbench names no task, so it clears the task
selection: that state is the selected device's whole conversation list, which is what the section shows
until a task row is picked. The open task detail yields with it, because the detail renders ahead of
every other view and would otherwise show nothing of the scope the user picked while keeping the task it
names as the selection. That reset is what keeps a conversation working on no task reachable once any
task has been opened, and a task that owns no conversation therefore no longer takes the whole section
off screen behind it.

### Decision Details

| Item | Content |
|------|---------|
| **Decision** | A fifth flat section listing the conversations assigned to the selected task, or — while no task is selected and a device is the selected target — every conversation that device owns, grouped by the task each one is assigned to, with a creation action in its header and a per-row menu carrying every operation the repository performs on a conversation. Opening a row is a content action: it does not change what the navigator is showing. |
| **Membership** | The conversation's assignment — its primary relation, at most one (ADR-0014) — and never its relation set. A conversation related to several tasks is listed once, under the one it is working on; a conversation related to tasks and assigned to none is listed under `No task`. So `No task` means "working on no task", not "related to no task", and no conversation can be listed twice. |
| **Header action** | Starts a conversation in the scope the section is showing: bound to the selected task, or the selected device's own and working on no task while none is selected. Never offered for the hardware target, which cannot own a conversation. A task that owns no conversation starts its first one from its own detail page, whose `Sessions` header carries the same action. |
| **Presence** | Rendered whenever a PLC device is the selected target, including with nothing to list, where it says so. Absent for the hardware target, which cannot own a conversation, and for a worktree or project whose own row is the deepest selection, which names no device to scope a list to. |
| **Target selection** | Selecting a device, the hardware row, a worktree or a workbench clears the task selection, so the section returns to the selected device's whole conversation list. Activating the target that is already selected clears it too, because it is the same statement about the scope. The open task detail closes with the selection, so the scope the user picked is what the main area shows. |
| **Row menu** | The conversation's only entry point, so it holds open, rename, export, edit its task relations and its assignment, and delete. The picker is a searchable list over the worktree's tasks that can own a conversation — never a prompt for a raw task id — with one check per task, a click setting that relation and a second click clearing it, plus an `Assigned task` control naming the checked task the conversation works on or `None`. One apply writes the set and the assignment, so the conversation is never half-linked and its assignment is never chosen for it (ADR-0014). |
| **Row open** | Opens the conversation in the chat view of the scope that is already selected, and leaves the workbench, worktree, device and task selection untouched. A conversation whose device is not the selected one — which the worktree's own task surface can ask for — has no device workspace to open in, so it opens in the worktree-level chat view, the one scope without a device. |
| **Row contents** | The section is a view of the device's conversation list, not a copy of it: it is re-read wherever that list changes, so a conversation renamed, deleted or re-bound in any surface is reflected in the section without a reload. Its menu is visible without hovering, like the navigator's other row menus. |
| **Worktree conversation tab** | The worktree surface's tab strip carries a `Sessions` tab listing every conversation the worktree holds, in the cards/list duality its `Tasks` tab already offers, with a search over them and the same row operations. It reads `GET …/worktrees/{wt}/sessions`, not a device's list, so a conversation whose header names no device is reachable there and nowhere else. It complements this section rather than replacing it: the section is what the selected scope is working in, and the tab is what the worktree holds. |
| **Why this** | It keeps the cascade one section per scope level, keeps every list bounded and scrollable on its own, and makes the list say exactly what the row above it says is selected, without reopening the nesting ADR-0006 removed. |
| **Known unknowns** | Whether the section needs a cap once a device accumulates many conversations or many groups, and whether a conversation related to several tasks needs an indication of that in its row. |
| **Reconsider when** | A conversation has to be reachable without naming its device at all — a worktree-wide list, which is the worktree surface's own concern and not this cascade's, whose every section is scoped by the row above it. The two conditions this list carried before were met and answered: conversations belonging to several tasks at once by ADR-0014 v1.0, and reaching a conversation without selecting the task that owns it by v1.5 above. |

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
| The selected task's conversations; the device's task-less conversations while no task is selected (the rule from v1.0 to v1.4, replaced) | Answers "what is this task carrying", which is what the section is for, and keeps the answer true when no task is selected | Two rules to state, and a device's task-bound conversations are out of reach from the navigator unless their own task is selected |
| The selected target's task-bound conversations, grouped by task (the rule as first decided, replaced) | Shows every conversation the target holds without needing a task selection | Lists conversations of tasks the user is not working on, so selecting a device shows a list unrelated to the selected task |
| Only the selected task's conversations, with nothing when no task is selected | One rule, and the section is only ever about one task | A worktree's task-less conversations stay unreachable from the navigator, and the live `master` worktree is exactly that case |
| Every conversation in the worktree, ignoring the target | One list to scan | Stops being target-scoped, so it would contradict the cascade it sits in |
| The selected task's conversations while one is selected; otherwise every conversation of the selected device, grouped by the task that owns each (v1.5 to v1.6, replaced) | Answers "what is this task carrying" while a task is selected, and makes the device's whole list reachable — including task-bound conversations whose task the user has not opened — when the device is what the user picked | Groups by the relation set, so a conversation related to several tasks is listed under each of them: the same row read several times in one section, and a task heading for a task no conversation is working on |
| The selected task's conversations while one is selected; otherwise every conversation of the selected device, grouped by assignment, with `No task` for the conversations working on none (current) | Answers "what is this task carrying" while a task is selected, and makes the device's whole list reachable when the device is what the user picked | The section can show more than one group, and its content changes with the task selection |

**Selected**: the last, on the user's decision after living with the one before it. Membership is the
assignment rather than the relation set, because a conversation works on at most one task (ADR-0014):
every conversation is in exactly one group, so the section can never show the same conversation twice,
and `No task` states what the user actually means by it — this conversation is not working on a task —
rather than what the graph happened to record. Each group's heading names the task its conversations are
working on, or that they are working on none, so the list never has to be guessed at.

A conversation whose assignment names a task that no longer resolves is grouped under that id rather
than silently folded into `No task`: the assignment is the graph's, this section does not get to decide
it is gone, and a group named by an id is still reachable, which is the property this rule exists to
give.

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
- A device's whole conversation list is reachable from the one surface that lists it: selecting the
  device shows every conversation it owns, so a task-bound conversation does not need its own task row to
  be found first, and the user does not have to know which task a conversation was recorded against.
- It stays reachable after a task has been opened: selecting a target names no task, so the device's
  whole list is one selection away, and a task that owns no conversation can no longer hide the section
  behind an empty list of its own.
- The section is never absent while a device is selected, so its presence no longer has to be
  interpreted: an empty list says so in words, and the header action that starts a conversation is always
  there to be used.
- Reading a conversation leaves the navigator where it was, so the list a row was read from is still
  there when the user comes back to it, and the task selection is not lost with the task detail.
- Retiring the right dock's "AI sessions" page loses no capability: export and task binding, which only
  that page offered, are row-menu operations now, and binding asks for a choice from the worktree's
  tasks instead of requiring a task id the user had to know.
- A conversation related to several tasks is listed by each of them, so a finding recorded in one
  conversation is reachable from every task that conversation produced, and both surfaces that list
  conversations read the same relation (ADR-0014). *Amended by v1.7: this is what the task page's own
  `Sessions` list is for. The section lists by assignment, so the same conversation is one row here.*
- Every conversation appears exactly once, under the task it is working on or under `No task`, so a
  section that shows a device's whole list can still be read as a list of distinct conversations.
- `No task` means what a user means by it: this conversation is not working on a task. A conversation
  that recorded tasks without being assigned one is found there rather than under a task it never
  worked on.

### Negative Consequences

- The navigator's cascade is no longer the four sections ADR-0006 fixed, so that ADR's shape needs this
  amendment rather than standing alone.
- The section is no longer "one thing at a time" while a device is the selection: it shows that device's
  whole list as several groups, so a device with many conversation-owning tasks is a longer scroll than
  the single list it replaced, and the deepest section's share of the dock is what bounds it.
- A conversation's relations are no longer visible in this section at all. A conversation related to a
  task it is not working on is not listed under that task, so "which conversations did this task
  produce" is answered by the task page's own `Sessions` list, not here.
- Selecting the hardware row leaves the section with nothing to show, because a hardware task cannot own
  a conversation at all, and the device scope the grouped rule needs does not exist there either.
- A conversation whose relation names a task the worktree no longer holds is grouped under that task id,
  so its heading is an id rather than a title. Keeping it listed is the point — the alternative is a
  conversation with no surface — but the heading is only as good as the relation it is built from.
- While the worktree's tasks are still loading, a conversation related to one of them is grouped under
  its id for that moment; the group is renamed by the title as soon as the list arrives.

### Neutral Consequences

This section itself adds no route, entity kind, relation kind, or persisted shape. The task surface keeps rendering its
own conversations exactly as it does today, and a conversation is still created, bound, exported and deleted through the
operations that already exist — the row menu only gives them their entry point back where the retired dock page held it.

## Architecture Impact

`WorkbenchNavigator` gains a section, and `MainStudio` gains the load and the callbacks it needs — open,
rename, export, bind, delete, and one to start a conversation. The session data itself is already
available per device, so showing the conversations involves no API, graph, or storage change; the delete
those rows offer is a separate decision with its own architecture impact (ADR-0010). The retired dock
page takes its `sessions` content kind with it, so `contextDock` no longer has a sessions content to
resolve. That the dock then resolved a device on a chat or source view to *no dock at all* was
superseded on 2026-10-08 by `ADR-0015`: the right dock is a shell-owned rail that exists on every
surface, and `contextDock` derives each of its pages' content instead of the dock's visibility.

The worktree surface's own conversation tab adds one read-only route, `GET
/api/workbenches/{wb}/worktrees/{wt}/sessions`, which lists the worktree's session directory and
projects the same graph relations the per-device list does. It is additive: no existing route changes,
nothing persisted changes, and the delete, rename and relation operations keep going through the
device-scoped routes they already use. `WorktreeSessionsPanel` owns its own load, as the worktree's task
panel does, and the conversation operations it shares with the navigator's rows live in one place
(`studio/src/studio/workbench/SessionOperations.tsx`) so two surfaces cannot offer diverging copies of
them.

## Implementation Guidance

Load the conversations the way the task surface already does: fan out the existing per-device list over
the selected worktree's devices, then select from that fan-out by the task the `TASKS` section shows as
selected — the task whose detail is open, or the task row last clicked, the same expression that
highlights the row — and, when that selection is empty, group the selected device's whole list instead:
the conversations no task is related to first, then one group per task that owns one, which is the same
list the device's own section was always reading, read in full. Keep the device on each loaded
conversation, because a row's open, export, re-binding and
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

Membership in that list is the conversation's assignment, not its relation set (ADR-0014): a
conversation is in the section while the selected task is the one it is working on, and `No task` is
the conversation working on none — a conversation related to tasks without being assigned one belongs
there, and one assigned to a task belongs only to that task. The device list therefore has to carry the
primary relation of each conversation as well as its relations, and the projection that fills it
belongs wherever both the graph and the conversation file are reachable.

## Update History

| Date | Version | Changes |
|---|---|---|
| 2026-10-02 | 1.0 | The fifth `SESSIONS` section, its content rule, the header action bound to the selected task, and the row menu with open, rename and delete. The earlier revisions of those decisions are recorded in `docs/design/studio-navigator-sessions-design.md`. |
| 2026-10-03 | 1.1 | The right dock's "AI sessions" page is retired, so this section becomes the only surface listing a device's conversations. Its row menu gains the export and task binding operations that page alone offered — binding from a picker over the worktree's tasks rather than an id prompt — and its header action starts a conversation in the scope the section is showing, including the device-scoped, task-less state, instead of only while a task is selected. |
| 2026-10-05 | 1.2 | The task selection is cleared whenever the selected target changes, and activating the target that is already selected clears it too: a target selection names no task, so the section returns to the selected device's task-less conversations and a conversation no task owns stays reachable after a task has been opened. The open task detail closes with that selection, so the picked scope is what the main area shows. A task that owns no conversation starts its first one from its own detail page, whose `Sessions` header carries the section's creation action — closing the first of the two gaps the negative consequences recorded. |
| 2026-10-08 | 1.3 | A conversation may be related to several tasks, so the row menu's binding becomes a set: a searchable picker with one check per task, where a second click clears that binding and the set is applied in one operation. The content rule is unchanged — the section still lists the selected task's conversations, and a conversation several tasks share is now listed by each of them — and the task-less case remains the conversation with no relation at all. This answers the first condition this ADR recorded under **Reconsider when**; the relation itself, its ownership by the engineering graph and the primary relation are [ADR-0014](ADR-0014-session-task-relations-belong-to-the-graph.md). |
| 2026-10-08 | 1.4 | The amendment's clause that a device on a chat or source view resolves to no dock at all is superseded by `ADR-0015`: the right dock is a shell-owned icon rail that exists on every surface, so this section is still the only surface that *lists* a device's conversations, but its absence is no longer what the dock expresses. |
| 2026-10-09 | 1.5 | A selected device no longer shows only its task-less conversations: the section shows every conversation the device owns, grouped by the task each one is related to, with the conversations no task owns first. The task selection keeps its single list, and selecting a device — which names no task — is what widens the list. The section is now rendered whenever a PLC device is the selected target, including with nothing to list, where it says so, so its presence no longer has to be interpreted and its creation action is always available. This answers the section's second recorded gap and the second condition under **Reconsider when**. |
| 2026-10-09 | 1.6 | The worktree surface gains its own conversation list: a `Sessions` tab beside `Tasks`, in the same cards/list duality, with a search and the same row operations, reading a new read-only `GET …/worktrees/{wt}/sessions`. It is the surface for a worktree that has accumulated many conversations, and the only list that can reach a conversation whose header names no device — which the per-device routes must keep omitting. This section stays the scope-scoped list; the tab is the worktree's. The row operations are now held once, in `SessionOperations.tsx`, and used by both. |
| 2026-10-10 | 1.7 | Membership becomes the conversation's **assignment** instead of its relation set, so the section lists a conversation under the one task it is working on and never under every task it is related to. `No task` therefore means "working on no task": a conversation related to tasks and assigned to none is listed there, which is the state a conversation is left in when it recorded findings as tasks without ever being assigned one. Grouping by the assignment also makes every conversation appear exactly once, which is what the section needed once it showed a device's whole list: a conversation related to several tasks used to be listed under each of them. The task page's own `Sessions` list remains the place a task's produced conversations are read from. Decided by the user on 2026-10-10, after living with v1.5. |

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
