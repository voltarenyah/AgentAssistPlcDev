# PLC AI Assistant

An AI-assisted workbench for industrial PLC engineering, starting with **Siemens TIA
Portal V17**. It brings program understanding, safe source editing, version control,
and an AI agent into one local desktop workflow — without ever giving the AI direct,
unchecked access to your PLC project.

## What it does

Working with a large PLC project means reading hundreds of blocks, networks, and tag
tables before you can change anything safely. PLC AI Assistant turns that project into
a navigable, queryable, versioned source tree that an AI agent can reason about — and
that you can browse, edit, and roll back like ordinary source code.

- **Read the project without opening TIA** — blocks, networks, tags, and UDTs are
  exported once and persisted, so browsing, searching, editing, and AI queries work
  fully offline.
- **Ask questions in natural language** — a grounded AI agent answers against the
  actual program content via a knowledge graph, not guesswork.
- **Edit with guardrails** — every write to the PLC is previewed, explicitly approved,
  validated, and snapshotted first. Nothing touches the live project silently.
- **Keep full history** — every device baseline lives in Git, so each refresh and each
  edit is a reviewable commit, with diff, restore, and branching built in.

## Key concepts

### Workbench projects

All work happens inside a named **workbench** — a folder containing one shared Git
repository and one or more worktrees. Each PLC device gets its own tracked source
baseline, a sparse overlay of your edits, and its own knowledge database. Multiple
worktrees let you experiment on branches in parallel and merge results back.

### Offline-first, explicit synchronization

The stored baseline and the live PLC are only ever reconciled through explicit,
non-destructive actions: **compare** exports the live PLC to a temporary staging area
and shows you a diff; **approve** applies the changes you select; **import & compile**
sends only your selected edits to TIA. Closing TIA or restarting the app never loses
your baseline, edits, history, or knowledge data.

### Knowledge graph

Exported sources are ingested into a per-device SQLite graph: blocks, networks, tags,
cross-references, and translated logic statements. The agent and the UI answer
questions from this graph, and the UI reports its freshness (`missing` / `stale` /
`current`) so you always know whether the AI is looking at up-to-date content.

### MCP-based architecture

Every capability is an independent [Model Context Protocol](https://modelcontextprotocol.io)
server, so the built-in agent — or any MCP-compatible client — can use them:

| Server | Purpose |
|---|---|
| Engineering | TIA Portal connection, export/import, compile (TIA Openness) |
| Knowledge | Source ingest and graph queries |
| Source editor | Protected parse, preview, apply, diff, validate |
| Version control | Git status, commit, diff, snapshot, restore, branches |

### Safety by design

Tools are tiered by risk, file access is jailed to registered workbench roots,
destructive operations require confirmation, and every action is recorded in an audit
log. Tool arguments and model output cannot grant themselves new filesystem access.

## Application layout

- `studio/` — React + Vite workbench UI
- `src/ApiHost/` — ASP.NET Core API hosting the UI and bridging chat/logs
- `src/Agent/` — AI agent loop (DeepSeek) with sandboxed tool routing
- `agent-service/` — optional LangGraph Workbench App Assistant sidecar; it guides
  project/worktree actions while the existing PLC AgentLoop remains independent
- `src/Mcp.*` — the MCP servers described above
- `src/Contracts/` — shared contracts and sandbox policy

## Requirements

- Windows with Siemens TIA Portal V17 and the Openness API (user must be in the
  "Siemens TIA Openness" group)
- .NET Framework 4.8 and .NET 8 SDKs
- Node.js for the studio UI
- A DeepSeek API key for the AI agent
- Python 3.13 and the `agent-service` dependencies for the development launcher

The development launcher starts the Workbench App Assistant on every run. Install
the `agent-service` dependencies into `agent-service\.venv` before running
`launch.ps1`; the launcher passes the shared `DEEPSEEK_API_KEY`, waits for the
sidecar health check on port 8787, and stores checkpoint/feedback data under the
user-local Automation Workbench data folder. Each normal development launch
starts a fresh App Assistant session by clearing old LangGraph checkpoints;
`-NoKill` preserves the existing assistant session. The packaged desktop shell remains
opt-in through `AUTOMATION_WORKBENCH_APP_ASSISTANT_ENABLED`.

## Status

Active development. Engineering, knowledge, version-control, and chat/agent slices are
implemented and tested; the safe generate → review → apply edit workflow and live-TIA
acceptance for the source editor are in progress. See `buildnote/plan/` for the phased
build plan and current milestone status.
