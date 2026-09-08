# Design Document: Hierarchical Workbench Tags

## Overview

- Outcome: A global tag taxonomy supports arbitrary-depth paths, stable identities, direct Workbench/worktree assignments, calculated inheritance, descendant matching, and AND-filtered Workbench/worktree results.
- Scope: Agent Workbench metadata services, ApiHost routes/contracts, Studio tag editors, and navigator filter.
- UI Spec: `docs/ui-spec/hierarchical-workbench-tags-ui-spec.md`
- Governing ADRs: `docs/adr/ADR-0002-global-workbench-tag-catalog.md`

## Requirement Boundary

- PRD or convergence carrier: Embedded user-approved Hierarchical Tag System brief.
- Current requirements: global taxonomy; arbitrary depth; any node assignable; stable tag IDs; sibling normalized-name uniqueness; direct tags; calculated Worktree inheritance; descendant matching; AND query; Project/worktree editing, autocomplete, hierarchical browser, and navigator filter.
- Non-goals: subtree move/merge, tag merge, delete subtree/reassign, saved filters, OR/NOT, permissions, and Git/TIA behavior changes.
- Open requirement fields: none.

## Acceptance Criteria

- **AC-001** — **When** a valid path such as `machine/press/hydraulic/500t` is created twice, **then** each segment has a stable node ID and the same final ID is returned. Source: user brief.
- **AC-002** — **When** two sibling names differ only by case or surrounding whitespace, **then** creation/rename rejects the collision while preserving the original display name for valid nodes. Source: user brief.
- **AC-003** — **When** a Workbench tag changes, **then** its worktrees' effective tags reflect the change without stored assignment copies. Source: user brief.
- **AC-004** — **When** a selected tag is a parent, **then** direct assignments to it or any descendant match; multiple selected tags require every selected descendant set to intersect the entity tags. Source: user brief.
- **AC-005** — **When** a registered worktree directory is missing, **then** an otherwise matching assignment remains searchable and is marked unavailable. Source: current overview behavior and user brief.
- **AC-006** — **When** a user edits Project tags, **then** the Project page shows and mutates direct Workbench tags. Source: user brief.
- **AC-007** — **When** a user views Worktree tags, **then** direct and inherited tags are distinguished and only direct tags are removable. Source: user brief.
- **AC-008** — **When** a user searches or enters a valid absent path in the picker, **then** the UI selects an existing node or creates and assigns the requested path. Source: user brief.
- **AC-009** — **When** navigator filters are selected, **then** matching Workbenches and the parents of matching worktrees remain in the Project → worktree hierarchy. Source: user brief.
- **AC-010** — **When** a Workbench or worktree is successfully deleted, **then** only its assignments are removed and taxonomy nodes remain. Source: user brief and ADR-0002.

## Existing Evidence

| Evidence | Location | Design effect |
|---|---|---|
| Stable Workbench/worktree IDs and relationship fields | `src/Agent/Workbench/WorkbenchModels.cs` | Assignments use `WorkbenchId`/`WorktreeId`, never paths or names. |
| Atomic JSON plus model-specific metadata schema validation | `src/Agent/Workbench/AtomicJsonStore.cs` | Reuse atomic replacement; give tags an independent schema reader/writer. |
| Catalog creation, registration, update, and removal | `src/Agent/Workbench/WorkbenchCatalog.cs` | Keep entity authority here; keep tags in a sibling service. |
| Missing worktree fallback in overview | `src/ApiHost/WorkbenchApiModels.cs` | Search reads registrations, not `worktree.json`, to preserve unavailable matches. |
| Host-owned atomic JSON registry | `src/Contracts/Sandbox/TrustedWorkbenchRootRegistry.cs` | Use the same `%LOCALAPPDATA%/AutomationWorkbench` ownership and mutex pattern. |
| Worktree metadata ignored by Git | `src/Mcp.VersionControl/Git/RepositoryService.cs` | Do not store tags in Git worktrees. |
| Existing frontend test stack | `studio/package.json`, `tests/ApiHost.Tests/*.csproj` | Use Vitest/happy-dom and xUnit API integration tests. |

## Design

### Selected Design

Create `src/Agent/Workbench/Tags/` with an application-global `WorkbenchTagStore` and `WorkbenchTagService`. The store persists `%LOCALAPPDATA%/AutomationWorkbench/metadata/tags.json` as a single independently versioned `1.0` document. The service is the only mutation surface and receives an entity lookup abstraction backed by the existing catalog/API state.

```text
Studio → ApiHost endpoints → WorkbenchTagService → WorkbenchTagStore → tags.json
                              ↘ catalog membership validation
```

`TagNode` stores `{ tagId, parentTagId?, name, normalizedName }`. `TagAssignment` stores `{ tagId, entityType, entityId, workbenchId? }`; `workbenchId` is mandatory for Worktree assignments and null for Workbench assignments. The service rejects IDs not present in the catalog, rejects mismatched Workbench/worktree pairs, and makes duplicate assign/unassign operations idempotent.

Paths are split on `/`, trim empty boundary whitespace, reject empty interior segments, and compare normalized siblings with invariant case-insensitive semantics. Full paths are derived by parent links. V1 delete succeeds only for unassigned leaves. Rename changes only node text/normalization after same-parent collision validation.

For each read, construct `nodesById`, `childrenByParentId`, and assignment indexes. A Worktree's effective tags are the union of direct Workbench and direct Worktree tags. A selected tag expands via DFS/BFS to a set containing that node and descendants. Every selected set must intersect the candidate's direct tags (Workbench) or effective tags (Worktree).

### Change Surface

| Responsibility or expected file | Change | Governing source | Unaffected boundary to preserve |
|---|---|---|---|
| `src/Agent/Workbench/Tags/WorkbenchTagModels.cs` | New tag database, node, assignment, query/result, and API-facing model types. | AC-001–AC-005 | Existing Workbench metadata records and schemas. |
| `src/Agent/Workbench/Tags/WorkbenchTagStore.cs` | Mutex-protected load/mutate/atomic-save of independent tag schema. | AC-001, AC-002, AC-010 | `AtomicJsonStore` behavior and existing files. |
| `src/Agent/Workbench/Tags/WorkbenchTagService.cs` | Path/node rules, validation, inheritance, descendant AND search, cleanup. | AC-001–AC-005, AC-010 | `WorkbenchCatalog` remains entity/lifecycle authority. |
| `src/ApiHost/Program.cs` | Register tag store/service and application-data path. | ADR-0002 | Existing DI registrations and test overrides. |
| `src/ApiHost/WorkbenchApiModels.cs` | Tag request/response contracts and routes; deletion cleanup only after successful lifecycle operation. | AC-006–AC-010 | Existing Workbench endpoints and status handling. |
| `studio/src/api/client.ts` | Tag types and API client calls. | AC-006–AC-009 | Existing Workbench API calls. |
| `studio/src/studio/workbench/tags/*` | New picker/tree/chip/filter components. | UI Spec, AC-006–AC-009 | Shared command primitive and current styles. |
| `ProjectLandingPage.tsx`, `WorktreeLandingPage.tsx`, `WorkbenchNavigator.tsx`, `MainStudio.tsx` | Integrate assignment displays/mutations and server-backed filtering. | UI Spec, AC-006–AC-009 | Current selection and unfiltered navigator behavior. |

### Contracts, State, and Persistence

| Boundary | Input / exact format | Output / exact format | Error or state behavior | Compatibility |
|---|---|---|---|---|
| `tags.json` | `{ schemaVersion: "1.0", nodes: [], assignments: [] }` | Same document written atomically | Corrupt/unsupported document fails tag requests clearly; no silent reset | New independent store; no legacy migration |
| Taxonomy API | `POST /api/tags/path { path }`, `PATCH /api/tags/{id} { name }`, delete by ID | Tree/node response | Invalid path, unknown ID, sibling conflict, assigned/non-leaf delete return structured client errors | New routes |
| Entity tags API | Workbench/worktree ID routes with tag ID | `{ direct, inherited, effective }` | Unknown/mismatched entity IDs are rejected before mutation | Existing IDs preserved |
| Search API | `POST /api/workbenches/search { tagIds: string[] }` | `{ workbenches, worktrees }` with tags and `available` | Unknown tag IDs are rejected; no filters returns the current unfiltered navigation model client-side | New structured route |

### Security Boundary

Tag paths and names are untrusted API input. The service validates segment form, uses no paths supplied by a caller to construct filesystem destinations, and returns plain display strings through React rendering. Entity mutations validate Workbench/worktree membership before write.

### Repository-Owned Migration, Flag, or Deployment Behavior

No migration or feature flag is needed: no tag store exists. The independent schema is `1.0`. Production and development resolve the store through the same application-data path provider, with a test-injected temporary path.

## Implementation Approach

- Slicing: hybrid—first establish the shared persistence/service/search contract, then deliver assignment and filter UI slices.
- Dependency order: store/models → service + unit tests → API + integration tests → landing editors → navigator filter → deletion cleanup and final cross-layer proof.
- First observable checkpoint: API-level create path, assignment, effective-tag result, and AND search persisted across a new service instance.
- Rationale: UI cannot safely define hierarchy semantics; once server contracts are proven, UI work follows existing component and API patterns.

## Verification Strategy

| Claim / AC | Level | Repository command or operation | Observable pass condition |
|---|---|---|---|
| Store/path/rename/delete rules | L1 | `dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-build -v q` after focused tag tests | Focused tests prove stable IDs, normalized uniqueness, and delete guards. |
| Assignment, inheritance, descendant AND semantics | L1/L2 | Agent service tests plus API endpoint tests | Before/action/after persisted assignments and exact search result assertions pass. |
| Missing registered worktree search | L2 | ApiHost test fixture with registration but absent `worktree.json` | Result includes matching worktree with `available: false`. |
| Landing editors and navigator filter | L1 | `Push-Location studio; npm test -- --run; Pop-Location` | Semantic UI tests show direct/inherited state, picker action, AND filter hierarchy, and recovery from API error. |
| Full compile/type boundary | L1 | `dotnet build AgentAssistPlcDev.sln -v q`; `Push-Location studio; npm run build; Pop-Location` | Both builds exit successfully. |
| User journey | L3 | `./launch.ps1` then browser automation only after health checks | Assign deep Project/worktree tags and filter navigator; no TIA mutation is approved. |

## Material Risks

| Risk | Evidence | In-scope response or verification |
|---|---|---|
| Concurrent host processes lose updates | Single JSON document and current host-registry mutex pattern | Store owns read-modify-write under a named mutex; concurrency test verifies serialized writes. |
| Directory loss hides a valid logical assignment | Overview intentionally retains registered worktrees | Search derives entity membership from registrations and marks availability separately. |
| Global taxonomy accidentally enters Git/TIA lifecycle | Worktree runtime metadata is ignored and coordinator owns lifecycle | Place store in host app data and restrict coordinator change to post-success cleanup. |
| Stale frontend semantics diverge from server | No current navigator query model | Return effective tags/search results from API; client does not compute inheritance/descendants. |

## References

- `docs/adr/ADR-0002-global-workbench-tag-catalog.md`
- `docs/ui-spec/hierarchical-workbench-tags-ui-spec.md`
- `src/Agent/Workbench/WorkbenchModels.cs`
- `src/Agent/Workbench/WorkbenchCatalog.cs`
- `src/ApiHost/WorkbenchApiModels.cs`

## Update History

| Date | Version | Changes |
|---|---|---|
| 2026-09-08 | 1.0 | Initial design |
