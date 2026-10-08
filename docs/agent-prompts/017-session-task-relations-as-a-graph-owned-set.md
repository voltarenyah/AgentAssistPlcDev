# 017. A conversation is related to every task it creates, and the row menu edits the relation set

Status: in-progress
Created: 2026-10-08
Depends on: `docs/adr/ADR-0014-session-task-relations-belong-to-the-graph.md` (accepted)

## Goal

A conversation's relation to tasks stops being a single binding. A conversation that created three
tasks from its own findings is related to all three, each of those tasks lists that conversation, and
the conversation's 3-dots menu binds and clears those relations as a set — a second click on a checked
task clears it.

Before: `create_task` recorded the tasks and the conversation stayed related to none of them, because
the tool never knew which conversation called it. `SESSIONS` listed only the conversations whose single
`taskId` matched the selected task, and the row menu's picker replaced that one id. After: the relation
is the graph's, a conversation's own task creation establishes one automatically, and both surfaces that
list conversations read the same set.

## Context

- **The decisions are already made** — `docs/adr/ADR-0014-session-task-relations-belong-to-the-graph.md`
  (graph is the only authority; one primary relation per conversation; the Workbench Assistant's
  conversation out of scope; the model sees one task in full and the rest by title and status), and
  `docs/adr/ADR-0009-navigator-sessions-section.md` v1.3 for the row menu. The UI contract is
  `docs/ui-spec/studio-information-architecture-ui-spec.md` v1.11 (AC-015, AC-017, AC-019).
- **Implementation design and task order** — `docs/design/session-task-relation-design.md` and
  `docs/plans/20261008-feature-session-task-relations.md`. Read both before editing; they are the
  authority for ordering, contracts and proof per task.
- **Why the current behaviour is what it is** — `src/ApiHost/TaskCreationTool.cs:80-113` writes the
  task and nothing else, and the tool is constructed with a device provider only
  (`src/ApiHost/CompatibilityEndpoints.cs:1009-1011`), so no session identity reaches it. The
  conversation's session is in hand where the catalog is built (`:942-980`) but is swapped in place
  afterwards without rebuilding the loop (`:859-872`, `:1145-1162`).
- **Where the one-to-one is enforced** — `ReplaceSessionTask` deletes every edge into the session
  (`src/Agent/Workbench/EngineeringGraph/EngineeringGraphService.cs:1044-1058`),
  `ReplaceTaskRelationship` deletes every `task → session` edge for a session target (`:306-310`), and
  `ApplyWithPersistence` reads with `SingleOrDefault()` (`src/ApiHost/EngineeringGraphApi.cs:138`).
  The schema permits several (`EngineeringGraphSchema.cs:71`, `:91-94`; `CurrentVersion = 6`).
- **Where the read shapes assume one** — `ChatSessionInfo.TaskId`
  (`src/Agent/Chat/SessionFileFormat.cs:44`), the navigator's filter
  (`studio/src/studio/workbench/WorkbenchNavigator.tsx:695`), the task surface's filter
  (`studio/src/studio/workbench/WorktreeTasksPanel.tsx:350`), the model's per-turn task context
  (`src/ApiHost/CompatibilityEndpoints.cs:1129-1143`), and the single-choice picker
  (`WorkbenchNavigator.tsx:1389-1420`, `MainStudio.tsx:1541-1558`).
- **Tests that assert the one-to-one** — `tests/ApiHost.Tests/WorkbenchEndpointsTests.cs:675`, `:709`,
  `:871-898`, `:971`, `:1038`; `tests/Agent.Tests/EngineeringGraphCommitAttributionTests.cs`;
  `tests/Agent.Tests/SessionManagerTests.cs:107`, `:139`;
  `studio/src/studio/workbench/WorkbenchNavigator.test.tsx`,
  `studio/src/studio/workbench/WorktreeTasksPanel.test.tsx`,
  `studio/src/studio/workbench/TaskDetail.test.tsx`,
  `studio/src/studio/MainStudio.chatTaskCreate.test.tsx`.

## Constraints

- The engineering graph is the only authority for the relation. No write path stores it in the session
  file, and the file's `taskId`/`taskProvenance` are read only as a one-time legacy import, never by a
  read (ADR-0014).
- At most one relation per conversation may be primary, and automatic association never moves the
  primary — it only adds. The primary is the only task whose full context the model receives.
- The Workbench Assistant's own conversation is out of scope; only conversations the worktree session
  store owns take part.
- Existing public routes stay backward compatible: the single-task routes keep working as "set the
  primary", and existing readers of `ChatSessionInfo.taskId` keep working against the projected primary.
- One stale, mismatched or deleted relation must not be able to fail a turn or a list read.
- Never weaken an existing assertion to make a new behaviour pass: extend the one-to-one cases into
  set cases with the same coverage.
- Do not add a runtime dependency for the picker (studio/AGENTS.md); extend the existing Dialog/cmdk
  picker and the existing Shadcn primitives.
- Preserve unrelated uncommitted work; the repository root carries an untracked screenshot
  (`sessions_rows_zoom.png`) that belongs to the user.

## Done when

1. The graph holds a conversation's relation set: several `task → session` edges per conversation, at
   most one primary, with the per-link validation (task exists, worktree compatible, device match) that
   the single-relation path enforced. Proven by `tests/Agent.Tests` covering add/remove/set, the
   primary uniqueness, and the schema-7 migration of an existing single edge.
2. Every session-list route projects the set: `ChatSessionInfo.taskId`/`taskProvenance` carry the
   primary and a new collection carries all relations with their provenance, including the documented
   behaviour when the graph cannot be opened. Proven by `tests/ApiHost.Tests` for the per-device list,
   the compatibility list and the single-session routes.
3. A device conversation that creates a task through `create_task` is related to it with provenance
   `auto`, without the primary being moved when it already had one. Proven by an ApiHost test that
   drives the tool through a conversation and then reads the relation back.
4. The model's task context carries the primary task in full and every other related task by title and
   status; a relation that no longer matches the conversation's device is skipped instead of failing
   the turn. Proven by a test on the context builder.
5. `SESSIONS` and the worktree task surface list a conversation under every task it is related to, and
   the row menu's picker sets and clears checks with the whole set applied in one operation. Proven by
   the colocated vitest cases (`WorkbenchNavigator.test.tsx`, `WorktreeTasksPanel.test.tsx`,
   `TaskDetail.test.tsx`).
6. `dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj --no-build -v q`,
   `dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-build -v q`,
   `npm test -- --run`, `npm run lint` and `npm run build` (in `studio/`) pass.
7. Runtime proof, not compilation alone: with `.\launch.ps1` running, a conversation with no task
   creates three tasks in one conversation, and all three tasks list that conversation in their
   `Sessions` section while the conversation's row menu shows all three checks set; a second click on
   one check then removes only that relation. Record the screenshot and the exact steps in Evidence.

## Evidence

Filled in by the executing agent: branch, commits, commands run with their results, the runtime
scenario's steps and screenshot, skipped steps and remaining risk.
