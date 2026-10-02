# 002. Task page: editable staged source objects with a Git-bound baseline

Status: done
Created: 2026-09-30
Depends on: none

## Goal

Opening a worktree task shows the source objects the task currently stages — its task-scoped TIA
compare basis — and lets the user add, remove, and take over objects from a searchable picker. A
newly added object acquires a fingerprint baseline bound to the object's committed Git content, so
`POST /api/workbenches/{id}/worktrees/{wt}/tasks/{taskId}/compare-tia` stops failing with
`TASK_STAGE_BASELINE_MISSING` and can actually report task differences.

## Context

Governing documents (accepted, and partially unimplemented — this item implements the missing part):

- `docs/adr/ADR-0003-task-scoped-source-evidence.md` — "device-bound task stages and sparse
  per-object evidence"; a commit records fingerprint evidence "bound to their committed source
  content", never a full live snapshot.
- `docs/design/task-scoped-tia-compare-design.md` — AC-001 (task compare reads only active stages),
  change surface row for `WorktreeTasksPanel` / version-control components.
- `docs/ui-spec/task-scoped-tia-compare-ui-spec.md` — row "Task stage | User opens a task | Shows
  staged objects and their current stage status; removing an item releases active ownership only."

Backend that already exists and works:

- Table `task_source_stages` with the unique index `(worktree_id, source_object_id) WHERE
  released_utc IS NULL` — one active owner per source object per worktree:
  `src/Agent/Workbench/EngineeringGraph/EngineeringGraphSchema.cs:110-134`.
- Stage operations `StageSourceObject` / `ListActiveStages` / `ReleaseSourceStage` /
  `UpdateStageEvidence`: `src/Agent/Workbench/EngineeringGraph/EngineeringGraphService.cs:66-112`.
- Routes `GET/POST .../tasks/{taskId}/stages`, `DELETE .../stages/{sourceObjectId}`, `POST
  .../tasks/{taskId}/compare-tia`: `src/ApiHost/WorkbenchApiModels.cs:901-941`.
- Scoped compare reads only active stages and returns candidates/problems with codes
  `TASK_STAGE_EMPTY`, `TASK_STAGE_BASELINE_MISSING`, `TASK_STAGE_BASELINE_INVALID`,
  `TASK_STAGE_MISSING`, `TASK_STAGE_INVALID`: `src/Agent/Workbench/WorkbenchCoordinator.cs:3595-3656`.
- Commit gate: a commit carrying a `taskId` may contain only actively staged source objects
  (`TASK_COMMIT_STAGE_MISMATCH`): `src/ApiHost/WorkbenchApiModels.cs:1166-1179`.

The three gaps this item closes:

1. `baseline_evidence_json` has exactly one writer, and it runs **after** a commit:
   `WorkbenchCoordinator.cs:3503-3511` → `:4248` (captures live TIA evidence for the staged objects
   and writes it back). A stage created by hand therefore has a null baseline, and
   `CompareTaskWithTiaAsync` rejects it at `:3615-3619`.
2. The message that error tells the user to follow — "Run a full scan and assign it before task
   comparison" — has no implementation: there is no assign route in `src/ApiHost` (`assign` matches
   only workbench-tag assignment and `ReassignTaskRelationship`), and no `unassigned` concept exists
   in any C# file.
3. No frontend calls the stage API: `listTaskSourceStages` (`studio/src/api/client.ts:1402`) and
   `compareTaskWithTia` (`:1411`) have zero callers. `TaskDetail` renders `detail.sourceObjects`,
   which is the graph *traceability* edge list, not the active stages
   (`studio/src/studio/workbench/TaskDetail.tsx:92-96,167`).

Baseline semantics, and the reason for them: ADR-0003 binds a stage baseline to **committed source
content**. Taking the live TIA fingerprint at add time would mark an object that the user already
changed in TIA but never committed as "in sync", hiding exactly the change ADR-0003 exists to
protect. The committed fingerprint is already on disk: the export manifest records the TIA
fingerprints captured at export time (`src/Contracts/Engineering/FingerprintModels.cs:3-5`,
`src/Mcp.Engineering/Adapter/TiaV17Adapter.cs:1655-1665,1701-1711`,
`src/Mcp.Engineering/Export/ExportMetadata.cs:185`, `ExportManifest.cs:119`), and the read side
already parses it (`src/Agent/Workbench/DeviceSnapshot.cs:173-237`, `:259`). Build the baseline
`ManagedSourceEvidenceObject` (`src/Contracts/Engineering/ManagedSourceEvidence.cs:23-42`) from that
committed manifest content, not from a live capture.

Reusable UI, and the picker's data source:

- The searchable-picker pattern to follow is `CommandDialog` + `CommandInput` + `CommandList` in
  `studio/src/studio/workbench/tags/TagPicker.tsx:59-82`, with removable chips as in
  `studio/src/studio/workbench/WorktreeLandingPage.tsx:341-363`.
- Filtering is a pure, already-tested module: `filterSourceObjects`, `SOURCE_TYPE_FILTERS`
  (OB/FB/FC/DB/Tags/UDT), `countSourceObjectsByType`, `limitSourceObjects` in
  `studio/src/studio/plcSourceState.ts`; its tests are `plcSourceState.test.ts`.
  `studio/src/studio/PlcSourcePanel.tsx:193-232` renders the same list but is coupled to the device
  snapshot and is not a dialog — reuse the pure helpers, not that component.
- There is no worktree-level source-object listing route; the only reads are the per-device
  `DeviceSnapshot` (`WorkbenchApiModels.cs:1694-1705`). The stage route already enumerates a device's
  manifest objects internally and registers them as `"{deviceId}:{sourceId}"` entities
  (`WorkbenchApiModels.cs:925-930`, `DeviceSnapshot.cs:173-237`).
- Take-over semantics already exist: release the current owner, then stage for the new task
  (`studio/src/studio/PlcSourcePanel.tsx:158-159`).

## Constraints

- Keep the `task_source_stages` columns, the unique index, the compare response contract
  (`candidates`, `candidateExports`, `problems`, `observedSoftwareChecksum`) and every existing error
  code unchanged.
- Keep the existing client signatures usable. `stageTaskSourceObject` may gain an optional argument;
  the four-argument calls in `PlcSourcePanel.tsx:150` must keep working, and its
  release-then-stage take-over behavior must stay correct.
- A stage baseline is derived from committed Git content. When the object has no committed content
  yet (a block that exists only in TIA), leave the baseline null and let the UI state that the object
  must be committed once or resolved by a project-wide scan first — do not fabricate a baseline from
  live TIA. The existing `TASK_STAGE_BASELINE_MISSING` code remains the correct signal there.
- Staging still requires a device-bound worktree task. A project-scope task (no worktree, no device)
  shows no Source objects section and one sentence explaining why, instead of disabled controls.
- Section naming on the task page, to avoid two sections with the same meaning:
  - **Source objects** — the active stages, editable (this item).
  - **Commits** — the task's commit edges (item 004 enriches it).
  - **Related records** — the remaining traceability edges (`detail.sourceObjects`,
    `detail.svnRevisions`), read-only with the existing manual-only Remove.
- Do not implement ADR-0007 (`targetKind`) and do not relax the rule that a task without a device
  cannot stage source objects.
- Follow `docs/STYLEGUIDE.md`; compose `studio/src/components/ui/` primitives; add no dependency.

## Done when

1. Component tests (colocated under `studio/src/studio/`) prove: the Source objects section lists the
   active stages with name, category and owning device; the empty state offers an add action; the add
   action is a searchable dialog whose query and type chips filter the list; Remove releases the
   stage; an object owned by another task shows its owner and requires an explicit take-over.
2. A server test proves a stage created for an object that exists in the committed manifest ends with
   a non-null `baseline_evidence_json` equal to that object's fingerprint evidence in the manifest at
   Git HEAD (place it beside the existing engineering-graph tests).
3. An integration assertion proves `CompareTaskWithTiaAsync` no longer reports
   `TASK_STAGE_BASELINE_MISSING` for such a stage, and still reads only the task's staged ids
   (AC-001). Use the existing fake engineering client; TIA is not required.
4. `cd studio && npm test -- --run` and `npm run build` pass; `dotnet test
   tests/ApiHost.Tests/ApiHost.Tests.csproj --no-build -v q` passes.
5. Runtime check with `.\launch.ps1`: open a worktree task, add one source object, and see it in the
   Source objects section. Record whether TIA was available; if a step could not run, state which one
   and what risk remains.

## Evidence

Branch `codex/002-task-stage-baseline`; commits `09becc0` (backend) and `d2260eb` (frontend) on base
`4e5409d`. Both halves landed; nothing is left calling an endpoint that does not exist.

### What changed

- `src/Agent/Workbench/CommittedSourceManifest.cs` (new) — pure, tolerant reader of one object's
  committed fingerprint evidence out of a device export manifest's JSON text.
- `src/Agent/Workbench/WorkbenchCoordinator.cs` — `StageTaskSourceObjectAsync` derives the stage
  baseline from the manifest **blob at Git HEAD** (`vc_show_file` with `commitSha: null`, never the
  working-tree file, never a live TIA capture) and stages in one write; the
  `TASK_STAGE_BASELINE_MISSING` message now states the real remedy (commit the object once) instead
  of the unimplemented "run a full scan and assign it".
- `src/Agent/Workbench/EngineeringGraph/EngineeringGraphService.cs` + `EngineeringGraphModels.cs` —
  `ListWorktreeActiveStages(worktreeId)` and the `WorktreeSourceStage(Stage, TaskTitle)` record.
- `src/ApiHost/WorkbenchApiModels.cs` — the stage POST route stages through the coordinator; new
  worktree-level stage listing route.
- `src/ApiHost/EngineeringGraphApi.cs` — `WorktreeSourceStageApiResponse`; the stage request's
  `BaselineEvidenceJson` is documented as accepted-but-ignored.
- `studio/src/studio/workbench/TaskSourceObjectsSection.tsx` (new) + colocated test — the editable
  Source objects section and its picker.
- `studio/src/studio/workbench/TaskDetail.tsx` — Source objects / Commits / Related records.
- `studio/src/api/client.ts`, `studio/src/studio/MainStudio.tsx` — client function and wiring.
- Tests: `tests/Agent.Tests/TaskStageBaselineTests.cs`, `tests/Agent.Tests/CommittedSourceManifestTests.cs`.

### API surface items 003, 004 and 005 build on

Routes (all under `/api/workbenches/{id}/worktrees/{wt}`):

| Route | Body / query | Response |
|---|---|---|
| `GET .../tasks/{taskId}/stages` (unchanged) | — | `TaskSourceStageApiResponse[]`: `{ taskId, sourceObjectId, deviceId, baselineEvidenceJson, stagedUtc }` |
| `POST .../tasks/{taskId}/stages` (behavior changed) | `{ sourceObjectId, baselineEvidenceJson? }` — the baseline is **always server-derived**; a client value is ignored | 201 `TaskSourceStageApiResponse` |
| `DELETE .../tasks/{taskId}/stages/{sourceObjectId}` (unchanged) | — | 204, or 404 `TASK_STAGE_NOT_FOUND` |
| **NEW** `GET .../source-stages` | — | `WorktreeSourceStageApiResponse[]`: `{ taskId, taskTitle, sourceObjectId, deviceId, baselineEvidenceJson, stagedUtc }` (active stages of the whole worktree, with the owning task) |
| `POST .../tasks/{taskId}/compare-tia` (unchanged) | — | `{ taskId, deviceId, candidates, candidateExports, problems, observedSoftwareChecksum }` |

Client (`studio/src/api/client.ts`):

- **NEW** `listWorktreeSourceStages(workbenchId, worktreeId): Promise<WorktreeSourceStage[]>`; type
  `WorktreeSourceStage = { taskId, taskTitle, sourceObjectId, deviceId, baselineEvidenceJson: string | null, stagedUtc }`.
- Unchanged and still usable: `listTaskSourceStages(wb, wt, taskId)`,
  `stageTaskSourceObject(wb, wt, taskId, sourceObjectId, baselineEvidenceJson?)`,
  `releaseTaskSourceObject(wb, wt, taskId, sourceObjectId)`, `compareTaskWithTia(wb, wt, taskId, operationId?)`.

Component: `studio/src/studio/workbench/TaskSourceObjectsSection.tsx` (default export). Props:
`{ workbenchId, worktreeId, taskId, deviceId, deviceName?, onChanged? }`. It loads its own data
(`listTaskSourceStages` + `listWorktreeSourceStages` + `getDeviceInfo`) and refreshes itself;
`onChanged` fires after every stage/release/take-over.

Task page (`TaskDetail.tsx`) — new optional prop `onStagesChanged?: () => void`; `MainStudio.tsx`
passes `() => void reloadTaskDetail()`.

| Section | `aria-label` | Rows |
|---|---|---|
| Source objects (editable stages) | `Source objects` | add `Add source object`; picker input `Search source objects`; type chips `Filter by <SOURCE_TYPE_FILTERS label>` (`Filter by Tag table`, `Filter by UDT`, …); stage item `Stage <category> <name>`; take-over `Take over <category> <name> from <taskTitle>`; remove `Remove source object <name>`; a null baseline renders `data-testid="stage-baseline-missing"` |
| Commits | `Commits` | unchanged: `Open Commits <id>`, `Remove Commits <id>` |
| Related records (was: separate `Source objects` / `SVN revisions` traceability sections) | `Related records` | `Open Source object <id>` / `Open SVN revision <id>`, `Remove Source object <id>` / `Remove SVN revision <id>`; navigation kinds unchanged (`sourceObject`, `svnRevision`); Remove stays manual-only |

A project-scope task, and a hardware worktree task, render **no** `Source objects` section: one
paragraph explains why, with no disabled controls.

Backend symbols for 005 (agent staging): `WorkbenchCoordinator.StageTaskSourceObjectAsync(workbenchId,
worktreeId, taskId, sourceObjectId, ct)`; `CommittedSourceManifest.TryReadObjectEvidence(manifestJson,
sourceObjectId)`; `EngineeringGraphService.ListWorktreeActiveStages(worktreeId)` returning
`WorktreeSourceStage` (`Agent.Workbench.EngineeringGraph`). `task_source_stages` columns, its unique
index, the compare response contract, and every error code are unchanged; no new error code was added.

### Done when

1. **Passed.** `cd studio && npm test -- src/studio/workbench/TaskSourceObjectsSection.test.tsx`
   → 6 passed: name/category/device row, empty-state add action, query+type-chip filtering and staging
   from the dialog, Remove releases, owner + explicit take-over, load failure + retry. `TaskDetail.test.tsx`
   → 8 passed (section names, project-scope explanation, no Source objects section for a project task).
2. **Passed.** `dotnet test tests/Agent.Tests/Agent.Tests.csproj --filter "FullyQualifiedName~TaskStageBaseline"`
   → `StageBindsTheBaselineToTheManifestAtGitHeadNotTheWorkingTree` asserts the read is
   `vc_show_file` on `devices/PLC_1/source/metadata.json` with `commitSha: null` (HEAD), that the stored
   `baseline_evidence_json` equals the manifest's `Code`/`Interface`/`Comments` fingerprints, and that a
   different working-tree manifest on disk is not used. Two further tests prove a null baseline for an
   object with no committed manifest content, and for one the committed manifest does not list.
   `CommittedSourceManifestTests` (6 passed) covers the parser, the Instance-DB exclusion, the legacy
   canonical fingerprint string, and that the evidence lookup resolves the same stable ID as
   `DeviceSnapshotReader.ReadManifestSourceObjects`.
3. **Passed.** `CompareReadsOnlyTheStagedIdsAndNoLongerReportsAMissingBaseline`: with one staged object
   and a second registered but unstaged object, `CompareTaskWithTiaAsync` returns no problems, the
   candidate from the fake engineering client, and requests exactly `sourceObjectIds: ["block-main"]`
   (AC-001) with the committed manifest evidence as the comparison baseline. The fake engineering client
   was used; TIA was not required. `CompareStillReportsBaselineMissingForAnObjectWithNoCommittedContent`
   proves the code remains the signal and that TIA is not called.
4. **Passed.** `cd studio && npm test -- --run` → 84 files, 539 passed. `npm run build` (`tsc -b && vite
   build`) → built, 0 errors. `npm run lint` (oxlint) → 0 errors, 15 pre-existing warnings, none in the
   changed files. `dotnet build AgentAssistPlcDev.sln -v q` → 0 errors. `dotnet test
   tests/ApiHost.Tests/ApiHost.Tests.csproj --no-build -v q` → 177 passed. `dotnet test
   AgentAssistPlcDev.sln --no-build -v q` → all suites passed except `E2E.Tests` (14 failed / 2 passed,
   pre-existing and unrelated — see below).
5. **SKIPPED — not run, not verified.** Deferred by the unattended-run rule for this work unit: no TIA
   is available and `.\launch.ps1` would take the shared ports 5173/5239, so no runtime/browser step was
   performed. Residual risk: the section's live rendering against a real device snapshot, the real
   `vc_show_file` round trip for a committed manifest, and the picker's behavior with a large device
   manifest are proven only by unit/component tests and the fakes above. A human must run
   `.\launch.ps1`, open a worktree task, add one source object, and confirm it appears in the Source
   objects section (and, if TIA is available, that `compare-tia` reports differences rather than
   `TASK_STAGE_BASELINE_MISSING`).

### Notes, decisions and risks

- **Decision:** the request's optional `baselineEvidenceJson` is kept for wire compatibility but
  ignored — the stored baseline is always the server-derived committed one, so a caller-supplied
  (live-TIA) baseline can never become task evidence. `StageBindsTheBaselineToTheManifestAtGitHeadNotTheWorkingTree`
  pins the derivation; the ignore is by construction (the route never forwards the field).
- **Decision:** the read is `vc_show_file` at HEAD for every stage, not the working-tree file. An
  uncommitted export therefore never becomes a baseline, which is what ADR-0003 requires.
- **Decision:** the new worktree-level listing route was necessary because nothing exposed "who owns
  this source object" — the graph traceability edge survives a release and cannot answer it.
- **Residual risk (semantics):** an ordinary task commit commits only source XML (`CommitSourceAsync`
  normalizes to source paths), so the tracked manifest is refreshed by the savepoint/baseline path and
  by export flows, not by every source commit. A baseline derived from the HEAD manifest can therefore
  describe the last exported/savepointed content rather than the immediately preceding XML commit. This
  is inherent to the item's own rule ("the manifest's fingerprint evidence at Git HEAD") and is the only
  committed fingerprint evidence available; the after-commit writer
  (`TryRecordTaskStageEvidenceAsync`) still refreshes a stage to its committed content.
- **Residual risk (degradation):** `TryReadCommittedStageBaselineAsync` degrades to a null baseline when
  the manifest cannot be read at all, so a broken version-control layer produces stages that compare
  reports as `TASK_STAGE_BASELINE_MISSING` rather than a hard staging failure.
- **Unrelated, pre-existing, not fixed:** `tests/E2E.Tests` fails 14/16 with
  `System.InvalidOperationException : close_session` because the E2E `EngineeringBoundary` fake has no
  `close_session` case; the call site (`WorkbenchCoordinator.cs:682-686` at the base commit) is outside
  this item's diff. Recommend a separate item.

