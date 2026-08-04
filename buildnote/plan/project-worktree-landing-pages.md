# Project & Worktree Landing Pages — Design and Implementation Plan

## Status

Implemented (Phases 1-2 + modified-blocks section from Phase 3). Verified: Agent.Tests 239/239, ApiHost.Tests 110/110, studio vitest 148/148. Navigator status dots deferred.

Review decisions incorporated:

- Task artifacts stay repo-local as JSON (no external task-management package, no database).
- No version-control work in this feature. No commit history, no git-based change lists — the VC area is under separate heavy modification and must not be touched here.
- Owner is plain text. Status is shown as a badge with an inline dropdown to change it.

## Goal

The project tree (left dock) has three levels — project (workbench) → worktree → device — but selecting a project or worktree node today shows either a generic placeholder or the hardware page. There is no surface for project-level and worktree-level information.

Add two landing pages:

- **Project landing page**: overview of all worktrees in the project — title, branch, purpose, status (ongoing/finished), responsible person — so a user can see what is happening over the project lifecycle.
- **Worktree landing page**: worktree title, branch, purpose, status, responsible person, and a task list — which PLC elements need modification, each task's status, and the modification plan. One page that answers "what is this worktree for and where does it stand".

## Current state (verified)

### Frontend (`studio/`)

- Tree: `studio/src/studio/workbench/WorkbenchNavigator.tsx`. Three hardcoded levels, no generic node model. Selection is one flat record `WorkbenchSelection { workbenchId, worktreeId, deviceId }` (WorkbenchNavigator.tsx:29).
- Panel switching is pure conditional rendering in `studio/src/studio/MainStudio.tsx` `<main>` (~1666-2055) on `(selection, hardwarePage, activeTab)`. No router, no state library.
- Selecting a worktree currently *forces* the hardware tree page (`selectWorktree`, MainStudio.tsx:815 sets `hardwarePage('tree')` and auto-selects a single device). Selecting a workbench shows a "Select a device context" placeholder (1695-1715). These are the two insertion points.
- All data via plain REST from `studio/src/api/client.ts` (base `/api`). The `Workbench` payload already embeds `worktrees: { worktreeId, name, branch, relativePath }[]`.
- No task/todo UI anywhere.
- Page pattern to follow: `HardwareBomView.tsx` / `HardwareNetworkView.tsx` (header card + filter + grouped rows, `state: 'available' | 'missing' | 'invalid'`, vitest tests alongside). UI kit: shadcn-style components on radix-ui, Tailwind v4, lucide icons, `react-markdown` already available.

### Backend (`src/`)

- Metadata is plain JSON via `src/Agent/Workbench/AtomicJsonStore.cs` — no database:
  - `workbench.json` → `WorkbenchMetadata { WorkbenchId, Name, CreatedAt, RootPath, RepositoryPath, EngineeringProjectId?, SourceProjectPath?, Worktrees[] }` (`src/Agent/Workbench/WorkbenchModels.cs:8`)
  - `worktrees/<name>/worktree.json` → `WorktreeMetadata { WorktreeId, WorkbenchId, Name, Branch, CreatedAt, BaseCommit?, ..., DeviceIds[], LastReconciliationCommit? }` (:25)
- Schema version `WorkbenchSchema.CurrentVersion = "1.0"`. **No purpose/status/owner/task fields exist anywhere.**
- No `GET` endpoint exposes the full `worktree.json`; the worktree list endpoint returns only id/name/branch/relativePath (`src/ApiHost/WorkbenchApiModels.cs`). `WorkbenchApiState.Worktree(id, wt)` already reads it server-side (:104).
- Modified-block information without git: `DeviceSnapshotReader` (`src/Agent/Workbench/DeviceSnapshot.cs:239-242`) flags blocks that have a file in the device's `modified-source/` overlay (`modified: true`, plus `OverlayCount`). Available at `GET .../devices/{device}`.

## Key design decisions

### D1. Task storage: repo-local JSON, no external package

Tasks are worktree-scoped artifacts and follow the system's existing metadata convention:

- Persisted as `worktrees/<name>/tasks.json` inside the worktree directory, written atomically via `AtomicJsonStore`.
- Because it lives inside the worktree, the task list is tracked by the same repository as the work itself: it travels with the branch, merges with it, and is deleted with the worktree. No separate lifecycle management.
- Consistent with every other metadata record in the system (`workbench.json`, `worktree.json`, `device.json`, chat sessions). Single-user desktop app, tens of tasks per worktree — a database buys nothing here.

### D2. Data model additions

Extend the existing records (all new fields optional → backward compatible; bump `WorkbenchSchema.CurrentVersion` to `"1.1"` and treat missing fields as defaults on read):

```csharp
// WorktreeMetadata (worktree.json) — new fields
string? Purpose              // free text, what this worktree is for
string? Owner                // responsible person, plain text (no identity system exists)
WorktreeStatus Status        // enum: Ongoing | Finished (default Ongoing)
DateTimeOffset? FinishedUtc  // set when status transitions to Finished

// New file worktrees/<name>/tasks.json (separate from worktree.json:
// tasks churn on every edit; keep the metadata file small and stable)
WorktreeTaskList { int Version; List<WorktreeTask> Tasks }
WorktreeTask {
  string TaskId              // Guid
  string Title
  string? Details            // what to modify / modification plan (markdown)
  WorktreeTaskStatus Status  // Todo | InProgress | Done
  string[] ElementRefs       // optional PLC element links, e.g. "Device01/FB_Motor_Control"
  DateTimeOffset CreatedUtc
  DateTimeOffset? DoneUtc
}

// WorkbenchMetadata (workbench.json) — new fields
string? Purpose
string? Owner
```

`ElementRefs` is a plain string list for display and jump-to-device, not a foreign key — elements get renamed and deleted; referential integrity is not enforceable and not needed.

The project page needs no status of its own in v1: project lifecycle is the union of its worktrees' statuses, shown in the worktree table.

### D3. Navigation model

`WorkbenchSelection` already distinguishes the levels; only what `<main>` renders changes. Replace the `hardwarePage: 'tree' | 'bom' | 'network'` ternary with one discriminated union in `MainStudio`:

```ts
type MainView =
  | { kind: 'project' }                                  // workbench selected
  | { kind: 'worktree'; tab: 'overview' | 'tasks' }      // worktree selected
  | { kind: 'hardware'; page: 'tree' | 'bom' | 'network' } // existing hardware pages
  | { kind: 'device' }                                   // existing device page
```

Behavior changes:

- `selectWorkbench` → `MainView { kind: 'project' }` (was: placeholder splash).
- `selectWorktree` → `MainView { kind: 'worktree', tab: 'overview' }` and **stop** forcing the hardware page and stop auto-selecting a single device (MainStudio.tsx:826-827).
- Hardware pseudo-nodes in the navigator ("Hardware configuration" / "BOM list" / "Network list") keep working by mapping to `kind: 'hardware'` — the worktree page does not swallow them.
- Selecting a device is unchanged (`kind: 'device'`, existing tab bar).

This keeps the no-router architecture; the union replaces the existing `hardwarePage` state.

### D4. Scope boundary with version control

This feature does **not** touch anything git-related: no commit history tab, no worktree-scoped VC endpoints, no range diffs, no changes to `GitPanel`, Mcp.VersionControl, or the device-scoped VC proxies. The VC area is under separate modification.

The worktree page may still show **modified blocks per device** (D5), which comes from the `modified-source/` overlay in the device snapshot — a device-level concept with no git dependency.

### D5. Modified elements without version control (optional section)

The worktree page can include a compact "Modified blocks" section built from data that already exists: for each device in `WorktreeMetadata.DeviceIds`, the device snapshot exposes `OverlayCount` and per-block `modified` flags (`GET .../devices/{device}`). Rendered as "device — N modified blocks" rows with the block names, plus a jump-to-device action. No new backend work beyond what the device endpoint already returns; the page fetches one snapshot per device in the worktree.

If this proves too noisy in practice it can be dropped without affecting anything else — it is a pure presentation concern over an existing endpoint.

## Page designs

### Project landing page

- Header card: project name, created date, root path, source TIA project path; inline-editable Purpose and Owner (plain text inputs).
- Worktree table (hand-rolled, following the `HardwareBomView` pattern): columns **Title | Branch | Status | Owner | Purpose | Tasks**.
  - Status column: badge (`Ongoing` / `Finished`), with a dropdown on the badge to change it directly from the table.
  - Tasks column: `open / total` count from the server-side aggregate.
  - Row click selects that worktree (navigates to its landing page).
- Ordering: ongoing worktrees first, then finished (by `FinishedUtc` desc). This is the project lifecycle view.

### Worktree landing page

- Header card: worktree title, branch badge, created date; inline-editable Purpose (multiline) and Owner (plain text); Status shown as a badge with dropdown selection (Ongoing ↔ Finished; switching to Finished sets `FinishedUtc`, switching back clears it).
- **Overview tab**: the header card fields plus the optional "Modified blocks" section (D5) and a task summary strip (counts per status, click jumps to Tasks tab).
- **Tasks tab**: grouped Todo / In Progress / Done; inline add-task input; per-task edit dialog (title, details/plan in markdown rendered with the existing `react-markdown`, element refs as removable chips); checkbox or status dropdown per task; delete with confirm. This is where "which PLC element needs modification + status + modification plan" lives.

Right dock: no change (existing docks are device/hardware-scoped).

## Backend work items

1. `src/Agent/Workbench/WorkbenchModels.cs` — add the D2 fields to `WorkbenchMetadata` and `WorktreeMetadata`; add `WorktreeTask`, `WorktreeTaskList`, `WorktreeStatus`, `WorktreeTaskStatus`; bump `WorkbenchSchema.CurrentVersion` to `"1.1"` (missing fields deserialize as defaults, old files keep working).
2. New small store, e.g. `src/Agent/Workbench/WorktreeTaskStore.cs` — load/save `tasks.json` via `AtomicJsonStore`; CRUD helpers. Metadata field updates go through the existing catalog write paths (`WorkbenchCatalog.cs`).
3. `src/ApiHost/WorkbenchApiModels.cs` — new routes:
   - `GET /api/workbenches/{wb}/overview` → project page payload: workbench metadata (incl. purpose/owner) + per-worktree `{ worktreeId, name, branch, purpose, status, owner, finishedUtc, openTasks, totalTasks }` aggregated server-side (one call for the whole project page, no N+1).
   - `GET /api/workbenches/{wb}/worktrees/{wt}` → full `WorktreeMetadata` (reader already exists at `WorkbenchApiState.Worktree`, :104).
   - `PATCH /api/workbenches/{wb}` → `{ purpose?, owner? }`.
   - `PATCH /api/workbenches/{wb}/worktrees/{wt}` → `{ purpose?, owner?, status? }` (server manages `FinishedUtc`).
   - `GET/POST /api/workbenches/{wb}/worktrees/{wt}/tasks` and `PATCH/DELETE .../tasks/{taskId}`.

Explicitly **not** in scope: any `vc/*` endpoint, any change to Mcp.VersionControl, `GitPanel`, or the device-scoped VC proxies.

## Frontend work items

1. `studio/src/api/client.ts` — types (`WorktreeOverview`, `WorkbenchOverview`, `WorktreeTask`, status enums) and functions for every endpoint above.
2. New components under `studio/src/studio/workbench/`:
   - `ProjectLandingPage.tsx` — header card + worktree table.
   - `WorktreeLandingPage.tsx` — header card + Overview/Tasks tab bar.
   - `WorktreeTasksPanel.tsx` — grouped task list, inline add, edit dialog.
   - `StatusBadge.tsx` — badge with dropdown selection, shared by both pages (radix dropdown + badge variants already in the UI kit).
3. `studio/src/studio/MainStudio.tsx` — introduce the `MainView` union (D3), rewire `selectWorkbench`/`selectWorktree`, map hardware pseudo-node callbacks to `kind: 'hardware'`, render the two new pages in `<main>`.
4. `studio/src/studio/workbench/WorkbenchNavigator.tsx` — no structural change required; optional small addition: a finished/ongoing status dot on worktree rows using the data already loaded for the project page.

Editing pattern: inline edits save on blur/Enter via PATCH; failures surface through the existing `sonner` toasts; concurrent-edit conflicts are out of scope (single-user app, last write wins).

## Testing

- Backend:
  - `tests/Agent.Tests` — metadata round-trip with new fields; a schema-1.0 file (without the new fields) reads with defaults; `WorktreeTaskStore` CRUD and atomic-write behavior.
  - `tests/ApiHost.Tests` — overview aggregate shape, PATCH semantics (including `FinishedUtc` set/cleared on status transitions), task CRUD routes, 404s for unknown worktree/task ids.
- Frontend: vitest + happy-dom tests beside the new components, following `HardwareBomView.test.tsx` — render states, status badge dropdown, task add/toggle/edit/delete, empty states.
- Regression: `dotnet test` at solution level and `npm test` in `studio/` must stay green; existing navigator/device/hardware flows unchanged.

## Phasing

- **Phase 1 — metadata + project page**: D2 model, workbench/worktree PATCH endpoints, overview aggregate endpoint, `ProjectLandingPage`, `StatusBadge`, `MainView` union with `kind: 'project'`. Worktree selection behavior unchanged in this phase if a smaller first step is preferred.
- **Phase 2 — worktree page + tasks**: task store + CRUD endpoints, `WorktreeLandingPage` (Overview + Tasks), `selectWorktree` rewiring (stop forcing hardware page), hardware pseudo-node mapping.
- **Phase 3 (optional)**: modified-blocks section (D5), navigator status dots, task → device jump via `ElementRefs`.

Each phase is independently shippable; later phases only add to the page shells built earlier.

## Resolved decisions

- Task storage: repo-local JSON (`tasks.json`), no external package, no database.
- Version control: entirely out of scope for this feature.
- Owner: plain text field.
- Status: badge with inline dropdown selection on both pages; `FinishedUtc` managed server-side.
- Task details: markdown, rendered with the existing `react-markdown` dependency.
- "Finished" behavior: badge/ordering only; no read-only enforcement in v1.
