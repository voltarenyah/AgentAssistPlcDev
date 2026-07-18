# mcp-engineering — TIA V17 engineering adapter (Phase 0–1 detailed)

## 1. Purpose

`mcp-engineering` is the MCP server that mediates all access to the Siemens TIA Portal V17 engineering API (TIA Openness). It is the **first deliverable** in the platform-adapter family (`TiaV17Adapter`), and the risk-spike that proves .NET Framework 4.8 + MCP SDK works end-to-end.

The server exposes MCP tools that map 1:1 to primary TIA Openness workflows:
- **Open / close** a TIA project (with or without visible UI)
- **Export** source XML (blocks) to a dedicated per-project working folder on disk
- **Import** modified XML files back into the TIA project
- **Compile** any or all blocks, reporting success/failure per block

## 1.1 Locked decisions (2026-07-17)

Locked with the user after the feasibility review of this document:

1. **`save_project` tool added** — explicit save is the ONLY way the server persists a TIA project. The server never saves implicitly (not on disconnect, not on process exit). If the project has unsaved changes, disconnect/shutdown reports that state; it does not save.
2. **Phase 1 tool surface = 12 tools:** `check_environment`, `list_sessions`, `connect`, `disconnect`, `save_project`, `get_project_info`, `list_blocks`, `export_block`, `export_all_blocks`, `import_block`, `compile_block`, `compile_plc`. Deferred to later phases: `export_selection`, `import_all`, `compile_hardware`, `get_compile_state`.
3. **`compile_block` semantics:** compile the PLC software, then filter result messages to the named block (Openness V17 believed to offer only software-level synchronous compile — verified in the API pass, step §15).
4. **Test project:** `C:\Users\Ansel\Documents\Automation\V17\AgentAssistProgramming\TestPLCExportDemo`

---

## 2. Architecture

```
MCP Client (Inspector / Agent / App)
       │  stdio JSON-RPC (UTF-8, line-delimited)
       ▼
┌─────────────────────────────────────┐
│  Mcp.Engineering.exe                │  net48 console app
│                                     │
│  ModelContextProtocol (C# SDK)      │  NuGet: ModelContextProtocol
│       │                             │
│  McpServer                          │  built-in transport dispatching
│       │                             │
│  EngineeringServer                  │  tool registration + error mapping
│       │                             │
│  IEngineeringPlatform ───┐          │  Contracts interface
│                          ▼          │
│  TiaV17Adapter                      │  Siemens.Engineering P/Invoke wrapper
│  ┌───────────────────────────────┐  │
│  │  TiaPortalProcess (attach)    │  │  Siemens.Engineering.TiaPortal
│  │  TiaProject (open/close)      │  │  Siemens.Engineering.Project
│  │  BlockExporter (export)       │  │  Siemens.Engineering.SW.Blocks.*
│  │  BlockImporter (import)       │  │
│  │  Compiler (compile)           │  │  Siemens.Engineering.Compiler.*
│  └───────────────────────────────┘  │
└─────────────────────────────────────┘
       │  references
       ▼
Contracts (netstandard2.0)
  - IEngineeringPlatform interface
  - EngineeringConnectionInfo, BlockInfo, CompileResult, … DTOs
```

### 2.1 Framework constraint

TIA Openness assemblies (`Siemens.Engineering.dll` et al.) target .NET Framework and refuse to load on any .NET Core / .NET 5+ runtime. Therefore:

| Component | Target | Reason |
|-----------|--------|--------|
| `Mcp.Engineering` | **net48** | Must `Assembly.Load` or reference Openness Dlls |
| `Contracts` | **netstandard2.0** | Shared DTOs cross-compile to net48 and net8 |

### 2.2 Process model

- The MCP server runs as a **child process** of the UI or agent.
- It uses **stdio transport** — one dedicated process per client session.
- Multiple clients → multiple child processes → each holds its own Openness COM context.
- The server **must run on the same Windows user session** as TIA Portal (Openness COM activation requires it).
- **Critical bootstrap:** Before any Siemens.Engineering type is touched, the server must register an `AppDomain.AssemblyResolve` handler to redirect `Siemens.Engineering.dll` loading to the correct registry-registered path (see §13.1). Without this, the DLLs will not load.

### 2.3 Lifetime contract

| Client action | Server behaviour |
|---|---|
| Start | Init COM; probe Openness DLL paths; validate user group |
| `initialize` (MCP handshake) | Return server capabilities & tool list |
| Tool call | Dispatch to adapter; return structured JSON result or error |
| `disconnect` | Release TIA project reference (no forced TIA shutdown) |
| Process exit / `SIGTERM` | Graceful close: release COM, dispose portal process, exit 0 — never saves implicitly; unsaved-changes state is reported on disconnect, not persisted |

---

## 3. MCP tool surface

Each tool is registered with `readOnlyHint` or `destructiveHint`, emits structured JSON, and returns actionable errors.

### 3.1 Session life cycle

| Tool | Hint | Input | Output | Notes |
|------|------|-------|--------|-------|
| `list_sessions` | read | — | `SessionInfo[]` | Enumerate running TIA Portal processes (attachable or standalone) |
| `connect` | write | `{ sessionId?, projectPath?, withUI: bool }` | `ConnectionInfo` | Attach to session or open project; starts a portal process / takes a project lock; see §4 |
| `disconnect` | write | — | `{}` | Release project; tool calls after this fail until next `connect` |
| `save_project` | write | — | `{}` | Explicit project save; the only tool that persists the project |

### 3.2 Block export / import

| Tool | Hint | Input | Output | Notes |
|------|------|-------|--------|-------|
| `list_blocks` | read | `{ plcName?: string }` | `BlockInfo[]` | Enumerate all blocks (OB/FB/FC/DB) incl. nested block groups; needed to discover names for export/import |
| `export_block` | read | `{ blockName: string, outputDir: string }` | `ExportResult` | Export single block to XML in outputDir |
| `export_all_blocks` | read | `{ outputDir: string }` | `ExportResult[]` | Export all PLC blocks; returns array of results |
| `import_block` | destructive | `{ blockName: string, xmlFilePath: string, comment?: string }` | `ImportResult` | Import modified XML; see §6.1 for safety guarantees |

`export_selection` and `import_all` are deferred past Phase 1 (see §1.1).

### 3.3 Compilation

| Tool | Hint | Input | Output | Notes |
|------|------|-------|--------|-------|
| `compile_block` | write | `{ blockName: string }` | `CompileResult` | Implemented as full-software compile + per-block message filtering (§7.1) |
| `compile_plc` | write | — | `CompileResult` | Full `PLCSoftware` compile |

compile_hardware and get_compile_state deferred past Phase 1 (see §1.1).

### 3.4 Environment & diagnostics

| Tool | Hint | Input | Output | Notes |
|------|------|-------|--------|-------|
| `check_environment` | read | — | `EnvCheckResult` | User group, DLL paths, TIA version, Openness registration |
| `get_project_info` | read | — | `ProjectInfo` | Name, path, PLC devices, block count, last modified |

---

## 4. TIA Openness connection strategies

This is the most failure-sensitive part of the adapter. Openness has **two connection modes**, both of which must be supported.

### 4.1 Attach to running TIA session (with UI)

```
Client: connect { sessionId: "..." }   (withUI defaults to true)
  │
  ▼
TiaV17Adapter.AttachToSession(sessionId)
  ├─ TiaPortal.GetProcesses()  → static; returns running TiaPortalProcess list; find matching process id
  ├─ process.Attach()         → attach via the matching process; returns running TiaPortal instance
  ├─ read the currently open project from the attached portal instance
  │   ├─ If null → error: "TIA is running but no project is open"
  │   └─ If set  → wrap in TiaProject handle
  └─ return ConnectionInfo { attached: true, projectName, hasUI: true }
```

- **When to use:** user has TIA open and is actively working; the adapter is a helper, not a driver.
- **Risk:** user can close the project or TIA while attached → `ObjectDisposedException`. Adapter must catch and re-query.
- Phase 1 attaches using the V17 Openness assembly only. Other TIA versions (V18+) require their own adapter assembly (§13.7); multi-version attach is out of scope for Phase 1.
- Exact API names (`GetProcesses` / `Attach`, current-project access) are verified in the API pass, §15 step 3.

### 4.2 Open project without UI (headless)

```
Client: connect { projectPath: "C:\\…\\MyProject.ap17", withUI: false }
  │
  ▼
TiaV17Adapter.OpenProject(projectPath, withUI: false)
  ├─ portal = new TiaPortal(TiaPortalMode.WithoutUserInterface)   ← COM starts a new portal process
  ├─ project = portal.Projects.Open(new FileInfo(projectPath))
  ├─ guard: if project == null → error "project file not found or version mismatch"
  └─ return ConnectionInfo { attached: false, projectName, hasUI: false }
```

(Exact API names verified in the API pass, §15 step 3.)

- **When to use:** automated pipelines, CI-like export-then-import workflows, batch compile.
- **Important:** `TiaPortalMode.WithoutUserInterface` still starts `Siemens.TIA.Portal.exe` as a background process (it hosts the COM server). The process is visible in Task Manager but has no window. It must be shut down explicitly (`portal.Dispose()`) when done.
- **Locking:** TIA acquires a write lock on the project file. A second Openness instance (or the user opening the same project in the UI) will fail with "project is already opened". Adapter checks this before attempting open.

### 4.3 Open project with UI (visible)

```
Client: connect { projectPath: "…", withUI: true }
  │
  ▼
Same as 4.2 but TiaPortalMode.WithUserInterface → user sees a new TIA window with the project loaded.
```

- Useful for debugging or when the user wants to watch the import/compile visually.
- The portal window is *owned* by the Openness process — closing the console MCP server also closes the TIA window.

### 4.4 Connection parameters explained

```jsonc
// connect input (all fields optional — server picks sensible default)
{
  "sessionId": "wpf_1234",         // GUID or process ID → attach mode (§4.1)
  "projectPath": "C:\\...\\pro.ap17",  // file path → open mode (§4.2/4.3)
  "withUI": false,                 // default false for pipelines; attach-mode inherits UI if running
  "timeoutSeconds": 60             // max wait for Openness startup (headless takes ~10–30s)
}
```

Resolution order:
1. If `sessionId` present → attach (ignore `withUI`; use whatever the running session has).
2. If `projectPath` present → open new portal process with `withUI`.
3. If neither → error: "provide sessionId or projectPath".
4. If both present → error: "ambiguous — provide sessionId or projectPath, not both".

---

## 5. Export details

### 5.1 Export mechanics

TIA Openness exports blocks as **XML files** conforming to the `http://www.siemens.com/automation/Openness/SW/Interface/vX` schema. The export is a multi-node XML document containing:

- **Interface section** (IO pins, statics, temps, constants)
- **Network list** (each network has instructions in STL/LAD/FBD/SCL XML)
- **Multilingual text** (`<Comment>`, `<Title>`, `<NetworkTitle>`, `<NetworkComment>` in each configured language)

```
Output directory structure (per TIA project):

C:\Users\Ansel\orca\projects\AgentAssistPlcDev\exported\[ProjectGuid]\[PlcName]\
├── Main [OB1].xml
├── Cyclic interrupt [OB30].xml
├── PumpControl [FB1].xml
├── MotorControl [FC1].xml
├── GlobalData [DB1].xml
└── ...
```

### 5.2 `export_block` workflow

```
1. Resolve PLC software via the device-tree traversal in §13.4 (Devices → DeviceItems → GetService<SoftwareContainer>() → PlcSoftware),
   then search `plcSoftware.BlockGroup` recursively through nested block groups
   └─ if not found → error listing available blocks
2. block.Export(filePath)       ← Openness native export
3. Read exported XML, parse key metadata (block name, type, number, network count)
4. Write to outputDir / "{BlockName} [{BlockNumber}].xml"
5. Return ExportResult { blockName, blockNumber, blockType, path, networkCount, exportedAt }
```

### 5.3 `export_all_blocks` note

TIA Openness does not have a built-in "export all" — the adapter enumerates all items in `project.Devices`, resolves `PLCSoftware`, iterates `BlockGroup.Blocks`, and calls `Export()` per item. Enumeration MUST recurse into nested block groups (user-created folders under the root BlockGroup), not just the root group. Large projects (500+ blocks) may take minutes. Export progress is reported via MCP `progress` notifications.

---

## 6. Import details

### 6.1 Safety design (the "no-surprise import" rule)

Import into a running TIA project is **destructive** and can corrupt a project if:
- The XML was edited in ways that break TIA schema constraints
- A block is currently open in the TIA editor with unsaved changes
- Block interface changes conflict with cross-block call sites

The adapter enforces:

1. **Pre-import validation:** `src_validate` (or ad-hoc XML schema check) must be called independently — the engineering MCP does not re-validate; it trusts the caller.
2. **Block-not-open guard:** Openness has NO API to enumerate open editor tabs, so this guard is exception-driven: the import is attempted inside the exclusive-access transaction (item 3); if Openness reports the block as open/checked-out in an editor, the transaction is rolled back and the tool returns an actionable error naming the block.
3. **Exclusive access + transaction:** Every import must be wrapped in `TiaPortal.ExclusiveAccess()` → `Transaction()` → `CommitOnDispose()` (see §13.3). This is required by Openness — operations outside a transaction can corrupt the TIA project file.
4. **Snapshot prerequisite** (recommended): The caller should call `vc_snapshot` on the working directory before every `import_block` call. The adapter **logs a warning** if the working directory's git tree is not clean, but does not enforce it.
5. **Re-export verify:** After import, the adapter attempts `Export()` again and compares the whole document against the imported file, normalized by stripping only the `<Created>` timestamp line (Spike B proved comment-only edits round-trip byte-stable otherwise). **Timing caveat (verified 2026-07-18):** a freshly imported code block is *inconsistent until compiled*, and inconsistent blocks cannot be exported — so the in-import verify is best-effort. If the block is inconsistent, the verify is **deferred** with a warning and the result reports `interfaceVerified: false`; the canonical verify chain is `import_block` → `compile_block` → `export_block` + compare (which the E2E walkthrough runs). If the compare runs and differs from what was written, the result flags **"interface drift detected"** (`interfaceDrift: true`).

### 6.2 `import_block` workflow

```
1. Guard: exception-driven block-not-open check (§6.1 item 2) — no editor enumeration; Openness reports an open/checked-out block during the transactional import
2. Read xmlFilePath, parse blockName from root element <SW.Blocks.GlobalDB> or similar
3. (Optional) Diff against last exported version if available — log warning if unchanged
4. plcSoftware.BlockGroup.Blocks.Import(new FileInfo(xmlFilePath), ImportOptions.Override)   ← Openness native import (verified: `Import(FileInfo, ImportOptions)` returns `IList<PlcBlock>`; an overload with `SWImportOptions` — IgnoreStructuralChanges etc. — exists for advanced cases)
5. Re-export to verify interface stability (§6.1 item 5)
6. Return ImportResult { blockName, success, warnings[], interfaceDrift: bool, importedAt }
```

### 6.3 Import failure modes

| Symptom | Likely cause | Adapter action |
|---------|-------------|----------------|
| `InvalidOperationException: "Block is checked out"` | Block open in editor | Return error with block name; user must close it |
| `COMException: "Access denied"` | File permissions / Openness group | Return EnvCheck guide |
| `XmlException: root element mismatch` | XML targets wrong block type | Return error with expected vs actual root |
| `ArgumentException: "The block … does not exist"` | Block was deleted between export & import | Return error + list existing blocks |
| Openness hangs > timeout | XML is deeply malformed or too large | Wait up to a bounded timeout; if exceeded, report "timeout — TIA may be hung; save your work and restart TIA Portal". The adapter never kills the TIA process |

---

## 7. Compilation

Openness V17 compile is believed **synchronous** and **software-granular** (confirmed in the API pass, §15 step 3). There is no per-block compile call — `compile_block` is implemented as a full-software compile plus per-block message filtering.

### 7.1 `compile_block` workflow

```
1. Resolve PlcSoftware via the §13.4 device-tree traversal
2. compiler = plcSoftware.GetService<ICompilable>()
3. result = compiler.Compile()   ← parameterless, synchronous; blocks until done (verified: no compile-options enum exists in V17)
4. Filter result.Messages to entries whose path/block name matches the requested block.
   Message paths look like `PLC_1\Main (OB1)\Network 1` — segment 2 is the block name
   carrying a " (OB1)"-style type suffix that the filter strips before comparing (verified 2026-07-17).
5. Return CompileResult for that block (state = block's worst message severity, or "success" if no messages for it)
```

### 7.2 `compile_plc` workflow

```
1. Resolve PLC device from device tree
2. plcSoftware = traverse per §13.4 (Devices → DeviceItems → GetService<SoftwareContainer>() → PlcSoftware)
3. compiler = plcSoftware.GetService<ICompilable>()
4. result = compiler.Compile()   ← parameterless, synchronous; blocks until done (verified: no compile-options enum exists in V17)
5. Return aggregate CompileResult
```

### 7.3 Compile states

Result state comes from `CompilerResult.State` (a `CompilerResultState` enum — verified values: `Success`, `Information`, `Warning`, `Error`, verified by reflection 2026-07-17).

Because compile is synchronous, there is no asynchronous "compiling" state to poll — which is why `get_compile_state` was dropped from the Phase 1 surface (§1.1).

### 7.4 Error reporting

Each `CompileResult` includes a structured `messages[]` array:

```jsonc
{
  "state": "error",
  "messages": [
    {
      "type": "error",
      "code": "C035",              // TIA error code; may be empty
      "text": "\"PumpControl\" (FB1): The actual data type of the parameter differs …",
      "blockName": "PumpControl",
      "networkNumber": 3,
      "line": 42
    }
  ],
  "durationMs": 5840
}
```

Message text is `CompilerResultMessage.Description` (verified — there is no `Text` property); other fields (block name, network, line) are mapped per the API-surface reference (`buildnote/bestpractice/openness-v17-api-surface.md`). The adapter does **not** attempt to parse or simplify error text — it passes Openness error messages verbatim; unparseable text is passed verbatim per existing policy.

---

## 8. Error handling & diagnostics

### 8.1 MCP error convention

Tool-level failures MUST be returned as a normal tool result with `isError: true` plus a structured JSON error payload. JSON-RPC protocol errors are reserved for protocol-level problems (unknown method, malformed parameters, dispatch failures).

Error payload shape:

```jsonc
{
  "code": "BLOCK_NOT_FOUND_OR_OPEN",
  "message": "Block 'PumpControl' is open in a TIA editor or does not exist.",
  "remediation": "Close the block in the TIA editor, or check the name with list_blocks."
}
```

| Category code | When |
|---------------|------|
| `TIA_NOT_INSTALLED` | TIA / Openness API missing |
| `NOT_IN_OPENNESS_GROUP` | User is not in "Siemens TIA Openness" group |
| `PROJECT_NOT_FOUND` | Project missing, locked, or version mismatch |
| `BLOCK_NOT_FOUND_OR_OPEN` | Block not found or open/checked-out in an editor |
| `IMPORT_REJECTED` | Import pre-condition failed (see §6.1) |
| `NON_RECOVERABLE` | `NonRecoverableException` — TIA state may be corrupted; save work, restart TIA |
| `OPENNESS_ERROR` | Generic recoverable `EngineeringException` |

All Siemens.Engineering exceptions are caught by a centralized `OpennessExceptionHandler` (§13.5) which maps them to the appropriate category code above.

### 8.2 Environment check (`check_environment`)

```jsonc
{
  "opennessInstalled": true,
  "opennessVersion": "V17.0.0.0",
  "tiaPortalVersion": "V17 (Build 12345)",
  "userInOpennessGroup": true,
  "opennessGroupName": "Siemens TIA Openness",
  "opennessDllPaths": {
    "Siemens.Engineering": "C:\\Program Files\\Siemens\\Automation\\Portal V17\\PublicAPI\\V17\\Siemens.Engineering.dll",
    "Siemens.Engineering.Hmi": "C:\\Program Files\\Siemens\\Automation\\Portal V17\\PublicAPI\\V17\\Siemens.Engineering.Hmi.dll"
  },
  "currentUser": "ansel",
  "machineName": "DEVELOPER-PC",
  "osVersion": "Windows 11 Pro 10.0.26200"
}
```

---

## 9. Phase 0 spike: standalone validation

Before any UI or agent integration, validate the riskiest path:

**Prerequisite:** MCP Inspector requires Node.js (`npx @modelcontextprotocol/inspector`) — it must be installed before Spike A.

### Spike A: mcp-engineering skeleton + MCP Inspector call

```
Scaffold:
  1. dotnet new console -n Mcp.Engineering
  2. Hand-edit the csproj to <TargetFramework>net48</TargetFramework> (SDK-style project; if no
     .NET Framework 4.8 targeting pack is installed, add the Microsoft.NETFramework.ReferenceAssemblies
     NuGet package). Add NuGet: ModelContextProtocol — pin an EXACT version (0.x preview — API churn
     between versions is high)
  3. Add reference: Contracts (netstandard2.0)
  4. Implement TiaV17Adapter.Connect() — just TiaPortal.GetProcesses() + Attach()
  5. Register one tool: `list_sessions` → returns attached process instances
  6. Test with MCP Inspector:
     - Start the server
     - Inspector discovers tool
     - Call `list_sessions` → see running TIA Portal instance(s)

Pass criteria:
  □ Server builds and runs on net48
  □ Inspector discovers the tool and calls it
  □ TIA Portal instance is listed
  □ Process exits cleanly (no COM leaks reported in Event Viewer)
```

### Spike B: manual XML round-trip (engineering setup)

```
Manual steps (in TIA):
  1. Open the test project: C:\Users\Ansel\Documents\Automation\V17\AgentAssistProgramming\TestPLCExportDemo
  2. Export one FB manually via Openness (console test harness)
  3. Hand-edit <NetworkComment> node
  4. Import modified XML back
  5. Open in TIA editor → confirm comment visible, logic byte-identical

Pass criteria:
  □ XML round-trip preserves block logic identically (diff export_before vs export_after)
  □ Comment is visible in TIA V17 editor
  □ Block compiles without errors
  □ Multilingual comment nodes are handled correctly
```

---

## 10. Deployment & configuration

### 10.1 Reference assemblies

The TIA Openness PublicAPI DLLs are **not** NuGet packages — they are installed by the TIA Portal installer:

```
C:\Program Files\Siemens\Automation\Portal V17\PublicAPI\V17\
├── Siemens.Engineering.dll
├── Siemens.Engineering.Hmi.dll
├── Siemens.Engineering.Compiler.dll
└── ...
```

The `.csproj` uses a **hard-coded hint path** pointing to this directory. Before any build, `check_environment` validates these paths exist.

### 10.2 App.config (net48)

```xml
<configuration>
  <startup useLegacyV2RuntimeActivationPolicy="true">
    <supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8" />
  </startup>
</configuration>
```

`useLegacyV2RuntimeActivationPolicy="true"` is required for mixed-mode assembly loading (C++/CLI dependencies in Openness).

### 10.3 Binding redirects

Openness DLLs may require binding redirects for `System.Runtime` and `System.Threading.Tasks.Dataflow` — probed and pinned during Spike A.

### 10.4 Openness registration

The COM interop layer requires Openness to be registered with Windows. TIA Portal setup does this automatically (`Siemens.Engineering.dll` is registered as a COM server during installation). If registration is missing (`check_environment` → `opennessInstalled: false`), the fix is a TIA Portal repair installation. The user is directed to Siemens support article [109750301](https://support.industry.siemens.com).

---

## 11. Testing strategy

| Level | What | Tooling |
|-------|------|---------|
| Unit | DTO serialization, error mapping, path resolution | xUnit + FluentAssertions |
| Integration | Openness API calls against a real TIA installation | xUnit `[Fact(Skip="Requires TIA")]` — run manually on dev machine |
| MCP | Protocol correctness (discovery, tool call, structured error) | MCP Inspector (manual); automated `McpServer` test host |
| E2E | Full export → edit → import → compile cycle on test project | Manual Spike B; later automated via Agent test script |

**Integration test fixture:**

```csharp
// Requires: TIA Portal installed + test .ap17 project at known path
// Skip unless env var PLCAI_TIA_TEST_PROJECT is set
public class TiaV17AdapterIntegrationTests
{
    [Fact]
    public void ExportAndImport_RoundTrip_PreservesLogic()
    {
        // Arrange
        var adapter = new TiaV17Adapter();
        adapter.OpenProject(TestProjectPath, withUI: false);

        // Act — export
        var export = adapter.ExportBlock("TestFB");
        Assert.NotNull(export.XmlContent);

        // Act — modify comment
        var modified = InsertComment(export.XmlContent, "en-US", "Test comment");

        // Act — import
        var result = adapter.ImportBlock("TestFB", modified);

        // Assert
        Assert.True(result.Success);
        Assert.Empty(result.Warnings);

        // Cleanup — verify
        var reExported = adapter.ExportBlock("TestFB");
        Assert.Equal(export.InterfaceXml, reExported.InterfaceXml); // interface unchanged
    }
}
```

---

## 12. Risks & mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| Openness COM crashes on corrupt XML | MCP server process dies | Import XML is validated by `src_validate` before reaching engineering MCP; this contract is documented in every import tool |
| TIA Portal licensing — headless instances consume a license seat | User runs out of licenses | Document this clearly; prefer attach-to-existing-session where possible; headless connection checks license seat availability if Openness API exposes it |
| Multi-user project locking conflicts | Import fails with opaque error | Adapter returns user-friendly message "Project is locked by [user]" if Openness provides the info; otherwise generic "open failed" + suggest checking other TIA instances |
| Openness API behaviour differs between TIA V17 minor updates (updates 1–7) | Export/import may fail on untested update | Test against the installed version (V17 Update 7); document tested version in `check_environment` |
| Headless portal process leak on crash | Zombie Siemens.TIA.Portal.exe accumulates | Finalizer in `TiaV17Adapter.Dispose()` calls `portal.Dispose()`; server main loop traps `AppDomain.CurrentDomain.ProcessExit` and `Console.CancelKeyPress` to clean up |

---

## 13. Key implementation patterns from Siemens Openness demo

The Siemens TIA Portal Openness Demo (`TiaPortalOpennessDemo_SourceCode`) served as the reference implementation for extracting these patterns. Below are the critical coding patterns the `TiaV17Adapter` must replicate.

### 13.1 Assembly resolution via `AppDomain.AssemblyResolve`

Siemens.Engineering DLLs are **not** distributed via NuGet — they are installed by TIA Portal and registered in the Windows registry. The runtime must be redirected to the correct version-specific path.

```csharp
// Static resolver registered in AppDomain
public static class Resolver
{
    public static Assembly OnResolve(object sender, ResolveEventArgs args)
    {
        var assemblyName = new AssemblyName(args.Name).Name;
        if (assemblyName.StartsWith("Siemens.Engineering"))
        {
            var path = GetOpennessLibraryPath(assemblyName);
            return Assembly.LoadFrom(path);
        }
        return null;
    }

    private static string GetOpennessLibraryPath(string assemblyName)
    {
        // Reads from registry:
        // HKLM\SOFTWARE\Siemens\Automation\Openness\{Version}\PublicAPI\{ApiVersion}\Siemens.Engineering
        var opennessKey = @"SOFTWARE\Siemens\Automation\Openness";
        using var baseKey = Registry.LocalMachine.OpenSubKey(opennessKey);
        // ... enumerate versions, find matching assembly path
    }
}
```

**Registration in adapter bootstrap:**

```csharp
// Must be registered BEFORE any Siemens.Engineering type is touched
AppDomain.CurrentDomain.AssemblyResolve += Resolver.OnResolve;
```

This is **not optional** — without it the Siemens DLLs cannot be loaded at all. The resolver must be the first thing initialized in the MCP server startup, before any tool handler runs.

### 13.1a `TiaPortalProcess` lifetime — do NOT dispose enumeration results (verified 2026-07-17)

`TiaSessionEnumerator` originally disposed the `TiaPortalProcess` objects returned by `TiaPortal.GetProcesses()` after reading their properties ("snapshot only"). This was the root cause of a long debugging detour:

- Disposing enumerated process objects **poisons the process-wide Openness acquisition context**: every later `TiaPortal.GetProcess(pid, timeout)` returns **null** (no exception), and `Attach()` on that null then throws a bare `NullReferenceException` with a stack containing only the caller's frame.
- Worse, UI portal instances that went through this died **silently** minutes later (Siemens diagnostics report `a187d3a6…`, Reason "Openness", faulting while closing the project). A fresh UI left untouched survives indefinitely (6+ min control watch).

Verified by bisection (standalone C# probe, PowerShell probe, and a host-stripped server build all attached successfully from MTA *and* STA threads; the only failing configuration was `list_sessions`-with-dispose followed by `connect`). Thread apartment is **irrelevant** — an earlier "STA required" theory was wrong, and the `StaAdapter`/`StaWorker` scaffolding built for it was removed.

**Rules:**
1. Never dispose `TiaPortalProcess` objects obtained from `GetProcesses()`/`GetProcess()` — leave them to the GC.
2. Always null-check the `GetProcess()` result; null means "not acquirable", and the adapter retries the whole `GetProcess` + `Attach` sequence (3 attempts, 5s apart).
3. Do not deliberately probe this behavior against a user's session — poisoned sessions can take the portal down.

### 13.2 Registry-based version discovery

Installed TIA Portal / Openness versions are discovered at runtime, not hard-coded:

```csharp
public static IReadOnlyList<OpennessVersion> GetEngineeringVersions()
{
    var versions = new List<OpennessVersion>();
    using var opennessKey = Registry.LocalMachine.OpenSubKey(
        @"SOFTWARE\Siemens\Automation\Openness");

    foreach (var versionSubKey in opennessKey.GetSubKeyNames())
    {
        // versionSubKey e.g. "17.0", "18.0", "19.0", "20.0"
        var apiPath = Path.Combine(opennessKey.ToString(), versionSubKey, "PublicAPI");
        // Reads the PublicAPI subkeys to find the assembly path
        versions.Add(new OpennessVersion { TiaPortalVersion = versionSubKey, ... });
    }
    return versions;
}
```

The `check_environment` tool uses this to report which versions are available and verify the target version (V17) is installed.

### 13.3 Exclusive access + transaction pattern for all writes

Every import, compile, and modification operation must be wrapped in TIA's exclusive access / transaction pattern. **This is not optional** — operations outside a transaction can corrupt the project.

```csharp
public ImportResult ImportBlock(string blockName, string xmlFilePath)
{
    // 1. Acquire exclusive access (prevents other Openness instances from interfering)
    using var exclusiveAccess = tiaPortal.ExclusiveAccess("Block import");

    // 2. Start a transaction on the project
    using var transaction = exclusiveAccess.Transaction(project, "Import block");

    // 3. Perform the operation
    var blockGroup = GetPlcBlockGroup();
    blockGroup.Blocks.Import(new FileInfo(xmlFilePath), ImportOptions.Override);

    // 4. Commit — changes are written to the project file
    //    (CommitOnDispose triggers on using-block exit)
    transaction.CommitOnDispose();
}
```

The demo uses `CommitOnDispose()` as an auto-commit — the transaction commits automatically when the `using` block exits, even on exception (which triggers rollback). This is the recommended pattern. Verified signature: `ExclusiveAccess.Transaction(ITransactionSupport persistence, string undoDescription)` — `Project` implements `ITransactionSupport`.

### 13.4 Software discovery via `GetService<SoftwareContainer>`

TIA's device tree mixes hardware and software items. To find PLC blocks, you must discover whether a device item contains PLC software:

```csharp
public PlcSoftware ResolvePlcSoftware(DeviceItem deviceItem)
{
    // Each device item may or may not contain a software container
    var container = deviceItem.GetService<SoftwareContainer>();
    if (container?.Software is PlcSoftware plcSoftware)
    {
        return plcSoftware;
    }
    return null;
}

// Then access blocks:
var blocks = plcSoftware.BlockGroup.Blocks;          // PlcBlockGroup
var tagTables = plcSoftware.TagTableGroup.TagTables;  // PlcTagTableGroup
var types = plcSoftware.TypeGroup.Types;              // PlcTypeGroup
```

Tree traversal pattern from the demo:

```csharp
// Walk project devices to find all PLC software
foreach (Device device in project.Devices)
{
    foreach (DeviceItem deviceItem in device.DeviceItems)
    {
        var sw = deviceItem.GetService<SoftwareContainer>()?.Software;
        if (sw is PlcSoftware plc)
        {
            foreach (PlcBlock block in plc.BlockGroup.Blocks)
            {
                // Process block
            }
        }
    }
}
```

Verified namespaces & shape (2026-07-17): `Device`/`DeviceItem` → `Siemens.Engineering.HW`; `SoftwareContainer` → `Siemens.Engineering.HW.Features`; `PlcSoftware` → `Siemens.Engineering.SW`; nested block groups use `PlcBlockUserGroupComposition` (there is NO `PlcBlockGroupComposition` type); block kind is the concrete `PlcBlock` subclass name — there is no `Type`/`BlockType` property on `PlcBlock`.

### 13.5 Exception wrapping

Siemens.Engineering throws two exception types that must be handled differently, plus COM interop failures:

| Exception | Meaning | MCP mapping (§8.1) |
|-----------|---------|--------------------|
| `NonRecoverableException` | State may be corrupted — TIA Portal may need restart | `NON_RECOVERABLE` — caller should save work and restart TIA |
| `EngineeringException` | Recoverable — last valid state is intact | `OPENNESS_ERROR` — operation can be retried |
| `COMException` | COM interop failure (TIA not running / Openness API missing) | `TIA_NOT_INSTALLED` or `OPENNESS_ERROR`, as appropriate |

Per §8.1, the handler produces structured `isError: true` tool results carrying these string codes — not `McpProtocolException` with JSON-RPC codes:

```csharp
public static class OpennessExceptionHandler
{
    // Returns the structured error payload for an isError tool result (§8.1)
    public static ToolError Map(Exception ex)
    {
        return ex switch
        {
            NonRecoverableException nre => new ToolError(
                "NON_RECOVERABLE",
                "TIA Openness encountered a non-recoverable error. " + nre.Message,
                "Save your work and restart TIA Portal."),

            EngineeringException ee => new ToolError(
                "OPENNESS_ERROR",
                "Openness API error (recoverable): " + ee.Message,
                "Retry the operation; if it persists, reconnect."),

            COMException ce => new ToolError(
                "TIA_NOT_INSTALLED",
                "COM interop with TIA Portal failed. Is TIA running? " + ce.Message,
                "Run check_environment; repair the TIA/Openness installation if missing."),

            _ => new ToolError(
                "OPENNESS_ERROR",
                "Unexpected error: " + ex.Message,
                "See server logs for details.")
        };
    }
}
```

### 13.6 Block export safety: `IsConsistent` check

Before calling `Export()` on a `PlcBlock`, you **must** check `block.IsConsistent`. An inconsistent block (one with compile errors or open editor changes) cannot be exported:

```csharp
if (!block.IsConsistent)
{
    return ExportResult.Failure(blockName,
        $"Block '{blockName}' is inconsistent. Compile it first before export.");
}

block.Export(new FileInfo(exportPath), ExportOption.WithDefaults);
```

### 13.7 Version-specific implementation separation

When adding support for multiple TIA versions (V18, V19, V20), each version gets its own folder with a dedicated `extern alias` and the full implementation:

```
TiaV17Adapter/
├── TiaPortalServiceProvider.cs       (uses Siemes.Engineering V17 DLL)
├── ProjectServiceProvider.cs
├── Step7Service.cs
└── EngineeringProjectModel.cs

TiaV18Adapter/   (future)
├── TiaPortalServiceProvider.cs       (uses Siemens.Engineering V18 DLL via extern alias)
└── ...
```

The `IEngineeringPlatform` interface (§14) remains version-agnostic — only the implementation changes. At runtime, the adapter selects the correct implementation based on `check_environment` results.

### 13.8 Trace logging for diagnostics

Every Siemens.Engineering call should be wrapped with diagnostic logging for troubleshooting (especially since Openness errors are opaque):

```csharp
public ExportResult ExportBlock(string blockName, string outputDir)
{
    var logId = Guid.NewGuid();
    logger.LogTrace("[{LogId}] ExportBlock start: block={BlockName}, dir={OutputDir}",
        logId, blockName, outputDir);

    try
    {
        // ... Siemens API call ...
        logger.LogTrace("[{LogId}] ExportBlock success", logId);
        return result;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "[{LogId}] ExportBlock failed: {Message}", logId, ex.Message);
        throw;  // let the caller map the exception
    }
}
```

The logging should go to stderr (never stdout) to avoid corrupting the MCP JSON-RPC channel.

---

## 14. Future adapter contract notes

`TiaV17Adapter` implements `IEngineeringPlatform` from `Contracts`:

```csharp
// Contracts / IEngineeringPlatform.cs (netstandard2.0)
public interface IEngineeringPlatform : IDisposable
{
    EnvCheckResult CheckEnvironment();
    SessionInfo[] ListSessions();
    ConnectionInfo Connect(ConnectOptions options);
    // Reports unsaved changes (IsModified); never saves implicitly (§1.1).
    DisconnectResult Disconnect();
    void SaveProject();

    BlockInfo[] ListBlocks(string plcName = null);
    ExportResult ExportBlock(string blockName, string outputDir);
    ExportResult[] ExportAllBlocks(string outputDir);
    ImportResult ImportBlock(string blockName, string xmlFilePath);

    CompileResult CompileBlock(string blockName);
    CompileResult CompilePlcSoftware();

    ProjectInfo GetProjectInfo();
}
```

When Rockwell ControlLogix (L5X) support is built later, `RockwellL5xAdapter : IEngineeringPlatform` will implement the same contract — the MCP server itself needs no code changes, only the adapter class registration.

---

## 15. Build order (within Phase 0–1)

1. **Scaffold** `Mcp.Engineering` (net48 console + MCP SDK) and `Contracts` (DONE 2026-07-17)
2. **Spike A** — `list_sessions` via MCP Inspector (proves MCP+net48+Openness works) (DONE 2026-07-17 — PASSED)
3. **API verification pass** — reflect over the installed V17 `Siemens.Engineering.dll` (and Openness docs on disk) to pin the real signatures this doc flagged as to-verify: process attach (GetProcesses/Attach), TiaPortalMode values, Projects.Open, Export/Import options, IsConsistent semantics, ICompilable/CompilerResult shape (synchronous? granularity?). Amend this doc where reality differs. (DONE 2026-07-17 — verified reference: `buildnote/bestpractice/openness-v17-api-surface.md`)
4. **Implement** `TiaV17Adapter` full connect/disconnect (attach + headless open) (DONE 2026-07-17)
5. **Implement** export (single + all blocks) with working folder structure (DONE 2026-07-17)
6. **Spike B** — manual XML round-trip on the test project `C:\Users\Ansel\Documents\Automation\V17\AgentAssistProgramming\TestPLCExportDemo` (proves XML edit → import → verify) (DONE 2026-07-17 — PASSED; diff vs original = only `<Created>` timestamp + the edited comment, byte-stable)
7. **Implement** import with safety guards (§6.1) (DONE 2026-07-17)
8. **Implement** compile tools (DONE 2026-07-17)
9. **Add** `check_environment` + `get_project_info` (diagnostic always useful) (DONE 2026-07-17)
10. **Integration tests** on real TIA installation (DONE 2026-07-17 — headless E2E walkthrough passed)
11. **MCP Inspector E2E walkthrough** — full export → edit → import → compile cycle (DONE 2026-07-17 headless; attached-mode walkthrough DONE 2026-07-17 after fixing the `TiaPortalProcess.Dispose` poisoning — §13.1a)
