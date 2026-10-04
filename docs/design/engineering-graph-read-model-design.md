# Design Document: Engineering facts served from the engineering graph

## Overview

- Outcome: the engineering facts the Studio front end reads for a device come from the engineering graph
  (`<workbench>/.automation/engineering.db`) instead of the exported files, so opening a device stops
  re-reading and re-parsing them; the graph is kept current by incremental projection. Carve-outs are
  named in the Requirement Boundary: the consistency service's comparison reads, the knowledge store's
  own imports, and the hardware/AML subtree (sequenced later).
- Scope: engineering-graph schema and ingest (Agent/Workbench), the device/source read routes
  (ApiHost), and the population of the facts those routes serve. The Studio client changes only where it
  sends a source-object id (and, if needed, the per-device fan-out); no response shape changes.
- UI Spec: not applicable — this is a backend change; every HTTP route and response shape the Studio
  consumes is preserved.
- Governing ADRs: `docs/adr/ADR-0011-engineering-graph-as-the-read-model.md`,
  `docs/adr/ADR-0012-graph-freshness-incremental-projection.md`

## Requirement Boundary

- PRD or convergence carrier: embedded record (no `docs/prd/` exists; Structural Scale is Medium).
- Current requirements (from the user's wording, verbatim intent preserved):
  1. Expand the engineering graph's coverage so it holds what the front end needs.
  2. Store in the graph what is currently obtained by reading source files or indirect files such as
     `metadata.json`; do not read those on a read path.
  3. Solve the field mismatch with a compatible shape — a node table plus a node-property table
     recording each node's attached information.
  4. Move the information currently managed by `metadata.json` fully into the graph.
- Non-goals (user-decided, 2026-10-04 — these are boundaries, not preferences):
  - **The knowledge database stays separate.** The user's boundary in their words: knowledge manages the
    PLC semantic information *inside* one device; engineering manages the *global engineering-file*
    information and does not concern itself with PLC logic. This design therefore never merges the two,
    and the knowledge store's schema, hashes and staleness rules stay untouched.
  - **The Studio client is not changed in this pass.** The backend and the graph are fixed first, so the
    identity fix is resolved server-side (AC-005) and no route, response shape or client call site
    changes.
  - **The block `modified` flag is not changed either.** The projection stores the manifest's `status`
    and `modifiedDate` accurately as properties, while the value the page shows stays exactly what it is
    today (AC-002); giving the "modified blocks" summary meaning is a later, separate change.
  - Git, SVN and TIA live-session reads keep their current sources; they are not facts about the exported
    source tree.
  - No filesystem watcher and no timer (the only hosted service in the codebase starts the MCP child
    processes); ADR-0012 keeps the request-driven shape.
  - The consistency service's comparison reads (`WorkbenchConsistencyService`, `PlcSourceScanner`) and
    the knowledge store's own manifest imports stay as they are: comparing source against a baseline
    genuinely has to read the files, and the knowledge store is a separate ingest.
  - The hardware/AML export subtree (`hardware/manifest.json`, `project.aml`) is **in scope by the
    stated principle** but is sequenced after the PLC source facts: it is a different exporter subtree
    with a measured cost of 11-63 ms, so it carries no latency requirement today.
- Open requirement fields: none.

## Acceptance Criteria

- **AC-001** — **When** a device page or a task picker asks for device facts, the answer shall come from
  the graph without reading the exported files. Source: requirement 2. Observable: opening the device
  reads no exported file at all (proved by removing or corrupting the XML files after an ingest and
  repeating the request), and the answer arrives in under 100 ms instead of today's ~1.2 s (up to ~12 s
  the first time). Routes in scope: the device snapshot, `…/blocks`, `…/source-objects`, the graph-entity
  route, **and the two compat routes `/api/project/info` and `/api/blocks`**
  (`CompatibilityEndpoints.cs:549-553`, `:579-583`), which call the same reader per request and have no
  live caller — they are switched too, so the outcome statement is true for every route.
- **AC-002** — **When** the device page renders, every value it shows shall equal what it shows today:
  device identity and export metadata (all 14 `DeviceExportMetadata` fields), knowledge state and its
  timestamp, the source-object list with name/category/number/language/group path/relative path/content
  hash/**`isKnowHowProtected`**/**`modifiedDate`**/status/fingerprints/evidence kind, the block subset,
  the counts, and ingest diagnostics. Source: requirements 1, 2. The block list and the device page's
  count keep today's meaning — block-category objects only (`Blocks/`+`DB/`), the `blocks.Count` the page
  shows, which is a different number from the picker's comparable manifest objects. Diagnostics keep
  today's meaning too: a manifest entry whose file is missing on disk, or a malformed or rejected path,
  must still surface.
- **AC-003** — **When** a device's exported source changes and one of the projection's write points runs
  (source apply, bootstrap, commit evidence, staging, branch switch, worktree create/delete), the next
  projection shall update only the affected rows and delete the rows of removed objects. Source:
  requirement 2. Observable: the projection reports how many property rows it wrote; that number is zero
  on a second run with no change; a changed object's rows differ; a removed object has none.
- **AC-004** — **When** a branch is switched inside a worktree, or the exported source is edited outside
  the application, the next selection boundary shall detect the change and re-project before serving.
  Source: requirement 2. Observable: the boundary records which input moved (the invalidation flag or
  the manifest digest), the following read reflects the new content, the check's own cost stays inside
  AC-001's budget, and a failed re-projection is reported as a projection failure rather than served as
  stale facts.
- **AC-005** — **When** the source panel expands a source object the graph has links for, the
  traceability read shall resolve. Source: requirement 1. Resolution rule: an exact entity id wins;
  otherwise the id is split on the first `:` **only when the prefix names a device registered in the
  current selection**; otherwise the route returns a 4xx with a named error code. Observable: **200 for
  an existing object of each id form**, including the `source:{relativePath}` form the reader itself
  emits (`DeviceSnapshot.cs:254`, `:488`), and a named error for an unresolvable one — never the silent
  "no links" the panel shows today.
- **AC-006** — **When** a commit is made through any in-app write path, including a worktree merge, its
  commit→source-object read shall resolve. Source: requirement 1. Observable: in the workbench whose
  graph holds no `git_commit` nodes today, a `git_commit` node exists after the commit and its
  commit→source-object read returns its objects. Routes in scope: the coordinator's commit paths, the
  raw gateway commit and checkout routes (`WorkbenchApiModels.cs:1972-1976`,
  `CompatibilityEndpoints.cs:642-643`), which bypass the coordinator's evidence indexing entirely, and
  `MergeWorktreeAsync`.
- **AC-007** — **When** a worktree is deleted, no facts for that worktree shall remain in the graph, and
  facts still owned by another worktree shall survive. Source: requirement 1. Observable: no nodes,
  properties or edges remain for the deleted worktree, while a device registered in another worktree
  keeps its facts.
- **AC-008** — **When** the task picker lists source objects, instance DBs shall stay excluded, while the
  device page's count shall keep counting block-category objects. Source: requirement 2 (preserved
  semantics). Observable: the picker-exclusion test keeps passing
  (`tests/Agent.Tests/DeviceSnapshotReaderTests.cs:474`) and the device page's count assertion names the
  block-category number for the same fixture.
- **AC-009** — **When** an existing graph database is opened, it shall migrate to the new schema version
  without losing anything already saved — task history, staged source objects, commit links, SVN
  revisions and staged-file evidence all survive — and a pre-migration backup shall exist. Opening an
  already-current database shall take no write lock, and a database newer than the supported version
  shall still be refused. Source: requirements 1, 2.

## Existing Evidence

| Evidence | Location | Design effect |
|---|---|---|
| The device snapshot read walks the whole source root and parses every block XML per request | `src/Agent/Workbench/DeviceSnapshot.cs:78-105`, `:340-446` | Replaced by graph reads; the readers become the ingest implementation |
| The same file documents the cheaper rule (manifest first, crawl only when missing/legacy) | `DeviceSnapshot.cs:133-153`, `:114-121` | The manifest is the ingest source; the crawl becomes the fallback ingester |
| Measured cost, 1097-object device: snapshot 1213 ms warm / 11959 ms cold (1.19 MB); `blocks` 1017 ms; `source-objects` 20 ms; graph task read 6 ms | this session's read-path inventory | Sets the AC-001 budget and proves the manifest path is already fast |
| The front end calls the device read once per device on the worktree overview tab | `studio/src/studio/workbench/WorktreeLandingPage.tsx:151-178`, `TaskCommitsSection.tsx:78-88` | The fan-out is the reason AC-001 is measured per device, not per page |
| `graph_entities` stores identity only (no payload); source-object ids are `{deviceId}:{manifestId}` | `EngineeringGraphSchema.cs:51-59`, `EngineeringGraphEvidenceIndexer.cs:60-63` | The property table is added; the existing id convention is kept and enforced |
| Live databases: one workbench 776 entities (774 `source_object`, 1 `git_commit`), another 1325 entities (1314 `source_object`, **0** `git_commit`) | read-only inspection of both `engineering.db` files | AC-006; the projection must not assume events always fired |
| Schema version ladder with per-step migration, transactional DDL, `VACUUM INTO .bak` before migrating | `EngineeringGraphSchema.cs:19-141`, `EngineeringGraphStore.cs:11-55` | The new schema is v6 in the same ladder; AC-009 is provable with existing test patterns |
| Store: one DB per workbench, single connection per scope, `Pooling=false`, `busy_timeout=5000`, SQLite's default rollback journal (the code deliberately sets no journal mode; WAL rejected) | `EngineeringGraphStore.cs:19-28`, `:79-84`, `:70-77` | Read concurrency must be designed inside this model; the device page must not open many scopes |
| "database is locked" history and its fix | `docs/agent-prompts/run-report-2026-10-03.md:493-517`, `tests/Agent.Tests/EngineeringGraphStoreTests.cs:111-134` | Regression risk to guard: opening scopes must stay lock-free while the schema is current |
| Knowledge store's incremental refresh: `AppliedOverlayHashes` (raw SHA256), `BaselineStale`, `update_components` | `WorkbenchCoordinator.cs:2439-2524` | Reused as the shape of the projection; its add/delete gaps are explicitly not repeated |
| Manifest per component carries `contentHash` (normalised), `fingerprints`, `status`, `modifiedDate` | `src/Mcp.Engineering/Export/ExportMetadata.cs:87-120` | The change-detection key; the normalised domain is chosen, raw SHA256 stays the knowledge store's |
| No filesystem watcher and no timer exists under `src/`; the only hosted service starts the MCP child processes (`Program.cs:278`) | repository search | The projection is event-driven plus an invalidation/digest check; no new background owner |
| `ParseGraphEntityKind` has no `task` case; the source panel sends a bare manifest id | `WorkbenchApiModels.cs:2214-2221`, `studio/src/studio/PlcSourcePanel.tsx:115` | AC-005 and the read API's identity contract |
| Verification commands available | `dotnet test tests/Agent.Tests`, `tests/ApiHost.Tests`, `cd studio && npm test` | The verification strategy uses existing lanes |

## Design

### Selected Design

Facts are stored as **nodes plus properties**, and reads serve them.

1. **Nodes** keep their current meaning (`graph_entities`: kind, id, workbench, worktree, device,
   external ref). Two node kinds are added: `device` and `worktree`. Existing `source_object`,
   `task`, `session`, `git_commit` and `svn_revision` nodes are unchanged.
2. **Properties** are stored in one table keyed by `(entity_kind, entity_id, name)` with a typed value
   column set (text, number, flag, timestamp, json) so a fact can be read without a join, and a
   `source` column recording which ingest wrote it. Properties are replaced as a set per node per
   ingest, so a removed fact disappears.
   - **The property table must survive node registration.** Every connection sets `ForeignKeys = true`
     (`EngineeringGraphStore.cs:23`) and node registration uses `INSERT OR REPLACE`
     (`EngineeringGraphService.cs:207`, `:225`), which runs on every stage click with the whole device
     manifest (`WorkbenchApiModels.cs:961-963`) and on every source-object anchor read (`:2210`). In
     SQLite `INSERT OR REPLACE` deletes the replaced row, so a property table carrying an
     `ON DELETE CASCADE` foreign key to `graph_entities` would lose a device's facts on the next stage
     click. The schema therefore keeps the property table's key independent of that replacement (no
     cascading delete from the entity row), and the ingest writes properties with an upsert that never
     replaces the entity row.
3. **Ingest** reads `metadata.json` once per device (components, `device` section, export root),
   derives group paths and evidence kinds exactly as the current reader does, and writes only nodes and
   properties whose normalised `contentHash`, path or presence changed. The block list is the
   block-category subset of the same rows. Two counts must stay distinct: the device page shows the
   block-crawl count (`DeviceSnapshot.cs:102` passes `blocks.Count`, i.e. only the `Blocks/`+`DB/`
   subset), while the task picker returns the comparable manifest objects (instance DBs excluded,
   `:129-131`). They are different numbers today and the projection keeps them different.
   - **The crawl fallback cannot classify an instance DB**: `CommittedSourceManifest.EvidenceKindOf`
     needs `siemensTypeName`, which the crawl passes as `null` (`DeviceSnapshot.cs:153`), and
     `BlockTypeOf` maps `SW.Blocks.InstanceDB` to the generic `DB` (`:540-547`). When the manifest is
     missing or legacy, the fallback must mark such objects **unclassified** rather than as standard
     blocks, so the picker's exclusion rule does not silently stop working.
   - **A preserved quirk, recorded deliberately**: the crawl builds every block with `modified = false`
     (`DeviceSnapshot.cs:495`, locked in by `tests/Agent.Tests/DeviceSnapshotReaderTests.cs:203`), so the
     worktree overview's "modified blocks" summary is empty today. The projection keeps that value
     (AC-002 preserves today's output) and additionally stores the manifest's `status` and
     `modifiedDate` as properties, so giving that summary meaning later is a query change, not a
     re-projection. Giving it meaning now is out of scope for this design.
4. **Device and knowledge facts** (identity, export metadata, knowledge state and timestamp, source
   root, project path, counts, ingest diagnostics) are properties of the `device` node.
5. **Freshness** (ADR-0012): the write points that change facts **invalidate** the affected device's
   projection, and a selection or refresh boundary compares the stored **manifest digest** — the export
   root plus a digest over every projected manifest field, not `contentHash` alone — against the
   manifest on disk, re-projecting before serving on mismatch. The boundary does **not** read a worktree
   HEAD: no cheap accessor exists (`WorkbenchApiModels.cs:250-251` takes Head from `worktree.json`'s
   base commit; `VcStatusResult` has no HEAD field, `src/Mcp.VersionControl/Git/Models.cs:7-12`; only MCP
   round trips return one), so a branch switch is signalled by its own route
   (`CompatibilityEndpoints.cs:648-649`) instead. The check and the rows it guards share one scope, and a
   projection failure is reported as a projection failure rather than as a read failure.
6. **Read routes** (`GET …/devices/{dev}`, `…/blocks`, `…/source-objects`, the graph-entity fallback,
   **and the two compat routes `/api/project/info` and `/api/blocks`**) assemble their existing response
   shapes from the graph. The crawl-based readers remain only as the ingest fallback for a missing or
   legacy manifest.
7. **Identity**: one source-object identity, `{deviceId}:{manifestId}`, is accepted and produced
   everywhere. The graph-entity route accepts the bare manifest id by resolving it against the selected
   device, and returns a clear error when it cannot — never a silent 404 for an object that exists.
8. **Repair**: a reconciliation entry point re-projects a device whose projection is invalidated but
   whose digest matches, and deletes the facts of worktrees that no longer exist — nodes, their
   properties, and the edges that reference them (the graph has no foreign key on `graph_edges`, so
   `RemoveEntity`, which removes both directions, is the path to use).
9. **Concurrency**, inside the store's existing model (one `engineering.db` per workbench, one
   connection per scope, `Pooling=false`, `busy_timeout=5000`, and SQLite's default rollback journal —
   the store deliberately sets no journal mode):
   - A read scope opens the database without taking a write lock, because the migration transaction is
     only opened when the schema lags (`EngineeringGraphStore.cs:37-48`, pinned by
     `tests/Agent.Tests/EngineeringGraphStoreTests.cs:111-134`). Concurrent device reads are therefore
     ordinary SQLite readers and do not serialise against each other.
   - The projection writes **one short transaction per device**: read the manifest, compute the diff,
     then open the transaction and write only the changed rows. No file read or XML parse happens
     inside that transaction, so a projection never holds the writer lock across slow I/O.
   - Under SQLite's default rollback journal — the store never sets a journal mode
     (`EngineeringGraphStore.cs:82` sets only `busy_timeout`; `:70-77` records that WAL is deliberately
     rejected so a failed migration leaves the file byte-identical) — a writer waits for readers and vice
     versa, bounded by `busy_timeout=5000`. A projection therefore stays a short critical section, and if
     the timeout expires the boundary reports a projection failure (the historical "database is locked"
     symptom, `docs/agent-prompts/run-report-2026-10-03.md:496-497`) rather than letting a read appear to
     fail.
   - The device page opens **one** graph scope per request, not one per row or per device fact.

### Change Surface

| Responsibility or expected file | Change | Governing source | Unaffected boundary to preserve |
|---|---|---|---|
| `src/Agent/Workbench/EngineeringGraph/EngineeringGraphSchema.cs` | Add schema v6: node-property table, `device`/`worktree` node kinds, invalidation flag and digest properties | AC-001, AC-009 | v1-v5 steps and their ordering stay intact; existing rows survive; the property table cannot be cascaded away by node registration |
| `src/Agent/Workbench/EngineeringGraph/` (new projection service) | Ingest from the manifest, hash-diff, property writes, invalidation/digest compare, removal | AC-002, AC-003, AC-004 | `EngineeringGraphService`'s task/stage/edge behaviour is untouched |
| `src/Agent/Workbench/DeviceSnapshot.cs` | Readers become the ingest implementation; the read path no longer calls `ReadBlocks` | AC-001 | Manifest parsing rules (tolerances, instance-DB exclusion, evidence kind) stay identical |
| `src/ApiHost/WorkbenchApiModels.cs` device routes (`:1739-1760`) and the graph-entity route (`:738-776`, `:2179-2212`) | Serve from the graph; accept the bare manifest id; add the `task` kind or document its absence | AC-001, AC-005 | Response shapes, status codes for genuinely missing entities, path validation |
| `src/ApiHost/CompatibilityEndpoints.cs` (`:549-553` project info, `:579-583` blocks) | Serve from the graph like the device routes; they call the same reader per request today and have no live caller | AC-001 | Their existing response shapes and compat status |
| `src/Agent/Workbench/SourceObjectInspector.cs` and the hardware readers (`HardwareConfigurationReader.cs:324`, `:339`) | Become ingest for the facts their routes serve; sequenced last (plan phases 5) | AC-001, AC-002 | The inspected payload and the hardware pages' values |
| `src/Agent/Workbench/WorkbenchCoordinator.cs` write points (`:2404`, `:2513`, `:2543`, `:3540`, `:3978`, `:739`, `:2600`, `:2682`) | Call the projection after the fact change | AC-003, AC-006 | The existing write semantics, ordering and error handling |
| Commit evidence indexing | Ensure every commit path produces a `git_commit` node: the coordinator's paths, `MergeWorktreeAsync` (`:2842-2877`), and the raw gateway commit/checkout routes (`WorkbenchApiModels.cs:1972-1976`, `CompatibilityEndpoints.cs:642-643`), which bypass the coordinator entirely | AC-006 | The guarded combined-transaction rules for source commits |
| Worktree deletion (`WorkbenchCoordinator.cs:1916-1970`) | Remove the deleted worktree's nodes, properties and edges | AC-007 | Git and catalog deletion behaviour |
| `studio/src/studio/PlcSourcePanel.tsx` | **No change in this pass** (user-decided): the server resolves the bare manifest id, so the panel keeps sending what it sends today | AC-005 | The panel's empty-state and error handling |
| `tests/Agent.Tests/DeviceSnapshotReaderTests.cs` | Replace the "600 objects under 2 s" crawl budget with the projection-read budget and a no-XML-read assertion | AC-001 | The manifest-tolerance and diagnostics tests |

### Components and Flow

```text
export / bootstrap / apply / commit / staging / branch switch / worktree create-delete   (events)
        │
        ▼
  projection service ── reads metadata.json (once per device) ── hash-diff ──► graph nodes + properties
        │                                                                          │
        └── writes the projection's invalidation flag and manifest digest ─────────┘

selection / refresh boundary ── digest compare ──(mismatch or invalidated)──► re-project ──► serve
                                                    │
read routes (device, blocks, source-objects, graph entity) ── graph only ──► existing response shapes
```

### Contracts, State, and Persistence (When Applicable)

| Boundary | Input / exact format | Output / exact format | Error or state behavior | Compatibility |
|---|---|---|---|---|
| Projection → graph (property write) | one node's property set: `(entity_kind, entity_id, name, value, value_kind, source)` | rows replaced as a set | a failed ingest writes nothing for that device and reports a diagnostic property | new |
| Graph → device read | existing `DeviceSnapshot` JSON (workbenchId, worktreeId, deviceId, plcName, engineeringIdentity, sourceRoot, knowledgeDbPath, sourceProjectPath, knowledge{state,updatedAt}, blocks[], sourceObjects[], sourceObjectCount, diagnostics[], device{…14 fields}) | unchanged field-for-field | a device with no projection is projected on demand before serving | preserved |
| Client → graph-entity route | `{deviceId}:{manifestId}` **and** the bare `{manifestId}` | existing `EngineeringGraphEntityDetailApiResponse` | an id that cannot be resolved against the selected device returns a clear 4xx, not a silent "no links" | widened |
| Migration | existing DB at schema v5 | schema v6, `.bak` present | failure leaves the database byte-identical (existing transactional-DDL guarantee) | preserved |

### Security Boundary (When Applicable)

Ingest keeps the current path discipline: the source root is resolved and validated through
`WorkbenchPaths` and reparse points stay rejected, exactly as `ReadBlocks` does today
(`DeviceSnapshot.cs:344-357`, `:400-404`). Property values are stored as data, never as SQL text. The
graph-entity route must not let a caller name a device outside the current selection — the existing
`RegisterListedSourceObject` guard (`WorkbenchApiModels.cs:2193-2203`) is kept.

### Repository-Owned Migration, Flag, or Deployment Behavior (When Applicable)

Schema v6 is added to the existing ladder. On first open of an older database the store backs it up
(`VACUUM INTO <db>.bak`) and migrates in one transaction. Existing nodes, edges, tasks, stages and file
evidence are preserved; the property table starts empty and fills on the first projection of each
device. No feature flag: a device without a projection is projected on demand, so an upgraded database
behaves like a fresh one for that device.

## Implementation Approach

- Slicing: foundation-first, then outcome-oriented slices.
- Dependency order: (1) schema v6 + property read/write + projection service; (2) device read routes
  served from the graph with the manifest as ingest; (3) identity contract and the traceability read
  (AC-005); (4) commit population across all write paths (AC-006); (5) stamp check at selection
  boundaries (AC-004); (6) reconciliation repair and worktree-deletion cleanup (AC-007); (7) source
  inspector content facts and the hardware/AML subtree.
- First observable checkpoint: after (2), the device page renders identical values from the graph and
  the 1213 ms read is gone.
- Rationale: (1) is the shared dependency every later slice needs; (2) delivers the measured win and is
  the smallest end-to-end proof; (3)-(7) are independent of each other and each has its own AC.

## Verification Strategy

| Claim / AC | Level | Repository command or operation | Observable pass condition |
|---|---|---|---|
| Projection writes and reads a device's facts (AC-002) | L1 | `dotnet test tests/Agent.Tests --filter FullyQualifiedName~Projection` (new tests) | properties round-trip; block subset and count match the manifest fixture |
| No read path parses XML (AC-001) | L1 | new test with a source root whose XML files are unreadable/absent after ingest | the device read returns the same values; no `XDocument`/directory call is made |
| Device read latency (AC-001) | L2 | `Invoke-WebRequest` against `GET …/devices/{dev}` on the live 1097-object device, before/after | ≤ 100 ms warm and no cold-crawl outlier |
| Hash-incremental update and deletion (AC-003) | L1 | `dotnet test tests/Agent.Tests --filter FullyQualifiedName~Projection` | only changed nodes rewritten; a removed path's rows are gone |
| Stamp detection (AC-004) | L2 | integration test: project, change the manifest/HEAD, re-select | mismatch reported and facts re-projected before serving |
| Traceability read (AC-005) | L2 | `dotnet test tests/ApiHost.Tests` + a request with the bare manifest id | 200 with the entity detail for an object that exists |
| Commit population (AC-006) | L2 | `dotnet test tests/ApiHost.Tests` / `tests/Agent.Tests` covering commit and merge paths | a `git_commit` node exists and its commit→source-object read resolves |
| Worktree deletion cleanup (AC-007) | L1 | agent test | no nodes, properties or edges remain for the deleted worktree |
| Preserved picker semantics (AC-008) | L1 | existing source-object tests | instance DBs excluded from the picker, included in the count |
| Migration (AC-009) | L1 | `dotnet test tests/Agent.Tests --filter FullyQualifiedName~EngineeringGraphStore` | v5 data survives, `.bak` exists, failure injection leaves the DB byte-identical |
| Studio regression | L2 | `cd studio && npm test` | unchanged device-page rendering tests still pass |

- Early verification point: slice (2) — the device page served from the graph with identical values.
- Data/persistence boundary: the real `engineering.db` of a workbench, migrated from v5.
- Existing observable-output comparison: the same device's `GET …/devices/{dev}` JSON before and after
  the change, compared field-for-field.

## Material Risks

| Risk | Evidence | In-scope response or verification |
|---|---|---|
| Property reads for ~1100 rows are slower than the crawl they replace | 774-1314 source objects per device (`docs/agent-prompts/run-report-2026-10-03.md:526` records 1,314 components); the property table is row-per-fact (~14k rows for the largest device) | The projection read is measured on the live device in slice (2); the table carries a covering index on `(entity_kind, entity_id)` and the read fetches one device's rows in a single statement. Because the 1.19 MB response body dominates the budget, the proof is a full-body comparison, not a `SELECT` timing |
| The digest check cannot be computed cheaply at a selection boundary | the digest needs a full `metadata.json` parse (`DeviceSnapshot.cs:208-218`), ~20-30 ms today, and the ingest performs the same parse | Slice (5) measures it; the check is skipped when the write points already invalidated the projection, and the digest is stored so a matching case costs one parse |
| A projection triggered from a read boundary can hit the 5 s `busy_timeout` | rollback journal; the historical "database is locked" symptom (`docs/agent-prompts/run-report-2026-10-03.md:496-497`) | The projection runs in its own short transaction at the boundary, before the read; a projection failure is reported as a projection failure, and slice (5) verifies the expiry behaviour |
| A projection bug silently changes what the UI shows | the read model becomes load-bearing (ADR-0011) | The before/after JSON comparison is a required check in slice (2); the crawl readers stay available as the ingest fallback |
| Existing data is lost or the DB is left locked by the migration | `EngineeringGraphSchema.cs:19-141`, `EngineeringGraphStoreTests.cs:24-101` | AC-009 with the existing failure-injection and backup tests extended to v6 |

## References

- `docs/adr/ADR-0011-engineering-graph-as-the-read-model.md`, `docs/adr/ADR-0012-graph-freshness-incremental-projection.md`
- `src/Agent/Workbench/DeviceSnapshot.cs`, `src/Agent/Workbench/EngineeringGraph/*`,
  `src/ApiHost/WorkbenchApiModels.cs`, `src/ApiHost/EngineeringGraphApi.cs`
- `src/Mcp.Engineering/Export/ExportMetadata.cs`, `src/Contracts/Engineering/XmlContentHash.cs`
- `docs/knowledge-workflow.md`, `docs/version-control-workflow.md`,
  `docs/agent-prompts/run-report-2026-10-03.md:493-517`
- This session's read-path inventory and the two live `engineering.db` inspections

## Update History

| Date | Version | Changes |
|---|---|---|
| 2026-10-04 | 1.0 | Initial design |
