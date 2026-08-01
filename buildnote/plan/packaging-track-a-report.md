# Packaging Track A Report

Date: 2026-08-01
Branch: `feature/windows-packaging`

## Result

The Windows packaging lifecycle is operational for the tested Windows x64
environment. Release creation, installer creation, clean installation,
active-process replacement, repair, uninstall, and real TIA V17 Openness
validation all passed.

## Installed layout

```text
{app}\
├── ApiHost.exe
├── appsettings*.json
├── wwwroot\
├── mcp\
│   ├── engineering\Mcp.Engineering.exe + .exe.config + dependencies
│   ├── knowledge\Mcp.Knowledge.exe + dependencies + e_sqlite3.dll
│   ├── source-editor\Mcp.SourceEditor.exe + dependencies
│   └── version-control\Mcp.VersionControl.exe + dependencies + git2-*.dll
├── tools\
│   └── AutomationWorkbench.OpennessWhitelist.exe
└── release-manifest.json
```

## Changed implementation

Key packaging commits:

* `f26b6f0` — installed MCP layout and executable resolution;
* `44dfc1d` — production Studio hosting and static-file fallback;
* `4d94aab` — production loopback startup and collision handling;
* `3724244` — deterministic release build and manifest;
* `76ea477` — Siemens Openness whitelist helper and parity test;
* `82a9dbc` — initial Inno Setup installer;
* `811b09c` — Inno Setup 6.7.3 compatibility fixes.

Important files:

* `scripts/build-release.ps1`;
* `scripts/build-installer.ps1`;
* `installer/AutomationWorkbench.iss`;
* `src/Tools.OpennessWhitelist/`;
* `tests/Mcp.Engineering.Tests/WhitelistParityTests.cs`;
* `src/ApiHost/Program.cs`.

## Build commands

```powershell
.\scripts\build-release.ps1 -Version 0.1.0-dev.1
.\scripts\build-installer.ps1 -Version 0.1.0-dev.1
```

Release output:

```text
artifacts\release\win-x64\
```

Installer output:

```text
artifacts\installer\AutomationWorkbench-0.1.0-dev.1-win-x64-setup.exe
```

The version B installer used for upgrade validation was:

```text
artifacts\installer\AutomationWorkbench-0.1.0-dev.2-win-x64-setup.exe
```

## Framework targets

| Component | Target |
|---|---|
| ApiHost | net8.0, self-contained win-x64 |
| Agent | net8.0 |
| Mcp.Knowledge | net8.0, self-contained win-x64 |
| Mcp.SourceEditor | net8.0, self-contained win-x64 |
| Mcp.VersionControl | net8.0, self-contained win-x64 |
| Mcp.Engineering | net48 |
| Openness whitelist helper | net48 |

## Validation results

### Build and tests

* backend tests: 632 passed;
* Studio tests: 95 passed;
* release folder build completed successfully;
* SQLite native binary verified;
* LibGit2Sharp native binary verified;
* Engineering `.exe.config` preserved;
* release manifest hashes verified;
* installer compiled with Inno Setup 6.7.3.

### Clean install

Installed to `C:\Temp\Automation Workbench Installer Test\`.

* ApiHost installed: passed;
* Engineering MCP installed: passed;
* whitelist helper installed: passed;
* uninstaller created: passed;
* uninstall removed the installation directory: passed.

### Upgrade locking

Five active-process upgrade cycles passed.

Every cycle reported:

* installer exit code `0`;
* previous ApiHost exited: `True`;
* Version B API status: `0.1.0-dev.2`;
* process count: `5`;
* missing processes: none.

The five processes were ApiHost and all four MCP servers.

### Repair and uninstall

* deleted `ApiHost.exe` was restored by repair;
* four isolated configuration/data sentinels survived repair;
* uninstall exit code was `0`;
* installation directory was removed;
* all four sentinels survived uninstall.

### Whitelist parity

The helper and `scripts/register-whitelist.ps1` generated identical complete
registry content for the same executable, including registry path, escaped
path, timestamp, Base64 SHA-256 hash, and value names.

## Real TIA V17 validation

Passed on the TIA V17 environment:

* elevated helper registration returned exit code `0`;
* helper verification returned exit code `0`;
* opening a project succeeded;
* exporting blocks succeeded;
* no unexpected Siemens firewall prompt was observed.

## Known limitations and deferred work

* real TIA V17 registration and an Openness operation remain required;
* signed-build hash validation is deferred until executable signing exists;
* .NET 10 migration is deferred to Track B;
* credential protection, single-instance behavior, and production update
  infrastructure remain Track C work.

## Recommendation

Track A is complete for the validated Windows x64 environments. Track B may
begin on a dedicated branch, preserving the Track A installer layout and
keeping `Mcp.Engineering` on net48.
