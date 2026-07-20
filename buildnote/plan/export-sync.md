# export-sync — incremental context refresh (`sync_export`)

Status: implemented + verified live 2026-07-20. Tool: `sync_export` (mcp-engineering), tier `Read`.

## 0. Context

Refreshing the context data for an already-exported TIA project used to mean "re-export everything
and rebuild the SQLite knowledge base", because nothing told us which blocks changed. Openness XML
export is the expensive part (seconds per object); local re-ingest is cheap. This feature makes the
refresh incremental.

## 1. Locked decisions (validated live, see api-surface §10)

Three-tier change detection, cheapest first:

1. **Tier 0 — station checksum gate.** `PlcChecksumProvider.Software` per PLC is stored in
   `metadata.json` (`plcSoftwareChecksum`) at export/sync time. On sync, one read: equal →
   `status: "unchanged"`, no exports, no writes (~50 ms measured). Null (unsupported CPU / not
   compiled) or different → tier 1. Blind spot accepted: comment-only edits (text-list checksum is
   not exposed in V17).
2. **Tier 1 — per-object detection.**
   - Blocks/UDTs: **TIA fingerprints** (`FingerprintProvider.GetFingerprints()`) compared against
     the manifest record's `fingerprints` string. In-memory, no export; considers only user input,
     so compiles/saves/dependency ripples don't move it. Differs → export that object (`changed`).
   - Tag tables (no FingerprintProvider in V17): **timestamps nominate** (`ModifiedTimeStamp` vs
     record), and the **content hash confirms**: re-export, hash the normalized XML, compare —
     same → `touched`, differs → `changed`.
3. **Tier 2 — refresh.** Only genuinely changed/new components are exported; the agent then calls
   the existing `ingest_source` (local, fast) to rebuild the knowledge DB. **No mcp-knowledge
   changes**: `SqliteSemanticGraphStore.Save` rewrites all tables anyway, so per-component ingest
   would add node-ownership/dangling-edge complexity for negligible gain (deferred optimization).

## 2. Manifest (metadata.json) — additive, schemaVersion stays "1.0"

- Document: `plcSoftwareChecksum` (string|null).
- Record: `contentHash` (SHA-256 base64url of `XmlCompare.Normalize`'d XML — `<Created>` and CR
  stripped) and `fingerprints` (canonical `Id=Value;…`, sorted). The mcp-knowledge reader DTO
  ignores unknown fields (guarded by `ManifestWithSyncExportFieldsStillImports`).
- Legacy manifests (pre-feature): first sync backfills fingerprints without export when timestamps
  match (`touched: fingerprint-backfill`); tag tables re-export once to backfill `contentHash`.
- Deletes: record without a live object → XML deleted (path re-validated under the export root),
  record dropped. Renames = delete + add. Unreadable metadata (KHP) → conservative re-export, hash
  still decides.

## 3. Implementation map

- `Contracts/Engineering/SyncResult.cs` — per-PLC result (`unchanged|updated`, checksums,
  `added/changed/touched/removed/failed` with reasons). `IEngineeringPlatform.SyncExport`.
- `Mcp.Engineering/Export/`: `SyncPlanner` (pure diff — unit-test seam), `FingerprintReader`
  (guarded; **enumerator-only materialization** — the Openness `IList<Fingerprint>` throws
  `NotSupportedException("Collection is read-only")` from `ICollection<T>.CopyTo`, which LINQ
  buffering hits), `ContentHasher`, `StableId`; `ExportManifest` (+`TryRead`, `WriteAll` checksum
  param, `CreateRecord` hashes/fingerprints on every export path).
- `TiaV17Adapter.SyncExport` — per-PLC: gate → enumerate (blocks/tags/UDTs, fingerprints +
  timestamps) → plan → re-export nominated → `WriteAll` with fresh checksum. No-baseline manifest →
  full export for that PLC. `export_all_blocks` also stamps the checksum.
- Tool `sync_export(outputDir, plcName?)`; classified `Read` in `SandboxPolicy.Defaults`.

## 4. Verification (2026-07-20, live TestPLCExportDemo session)

- `scripts/e2e-sync.json`: no-baseline full export (7 objects) → second sync `unchanged` in ~56 ms
  (gate) → legacy migration on the real folder (backfills + tag-table change detected via
  timestamp+hash).
- Fingerprint fix verified: forced diff → 6 `fingerprint-backfill` touched, 0 exports; next forced
  diff → all skip, 0 exports.
- Unit: 17 tests in new `tests/Mcp.Engineering.Tests` (net48) — full planner matrix + hasher
  normalization; whole suite green (192 tests).

## 5. Out of scope / follow-ups

- Per-component incremental ingest into SQLite (see §1 tier 2 rationale).
- Wiring `sync_export` into the App "Read Project Context" workflow (today: agent calls the tool).
- Comment-only changes: invisible to the station checksum (gate) but **covered by the `Comments`
  fingerprint** whenever a diff runs; the gate only skips when *nothing* changed.
