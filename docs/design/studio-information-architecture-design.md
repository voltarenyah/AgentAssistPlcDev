# Design Document: Studio information architecture

## Overview

- Outcome: the left navigator renders the current scope cascade as flat, independently collapsible
  sections (`PROJECTS`, `WORKTREE`, an unbound-task group when present, `DEVICE`, `TASKS`), each with
  its own header action and its rows keeping a 3-dots menu, while the gated device/hardware subtree is
  replaced and the operations it held are re-homed into those menus.
- Scope: `studio/src/studio/workbench/WorkbenchNavigator.tsx` and its render site in
  `studio/src/studio/MainStudio.tsx`, plus the task-create entry that must pre-bind a device.
- UI Spec: `docs/ui-spec/studio-information-architecture-ui-spec.md`
- Governing ADRs: `docs/adr/ADR-0006-studio-navigator-ownership-and-shape.md`,
  `docs/adr/ADR-0007-task-target-model.md`

## Requirement Boundary

- PRD or convergence carrier: **user-decided deviation — no PRD.** ADR-0006 and ADR-0007 are the
  requirement carriers for this change; the deviation is recorded here so the documentation path is not
  silently shortened.
- Current requirements: four-level cascade; flat sections with independent collapse and bounded
  scroll; per-section header actions; unbound-task group only when such tasks exist; filter-time
  visibility rule; every section row keeps a 3-dots menu for that object's operations; the device
  operations the removed subtree held become reachable again; the `DEVICE` section presents the
  worktree's hardware as one row; a worktree task carries an explicit target kind so a hardware task is
  distinguishable from a program task and from an unbound task.
- Non-goals: main-area view or tab-strip ownership; changing the worktree `Tasks` view; automatic
  collapse of shallower sections; persisting collapse state; per-PLC hardware granularity (one hardware
  target per worktree); any other change to feature-page contents.
- Open requirement fields: none.

## Acceptance Criteria

- **AC-001** — **When** the navigator renders, the system shall show a collapsible `PROJECTS` section
  listing every workbench with a create-workbench action in its header. Source: confirmed scope.
- **AC-002** — **When** a workbench is selected, the system shall reveal its `WORKTREE` section with a
  create-worktree action and shall not show deeper sections for a different workbench. Source: confirmed scope.
- **AC-003** — **If** the selected worktree has at least one task whose `deviceId` is null and whose
  target is not hardware, **then** the system shall list those tasks in an unbound-task group under
  `WORKTREE` with no creation action. Source: confirmed scope.
- **AC-004** — **When** a worktree is selected, the system shall reveal a flat `DEVICE` section listing
  that worktree's PLC devices plus one `Hardware` row, whose header exposes refresh only. Source: ADR-0006.
- **AC-005** — **When** a PLC device or the `Hardware` row is selected, the system shall list that
  target's tasks in `TASKS` and shall open task creation with that target already bound. Source: confirmed scope.
- **AC-006** — **While** the tag filter is active, the system shall show only `PROJECTS` and `WORKTREE`
  in the navigator. Source: confirmed scope.
- **AC-007** — **When** a section header is activated, the system shall collapse or expand only that
  section and shall leave the other sections' state and scroll position unchanged. Source: confirmed scope.
- **AC-008** — **When** a `DEVICE` row's 3-dots menu is opened, the system shall offer that device's
  operations, restoring the ones the removed subtree held. Source: confirmed scope.
- **AC-009** — **When** the `Hardware` row is selected, the system shall open that worktree's hardware
  configuration page and list only hardware-target tasks. Source: confirmed scope, ADR-0007.
- **AC-010** — **When** a worktree task is created with the hardware target, the system shall accept it
  without a device and shall list it under the `Hardware` row rather than in the unbound-task group.
  Source: ADR-0007.
- **AC-011** — **If** a worktree task is created without a target that resolves to a registered device or
  to hardware, **then** the system shall keep rejecting it with `TASK_DEVICE_REQUIRED`. Source: preserved
  contract in `WorkbenchApiModels.cs:872`.

## Existing Evidence

| Evidence | Location | Design effect |
|---|---|---|
| `showLegacyDeviceTree = false` gates the whole device/hardware block, so its rows are dead markup today | `WorkbenchNavigator.tsx:116`, `:530-622` | Replace the block with the flat `DEVICE` section, and re-home the operations it holds rather than deleting them: the seven device callbacks return in the device row menu, the three hardware callbacks on the `Hardware` row. |
| The hardware pages have no reachable entry point | `onSelectHardware` wired only at `MainStudio.tsx:2359`, used only inside the gate above; the shell still renders the pages at `MainStudio.tsx:2440-2483` | AC-009 restores the entry as a `DEVICE`-section row, so the shell's hardware pages stop being dead ends. |
| A worktree task must name a registered device | `WorkbenchApiModels.cs:872-873` (`TASK_DEVICE_REQUIRED`) | The create check becomes conditional on the target kind; the hardware target is the only newly accepted case and the rejection path is preserved (AC-011). |
| The task table's `device_id` is nullable and every same-device rule tolerates null | `EngineeringGraphService.cs:33-59`, `:211`, `:252`, `:285`, `:314`, and the source-stage guard at `:69` | A hardware task needs no new graph concept: it reuses the null-tolerant relationship rules and is already excluded from source staging. |
| Hardware changes already produce a Git commit, and `TaskCommit` already exists | `WorkbenchCoordinator.cs:2145-2205`; `EngineeringGraphModels.cs:7` | A hardware task's evidence is commits through the existing relation; no staging or source comparison is involved. |
| Every graph worktree task already carries `deviceId` | `GET …/worktrees/{wt}/engineering-tasks` response | The `DEVICE → TASKS` level is a client-side filter over already-loaded data; no fetch or contract change. |
| Tasks reach the UI from the graph endpoint | `MainStudio.tsx:1052`, `:1131` | The cascade derives from `tasksByWorktree`; no new loading state. |
| `viewKind` exists to drive the active-row highlight | `WorkbenchNavigator.tsx:65`, `:358` | The cascade level replaces it; the prop can be dropped. |
| Tag filtering is server-owned and catalog-level | `WorkbenchNavigator.tsx:69-73`, `filterControl` | AC-006 is a visibility switch, not a new query. |
| Dense row and section styling already exists | `studio/src/assets/main.css`, `docs/STYLEGUIDE.md` | Sections reuse existing tokens and row patterns; no new visual primitive. |
| Colocated vitest suite and strict `tsc -b` | `studio/AGENTS.md` | Verification lane for AC-001…AC-008. |

## Design

### Selected Design

`WorkbenchNavigator` keeps its fixed regions — title row, landing-page button, refresh button, and the
caller-owned filter control — and replaces the single tree with one scroll region holding sections.

A new internal `NavigatorSection` renders `{ id, title, action?, collapsed, onToggle, children }`. Its
header is a native button exposing `aria-expanded` and `aria-controls`; its body is a bounded scroll
region with a sticky header, so headers never scroll away.

Section content is derived, never duplicated. Every section is a pure function of the selection one
level above it:

| Section | Visible when | Content | Header action |
|---|---|---|---|
| `PROJECTS` | always | `workbenches` | create workbench |
| `WORKTREE` | a workbench is selected | that workbench's `worktrees` | create worktree for it |
| unbound group | the selected worktree has tasks with `deviceId == null` | those tasks | none — new worktree tasks require a device |
| `DEVICE` | a worktree is selected | `devicesByWorktree[key]` | refresh |
| `TASKS` | a device is selected | `tasksByWorktree[key]` filtered by `deviceId` | create task pre-bound to that device |

Collapse state is a component-local `Set<SectionId>`, empty by default so every reachable section
starts expanded. It is deliberately not persisted (non-goal), and each section's state is independent,
which is what makes the deferred "collapse everything shallower than the active scope" rule addable
without a relayout.

When the tag filter is active, the navigator renders only the `PROJECTS` and `WORKTREE` sections from
the existing server-owned results, so filtering stays a selection-mode switch rather than a second
query path.

Selecting an item clears the deeper selections, because the deeper sections describe the previous
parent. Task and device selection remain what they are today: choosing a device enters that device's
workspace, choosing a task opens its detail.

### Change Surface

| Responsibility or expected file | Change | Governing source | Unaffected boundary to preserve |
|---|---|---|---|
| `studio/src/studio/workbench/WorkbenchNavigator.tsx` | Add the section shell; derive section content; replace the gated device/hardware block with the flat `DEVICE` section (PLC devices plus one `Hardware` row); re-home the device operations into the device row menu and the hardware operations onto the hardware row; drop `viewKind` | AC-001…AC-011 | Tag filter wiring, the native-button row/ARIA pattern, and the existing project/worktree/task row menus |
| `studio/src/studio/MainStudio.tsx` | Stop passing `viewKind`; keep passing the device and hardware callbacks, now used by the `DEVICE` section; pass the selected target into task creation | AC-005, AC-008, AC-009 | Selection state stays owned by MainStudio; landing-page and top-bar selection unchanged |
| `src/Agent/Workbench/EngineeringGraph/*` | Add the nullable `target_kind` column; carry the target through `CreateTask` / `UpdateTask` and the task read path; make the service-level `TASK_DEVICE_REQUIRED` guard at `EngineeringGraphService.cs:40-41` conditional on the target kind, and exclude a hardware task from source staging | AC-010, AC-011 | Every existing null-tolerant same-device rule and the source-stage guard |
| `src/ApiHost/WorkbenchApiModels.cs` | Make `TASK_DEVICE_REQUIRED` conditional on the target kind; expose the target on the task response | AC-010, AC-011 | The rejection of a task with no resolvable target (AC-011) |
| `studio/src/api/client.ts` | Carry the target on `EngineeringTask` and the create request | AC-005, AC-010 | Existing task fields and routes |
| Task create entry (`WorktreeTasksPanel` / its dialog) | Accept a preselected target: a device or the worktree's hardware | AC-005, AC-010 | The rule that a task cannot be created without a resolvable target |
| `docs/ui-spec/task-device-navigation-ui-spec.md` | Revise AC-001, AC-002 and AC-004 and record the reasons | ADR-0006, ADR-0007 | Every other criterion in that spec |
| `docs/user-workflow.md` | Update the worktree/device navigation step and the task-binding step | Repository maintenance rule | Remaining workflow steps |

### Components and Flow

```
MainStudio (owns selection + collections)
   workbenches, tasksByWorktree, devicesByWorktree, selection, activeTaskId
        │
        ▼
WorkbenchNavigator
   fixed header + filter
   scroll region
     ├ NavigatorSection PROJECTS  ← workbenches
     ├ NavigatorSection WORKTREE  ← selected workbench.worktrees
     │    └ unbound group         ← tasksByWorktree[key] with no resolvable target
     ├ NavigatorSection DEVICE    ← devicesByWorktree[key] plus one hardware row
     └ NavigatorSection TASKS     ← tasksByWorktree[key] where target == the selected target
        │
        └ selection callbacks ──► MainStudio (unchanged handlers)
```

### Contracts, State, and Persistence (When Applicable)

| Boundary | Input / exact format | Output / exact format | Error or state behavior | Compatibility |
|---|---|---|---|---|
| `MainStudio → WorkbenchNavigator` task creation | `onAddTask(workbench, worktree, target)` where target is a device id or the hardware target | dialog opens with the target preselected | Creation still fails validation without a resolvable target | Internal prop change only; no external contract |
| Collapse state | component-local keyed set | expanded or collapsed per section | Unknown ids are ignored | Not persisted; no layout file changes |
| Task grouping | `tasksByWorktree[key]` filtered by the task target | per-target list plus one unbound list | A task appears in exactly one group | Client-side only; no route change |
| Task target, API and storage | create request carries the target kind; `tasks.target_kind` is a nullable column whose effective default is `device` | task responses carry the target | A hardware target is accepted with no device; any other targetless task keeps failing with `TASK_DEVICE_REQUIRED` | Additive column; existing rows read as `device`; no row rewrite. The target is immutable after creation, like `device_id`, so it cannot end up disagreeing with the device binding |

### Repository-Owned Migration, Flag, or Deployment Behavior (When Applicable)

One additive migration adds the nullable `target_kind` column to `tasks`; existing rows keep the
effective `device` default and are not rewritten, mirroring how `device_id` itself was added. The
Studio-side changes introduce no persisted shape and no stored layout or flag depends on the
navigator's tree structure.

## Implementation Approach

- Slicing: foundation-first inside the navigator, then a separate slice that replaces the gated
  device/hardware block and re-homes its operations.
- Dependency order: the section shell must exist before content moves into sections; the `onAddTask`
  signature change must land with or before the `TASKS` header action that uses it; the device and
  hardware row menus must be in place before the gated block is removed, so no operation loses its
  home in between.
- First observable checkpoint: `PROJECTS` and `WORKTREE` render as collapsible sections and the app
  still navigates end to end — every later slice is additive from there.
- Rationale: the navigator render is a single function, so splitting by section keeps each slice
  reviewable and keeps the app runnable between slices, while the block replacement stays isolated
  and type-check-provable.

## Verification Strategy

| Claim / AC | Level | Repository command or operation | Observable pass condition |
|---|---|---|---|
| Section visibility, cascade derivation, unbound grouping, filter visibility, collapse independence | L1 | `npm test -- --run` in `studio/`, new cases in `WorkbenchNavigator.test.tsx` | Every AC-001…AC-009 case passes and no existing navigator case regresses |
| Hardware target accepted, targetless task still rejected, existing rows unaffected | L1 | `dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-build -v q` and `dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj --no-build -v q` | A hardware task is created with no device; a targetless worktree task still returns `TASK_DEVICE_REQUIRED`; a pre-existing row reads as `device` (AC-010, AC-011) |
| Re-homed callbacks are actually wired to rendered controls | L1 | `npx tsc -b` in `studio/` | `noUnusedLocals` fails the build if a callback is passed but no rendered control uses it, so the device and hardware operations cannot silently stay orphaned |
| Navigation still works end to end | L3 | `.\launch.ps1`, then drive the app | Workbench → worktree → device → task selection still reaches the same pages; console shows no local-application error |
| Superseded criteria revised | L1 | Re-read the revised spec | AC-002/AC-004 record the superseding decision and its reason |

- Early verification point: the first slice's `WorkbenchNavigator.test.tsx` case for `PROJECTS` plus
  `WORKTREE`, run before the block-replacement slice.
- Environment constraint: `dotnet test` cannot run inside the sandboxed agent session — the test host
  aborts when it cannot open its parent process handle (`Access is denied`), so the two backend lanes
  above must be run from an unsandboxed terminal. `tsc -b` and `vitest` do run in the session, so the
  Studio slices can be verified there without that caveat.
- Data/persistence boundary: none — the change reads existing in-memory collections.
- Existing observable-output comparison: browser evidence before and after for the same workbench,
  worktree, and device, since the navigator's visible structure changes.

## Material Risks

| Risk | Evidence | In-scope response or verification |
|---|---|---|
| Four sections crowd out rows in a fixed-height column | The navigator already truncates task titles at task depth | Bounded scroll region per section with a sticky header; verified in the browser at the default window size |
| Dropping `viewKind` loses the "where am I" cue | `WorkbenchNavigator.tsx:65`, `:358` | The cascade level plus `aria-current` replaces it; verified in the browser and in the L1 cases |
| Device-less tasks silently disappear under the new chain | `GraphTask.DeviceId` is nullable for legacy and project-scoped tasks | AC-003's unbound group, with a test that covers both the present and absent case |
| Restoring the device row menu also restores its heavier operations (rebuild, update knowledge, open in TIA) | Those operations are unreachable today only because of the gate, not because they were removed | AC-008 keeps the existing labels and callbacks unchanged, so the operations behave exactly as they do on the device page; the existing confirmation and operation-status paths still apply |
| Hardware placement on a dedicated row may need revisiting once hardware gains its own views | ADR-0007 fixes one hardware target per worktree | The row reuses the existing `onSelectHardware` callback and the shell's pages, so moving it later touches no contract |
| A hardware task could be mistaken for an unbound or legacy task | Both have a null `deviceId` | AC-003's group excludes hardware targets and a test covers both categories; the target kind, not the null device, decides the group |
| Revising an accepted spec without changing the code it describes | `task-device-navigation-ui-spec.md` AC-002/AC-004 describe today's shipped behaviour | The revision lands in the same change as the navigator restructure, not before it |

## References

- `docs/adr/ADR-0006-studio-navigator-ownership-and-shape.md`
- `docs/ui-spec/studio-information-architecture-ui-spec.md`
- `docs/ui-spec/task-device-navigation-ui-spec.md`
- `studio/src/studio/workbench/WorkbenchNavigator.tsx`
- `studio/src/studio/MainStudio.tsx`

## Update History

| Date | Version | Changes |
|---|---|---|
| 2026-09-30 | 1.0 | Initial design |
