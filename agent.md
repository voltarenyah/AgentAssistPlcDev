# AgentAssistPlcDev — Agent Guide

## Project Overview

Windows desktop app that assists PLC programming work, starting with Siemens TIA Portal V17. The app is decomposed into independent MCP servers (one per capability domain) so any MCP-compatible client can call each server individually. Each MCP hosts pluggable platform adapters behind a shared contract.

**Status:** Phase 1 (mcp-engineering) complete 2026-07-18. Phase 2 in steps: mcp-knowledge (ingest + tags/UDTs + knowledge depth) done 2026-07-18 (`buildnote/plan/mcp-knowledge.md`); WPF App step 7a — read-only shell + "Read Project Context" — in progress (`buildnote/plan/app.md`).

## Tech Stack


| Layer           | Technology                                    | Target Framework |
| --------------- | --------------------------------------------- | ---------------- |
| UI              | WPF                                           | `net8-windows`   |
| AI              | DeepSeek cloud API (OpenAI-compatible)        | —                |
| MCP SDK         | `ModelContextProtocol` NuGet, stdio transport | varies           |
| Database        | SQLite (`Microsoft.Data.Sqlite`)              | `net8`           |
| Version control | LibGit2Sharp                                  | `net8`           |


## Framework Split (Hard Constraint)

Siemens API assemblies require .NET Framework 4.8:

| Target                   | Projects                               | Framework          |
| ------------------------ | -------------------------------------- | ------------------ |
| Engineering + Simulation | `Mcp.Engineering`, `Mcp.Simulation`    | **net48**          |
| Everything else          | All other src projects                 | **net8**           |
| Shared contracts         | `Contracts`                            | **netstandard2.0** |

- **Never** add net48 dependency to a net8 project or vice versa.
- Always route Siemens-specific APIs through the contract interfaces (`IEngineeringPlatform`, `ISimulationPlatform`).

## Solution Layout

```
AgentAssistPlcDev.sln
├── src/
│   ├── Contracts/                 netstandard2.0 — shared DTOs + platform interfaces
│   ├── Mcp.Engineering/           net48 — TIA Openness adapter
│   ├── Mcp.Knowledge/             net8  — SQLite knowledge graph from exported XML
│   ├── Mcp.SourceEditor/          net8  — block XML parse/edit/generate
│   ├── Mcp.Simulation/            net48 — PLCSIM Advanced adapter (Phase 5)
│   ├── Mcp.VersionControl/        net8  — git operations
│   ├── Agent/                     net8  — DeepSeek client + MCP host
│   └── App/                       net8-windows — WPF UI shell
└── tests/
```

## Build Commands

- **Build solution:** `dotnet build AgentAssistPlcDev.sln`
- **Build specific project:** `dotnet build src/Mcp.Engineering/`
- **Run tests:** `dotnet test`
- **Restore packages:** `dotnet restore`

## Key Architecture Rules

1. **No useless prefixes on project names.** Names are scoped by what a thing IS within the solution, not by what the solution is called. Every project already belongs to this solution — repeating the app name as a prefix (`PlcAi.Contracts`, `PlcAi.Agent`) adds nothing. `Mcp.Engineering` is good (tells you it's an MCP server + domain). `PlcAi.Mcp.Engineering` is not. If removing a prefix would make the name ambiguous, keep it; otherwise cut it.

2. **MCP naming convention:** plain `verb_noun` (no per-server prefix), e.g. `list_sessions`, `ingest_source`. Annotate tools with `readOnlyHint` or `destructiveHint`. Return structured JSON. Test each MCP server standalone with MCP Inspector before UI integration.

3. **Openness dependency:** TIA Portal V17 DLLs at `C:\Program Files\Siemens\Automation\Portal V17\PublicAPI\V17\`. Windows user must be in "Siemens TIA Openness" group.

4. **DeepSeek config:** API key stored in `%APPDATA%/PlcAiAssistant/config.json` (git-ignored). Endpoint: `https://api.deepseek.com` (OpenAI-compatible).

5. **Platform expansion:** Adapter contracts should be written generically from the start. Rockwell ControlLogix (L5X XML) is the planned second platform.

6. **Safety:** Never import a block into TIA without a `vc_snapshot` first. Always `src_validate` before `import_block`. Dry-run mode must produce diff-on-disk without importing.

## Git Workflow

- **Current branch:** `master`
- **Main branch:** `main`
- **Co-author commits** with Claude when applicable: `Co-Authored-By: Claude <noreply@anthropic.com>`
- Commit messages should reference the phase and component.

## Key Files

- `buildnote/plan/initialLaunch_20260717.md` — full phased build plan with exit criteria (source of truth for architecture decisions)
- `buildnote/plan/mcp-engineering.md` — Phase 0–1 detailed design for the engineering MCP server (complete 2026-07-18)
- `buildnote/plan/mcp-knowledge.md` — Phase 2 step 1 detailed design for the knowledge MCP server
- `buildnote/plan/app.md` — Phase 2 step 7a design for the WPF App (read-only shell + Read Project Context)
- `agent.md` — this file; concise rules and context for AI agents
- `%APPDATA%/PlcAiAssistant/config.json` — local config (git-ignored)

## MCP Server Inventory

| Server | Phase | Key Tools |
| ------ | ----- | --------- |
| Engineering | 1 (done) | `check_environment`, `list_sessions`, `connect`, `disconnect`, `save_project`, `get_project_info`, `list_blocks`, `export_block`, `export_all_blocks`, `import_block` (destructive), `compile_block`, `compile_plc` |
| Knowledge | 2, step 1 + depth | `ingest_source`, `query` (read-only SQL), `get_schema`, `get_block`, `get_network`, `search` |
| Source Editor | 2, step 4 | `parse_block`, `insert_network_comment`, `diff`, `validate` |
| Version Control | 2, step 5 | `init`, `snapshot`, `log`, `diff`, `restore` (destructive) |
| Simulation | 5 | instance lifecycle, tag I/O, cycle control |

## Phase Sequence


| Phase | What                                            | Exit Criteria                                                                 |
| ----- | ----------------------------------------------- | ----------------------------------------------------------------------------- |
| 0     | Scaffold + 2 spikes (MCP+net48, XML round-trip) | DONE 2026-07-17 — both spikes passed; solution builds; Inspector calls skeleton server |
| 1     | mcp-engineering complete                        | DONE 2026-07-18 — full tool surface verified E2E (headless + attached)        |
| 2     | AI network comments over the MCP chain — split into steps: knowledge ingest → tag/UDT export+import → knowledge depth → source-editor → version-control → agent → WPF UI | Comments visible in TIA; block logic unchanged; git snapshot; LLM audit trail |
| 3     | Program understanding &amp; Q&amp;A             | Chat answers verifiable against DB                                            |
| 4     | AI-assisted modification &amp; generation       | AI-modified block round-trips and compiles                                    |
| 5     | PLCSIM simulation                               | Modified block runs in simulation; assertion passes                           |
| 6     | Version control depth                           | Full history browsable/diffable in-app                                        |
| 7     | Platform expansion (Rockwell) + hardening       | Installer, multi-platform adapters                                            |


## Notes for AI Agents

- When planning changes, check `buildnote/plan/initialLaunch_20260717.md` for architectural context first.
- Do not add frameworks or patterns outside the approved tech stack without explicit user approval.
- Always consider the Framework Split constraint — net48 vs net8 vs netstandard2.0.
- MCP servers use **stdio transport** only (no HTTP/networking in MVP).
- The user's working directory is `C:\Users\Ansel\orca\projects\AgentAssistPlcDev`.
- Use english as default language, expect user ask for different language or ask to translate.

