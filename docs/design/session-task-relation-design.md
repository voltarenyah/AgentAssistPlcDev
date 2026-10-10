# Design Document: Session-task relations as engineering-graph edges

## Overview

- **Outcome**: a conversation's relation to a task is a `task_session` edge in the workbench's
  engineering graph and nothing else; a conversation may hold several, at most one of them primary
  ("the task this conversation is working on"); the conversation's own `create_task` call relates it to
  the task it created; every session-list response carries the relation set, so each related task lists
  the conversation and the navigator's `SESSIONS` list is set membership; and the row menu's binding
  becomes a checked set applied in one request.
- **Scope**: the graph schema and its relation writes
  (`src/Agent/Workbench/EngineeringGraph/EngineeringGraphSchema.cs`, `EngineeringGraphService.cs`), the
  relation API and its projection in ApiHost (`src/ApiHost/EngineeringGraphApi.cs`,
  `WorkbenchApiModels.cs`, `CompatibilityEndpoints.cs`), session persistence
  (`src/Agent/Chat/SessionManager.cs`, `SessionFileFormat.cs`), the device chat's task-creation tool
  (`src/ApiHost/TaskCreationTool.cs`), the model's per-turn task context, and the Studio surfaces named
  in the change surface.
- **UI Spec**: `docs/ui-spec/studio-information-architecture-ui-spec.md` — v1.11 (2026-10-08) already
  amends AC-015, AC-017 and AC-019 for a relation set, and its Design Evidence table already names
  ADR-0014. This design implements those criteria; it changes no other criterion.
- **Governing ADRs**: `docs/adr/ADR-0014-session-task-relations-belong-to-the-graph.md` (the authority
  for this task), `docs/adr/ADR-0009-navigator-sessions-section.md` (the surface and the row menu),
  `docs/adr/ADR-0010-deleting-a-task-conversation.md` (the delete ordering between the two stores),
  `docs/adr/ADR-0011-engineering-graph-as-the-read-model.md` (the graph as the read model).

## Requirement Boundary

- **PRD or convergence carrier**: embedded record — ADR-0014's Decision, whose four items were decided
  by the user on 2026-10-08. There is no `docs/prd/`; Structural Scale is Medium (one schema version,
  one new relation read, one new route, four touched surfaces).
- **Current requirements** (binding, each traced to ADR-0014):
  1. The graph is the relation's only authority; no write path stores it in the session file
     (Decision 1).
  2. At most one primary relation per conversation, expressed by `is_primary = 1` on one edge, and a
     conversation may carry none (Decision 2).
  3. A conversation's own tool call that creates a task adds the relation with provenance `auto` and
     nothing else: the primary is neither moved nor established, so a conversation that had none keeps
     none (Decision 2, Decision Details *Automatic association*).
  4. Per turn the primary relation's task is injected as it is today (title, type, status, goal,
     expected result, description); every other related task is named by title and status only
     (Decision 4).
  5. The Workbench Assistant's own conversation is excluded (Decision 3).
  6. The relation set is projected where both stores are reachable, with `taskId`/`taskProvenance` as
     the projected primary link, and the model's task context is read from the graph per turn
     (Consequences).
  Plus the UI-side criteria this design must satisfy: UI Spec AC-015 (set membership; a relation the
  conversation established by creating the task appears without the user binding anything), AC-017
  (the delete confirmation speaks of the links it loses), AC-019 (a checked picker, a second click
  clears, the set is applied in one operation).
- **Non-goals** (boundaries, not preferences):
  - **The Workbench Assistant's conversation does not participate** (ADR-0014 Decision 3): it is not a
    graph session entity and no conversation list can resolve it to a title or an open action.
  - **No new relation entry point to a task that cannot own a conversation.** The UI's picker list stays
    the worktree's device-bound tasks (`WorkbenchNavigator.tsx:707`), and the write path keeps exactly
    the validation it has today (worktree compatibility, device match, task existence) — this design
    adds none and removes none. A project-scope or device-less task therefore stays reachable only where
    it is reachable today.
  - **No task mutation other than creation establishes a relation.** Renaming, re-typing or
    re-status a task is a UI action performed by the user, not by the conversation; it is not an actor
    this relation can name.
  - **No per-relation attributes**: no note, no ordering, no pin. ADR-0014's *Reconsider when* owns the
    first requirement for one.
  - **The model does not gain a read path for another related task's full content** (ADR-0014
    Decision 4, last sentence).
  - **`SESSIONS` still shows one task's list at a time.** Set membership changes which list a
    conversation appears in, not how many lists are on screen (UI Spec explicit exclusions,
    `:14-18`).
  - **The section's heading, absence, hardware exclusion, tag-filter exclusion and deepest-section role
    are unchanged** (ADR-0009 v1.2, ADR-0008).
- **Open requirement fields**: none.

## Acceptance Criteria

Each criterion names the governing source. `AC-0NN` ids below are local to this design; where a UI Spec
criterion exists it is cited by its own id.

- **AC-001** — **When** a graph database at schema version 6 is opened, **the** system **shall** migrate
  it to version 7 by adding the partial unique index on a primary `task_session` edge and promoting the
  `task_session` edge of every conversation that has exactly one, leaving every other edge, task and
  property row untouched. Source: ADR-0014 *Migration*.
- **AC-002** — **If** the version 7 step fails at any point, **then** the database **shall** be left
  byte-identical to its pre-migration state with a `.bak` beside it and the recorded version **shall**
  stay 6. Source: ADR-0014 *Migration*; the store's existing all-or-nothing rule
  (`EngineeringGraphStore.cs:37-48`, `EngineeringGraphStoreTests.cs:26-62`).
- **AC-003** — **When** a task is deleted, **then** every `graph_edges` row that references it
  **shall** be removed in the same transaction as its `tasks`, `task_source_stages` and `graph_entities`
  rows, so no conversation keeps a relation to a task that no longer exists. Source: ADR-0014
  *Migration* ("a many-to-many relation multiplies that dangling reference").
- **AC-004** — **When** a conversation's relation set is written, **the** graph **shall** hold exactly
  the requested `task_session` edges with at most one primary, **shall** preserve the provenance,
  edge id and creation time of every relation it keeps, and **shall** have no path that deletes a
  conversation's other relations as a side effect. Source: ADR-0014 Decision 1, Decision 2.
- **AC-005** — **If** a relation write names a task that is not in the workbench, a worktree-scoped task
  from another worktree, or a task whose bound device differs from the conversation's device, **then**
  the write **shall** be refused with the existing code (`TASK_NOT_FOUND`, the worktree-compatibility
  message, `TASK_DEVICE_MISMATCH`) and **shall** write nothing. Source: existing per-link validation,
  kept exactly (`EngineeringGraphService.cs:1035-1043`, `:261-264`).
- **AC-006** — **When** a session-list route answers, **then** every conversation **shall** carry its
  relation set and **shall** carry the primary projected into `taskId`/`taskProvenance`, and the page's
  relations **shall** be read in one query rather than one per conversation. Source: ADR-0014
  *Consequences*, and its *Implementation Guidance* ("one query over `graph_edges` for the sessions in
  the page").
- **AC-007** — **If** the workbench graph cannot be opened or read while a session list is projected,
  **then** the route **shall** still answer `200` with every conversation file-derived and an empty
  relation set, never a 5xx. Source: ADR-0014 Negative Consequences ("the conversation list itself must
  stay usable").
- **AC-008** — **When** a session whose persisted header still carries a legacy `taskId` is next
  written, **then** that value **shall** be imported once, as a relation related with the header's own
  provenance (primary when the conversation has no primary), and the persisted header **shall** no
  longer carry it; a read **shall** never import it. Source: ADR-0014 Decision 1, *Decision Details*
  (Authority, Migration).
- **AC-009** — **When** a user clears every relation from a legacy conversation, **then** the write
  **shall** persist a header with no legacy `taskId` and **no** later write **shall** re-create the
  cleared relation. Source: ADR-0014 *Authority* (the import is once; the case the rule implies).
- **AC-010** — **When** a conversation's own `create_task` call creates a task, **then** a relation with
  provenance `auto` **shall** relate that conversation to the created task, the primary **shall** move
  only when the conversation had none, and the call **shall** still report the created task if the
  relation write fails. Source: ADR-0014 Decision 2, *Decision Details* *Automatic association*.
- **AC-011** — **When** a turn's runtime context is built, **then** the primary relation's task
  **shall** be injected exactly as today, every other related task **shall** be named by title and
  status within a bounded list, and a relation whose task no longer resolves or no longer matches the
  conversation's device **shall** be skipped without failing the turn. Source: ADR-0014 Decision 4, and
  today's throwing behaviour it replaces (`CompatibilityEndpoints.cs:1129-1143`).
- **AC-012** — **When** the row menu's picker is applied, **then** one request **shall** replace the
  whole relation set — so the conversation is never observable half-linked — and the existing
  `PUT …/task` routes **shall** keep working as "set the primary relation". Source: UI Spec AC-019;
  ADR-0014 *Implementation Guidance*.

## Existing Evidence

| Evidence | Location | Design effect |
|---|---|---|
| The relation is one-to-one only by code | `EngineeringGraphService.cs:1045` (delete every edge into the session), `:306-310` (delete every `task → session` edge for a session target), `EngineeringGraphApi.cs:138` (`SingleOrDefault`) | Three call sites to replace with set-shaped writes; storage needs no new relation kind |
| Storage already permits several `task → session` edges | `EngineeringGraphSchema.cs:71` (`UNIQUE (from_kind, from_id, to_kind, to_id, relation_kind)`), `:91-94` (the only partial index, commit-specific) | The migration is one index plus a promotion, not a table rewrite |
| `is_primary` is refused for this relation | `EngineeringGraphService.cs:265-266`, `:300-301` | Both refusals widen to `TaskSession`; the unique-violation mapping (`:276-281`, `:323-326`) already exists |
| The schema ladder, its failure injection and its backup | `EngineeringGraphSchema.cs:19`, `:141-168`; `EngineeringGraphStore.cs:11`, `:37-48`, `:86-96` | v7 follows the frozen-DDL pattern the v6 tests already exercise (`EngineeringGraphStoreTests.cs:141-179`, `:181-205`, `:209-253`) |
| `DeleteTask` leaves `graph_edges` behind | `EngineeringGraphService.cs:199-209` against `RemoveEntityWithin`'s own edge cleanup at `:364-370` | Closely-related fix: route the task delete through the existing edge cleanup in one transaction (AC-003) |
| A conversation's task is written to the file header **and** the graph | `WorkbenchApiModels.cs:2019-2021`, `:2031-2033`; `CompatibilityEndpoints.cs:497-499` | The dual write disappears; `ApplyWithPersistence` becomes the graph-side set API |
| A graph session entity is registered on create, not per turn | `WorkbenchApiModels.cs:1988`, `:2227`; `CompatibilityEndpoints.cs:481` | `Register` takes the primary task explicitly instead of reading it from the header (`EngineeringGraphApi.cs:158-165`) |
| An implicit session has no graph entity | `CompatibilityEndpoints.cs:942-944` | The relation write registers the session entity idempotently, as `ApplyWithPersistence` already does (`EngineeringGraphApi.cs:139-140`) |
| The device chat's session is swapped in place | `CompatibilityEndpoints.cs:859-872` (load), `:1145-1162` (save); the catalog is built once per scope key at `:946-947` | `create_task`'s conversation identity must be a live provider, not a captured id |
| The model's task context is built from the header | `CompatibilityEndpoints.cs:973-974`, `:1129-1143` | Moves to a per-turn graph read with per-link degradation (AC-011) |
| The task page already reads its conversations from the graph | `WorkbenchApiModels.cs:2512-2527`; `TaskDetail.tsx:143`, `:202-210` | No task-page change for membership; only the remove gating and the provenance label move |
| `SessionManager.ListSessions` is a pure file scan | `SessionManager.cs:44-71`, `:300-356` (where `taskId` is read) | The projection lives in ApiHost; `SessionManager` stays graph-unaware and stops being a complete answer |
| The task surface filters a device's sessions by the single id | `WorktreeTasksPanel.tsx:349-351` | Becomes set membership |
| The compat route and its client function have no live caller | `CompatibilityEndpoints.cs:465-466`; `client.ts:1981-1985`, `:2063-2067` | They still must answer truthfully; the projection there is cheap and keeps the compat surface honest |
| The frontend unions and label function carry three provenances | `client.ts:132`, `:145`; `TaskDetail.tsx:18-24`; the twin at `TaskCommitsSection.tsx:40` | `'auto'` must be added in both places or an automatic relation renders as "Unassigned" |
| Existing suites assert the single-id behaviour | `WorkbenchEndpointsTests.cs:675`, `:709-710`, `:871-898`, `:971-973`, `:1013-1040`; `EngineeringGraphConstraintsTests.cs:11-25`, `:28-49`, `:129-148`; `SessionManagerTests.cs:97-109`; `WorkbenchNavigator.test.tsx:862-903`, `:905-927`; `WorktreeTasksPanel.test.tsx:265-281`; `TaskDetail.test.tsx:36-54` | Extended to the set, never weakened; `SessionManagerTests.cs:97-109` has its premise deleted by ADR-0014 and is replaced by the import test (AC-008) |
| The commit attribution rules stay untouched | `EngineeringGraphCommitAttributionTests.cs:10-48` (one primary `task → commit` edge), `:50-85` (repairable warning) | The widened `is_primary` rule must stay commit-and-session only; a second primary **commit** edge must still be refused |

## Design

### Selected Design

**1. Storage and schema v7.** The relation is the `task_session` edge. `EngineeringGraphSchema.CurrentVersion`
becomes 7; a `if (version < 7)` block runs in the same migration transaction as every earlier step
(`EngineeringGraphSchema.cs:19-168`) and does exactly two things:

```sql
-- 1. Promote the only relation of a conversation that has exactly one and no primary yet. Idempotent:
--    the SUM(is_primary) = 0 condition excludes a conversation whose single edge is already primary,
--    so a second run inside the same transaction is a no-op.
UPDATE graph_edges SET is_primary = 1, updated_utc = $utc
 WHERE relation_kind = 'task_session' AND from_kind = 'task' AND to_kind = 'session'
   AND is_primary = 0
   AND to_id IN (
     SELECT to_id FROM graph_edges
      WHERE relation_kind = 'task_session' AND from_kind = 'task' AND to_kind = 'session'
      GROUP BY to_id
     HAVING COUNT(*) = 1 AND SUM(is_primary) = 0);
-- 2. Mirror the commit index (EngineeringGraphSchema.cs:91-94).
CREATE UNIQUE INDEX IF NOT EXISTS ux_graph_edges_primary_task_session
    ON graph_edges (to_kind, to_id)
    WHERE relation_kind = 'task_session' AND is_primary = 1
      AND from_kind = 'task' AND to_kind = 'session';
```

The `SUM(is_primary) = 0` half of the group condition is what the promotion must not omit: filtering the
group on `is_primary = 0` alone makes a conversation that already has two relations *and* a primary look
like a one-relation conversation, promotes the other edge, and then fails at index creation — aborting a
migration that had nothing wrong with it (found while implementing issue 115).

Promotion runs before index creation and inside one transaction, so a database that already violates
the invariant (two primary `task_session` edges for one conversation, which no shipped code path can
produce) aborts the migration at the index and is restored from the `.bak` rather than half-migrated —
the store's existing rule, which `EngineeringGraphStoreTests.FailedVersionSixMigrationLeavesTheDatabaseByteIdentical`
already proves for the previous step. The version row is appended exactly as `:166-167` does today, and
the block's failure injector is invoked before and after its work (the ladder's pattern at `:143`, `:165`)
so the all-or-nothing property is testable for v7 too.

**Task deletion cleans its edges (closely-related work).** `DeleteTask` (`EngineeringGraphService.cs:199-209`)
currently removes `task_source_stages`, `tasks` and the task's `graph_entities` row and leaves every
`graph_edges` row that references the task. With one relation per conversation that was a latent
dangling reference; with several, every conversation a deleted task related to keeps pointing at a task
`FindTask` can no longer resolve. The delete reuses the existing cleanup
(`RemoveEntityWithin`, `:364-370`: properties, then edges in both directions, then the entity row) inside
its own transaction, ahead of the stage and row deletes. This is ADR-0014's "one cleanup rule instead of
two" applied to the task half, and it is what makes AC-003 true.

**2. Graph service: set-shaped relation writes.** `ReplaceSessionTask` (`:1031-1059`) is replaced by
three methods, all on `EngineeringGraphService`:

| Method | Contract |
|---|---|
| `AddSessionTask(sessionId, taskId, provenance, makePrimaryIfNone = false)` | Validates task existence, worktree compatibility and device match exactly as `:1035-1043` does; inserts one edge; when `makePrimaryIfNone` and the conversation has no primary, the primary is set by an `UPDATE … SET is_primary = 1 WHERE edge_id = $edge AND NOT EXISTS (SELECT 1 FROM graph_edges WHERE relation_kind = 'task_session' AND to_kind = 'session' AND to_id = $session AND is_primary = 1)` in the same transaction — one statement, so two concurrent adds cannot both win and no retry is needed. Returns the edge; an already-present pair returns the existing edge unchanged (the delete-then-insert of `ReplaceSessionTask` is what would otherwise lose provenance and `created_utc`). |
| `RemoveSessionTask(sessionId, taskId)` | Deletes that one edge. Removing the primary leaves the conversation with relations and no primary — ADR-0014 states the primary is established when a conversation *gains* its first relation, not when it loses one, so no promotion happens here. |
| `SetSessionTasks(sessionId, taskIds, primaryTaskId)` | Replace-as-a-set in one transaction: delete only the `task_session` edges of that conversation whose `from_id` is not in `taskIds`, insert the missing pairs, preserve kept edges, then demote the current primary and promote `primaryTaskId` if it is in the set (demote before promote; the partial index is checked per statement). Validates every requested id before the first write, so an approved set writes exactly itself. |

`ReplaceTaskRelationship` (`:293-327`) keeps its pair-replacement behaviour for commits, source objects
and SVN revisions, and for a `session` target stops deleting the conversation's other relations
(`:306-310`): it becomes add-if-absent plus, when `isPrimary`, the demote-then-promote pair. `AddEdge`'s
and `ReplaceTaskRelationship`'s `is_primary` refusals (`:265-266`, `:300-301`) widen to
`GraphRelationKind.TaskSession` **only**; `EngineeringGraphCommitAttributionTests:10-48` keeps proving
that a second primary **commit** edge is still refused.

The one-query read is a new service method:

```csharp
public sealed record SessionTaskRelation(string SessionId, string TaskId, string EdgeId,
    GraphProvenance Provenance, bool IsPrimary);

public IReadOnlyList<SessionTaskRelation> ListSessionTaskRelations(IReadOnlyCollection<string> sessionIds);
```

It selects `edge_id, from_id, to_id, provenance, is_primary` from `graph_edges` where
`relation_kind = 'task_session' AND from_kind = 'task' AND to_kind = 'session' AND to_id IN (…)`, ordered
by `to_id, is_primary DESC, created_utc, from_id` so the primary is first and the rest are stable, and
reuses the existing `Placeholders` helper (`:1020-1021`) as `ReadEntitiesOfWorktrees` (`:1003-1018`)
already does. The page is one device's conversation list; a page large enough to exceed the driver's
parameter cap does not exist and chunking is not added.

`SessionTaskRelation` above is the graph-side read row and keeps `GraphProvenance`; the **wire** shape is
a different, plain record declared beside `ChatSessionInfo` in `src/Agent/Chat/SessionFileFormat.cs`:

```csharp
public sealed record ChatSessionRelation(string TaskId, string EdgeId, string Provenance, bool IsPrimary);
```

Two records rather than one, for two reasons. The provenance must cross the JSON boundary as a string:
the response types declare `string Provenance` today (`EngineeringTaskRelationshipApiResponse` and
`EngineeringTaskRelationshipMutationApiResponse`, `src/ApiHost/EngineeringGraphApi.cs:82-97`;
`ToRelationshipMutation`, `src/ApiHost/WorkbenchApiModels.cs:2256-2259`) and the repository converts
enums per property where it must (`[property: JsonConverter(typeof(JsonStringEnumConverter<GraphTaskType>))]`,
`EngineeringGraphApi.cs:37`), so a `GraphProvenance` in a response record would serialize as a number and
break the frontend's union. And keeping the wire record in `Agent.Chat` leaves `SessionManager` and
`ChatSessionInfo` free of the graph's types, which is what ADR-0014's "`SessionManager` stays unaware of
the graph" means in practice; the projection maps one to the other in one `Select` and lowercases the
provenance name.

**3. `GraphProvenance.Auto`.** The enum (`EngineeringGraphModels.cs:9`) gains `Auto` between `Default`
and `Evidence`. `ParseProvenance` (`EngineeringGraphService.cs:1142`) gains `"auto" => GraphProvenance.Auto`
— without it the switch's `_ => Manual` default silently downgrades a stored automatic relation to
`manual`, which is a correctness bug, not a cosmetic one. The API needs no mapping change: both
provenance serializers use the enum name (`WorkbenchApiModels.cs:2256-2259`, `:2515-2520`), so the wire
value is `"auto"`. The frontend unions gain it (`client.ts:132`, `:145`) and `provenanceLabel`
(`TaskDetail.tsx:18-24`) gains `'auto' → 'Created by this conversation'`; `TaskCommitsSection.tsx:40`
keeps its three cases, because no commit relation is ever `auto`.

**4. `SessionGraphOperations` becomes the relation API and the projection.**
`src/ApiHost/EngineeringGraphApi.cs` keeps being the one place that knows both the session identity and
the graph:

| Member | Change |
|---|---|
| `Register(graph, session, provenance, primaryTaskId)` | The primary task arrives as an argument instead of being read from the header (`:158-165`), so the header stops being an input to the graph write. The `RegisterOverride` test hook and the compensation behaviour (`:124`, `:153`, `:167-171`) are preserved. |
| `SetTasks(graph, session, taskIds, primaryTaskId)` | The one relation writer the routes call: registers the session entity idempotently (`:139-140`), then `SetSessionTasks`. |
| `ImportLegacy(graph, session)` | The only reader of the legacy header field: when the header carries a non-blank `taskId` and the conversation has no relation, adds it (primary; provenance `Default` for `"default"`, otherwise `Manual`) and reports whether it imported. |
| `Project(ChatSessionInfo, relations)` / `Project(ChatSessionData, relations)` | The response projection: the relation set with provenance and primary, plus `taskId`/`taskProvenance` copied from the primary relation. Never persisted. |
| `ValidateCandidate(...)` | Stops preserving `TaskId`/`TaskProvenance` from an incoming header (`:132`): the header is not an input to the relation any more, so an echoed response cannot inject a binding. |
| `SetTask` (`:174-179`) | Removed: no caller (`grep` over the repository finds only its definition). |
| `RemoveSessionTask`, `ReplaceSessionTask`, `ApplyWithPersistence` | Removed or replaced by the set API above. |

**5. Projection: which route fills what.** Every route that answers with conversations projects the
relation set; the routes that answer with one conversation project the primary on the response object
they already return, so no current reader breaks.

| Route | Projection |
|---|---|
| `GET …/devices/{device}/sessions` (`WorkbenchApiModels.cs:1976-1978`) | Full set, one query for the page (the navigator's and the task panel's source) |
| `GET /api/devices/{device}/sessions` (`:2218`) | Full set (typed sibling of the compat list) |
| `GET /api/chat/sessions` (`CompatibilityEndpoints.cs:465-466`) | Full set (compat twin; no live caller, kept truthful) |
| `POST` session create, all three routes (`WorkbenchApiModels.cs:1979-1991`, `:2219-2230`; `CompatibilityEndpoints.cs:467-486`) | The primary the route just registered is projected onto the returned session — no extra read, because the graph scope is already open. This is what keeps `createChatSessionForTask`'s binding check (`MainStudio.tsx:1498-1503`) true. |
| `PUT …/sessions/{session}` (`WorkbenchApiModels.cs:2009-2023`) | Imports legacy, writes the file, returns `204` (`:2022`) — no session body, so no projection |
| `PUT …/sessions/{session}/task` (`:2024-2035`) and `PUT /api/chat/session/task` (`CompatibilityEndpoints.cs:487-502`) | Keep their contract as "set the primary relation" and return the session with the primary projected — `MainStudio.setChatSessionTask` opens the returned session as a tab (`MainStudio.tsx:1551`) |
| New set route (both flavours, `:2024` sibling and `/api/chat/session/tasks`) | Returns the session with the projected primary |
| `POST /api/chat/session/load` and `GET …/sessions/{session}` (`CompatibilityEndpoints.cs:503-508`; `WorkbenchApiModels.cs:1992-1995`, `:2231`) | Project the primary onto the returned `ChatSessionData`, so that two responses of the same conversation cannot disagree about it; the relation set is not carried, because a `ChatSessionData` is the conversation, not a list row |
| `GET /api/chat/session/info` (`CompatibilityEndpoints.cs:538-550`) | **No projection.** It answers a count and two ids (`sessions`, `activeSessionId`, `requiresExplicitSession`) and carries no conversation row, so there is nothing to project; adding a graph open here would buy nothing. |

`SessionManager.ListSessions` keeps reading files and **stops reading the relation fields** from the
header (`SessionManager.cs:349-350`): `ChatSessionInfo.TaskId`/`TaskProvenance` are filled only by the
ApiHost projection. The record keeps both properties with a null default and gains
`IReadOnlyList<ChatSessionRelation>? TaskRelations = null`, so the wire shape is additive and every
existing positional construction (there is one, `SessionManager.cs:336-350`) still compiles.

**When the graph cannot be opened.** The list routes wrap graph I/O in a local guard: a failure yields an
empty relation set on every row (`taskId: null`, `taskProvenance: null`, `taskRelations: []`) and a
`graph-unavailable` entry on the existing log stream (`CompatibilityRuntimeState.Logs` — the stream
`GET /api/logs` serves and the confirmation poller filters by kind,
`MainStudio.tsx:1630`; implementing the design corrected this document's earlier naming of
`WorkbenchApiState`, which has no log member). The list stays complete and usable (AC-007); the cost is that a bound
conversation reads as task-less until the graph is readable again, which is recorded under Material
Risks. The failure is never reported as a 5xx, and it is never reported as a read failure of the
conversation files.

**6. Legacy import: when the file's `taskId` is read.** After this change the field has exactly one
reader, `SessionGraphOperations.ImportLegacy`, and one writer, `SessionManager.WriteSession`, which
persists `TaskId = null`/`TaskProvenance = null` (`SessionManager.cs:218-226`). Every ApiHost path that
persists a session calls the import before its write:

| Write path | Where |
|---|---|
| `PUT …/sessions/{session}` | `WorkbenchApiModels.cs:2009-2023` |
| `PUT …/sessions/{session}/task` and the new set route | `:2024-2035` |
| `PUT /api/chat/session/task` and the new set route | `CompatibilityEndpoints.cs:487-502` |
| `POST /api/chat/session/rename` (`:509-517`) → `ApiChatService.RenameSession` (`:874-896`) | `ApiChatService` already holds `EngineeringGraphApiFactory` and `WorkbenchApiState` (`:752-763`), so the import costs one graph open only while the header still carries a value |
| The chat turn's save, `ApiChatService.SaveActiveSession` (`:1145-1162`) and `Clear` (`:827-835`) | Same; the guard is the legacy field itself, so a post-change session pays nothing |

`ApiChatService.CreateSession` (`:849-857`) keeps its `taskId`/`taskProvenance` parameters as the
*relation* input and stops passing them to `SessionManager.CreateNewSession` (`SessionManager.cs:101-158`,
whose two relation parameters are removed): the primary relation is created by the route's `Register`
call, and the file no longer has a field to carry it.

**Order inside a set-replace operation** (this differs from ADR-0010's graph-first delete rule, and
deliberately): read the file and the current graph set → import the legacy value if any → write the file
(which clears the legacy fields) → replace the graph set in one transaction → project the response. A
failure between the file write and the graph write can only leave the requested change unapplied, never
fabricate a relation. Graph-first would leave the legacy field in the file while the graph already holds
the new set, and an operation that emptied the set would then re-import the old value on the next write
(AC-009). Deleting a conversation keeps ADR-0010's order unchanged: graph entity and edges first, then
the file.

**7. Automatic association.** `TaskCreationTool.Create` (`TaskCreationTool.cs:80-113`) gains the
conversation's identity and, after the existing task write, relates that conversation to the created
task:

- **The mechanism.** `BuildToolCatalog` (`CompatibilityEndpoints.cs:991-1020`) gains a
  `Func<string?>` session-id provider and passes it to `TaskCreationTool.CreateSpec`
  (`TaskCreationTool.cs:77-78`). `EnsureActiveChatAsync` (`:930-983`) builds it inside the branch that
  builds the catalog (`:940-981`) as
  `() => chats.TryGetValue(contextKey, out var current) ? current.Session.Header.SessionId : session.Header.SessionId`
  — the same live-read shape the runtime-context closure already uses (`:973-974`), and necessary
  because `chats[key]` is replaced in place when the user loads another conversation (`:859-872`) while
  the loop and the catalog are reused. A captured id would attribute the relation to the previous
  conversation.
- **Ripple**: `BuildToolCatalog` is `internal static` and called from `EnsureActiveChatAsync:946-947`
  and from `tests/ApiHost.Tests/OpenTiaProjectToolTests.cs:45-53`; `TaskCreationTool`'s constructor and
  `CreateSpec` are constructed in `tests/ApiHost.Tests/TaskCreationToolTests.cs:297` and `:313`. Both
  call sites pass a provider (`() => null` where no conversation identity exists), so the tests keep
  their meaning.
- **Order and failure.** The task write happens first (the relation needs the id), then
  `AddSessionTask(sessionId, taskId, GraphProvenance.Auto, makePrimaryIfNone: true)` in its own graph
  scope. If the relation write fails, the tool does **not** fail the call: the created task is reported
  as its result always, and the result gains an additive `relationWarning` string naming the failure —
  the shape `EngineeringGraphCommitAttribution.Associate` already uses for a relation that must be
  repaired rather than thrown (`EngineeringGraphCommitAttributionTests.cs:50-85`). The user approved a
  creation; reporting failure for a task that exists would be wrong, and silently dropping the relation
  without saying so would be worse.
- **Provenance and primary**: `Auto`, and the primary is set only when the conversation has none —
  ADR-0014 Decision 2 is explicit that automatic association never moves it.

**8. The model's task context, per turn.** `TaskContext` (`CompatibilityEndpoints.cs:1129-1143`) is
replaced by a graph read of the conversation's relations (`ListSessionTaskRelations`) and builds:

```
Active task: {title} ({taskId})
Task type: {type}; status: {status}
Task goal: {intent}
Expected result: {expectedResult}
Task context: {description}
Related tasks: {title} ({taskId}) — {type}, {status}; … (and {K} more)
```

The primary's five lines are byte-identical to today's, so no prompt behaviour drifts for the
single-relation case that every existing conversation is in. The last line is present only when a second
resolvable relation exists, names at most ten tasks, and appends `(and {K} more)` beyond that; the bound
exists because this line is injected every turn and a conversation may relate to many tasks (ADR-0014's
*Reconsider when* owns ordering and per-relation attributes, not a token bound). Degradation is per
link: a relation whose `FindTask` returns null, or whose worktree-scoped task no longer matches the
conversation's worktree or device, is skipped — no exception. The primary block is emitted only when the
primary relation resolves; a dangling primary is **not** promoted to a "related" row and does not invent
a new active task, and a conversation with no resolvable relation gets no task lines at all, exactly as a
task-less conversation does today.

**9. Routes and contracts.** One new operation replaces the set:

```
PUT /api/workbenches/{wb}/worktrees/{wt}/devices/{device}/sessions/{session}/tasks
PUT /api/chat/session/tasks                         (compat twin)
body: { taskIds: string[], primaryTaskId?: string | null }
```

Primary resolution, server-side and deterministic: `unassigned` when the caller says the conversation is
assigned to none of its tasks; else `primaryTaskId` when it is present in `taskIds`; else
the conversation's current primary when it is still in `taskIds`; else the first element of `taskIds`;
else none. The UI Spec's picker (AC-019, row `:79`) sets relations *and* the assignment, so the client
always sends the one it is showing — the conversation's current primary while that task is still
checked, or `unassigned` — because checking a task relates the conversation to it without assigning it,
and an apply that named nothing would let the server pick a task the user did not pick. The response is
the conversation with the
projected primary, matching the existing `/task` routes' response so `MainStudio`'s tab update
(`:1551`) keeps working. Both existing `PUT …/task` routes stay as "set the primary relation", mapped
onto `SetSessionTasks` with the current set plus (or minus) the named primary, which keeps every current
caller working (AC-012).

The typed route is canonical and the compat twin delegates to the same shared operation, exactly as
`POST /api/chat/session/delete` already delegates (`CompatibilityEndpoints.cs:518-528`). The client
keeps calling the compat path, because the navigator's callback already selects the device first
(`MainStudio.tsx:1549`) — moving it to the typed path is a simplification this change does not need.

**10. UI change surface.** No interaction is added or renamed; three predicates change, the picker gains
checks, and two display rules follow the new provenance:

| Surface | Change | Preserved |
|---|---|---|
| `WorkbenchNavigator.tsx:692-698` | The content rule becomes set membership: the selected task's list is `taskRelations.some(r => r.taskId === task.taskId)`; the task-less list is a conversation with no relation | The heading rule (`:699-700`), the absence rule (`:977`), the hardware exclusion (`:690`), the tag-filter exclusion, the deepest-section role (`:983`) |
| `WorkbenchNavigator.tsx:707`, `:1388-1416` | The picker becomes a checked multi-select over the same bindable tasks: a click sets a check, a second click clears it, and one apply writes the whole set | The search field, the `CommandDialog` shell, the "never a free-form id" rule, the device-bound offer list |
| `WorkbenchNavigator.tsx:492-501`, `:1011` | "Attach task"/"Reassign task" become one "Tasks…" entry opening the picker; the separate "Remove task" item disappears, because a second click clears a binding | Open, rename, export and delete in the same menu |
| `MainStudio.tsx:1541-1558` | `setChatSessionTask` becomes `setChatSessionTasks(session, taskIds, primaryTaskId)`, one call to the new route, then the same tab update and session refresh | The device selection the callback performs first, the tab update, the refresh every surface already calls (`:1357-1379`) |
| `WorktreeTasksPanel.tsx:349-351` | A task's conversations become set membership (the card's disclosure and the list's count) | The fan-out load (`:285-292`) and `TaskSessionsDisclosure`'s row content |
| `TaskDetail.tsx:202-210` | The Remove control covers `auto` as well as `manual` (ADR-0014 widens exactly that rule); the provenance label gains `auto` | The section's header, the New-chat action, the `· Primary` marker (`:206`), `default` relations staying non-removable from this page (they are cleared from the conversation's own picker) |
| `MainStudio.tsx:2262-2266` | Unchanged: the remove still goes through the task-relationship `DELETE` with the row's edge id | — |
| `client.ts:119-133`, `:135-146` | `taskRelations?: SessionTaskRelation[]`; `taskProvenance` gains `'auto'`; a `setChatSessionTasks` beside `setChatSessionTask` (`:2000-2007`) | `taskId`/`taskProvenance` stay optional and keep their meaning as the projected primary; existing fixtures that omit the collection stay valid, and every reader treats absent as empty |

### Change Surface

| Responsibility or expected file | Change | Governing source | Unaffected boundary to preserve |
|---|---|---|---|
| `src/Agent/Workbench/EngineeringGraph/EngineeringGraphSchema.cs` | `CurrentVersion = 7`; the v7 block (primary promotion + partial unique index) and its failure-injection points | ADR-0014 *Migration*; AC-001, AC-002 | Versions 1-6's frozen DDL, the single migration transaction, the `.bak` rule |
| `src/Agent/Workbench/EngineeringGraph/EngineeringGraphModels.cs` | `GraphProvenance.Auto` | ADR-0014 *Decision Details* | The other enum members and every existing switch's meaning |
| `src/Agent/Workbench/EngineeringGraph/EngineeringGraphService.cs` | `AddSessionTask` / `RemoveSessionTask` / `SetSessionTasks` / `ListSessionTaskRelations`; `ReplaceSessionTask` and `ApplyWithPersistence`'s deleter removed; `ReplaceTaskRelationship`'s session branch stops deleting siblings; the `is_primary` refusals widen to `TaskSession`; `ParseProvenance` learns `auto`; `DeleteTask` cleans edges through `RemoveEntityWithin` | ADR-0014 Architecture Impact; AC-003, AC-004, AC-005 | The per-link validation codes, the commit-only primary rule, the property table's no-cascade rule, `RemoveEntity`'s existing cleanup |
| `src/ApiHost/EngineeringGraphApi.cs` | `SessionGraphOperations` becomes the relation API (`Register`/`SetTasks`/`ImportLegacy`/`Project`), `ValidateCandidate` stops trusting the header, `SetTask` removed | ADR-0014 Architecture Impact | The `RegisterOverride` hook and the create-compensation behaviour |
| `src/Agent/Chat/SessionManager.cs`, `SessionFileFormat.cs` | Writers never set `TaskId`/`TaskProvenance` and clear them on write; `ReadSessionInfo` stops reading them; `ChatSessionInfo` gains the optional relation collection of `ChatSessionRelation` wire items | ADR-0014 Decision 1 | Session file paths, the header's identity fields, the list's ordering and counts, the legacy-without-task load case (`SessionManagerTests.cs:111-141`), and `Agent.Chat`'s independence from the graph's types |
| `src/ApiHost/WorkbenchApiModels.cs` | List/Create/Load/Task routes project relations; the new typed set route; the session PUT route imports before writing | ADR-0014 *Consequences*; AC-006, AC-007, AC-008, AC-012 | Every route's path, verb, request shape and status codes; the task-detail projection (`:2512-2527`) |
| `src/ApiHost/CompatibilityEndpoints.cs` | `/api/chat/sessions` projects; the new compat set route; `/chat/session/task` keeps working; rename/clear/turn-save import before writing; `TaskContext` reads the graph with per-link degradation; `BuildToolCatalog` and `EnsureActiveChatAsync` pass the live session provider | ADR-0014 *Consequences*, Decision 4; AC-006 to AC-012 | The compat routes' request/response shapes, the confirmation flow, the in-memory chat map's keys |
| `src/ApiHost/TaskCreationTool.cs` | The relation write after the task write, its `auto` provenance, the primary-only-when-absent rule, the additive `relationWarning` | ADR-0014 Decision 2; AC-010 | The device-bound target, the argument validation, the approval-card shape, the brief composition (ADR-0013) |
| `studio/src/api/client.ts` | `SessionTaskRelation`, `taskRelations`, `'auto'` in both provenance unions, `setChatSessionTasks` | UI Spec AC-019; ADR-0014 *Consequences* | `taskId`/`taskProvenance` as optional fields; every existing session function |
| `studio/src/studio/workbench/WorkbenchNavigator.tsx` | Set membership in the content rule; the checked picker; the row menu's binding entry | UI Spec AC-015, AC-019 | The heading, absence, hardware, tag-filter and deepest-section rules; the row's other operations |
| `studio/src/studio/workbench/WorktreeTasksPanel.tsx` | Set membership for a task's conversations | UI Spec AC-015 | The fan-out load and the disclosed row content |
| `studio/src/studio/workbench/TaskDetail.tsx` | The Remove gating covers `auto`; the provenance label gains it | UI Spec AC-019 (the relation must be clearable); ADR-0014 *Decision Details* | The section's other controls; `default` staying non-removable here |
| `studio/src/studio/MainStudio.tsx` | `setChatSessionTasks` and the picker's plumbing | UI Spec AC-019 | The refresh every surface calls, the device selection, the task-chat view |
| `tests/Agent.Tests/EngineeringGraphStoreTests.cs` | The v6 fixture, the v7 migration, the promotion and the failed-migration byte-identity case | AC-001, AC-002 | The existing v5→v6 cases |
| `tests/Agent.Tests/EngineeringGraphConstraintsTests.cs` | Set-shaped relation writes, multi-relation conversations, one-primary enforcement, the task-delete edge cleanup | AC-003, AC-004, AC-005 | The per-link validation codes and the relation-pair coverage (`:28-49`) |
| `tests/Agent.Tests/SessionManagerTests.cs` | `TaskId_roundtrips…` (`:97-109`) is replaced by the import-and-clear case; the legacy-without-task load stays | AC-008, AC-009 | Every other session case |
| `tests/ApiHost.Tests/WorkbenchEndpointsTests.cs` | The single-edge assertions become set assertions; the projection, the set route, the graph-unavailable list, the import and the clear-all case are added | AC-006 to AC-012 | The compensation (`:976-1005`) and persist-failure (`:1013-1040`) cases, in set form |
| `tests/ApiHost.Tests/TaskCreationToolTests.cs` | The auto-relation case and the relation-write-failure case; the fixture's new provider argument | AC-010 | The schema, validation, approval-tier and device-context cases |
| `tests/ApiHost.Tests/OpenTiaProjectToolTests.cs` | `BuildToolCatalog`'s new argument at `:45-53` | AC-010 | Every assertion |
| `studio/src/studio/workbench/WorkbenchNavigator.test.tsx` | The picker's check/second-click/one-apply case; the set-membership cases | UI Spec AC-015, AC-019 | Every existing navigator case |
| `studio/src/studio/workbench/WorktreeTasksPanel.test.tsx`, `TaskDetail.test.tsx`, `MainStudio.chatTaskCreate.test.tsx` | Set membership in the disclosure; the `auto` remove gating and label; the post-turn re-read that makes a new automatic relation appear | UI Spec AC-015, AC-019; AC-010 | Each file's other cases |
| `docs/ui-spec/studio-information-architecture-ui-spec.md` | Already amended in v1.11 (AC-015, AC-017, AC-019, the row menu and the picker rows) — no further change by this design | — | Every other criterion |

### Components and Flow

| Component | Input | Interaction and response |
|---|---|---|
| Relation writer (`SessionGraphOperations.SetTasks` / `ImportLegacy`) | the session identity, the requested task ids, the primary, a provenance | Registers the session entity idempotently, replaces the relation set in one graph transaction, and is the only place that reads the legacy header field |
| Session-list projection | the file-derived `ChatSessionInfo` page, one `ListSessionTaskRelations` query | Fills each row's relation set and primary; degrades to an empty set (never a 5xx) when the graph cannot be read |
| `create_task` | the conversation's live id, the device, the approved brief | Writes the task, then the `auto` relation (primary only when absent); the result always names the task and carries `relationWarning` on a relation failure |
| Turn runtime context | the conversation's relations, per turn | Primary in full, other relations by title and status, unresolvable or device-mismatched relations skipped |
| Navigator `SESSIONS` | the selected task, the selected device, the projected relation sets | Set membership decides the list; the heading, absence and hardware rules are unchanged |
| Row-menu picker | the conversation, the worktree's bindable tasks, the conversation's relations | Checks reflect the current relations; a second click clears; one apply replaces the set |

### Contracts, State, and Persistence

| Boundary | Input / exact format | Output / exact format | Error or state behaviour | Compatibility |
|---|---|---|---|---|
| Graph schema | a v6 database | a v7 database: `tasks`, `graph_entities`, `graph_edges`, `graph_entity_properties`, `task_source_stages`, `legacy_imports`, `graph_file_evidence` unchanged; one new partial unique index; promoted primary edges | A failure aborts the whole migration transaction and leaves the database byte-identical with a `.bak` beside it and version 6 recorded | Additive; every earlier version step untouched |
| `graph_edges` (`task_session`) | 0..N rows per conversation, at most one with `is_primary = 1` | the same | A second primary insert is refused by the index and reported as `GRAPH_PRIMARY_RELATIONSHIP_EXISTS` through the existing mapping | The storage already allowed N rows; only the primary is newly expressible |
| Provenance values (wire) | `"manual"`, `"default"`, `"auto"`, `"evidence"` | the same strings on every relation and relationship response | An unknown stored value reads back as `manual` (the existing `ParseProvenance` default) — which is why `auto` is added there | Additive string; the frontend unions and the label function gain it |
| `ChatSessionInfo` | file fields + the projected relation set | `{ …, taskId, taskProvenance, taskRelations: [{ taskId, edgeId, provenance, isPrimary }] }` where each item is a `ChatSessionRelation` (`SessionFileFormat.cs`) and `provenance` is its lowercased name | `taskId`/`taskProvenance` are the primary relation or null; `taskRelations` is `[]` when the conversation has none **or** when the graph could not be read | Additive; both scalar fields keep their names and the primary's meaning |
| `ChatSessionData` (create/load/save responses) | the file's payload | the same payload with `header.taskId`/`header.taskProvenance` projected from the primary relation | The persisted header never carries them; the projection is response-only and is never read back as state | Additive to the payload; the persisted file loses two fields |
| Legacy header field | `header.taskId`, `header.taskProvenance` in a pre-change file | imported once into the graph as a relation; the next write persists neither field | A read never imports; an emptied relation set persists the cleared header, so nothing re-imports | Read-compatible; the fields stop being written |
| Set route (typed and compat) | `{ taskIds: string[], primaryTaskId?: string \| null }` | the conversation with the projected primary | Unknown/incompatible task ids are refused with the existing codes and nothing is written; an empty `taskIds` clears every relation | New route; the existing `PUT …/task` routes keep their shape and now mean "set the primary" |
| `create_task` result | the created task's fields | the same fields plus an optional `relationWarning` | A relation failure never fails the call and never hides the created task | Additive field |
| Model runtime context | the conversation's relations per turn | the primary's five lines plus one bounded `Related tasks` line | Unresolvable or mismatched relations are skipped; a dangling primary is not promoted | The single-relation formatting is byte-identical |

### Repository-Owned Migration, Flag, or Deployment Behavior

One schema version (6 → 7) with the store's existing automatic, backup-first, all-or-nothing migration
(`EngineeringGraphStore.cs:30-48`, `:86-96`). No feature flag, no dual-write window, no manual step: the
relation's reader and writer change in the same release, and the only compatibility path is the legacy
header import, which is idempotent and runs once per conversation. A workbench whose
`.automation/engineering.db` is deleted loses the relations (the file never stored them); the primary is
recoverable only from a legacy header that has not yet been written.

## Implementation Approach

- **Slicing**: foundation-first. The graph must be able to express the relation (schema, set writes,
  primary) before anything can project or write it.
- **Dependency order**: schema v7 and the graph API → projection and routes (including the import) →
  automatic association and the model context → UI → runtime verification. The projection must exist
  before the UI can read a set; the automatic association needs both the set write and the projection to
  be observable.
- **First observable checkpoint**: after the graph phase, `AddSessionTask` twice for one conversation
  leaves two edges with one primary, and a task delete removes them — visible in the `Agent.Tests` lane
  with no API or UI involvement.
- **Rationale**: this order makes each phase's failure local. A wrong migration shows up in the graph
  lane; a wrong projection in the ApiHost lane; a wrong association mechanism in either; and the UI is
  the only consumer of the JSON shape, so it moves last.

## Verification Strategy

| Claim / AC | Level | Repository command or operation | Observable pass condition |
|---|---|---|---|
| v7 migration, promotion and idempotency | L1 | `dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-build -v q` — a v6 fixture with three conversations (one relation, two relations with one primary already, none) migrates with every row intact, the promotion applied only to the single-relation one, and the index present | Row counts unchanged, `is_primary` exactly on the promoted edge, and a second open does not rewrite anything |
| Failed v7 migration leaves the database byte-identical | L1 | the same lane, with the v7 failure-injection phase | Bytes equal to the pre-migration file, `.bak` present, version still 6 (AC-002) |
| Set-shaped writes, one primary, preserved provenance | L1 | the same lane (`EngineeringGraphConstraintsTests`) | Two relations with one primary; re-writing a set that keeps a relation preserves its edge id, provenance and creation time; a third primary is refused (AC-004) |
| The per-link validation is unchanged | L1 | the same lane | `TASK_NOT_FOUND`, the worktree message and `TASK_DEVICE_MISMATCH` are still the codes for the same three inputs (AC-005) |
| Task delete removes its edges | L1 | the same lane | After `DeleteTask`, no `graph_edges` row names the task from either side, and the conversation's relation set no longer lists it (AC-003) |
| The list projects the set in one query | L1 | `dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj --no-build -v q` — a conversation related to two tasks answered by `GET …/devices/{device}/sessions` | Both ids present, `taskId` equals the primary, `taskProvenance` its provenance |
| The list survives an unreadable graph | L1 | the same lane, with the workbench's `engineering.db` replaced by a non-database file | `200` with every conversation present and an empty relation set (AC-007) |
| Legacy import once, and never on a read | L1 | the same lane — a header-carrying file, one list read, then a write (rename) followed by a second list read | The first read shows no relation (no import), the write imports it, both later reads show it, and the file's header no longer carries `taskId` (AC-008) |
| Clearing every relation from a legacy conversation sticks | L1 | the same lane — the set route with `taskIds: []` on a header-carrying file, then a rename | The relation set is empty after both writes and nothing re-imports (AC-009) |
| The set route replaces the whole set atomically | L1 | the same lane — a set change applied in one request, and the persist-failure rollback case | One edge set after one request; on an injected file-write failure the graph keeps the pre-request set (AC-012) |
| The existing `PUT …/task` routes still work | L1 | the same lane — both routes, on a conversation with two relations | The named task becomes the primary, the other relation survives (AC-012) |
| `create_task` establishes the relation and nothing else | L1 | the same lane (`TaskCreationToolTests`) — an approved call from a conversation with and without a primary, two calls from a conversation with none, and with an injected edge-write failure | A relation with `auto` provenance is added, the primary is untouched in both cases so a conversation that had none still has none, and the failure case still reports the created task with `relationWarning` (AC-010) |
| The set route can assign none | L1 | the same lane — the set route with two relations and `unassigned` | Both relations survive and the conversation carries no primary (`WorkbenchEndpointsTests`) |
| The model context degrades per link | L1 | the same lane — a turn with a resolvable primary, a second related task, a deleted task and a device-mismatched task among the relations | The primary block appears unchanged, the related line names only the resolvable others, and the turn completes (AC-011) |
| The picker is a checked set applied once | L1 | `npm test -- --run` in `studio/` (`WorkbenchNavigator.test.tsx`) | The offered options carry checks for the current relations; a second click clears; one apply call carries the whole set (UI Spec AC-019) |
| `SESSIONS` and the task surface use set membership | L1 | the same lane (`WorkbenchNavigator.test.tsx`, `WorktreeTasksPanel.test.tsx`) | A conversation related to two tasks appears in each task's list, and a related conversation leaves the task-less list (UI Spec AC-015) |
| An automatic relation is visible and clearable | L1 | the same lane (`TaskDetail.test.tsx`, `MainStudio.chatTaskCreate.test.tsx`) | The task page labels the relation `auto` and offers Remove for it; the conversation list is re-read after the turn, so the created task lists the conversation (AC-010) |
| Nothing else regressed | L1 | `dotnet build AgentAssistPlcDev.sln -v q` (launcher stopped), the two `dotnet test` lanes, `npx tsc -b`, `npm run lint`, the full vitest lane | Type check clean, no new lint warning, the suites pass as before |
| Three tasks from one task-less conversation | L3 | the running app via `.\launch.ps1`, driven with the browser: in a device conversation that is related to no task, approve three `create_task` calls in turn | Each of the three tasks lists that conversation, the conversation's row menu shows all three checks, a second click clears one and the task's list loses it, and no console error |

## Material Risks

| Risk | Evidence | In-scope response or verification |
|---|---|---|
| The migration's promotion marks the wrong edge | A conversation can only have had one edge before v7 (`ReplaceSessionTask` deleted the rest), so a multi-edge conversation exists only if the database was edited by hand | The promotion is restricted to `HAVING COUNT(*) = 1` and `is_primary = 0`, and the index is created after it, so a hand-edited violation aborts the migration instead of half-applying |
| The automatic relation is attributed to the wrong conversation | The device chat swaps `chats[key].Session` in place and reuses the loop and catalog (`CompatibilityEndpoints.cs:859-872`, `:940-981`) | The provider reads the current session from the map per call, the shape the runtime-context closure already uses (`:973-974`); a test drives a turn after loading another conversation |
| A conversation is briefly unlinked because the task write succeeded and the relation write did not | Two graph writes in one tool call, not one transaction (the task write owns its own transaction, `EngineeringGraphService.cs:65-75`) | The tool reports the created task and a `relationWarning` (AC-010); the relation can be set from the row menu afterwards. Making the two one transaction would mean the tool writes the relation inside `CreateTask`, which would put conversation identity into the graph task API — rejected as a wider coupling than the failure it removes |
| A relation write leaves the file and the graph disagreeing | Two stores, but now one owns the relation; the file write is what clears the legacy field | The operation order (import → file → graph set) can only lose the requested change, never fabricate or resurrect a relation; tested with the injected file-write failure (AC-012) |
| An unreadable graph makes a bound conversation look task-less | The projection degrades rather than failing the list (AC-007) | Accepted: the alternative hides conversations or 5xxes a working surface. The degradation is logged as `graph-unavailable`, and the task page — which reads the graph directly — is degraded by the same condition, so neither surface silently contradicts the other |
| The model's prompt grows with the relation count | The `Related tasks` line is injected every turn | The line names at most ten tasks and counts the rest; the primary block is unchanged from today |
| A stale relation breaks a turn | Today's `TaskContext` throws `TASK_DEVICE_MISMATCH` for one mismatched task (`CompatibilityEndpoints.cs:1136`) | Resolution is per link and skips what it cannot resolve (AC-011); the throw is removed |
| A dangling relation survives a task delete | `DeleteTask` leaves `graph_edges` today (`EngineeringGraphService.cs:199-209`) | In scope as closely-related work (AC-003), verified by the graph lane |
| Widening `is_primary` weakens the commit rule | The refusal is shared by every relation (`:265-266`, `:300-301`) | The widening is by relation kind only; `EngineeringGraphCommitAttributionTests.cs:10-48` still proves a second primary commit edge is refused and stays unmodified |
| Existing conversations lose their binding until the next write | The header is no longer read as state, and a read never imports (ADR-0014) | Bounded, not general: a conversation the shipped code created for a task already has its `task_session` edge registered by the same route (`WorkbenchApiModels.cs:1988`, `:2227`; `CompatibilityEndpoints.cs:481`) and v7 promotes it, so it keeps its binding through the upgrade. What is lost until the next write is only a *drift* value — a header `taskId` the graph never received — or a binding whose graph file was deleted or rebuilt. Accepted by ADR-0014: any write of that conversation (a turn, a rename, a re-bind) recovers the primary, and ADR-0014's *Reconsider when* owns revisiting an eager import |

## References

- `docs/adr/ADR-0014-session-task-relations-belong-to-the-graph.md`
- `docs/adr/ADR-0009-navigator-sessions-section.md`, `docs/adr/ADR-0010-deleting-a-task-conversation.md`,
  `docs/adr/ADR-0011-engineering-graph-as-the-read-model.md`
- `docs/ui-spec/studio-information-architecture-ui-spec.md` (v1.11)
- `docs/design/engineering-graph-read-model-design.md` — the schema ladder, the projection pattern and
  the migration test shape this design follows
- `docs/plans/20261008-feature-session-task-relations.md` — the work plan under this design
- `src/Agent/Workbench/EngineeringGraph/EngineeringGraphSchema.cs`,
  `src/Agent/Workbench/EngineeringGraph/EngineeringGraphService.cs`,
  `src/Agent/Workbench/EngineeringGraph/EngineeringGraphStore.cs`,
  `src/Agent/Workbench/EngineeringGraph/EngineeringGraphModels.cs`
- `src/ApiHost/EngineeringGraphApi.cs`, `src/ApiHost/WorkbenchApiModels.cs`,
  `src/ApiHost/CompatibilityEndpoints.cs`, `src/ApiHost/TaskCreationTool.cs`
- `src/Agent/Chat/SessionManager.cs`, `src/Agent/Chat/SessionFileFormat.cs`
- `studio/src/api/client.ts`, `studio/src/studio/MainStudio.tsx`,
  `studio/src/studio/workbench/WorkbenchNavigator.tsx`,
  `studio/src/studio/workbench/WorktreeTasksPanel.tsx`,
  `studio/src/studio/workbench/TaskDetail.tsx`

## Update History

| Date | Version | Changes |
|---|---|---|
| 2026-10-08 | 1.0 | Initial design under ADR-0014: the relation lives in the graph with at most one primary; schema v7 promotes existing single relations and adds the partial unique index; `DeleteTask` cleans its edges as closely-related work; `GraphProvenance.Auto`; the relation is projected into `ChatSessionInfo` on every list route with the primary projected into `taskId`/`taskProvenance`; the legacy header field is imported once on a write and cleared; `create_task` establishes an `auto` relation from the conversation's live identity; the model sees the primary in full and other relations by title and status with per-link degradation; one set-replace route with the existing `/task` routes kept as "set the primary"; the UI's filters become set membership and the picker becomes a checked set applied once. |
| 2026-10-10 | 1.1 | The relation model is unchanged, but two of the surfaces it described are not: automatic association never establishes a primary, so a conversation that records tasks without being assigned one keeps none, and the set route carries the caller's own "assigned to none" (`unassigned`) with the picker's `Assigned task` control sending it (ADR-0014 v1.1). The navigator's `SESSIONS` membership is the assignment rather than the relation set, so the statements above about the navigator's set-membership content rule describe the earlier revision; the task page's own `Sessions` list and the picker's checks are still set membership (ADR-0009 v1.7). |
