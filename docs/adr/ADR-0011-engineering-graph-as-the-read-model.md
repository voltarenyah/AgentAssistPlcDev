# ADR-0011 The engineering graph is the read model for device and source facts

## Status

Accepted

## Context

**In plain terms.** When you open a device page, the application currently walks the whole exported
PLC source folder and reads every block file, on every visit. That takes about 1.2 seconds on a
1097-object device (and about 12 seconds the first time), and the worktree overview page repeats it
once per device. Meanwhile the workbench already has a database — the engineering graph — that holds
the identity of these objects but none of their information.

Studio reads engineering facts through ApiHost. Some of those reads parse the exported PLC source tree
(`Blocks/*.xml`, `DB/*.xml`) or the export manifest (`metadata.json`) **on every request**:

| Read | Cost (measured against the running ApiHost, workbench `8003ec87…`, device `83a5be5f…`, 2 runs) |
|---|---|
| `GET /api/workbenches/{id}/worktrees/{wt}/devices/{dev}` (`DeviceSnapshotReader.Read` → `ReadBlocks`) | 1213 ms warm, 11959 ms first call, 1.19 MB |
| `GET …/devices/{dev}/blocks` (same crawl, no front-end caller) | 1017 ms |
| `GET /api/project/info` (compat, no front-end caller) | 1229 ms |
| `GET …/devices/{dev}/source-objects` (manifest path, falls back to the crawl when the manifest is empty) | 20 ms |
| `GET …/worktrees/{wt}/engineering-tasks` (engineering graph) | 6-14 ms |

Provenance: these numbers were measured in this session with `Invoke-WebRequest` + `Stopwatch` against
the running ApiHost, and the same order of magnitude is recorded in the repository at
`docs/agent-prompts/run-report-2026-10-03.md:529-530` ("about 1.9 s") for a manifest holding 1,314
components. No measurement artifact is committed; the Design Doc carries the before/after check that
makes them falsifiable.

`DeviceSnapshotReader.Read` (`src/Agent/Workbench/DeviceSnapshot.cs:78-105`) calls `ReadBlocks`
unconditionally (`:81`); that method walks the whole source root and `XDocument`-parses every block file
(`:340-446`). The same file already documents the cheaper rule — "manifest objects, or the block crawl
when the manifest is missing or legacy" (`:133-153`) — and the `source-objects` route follows it.

The cost is multiplied by the front end: `WorktreeLandingPage.tsx:156-158` and
`TaskCommitsSection.tsx:87-88` call `getDeviceInfo` **once per device** (`Promise.all(devices.map(…))`),
so a worktree with N devices pays N crawls.

Meanwhile the engineering graph (`<workbench>/.automation/engineering.db`, schema v5) stores
**identity and relationships only**: `graph_entities` has `entity_kind, entity_id, workbench_id,
worktree_id, device_id, external_ref` and no payload at all. A `source_object` row therefore knows its
`{deviceId}:{manifestId}` id and its relative path, and nothing else — no name, category, number,
language, content hash, fingerprint or status. A live workbench's database holds 774 `source_object`
rows (plus 1 `git_commit`), another holds 1,314 `source_object` rows and **zero** `git_commit` rows.

Two defects already follow from that gap: the source panel queries the graph with a bare manifest id
while the server requires `{deviceId}:{manifestId}` (`PlcSourcePanel.tsx:115` vs
`WorkbenchApiModels.cs:2186-2190`), so traceability silently reads "no links"; and merge paths write no
commit entity (`MergeWorktreeAsync`, `WorkbenchCoordinator.cs:2873-2876` calls only `vc_merge`), so
commit→source-object reads return 404.

The user's decision, in their words: expand the engineering graph's coverage; store directly in the
graph what is currently obtained by reading source files or indirect files such as `metadata.json`;
solve the field mismatch with a compatible shape — a node table plus a node-property table recording
each node's attached information; and move the information currently managed by `metadata.json` fully
into the graph.

## Decision Point

- **Question**: where does the front end's engineering information come from, and how are those facts
  stored?
- **Why a decision exists**: four credible, materially different options are supported by the
  repository (below), and the choice changes a responsibility boundary every future read must respect.
- **Scope boundary**: the confirmed user requirement above, and the existing API contract — the routes
  and response shapes the Studio consumes stay as they are.

## Decision

The engineering graph becomes the read model for engineering facts. Reads serve from the graph; parsing
exported XML or `metadata.json` happens **only in ingest**, never in a read.

### Decision Details

| Item | Content |
|------|---------|
| **Decision** | Facts live in the engineering graph as nodes (`graph_entities`) plus a node-property table holding each node's attached information; read routes serve those rows instead of parsing files. |
| **Why this** | It is the user's requirement, and it gives every consumer one identity and one freshness rule while reusing the store, the schema-version ladder and the existing graph API. A cheaper option (A2 below) removes the measured cost but leaves the facts on disk, so it cannot satisfy the requirement. |
| **Known unknowns** | The property table's query cost for a device with ~1,100 objects (~14k property rows), and whether the store's per-scope connection model needs a read-path change once every device read opens a scope. Both are measured by the Design Doc's verification tasks. |
| **Reconsider when** | A read that must stay in the graph becomes slower than parsing the file it replaced, or a fact appears that cannot be modelled as a node plus properties. |

## Rationale

### Options Considered

| Option | Requirement and repository fit | Current-scope benefit | Lifecycle cost | Maintainability | Material trade-offs |
|---|---|---|---|---|---|
| **A. Node + node-property tables in the engineering graph** | Matches the user's stated direction; reuses `EngineeringGraphStore`, the v1-v5 migration ladder and `EngineeringGraphApi` | Removes the 1.2 s crawl from every device read; one identity, one freshness rule | New schema version, ingest path, property query surface, projection triggers at 8 coordinator write points, commit population across every write path, worktree-deletion cleanup, and a repair path that is a **second writer** into the same SQLite file | One store, one migration mechanism, one API surface | A property table is weaker to query than typed columns, so list reads need a designed query; it does not technically dominate option B — it is selected because the requirement says facts live in the graph |
| B. Typed fact tables in the engineering graph | Same reuse, stronger per-fact queries and indexes | Same latency win; simpler list queries | New schema version plus one table per fact kind, and a table per future fact kind | More code to keep aligned as facts grow | Every new fact kind needs a migration; the "attached information" idea does not generalise |
| C. A separate read-model store | Clean separation from the task/evidence graph | Same latency win without touching the graph schema | A second database per workbench, a second migration mechanism, and a second identity space to keep aligned | Two stores to keep consistent | The graph already exists and already holds the identities; a second store duplicates its key space |
| D. Stop crawling on the read path (manifest only) | Smallest possible change: `Read` stops calling `ReadBlocks` when the manifest is present | Removes the measured 1.2 s with no schema, migration, projection or stamp | None beyond a code change and a test | One code path changed | Leaves the facts on disk, so it does not satisfy the requirement that the graph holds them, and the identity/freshness defects remain |

**Selected**: Option A, the user's direction. Option D is materially cheaper for the measured symptom
and is therefore recorded here explicitly rather than omitted; it is rejected only because it does not
meet the stated requirement. Option B is admissible and was the closest runner-up; the user asked for
the node + property shape.

## Consequences

### Positive Consequences

- One fast path for engineering reads; the measured 1213 ms snapshot becomes a graph read on the order
  of the existing 6-14 ms graph routes plus serialising the same 1.19 MB body.
- One identity convention and one freshness rule for every consumer, which is what the two verified
  defects need to stop recurring.
- `metadata.json` keeps its role as the export's own record, but the workbench no longer depends on
  parsing it per request.

### Negative Consequences

- The graph becomes load-bearing for the UI: if a projection is missing or stale, the page shows
  nothing instead of falling back to a file read. **In plain terms**: keeping the copy complete becomes
  a correctness requirement, not an optional improvement.
- **A concrete data-loss hazard the schema must be designed around**: every connection sets
  `ForeignKeys = true` (`EngineeringGraphStore.cs:23`) and node registration uses `INSERT OR REPLACE`
  (`EngineeringGraphService.cs:207`, `:225`), which runs on every stage click with the whole manifest
  (`WorkbenchApiModels.cs:961-963`) and on every anchor read (`:2210`). In SQLite, `INSERT OR REPLACE`
  deletes the replaced row, so a property table carrying an `ON DELETE CASCADE` foreign key to
  `graph_entities` would have its properties **silently deleted** by the next registration. The
  property table's key and the registration path must therefore be designed together (no cascading
  delete from the entity row, or registration changed to an upsert that does not replace the row).
- A node-property table answers "give me every fact of these ~1,100 nodes" differently from typed
  columns, so the list reads need an explicit query and index design.
- The projection adds a second writer into a per-workbench SQLite file that already has task, stage and
  evidence writers, under a rollback journal with a 5 s `busy_timeout`; the write path must stay short
  and the expiry behaviour must be defined.
- Every fact that moves in needs a schema version and a migration.

### Neutral Consequences

- The API routes and response shapes stay as they are; the identity fix is resolved **server-side**, so
  the Studio client needs no change in this pass (a user-decided boundary, 2026-10-04: fix the backend
  and the graph first).
- **A user-decided boundary the design must keep**: the knowledge database (`plc-knowledge.db`) is a
  different concern and is never merged here. In the user's words: knowledge manages the PLC semantic
  information inside one device; engineering manages the global engineering-file information and does
  not concern itself with PLC logic. Its schema, hashes and staleness rules stay untouched.

## Architecture Impact

- `src/Agent/Workbench/EngineeringGraph/*` gains a schema version (v6) with the property table and an
  ingest service. The store's journal mode is **not** set by the code: `EngineeringGraphStore.cs:82`
  sets only `busy_timeout`, and `:70-77` records that the journal mode is deliberately left at the
  SQLite default (rollback journal) with WAL rejected, so a failed migration leaves the file
  byte-identical.
- `src/Agent/Workbench/DeviceSnapshot.cs` changes role: its file and manifest readers become the
  **ingest** implementation instead of the read implementation.
- `src/ApiHost/WorkbenchApiModels.cs` device routes (`:1739-1760`) read the graph; the response shapes
  are preserved.
- The workbench coordinator's existing write points become the projection triggers.

## Implementation Guidance

- Enforce one source-object identity at the API boundary: `{deviceId}:{manifestId}`, with the bare id
  resolved server-side against the selected device and a clear error when it cannot be resolved — the
  current silent 404 must not survive.
- Project the facts the UI actually consumes: device identity and export metadata, knowledge state and
  its timestamp, the source-object list with name/category/number/language/group path/hash/status/
  fingerprints/evidence kind, the block subset, counts, and ingest diagnostics.
- State the two counts explicitly and keep them apart: the device page shows the block-crawl count
  (`DeviceSnapshot.cs:102` passes `blocks.Count`, i.e. only the `Blocks/`+`DB/` subset), while the task
  picker returns the comparable manifest objects (instance DBs excluded). They are different numbers
  today and must stay different.
- Keep the block crawl as the ingest fallback for a missing or legacy manifest, not as a read path — and
  record that the crawl **cannot** classify an instance DB (`CommittedSourceManifest.EvidenceKindOf`
  needs `siemensTypeName`, which the crawl passes as `null`, and `BlockTypeOf` maps
  `SW.Blocks.InstanceDB` to the generic `DB`), so the fallback must mark such objects unclassified
  rather than as standard blocks.
- Delete facts when their source disappears, and delete the edges that reference a removed node: the
  graph has no foreign keys on `graph_edges`, so `RemoveEntity` (which removes both directions) is the
  path to use.
- Verify by comparing the full response body before and after, not by timing a `SELECT`: the 1.19 MB
  payload dominates the budget, so only a body comparison proves the values are preserved.

## Related Information

- Measurements and read inventory: this session's read-path inventory (route → handler → data source →
  measured cost) and the live `engineering.db` inspections; `docs/agent-prompts/run-report-2026-10-03.md:526`
  (a manifest with 1,314 components), `:493-517` (the "database is locked" history and its fix).
- `docs/design/engineering-graph-read-model-design.md` — fact model, ingest points, migration.
- `src/Agent/Workbench/EngineeringGraph/EngineeringGraphSchema.cs` (v5 ladder),
  `EngineeringGraphStore.cs`, `EngineeringGraphService.cs`.
- Review record: the ADR batch review (2026-10-04) verified the claims above and corrected four of
  them: the store never sets `journal_mode`; `s.Refresh(id)` writes no graph fact; the crawl fallback
  cannot classify instance DBs; and the source-list size reaches 1,314 components, not 1,097.
