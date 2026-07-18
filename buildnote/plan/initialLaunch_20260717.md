# PLC AI Assistant — Phased Build Plan (MCP architecture)

## Goal

Windows desktop app that assists PLC programming work, initially Siemens TIA Portal V17:

1. Read/write engineering projects via **TIA Openness** (V17 first, other versions later)
2. Translate program source (blocks/networks/tags) into a **SQLite** database
3. **DeepSeek cloud API** for program understanding, suggestions, and source modification
4. Test modified logic against **PLCSIM Advanced** (installed)
5. Version control for program source files (git)
6. One WPF UI combining all of the above

**MVP (Phase 1):** `mcp-engineering` complete — all TIA Openness operations (connect/disconnect, export/import, compile) accessible as MCP tools, verified standalone via MCP Inspector, ready for downstream consumers.

**Architecture direction (user-approved):** the app is decomposed into **independent MCP servers**, one per capability domain, so an agent (and any MCP-compatible client) can call each with minimal friction. Each MCP hosts **pluggable platform adapters** (TIA V17 today; Rockwell ControlLogix / other TIA versions later) behind a shared contract.

## Confirmed decisions

- **Language:** C# everywhere. WPF for the UI shell. DeepSeek cloud API (`https://api.deepseek.com`, OpenAI-compatible).
- **MCP SDK:** official C# SDK (`ModelContextProtocol` NuGet), **stdio transport** — the UI spawns each MCP server as a child process; servers are also usable standalone from any MCP client (Inspector, Claude Code, etc.).
- **Framework split (hard constraint):** Siemens API assemblies require .NET Framework 4.8 → `mcp-engineering` and `mcp-simulation` target **net48**; all other components target **net8** (better SDK/async support). `Contracts` is netstandard2.0 so both sides share it.
- **Location:** `C:\Users\Ansel\orca\projects\AgentAssistPlcDev` (project folder already created by the user; this plan is saved there as `buildnote/plan/initialLaunch_20260717.md`).
- **Environment:** TIA Portal V17 + PLCSIM Advanced already installed.

## Solution layout

```
C:\Users\Ansel\orca\projects\AgentAssistPlcDev\
├── AgentAssistPlcDev.sln
├── src/
│   ├── Contracts/           netstandard2.0 — shared DTOs (BlockInfo, NetworkModel,
│   │                              SourceDocument, TagValue) + platform capability interfaces
│   │                              (IEngineeringPlatform, ISimulationPlatform)
│   ├── Mcp.Engineering/     net48 — MCP server: engineering software interface
│   │                              Adapter: TiaV17Adapter (Openness). Future: TiaV18+, RockwellL5x
│   ├── Mcp.KnowledgeStore/  net8  — MCP server: SQLite generation & query
│   │                              (Microsoft.Data.Sqlite)
│   ├── Mcp.SourceEditor/    net8  — MCP server: source parse/edit/generate/diff/validate
│   │                              (TIA block XML first; L5X + SCL/ST generation later)
│   ├── Mcp.Simulation/      net48 — MCP server: simulation (Phase 5)
│   │                              Adapter: PlcSimAdvancedAdapter
│   ├── Mcp.VersionControl/  net8  — MCP server: git for source folders (LibGit2Sharp)
│   ├── Agent/               net8  — DeepSeek client + MCP client host + tool-routing
│   │                              agent loop + prompt templates + token/run logging
│   └── App/                 net8-windows WPF — UI shell (CommunityToolkit.Mvvm);
│                                  spawns/attaches MCP servers, hosts chat + review panes
└── tests/                         unit tests + sample TIA V17 XML fixtures
```

## MCP server tool surfaces (full planned surface; ships per phase)

Naming convention: tools use a plain verb_noun pattern (no per-server prefix). Each tool annotated (`readOnlyHint`/`destructiveHint`), structured JSON output, actionable error messages. Each server tested standalone with MCP Inspector before UI integration.

- **mcp-engineering** (Phase 1): `check_environment`, `list_sessions`, `connect`, `get_project_info`, `list_blocks`, `export_block`, `export_all_blocks`, `import_block` (destructive), `compile_block`, `compile_plc`, `save_project`, `disconnect`
- **mcp-knowledge-store** (Phase 2): `db_ingest_source`, `db_get_block`, `db_get_network`, `db_search`, `db_query` (read-only SQL), `db_schema`
- **mcp-source-editor** (Phase 2): `src_parse_block`, `src_insert_network_comment`, `src_diff`, `src_validate` (well-formedness + TIA schema sanity checks)
- **mcp-version-control** (Phase 2): `vc_init`, `vc_snapshot`, `vc_log`, `vc_diff`, `vc_restore` (destructive)
- **mcp-simulation** (Phase 5): `sim_list_instances`, `sim_create_instance`, `sim_power`, `sim_set_state`, `sim_read_tag`, `sim_write_tag`, `sim_run_assertion`

## Prerequisites (validated in Phase 0)

- Windows user is member of **"Siemens TIA Openness"** group; Openness DLLs at `C:\Program Files\Siemens\Automation\Portal V17\PublicAPI\V17\`
- PLCSIM Advanced runtime API present (path confirmed in Phase 5)
- DeepSeek API key (stored in `%APPDATA%/PlcAiAssistant/config.json`, git-ignored)

---

## Phase 0 — Scaffold & two de-risking spikes

- Create solution/projects as above; git init with `.gitignore` (config, `*.db`, exported source working dirs)
- **Spike A (MCP+net48):** minimal `mcp-engineering` skeleton — C# MCP SDK server on net48, referencing `Siemens.Engineering.dll`, answering one stdio tool call (`list_sessions`) from MCP Inspector. Proves the riskiest combination before anything is built on it.
- **Spike B (XML round-trip):** manually export one FB via Openness, hand-edit a network `<Comment>` node, re-import, confirm in TIA editor the comment appears and logic is unchanged. Locks the exact V17 XML schema (multilingual comment nodes) that `mcp-source-editor` targets.
- Env-check tool shared by UI and engineering MCP: user group, DLL paths, attach to running TIA process
- **Exit criteria:** both spikes pass on this machine; solution builds; Inspector can call the skeleton server

## Phase 1 — mcp-engineering complete

Deliver a fully functional TIA V17 engineering adapter behind the MCP protocol, verified end-to-end with MCP Inspector. This is the foundation everything else builds on — all downstream MCP servers, the agent, and the UI consume engineering as a dependency.

1. **TiaV17Adapter — full implementation:**
   - Connect modes: attach to running TIA instance (with UI), open project headless (`TiaPortalMode.WithoutUI`), open with visible UI
   - Enumerate PLC devices and block groups (OB/FB/FC/DB)
   - Export single block / all blocks / named selection to XML in a per-project working folder
   - Import modified XML with safety guards (block-not-open check, re-export interface verify)
   - Compile single block or full PLC software, returning structured error messages per block
   - Guard: refuse import into a block open-with-changes in the TIA editor

2. **Transport & lifetime:**
   - Stdio MCP server (child process model)
   - Graceful shutdown — release COM, dispose portal process on exit
   - Multiple concurrent server instances allowed (one per client)

3. **Diagnostics:**
   - `check_environment` — user group, DLL paths, TIA version, Openness registration
   - `get_project_info` — project name, PLC devices, block count, last modified

4. **Standalone validation (MCP Inspector):**
   - Full cycle: attach/open → list blocks → export → import → compile → disconnect
   - XML round-trip verify: export → modify comment → import → re-export → confirm logic byte-identical
   - Both headless and attached modes tested

- **Exit criteria:** all `mcp-engineering` tools pass end-to-end in MCP Inspector on a real test project; import safety guards reject invalid inputs with actionable messages; compile reports structured errors per block; server exits cleanly with no COM leaks.

**Phase 1 tool surface:**

- **mcp-engineering** (TIA V17 adapter): `check_environment`, `list_sessions`, `connect`, `get_project_info`, `list_blocks`, `export_block`, `export_all_blocks`, `import_block` (destructive), `compile_block`, `compile_plc`, `save_project`, `disconnect`

(Other MCP servers — knowledge-store, source-editor, version-control, simulation — are scoped to their respective phases below.)

## Phase 2 — AI network comments over the MCP chain

Build the remaining MCP servers needed for the AI comment-generation workflow, the agent that orchestrates them, and a basic WPF UI. Assumes Phase 1 mcp-engineering is complete and usable.

1. **mcp-knowledge-store (MVP schema):** `projects`, `blocks`, `networks`, `tags`, `llm_runs` (full prompt/response audit). Ingest exported XML → queryable rows.
2. **mcp-source-editor (MVP):** parse block XML into Block/Network model; insert/replace only the comment node (round-trip safe); `src_validate` before any import.
3. **mcp-version-control (MVP):** `vc_snapshot` of the working folder before every write-back.
4. **Agent:** DeepSeek client + MCP client host; comment-generation workflow: pull block context via `db_get_block`/`db_get_network` (interface tags, network instructions, neighboring titles) → DeepSeek returns strict JSON `{network_number, comment}` per network → map to `src_insert_network_comment` edits.
5. **WPF UI (MVP flow):** Connect to TIA → block tree → select blocks → "Generate comments" → review grid (network #, generated comment, editable, accept/reject) → "Apply" (triggers `vc_snapshot` → XML edit → `src_validate` → `eng_import_block` → re-export verify) → result log. Chat panel can be minimal/hidden in MVP.
6. **Dry-run mode:** produce the commented XML + diff on disk without importing.
- **Exit criteria:** on a real test project, generated comments are applied to an FB/FC and visible in TIA V17 editors, block logic byte-identical, pre-write snapshot exists in git, and every LLM call is auditable in `llm_runs`

## Phase 3 — Program understanding & Q&A

- Knowledge store deepened: instruction-level parsing (STL/LAD/FBD), PLC tag tables, data types, block-call cross-reference graph; incremental re-ingest on export change
- Agent Q&A workflow: natural-language question → SQL/cross-reference retrieval → grounded answer; suggestion mode per selected block/network
- UI: chat panel + cross-reference browser
- **Exit criteria:** chat answers verifiable against the DB; cross-reference queries correct on the test project

## Phase 4 — AI-assisted source modification & generation

- `src_generate_block` (new network/block from instruction, SCL/ST and LAD/FBD where expressible in XML), `src_replace_network`
- Agent modification workflow with mandatory `src_diff` preview in UI → approve → snapshot → validate → import → verify compiles in TIA
- Rollback via `vc_restore`; keep N previous XML versions per block
- **Exit criteria:** an AI-generated modification round-trips and compiles in TIA without errors

## Phase 5 — Simulation (PLCSIM Advanced)

- mcp-simulation with PlcSimAdvancedAdapter (`Siemens.Simatic.Simulation.Runtime.*`): instance lifecycle, power/RUN/STOP, tag I/O (I/Q/M/DB), cycle control
- Spike first: download path for a modified program to the virtual PLC from V17 (TIA download vs API) — version-specific, validated before building the runner
- Test runner: set inputs → run N cycles → assert outputs; agent proposes test cases for modified blocks
- **Exit criteria:** Phase-4-modified block runs in PLCSIM Advanced; scripted assertion passes from inside the app

## Phase 6 — Version control depth

- History/timeline UI, commit messages auto-generated from change summaries, in-app diffs between any two snapshots, branch per experiment
- **Exit criteria:** full iterated history of a block browseable/diffable in-app

## Phase 7 — Platform expansion & hardening

- Adapter contracts hardened from V17 experience; **Rockwell ControlLogix** support: engineering adapter via **L5X XML import/export** (no free Openness-equivalent — Studio 5000 exchange is file-based), source-editor L5X parser, simulation adapter evaluated (FactoryTalk Logix Echo/Emulate — flagged as research task); TIA V18+ adapter
- Packaging: installer + first-run wizard (env check, API key, MCP server health); DeepSeek rate-limit/backoff, token cost display, cancellation
- Optional: MCP eval suites per server (10-question read-only evals per mcp-builder guidance)

## Build order discipline

Phase 0 spikes first — nothing proceeds until both pass. Each phase ends with a working app meeting its exit criteria on a real test project.

## Out of scope (for now)

- HMI/WinCC objects, Safety (F) blocks, multi-user TIA projects, non-Siemens/Rockwell platforms, remote/networked MCP deployment (stdio-local only)
