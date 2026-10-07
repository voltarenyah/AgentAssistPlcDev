# Device knowledge workflow

Each device owns `<device>\plc-knowledge.db`; databases are not shared across devices
or worktrees and are ignored by Git.

A full rebuild reads the tracked `exported-source` manifest as the authoritative
component list, substitutes matching files from sparse `modified-source`, includes
validated overlay-only components, and writes only that device database.

Editing an overlay marks the device knowledge state stale. Do not update after every
individual edit. Once a related batch is finished, call `update_components` once with
the changed relative paths before reusing the database. Component provenance enables
transactional replacement of the old component graph while retaining graph data
still owned or referenced by other components. Baseline refreshes require a full
rebuild; successful updates persist applied hashes and clear stale state.

## Freshness before reuse

The database is derived, so it can be behind the source in ways the persisted flags do not
record: an in-app edit sets `knowledge.Stale`, and accepting TIA source also sets
`knowledge.BaselineStale`, but an edit made outside the application sets no flag at all
(ADR-0012). The applied hashes are therefore the authority for freshness, and only the
coordinator can compare them.

`WorkbenchCoordinator.ReadKnowledgeStatus` reports it as `DeviceKnowledgeStatus`:
`missing` (no database file), `stale` (flagged, or a source component changed, was added,
or is gone), or `current`. `requiresRebuild` distinguishes the states a partial update
cannot repair — a missing database, a stale baseline, a component the database holds no
provenance for (added), and a component whose source file is gone (removed) — from a plain
content change, which `UpdateKnowledgeAsync` replaces incrementally.

Both surfaces expose it:

- HTTP: `GET /api/workbenches/{workbenchId}/worktrees/{worktreeId}/devices/{device}/knowledge/status`
  (and the `/api/devices/{device}/knowledge/status` alias). Read-only.
- Device chat: the read-tier `knowledge_status` tool reports the same status, and the
  write-tier `refresh_knowledge` tool repairs it. `refresh_knowledge` routes to
  `RebuildKnowledgeAsync` when `requiresRebuild` is set and to `UpdateKnowledgeAsync`
  otherwise, so applied hashes, staleness flags and projected device facts move together.

The device chat's runtime context carries the persisted state on every turn. Before it
answers anything about a program change — and whenever that state is stale or missing — the
agent calls `knowledge_status`, then `refresh_knowledge` when the state is not current, and
answers from the refreshed database. It must not call `ingest_source` or `update_components`
for the conversation's device: those write the graph without recording the applied hashes,
which is what keeps the device reporting stale knowledge to the app and to later turns.

