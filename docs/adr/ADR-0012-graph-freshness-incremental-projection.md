# ADR-0012 Keeping the engineering graph current: invalidate on change, check on selection

## Status

Accepted

## Context

**In plain terms.** Once the screens read from the saved facts, those facts must not go stale. Three
things can change a device's facts today without anything noticing: switching a branch inside a
worktree, editing files outside the application, and deleting a worktree. This decision records how the
saved facts are refreshed and how a missed change is detected.

ADR-0011 makes the engineering graph the read model for engineering facts. That only works if the
graph is current. What exists today:

**Write points that already run when facts change** (real events; each already writes state or the
graph):

| Event | Where | What it writes today |
|---|---|---|
| Approved source apply (managed XML changed) | `WorkbenchCoordinator.cs:2404-2415` | `LastExportUtc`, `Knowledge.Stale/BaselineStale` |
| Device knowledge updated / rebuilt | `WorkbenchCoordinator.cs:2513-2521`, `:2543-2548` | `AppliedOverlayHashes`, cleared flags |
| Source commit | `WorkbenchCoordinator.cs:3540-3543` | commit attribution + graph evidence |
| SVN savepoint / baseline commit | `WorkbenchCoordinator.cs:3978-3981`, `:3836-3839` | graph evidence |
| Workbench creation / device / worktree bootstrap | `WorkbenchCoordinator.cs:739-740`, `:2600-2604`, `:2682-2686` | baseline graph evidence |
| Task staging (whole device manifest registered) | `WorkbenchApiModels.cs:961-963`, `src/ApiHost/TaskSourceStagingTool.cs:172-174` | source-object nodes |

**Event locations where a projection call must be added** (they exist, but they write no graph fact):

| Event | Where | Today |
|---|---|---|
| Worktree create / delete | `WorkbenchApiModels.cs:1091`, `:1122` | `s.Refresh(id)` only reloads the catalog and the runtime projection (`:180-185`); worktree create registers no source-object nodes |
| Branch switch inside a worktree | `vc_checkout`, routed at `CompatibilityEndpoints.cs:648-649` | nothing invalidates anything |

**What is missing**: no `FileSystemWatcher`, no hosted service and no timer anywhere in `src/` — the
only `IHostedService` is `McpRuntimeHostedService` (`Program.cs:278-282`), which starts and stops the
MCP child processes. And three paths change facts with no signal:

- A branch switch inside a worktree invalidates nothing; `KnowledgeState` records no HEAD
  (`WorkbenchModels.cs:127-131`), and `MergeRuntimeObservations` keeps the previous Head while git
  status is `unknown` (`WorkbenchApiModels.cs:218-219`).
- An edit outside the app sets no stale flag; only `DeviceSourceResolver` (in-app) marks knowledge stale
  (`:24`, `:76` via `Program.cs:89-98`).
- Worktree deletion leaves the removed worktree's rows in the shared per-workbench database; the only
  graph deletions today are task- and session-scoped (`EngineeringGraphService.cs:197`, `:343-346`).

**Prior art, and its failure modes**: the knowledge store already refreshes incrementally by hash
(`WorkbenchCoordinator.UpdateKnowledgeAsync:2439-2524`): paths whose raw SHA256 differs from
`AppliedOverlayHashes` are re-ingested, an unchanged device makes no call, and a missing database or
`BaselineStale` forces a full ingest. Its gaps are instructive: a new component fails with
`COMPONENT_NOT_IN_DATABASE` (`KnowledgeTools.cs:394-402`), a deleted file is never removed (only stale
paths are sent, `:2493-2501` — inferred from the code, not observed), and the "the file list may have
changed" flag can be lost in the read-modify-write window between `:2449` and `:2513`.

**Two hash domains exist**: the knowledge store and `AppliedOverlayHashes` use raw SHA256
(`WorkbenchCoordinator.cs:5155-5156`); the export manifest's `contentHash` and `DeviceReconciler` use the
normalised `XmlContentHash` (`src/Contracts/Engineering/XmlContentHash.cs:16-23`). The manifest carries
per component, **independently of `contentHash`**: `fingerprints`, `status`, `modifiedDate`,
`codeModifiedDate` (`src/Mcp.Engineering/Export/ExportMetadata.cs:95,103,104,110,115`).

## Decision Point

- **Question**: how does the graph stay current when a device's facts change?
- **Why a decision exists**: three materially different options are supported by the repository —
  event-driven projection that lets the write points invalidate, event-driven projection plus a stamp
  re-derived at every read boundary, and periodic re-scanning — and the choice decides what a read may
  assume about freshness.
- **Scope boundary**: ADR-0011 (the graph is the read model) and the measured requirement that reads
  stay in the tens of milliseconds.

## Decision

Project facts incrementally at the existing write points, let those write points **invalidate** the
device's projection, and check cheaply at selection boundaries:

1. **Ingest** reads `metadata.json` once per device and writes only the nodes and properties whose
   projected content changed; additions, deletions and renames are all handled explicitly.
2. **Invalidate on change**: every write point above — including the branch-switch route
   (`CompatibilityEndpoints.cs:648-649`) and worktree create/delete, which today write no graph fact —
   marks the affected device's projection as needing a re-projection. This is the primary mechanism: the
   code that changes the facts is the code that says so.
3. **Check on selection**: a selection or refresh boundary compares the stored **manifest digest** (the
   export root plus a digest over every projected manifest field, not `contentHash` alone) against the
   manifest on disk — a parse the ingest performs anyway — and re-projects before serving on mismatch.
   The check does not require a worktree HEAD read.
4. **Repair**: a reconciliation pass re-projects a device whose projection is invalidated but whose
   digest matches, and removes facts (nodes, properties and the edges referencing them) of worktrees
   that no longer exist.

### Decision Details

| Item | Content |
|------|---------|
| **Decision** | Write points invalidate; the selection boundary checks the manifest digest; a repair pass reconciles what neither caught. |
| **Why this** | The write points already exist at exactly the places facts change, so the common case costs one extra call and no re-derivation; the digest check catches what has no event; and this avoids the one input with no cheap accessor — the worktree HEAD. |
| **Known unknowns** | The digest check's cost at a selection boundary (a full `metadata.json` parse, ~20-30 ms today) and the projection's own cost for ~1,100 objects. Both are measured by the Design Doc's verification tasks. |
| **Reconsider when** | A fact source appears that changes without either an event or a manifest change, or the digest check becomes a visible delay at selection. |

## Rationale

### Options Considered

| Option | Requirement and repository fit | Current-scope benefit | Lifecycle cost | Maintainability | Material trade-offs |
|---|---|---|---|---|---|
| **A. Write-point invalidation + manifest-digest check on selection (selected)** | Uses the eight existing write points; needs one added call at the branch-switch and worktree routes; the digest is the same parse the ingest already does | Common case costs one call; catches the three silent paths at the next selection; no HEAD accessor needed | One invalidation flag per device, one digest comparison, one repair pass | One rule: "invalidated or digest changed → re-project before serving" | A change that neither an event nor the manifest reflects is caught only by the repair pass |
| B. Re-derive a stamp (manifest hashes + worktree HEAD) at every selection boundary | Also workable, but the HEAD component has **no cheap accessor**: `BuildRuntimeSummaries` hardcodes `GitStatus="unknown"` and takes Head from `worktree.json`'s `BaseCommit` (`WorkbenchApiModels.cs:250-251`), `VcStatusResult` has no HEAD field (`src/Mcp.VersionControl/Git/Models.cs:7-12`), and only MCP round trips return a HEAD (`vc_branches` `VersionControlTools.cs:224-228`, `vc_log` `:139-146`) | Detects every change at the boundary without any invalidation plumbing | A cross-process call (or a stale base-commit value) on every selection, plus the same digest work | More moving parts for a strictly weaker guarantee | The stamp's HEAD component is either expensive or wrong; and a stamp covering only `contentHash` can agree while `fingerprints`/`status`/`modifiedDate` are stale |
| C. Periodic re-scan | Needs no events and no stamp | Eventually consistent without extra plumbing | A timer walking every device; freshness bounded by the interval; cost scales with device count | New background owner in a codebase that has none | Wastes the work the write points already do, and still needs deletion handling |

**Selected**: Option A. Option B's detection idea is right, but its only available HEAD source is a
cross-process call or a value that is not the current HEAD, and the write points already know when facts
change — so prevention replaces detection for the events, and the digest covers the rest.

## Consequences

### Positive Consequences

- The common path (apply → project → read) costs one invalidation call and a hash diff, the same shape
  as the knowledge store's proven incremental update.
- A branch switch, an out-of-app edit or a deleted worktree is caught at the next selection instead of
  silently serving wrong facts.
- Deletions and additions are first-class, so the graph does not accumulate rows for objects that no
  longer exist.

### Negative Consequences

- The invalidation call must be added at routes that today write no graph fact (worktree create/delete,
  branch switch); missing one re-creates the silent-change problem for that route.
- A mismatch turns a read boundary into a **write**: the projection must run in its own short
  transaction before the read is served, and it must not be triggered from inside a read that cannot
  retry. Under the rollback journal with `busy_timeout=5000`, expiry surfaces as a SQLite error, which is
  the original "database is locked" symptom (`docs/agent-prompts/run-report-2026-10-03.md:496-497`); the
  projection therefore stays a short critical section and the boundary reports a projection failure as a
  projection failure, not as a read failure.
- Two hash domains remain in the repository. **In plain terms**: the app computes file fingerprints in
  two different ways; this work uses the export's own fingerprint and does not touch the knowledge
  store's. They look similar and are not interchangeable.
- The digest must cover every projected manifest field, or a projected fact can go stale while the
  digest agrees.

### Neutral Consequences

- No background service is introduced; the existing request-driven shape is preserved.
- `engineering-state/revision.json` and the runtime revision keep their current roles and are not reused
  as the projection digest.

## Architecture Impact

- New projection state (invalidation flag, digest, per-node hashes) inside the engineering graph schema.
- The eight write points gain one call each; the branch-switch route and the worktree create/delete
  routes gain one; the ApiHost selection/refresh routes gain the digest check.
- The knowledge store's own staleness logic is untouched; its hashes remain a different domain.

## Implementation Guidance

- Handle removal explicitly: a path present in the projection but absent from the manifest is deleted
  from the graph **together with the edges that reference it** (the graph has no foreign key on
  `graph_edges`, so `RemoveEntity`, which removes both directions, is the path to use).
- Do not clear a "the file set may have changed" flag unconditionally after a partial update; the
  knowledge store's `BaselineStale` handling is the counter-example.
- Keep the digest check out of per-row reads; it belongs at the boundaries named in the decision.
- Record which input moved (an invalidated flag, or which manifest field's digest changed) and log it as
  a projection event rather than an error.
- Do not add a worktree HEAD read to the boundary check: no cheap accessor exists, so a branch switch is
  signalled by its route instead.

## Related Information

- ADR-0011 (the engineering graph is the read model).
- `docs/knowledge-workflow.md:10-15` (the knowledge store's staleness contract),
  `src/Agent/Workbench/WorkbenchCoordinator.cs:2439-2524`.
- `src/Mcp.Engineering/Export/ExportMetadata.cs:87-120` (per-component `contentHash`, `fingerprints`,
  `status`, `modifiedDate`), `src/Contracts/Engineering/XmlContentHash.cs:16-23`.
- `src/Agent/Workbench/DeviceReconciler.cs:57-167` (preview/apply with a stale-approval guard — the
  model for the repair pass).
- Review record: the ADR batch review (2026-10-04) corrected two claims here — `s.Refresh(id)` is not a
  graph write point, and the worktree HEAD has no cheap accessor — and both corrections are folded into
  the decision above.
