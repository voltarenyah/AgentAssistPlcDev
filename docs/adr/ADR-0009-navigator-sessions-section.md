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
- **Every navigator section row owes a 3-dots menu** (UI Spec AC-008), so a session row needs one too.
- **The deepest section grows into the dock's remaining height** (ADR-0008), so a fifth section becomes
  the one that grows and takes that role from `TASKS`.

## Decision Point

- **Question**: where does a task's conversation live in the navigator, and which conversations does
  the section show?
- **Why a decision exists**: at least three placements fit the repository and each implies a different
  section model, scroll owner, and row contract, so the choice cannot be settled inside the component.
- **Scope boundary**: the navigator's treatment of a task's conversations — their placement, the
  content rule, the header action and the row menu. It does not change how a conversation is created,
  stored, bound to a task, or rendered in the task surface, and it does not add a session-level scope.

## Decision

A fifth navigator section, `SESSIONS`, sits below `TASKS`. It lists the conversations bound to the
selected target's tasks, grouped under the task that owns them. A conversation bound to no task is not
listed. Its header starts a new conversation for the selected target: it binds to the worktree's active
task when that task belongs to the target, and otherwise asks which of the target's tasks to use,
because a conversation must be bound to a task to appear here at all. The action is not offered for the
worktree's hardware, which cannot own a conversation, nor for a target that has no task to bind to.

### Decision Details

| Item | Content |
|------|---------|
| **Decision** | A fifth flat section listing the selected target's task-bound conversations, grouped by task, with a creation action in its header and a per-row menu whose operations ADR-0010 governs. |
| **Header action** | Starts a conversation for the selected target. It resolves the task the conversation must bind to as the worktree's active task when that task belongs to the target — the same default the create route already applies when no task is named — and otherwise asks the user to choose among the target's tasks. |
| **Why this** | It keeps the cascade one section per scope level, keeps every list bounded and scrollable on its own, and reaches the conversations from the same place the tasks are reached, without reopening the nesting ADR-0006 removed. |
| **Known unknowns** | Whether a conversation ever needs to be reachable without its task, and whether the section needs a cap once a device accumulates many conversations. |
| **Reconsider when** | Users ask to see or manage conversations that belong to no task, or a worktree's conversation count makes a single list impractical. |

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
| Only conversations bound to the selected target's tasks, grouped by task | Answers "which conversations is this task carrying", which is what the section is for; unbound conversations are already visible in the device chat surface | A worktree can hold conversations that the section does not show, which needs saying in the empty state |
| Also list unbound conversations in their own group | Shows every conversation the device holds | Widens the section beyond the task it is named for, and duplicates the device chat surface's own list |
| Every conversation in the worktree, ignoring the target | One list to scan | Stops being target-scoped, so it would contradict the cascade it sits in |

**Selected**: the first, on the user's decision. The empty state names the rule rather than leaving the
user to guess why a conversation they know about is absent.

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
- Grouping by task means the section states the task-to-conversation relationship the user asked about,
  rather than presenting an undifferentiated list.

### Negative Consequences

- The navigator's cascade is no longer the four sections ADR-0006 fixed, so that ADR's shape needs this
  amendment rather than standing alone.
- A worktree can hold conversations the section does not show, because a conversation bound to no task
  is out of scope by decision; the empty state has to say so or the absence looks like a bug.
- Selecting the hardware row leaves the section with nothing to show, because a hardware task cannot own
  a conversation at all.
- The section's content is a third level below a task, so it depends on the tasks having loaded; its
  empty state can mean "no tasks", "no bound conversations" or "still loading", which the display detail
  has to distinguish.

### Neutral Consequences

This section itself adds no route, entity kind, relation kind, or persisted shape. The task surface keeps rendering its
own conversations exactly as it does today, and a conversation is still created and bound through the
operations that already exist.

## Architecture Impact

`WorkbenchNavigator` gains a section, and `MainStudio` gains the load and the callbacks it needs — one to
open a conversation, one to start one. The session data itself is already available per device, so
showing the conversations involves no API, graph, or storage change; the delete those rows offer is a
separate decision with its own architecture impact (ADR-0010).

## Implementation Guidance

Load the conversations the way the task surface already does: fan out the existing per-device list over
the selected worktree's devices and group by `taskId`, rather than adding an endpoint or fetching each
task's detail. Reuse `TaskSessionsDisclosure`'s row content and its relative-time formatting for the row
itself, but keep the rows always visible: the section is the container, so a disclosure inside it would
be a second, redundant expander. A row menu offers only the operations the repository performs — open,
rename, and delete — and the delete follows ADR-0010 rather than being reimplemented here.

## Related Information

- `docs/adr/ADR-0010-deleting-a-task-conversation.md` — the delete a conversation row offers
- `docs/adr/ADR-0006-studio-navigator-ownership-and-shape.md` — the section set this amends
- `docs/adr/ADR-0008-navigator-section-sizing.md` — the sizing and deepest-section rule this section joins
- `docs/ui-spec/studio-information-architecture-ui-spec.md` — the surface this extends
- `studio/src/studio/workbench/TaskSessionsDisclosure.tsx` — the existing conversation row
- `studio/src/studio/workbench/WorktreeTasksPanel.tsx:292-301, :399` — the existing load and grouping
- `src/ApiHost/WorkbenchApiModels.cs:1850` — the per-device conversation list
