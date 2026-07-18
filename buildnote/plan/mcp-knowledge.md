# mcp-knowledge — SQLite knowledge base built from mcp-engineering exports (Phase 2, step 1)

## 0. Context

Phase 2 of `initialLaunch_20260717.md` bundled four MCP servers + agent + UI into one phase — too aggressive. It is now split into small steps. **This document covers step 1 only**: a new MCP server that turns the XML files exported by `mcp-engineering` into a queryable SQLite database.

**Rename (user decision, 2026-07-18):** `mcp-knowledge-store` → **`mcp-knowledge`** ("store" carries no meaning). Project: `src/Mcp.Knowledge/`. Doc references in `initialLaunch_20260717.md` and `agent.md` are updated when implementation starts.

**Extraction method reference:** the PlcSourceExporter project (`C:\Users\Ansel\Documents\Siemens TIA Add-in Dev\PlcSourceExporter`) already solves exactly this problem — see its agent guide (`2026-07-18-195403-...txt`, kept next to that project's root). This plan ports its proven pipeline instead of inventing a new one.

## 1. Purpose

`mcp-knowledge` ingests a folder of TIA Openness XML exports (the `outputDir` filled by `export_block` / `export_all_blocks` of mcp-engineering) and builds a **property-graph SQLite database**: blocks, networks, calls, symbol reads/writes, data blocks and their members. Downstream consumers (agent, UI) query it for program understanding; the AI-comment workflow (later step) pulls per-network context from it.

Nothing else is in scope: no LLM audit tables, no source editing, no git, no UI.

## 2. Locked decisions

1. **Adopt the PlcSourceExporter graph schema verbatim** (4 tables: `graph_nodes`, `graph_node_properties`, `graph_edges`, `graph_edge_properties` + indices). This supersedes the relational MVP sketch (`projects`/`blocks`/`networks`/`tags`) in `initialLaunch_20260717.md` §Phase 2 — the parser being ported writes exactly this schema, it is proven on real V17 exports, and IDs are deterministic. `llm_runs` stays deferred to the agent step.
2. **Port code, don't reference.** `PlcSourceExporter.Core` is netstandard2.0 with only `Microsoft.Data.Sqlite` as a dependency, so its parser/persistence code ports to net8 almost verbatim. We **copy and adapt** the needed files into `src/Mcp.Knowledge/` (provenance note in each file header) rather than adding a cross-repo project reference — this solution stays self-contained.
3. ~~**Crawl by root element, not `metadata.json`.**~~ **SUPERSEDED 2026-07-18 (stage 3):** mcp-engineering now writes a PlcSourceExporter-compatible `metadata.json` (schema "1.0") into its export roots, so the importer is **manifest-first**: when `<exportRoot>/metadata.json` exists, components are dispatched from the manifest (the previously unported `ImportExportRoot`/`LoadExportedComponents`/`IsProgramBlockCategory` were ported in stage 3); the root-element crawl remains as the fallback for manifest-less folders (legacy exports). Manifest mode adds reconciliation warnings: Exported-but-missing file, and on-disk `*.xml` not referenced by the manifest (catches legacy flat files and `spike/` copies). Malformed manifest → `MANIFEST_INVALID` error (loud over silent fallback).
4. **Rebuild-only ingest.** Delete-all + bulk insert in one transaction, same as the reference. Incremental re-ingest remains a Phase-3 item (`initialLaunch` §Phase 3).
5. **Scope = what engineering exports today.** mcp-engineering Phase 1 exports blocks only (OB/FB/FC/DB, including instance DBs). Tag-table and UDT import paths are **not** ported now; they land in the same step that adds tag/UDT export to mcp-engineering (§13). The dispatch is designed so those categories slot in without schema changes. **(2026-07-18, stage 4: the knowledge-side import of `Tags`/`UDT` categories and `SW.Tags.PlcTagTable`/`SW.Types.PlcStruct` root elements is now ported and wired into both ingest paths; engineering-side export lands in parallel.)**
6. **`logicStatements` (SCL-like network text) deferred.** It comes from `ProgramBlockLogicYamlWriter.cs` (2 097 lines, the largest file). Network nodes already carry title + reads/writes/calls without it; it ports in the step that builds `get_block`/`get_network` for the comment workflow.
7. **Tool surface this step = 3 tools** (`ingest_source`, `query`, `get_schema`), following the shipped mcp-engineering convention of plain verb_noun names with no server prefix.

## 3. Input contract with mcp-engineering

Facts verified against the Phase 1 implementation (`src/Mcp.Engineering/Adapter/TiaV17Adapter.cs`) and real output (`exported/TestPLCExportDemo/`):

- Export folder is **flat**: `{BlockName} [{TypeCode}{Number}].xml`, e.g. `Main [OB1].xml`.
- Multi-PLC projects: one **subfolder per PLC** (`Path.Combine(outputDir, plc.Name)`), only when more than one PLC exists.
- No manifest file; files may be accompanied by unrelated working subfolders (e.g. `spike/`).

Ingest rules:

| Rule | Behaviour |
|---|---|
| Crawl | `*.xml` recursively under `exportRoot` |
| Classify | By XML **root element** (content, not filename): `SW.Blocks.OB/FB/FC` → program block; `SW.Blocks.GlobalDB/InstanceDB/ArrayDB/DB` → data block; `SW.Types.PlcStruct` → UDT (deferred → skipped with note); `SW.Tags.PlcTagTable` → tag table (deferred → skipped with note); anything else → warning, skipped |
| Duplicates | Same block identity (name+type from content) appearing in several files (e.g. `spike/reexport/` copies) → keep the file **closest to export root** (shallowest relative path, ties alphabetical), report skipped duplicates in `warnings[]` |
| Per-file failure | Malformed XML or unsupported schema → warning entry naming the file, continue with the rest. Abort only if **zero** files import successfully |
| Project node | `project:{exportRoot folder name}`, `CONTAINS` edges to every top-level object (matches reference behaviour) |

**Amended 2026-07-18 (stage 3, manifest-first):** the export root layout is now `<exportRoot>/metadata.json` + `Blocks/*.xml` + `DB/*.xml`, manifest = PlcSourceExporter schema "1.0" (`components[]` with `name, sourcePath, category, status, exportedFile`, …). When the manifest exists it is authoritative: components with `status == "Exported"` and a non-empty `exportedFile` are imported ordered by `sourcePath`; `OB/FB/FC` → program-block import, `DB` → data-block import, `UDT`/`Tags` → deferred warning. The root-element crawl table above applies only to the manifest-less fallback (and to stage-1/2 fixtures). Reconciliation in manifest mode: Exported-but-missing file → warning; on-disk `*.xml` not referenced by any manifest entry → "not in manifest, ignored" warning. `exportedFile` may use `/` or `\`. `IngestResult` gained `source: "manifest" | "crawl"`.

## 4. Architecture

```
MCP Client (Inspector / Agent / App)
       │  stdio JSON-RPC
       ▼
┌─────────────────────────────────────┐
│  Mcp.Knowledge.exe                  │  net8 console app
│  ModelContextProtocol 1.4.1         │  same pin as Mcp.Engineering
│       │                             │
│  KnowledgeTools (MCP tools)         │  ingest_source / query / get_schema
│       │                             │
│  Importer ──► Graph ──► Store       │  ported from PlcSourceExporter.Core
│  (folder crawl)  (model)  (SQLite)  │  Microsoft.Data.Sqlite 8.0.x
└─────────────────────────────────────┘
```

- **net8** per the framework split (no Siemens dependency at all — pure XML + SQLite).
- **Stateless per call**: each tool call opens the SQLite connection, does its work, closes. No session state, no `connect`/`disconnect` tools.
- **No `Contracts` dependency this step**: result DTOs live inside `Mcp.Knowledge`. Promote to `Contracts` when a second consumer (UI/agent) needs typed access.
- Logging to **stderr** only (stdout is the JSON-RPC channel), same as engineering.
- The reference project embeds `e_sqlite3.dll` as a resource because it is a net48 add-in; on net8 the `SQLitePCLRaw.bundle_e_sqlite3` transitive package handles native loading — the `NativeSqliteRuntime` extraction code is **not** ported.

## 5. Database schema (ported verbatim)

```sql
graph_nodes(id TEXT PK, kind TEXT, name TEXT)
graph_node_properties(node_id TEXT, name TEXT, value TEXT, PK(node_id, name))
graph_edges(id TEXT PK, from_node_id TEXT, to_node_id TEXT, type TEXT)
graph_edge_properties(edge_id TEXT, name TEXT, value TEXT, PK(edge_id, name))
-- + indices on kind, name, type, from_node_id, to_node_id
```

Vocabulary produced by this step (subset of the reference; ~~UDT/tag kinds arrive later~~ **updated 2026-07-18, stage 4:** UDT/tag kinds now produced):

- **Node kinds:** `Project`, `OB`, `FB`, `FC`, `Network`, `Instruction` (calls), `Variable` (symbols), `Global DB`, `Instance DB`, `DB Member`, `Data Type`, `UDT`, `UDT Member`, `PLC Tag`, `IO Address`
- **Edge types:** `CONTAINS`, `CALLS`, `READS`, `WRITES`, `HAS_TYPE`, `INSTANCE_OF`, `CONNECTED_TO`, `EXECUTES_BEFORE`, `EXECUTES_AFTER`
- **ID patterns:** `block:{Name}`, `network:{Block}:{Index}`, `instruction:{Block}:{N}:call:{Seq}`, `symbol:{Name}`, `db:{Name}`, `db-member:{Db}:{Path}`, `type:{Name}`, `udt:{Name}`, `udt-member:{Udt}:{Path}`, `tag:{Table}:{Name}:{Address}`, `io:{Address}` — deterministic: the same export always yields the same IDs.
- Reference behaviours kept: placeholder `block:{B}` node (`declaredByReference: true`) when a callee wasn't exported; symbol dedup by name; execution-order edges between consecutive networks; UDT import flattens **first-level members only** (reference behaviour — nested struct members are not expanded); tag tables get no project `CONTAINS` edge.

`get_schema` returns the DDL plus this vocabulary (port of the reference `AGENT_SQLITE_GUIDE.md` essence) so agents can self-serve.

## 6. Tool surface

| Tool | Hint | Input | Output | Notes |
|---|---|---|---|---|
| `ingest_source` | write | `{ exportRoot: string, dbPath?: string }` | `IngestResult` | Crawl → classify → parse → rebuild DB. Default `dbPath` = `<exportRoot>/plc-knowledge.db` |
| `query` | read | `{ dbPath: string, sql: string, maxRows?: int }` | `{ columns[], rows[][], truncated: bool }` | Read-only SQL. Connection opened with `Mode=ReadOnly`; single statement, must start with `SELECT`/`WITH`/`EXPLAIN`; `maxRows` default 200, hard cap 1000, `truncated` flag set when cut |
| `get_schema` | read | — | `{ ddl: string, nodeKinds[], edgeTypes[], exampleQueries[] }` | Static content; no DB needed |

`IngestResult`:

```jsonc
{
  "dbPath": "C:\\...\\exported\\TestPLCExportDemo\\plc-knowledge.db",
  "filesFound": 4, "filesImported": 2,
  "nodes": 41, "edges": 55,
  "byKind": { "OB": 1, "FC": 1, "Network": 7, "Variable": 9 },
  "warnings": [ "skipped duplicate: spike/reexport/FC_LAD_SimulateCylinder_Call [FC1].xml" ],
  "durationMs": 320
}
```

Deferred to later steps: `get_block`, `get_network`, `search` (arrive with `logicStatements` and the comment workflow); UDT/tag import (arrives with engineering-side export).

## 7. Ingest pipeline

```
ingest_source(exportRoot, dbPath?)
 1. Validate exportRoot exists → error EXPORT_ROOT_NOT_FOUND otherwise
 2. (2026-07-18) if <exportRoot>/metadata.json exists → manifest mode (§3 amendment):
    parse manifest (malformed → MANIFEST_INVALID), filter status=="Exported" + exportedFile,
    order by sourcePath, dispatch per category, reconciliation warnings
 3. else → crawl mode: crawl *.xml recursively (ordinal, sorted by relative path),
    classify each file by root element (§3); collect skips/duplicates as warnings
 4. Zero importable files → error NO_SOURCE_FILES (list what was found)
 5. Parse per category:
    - program block → ProgramSemanticReferenceBuilder.Parse() → block + network
      nodes, EXECUTES_* chains, call instructions, CALLS/READS/WRITES edges
    - data block   → XDocument walk of Interface sections → DB + member nodes,
      HAS_TYPE edges, INSTANCE_OF edge for instance DBs
 6. Save: open dbPath (create dir), delete-all + bulk insert in ONE transaction
 7. Return IngestResult with counts + warnings + source ("manifest" | "crawl")
```

Error convention mirrors engineering: tool failures are normal tool results with `isError: true` and a structured `{ code, message, remediation }` payload. Codes: `EXPORT_ROOT_NOT_FOUND`, `NO_SOURCE_FILES`, `MANIFEST_INVALID` (2026-07-18: metadata.json present but not valid/readable JSON), `DB_LOCKED` (SQLite busy — likely another process holds the file), `QUERY_REJECTED` (non-read-only SQL), `DB_NOT_FOUND` (query against missing file).

## 8. Code reuse map (port sources)

All under `C:\Users\Ansel\Documents\Siemens TIA Add-in Dev\PlcSourceExporter\src\PlcSourceExporter.Core\`:

| Source file | What ports | Destination in `src/Mcp.Knowledge/` | Adaptations |
|---|---|---|---|
| `SemanticPlcGraph.cs` (1 548 ln) | Graph model, `SqliteSemanticGraphStore`, schema DDL, agent-guide text | `Graph/` | Replace `metadata.json` crawl with folder crawl + root-element classify (§3); drop `NativeSqliteRuntime`; drop UDT/tag import methods for now |
| `ProgramSemanticReference.cs` (1 012 ln) | `ProgramSemanticReferenceBuilder.Parse()` — compile units, wires, accesses, calls | `Parsing/` | As-is |
| — (new) | Root-element classifier + duplicate resolution | `Import/ExportFolderCrawler.cs` | New code, small |

Explicitly **not** ported now: `ExportMetadata.cs`, `ProgramBlockComponentCatalog.cs` (manifest crawl — N/A), `ProgramBlockLogicYamlWriter.cs` (step 3), `TagTable.cs` / `UdtTypeTable.cs` (step 2), `PlcExportService.cs` (export orchestration — that's mcp-engineering's job), AddIn/TestHarness projects.

Each ported file gets a header comment: origin path + "adapted for mcp-knowledge; keep changes minimal to ease future re-syncs".

## 9. Project & solution changes

```
src/Mcp.Knowledge/
  Mcp.Knowledge.csproj     net8, OutputType Exe, ModelContextProtocol 1.4.1,
                           Microsoft.Extensions.Hosting (aligned version),
                           Microsoft.Data.Sqlite 8.0.x
  Program.cs               mirrors Mcp.Engineering/Program.cs (host + stdio + stderr logging)
  Tools/KnowledgeTools.cs
  Graph/  Parsing/  Import/
tests/Mcp.Knowledge.Tests/
  Mcp.Knowledge.Tests.csproj  net8, xUnit (same pins as Contracts.Tests)
  Fixtures/*.xml           committed, trimmed real V17 exports: one OB, one FC
                           with a call, one GlobalDB, one InstanceDB
```

- Add both projects to `AgentAssistPlcDev.sln`.
- `.gitignore` already covers `*.db` and `exported/` — no change needed. Test fixtures live under `tests/` so they are committed (the real `exported/` folder is git-ignored and must not be a test dependency).
- Docs on implementation start: rename mentions in `initialLaunch_20260717.md` + `agent.md` (inventory table row "Knowledge Store" → "Knowledge"), and add this file to `agent.md` Key Files.

## 10. Testing & exit criteria

**Unit tests** (`tests/Mcp.Knowledge.Tests`):

- Parser: fixture OB/FC → expected network count, titles, reads/writes/calls; fixture DBs → member tree, `INSTANCE_OF`.
- Store round-trip: import fixtures → save to temp DB → load → identical graph.
- Determinism: same fixtures ingested twice → identical node/edge IDs.
- Crawler: duplicate block in a subfolder is skipped with warning (mirrors the real `spike/reexport/` situation); unknown root element skipped.
- `query` guard: non-SELECT statements rejected.

**E2E** (real data): add `scripts/e2e-knowledge.json` for the existing `scripts/mcp-e2e.mjs` runner: `ingest_source` on `exported/TestPLCExportDemo` → `query` node/edge counts → spot-check one network's READS. Then a manual MCP Inspector pass.

**Exit criteria:**

- `dotnet build` + `dotnet test` green.
- Ingest of `exported/TestPLCExportDemo` (2 real blocks: `Main [OB1]`, `FC_LAD_SimulateCylinder_Call [FC1]`) succeeds: exactly 2 block nodes; every `<SW.Blocks.CompileUnit>` in the source XMLs has a `Network` node; symbol Variable nodes exist for the FC's accesses; the FC's call produces `CALLS` + `Instruction` nodes and a placeholder callee if absent; duplicate copies under `spike/` are reported in `warnings[]`, not double-ingested.
- Re-ingest of the same folder yields identical IDs (verified via `query`).
- MCP Inspector walkthrough of all 3 tools passes.

## 11. Risks & mitigations

| Risk | Impact | Mitigation |
|---|---|---|
| Port drift vs the reference implementation | Subtle parse regressions | Port verbatim where possible; provenance headers; port the reference's relevant unit-test cases into `tests/Mcp.Knowledge.Tests` |
| Engineering export layout changes (e.g. adds subfolders) | Ingest misses/duplicates files | Classification is content-based, not path-based; duplicate rule is deterministic |
| Concurrent ingest from two server instances | SQLite lock errors | Single-writer design documented; `DB_LOCKED` error with remediation; not a scenario for MVP (one agent) |
| V17 XML schema variants (ArrayDB etc.) | Files skipped silently | Explicit `warnings[]` per skipped file with reason; widen classifier as real files appear |

## 12. Non-goals (this step)

- UDT / tag-table import, incremental re-ingest, `logicStatements` text, `get_block`/`get_network`/`search`, `llm_runs`, source editing, version control, agent, UI.
- No changes to mcp-engineering (its export output is already sufficient input).

## 13. Remaining Phase 2 steps (the split, for roadmap context)

1. **mcp-knowledge ingest** ← this document
2. Engineering exports tag tables + UDTs; knowledge imports them (`TagTable.cs` / `UdtTypeTable.cs` port) — **knowledge side DONE 2026-07-18 (stage 4)**; engineering-side `export_tag_tables`/`export_udts` in parallel at that date
3. `logicStatements` port + knowledge query helpers (`get_block`, `get_network`, `search`)
4. mcp-source-editor MVP (parse/comment-edit/validate)
5. mcp-version-control MVP (`vc_snapshot` before write-back)
6. Agent (DeepSeek) + comment-generation workflow
7. WPF UI MVP (block tree → generate → review → apply)

## 14. Build order (within this step)

1. Scaffold `Mcp.Knowledge` (net8 + MCP SDK) answering `get_schema` only; Inspector smoke test
2. Port graph model + schema + store; unit-test store round-trip
3. Port `ProgramSemanticReferenceBuilder`; unit-test against fixtures
4. Importer: crawler + classify + block/DB import; unit-test with fixtures
5. `ingest_source` tool end-to-end; `query` tool + read-only guard
6. E2E via `scripts/mcp-e2e.mjs` on the real export folder; MCP Inspector walkthrough; record results in `buildnote/log/`
