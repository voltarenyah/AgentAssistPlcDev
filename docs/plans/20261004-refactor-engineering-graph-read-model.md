# Work Plan: Engineering facts served from the engineering graph

Created Date: 2026-10-04
Type: refactor
Related Issue/PR: none — direct user request (no GitHub issue)
Review Scope: `src/Agent/Workbench/EngineeringGraph/*`, `src/Agent/Workbench/DeviceSnapshot.cs`, `src/Agent/Workbench/WorkbenchCoordinator.cs`, `src/ApiHost/WorkbenchApiModels.cs`, `studio/src/studio/PlcSourcePanel.tsx`, and their colocated tests

## WorkPlan Review

Plan creation and material updates set this to `pending`. Record `approved` after the user approves the reviewed implementation scope.

- **Status**: pending

## Governing Documents

- Design Doc: `docs/design/engineering-graph-read-model-design.md`
- UI Spec: not applicable (backend change; routes and response shapes preserved)
- ADR: `docs/adr/ADR-0011-engineering-graph-as-the-read-model.md`, `docs/adr/ADR-0012-graph-freshness-incremental-projection.md`
- PRD: not applicable (no `docs/prd/`; Structural Scale Medium, convergence record embedded in the Design Doc)
- Test skeletons: none generated (existing lanes: `dotnet test tests/Agent.Tests`, `tests/ApiHost.Tests`, `cd studio && npm test`)

## Implementation Scope

The Studio front end reads every engineering fact from the engineering graph: the projection holds the
device, source-object and block facts that the read paths serve today by walking the exported source
tree and parsing `metadata.json`, the graph is kept current by hash-incremental projection plus a
validity stamp checked at selection boundaries, and no HTTP route or response shape changes.

## Implementation Phases

### Phase 1: Property storage and the projection (foundation)

#### Tasks

- [ ] **P1-T1: Add schema v6 — node-property table, `device`/`worktree` node kinds, stamp properties**
  - **Source**: Design Doc `Selected Design` items 1-2 and 9, `Repository-Owned Migration…`; ADR-0011 `Decision`; AC-009
  - **Scope**: `src/Agent/Workbench/EngineeringGraph/EngineeringGraphSchema.cs`, `EngineeringGraphModels.cs`; the property table carries a covering index on `(entity_kind, entity_id)` for the single-statement device read
  - **Depends on**: none
  - **Verification**: `dotnet test tests/Agent.Tests --filter FullyQualifiedName~EngineeringGraphStore` — extend the existing failure-injection and backup tests to v6; a v5 database migrates with all rows intact and a `.bak` present; a read scope on a current schema still takes no write lock; and one device request opens exactly one graph scope, including when several device selections run in parallel
  - **Primary failure**: migration succeeds but drops or rewrites existing entities/edges/tasks/stages
  - **Observable check**: row counts per table before/after migration on a copy of a real `engineering.db`

- [ ] **P1-T2: Property read/write API on the graph service (replace-as-a-set, batched per device)**
  - **Source**: Design Doc `Selected Design` items 2 and 9, `Contracts, State, and Persistence`; ADR-0011 `Decision Details`
  - **Scope**: `src/Agent/Workbench/EngineeringGraph/EngineeringGraphService.cs` (+ store helpers), new colocated tests; a device's property writes happen in one transaction with no file I/O inside it
  - **Depends on**: P1-T1
  - **Verification**: `dotnet test tests/Agent.Tests` — round-trip a node's property set, replace it with a smaller set, and assert the removed property is gone; assert a device's whole property set is written in one transaction
  - **Primary failure**: a property write merges instead of replacing, leaving facts for objects that no longer exist
  - **Observable check**: a second write with one property omitted leaves the node with the new set only

- [ ] **P1-T3: Projection service — manifest ingest with hash-diff, removals and diagnostics**
  - **Source**: Design Doc `Selected Design` items 3-4, `Change Surface` (DeviceSnapshot row); ADR-0012 `Decision` items 1-2; AC-002, AC-003
  - **Scope**: new `src/Agent/Workbench/EngineeringGraph/` projection service; `src/Agent/Workbench/DeviceSnapshot.cs` readers reused as the ingest implementation
  - **Depends on**: P1-T2
  - **Verification**: `dotnet test tests/Agent.Tests --filter FullyQualifiedName~Projection` (new) — ingest a manifest fixture twice (second run writes nothing), change one component's `contentHash` (only that node's properties change), remove one component (its node's properties are deleted)
  - **Primary failure**: the second ingest rewrites every row (no hash-diff), or a removed component keeps its properties
  - **Observable check**: written-row count on the second run is zero; the removed path has no property rows

#### Phase Completion

- [ ] Phase tasks are complete and their verification passes

### Phase 2: The device page reads from the graph (measured outcome)

#### Tasks

- [ ] **P2-T1: Serve the device routes from the graph; the crawl becomes ingest-only**
  - **Source**: Design Doc `Selected Design` items 3-6, `Change Surface` (ApiHost device routes and compat routes rows); ADR-0011 `Decision`; AC-001, AC-002, AC-008
  - **Scope**: `src/ApiHost/WorkbenchApiModels.cs` device routes (`:1739-1760`), `src/ApiHost/CompatibilityEndpoints.cs:549-553` and `:579-583`, `DeviceSnapshotReader.Read`/`ReadSourceObjects` call sites, and `tests/Agent.Tests/DeviceSnapshotReaderTests.cs` (replace the "600 objects under 2 s" crawl budget with the projection-read budget and a no-XML-read assertion)
  - **Depends on**: P1-T3
  - **Verification**: automated — extend the existing manifest fixture with a manifest `device` section and a knowledge-database-present case, then assert every AC-002 field (including `isKnowHowProtected` and `modifiedDate`) and the block-category count, and assert the picker's instance-DB exclusion (`DeviceSnapshotReaderTests.cs:474`); `dotnet test tests/ApiHost.Tests`; `cd studio && npm test`
  - **Operator-run acceptance check** (not a repository artifact): field-for-field comparison of `GET …/devices/{dev}` JSON before/after on the live 1097-object device, plus `Invoke-WebRequest` timing (target ≤ 100 ms warm, no cold-crawl outlier)
  - **Primary failure**: the response shape drifts (a field missing or renamed) or a value differs (count, block subset, knowledge state, a diagnostic)
  - **Observable check**: the fixture test fails if a projected field is missing, and the operator check's before/after JSON diff is empty apart from ordering

- [ ] **P2-T2: The worktree overview's per-device fan-out costs graph reads, not crawls**
  - **Source**: Design Doc `Existing Evidence` (fan-out row), `Verification Strategy`; AC-001
  - **Scope**: measurement only. The two fan-out call sites (`WorktreeLandingPage.tsx:151-178`, `TaskCommitsSection.tsx:78-88`) keep their current behaviour because the client is not changed in this pass; if the measured cost is unacceptable, record it as a follow-up rather than changing the client
  - **Depends on**: P2-T1
  - **Verification**: automated — the no-XML-read fixture test from P2-T1 covers the route each call uses; operator-run — measure the worktree overview tab with N devices before/after
  - **Primary failure**: the page still issues N crawls because a second code path reads the file
  - **Observable check**: the recorded measurement shows the tab's cost scaling with graph-read time, not crawl time

#### Phase Completion

- [ ] Phase tasks are complete and their verification passes

### Phase 3: Identity and population defects (AC-005, AC-006)

#### Tasks

- [ ] **P3-T1: One source-object identity at the API boundary**
  - **Source**: Design Doc `Selected Design` item 7, `Change Surface` (graph-entity route row); AC-005
  - **Scope**: `src/ApiHost/WorkbenchApiModels.cs` (`:738-776`, `:2179-2212`, `:2214-2221`), `tests/ApiHost.Tests`. **No client change in this pass** (user-decided): the server resolves the bare manifest id, so `PlcSourcePanel.tsx:115` keeps sending what it sends today
  - **Depends on**: P2-T1
  - **Verification**: automated — an ApiHost test asserting 200 for an existing object of **each** id form (the exact `{deviceId}:{manifestId}` id, the bare manifest id, and the `source:{relativePath}` form) and a named 4xx for an unresolvable one; operator-run — the same request against the running ApiHost
  - **Primary failure**: the bare id still resolves to a silent 404 that the client renders as "no links"
  - **Observable check**: the panel shows the object's links where the graph has them

- [ ] **P3-T2: Every commit path produces a `git_commit` node**
  - **Source**: Design Doc `Change Surface` (commit evidence row); ADR-0011 `Context` (the workbench with no commit nodes); AC-006
  - **Scope**: `src/Agent/Workbench/WorkbenchCoordinator.cs` (`MergeWorktreeAsync:2842-2877` and any other raw git path), the raw gateway commit and checkout routes (`WorkbenchApiModels.cs:1972-1976`, `CompatibilityEndpoints.cs:642-643`), commit attribution/indexer call sites
  - **Depends on**: P1-T3
  - **Verification**: automated — `dotnet test tests/Agent.Tests` + `tests/ApiHost.Tests` covering each commit path and the merge; the E2E harness (`tests/E2E.Tests/WorkbenchLifecycleTests.cs`) is the closest existing fixture for a real merge. Operator-run — after a merge, `GET …/engineering-graph/git_commit/{sha}` resolves and lists its source objects
  - **Primary failure**: one of the commit paths still writes no commit node, so the timeline's commit evidence stays empty for it
  - **Observable check**: the graph holds a `git_commit` node for a commit made through each path

#### Phase Completion

- [ ] Phase tasks are complete and their verification passes

### Phase 4: Freshness guarantees (AC-004, AC-007)

#### Tasks

- [ ] **P4-T1: Validity stamp and the selection-boundary check**
  - **Source**: ADR-0012 `Decision` items 2-3; Design Doc `Selected Design` item 5; AC-004
  - **Scope**: projection invalidation flag and digest properties, selection/refresh boundaries in `src/ApiHost/WorkbenchApiModels.cs` (device/worktree selection, refresh routes)
  - **Depends on**: P2-T1
  - **Verification**: automated — an integration test that invalidates a projection (changed manifest fixture), then asserts the boundary re-projects before serving and the read reflects the new content; operator-run — a real branch switch followed by a device selection. Budget: the boundary stays inside AC-001's ≤100 ms on the 1097-object device
  - **Primary failure**: a branch switch or an out-of-app edit is served from stale facts
  - **Observable check**: the boundary records which input moved (the invalidation flag or the digest) and the following read reflects the new content

- [ ] **P4-T2: Reconciliation repair and worktree-deletion cleanup**
  - **Source**: ADR-0012 `Decision` item 4; Design Doc `Selected Design` item 8; AC-007
  - **Scope**: repair entry point, `src/Agent/Workbench/WorkbenchCoordinator.cs:1916-1970` (worktree delete)
  - **Depends on**: P4-T1
  - **Verification**: agent test — delete a worktree, then assert no nodes, properties or edges remain for it; a stamp mismatch re-projects only that device
  - **Primary failure**: orphan rows remain and a later stage/unique-index operation fails on them
  - **Observable check**: row counts for the deleted worktree are zero

#### Phase Completion

- [ ] Phase tasks are complete and their verification passes

### Phase 5: The remaining file-reading paths (user's principle)

#### Tasks

- [ ] **P5-T1: Source inspector content facts**
  - **Source**: Design Doc `Requirement Boundary` requirement 2, `Selected Design` (fact model), ADR-0011 `Decision`; AC-001
  - **Scope**: `src/Agent/Workbench/SourceObjectInspector.cs` (ingest), the inspect route `:1842-1851`, projection of per-object parsed content. No client change in this pass (user-decided)
  - **Depends on**: P2-T1
  - **Verification**: automated — a fixture test asserting the inspect route serves the same payload from the graph with the XML files removed after ingest; operator-run — `Invoke-WebRequest` before/after for a sampled object
  - **Primary failure**: the inspected payload is projected partially, so the panel shows less than today
  - **Observable check**: the same object's inspection JSON matches before/after

- [ ] **P5-T2: Hardware/AML subtree facts**
  - **Source**: Design Doc `Requirement Boundary` (hardware in scope, sequenced later); AC-002
  - **Scope**: `HardwareConfigurationReader.cs`, `HardwareListReader.cs`, the hardware routes `:1136-1150`
  - **Depends on**: P2-T1
  - **Verification**: the hardware pages render identical values from the graph; measured cost stays at or below today's 11-63 ms
  - **Primary failure**: AML facts are projected with a different meaning than the readers derive
  - **Observable check**: before/after JSON comparison for configuration, BOM and network

#### Phase Completion

- [ ] Phase tasks are complete and their verification passes

## Completion Criteria

- [ ] Every Design Doc obligation needed for implementation is covered by at least one task
- [ ] Every task cites each directly constraining governing section and its applicable ACs
- [ ] Every task produces a repository implementation outcome required by the Design Doc
- [ ] Dependencies permit execution in the listed order
- [ ] Every task names at least one automated check runnable from repository artifacts, and marks any live-device, browser, staged-export or mutating step explicitly as an operator-run acceptance check
- [ ] Task verification passes and the cited acceptance criteria are satisfied
