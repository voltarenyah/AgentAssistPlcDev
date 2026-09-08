# ADR-0002: Global Workbench Tag Catalog

## Status

Accepted

## Context

Projects are `WorkbenchMetadata` instances and worktrees have stable `WorktreeId` values, but their metadata is split between `workbench.json` and ignored `worktree.json` files. The hierarchical tag feature must filter across all visible Workbenches and worktrees. A path such as `machine/press` must therefore denote one taxonomy node regardless of which Workbench created or uses it.

## Decision Point

- **Question**: Where do the hierarchy and assignments live?
- **Why a decision exists**: A per-Workbench store keeps data portable with a Workbench root, while a host-owned store provides one stable taxonomy and one cross-Workbench query space.
- **Scope boundary**: Hierarchy, assignment, inheritance, and structured tag filtering; not Git/TIA engineering data or future subtree move/merge features.

## Decision

Persist one application-global tag database at `%LOCALAPPDATA%\\AutomationWorkbench\\metadata\\tags.json`. It contains stable `TagId` nodes and assignments to `WorkbenchId` and `WorktreeId`; it is never written into a Git worktree.

### Decision Details

| Item | Content |
|---|---|
| **Decision** | Use an independent, host-owned JSON tag database with its own `1.0` schema. |
| **Why this** | It makes one path resolve to one ID across all Workbenches and preserves assignments when a registered worktree directory is unavailable. |
| **Known unknowns** | None that alter V1; migration from a nonexistent prior tag store is not required. |
| **Reconsider when** | A supported multi-user/shared-workspace deployment requires concurrent access across hosts or remote synchronization. |

## Rationale

`WorkbenchCatalog` currently discovers and lists multiple Workbenches, while the navigator renders them together. A per-Workbench taxonomy would make identical displayed paths different logical tags and require reconciliation during global filtering. The selected design instead uses generated IDs for all three entity types and retains existing Workbench JSON/Git lifecycle ownership.

### Options Considered

| Option | Requirement and repository fit | Current-scope benefit | Lifecycle cost | Maintainability | Material trade-offs |
|---|---|---|---|---|---|
| Host-owned `tags.json` | Matches `TrustedWorkbenchRootRegistry` host metadata and global search requirement | One hierarchy and direct cross-Workbench filtering | New small store and synchronization lock | Isolates tags from TIA/Git lifecycle | Tags do not travel if a Workbench folder alone is copied |
| Per-Workbench `tags.json` | Matches Workbench-local JSON layout | Portable individual taxonomy | Cross-Workbench meaning and search reconciliation | Duplicates taxonomy lifecycle | Does not satisfy a single global `machine/press` identity |
| Extend PLC SQLite knowledge DB | Does not match the database's per-device, derived graph purpose | None | Cross-domain schema and device coupling | Couples organizational metadata to disposable data | Incorrect ownership and lifecycle |

**Selected**: host-owned `tags.json` is the smallest design that fulfills global taxonomy and filtering without changing the established Workbench or PLC knowledge stores.

## Consequences

### Positive Consequences

- Tag paths have one identity across all Workbenches.
- Direct assignments survive an unavailable worktree directory long enough for catalog-driven search and cleanup.
- Workbench tags can be inherited at read time without copying assignments.

### Negative Consequences

- The tag database requires explicit cleanup when a Workbench or worktree is deleted.
- The store must serialize host-process read-modify-write operations.

### Neutral Consequences

- Existing `workbench.json` and `worktree.json` schema versions are unchanged.

## Architecture Impact

`WorkbenchTagStore` becomes a sibling of `WorkbenchCatalog`; `WorkbenchTagService` validates entity membership through `WorkbenchApiState`/`WorkbenchCatalog` data. `WorkbenchCoordinator` remains responsible for TIA and Git worktree lifecycle, calling only cleanup methods after successful deletion.

## Implementation Guidance

Use stable IDs, parent IDs, and calculated inheritance. Keep descendant expansion, AND matching, and assignment validation server-side. Do not store full paths as IDs or copy inherited assignments.

## Related Information

- `docs/design/hierarchical-workbench-tags-design.md`
- `docs/ui-spec/hierarchical-workbench-tags-ui-spec.md`
- `src/Agent/Workbench/WorkbenchCatalog.cs`
- `src/Contracts/Sandbox/TrustedWorkbenchRootRegistry.cs`
