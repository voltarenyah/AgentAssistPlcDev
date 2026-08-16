# export-sync — incremental context refresh (`sync_export`)

Status: implemented + verified live 2026-07-20. Tool: `sync_export` (mcp-engineering), tier `Read`.

> **Workbench integration (2026-07-27):** `sync_export` now targets the selected
> device's ignored `staging` directory. Its historical direct-export workflow below is
> retained as tool background; tracked `exported-source` changes require a fresh
> preview, user confirmation, reconciliation, and automatic Git commit.

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
   - Blocks/UDTs: **TIA fingerprints** (`FingerprintProvider.GetFingerprints()`) **and modified
     timestamps** — either one nominates. Fingerprints consider only user input, so compiles/saves
     don't move them; but both signals can lag the edit until TIA propagates it (verified
     2026-07-21), hence the dual check. The **content hash of the re-exported XML is always the
     verdict**: fingerprint mismatch → changed directly; timestamp-only mismatch → export + hash,
     changed or touched (compile ripples land here).
   - **Instance DBs**: no reliable signal — TIA regenerates them system-side when the parent FB
     changes, and in the propagation window neither fingerprints nor timestamps move. Re-exported
     on every diff for a hash verdict (few and small in practice).
   - Tag tables (no FingerprintProvider in V17): timestamps nominate, content hash confirms.
3. **Tier 2 — refresh.** Only genuinely changed/new components are exported; the agent then calls
   the existing `ingest_source` (local, fast) to rebuild the knowledge DB. **No mcp-knowledge
   changes**: `SqliteSemanticGraphStore.Save` rewrites all tables anyway, so per-component ingest
   would add node-ownership/dangling-edge complexity for negligible gain (deferred optimization).

## 2. Manifest (metadata.json) — additive, schemaVersion stays "1.0"

- Document: `plcSoftwareChecksum` (string|null).
- Document (per-device, 2026-07-31): additive `device` object — project/device identity captured
  from Openness at export time for UI display without a live session: `plcName`, `deviceName`
  (station), `typeIdentifier` (CPU order number + firmware), `projectName`, `projectAuthor`,
  `projectComment`, `projectVersion`, `projectCopyright`, `projectCreationTime`,
  `projectLastModified`, `projectLastModifiedBy`. Written by every manifest-writing export path
  (`export_block`, `export_all_blocks`, `export_tag_tables`/`export_udts`, `sync_export`);
  WriteAll/Upsert preserve the stored section when a write has no fresh capture. Not part of the
  reference add-in schema — additive-only, tolerated by the mcp-knowledge reader.
- Record: `contentHash` (SHA-256 base64url of `XmlCompare.Normalize`'d XML — `<Created>` and CR
  stripped) and `fingerprints` (a JSON object keyed by the canonical TIA fingerprint id, such as
  `Code`, `Comments`, `Events`, `Interface`, and `Properties`). Readers accept the legacy sorted
  `Id=Value;…` string and normalize it when rewriting metadata. The mcp-knowledge reader DTO
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

## 5. UI integration: check → confirm → sync (2026-07-21)

Hard rule: **attaching/opening a project never regenerates context data.** Three distinct steps:

1. **Check** — new read-only tool `get_context_status(outputDir, plcName?)` (tier Read): per PLC
   the stored manifest checksum vs the live software checksum + `State` ∈ `no-baseline` /
   `in-sync` / `changed` / `unknown` (live checksum unavailable or legacy manifest). No exports,
   no writes — the App runs it automatically after every attach and after every sync.
   The manifest preserves the previous stored checksum when a sync reads null (uncompiled
   program), so the check compares against the last compiled state instead of degrading to
   "unknown" until the next sync (locked 2026-07-21 after live validation).
2. **Confirm** — the "Project context" panel shows the state + both checksums + the export root
   (wrong-project guard); the **Sync Context** button (formerly "Read Project Context") asks for
   explicit confirmation (MessageBox) before anything is written.
3. **Sync** — `ReadProjectContextWorkflow` now calls `sync_export` (full export when no baseline)
   instead of the three export tools, and runs `ingest_source` only when content changed or the
   knowledge db is missing (all-unchanged + db present → sub-second no-op). `SyncResult` carries
   `BaselineExisted` for the zero-content guard; `ReadProjectContextResult` exposes `Sync`,
   `Ingest` (null when skipped), `UpToDate`.

**Compare tab** (`compare_context` tool, tier Read; App tab after Warnings): per-component
read-only diff — name/category/state (`same`/`different`/`new`/`missing`/`unverifiable` for
instance DBs/`unknown`), per-fingerprint component matches with stored/live hashes available for
detail views, modified dates, and project checksums. Runs the same capture + planner as sync but
executes nothing; on demand and after each sync, never automatic on attach (full-PLC enumeration
cost).

**Bug fix (2026-07-21, user-reported "only 1 changed"):** a failed re-export during sync used to
replace the last-known-good manifest record with a Failed stub (losing `exportedFile`,
`contentHash`, `fingerprints`) — the later recovery then misreported the component as `added`
instead of `changed`. Sync now keeps the last-known-good record on failure and reports the item
in `failed` only. The same investigation revealed the signal propagation lag (api-surface §10):
nomination widened to fingerprints OR timestamps with the content hash as verdict, plus the
instance-DB hash-verify rule — a GlobalDB start-value edit and an FB static-area edit (incl. the
instance DB) are now all detected (verified live: 3 changed, compile ripple as touched).

## 6. Out of scope / follow-ups

- Per-component incremental ingest into SQLite (see §1 tier 2 rationale).
- Detailed raw hashes in the normal confirmation view (the dialog shows per-component state and
  keeps stored/live hashes in hover details).
- Comment-only changes: invisible to the station checksum (gate) but **covered by the `Comments`
  fingerprint** whenever a diff runs; the gate only skips when *nothing* changed.
