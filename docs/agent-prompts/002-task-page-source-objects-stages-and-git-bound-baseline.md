# 002. Task page: editable staged source objects with a Git-bound baseline

Status: pending
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

Not yet executed.
