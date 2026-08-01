# Packaging investigation — Windows installer

Status: investigation only, no packaging technology chosen yet (zero hits for
WiX/Inno/MSIX/Squirrel/Electron/Tauri; no publish profiles, `RuntimeIdentifier`,
`global.json`, `Directory.Build.props`, or NuGet.config anywhere in the repo).
Gathered 2026-08-01 to support the Phase 7 "installer + first-run wizard" work
(`initialLaunch_20260717.md:236`).

## 1. What ships

A multi-process desktop app, not a single exe:

| Component | TFM | Role |
|---|---|---|
| `src/ApiHost` | net8.0 (Web SDK) | Main process; hosts the agent loop **in-process** (`src/Agent` is a library, not a process) |
| `src/Mcp.Engineering` | **net48** exe | TIA Openness MCP server (stdio child) |
| `src/Mcp.Knowledge` | net8.0 exe | Knowledge graph MCP server (stdio child) |
| `src/Mcp.SourceEditor` | net8.0 exe | Source editor MCP server (stdio child) |
| `src/Mcp.VersionControl` | net8.0 exe | Git MCP server (stdio child) |
| `studio/` | React 19 + Vite 8 | UI; today only served by the Vite **dev server** |

- MCP servers are spawned by ApiHost as stdio child processes
  (`src/Agent/Mcp/McpServerConnection.cs:53-60`), stdout = JSON-RPC, stderr → logs.
- Native binary payloads that must survive publish/single-file/trimming:
  LibGit2Sharp 0.27 win-x64 libgit2 (`Mcp.VersionControl.csproj:16-19`),
  SQLitePCLRaw e_sqlite3 (via Microsoft.Data.Sqlite in `Agent` and `Mcp.Knowledge`).

## 2. Hard constraints (installer can detect, not provide)

- **Dual runtime**: .NET Framework 4.8 (Openness assemblies load only on .NET
  Framework, agent.md) **and** .NET 8. net48 cannot be self-contained; net8 exes can.
- `Mcp.Engineering.exe.config` with `useLegacyV2RuntimeActivationPolicy="true"` is
  required for the mixed-mode (C++/CLI) Openness assemblies and **must ship**
  (`mcp-engineering.md` §10.2).
- **TIA Portal V17 only, hardcoded everywhere**: registry
  `HKLM\SOFTWARE\Siemens\Automation\Openness\17.0\...`
  (`src/Mcp.Engineering/Openness/OpennessAssemblyResolver.cs:14-17`,
  `WhitelistRegistrar.cs:23-24`), compile-time HintPath
  `C:\Program Files\Siemens\Automation\Portal V17\PublicAPI\V17\Siemens.Engineering.dll`.
  A V18+ story is Phase 7 scope.
- TIA itself, Openness COM registration (done by TIA setup; fix = TIA repair), and the
  user's membership in the local **"Siemens TIA Openness"** group are prerequisites.
  `check_environment` (`src/Mcp.Engineering/.../EnvironmentChecker.cs:14`) already
  validates all of this — reuse it for a first-run wizard.
- **Openness whitelist** (`scripts/register-whitelist.ps1`): writes under HKLM →
  needs **elevation**; entry is keyed on exe **path + SHA-256 hash** → every install
  location and every app update invalidates it. The installer/updater must re-run
  whitelist registration on install and on every update.
- **git on PATH** required by `Mcp.VersionControl`
  (`src/Mcp.VersionControl/Git/RepositoryService.cs:848` runs plain `"git"`) —
  bundle portable git or make it a prerequisite.
- DeepSeek API key is stored in **plaintext** at
  `%APPDATA%\AutomationWorkbench\config.json` (DPAPI hardening is a release candidate).

## 3. Code that breaks in an installed layout (rework before packaging)

1. `src/ApiHost/McpExecutableResolver.cs:29-39` — resolves MCP exes by walking up
   from `AppContext.BaseDirectory` to `AgentAssistPlcDev.sln` and building
   `src/<proj>/bin/{Debug|Release}/<tfm>/<proj>.exe` paths. **Throws when the .sln
   is absent.** Needs exe-relative defaults (e.g. `mcp/<name>.exe` next to ApiHost);
   config overrides `Mcp:Engineering` / `Mcp:Knowledge` / `Mcp:VersionControl` /
   `Mcp:SourceEditor` already exist as fallback.
2. `#if DEBUG` selects the build folder at compile time
   (`McpExecutableResolver.cs:10-14`) — installed builds are always Release.
3. **No static hosting for `studio/dist`**: no `UseStaticFiles`/`MapFallbackToFile`
   in `src/ApiHost/Program.cs`. The frontend uses relative `/api`
   (`studio/src/api/client.ts:1`), so serving `dist` from ApiHost is the simplest
   production model and eliminates the Vite dev server entirely.
4. **Port 5239 exists only in `launchSettings.json:17`** — a published `ApiHost.exe`
   run directly falls back to port 5000. Needs explicit `UseUrls`/Kestrel config;
   the 5173 vite proxy (`studio/vite.config.ts:13-21`) becomes irrelevant once
   ApiHost serves the SPA.
5. The committed `src/Mcp.Engineering/register-whitelist.reg` has a dev path
   (`C:\Users\Ansel\...`) baked in — must not ship. Whitelist generation is a
   Debug-only post-build target (`Mcp.Engineering.csproj:51-53`); Release builds
   generate nothing today.
6. `launch.ps1` (kill-by-name, `dotnet run`, `npx vite`) is dev-only; release needs
   a real launcher/shortcut story.
7. Cleanup candidates for release: `Mcp:StartExternal`/`Testing` env switches,
   legacy `%APPDATA%\PlcAiAssistant` config path, CORS `AllowAnyOrigin`
   (`Program.cs:104`) once UI and API share an origin.

## 4. User data (installer/uninstaller must never touch)

- Config: `%APPDATA%\AutomationWorkbench\` (current), `%APPDATA%\PlcAiAssistant\` (legacy)
- Workbenches, git repos, SQLite knowledge DBs, audit logs:
  `%LOCALAPPDATA%\AutomationWorkbench\Project\<name>` and legacy
  `%LOCALAPPDATA%\PlcAiAssistant\exports` — agent.md explicitly forbids migrating
  legacy exports. Uninstall should leave all user data in place.
- Sandbox trusted-roots file is passed to MCP children via env var
  `AUTOMATION_WORKBENCH_TRUSTED_ROOTS_FILE` — all exes must stay launchable with
  the same inherited environment.

## 5. Open decisions this points to

- **Installer tech**: needs elevation + HKLM registry writes + prerequisite checks
  (net48, TIA V17, git, group membership). WiX or Inno Setup fit naturally; MSIX is
  awkward — the container complicates HKLM whitelist writes and spawning net48
  children outside the package.
- **net8 deployment mode**: self-contained single-file (no .NET 8 prerequisite,
  larger payload, verify native assets survive) vs framework-dependent (smaller,
  adds a runtime prerequisite check).
- **App shell**: plain `ApiHost.exe` serving the SPA + auto-open browser (no shell
  needed, matches current browser model) vs WebView2/Electron wrapper.
- **Updater story**: exe hash changes break the TIA whitelist, so update and
  whitelist re-registration must be owned by the same mechanism.
