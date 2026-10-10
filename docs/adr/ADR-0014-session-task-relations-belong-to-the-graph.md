# ADR-0014 Session-task relations belong to the engineering graph

## Status

Accepted

## Context

A conversation and a task are related, and the repository records that relation **twice**: the session
file's header carries `taskId` and `taskProvenance` (`src/Agent/Chat/SessionFileFormat.cs:18-19`,
projected into `ChatSessionInfo` by `SessionManager.ReadSessionInfo`), and the engineering graph
carries a `task_session` edge (`src/ApiHost/EngineeringGraphApi.cs:151-172`). `SessionGraphOperations`
is the single writer that keeps the two in step, and ADR-0010 fixes the delete order between them.

That relation is one-to-one **by code, not by storage**. `graph_edges` has no constraint that could
prevent a conversation from holding several task edges: its uniqueness rule is
`UNIQUE (from_kind, from_id, to_kind, to_id, relation_kind)`
(`src/Agent/Workbench/EngineeringGraph/EngineeringGraphSchema.cs:71`), and the only partial index is
commit-specific (`ux_graph_edges_primary_git_commit`, `:91-94`). The one-to-one lives in four places:

- `ReplaceSessionTask` deletes every edge into the session before inserting one
  (`src/Agent/Workbench/EngineeringGraph/EngineeringGraphService.cs:1044-1058`);
- `ReplaceTaskRelationship` deletes every `task → session` edge for a session target (`:306-310`);
- `ApplyWithPersistence` reads a session's incoming edges with `SingleOrDefault()`
  (`src/ApiHost/EngineeringGraphApi.cs:138`);
- and every read shape exposes one id: `ChatSessionInfo.TaskId`, filtered with
  `session.taskId === task.taskId` in the navigator
  (`studio/src/studio/workbench/WorkbenchNavigator.tsx:695`) and in the worktree task surface
  (`studio/src/studio/workbench/WorktreeTasksPanel.tsx:350`).

The relation is also unusable in the case that produces it. A device conversation with no task can
record findings as tasks — that is `create_task` (`src/ApiHost/TaskCreationTool.cs`) — and the tool
cannot know which conversation called it: it is constructed with a device provider only
(`src/ApiHost/CompatibilityEndpoints.cs:1009-1011`), and `Create` (`TaskCreationTool.cs:80-113`) writes
the task and nothing else. A conversation that created three tasks from its own findings is therefore
related to none of them, and none of the three lists that conversation. The user reported exactly this
case, from the conversation's own screenshot of the `SESSIONS` section.

Two accepted decisions bound the choice:

- ADR-0011 makes the engineering graph the read model for engineering relationships, and the task page
  already reads its `Sessions` section from graph edges (`src/ApiHost/WorkbenchApiModels.cs:2523`), so
  the relation already has a graph-native reader and a graph-native display.
- ADR-0009 shows a device's conversations in the navigator and names the condition that reopens its
  content rule: *"Users ask to see conversations belonging to several tasks at once, or to reach a
  conversation from the navigator without selecting the task that owns it"*
  (`docs/adr/ADR-0009-navigator-sessions-section.md:111`). This requirement meets that condition, so
  ADR-0009 is amended by this decision rather than contradicted by it.

Finally, the graph cannot currently say which of several related tasks a conversation is *working on*:
`AddEdge` refuses `is_primary` for any relation other than `task → commit`
(`EngineeringGraphService.cs:265-266`), and `ReplaceTaskRelationship` refuses it too (`:300-301`).

The relevant read and write paths, measured in this session:

| Fact | Evidence |
|---|---|
| The graph permits several `task → session` edges | `EngineeringGraphSchema.cs:71`, `:91-94` |
| But no code path creates them | `EngineeringGraphService.cs:1044-1058`, `:306-310`, `EngineeringGraphApi.cs:138` |
| A conversation's task is written to the file header *and* to the graph | `WorkbenchApiModels.cs:2019-2021`, `:2031-2033`; `CompatibilityEndpoints.cs:497-499` |
| A graph session entity is registered on create, not per turn | `WorkbenchApiModels.cs:1988`, `:2227`; `CompatibilityEndpoints.cs:481` |
| Implicit sessions have no graph entity at all | `CompatibilityEndpoints.cs:942-944` (`EnsureActiveChatAsync`) |
| The device chat's session is swapped in place, so a captured id would go stale | `CompatibilityEndpoints.cs:859-872`, `:1145-1162` |
| The task page reads its conversations from the graph | `WorkbenchApiModels.cs:2512-2527` |
| The session list reads files only, with no graph access | `src/Agent/Chat/SessionManager.cs:44-71` |

## Decision Point

- **Question**: where do a conversation's task relations live, which store is authoritative, and how is
  "the task this conversation is working on" expressed once a conversation relates to several?
- **Why a decision exists**: at least two materially different models fit the repository — the session
  file keeps the relation with the graph as a mirror (today's shape, extended from one id to a set), or
  the graph becomes its only owner and the file stops carrying it. They differ in persisted format, in
  what every session-list read has to open, and in whether the two stores can disagree at all.
- **Scope boundary**: the ownership, shape, provenance and migration of the conversation↔task
  relation. It does not change how a conversation is created, renamed or deleted, changes no task
  field, does not move the relation's entry points in the UI (ADR-0009 owns those), and excludes the
  Workbench Assistant's own conversation.

## Decision

Decided by the user on 2026-10-08:

1. **The graph is the only authority.** A conversation's task relations are `task_session` edges and
   nothing else. The session file's `taskId`/`taskProvenance` stop being state: no write path stores
   them any more, and they are read only as a one-time legacy import source the first time a session is
   written after this change.
2. **One primary relation survives.** `taskId` keeps its meaning — "the task this conversation is
   working on" — and is carried by exactly one edge per conversation with `is_primary = 1`.
   Automatic association only adds relations; it never sets a primary. A conversation gains one by
   being created for a task, by importing a legacy header that carried one, or by the user naming it,
   and a conversation that has none stays without one however many relations it accumulates.
   *Amended 2026-10-10: v1.0 read "a conversation whose primary is empty and which gains its first
   relation makes that relation primary", which made the first task a conversation recorded an
   assignment the user never made. See the Update History.*
3. **The Workbench Assistant's conversation is out of scope.** It lives outside the worktree session
   store (`%LOCALAPPDATA%\AutomationWorkbench\assistant\session.json`), is not a graph session entity,
   and is not listed by the `SESSIONS` section, so a relation from it would put a row on a task page
   that no conversation list could resolve to a title or an open action.
4. **The model sees one task in full.** Per turn the primary relation's task is injected as it is today
   — title, type, status, goal, expected result and description. Every other related task is named by
   title and status only. Reading another related task's full content on request is a later,
   separately decided affordance, not part of this decision.

These consequences are fixed with them, because the decision is not implementable without them:

- **A primary `task_session` edge becomes expressible and unique.** The graph schema gains a partial
  unique index mirroring `ux_graph_edges_primary_git_commit`, and `AddEdge`/`ReplaceTaskRelationship`
  stop refusing `is_primary` for this relation only (`EngineeringGraphService.cs:265-266`, `:300-301`).
- **`GraphProvenance` gains `auto`**, for a relation a conversation's own tool call established:
  `manual` is a choice made in the UI, `default` is the task the conversation was created for, `auto`
  is the relation the conversation created by doing the work. The task page's existing "a manual
  relation may be removed" rule is widened to cover `auto` as well, or an automatic relation could
  never be cleared.
- **The relation set is projected where both stores are reachable.** `SessionManager.ListSessions`
  keeps reading files; ApiHost projects the graph's relation set into `ChatSessionInfo`, where the
  existing `taskId`/`taskProvenance` become the projected primary link (so every current reader keeps
  working) and a new collection carries all relations with their provenance.
- **The model's task context is read from the graph per turn**, not from the session header.

## Decision Details

| Item | Content |
|------|---------|
| **Decision** | A conversation's task relations are graph edges: zero or more `task → session` edges with `relation_kind = task_session`, at most one of them primary. The session file no longer carries the relation as state. |
| **Authority** | The engineering graph only. No write path stores the relation in the session file; the file's legacy `taskId` is read once, when that session is next written, and imported into the graph if the session has no relation yet. |
| **Primary** | At most one relation per conversation may carry `is_primary = 1`, enforced by a partial unique index, and a conversation may carry none. It means "the task this conversation is working on" and is what the model's task context, and only that, is built from. The picker writes it as a statement of its own, so an apply can name a task or name none; the set write's own resolution — keep the current primary while it is still related, otherwise take the set's first task — is what a caller that names nothing at all gets, not what the picker sends. |
| **Automatic association** | A conversation's own tool call that creates a task adds a relation with provenance `auto`, and that is all it does: the primary is neither moved nor established, so a conversation that had none keeps none. Being assigned to a task is the user's statement about the conversation — made when it is created for a task, restored from a legacy header, or named in the picker — never a side effect of the conversation recording what it found. |
| **Out of scope** | The Workbench Assistant's conversation; relations to a task that cannot own a conversation (no device, or project scope); any task mutation other than creation. |
| **Migration** | Existing `task_session` edges are preserved; schema version 7 promotes the edge of every conversation that has exactly one to primary. Legacy file headers are imported on the session's next write, never by a read. |
| **Reconsider when** | A relation needs its own attributes (for example a per-relation note or an ordering); the Workbench Assistant should join the relation; or the model needs the full content of more than one related task per turn. |

## Rationale

### Options Considered

| Option | Requirement and repository fit | Current-scope benefit | Lifecycle cost | Maintainability | Material trade-offs |
|---|---|---|---|---|---|
| A. The file header holds the relation set; the graph mirrors it | Follows today's shape (`SessionGraphOperations` already writes both) and keeps the per-device session list a pure file read | The session list keeps working without opening the graph at all | Two stores can still disagree, now about a *set*: a conversation can appear under a task the task page does not list | One more shape to keep in step forever, with no single reader to compare against | The failure the user would see most often — a conversation in `SESSIONS` that the task page does not list — stays possible |
| **B. The graph is the only authority** (selected) | ADR-0011 already declares the graph the read model for relationships, and the task page already reads its conversations from it (`WorkbenchApiModels.cs:2512-2527`) | One owner, so the navigator's list and the task page's list cannot disagree; a second relation is an insert, not a redesign | Every session-list route now opens the workbench graph for one extra query; `ChatSessionInfo` is no longer self-describing from the file alone | One write path, one read projection, and the relation is inspectable in the same place as every other engineering fact | A session-list read depends on the graph database being openable; a damaged graph degrades the relation display rather than failing the list |
| C. The file header holds the relation; graph edges are dropped | No second store at all | Simplest possible write path | The task page's `Sessions` section has no reader, and ADR-0011's read model is bypassed for this one fact | Contradicts an accepted decision and strands an existing surface | Rejected: it deletes a working read path to avoid a projection |

**Selected**: B. The relation is an engineering fact, and an engineering fact the task page already
reads from the graph has no reason to live in a chat file as well. Option A is the smaller diff, and it
was the first recommendation in this session's assessment; it was rejected by the user because it keeps
the one failure mode that is hardest to see — two stores that disagree about the same relation.

## Consequences

### Positive Consequences

- A conversation can belong to several tasks, and each of those tasks lists it, from the one relation
  store both surfaces read.
- The drift the two-store shape allowed is gone for this relation: there is exactly one writer and one
  reader-side projection, so the navigator's list and the task page cannot disagree.
- The relation gains a place to say *which* task a conversation is working on, and a provenance that
  distinguishes a UI choice, the conversation's creation context, and the conversation's own work.
- Deleting a conversation or a task keeps one cleanup rule instead of two (ADR-0010's ordering).

### Negative Consequences

- Every session-list read now opens the workbench graph for its relations, so a list that was a pure
  directory scan depends on the graph being readable. The design must state what the list shows when
  the graph cannot be opened.
- `ChatSessionInfo` stops being derivable from the session file alone: the projection lives in ApiHost,
  and `SessionManager.ListSessions` alone is no longer a complete answer.
- The session file keeps a legacy `taskId` field whose only reader is a migration path. Left
  undocumented, a future reader can mistake it for state — which is why this ADR names it.
- A graph rebuild or a workbench whose `.automation/engineering.db` is deleted loses the relations
  while the conversation files survive, which is a new way for the two to look inconsistent. The
  legacy header import is what recovers the primary relation; the non-primary ones are not recoverable
  from a file that never stored them.

### Neutral Consequences

No new entity kind and no new relation kind: `task_session` and `GraphEntityKind.Session` are reused.
The relation's entry points in the UI are unchanged by this ADR; the row menu that operates on them is
amended in ADR-0009.

## Architecture Impact

`EngineeringGraphSchema` gains version 7 (a partial unique index plus the primary promotion).
`EngineeringGraphService` gains set-shaped relation writes and a session-relation read.
`SessionGraphOperations` stops being a two-store writer and becomes the graph-side relation API.
`SessionManager` stays unaware of the graph, and ApiHost owns the projection into `ChatSessionInfo`.
The device chat's tool catalog gains the conversation's own identity so `create_task` can establish the
relation, which is the one place the agent's context and the graph meet.

## Implementation Guidance

Read the relation set with one query over `graph_edges` for the sessions in the page
(`relation_kind = 'task_session'`, `to_kind = 'session'`) rather than one query per session, and project
it into the response type; the in-memory session must never be the carrier. The session's identity for
the automatic relation must be resolved live from the active chat for that scope key, because the chat
is swapped in place without rebuilding the loop or the tool catalog
(`src/ApiHost/CompatibilityEndpoints.cs:859-872`). A relation write and its response projection belong
to one operation, so a client cannot observe a half-linked conversation. Whatever a list route shows
when the graph cannot be opened must be stated and tested; the conversation list itself must stay
usable.

## Update History

| Date | Version | Changes |
|---|---|---|
| 2026-10-08 | 1.0 | The relation moves to the graph as its only owner; one primary relation per conversation; automatic association on a conversation's own task creation; the Workbench Assistant's conversation excluded; the model sees the primary task in full and the others by title and status. Decisions 1-4 were made by the user on 2026-10-08. |
| 2026-10-10 | 1.1 | Automatic association no longer establishes a primary. A conversation that had none and created two tasks ended up assigned to the first of them, which the turns that followed rendered as `Active task: …` — an assignment the user never made. Being assigned a task is now only ever the user's statement: it arrives with a conversation created for a task, restored from a legacy header, or named in the picker, and a conversation may carry no primary however many relations it has. The set write gains the caller's own "assigned to none" (`unassigned`), because naming no primary on its own means "resolve one" — and the picker gains the control that sends it, so an assignment can be taken back without deleting the relations. Decided by the user on 2026-10-10. |

## Related Information

- `docs/adr/ADR-0009-navigator-sessions-section.md` — the surface that lists a task's conversations, and
  the row menu this decision changes; its "reconsider when" condition is what this ADR answers
- `docs/adr/ADR-0010-deleting-a-task-conversation.md` — the delete ordering between the two stores
- `docs/adr/ADR-0011-engineering-graph-as-the-read-model.md` — the graph as the read model this follows
- `docs/design/session-task-relation-design.md` — the implementation design under this decision
- `src/ApiHost/TaskCreationTool.cs` — the tool whose call establishes the automatic relation
- `src/ApiHost/EngineeringGraphApi.cs` — `SessionGraphOperations`, the writer this decision relocates
