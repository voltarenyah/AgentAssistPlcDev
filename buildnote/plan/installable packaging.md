# Installable Packaging Execution Plan

## Purpose

Reshape AgentAssistPlcDev into a release-friendly Windows application and validate that it can be built, installed, upgraded, repaired, and uninstalled reliably.

The immediate target is an **internal packaging prototype** based on the current .NET 8 codebase.

Production hardening and the .NET 10 migration are separate tracks.

---

# Locked design decisions

## Application architecture

* Product name: `Automation Workbench`
* Main executable remains: `ApiHost.exe`
* Installer technology: Inno Setup 6
* Installer scope: per-machine
* Architecture: Windows x64
* Install directory: `C:\Program Files\Automation Workbench`
* Frontend: Vite production assets served by `ApiHost`
* API binding: `http://127.0.0.1:5239`
* MCP processes: executable-relative child processes
* Modern .NET processes: self-contained .NET 8 folder deployments
* Engineering MCP: .NET Framework 4.8
* Upgrade model: complete installer replacement
* Siemens whitelist: dedicated elevated registration helper
* User data: preserved during repair, upgrade, and uninstall

## Deferred from the packaging prototype

Do not include these in Track A:

* .NET 10 retargeting
* executable rename from `ApiHost.exe`
* single-instance mutex
* DPAPI or Credential Manager migration
* automatic update checking
* Authenticode enforcement
* embedded WebView2, Electron, or Tauri
* polished first-run wizard
* MSI, MSIX, ClickOnce, or WiX
* delta patching
* background updater service
* bundled Git
* TIA V18+ support

## Release gate

The Track A package is for internal development and validation.

No supported public release may ship on .NET 8 after November 10, 2026.

---

# Execution rules

Execute one phase at a time.

After each phase:

1. build all affected projects;
2. run relevant automated tests;
3. perform the listed manual checks;
4. summarize modified files;
5. summarize commands executed;
6. report test results;
7. report unresolved risks;
8. commit the completed phase;
9. stop before starting the next phase.

Use a dedicated branch:

```text
feature/windows-packaging
```

Suggested commits:

```text
packaging-a1: define installed application layout
packaging-a2: host production studio in ApiHost
packaging-a3: add production loopback startup
packaging-a4: create deterministic release build
packaging-a5: add Openness whitelist helper
packaging-a6: create initial Inno installer
packaging-a7: validate installer upgrade locking
packaging-a8: complete install upgrade uninstall validation
```

Avoid unrelated refactoring.

Do not delete, migrate, or rewrite user workbench data.

---

# Track A — Packaging prototype

## Phase A1 — Define the installed application layout

### Objective

Allow `ApiHost.exe` to locate MCP child executables from an installed directory without depending on the repository or solution file.

### Installed layout

```text
C:\Program Files\Automation Workbench\
├── ApiHost.exe
├── ApiHost.dll
├── ApiHost.deps.json
├── ApiHost.runtimeconfig.json
├── appsettings.json
├── wwwroot\
├── mcp\
│   ├── engineering\
│   │   ├── Mcp.Engineering.exe
│   │   ├── Mcp.Engineering.exe.config
│   │   └── dependency files
│   ├── knowledge\
│   │   ├── Mcp.Knowledge.exe
│   │   └── dependency files
│   ├── source-editor\
│   │   ├── Mcp.SourceEditor.exe
│   │   └── dependency files
│   └── version-control\
│       ├── Mcp.VersionControl.exe
│       └── dependency files
├── tools\
│   └── AutomationWorkbench.OpennessWhitelist.exe
└── licenses\
```

### Required changes

Update:

```text
src/ApiHost/McpExecutableResolver.cs
```

Use this resolution order:

1. explicit configuration override;
2. installed executable-relative path;
3. development repository fallback.

Keep configuration keys:

```text
Mcp:Engineering
Mcp:Knowledge
Mcp:SourceEditor
Mcp:VersionControl
```

Installed defaults:

```text
mcp\engineering\Mcp.Engineering.exe
mcp\knowledge\Mcp.Knowledge.exe
mcp\source-editor\Mcp.SourceEditor.exe
mcp\version-control\Mcp.VersionControl.exe
```

Development fallback may continue locating:

```text
AgentAssistPlcDev.sln
src\<project>\bin\<configuration>\<target-framework>
```

The installed path must never depend on:

* the solution file;
* source directories;
* the current working directory;
* `bin\Debug`;
* `bin\Release`.

Do not rename `ApiHost`.

Do not modify `launch.ps1` process naming during this phase.

### Validation behavior

Validate all MCP paths before starting processes.

Report all missing executables in one exception:

```text
Required MCP executables were not found:

Engineering: <path>
Knowledge: <path>
SourceEditor: <path>
VersionControl: <path>

Repair the installation or configure the corresponding Mcp path.
```

### Tests

Add tests for:

* explicit configuration override;
* executable-relative installed path;
* development fallback;
* path containing spaces;
* one missing executable;
* several missing executables;
* all failures reported together;
* inherited environment variables remain available to child processes.

### Acceptance criteria

* `ApiHost.exe` runs from a directory without the solution file.
* MCP executables start from the installed layout.
* development launch continues working;
* existing tests pass;
* `AUTOMATION_WORKBENCH_TRUSTED_ROOTS_FILE` reaches all MCP processes.

---

## Phase A2 — Serve the production Studio build from ApiHost

### Objective

Remove Node.js and the Vite development server from the installed runtime.

### Required changes

Build the Studio frontend:

```powershell
cd studio
npm ci
npm run build
```

Expected output:

```text
studio\dist
```

Update:

```text
src/ApiHost/Program.cs
src/ApiHost/ApiHost.csproj
tests/ApiHost.Tests\
.gitignore
```

Serve static files from `wwwroot`.

Required routing behavior:

1. static-file middleware;
2. API endpoint mappings;
3. SPA fallback;
4. `/api` requests excluded from SPA fallback.

Expected middleware structure:

```csharp
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapWorkbenchEndpoints();
app.MapCompatibilityEndpoints();

// SPA fallback that never captures /api paths.
```

### Testing-environment contract

`ASPNETCORE_ENVIRONMENT=Testing` must not depend on `studio/dist`.

Use one deterministic approach:

* create an empty temporary `wwwroot` for tests; or
* include a minimal test `index.html`; or
* disable SPA fallback explicitly in Testing.

Preferred approach:

```text
Testing uses a minimal deterministic wwwroot supplied by ApiHost.Tests.
```

Tests must run from a clean checkout without building Studio first.

### Frontend build integration

Add an explicit MSBuild property:

```text
BuildStudio
```

Expected behavior:

```text
BuildStudio=false
```

for normal backend development builds.

Expected release behavior:

```powershell
dotnet publish src\ApiHost\ApiHost.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:BuildStudio=true
```

The release build must fail if the Studio build fails.

Do not commit generated `ApiHost/wwwroot` content unless the project deliberately adopts checked-in production assets.

Add generated frontend output to `.gitignore`.

### CORS behavior

Development:

```text
Allow only the configured Vite development origin.
```

Production:

```text
No cross-origin access required.
```

Testing:

```text
Use an explicit deterministic test policy.
```

Remove production use of `AllowAnyOrigin`.

### Tests

Verify:

* `/` serves the SPA;
* `/assets/...` serves a static asset;
* `/api/status` returns JSON;
* unknown SPA route returns `index.html`;
* unknown `/api/...` route returns 404;
* `/api/...` never returns HTML;
* Testing does not require `studio/dist`;
* development Vite workflow still functions.

### Acceptance criteria

A published `ApiHost.exe` serves the complete UI without:

* Node.js;
* npm;
* Vite;
* a separate frontend process.

---

## Phase A3 — Add production loopback startup

### Objective

Give the published application a deterministic local address.

### Required binding

Bind production explicitly to:

```text
http://127.0.0.1:5239
```

Do not rely on `launchSettings.json`.

Do not expose the API on LAN interfaces.

Suggested configuration:

```json
{
  "Application": {
    "Host": "127.0.0.1",
    "Port": 5239,
    "OpenBrowserOnStart": true
  }
}
```

Environment and command-line configuration may override these values for development and testing.

### Port-collision behavior

Before completing startup:

1. attempt to bind port `5239`;
2. detect address-in-use errors;
3. stop startup cleanly;
4. log an actionable error.

Expected message:

```text
Automation Workbench could not start because port 5239 on 127.0.0.1 is already in use.

Close the other application or configure Application:Port to another loopback port.
```

Do not silently fall back to a random port during Track A.

### Browser launch

After Kestrel confirms it is listening:

```text
Open http://127.0.0.1:5239 using the default browser.
```

Use shell execution.

Browser launch must be configurable:

```text
Application:OpenBrowserOnStart=false
```

Testing must disable browser launch.

### Single-instance behavior

Do not add a mutex in Track A.

Development and installed instances may run simultaneously when configured with different ports.

### Tests

Verify:

* published application uses `127.0.0.1:5239`;
* launch profile settings are unnecessary;
* port collision produces a clear failure;
* browser launch happens after server readiness;
* browser launch can be disabled;
* Testing opens no browser;
* custom configured port works.

### Acceptance criteria

Launching `ApiHost.exe` from Explorer or a shortcut opens the application without PowerShell or `dotnet run`.

---

## Phase A4 — Create a deterministic release build

### Objective

Produce one complete runnable release directory from a clean checkout.

### Framework targets

Keep existing targets:

```text
ApiHost: net8.0
Agent: existing target
Mcp.Knowledge: net8.0
Mcp.SourceEditor: net8.0
Mcp.VersionControl: net8.0
Mcp.Engineering: net48
```

Do not retarget projects in Track A.

### Modern .NET publish settings

Use:

```text
RuntimeIdentifier=win-x64
SelfContained=true
PublishSingleFile=false
PublishTrimmed=false
PublishReadyToRun=false
```

Folder deployment is required for predictable native-library loading.

### Release script

Create:

```text
scripts\build-release.ps1
```

Responsibilities:

1. clean previous release output;
2. verify required SDKs and tools;
3. restore .NET dependencies;
4. run backend tests;
5. install frontend dependencies with `npm ci`;
6. run frontend tests;
7. build Studio;
8. publish `ApiHost`;
9. publish `Mcp.Knowledge`;
10. publish `Mcp.SourceEditor`;
11. publish `Mcp.VersionControl`;
12. build `Mcp.Engineering` in Release/net48;
13. assemble the installed directory structure;
14. preserve `Mcp.Engineering.exe.config`;
15. verify SQLite native binaries;
16. verify LibGit2Sharp native binaries;
17. verify all required executables;
18. generate a release manifest.

Output:

```text
artifacts\release\win-x64\
```

### Release manifest

Generate:

```text
artifacts\release\win-x64\release-manifest.json
```

Include:

* application version;
* Git commit;
* UTC build timestamp;
* target architecture;
* target frameworks;
* relative file paths;
* file sizes;
* SHA-256 hashes.

Do not include:

* credentials;
* absolute developer paths;
* local user names;
* workbench data.

### Versioning

Use one authoritative version passed to the release script.

Example:

```powershell
.\scripts\build-release.ps1 -Version 0.1.0-dev.1
```

Apply that version to:

* executable metadata;
* API status;
* release manifest;
* installer version.

Avoid introducing `Directory.Build.props` during the first implementation unless current version duplication cannot be controlled safely.

### Acceptance criteria

Run the release from:

```text
C:\Temp\Automation Workbench Package Test\
```

The release must work without:

* repository source;
* solution file;
* Visual Studio;
* .NET SDK;
* Node.js.

---

## Phase A5 — Add the Siemens Openness whitelist helper

### Objective

Replace installer-time use of the PowerShell script with a reliable elevated executable.

### New project

Create:

```text
src\Tools.OpennessWhitelist\
```

Output:

```text
AutomationWorkbench.OpennessWhitelist.exe
```

Add it to the solution.

### Commands

Implement:

```text
register --exe "<path>"
verify --exe "<path>"
remove --exe "<path>"
status --exe "<path>"
```

### Required behavior

For `Mcp.Engineering.exe`:

1. verify the file exists;
2. calculate SHA-256 over the final executable bytes;
3. encode the hash exactly as required by Siemens;
4. format the modification timestamp exactly as the existing script;
5. generate the V17 registry key and values;
6. write HKLM when elevated;
7. read the values back;
8. verify path, timestamp, and hash;
9. return a meaningful exit code.

### PowerShell parity requirement

Keep:

```text
scripts\register-whitelist.ps1
```

as the reference implementation.

Add a parity test that compares the complete generated registry content.

Given the same executable, both implementations must produce equal:

* registry path;
* executable path;
* timestamp value;
* Base64 SHA-256 value;
* value names.

Do not limit testing to individual hash or formatting functions.

### Real-machine validation

Validate on a TIA Portal V17 machine:

1. remove the test whitelist entry;
2. register using the helper;
3. verify using the helper;
4. start an Openness operation;
5. confirm Siemens does not show an unexpected firewall prompt.

### Signing-order rule

Document permanently:

```text
Build executable
→ sign executable when signing is introduced
→ install executable
→ calculate hash from installed signed file
→ register whitelist
```

Never precompute the final whitelist hash before signing.

### Exit codes

Document stable exit codes:

```text
0  success
10 invalid arguments
11 executable missing
12 unsupported TIA version
13 elevation required
14 registry write failure
15 verification failure
16 hash calculation failure
```

### Acceptance criteria

The helper produces values identical to the PowerShell reference and succeeds on a real V17 environment.

---

## Phase A6 — Create the initial Inno Setup installer

### Objective

Install the assembled release directory under Program Files and register the Siemens whitelist.

### New files

Create:

```text
installer\AutomationWorkbench.iss
scripts\build-installer.ps1
```

### Installer configuration

Use:

```text
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
DefaultDirName={autopf}\Automation Workbench
CloseApplications=yes
RestartApplications=no
Compression=lzma2
SolidCompression=yes
```

Use one stable `AppId`.

### Installer source

Install only from:

```text
artifacts\release\win-x64\
```

Do not collect files directly from project `bin` or `publish` directories.

### Install sequence

1. detect Windows x64;
2. detect an existing installation;
3. request closure of running application processes;
4. install release files;
5. run the whitelist helper against the installed `Mcp.Engineering.exe`;
6. verify the whitelist;
7. create Start Menu shortcut;
8. optionally create desktop shortcut;
9. optionally launch `ApiHost.exe` unelevated.

Shortcut display name:

```text
Automation Workbench
```

Shortcut target:

```text
{app}\ApiHost.exe
```

### Missing TIA behavior

Missing TIA Portal V17 does not block file installation during Track A.

The installer must report that engineering integration requires TIA V17.

Whitelist registration behavior when TIA registry keys are missing must be explicit and tested.

Preferred prototype behavior:

```text
Install succeeds with a warning.
Engineering integration remains unavailable until TIA V17 is installed and whitelist registration is rerun through Repair.
```

### Uninstall behavior

Remove:

* `{app}`;
* shortcuts;
* application-specific whitelist entry;
* uninstall registration.

Preserve:

```text
%APPDATA%\AutomationWorkbench
%APPDATA%\PlcAiAssistant
%LOCALAPPDATA%\AutomationWorkbench
%LOCALAPPDATA%\PlcAiAssistant
```

### Output

```text
artifacts\installer\AutomationWorkbench-<version>-win-x64-setup.exe
artifacts\installer\AutomationWorkbench-<version>-win-x64-setup.exe.sha256
```

### Acceptance criteria

A clean Windows machine can install, launch, and uninstall the application while retaining user data.

---

## Phase A7 — Spike upgrade process locking

### Objective

Verify that Inno Setup can replace `ApiHost.exe` and all MCP executable files while the installed application is running.

This phase must happen immediately after the first working installer.

### Test scenario

1. install version A;
2. launch `ApiHost.exe`;
3. trigger all four MCP processes;
4. confirm each process is running;
5. keep the application active;
6. start version B installer;
7. allow Inno Setup to close applications;
8. observe shutdown behavior;
9. confirm all file handles are released;
10. complete replacement;
11. launch version B;
12. verify all MCP processes start;
13. verify the whitelist references the version B engineering executable.

### Important risk

The MCP processes are child processes of `ApiHost.exe`.

Restart Manager may detect `ApiHost.exe` while file locks remain held by MCP descendants.

Inspect:

* whether child processes terminate when ApiHost is closed;
* whether Inno identifies each locked MCP executable;
* whether file replacement waits correctly;
* whether orphan MCP processes remain;
* whether upgrade intermittently fails.

### Required result

Choose one outcome based on evidence.

#### Outcome A — Restart Manager is sufficient

Document the exact Inno configuration and process behavior.

#### Outcome B — Graceful shutdown support is required

Add a minimal production shutdown mechanism before continuing.

Possible mechanisms:

* local authenticated shutdown endpoint;
* named event;
* installer-invoked command;
* parent process signal handling.

The mechanism must:

1. stop accepting new requests;
2. dispose `McpHost`;
3. terminate all MCP child processes;
4. wait for process exit;
5. exit `ApiHost.exe`;
6. avoid exposing unauthenticated remote shutdown capability.

#### Outcome C — Installer must detect child processes explicitly

Add Inno process checks for:

```text
ApiHost.exe
Mcp.Engineering.exe
Mcp.Knowledge.exe
Mcp.SourceEditor.exe
Mcp.VersionControl.exe
```

Do not proceed to final upgrade validation until repeated upgrade tests release every executable reliably.

### Repetition requirement

Run the active-application upgrade test at least five times.

One successful run is insufficient for file-lock validation.

### Acceptance criteria

Five consecutive upgrades complete without:

* locked-file errors;
* orphan MCP processes;
* partial replacement;
* stale whitelist hash;
* deleted user data.

---

## Phase A8 — Complete packaging validation

### Objective

Validate installation lifecycle and project structure.

### Test environments

Test at least:

#### Environment A

```text
Windows x64
TIA Portal V17 installed
User belongs to Siemens TIA Openness group
Git installed
No previous application installation
```

#### Environment B

```text
Windows x64
TIA Portal V17 missing
Git installed
```

#### Environment C

```text
Windows x64
TIA Portal V17 installed
User missing Siemens TIA Openness membership
```

#### Environment D

```text
Previous Automation Workbench version installed
Existing workbench projects
Application running during upgrade
```

#### Environment E

```text
Windows user name contains non-ASCII characters
User-data paths contain spaces
```

### Clean installation checks

Verify:

* UAC elevation;
* installation path;
* release manifest;
* Start Menu shortcut;
* unelevated application launch;
* loopback binding;
* browser launch;
* SPA routes;
* API routes;
* MCP startup;
* SQLite native loading;
* LibGit2Sharp native loading;
* engineering assembly resolution;
* whitelist verification.

### Functional smoke tests

Perform:

* create or open a workbench;
* add or open a device;
* run engineering environment check;
* export a safe TIA object;
* ingest source into the knowledge database;
* query knowledge;
* inspect Git status;
* create a snapshot;
* restart the application;
* confirm persisted state.

### Upgrade checks

Verify:

* previous installation detected;
* running processes closed;
* all binaries replaced;
* whitelist hash refreshed;
* application version updated;
* workbench repositories unchanged;
* SQLite databases valid;
* configuration preserved.

### Repair checks

Verify:

* missing application file restored;
* whitelist entry restored;
* user data unchanged.

### Uninstall checks

Verify removal of:

* Program Files application directory;
* shortcuts;
* application whitelist entry;
* uninstall metadata.

Verify preservation of:

* current user configuration;
* legacy configuration;
* workbenches;
* Git repositories;
* SQLite databases;
* audit logs;
* exports.

### Track A completion report

Produce:

```text
buildnote\plan\packaging-track-a-report.md
```

Include:

1. final installed layout;
2. modified files;
3. build command;
4. installer command;
5. framework targets;
6. installer version;
7. clean-install results;
8. upgrade-locking results;
9. repair results;
10. uninstall results;
11. whitelist parity results;
12. real TIA V17 validation;
13. known limitations;
14. deferred work;
15. recommendation on readiness for Track B.

---

# Track B — .NET 10 migration

Start only after Track A is stable.

## Objective

Move the modern .NET projects from .NET 8 to .NET 10 while preserving the exact packaging architecture validated in Track A.

## Rules

* use a dedicated branch;
* add `global.json` for the approved SDK;
* keep `Mcp.Engineering` on net48;
* do not redesign the installer layout;
* do not combine credential or signing work into the migration.

## Required validation

Verify:

* ModelContextProtocol package behavior;
* ASP.NET Core startup;
* static-file hosting;
* SQLite native loading;
* LibGit2Sharp native loading;
* MCP stdio communication;
* tests;
* release build;
* clean install;
* upgrade from the latest .NET 8 internal package;
* whitelist refresh;
* user-data preservation.

## Completion gate

Track B is complete only when the .NET 10 package passes the complete Track A validation matrix.

---

# Track C — Production hardening

Start after Track B or before an external supported release.

## Scope

* DPAPI or Windows Credential Manager
* atomic plaintext-key migration
* installer and executable signing
* signature verification
* polished first-run environment wizard
* single-instance behavior
* improved graceful shutdown
* production update manifest
* release diagnostics
* prerequisite guidance
* TIA V18+ architecture
* enterprise installer evaluation

## Signing requirement

When signing is introduced:

```text
Build
→ sign
→ install
→ calculate installed-file hash
→ register Siemens whitelist
```

The whitelist hash must always represent the final signed executable.

---

# Final definition of done

The packaging restructuring is validated when:

* one command creates the release directory;
* one command creates the installer;
* the installed application runs without SDKs or Node.js;
* `ApiHost.exe` serves the frontend;
* production binds explicitly to `127.0.0.1:5239`;
* tests do not depend on `studio/dist`;
* MCP executables resolve from installed relative paths;
* whitelist helper output matches the PowerShell reference;
* real TIA V17 integration succeeds;
* five consecutive active-process upgrades succeed;
* repair restores binaries and whitelist state;
* uninstall preserves all user data;
* the remaining .NET 10 and production-hardening work is documented.

