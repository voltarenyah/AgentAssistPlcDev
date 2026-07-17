# AgentAssistPlcDev — Agent Guide

## Project Overview

Windows desktop app that assists PLC programming work, starting with Siemens TIA Portal V17. The app is decomposed into independent MCP servers (one per capability domain) so any MCP-compatible client can call each server individually. Each MCP hosts pluggable platform adapters behind a shared contract.

**MVP (Phase 1):** AI-generated network comments — understand project context, generate per-network comments via DeepSeek, user reviews, comments written back into the TIA project.

## Tech Stack

| Layer | Technology | Target Framework |
|---|---|---|
| UI | WPF | `net8-windows` |
| AI | DeepSeek cloud API (OpenAI-compatible) | — |
| MCP SDK | `ModelContextProtocol` NuGet, stdio transport | varies |
| Database | SQLite (`Microsoft.Data.Sqlite`) | `net8` |
| Version control | LibGit2Sharp | `net8` |

## Framework Split (Hard Constraint)

Siemens API assemblies require .NET Framework 4.8:

| Target | Projects | Framework |
|---|---|---|
| Engineering + Simulation | `PlcAi.Mcp.Engineering`, `PlcAi.Mcp.Simulation` | **net48** |
| Everything else | All other src projects | **net8** |
| Shared contracts | `PlcAi.Contracts` | **netstandard2.0** |

- **Never** add net48 dependency to a net8 project or vice versa.
- Always route Siemens-specific APIs through the contract interfaces (`IEngineeringPlatform`, `ISimulationPlatform`).

## Solution Layout

```
AgentAssistPlcDev.sln
├── src/
│   ├── PlcAi.Contracts/           netstandard2.0
│   ├── PlcAi.Mcp.Engineering/     net48 — TIA Openness adapter
│   ├── PlcAi.Mcp.KnowledgeStore/  net8  — SQLite generation & query
│   ├── PlcAi.Mcp.SourceEditor/    net8  — block XML parse/edit/generate
│   ├── PlcAi.Mcp.Simulation/      net48 — PLCSIM Advanced adapter (Phase 4)
│   ├── PlcAi.Mcp.VersionControl/  net8  — git operations
│   ├── PlcAi.Agent/               net8  — DeepSeek client + MCP host
│   └── PlcAi.App/                 net8-windows — WPF UI shell
└── tests/
```

## Build Commands

- **Build solution:** `dotnet build AgentAssistPlcDev.sln`
- **Build specific project:** `dotnet build src/PlcAi.Mcp.Engineering/`
- **Run tests:** `dotnet test`
- **Restore packages:** `dotnet restore`

## Key Architecture Rules

1. **MCP naming convention:** `<domain>_<action>[_<noun>]`, e.g. `eng_list_sessions`, `db_ingest_source`. Annotate tools with `readOnlyHint` or `destructiveHint`. Return structured JSON. Test each MCP server standalone with MCP Inspector before UI integration.
2. **Openness dependency:** TIA Portal V17 DLLs at `C:\Program Files\Siemens\Automation\Portal V17\PublicAPI\V17\`. Windows user must be in "Siemens TIA Openness" group.
3. **DeepSeek config:** API key stored in `%APPDATA%/PlcAiAssistant/config.json` (git-ignored). Endpoint: `https://api.deepseek.com` (OpenAI-compatible).
4. **Platform expansion:** Adapter contracts should be written generically from the start. Rockwell ControlLogix (L5X XML) is the planned second platform.
5. **Safety:** Never import a block into TIA without a `vc_snapshot` first. Always `src_validate` before `eng_import_block`. Dry-run mode must produce diff-on-disk without importing.

## Git Workflow

- **Current branch:** `master`
- **Main branch:** `main`
- **Co-author commits** with Claude when applicable: `Co-Authored-By: Claude <noreply@anthropic.com>`
- Commit messages should reference the phase and component.

## Key Files

- `PLAN.md` — full phased build plan with exit criteria (source of truth for architecture decisions)
- `agent.md` — this file; concise rules and context for AI agents
- `%APPDATA%/PlcAiAssistant/config.json` — local config (git-ignored)

## MCP Server Inventory (MVP)

| Server | Tool Prefix | Key Tools |
|---|---|---|
| Engineering | `eng_` | `list_sessions`, `connect`, `list_blocks`, `export_block`, `import_block` (destructive), `get_block_status`, `disconnect` |
| Knowledge Store | `db_` | `ingest_source`, `get_block`, `get_network`, `search`, `query` (read-only SQL), `schema` |
| Source Editor | `src_` | `parse_block`, `insert_network_comment`, `diff`, `validate` |
| Version Control | `vc_` | `init`, `snapshot`, `log`, `diff`, `restore` (destructive) |
| Simulation | `sim_` | (Phase 4) instance lifecycle, tag I/O, cycle control |

## Phase Sequence

| Phase | What | Exit Criteria |
|---|---|---|
| 0 | Scaffold + 2 spikes (MCP+net48, XML round-trip) | Both spikes pass; solution builds; Inspector calls skeleton server |
| 1 | AI network comments end-to-end | Comments visible in TIA; block logic unchanged; git snapshot; LLM audit trail |
| 2 | Program understanding & Q&A | Chat answers verifiable against DB |
| 3 | AI-assisted modification & generation | AI-modified block round-trips and compiles |
| 4 | PLCSIM simulation | Modified block runs in simulation; assertion passes |
| 5 | Version control depth | Full history browsable/diffable in-app |
| 6 | Platform expansion (Rockwell) + hardening | Installer, multi-platform adapters |

## Notes for AI Agents

- When planning changes, check `PLAN.md` for architectural context first.
- Do not add frameworks or patterns outside the approved tech stack without explicit user approval.
- Always consider the Framework Split constraint — net48 vs net8 vs netstandard2.0.
- MCP servers use **stdio transport** only (no HTTP/networking in MVP).
- The user's working directory is `C:\Users\Ansel\orca\projects\AgentAssistPlcDev`.
