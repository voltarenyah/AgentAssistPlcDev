# Work Plan: Session-task relations as engineering-graph edges

Created Date: 2026-10-08
Type: feature
Related Issue/PR: none — direct user request (no GitHub issue); governed by
`docs/adr/ADR-0014-session-task-relations-belong-to-the-graph.md`
Review Scope: `src/Agent/Workbench/EngineeringGraph/*`, `src/Agent/Chat/SessionManager.cs`,
`src/Agent/Chat/SessionFileFormat.cs`, `src/ApiHost/EngineeringGraphApi.cs`,
`src/ApiHost/WorkbenchApiModels.cs`, `src/ApiHost/CompatibilityEndpoints.cs`,
`src/ApiHost/TaskCreationTool.cs`, the Studio surfaces and client named in the Design Doc's change
surface, and their colocated tests

## WorkPlan Review

Plan creation and material updates set this to `pending`. Record `approved` after the user approves the reviewed implementation scope.

- **Status**: approved

## Governing Documents

- Design Doc: `docs/design/session-task-relation-design.md`
- UI Spec: `docs/ui-spec/studio-information-architecture-ui-spec.md` (v1.11, 2026-10-08 — AC-015, AC-017 and AC-019 already amended for a relation set; no further UI Spec change is planned)
- ADR: `docs/adr/ADR-0014-session-task-relations-belong-to-the-graph.md` (the authority; Status `Proposed` as written — its approval is the user's), with `ADR-0009`, `ADR-0010`, `ADR-0011`
- PRD: not applicable (no `docs/prd/`; Structural Scale Medium, convergence record embedded in ADR-0014 and the Design Doc)
- Test skeletons: none generated (existing lanes: `dotnet test tests/Agent.Tests`, `dotnet test tests/ApiHost.Tests`, `cd studio && npm test -- --run`)

## Implementation Scope

A conversation's task relations become `task_session` edges in the workbench's engineering graph as their
only owner, with at most one primary edge; schema version 7 promotes the existing single relation of every
conversation and adds the partial unique index that makes the primary expressible; a task delete cleans the
edges it used to leave behind; ApiHost projects the relation set into every session list and the primary
into `taskId`/`taskProvenance`; every session write imports a legacy header `taskId` once and then clears
it; a conversation's own `create_task` call relates it to the task it created with provenance `auto`; the
model's per-turn task context is read from the graph with the primary in full and other relations named by
title and status; one set-replace route lets the row menu's checked picker write the whole set in one
request; and the Studio filters that compared a single id become set membership. No other behaviour
changes; `SESSIONS`'s heading, absence, hardware, tag-filter and deepest-section rules, and the
conversation's other row operations, are preserved.

## Implementation Phases

### Phase 0: Documentation gate (the documents exist; their approval does not)

#### Tasks

- [ ] **P0-T1 — 2026-10-08: Record the governing documents as approved**
  - **Source**: ADR-0014 *Status*; Design Doc *Update History*; this plan's *WorkPlan Review*
  - **Scope**: documentation only — ADR-0014's `Status` moves from `Proposed` to `Accepted` when the user approves it, and this plan's `WorkPlan Review` moves from `pending` to `approved`. `docs/ui-spec/studio-information-architecture-ui-spec.md` (v1.11) and `docs/design/session-task-relation-design.md` (1.0) are already written.
  - **Depends on**: none
  - **Verification**: read the three documents' status/history lines; no implementation starts while ADR-0014 is `Proposed` and this plan is `pending`
  - **Primary failure**: implementation starts against an unapproved decision, so a later ADR edit invalidates code instead of a document

#### Phase Completion

- [ ] The ADR, UI Spec and Design Doc are the ones this plan cites, and the ADR is no longer `Proposed`

### Phase 1: The graph can express the relation (foundation)

#### Tasks

- [ ] **P1-T1 — 2026-10-08: Add schema v7 — the primary promotion and the partial unique index**
  - **Source**: Design Doc `Selected Design` 1, `Contracts, State, and Persistence`; ADR-0014 *Migration*; AC-001, AC-002
  - **Scope**: `src/Agent/Workbench/EngineeringGraph/EngineeringGraphSchema.cs` — `CurrentVersion = 7`, a `if (version < 7)` block that promotes the `task_session` edge of every conversation with exactly one (`HAVING COUNT(*) = 1`, `is_primary = 0`) and then creates `ux_graph_edges_primary_task_session` as the mirror of `ux_graph_edges_primary_git_commit` (`:91-94`), with the ladder's two failure-injection points (`:143`, `:165` as the pattern); `tests/Agent.Tests/EngineeringGraphStoreTests.cs` — a v6 fixture (the `CreateVersionFiveDatabase` pattern, `:209-253`), the migration case, the promotion case, the idempotent second open, and the failed-v7 byte-identity case
  - **Depends on**: P0-T1
  - **Verification**: `dotnet build AgentAssistPlcDev.sln -v q` (development launcher stopped, per the root `AGENTS.md`) then `dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-build -v q` — a v6 database with three conversations (one relation; two relations already carrying one primary; none) migrates with every task, entity, edge, property, stage and evidence row intact and `is_primary` set only on the single-relation conversation's edge; reopening a v7 database writes nothing; an injected v7 failure leaves the file byte-identical to its pre-migration bytes with a `.bak` beside it and version 6 recorded
  - **Primary failure**: the migration succeeds but the promotion marks the wrong edge, or a second index/promotion run rewrites promoted rows
  - **Observable check**: `SELECT COUNT(*)` per table and the promoted edge's `edge_id` before/after on a copy of a real `engineering.db`

- [ ] **P1-T2 — 2026-10-08: Make `auto` a provenance and let a `task_session` edge be primary**
  - **Source**: Design Doc `Selected Design` 3, `Contracts…` (provenance row); ADR-0014 *Decision Details*; AC-004
  - **Scope**: `src/Agent/Workbench/EngineeringGraph/EngineeringGraphModels.cs:9` (`Auto`); `EngineeringGraphService.cs:1142` (`ParseProvenance` learns `"auto"`), `:265-266` and `:300-301` (the `is_primary` refusal widens to `GraphRelationKind.TaskSession` **only**)
  - **Depends on**: P1-T1
  - **Verification**: `dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-build -v q` — a `task_session` edge round-trips as `GraphProvenance.Auto` (the parse default is what would otherwise downgrade it to `manual`); a primary session edge is accepted; a second primary session edge for the same conversation is refused with `GRAPH_PRIMARY_RELATIONSHIP_EXISTS`; `EngineeringGraphCommitAttributionTests.cs:10-48` still passes unmodified, so a second primary **commit** edge is still refused
  - **Primary failure**: `auto` parses back as `manual`, or the widening lets a second primary commit edge through
  - **Observable check**: `service.GetEdges(Task, taskId, Session)` reports `Auto` after a store round-trip

- [ ] **P1-T3 — 2026-10-09: Set-shaped relation writes and the one-query relation read**
  - **Source**: Design Doc `Selected Design` 2, `Contracts…` (`graph_edges` row); ADR-0014 *Architecture Impact*, *Implementation Guidance*; AC-004, AC-005
  - **Scope**: `EngineeringGraphService.cs` — `AddSessionTask(sessionId, taskId, provenance, makePrimaryIfNone)` (primary set by one `UPDATE … WHERE … NOT EXISTS (… is_primary = 1)`), `RemoveSessionTask(sessionId, taskId)`, `SetSessionTasks(sessionId, taskIds, primaryTaskId)` (replace-as-a-set: delete only dropped pairs, insert missing ones, preserve kept edges, demote-then-promote the primary) and `ListSessionTaskRelations(sessionIds)` (one `IN (…)` query over `graph_edges` reusing `Placeholders`, `:1020-1021`); `ReplaceSessionTask` (`:1031-1059`) removed; `ReplaceTaskRelationship`'s session branch (`:306-310`) stops deleting a conversation's other relations; the per-link validation of `:1035-1043` is reused verbatim; `tests/Agent.Tests/EngineeringGraphConstraintsTests.cs`
  - **Depends on**: P1-T2
  - **Verification**: `dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-build -v q` — two relations on one conversation with one primary; `SetSessionTasks` keeping a pair preserves its edge id, provenance and `created_utc` while deleting only the dropped one; `primaryTaskId` outside the set leaves no primary; an empty set clears every relation; one read returns one conversation's whole set in the primary-first, stable order; the three refusal codes of AC-005 are unchanged
  - **Primary failure**: a write path still deletes the conversation's other relations, or the primary is chosen by iteration order instead of the explicit rule
  - **Observable check**: `SELECT to_id, is_primary FROM graph_edges WHERE to_id = $session AND relation_kind = 'task_session'` after each write

- [ ] **P1-T4 — 2026-10-09: A deleted task stops leaving its edges behind (closely-related work)**
  - **Source**: Design Doc `Selected Design` 1 (*Task deletion cleans its edges*), `Material Risks`; ADR-0014 *Consequences*; AC-003
  - **Scope**: `EngineeringGraphService.cs:199-209` — `DeleteTask` performs the existing `RemoveEntityWithin` cleanup (`:364-370`: properties, then edges in both directions, then the entity row) inside its own transaction, ahead of the `task_source_stages` and `tasks` deletes; the graph test lane asserts it
  - **Depends on**: P1-T3
  - **Verification**: `dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-build -v q` — after `DeleteTask`, no `graph_edges` row references the task from either side, its stages and row are gone, a conversation it related to no longer lists it, and `EngineeringGraphConstraintsTests.ReleasingAStageAllowsTheSameTaskToRestageItAndDeletingTaskReleasesOwnership` (`:129-148`) still passes
  - **Primary failure**: the task row disappears while a conversation still lists it, so a relation points at a task `FindTask` cannot resolve
  - **Observable check**: the two `SELECT COUNT(*) … FROM graph_edges WHERE from_id = $task OR to_id = $task` counts are 0

#### Phase Completion

- [ ] Phase tasks are complete and their verification passes (`dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-build -v q` green)

### Phase 2: Projection, routes and the legacy import

#### Tasks

- [ ] **P2-T1 — 2026-10-09: The session file stops carrying the relation**
  - **Source**: Design Doc `Selected Design` 4 (`ChatSessionInfo`), 6 (`SessionManager` write path); ADR-0014 Decision 1, *Decision Details* (Authority); AC-008, AC-009
  - **Scope**: `src/Agent/Chat/SessionFileFormat.cs` — `ChatSessionInfo` gains the optional relation collection (`TaskId`/`TaskProvenance` keep a null default and stay the projected primary); `src/Agent/Chat/SessionManager.cs` — `WriteSession` (`:218-226`) persists neither legacy field, `CreateNewSession` (`:101-158`) loses its two relation parameters, `ReadSessionInfo` (`:349-350`) stops reading them; call sites at `WorkbenchApiModels.cs:1984-1986`, `:2224-2225` and `CompatibilityEndpoints.cs:854`; `tests/Agent.Tests/SessionManagerTests.cs:97-109` is replaced by the import-and-clear case
  - **Depends on**: P1-T1
  - **Verification**: `dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-build -v q` — a hand-written header carrying `taskId`/`taskProvenance` still **loads** (`LoadSession` keeps the value for the import path), `ListSessions` reports **no** relation for it, and a session written through `SaveSession` persists neither field; the legacy-without-task case (`:111-141`) is unchanged
  - **Primary failure**: `ListSessions` still reports a header-derived relation, which would let the file and the graph disagree again
  - **Observable check**: the persisted JSON of a written session has no `taskId` key, and `ChatSessionInfo.TaskId` is null for a file that still has one

- [ ] **P2-T2 — 2026-10-09: `SessionGraphOperations` becomes the relation API and the projection**
  - **Source**: Design Doc `Selected Design` 4, 5, 6 (`Order inside a set-replace operation`); ADR-0014 *Architecture Impact*; AC-006, AC-007, AC-008
  - **Scope**: `src/ApiHost/EngineeringGraphApi.cs` — `Register(graph, session, provenance, primaryTaskId)` (the header is no longer an input, `:158-165`), `SetTasks`, `ImportLegacy` (the field's only reader), `Project` for `ChatSessionInfo` and `ChatSessionData`, `ValidateCandidate` stops preserving `TaskId`/`TaskProvenance` (`:132`), `SetTask` (`:174-179`) and `ApplyWithPersistence`/`ReplaceSessionTask` calls removed; the `RegisterOverride` hook and the compensation behaviour are preserved; `src/ApiHost/WorkbenchApiModels.cs` list/create/load/task routes project (one query per page) and import before writing, with the graph-unavailable guard returning an empty relation set plus a `graph-unavailable` log entry; `src/ApiHost/CompatibilityEndpoints.cs:465-466`, `:467-486`, `:487-502`, `:503-508`, `:509-517`, `:538-550` likewise; `tests/ApiHost.Tests/WorkbenchEndpointsTests.cs`
  - **Depends on**: P2-T1
  - **Verification**: `dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj --no-build -v q` — a conversation with two relations answered by the device list, the typed device list and `/api/chat/sessions` carries both ids with the primary projected into `taskId`/`taskProvenance`; a create response carries the primary it registered; a conversation whose workbench graph file is replaced by a non-database gets `200` with an empty relation set; a legacy header is imported by the first write (a rename or a turn save) and not by a read, and the file afterwards carries neither field; the existing compensation (`:976-1005`) and persist-failure (`:1013-1040`) cases are extended to the set; `/api/chat/session/info` still answers its count and ids with no graph open
  - **Primary failure**: a route answers with a relation the task page does not read (or the reverse), or a read imports the legacy value
  - **Observable check**: for one conversation, the ids in `GET …/devices/{device}/sessions` equal the ids in the task detail's `sessions`

- [ ] **P2-T3 — 2026-10-10: One route replaces the whole relation set**
  - **Source**: Design Doc `Selected Design` 9, `Contracts…` (set route row); ADR-0014 *Implementation Guidance*; UI Spec AC-019; AC-012
  - **Scope**: `src/ApiHost/WorkbenchApiModels.cs` — `PUT …/devices/{device}/sessions/{session}/tasks` with `{ taskIds, primaryTaskId? }`, the primary-resolution rule, and the response being the conversation with the projected primary; `src/ApiHost/CompatibilityEndpoints.cs` — the compat twin `PUT /api/chat/session/tasks` delegating to the same operation, and both existing `PUT …/task` routes mapped onto `SetSessionTasks` as "set the primary relation"; `tests/ApiHost.Tests/WorkbenchEndpointsTests.cs`
  - **Depends on**: P2-T2
  - **Verification**: `dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj --no-build -v q` — one request moves a conversation from one relation to a three-relation set and back to an empty set; the primary resolves by the documented rule (`primaryTaskId` in the set, else the current primary if still present, else the first id); `taskIds: []` on a legacy-header file leaves no relation and no re-import on the next write; both `/task` routes still set the primary while leaving the other relations intact; an unknown or device-mismatched id is refused with its existing code and nothing is written
  - **Primary failure**: a half-linked conversation is observable (two requests needed for one set change), or the old `/task` routes silently delete the other relations
  - **Observable check**: after one request, `GET …/devices/{device}/sessions` shows exactly the requested set

#### Phase Completion

- [ ] Phase tasks are complete and their verification passes (`dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj --no-build -v q` green)

### Phase 3: The conversation's own work and what the model sees

#### Tasks

- [ ] **P3-T1 — 2026-10-10: `create_task` relates the conversation that called it**
  - **Source**: Design Doc `Selected Design` 7; ADR-0014 Decision 2, *Decision Details* (*Automatic association*); AC-010
  - **Scope**: `src/ApiHost/CompatibilityEndpoints.cs` — `BuildToolCatalog` (`:991-1020`) gains the `Func<string?>` session provider and `EnsureActiveChatAsync` (`:930-983`) supplies it from the live `chats[contextKey]` (the shape of the runtime-context closure, `:973-974`); `src/ApiHost/TaskCreationTool.cs` — the constructor/`CreateSpec` take the provider, `Create` (`:80-113`) writes the `auto` relation after the task write with `makePrimaryIfNone: true`, and a relation failure leaves the created task reported with an additive `relationWarning`; `tests/ApiHost.Tests/OpenTiaProjectToolTests.cs:45-53` and `tests/ApiHost.Tests/TaskCreationToolTests.cs:288-318` (fixture and provider), plus the new cases
  - **Depends on**: P2-T3
  - **Verification**: `dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj --no-build -v q` — an approved call from a conversation with no task adds a relation with provenance `auto` and makes it primary; a call from a conversation already related to another task adds the relation **without** moving the primary; a turn after loading another conversation in the same device scope attributes the relation to the loaded conversation; an injected edge-write failure still returns the created task with `relationWarning`
  - **Primary failure**: the relation is attributed to the conversation that was active when the catalog was built, or a relation failure fails the tool call and hides a task that exists
  - **Observable check**: after the call, `ListSessionTaskRelations(the conversation's id)` names the new task with `auto`, and the primary is unchanged when it existed

- [ ] **P3-T2 — 2026-10-10: The model's task context comes from the graph, per link**
  - **Source**: Design Doc `Selected Design` 8; ADR-0014 Decision 4; AC-011
  - **Scope**: `src/ApiHost/CompatibilityEndpoints.cs:1129-1143` (`TaskContext`) and its call site `:971-976` — the context is built from `ListSessionTaskRelations` for the conversation's live id, the primary block is byte-identical to today's five lines, one bounded `Related tasks:` line names at most ten other resolvable tasks and counts the rest, and a relation whose task is missing or whose worktree-scoped task no longer matches the conversation's worktree or device is skipped without throwing; `tests/ApiHost.Tests/WorkbenchEndpointsTests.cs`
  - **Depends on**: P3-T1
  - **Verification**: `dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj --no-build -v q` — a conversation related to three tasks injects the primary in full and the other two by title and status; a relation to a deleted task and a relation to a task of another device are both skipped and the turn completes (no `TASK_DEVICE_MISMATCH`); a conversation with no resolvable relation injects no task lines; a fifteen-relation conversation names ten and reports the remainder
  - **Primary failure**: a stale relation aborts the turn, or the single-relation context drifts from today's text
  - **Observable check**: the runtime context recorded for a turn contains the primary's five lines and no full block for any other task

#### Phase Completion

- [ ] Phase tasks are complete and their verification passes

### Phase 4: The Studio surfaces read and write the set

#### Tasks

- [ ] **P4-T1 — 2026-10-11: Client types and the set call**
  - **Source**: Design Doc `Change Surface` (`client.ts` row), `Contracts…` (`ChatSessionInfo` row); UI Spec AC-019; AC-012
  - **Scope**: `studio/src/api/client.ts` — `SessionTaskRelation`, `taskRelations?: SessionTaskRelation[]` on `ChatSessionInfo` (and the create/load payload where the primary is projected), `'auto'` in both provenance unions (`:132`, `:145`), `setChatSessionTasks(sessionId, taskIds, primaryTaskId)` beside `setChatSessionTask` (`:2000-2007`)
  - **Depends on**: P2-T3
  - **Verification**: `cd studio && npm run build` (type check) and `npm test -- --run` — every existing session fixture that omits `taskRelations` still type-checks, and the new function's request body is asserted in the caller's test (P4-T2)
  - **Primary failure**: the collection is made required, breaking every pre-existing session fixture, or `'auto'` is missing from a union so a server value fails the type
  - **Observable check**: `npx tsc -b` is clean with the new optional field and `setChatSessionTasks` in place

- [ ] **P4-T2 — 2026-10-11: `SESSIONS` membership becomes a set and the picker becomes checks**
  - **Source**: Design Doc `Selected Design` 10; UI Spec AC-015, AC-019, and the picker row (`:79`); ADR-0009 v1.3
  - **Scope**: `studio/src/studio/workbench/WorkbenchNavigator.tsx` — the content rule (`:692-698`) becomes set membership (`taskRelations.some(…)` for the selected task; no relation for the task-less list), the picker (`:1388-1416`) gains a per-task check seeded from the conversation's relations where a click sets and a second click clears, one apply writes the whole set, and the row menu (`:492-501`, `:1011`) keeps one binding entry instead of attach/reassign plus remove; `studio/src/studio/MainStudio.tsx:1541-1558` becomes `setChatSessionTasks` (device selection, tab update and refresh unchanged); `studio/src/studio/workbench/WorkbenchNavigator.test.tsx`
  - **Depends on**: P4-T1
  - **Verification**: `cd studio && npm test -- --run` — a conversation related to two tasks appears in each task's list and not in the task-less list; the picker's options carry the current checks, a second click clears one, and **one** call carries the whole set with the read primary; the heading, absence, hardware and tag-filter cases are unchanged; then `npm run lint`
  - **Primary failure**: the picker still applies one task at a time (a half-linked conversation through the UI), or the section's heading/absence rules drift
  - **Observable check**: the navigator test's apply assertion shows one call whose `taskIds` is the full checked set

- [ ] **P4-T3 — 2026-10-11: The task surface and the task page follow the set**
  - **Source**: Design Doc `Selected Design` 10; UI Spec AC-015, AC-017; ADR-0014 *Decision Details* (`auto` is removable); AC-010
  - **Scope**: `studio/src/studio/workbench/WorktreeTasksPanel.tsx:349-351` (set membership for the card's disclosure and the list's count), `studio/src/studio/workbench/TaskDetail.tsx:18-24` and `:202-210` (the provenance label gains `auto`; the Remove control covers `manual` **and** `auto`, keeping `default` non-removable here), `studio/src/studio/workbench/WorktreeTasksPanel.test.tsx`, `TaskDetail.test.tsx`, `MainStudio.chatTaskCreate.test.tsx` (the post-turn re-read leaves the created task listing the conversation)
  - **Depends on**: P4-T2
  - **Verification**: `cd studio && npm test -- --run` — a conversation related to two tasks is counted and disclosed by each; an `auto` relation renders its own label and offers Remove; a `default` relation offers none; the created-task case asserts the conversation list is re-read after the turn; then `npm run build` and `npm run lint`
  - **Primary failure**: an automatic relation renders as "Unassigned" or cannot be cleared from either surface
  - **Observable check**: a fixture with one `auto` and one `default` session relation shows the auto label plus one Remove control

#### Phase Completion

- [ ] Phase tasks are complete and their verification passes (`npm test -- --run`, `npm run build`, `npm run lint` green in `studio/`)

### Phase 5: Runtime verification

#### Tasks

- [ ] **P5-T1 — 2026-10-12: Three tasks from one task-less conversation, in the running app**
  - **Source**: Design Doc `Verification Strategy` (last row), `Overview` *Outcome*; ADR-0014 Decision 2; AC-010, AC-012
  - **Scope**: no repository artifact; the running application driven through the browser
  - **Verification**: `.\launch.ps1` (health-check both services: `http://localhost:5173/` and `http://localhost:5239/api/status`), then in a device conversation that is related to no task, ask for findings and approve three `create_task` calls in turn. Confirm: each of the three tasks lists that conversation in its own session list; the conversation's row menu shows all three checks with the first-created as the primary; a second click on one clears it, one apply writes the set, and that task's list loses the conversation while the other two keep it; the conversation's own `SESSIONS` row keeps its heading and the section still shows one task's list at a time; opening a task with the relation shows the `auto` label and its Remove control; the browser console carries no local application error
  - **Depends on**: P3-T2, P4-T3
  - **Primary failure**: the three tasks do not all list the conversation (the automatic relation was never written or was never projected), or the picker cannot clear one binding without clearing the others
  - **Observable check**: three task pages each list the same conversation, and the conversation's picker shows three checks with one primary

#### Phase Completion

- [ ] The scenario above is recorded with its observed result, and any deviation is reported rather than worked around

## Completion Criteria

- [ ] Every Design Doc obligation needed for implementation is covered by at least one task
- [ ] Every task cites each directly constraining governing section and its applicable ACs
- [ ] Every task produces a repository implementation outcome required by the Design Doc
- [ ] Dependencies permit execution in the listed order (Phase 1 → 2 → 3 → 4 → 5; P2-T1 only needs P1-T1, because the writers it changes are independent of the set API)
- [ ] Verification is executable from repository artifacts or the task's own output; the runtime scenario is marked as the one operator-run check
- [ ] Task verification passes and the cited acceptance criteria are satisfied
