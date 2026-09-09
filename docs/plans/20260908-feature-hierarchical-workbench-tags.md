# Work Plan: Hierarchical Workbench Tags Implementation

Created Date: 2026-09-08
Type: feature
Related Issue/PR: N/A
Review Scope: global tag metadata, Workbench/worktree lifecycle integration, HTTP contracts, Studio tag assignment, and navigator filtering.

## WorkPlan Review

- **Status**: pending

## Governing Documents

- Design Doc: `docs/design/hierarchical-workbench-tags-design.md`
- UI Spec: `docs/ui-spec/hierarchical-workbench-tags-ui-spec.md`
- ADR: `docs/adr/ADR-0002-global-workbench-tag-catalog.md`
- PRD: N/A — user-approved Hierarchical Tag System brief is embedded in the Design Doc requirement boundary.

## Implementation Scope

Deliver a host-owned global hierarchical tag taxonomy, stable Workbench/worktree assignments, calculated inheritance, server-owned descendant/AND search, assignment editors, and a hierarchy-preserving navigator filter without changing engineering Git/TIA metadata behavior.

## Implementation Phases

### Phase 1: Persist and manipulate a global tag taxonomy

#### Tasks

- [ ] **P1-T1: Add independent tag domain and durable JSON store.**
  - **Source**: Design §§ Selected Design, Contracts, AC-001, AC-002; ADR-0002 Decision.
  - **Scope**: Create `src/Agent/Workbench/Tags/WorkbenchTagModels.cs` and `WorkbenchTagStore.cs`; add an application-data path resolver if no existing suitable helper can expose `%LOCALAPPDATA%/AutomationWorkbench/metadata/tags.json` without changing Workbench root behavior.
  - **Depends on**: none.
  - **Verification**: Add `tests/Agent.Tests/WorkbenchTagStoreTests.cs`; run it first red, then `dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-build -v q`.
  - **Primary failure**: A load/save path changes existing `workbench.json` schema behavior or silently resets corrupt data.
  - **Observable check**: Restart the store against the same temporary path and assert node IDs/assignments persist; corrupt and unsupported documents yield a defined error.

- [ ] **P1-T2: Implement normalized path creation, rename, and conservative delete rules.**
  - **Source**: Design §§ Selected Design, AC-001, AC-002; ADR-0002 Implementation Guidance.
  - **Scope**: `WorkbenchTagService.cs` plus focused service tests. Implement idempotent slash-path creation, invariant normalized sibling comparison, rename collision protection, and deletion only for unassigned leaf nodes.
  - **Depends on**: P1-T1.
  - **Verification**: Focused red/green tests for deep path, repeated creation, whitespace/case collision, rename, assigned delete, and parent-with-children delete.
  - **Primary failure**: Full paths become identities or rename invalidates descendants.
  - **Observable check**: Rename an ancestor and assert all node IDs and assignments are unchanged while derived display paths change.

#### Phase Completion

- [ ] Tag database round-trips atomically, supports arbitrary depth, and passes focused domain/store tests.

### Phase 2: Validate assignments, calculate inheritance, and search by tags

#### Tasks

- [ ] **P2-T1: Add catalog-backed direct-assignment and effective-tag operations.**
  - **Source**: Design §§ Selected Design, Contracts, AC-003; ADR-0002 Architecture Impact.
  - **Scope**: Extend `WorkbenchTagService` with Workbench/worktree assign/unassign, membership validation, direct/effective tag projections, and idempotent duplicate operations. Use an injected lookup interface/adaptor rather than making tags own `WorkbenchCatalog` lifecycle.
  - **Depends on**: P1-T2.
  - **Verification**: Add service tests using temporary `WorkbenchCatalog` fixtures; run focused tests, then Agent test project.
  - **Primary failure**: A Worktree from another Workbench receives an assignment or inherited tags are copied into storage.
  - **Observable check**: Assert mismatched pair rejection and that removing a Workbench assignment immediately changes an existing Worktree's effective response with no Worktree write.

- [ ] **P2-T2: Implement descendant expansion and structured AND search.**
  - **Source**: Design §§ Selected Design, AC-004, AC-005.
  - **Scope**: Add in-memory node/children/assignment indexes and result models to `WorkbenchTagService`; direct Workbench matching, effective Worktree matching, and availability determined from catalog registration plus worktree metadata/file existence.
  - **Depends on**: P2-T1.
  - **Verification**: Service tests covering parent-to-descendant matching, exact match, nonmatching sibling, AND across direct/inherited tags, and no-match result.
  - **Primary failure**: A parent Workbench is treated as a direct tag match merely because one child worktree matches all filters.
  - **Observable check**: Assert the Workbench result set uses only direct Workbench tags; matching Worktree results separately identify their parent Workbench.

#### Phase Completion

- [ ] Catalog-valid assignments, calculated inheritance, and descendant/AND semantics pass isolated tests.

### Phase 3: Expose the tag contract through ApiHost

#### Tasks

- [ ] **P3-T1: Register tag dependencies and add taxonomy/assignment/search endpoints.**
  - **Source**: Design §§ Change Surface, Contracts, AC-006–AC-009.
  - **Scope**: Update `src/ApiHost/Program.cs` DI and `src/ApiHost/WorkbenchApiModels.cs`. Add routes for taxonomy CRUD, Workbench tags, Worktree tags, and `POST /api/workbenches/search`; define exact DTOs with `direct`, `inherited`, and `effective` tag arrays.
  - **Depends on**: P2-T2.
  - **Verification**: Extend `tests/ApiHost.Tests/WorkbenchEndpointsTests.cs` or add `WorkbenchTagEndpointsTests.cs`; use real temporary tag storage and catalog fixtures.
  - **Primary failure**: HTTP routes bypass membership validation or duplicate descendant/inheritance calculations in endpoint code.
  - **Observable check**: API assertions cover structured validation errors, persisted before/after assignment state, and search output with expected `available` value.

- [ ] **P3-T2: Integrate assignment cleanup into successful deletion lifecycle.**
  - **Source**: Design §§ Selected Design, AC-010; ADR-0002 Consequences.
  - **Scope**: Update the deletion orchestration that calls `WorkbenchCoordinator.DeleteWorkbenchAsync` and `DeleteWorktreeAsync`; remove assignments only after the coordinator operation succeeds. Do not delete nodes.
  - **Depends on**: P3-T1.
  - **Verification**: API/lifecycle tests exercise successful deletion plus a simulated deletion failure; assert no cleanup occurs on failure and only matching assignments disappear on success.
  - **Primary failure**: Cleanup runs before a failed destructive lifecycle operation and discards recoverable assignments.
  - **Observable check**: Compare tags before failed delete and after successful delete using the public tag API.

#### Phase Completion

- [ ] API provides the complete server-owned tag contract, including lifecycle cleanup, and all tag endpoint tests pass.

### Phase 4: Add frontend contracts and reusable tag editor components

#### Tasks

- [ ] **P4-T1: Add typed client models and API calls.**
  - **Source**: Design Change Surface/Contracts; UI Spec Components and Interactions.
  - **Scope**: Update `studio/src/api/client.ts` with tag, entity-tag, and search result types plus calls for every Phase 3 route. Keep it a transport layer only.
  - **Depends on**: P3-T1.
  - **Verification**: Type-check through `npm run build`; client tests verify route/body serialization where existing `client` tests provide the pattern.
  - **Primary failure**: Frontend derives hierarchy/inheritance locally or serializes displayed paths rather than tag IDs.
  - **Observable check**: Public call inputs accept IDs and returned models expose server-provided direct/inherited/effective collections.

- [ ] **P4-T2: Build accessible picker, tree, and chip components.**
  - **Source**: UI Spec §§ Components, State / Display Detail, Accessibility; Design AC-008.
  - **Scope**: Create `studio/src/studio/workbench/tags/TagChip.tsx`, `TagTree.tsx`, and `TagPicker.tsx` with sibling `.test.tsx` files. Reuse `components/ui/command.tsx`; derive full display paths from API tree data or an explicit API display-path field.
  - **Depends on**: P4-T1.
  - **Verification**: Vitest/happy-dom tests use role/label queries to prove keyboard selection, `aria-expanded`, valid absent-path create action, existing-node selection, and API-error recovery.
  - **Primary failure**: Inherited labels are presented as direct/removable or invalid paths create malformed nodes.
  - **Observable check**: Rendered controls expose correct accessible names and expected direct/inherited state.

#### Phase Completion

- [ ] Typed frontend contract and independently tested, accessible tag-editor primitives are complete.

### Phase 5: Deliver Project and Worktree tag assignment views

#### Tasks

- [ ] **P5-T1: Integrate direct Workbench tags into ProjectLandingPage.**
  - **Source**: UI Spec Project tag editor; Design AC-006.
  - **Scope**: Extend `studio/src/studio/workbench/ProjectLandingPage.tsx` and its test. Load direct tags with existing overview/error conventions; use picker assignment and chip removal, then refresh the tag view without overwriting Workbench purpose/owner state.
  - **Depends on**: P4-T2.
  - **Verification**: `ProjectLandingPage.test.tsx` asserts initial direct chips, assign, remove, loading, and API error behavior; run `npm test -- --run`.
  - **Primary failure**: A tag mutation causes an unrelated overview reload to hide current Project metadata or stale Worktree summary.
  - **Observable check**: Purpose/owner and current Worktree rows remain rendered across a successful tag mutation.

- [ ] **P5-T2: Integrate direct and inherited tags into WorktreeLandingPage.**
  - **Source**: UI Spec Worktree tag editor; Design AC-007.
  - **Scope**: Extend `studio/src/studio/workbench/WorktreeLandingPage.tsx` and its test. Render direct tags with remove controls and inherited tags with a clear label and no remove control.
  - **Depends on**: P5-T1.
  - **Verification**: `WorktreeLandingPage.test.tsx` proves direct/inherited distinction, assignment/removal, and error recovery.
  - **Primary failure**: UI attempts to unassign an inherited tag from a Worktree.
  - **Observable check**: The inherited chip has no remove button and the direct chip calls only the worktree unassign route.

#### Phase Completion

- [ ] Project and Worktree landing pages display and mutate the correct assignment ownership.

### Phase 6: Filter the navigator without flattening Project context

#### Tasks

- [ ] **P6-T1: Add a server-backed tag filter and hierarchy-preserving navigator projection.**
  - **Source**: UI Spec Navigator filtering; Design AC-009, AC-005.
  - **Scope**: Create `TagFilter.tsx`; extend `WorkbenchNavigator.tsx` to accept a filtered projection; update `MainStudio.tsx` to own selected filter IDs, invoke search, clear filters, and preserve existing selection behavior.
  - **Depends on**: P5-T2.
  - **Verification**: New `TagFilter.test.tsx` and `WorkbenchNavigator` tests prove one-tag descendant matching, multi-tag AND response rendering, parent-only context, clear-to-unfiltered behavior, and unavailable worktree rendering.
  - **Primary failure**: Client-side filtering recreates business semantics or hides a matching worktree's Project parent.
  - **Observable check**: Mocked API result with a matching Worktree but nonmatching parent still renders the parent and child; the parent is not labelled a direct match.

- [ ] **P6-T2: Run the narrowest complete user-facing proof.**
  - **Source**: Design Verification Strategy, UI Spec Acceptance Traceability.
  - **Scope**: No product code beyond any correction exposed by the focused checks. Run Studio tests/build and, when local services can start, launch the stack and browser-test tag assignment/filter flow without approving TIA/Git mutation dialogs.
  - **Depends on**: P6-T1.
  - **Verification**: `Push-Location studio; npm test -- --run; Pop-Location`; `npm run build`; focused .NET tests; then `./launch.ps1` health checks and browser observations.
  - **Primary failure**: Component tests pass while real API selection/reload state breaks the explorer.
  - **Observable check**: A visible deep path assignment affects effective Worktree tags and the navigator filter retains the Workbench parent.

#### Phase Completion

- [ ] The end-to-end tag workflow is proven through focused backend/frontend checks and the available local browser boundary.

## Completion Criteria

- [ ] Every Design Doc obligation is covered by a task and applicable acceptance criteria.
- [ ] Tags are host-owned JSON metadata, with an independent `1.0` schema and serialized mutations.
- [ ] No full path, Workbench filesystem path, or Git branch is used as a tag/entity identity.
- [ ] Worktree inheritance is calculated, not stored as copied assignments.
- [ ] Descendant and AND semantics are tested through the API/service boundary.
- [ ] Successful deletion cleans assignments while failed deletion preserves them and both retain taxonomy nodes.
- [ ] UI tests prove accessible picker/editor/filter behavior and navigator hierarchy preservation.
- [ ] Required focused tests and builds pass; any unavailable runtime proof is reported with its remaining risk.
