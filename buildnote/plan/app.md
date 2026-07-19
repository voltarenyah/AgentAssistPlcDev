# App (WPF) — UI-0 shell+MCP host & UI-1 "Read Project Context" (Phase 2, step 7a)

Direction approved by user 2026-07-18: **UI-first read-only** — the read-only slices of `initialLaunch_20260717.md` §Phase 2.7 are pulled ahead of steps 4–6 (source-editor, version-control, agent), because the export→ingest chain needs nothing from them. This document covers **UI-0 + UI-1**; UI-2 (context browser) is the follow-up step; UI-3 (generate/review/apply comments) stays after step 6.

## 1. Purpose

A WPF shell that spawns the two existing MCP servers as child processes and exposes the read-only project-context chain behind one button: **Read Project Context** = `get_project_info` → `export_all_blocks` → `export_tag_tables` → `export_udts` → `ingest_source`, ending in a summary view (nodes/edges by kind, warnings, dbPath). The same orchestration is later triggered by the AI agent — two triggers, one workflow.

## 2. Locked decisions

1. **`src/App/`** — WPF, `net8.0-windows`, `WinExe`, `UseWPF=true`, nullable + implicit usings. Namespace `App`. Packages: `ModelContextProtocol.Core` 1.4.1 (client types live there in SDK 1.4.x — `McpClient`, `StdioClientTransport`), `CommunityToolkit.Mvvm` 8.4.x. ProjectReference → `Contracts`.
2. **`IngestResult` promoted to `Contracts`** (`Contracts/Knowledge/IngestResult.cs`) — mcp-knowledge.md §4 deferred this until a second consumer needed typed access; the App is that consumer. `KnowledgeTools.Ingest` returns it; JSON shape byte-identical (guarded by existing tests + e2e). get_block/get_network/search DTOs promote with UI-2.
3. **MCP host lives in App** (`App/Mcp/`) with an `IMcpToolCaller` seam; when step 6 (Agent) lands, `Mcp/` + `Workflows/` move to a shared net8 library. No premature shared project.
4. **`ReadProjectContextWorkflow` = the single orchestration** (UI button now, AI agent later). Connect/disconnect is UI session state, NOT part of the workflow; the workflow validates the connection via `get_project_info`.
5. **Export root:** `%LOCALAPPDATA%\PlcAiAssistant\exports\<projectName>\` (invalid path chars stripped; Local, not Roaming — exports are large). dbPath = default `<exportRoot>/plc-knowledge.db`.
6. **Server exe resolution:** `%APPDATA%\PlcAiAssistant\config.json` optional keys `engineeringServerPath` / `knowledgeServerPath` override; default = walk up from `AppContext.BaseDirectory` to `AgentAssistPlcDev.sln`, then `src/Mcp.Engineering/bin/<Debug|Release>/net48/Mcp.Engineering.exe` and `src/Mcp.Knowledge/bin/<Debug|Release>/net8.0/Mcp.Knowledge.exe` (`#if DEBUG`). Missing exe → explicit error; resolved paths logged at startup.
7. **All MCP traffic through `IMcpToolCaller`** — `Task<T> CallAsync<T>(string tool, object args, CancellationToken)`: first `TextContentBlock` JSON → `T`; `isError` ⇒ `ToolCallException(Code, Message, Remediation)`. Server stderr streams via `StdioClientTransportOptions.StandardErrorLines` into the UI log pane.
8. **No UI automation tests.** Tests cover workflow orchestration + settings resolution with a scripted fake caller.

## 3. Layout

```
src/App/
  App.csproj / App.xaml(.cs) / MainWindow.xaml(.cs)
  ViewModels/MainViewModel.cs
  Mcp/IMcpToolCaller.cs, ToolCallException.cs, McpServerConnection.cs, McpHost.cs
  Workflows/ReadProjectContextWorkflow.cs, ReadProjectContextResult.cs
  AppSettings.cs
tests/App.Tests/
  App.Tests.csproj (xUnit pins as Mcp.Knowledge.Tests; ProjectReference → App)
  FakeToolCaller.cs, ReadProjectContextWorkflowTests.cs, AppSettingsTests.cs
```

MainWindow: status strip (env check summary, server states), sessions group (list/attach/open-headless/disconnect), Read-Project-Context group (button + cancel, progress log, ingest summary + warnings + dbPath).

## 4. Workflow

```
RunAsync(ct)
 1. engineering.get_project_info            → projectName (error ⇒ abort)
 2. exportRoot = %LOCALAPPDATA%\PlcAiAssistant\exports\<sanitized projectName>
 3. engineering.export_all_blocks(exportRoot)
 4. engineering.export_tag_tables(exportRoot)   (0 is fine)
 5. engineering.export_udts(exportRoot)         (0 is fine)
 6. knowledge.ingest_source(exportRoot)         → IngestResult
 7. return ReadProjectContextResult { ProjectName, ExportRoot, DbPath, BlocksExported,
                                       TagTablesExported, UdtsExported, Ingest }
```

`IProgress<string>` log lines with per-step timings; CancellationToken checked between steps (a running export call finishes; cancel takes effect at the next boundary). Any `ToolCallException` aborts and surfaces code/message/remediation.

## 5. Tests

- Workflow: exact call order; exportRoot ends `exports\<ProjectName>`; counts mapped; `get_project_info` error aborts before export; export error aborts before ingest; cancel between steps ⇒ ingest never called.
- AppSettings: config override wins; default resolution finds both exes in a temp fake solution layout; missing sln ⇒ explicit error.
- Existing suites stay green (Contracts promotion is shape-preserving).

## 6. Exit criteria

- `dotnet build` + `dotnet test` green.
- App starts both servers, shows env check, attaches to a real TIA session.
- One button press runs export→ingest on a real project; summary displayed; DB under `%LOCALAPPDATA%\PlcAiAssistant\exports\<project>\plc-knowledge.db`.
- E2E recorded in `buildnote/log/`.

## 7. Non-goals

UI-2 context browser, chat panel, comment generation/apply, DeepSeek, source-editor, version-control, packaging, servers beyond the current two.

## 8. Risks

| Risk | Mitigation |
|---|---|
| CommunityToolkit.Mvvm restore fails | Hand-rolled `ViewModelBase`/`RelayCommand` fallback |
| Wrong exe config resolved (Debug/Release) | config.json override; resolved paths logged at startup |
| Long export on large projects | async + cancel between steps + per-step timing log |
| Contracts promotion breaks knowledge JSON | existing tests + `scripts/e2e-knowledge.json` as shape guard |
