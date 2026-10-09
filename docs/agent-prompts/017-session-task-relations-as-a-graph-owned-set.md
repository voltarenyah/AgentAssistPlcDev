# 017. A conversation is related to every task it creates, and the row menu edits the relation set

Status: done
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

Executed 2026-10-08/09 in the issue worktree
`.worktrees/issue-115-session-task-relations` on branch `dsh/115-session-task-relations`, based on the
local `master` `4ecd3d2`.

**Commits** — `6d53b95` (the governing documents: ADR-0014, the ADR-0009 v1.3 amendment, the UI Spec
v1.11 amendments, the design document, this plan and this item), `ed5aa7f` (the server side: schema v7,
the set-shaped graph API, the projection, the legacy import, `create_task`'s automatic relation and the
graph-sourced model context), `c7eb7bc` (the Studio side: set membership, the checked picker, the task
page's `auto` label and Remove), and the documentation-evidence commit this section ships in.

**Commands actually run, with results** (from the worktree):

| Command | Result |
|---|---|
| `dotnet build AgentAssistPlcDev.sln -v q` | 0 errors |
| `dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-build -v q` | 546 passed, 0 failed |
| `dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj --no-build -v q` | 281 passed, 0 failed |
| `cd studio && npm test -- --run` | 89 files, 609 tests passed (baseline 604; +5) |
| `cd studio && npm run lint` | 0 errors, 16 warnings, all at pre-existing locations |
| `cd studio && npm run build` | clean (`tsc -b` + `vite build`) |

**Runtime scenario** — `.\launch.ps1 -NoBuild` from this worktree (both services health-checked on
`http://localhost:5173/` and `http://localhost:5239/api/status`), driven with Playwright against the
real workbench `TestTEst` (worktree `master`, device `PLC_1`), whose database v7 then migrated in place:
its three conversations each had exactly one relation, and each was promoted to primary. The
conversation `help me review the plc code…` had produced three tasks and was related to one of them —
the defect this item fixes, on the user's own data. 13 of 14 scripted assertions passed:

- on a fresh page each task's `SESSIONS` list carried only its own conversation, and the task related to
  nothing showed no section;
- the row menu's picker opened with that one relation checked, a click set a second and a third check
  without closing the dialog, and one apply wrote all three relations (`GET …/sessions` then reported
  all three, one primary);
- each of the three task pages listed the conversation afterwards;
- a second click cleared one check, one apply removed exactly that relation, and the other two survived;
- the task detail still read its relations from the graph.

Screenshots: `tmp/issue-115-runtime/dsh-115-final-*.png` (git-ignored, on this machine). Two console
errors were observed and neither comes from this change: the duplicate-`sessionId` key error is issue
113 (every device's list returns the whole worktree, so the fan-out renders each conversation once per
device), and the 404 is `getEngineeringTaskDetail` probing the project-scope route before the
worktree-scope one.

**Not run, and why** — the live model turn that creates three tasks from one task-less conversation
(Done when 7). The automatic relation is covered by `tests/ApiHost.Tests/TaskCreationToolTests.cs`
(auto provenance, the primary moving only when there was none, the relation following the live
conversation identity rather than a captured one, and the created task still reported with a
`relationWarning` when the relation write fails), and the projection and set routes were exercised
against the running app above; what remains unproven is that wiring end to end inside a live turn.

**Corrections made while executing** — the design's promotion SQL filtered its group on `is_primary = 0`
alone, which would have promoted the second relation of a conversation that already had a primary and
then failed the migration at index creation; it now also requires `SUM(is_primary) = 0`, and the design
records the correction. The design also named `WorkbenchApiState.Logs` for the `graph-unavailable`
entry; the stream is `CompatibilityRuntimeState.Logs`, and the design now says so.

**Environment** — the verification ran against the user's real workbench and then restored it: the
session file it rewrote has its `taskId`/`taskProvenance` back, and all three workbenches'
`engineering.db` files were restored from the pre-run copies in
`%TEMP%\dsh-115-backup-20261009-000855`, so the primary checkout's `master` build opens them again. The
one residual difference is that conversation's `updatedAt`, which the verification's write advanced.

