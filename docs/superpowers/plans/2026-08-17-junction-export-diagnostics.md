# Junction Export Diagnostics and Fix Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Determine why the short `%TEMP%` junction intermittently fails sandbox validation, make the junction path reliable when the confirmed cause is fixable, and retain the existing normal temporary-directory fallback for environments where junctions remain unsupported.

**Architecture:** Keep the short-path export optimization in `SafeDeviceExportStager`, but make the sandbox’s native junction probe inspect the reparse point itself instead of opening the junction in a way that follows its target. Preserve the security rule that unresolved or escaping links are denied; the existing stager fallback remains available when junction creation or validation cannot be proven safe.

**Tech Stack:** C#/.NET 8, netstandard2.0 Contracts library, Windows junctions via `kernel32.dll`, MCP stdio engineering service, xUnit, ASP.NET API host.

**Execution result:** The production-layout reproduction identified `CreateFile` target-following as the failure. A long junction target caused `ERROR_INVALID_NAME (123)` before the resolver could inspect it. The fix reads `FSCTL_GET_REPARSE_POINT` with `FILE_FLAG_OPEN_REPARSE_POINT`, parses mount-point and symlink targets, and retains detailed probe diagnostics for denied paths.

---

### Task 1: Capture the exact junction failure at the sandbox boundary

**Files:**
- Modify: `src/Contracts/Sandbox/PathJail.cs:104-171`
- Modify: `src/Mcp.Engineering/Sandbox/EngineeringGuard.cs:27-73`
- Test: `tests/Contracts.Tests/PathJailTests.cs`
- Test: `tests/Mcp.Engineering.Tests/ArchiveProjectSandboxTests.cs` or a new focused sandbox test file beside it

- [ ] **Step 1: Add a failing diagnostic test for an unreadable junction target**

Create a junction whose target is removed after the link is created, then assert that `PathJail.Validate` reports the alias path, the reparse-resolution stage, and the native failure category. The test must skip cleanly when the Windows test process cannot create junctions.

```csharp
[Fact]
public void UnresolvedDirectoryLinkReportsResolutionFailure()
{
    if (!OperatingSystem.IsWindows()) return;

    var target = Path.Combine(root, "missing-target");
    var link = Path.Combine(Path.GetTempPath(), "awst-test-" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(target);
    CreateDirectoryJunction(link, target);
    Directory.Delete(target, recursive: true);

    try
    {
        var exception = Assert.Throws<SandboxException>(() => jail.Validate(
            Path.Combine(link, "Blocks", "A.xml"),
            "outputDir"));

        Assert.Contains("outputDir", exception.Message, StringComparison.Ordinal);
        Assert.Contains("reparse point", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot be resolved", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
    finally
    {
        if (Directory.Exists(link)) Directory.Delete(link);
    }
}
```

- [ ] **Step 2: Run the focused test before implementation**

Run:

```powershell
dotnet test tests/Contracts.Tests/Contracts.Tests.csproj --filter "FullyQualifiedName~PathJailTests" -v q
```

Expected: the existing tests pass; the new test establishes the current generic error behavior and identifies the diagnostic assertion that the implementation must satisfy.

- [x] **Step 3: Preserve the native error details**

Change `TryReadLinkTarget` so it distinguishes:

```csharp
internal sealed record LinkTargetProbe(
    string? Target,
    string Failure,
    int Win32Error,
    uint ReturnedLength);
```

Use `Marshal.GetLastWin32Error()` immediately after a failed `CreateFile` or `GetFinalPathNameByHandle` call. Report one of `open-failed`, `target-name-too-long`, `target-name-empty`, or `not-windows` in the `SandboxException` message. Do not allow a failed probe to pass validation.

- [ ] **Step 4: Add the diagnostic details to the engineering audit**

Keep `EngineeringGuard`’s deny decision unchanged, but include the probe failure category and Win32 error in the audited detail. Do not include environment variables or unrelated user data.

- [x] **Step 5: Run the focused tests again**

Run:

```powershell
dotnet test tests/Contracts.Tests/Contracts.Tests.csproj --filter "FullyQualifiedName~PathJailTests" -v q
dotnet test tests/Mcp.Engineering.Tests/Mcp.Engineering.Tests.csproj --filter "FullyQualifiedName~Sandbox" -v q
```

Expected: all focused tests pass and an unresolved junction produces an auditable native failure category.

- [ ] **Step 6: Commit the diagnostic-only change**

```powershell
git add src/Contracts/Sandbox/PathJail.cs src/Mcp.Engineering/Sandbox/EngineeringGuard.cs tests/Contracts.Tests/PathJailTests.cs tests/Mcp.Engineering.Tests
git commit -m "test: diagnose export junction resolution failures"
```

### Task 2: Reproduce the production layout and classify the failure

**Files:**
- Modify: `tests/Agent.Tests/SafeDeviceExportStagerTests.cs` if the existing stager tests are extended
- Test: `tests/Contracts.Tests/PathJailTests.cs`
- Inspect: `src/Agent/Workbench/SafeDeviceExportStager.cs:95-225`
- Inspect: `src/Contracts/Sandbox/PathJail.cs:89-171`

- [x] **Step 1: Add a production-layout junction test**

Use a normal target under an Automation Workbench-shaped root and a short alias under `%TEMP%`, including the hidden `.st-<guid>` directory name used by `SafeDeviceExportStager`. Assert that `PathJail.Validate(alias + "\\Blocks\\A.xml", "outputDir")` returns the alias when the target is inside an allowed root.

- [x] **Step 2: Add a long-target test**

Construct a target path at or above the former 1024-character probe buffer. The pre-fix test reproduced `open-failed; win32Error=123`; the post-fix test resolves the alias successfully.

- [x] **Step 3: Run the production-layout tests**

Run:

```powershell
dotnet test tests/Contracts.Tests/Contracts.Tests.csproj --filter "FullyQualifiedName~PathJailTests" -v q
dotnet test tests/Agent.Tests/Agent.Tests.csproj --filter "FullyQualifiedName~SafeDeviceExportStager" -v q
```

Expected: the normal production layout passes; the long-target test either confirms or rules out the fixed-buffer hypothesis.

- [ ] **Step 4: Reproduce once through the installed app**

After rebuilding only the diagnostic binaries, repeat creation from:

```text
C:\Users\Ansel\Documents\Automation\V17\AgentAssistProgramming\TestPLCExportDemo\TestPLCExportDemo.ap17
```

Record the new `engineering.jsonl` entry for the `rebuild_export` denial. The entry must identify the native failure category and Win32 error.

### Task 3: Apply the smallest confirmed junction fix

**Files:**
- Modify: `src/Contracts/Sandbox/PathJail.cs`
- Modify: `src/Agent/Workbench/SafeDeviceExportStager.cs`
- Test: `tests/Contracts.Tests/PathJailTests.cs`
- Test: `tests/Agent.Tests/SafeDeviceExportStagerTests.cs`

- [x] **Step 1: If the diagnostic proves the fixed buffer is the cause, implement a reparse-point-native probe**

Open the link itself with `FILE_FLAG_OPEN_REPARSE_POINT`, read `FSCTL_GET_REPARSE_POINT`, parse the target from the native reparse buffer, and add a regression test using a target longer than 1024 characters.

- [ ] **Step 2: If the diagnostic proves an access failure, verify the actual identities and ACLs**

Confirm that the API host and engineering child process run under the same Windows identity and that both can traverse the target. Do not weaken the sandbox or whitelist all of `%TEMP%`. If the processes differ, fix the child-process launch identity or use the normal temporary-directory fallback.

- [ ] **Step 3: If the diagnostic proves a target race, verify before sending the MCP call and retry once**

Immediately before `engineering.CallAsync`, verify that the alias is still a directory reparse point and that its resolved target equals the expected `incoming` directory. If verification fails, remove the alias and use the normal temporary-directory fallback for that export.

- [x] **Step 4: Keep the fallback explicit and safe**

The existing stager fallback remains in place when junction creation fails. The PathJail change never accepts an unresolved or escaping link, and no system-wide `%TEMP%` whitelist was added.

- [ ] **Step 5: Add the regression test for the selected fix**

The regression must exercise the exact failure condition identified in Task 2 and assert both security behavior and successful export staging.

- [x] **Step 6: Run focused tests**

```powershell
dotnet test tests/Contracts.Tests/Contracts.Tests.csproj --filter "FullyQualifiedName~PathJailTests" -v q
dotnet test tests/Agent.Tests/Agent.Tests.csproj --filter "FullyQualifiedName~SafeDeviceExportStager" -v q
dotnet test tests/Mcp.Engineering.Tests/Mcp.Engineering.Tests.csproj --filter "FullyQualifiedName~Sandbox" -v q
```

### Task 4: Validate the complete workbench-creation path

**Files:**
- Inspect: `src/Agent/Workbench/WorkbenchCoordinator.cs:311-570`
- Inspect: `src/Agent/Workbench/SafeDeviceExportStager.cs:75-188`
- Inspect: `src/ApiHost/WorkbenchApiModels.cs:400-430`

- [x] **Step 1: Build the solution and engineering service**

```powershell
dotnet build AgentAssistPlcDev.sln -v q
```

Expected: build succeeds with no source or package-lock changes.

- [x] **Step 2: Run the relevant automated suites**

```powershell
dotnet test tests/Contracts.Tests/Contracts.Tests.csproj --no-build -v q
dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-build -v q
dotnet test tests/Mcp.Engineering.Tests/Mcp.Engineering.Tests.csproj --no-build -v q
dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj --no-build -v q
```

- [ ] **Step 3: Run one real creation attempt**

Use the installed or rebuilt app with the same V17 project. Confirm that the audit sequence reaches `rebuild_export` with `decision=allow`, then reaches hardware export and knowledge initialization. Confirm that no `awst-*` junction remains after success or failure.

- [ ] **Step 4: Verify rollback behavior**

Force an export failure in a test fixture and confirm that the workbench rollback leaves no active alias, does not delete an existing staging tree, and does not leave a broken `awst-*` link in `%TEMP%`.

### Task 5: Rebuild and package after runtime validation

**Files:**
- Inspect: `buildnote/guide/installable-package-operations.md`
- Inspect: `build-release.ps1`
- Artifacts: `artifacts/installer/`

- [x] **Step 1: Stop running development and packaged processes**

Use the repository’s documented launcher/process procedure so the API and engineering binaries are not locked during packaging.

- [x] **Step 2: Run the documented release build**

Use the exact command from `buildnote/guide/installable-package-operations.md` and record the generated installer name and SHA-256.

- [x] **Step 3: Validate the installer payload**

Confirm that the packaged API host, engineering service, Contracts assembly, and desktop executable contain the junction diagnostics/fix.

- [ ] **Step 4: Perform an elevated clean-install smoke test**

Blocked in this non-elevated execution environment: the documented silent install returned exit code 2 before copying files. The release payload itself was started successfully on port 5255 with proxy variables cleared. An elevated clean-install and real TIA workbench creation remain to be run on an administrator session.

- [x] **Step 5: Report the result**

Report the confirmed native failure cause, files changed, focused and full test results, installer path, SHA-256, and any remaining fallback conditions.

---

## Plan self-review

- The source-path whitelist is covered by the existing successful `connect` and `save_project_as` audit evidence; no source-path change is required.
- The unresolved-junction case is covered before any production behavior change.
- The fixed-buffer, access, and race hypotheses each have a distinct diagnostic outcome and test path.
- The normal temporary-directory fallback remains available and does not require whitelisting all of `%TEMP%`.
- Existing unrelated desktop proxy changes remain outside this plan.
