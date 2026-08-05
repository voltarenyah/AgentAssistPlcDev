# TIA Consistency and Selective Synchronization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the worktree-level checksum fast gate, race-safe full XML scan, object comparison, selective TIA-to-master synchronization, and immutable `tia-sync` evidence.

**Architecture:** The Engineering MCP exposes live compiled checksums independently of export manifests. `PlcSourceScanner` exports each required device into ignored staging, fingerprints normalized XML, and rejects scans whose checksum changes. `WorkbenchConsistencyService` reads master validation evidence for the fast path and owns selective synchronization plus hash-bound master commit authorization.

**Tech Stack:** Siemens TIA Openness V17/.NET Framework 4.8, shared .NET contracts, C#/.NET 8 coordinator, MCP tool calls, xUnit, ASP.NET minimal APIs.

---

## Execution relationship

This is plan 2 of 4. It requires the single `SourceRoot`, filtered Git status, selected commit API, and validation-tag store from plan 1. It does not import feature objects or merge branches; those operations arrive in plan 3.

## File responsibility map

- `src/Contracts/Engineering/PlcChecksumInfo.cs`: live per-device checksum contract.
- `src/Contracts/IEngineeringPlatform.cs`: adapter method for checksum reads.
- `src/Mcp.Engineering/Adapter/TiaV17Adapter.cs`: guarded `PlcChecksumProvider.Software` access.
- `src/Mcp.Engineering/Tools/EngineeringTools.cs`: read-only `get_plc_checksums` MCP tool.
- `src/Agent/Workbench/SourceTreeReader.cs`: deterministic local/staging XML identities and normalized fingerprints.
- `src/Agent/Workbench/PlcSourceScanner.cs`: full export with before/after checksum race guard.
- `src/Agent/Workbench/ConsistencyModels.cs`: fast-gate, scan, object status, and authorization records.
- `src/Agent/Workbench/WorkbenchConsistencyService.cs`: orchestration across every workbench device.
- `src/Agent/Workbench/WorkbenchWritePolicy.cs`: pending master synchronization authorization.
- `src/Agent/Workbench/WorkbenchCoordinator.cs`: public compare, apply, commit, and label workflows.
- `src/ApiHost/WorkbenchApiModels.cs`: consistency and synchronization endpoints.

### Task 1: Expose live compiled PLC checksums without manifests

**Files:**
- Create: `src/Contracts/Engineering/PlcChecksumInfo.cs`
- Modify: `src/Contracts/IEngineeringPlatform.cs`
- Modify: `src/Mcp.Engineering/Adapter/TiaV17Adapter.cs`
- Modify: `src/Mcp.Engineering/Tools/EngineeringTools.cs`
- Modify: `src/Mcp.Engineering/Sandbox/EngineeringGuard.cs`
- Create: `tests/Mcp.Engineering.Tests/PlcChecksumContractTests.cs`
- Modify: `tests/Agent.Tests/McpToolCatalogTests.cs`

- [ ] **Step 1: Write the failing tool contract test**

```csharp
[Fact]
public void EngineeringSurfaceExposesReadOnlyPlcChecksums()
{
    var method = typeof(EngineeringTools).GetMethod(nameof(EngineeringTools.GetPlcChecksums));
    Assert.NotNull(method);
    var attribute = Assert.Single(method!.GetCustomAttributes<McpServerToolAttribute>());
    Assert.Equal("get_plc_checksums", attribute.Name);
}
```

Add a sandbox parity test asserting `get_plc_checksums` is read-only and accepts no caller-controlled filesystem path.

- [ ] **Step 2: Run the Engineering tests and verify failure**

Run: `dotnet test tests/Mcp.Engineering.Tests/Mcp.Engineering.Tests.csproj --no-restore --filter "FullyQualifiedName~PlcChecksumContractTests"`

Expected: FAIL because the tool does not exist.

- [ ] **Step 3: Add the contract and adapter operation**

```csharp
public sealed class PlcChecksumInfo
{
    public string PlcName { get; set; } = string.Empty;
    public string ProjectIdentity { get; set; } = string.Empty;
    public string? SoftwareChecksum { get; set; }
    public bool IsCompiled => !string.IsNullOrWhiteSpace(SoftwareChecksum);
}
```

Add to `IEngineeringPlatform`:

```csharp
PlcChecksumInfo[] GetPlcChecksums(string? plcName = null);
```

In `TiaV17Adapter`, resolve one PLC when `plcName` is supplied or enumerate every PLC otherwise. Return project path/name as `ProjectIdentity` and the guarded `TryReadSoftwareChecksum` value. Never write metadata or export files.

Expose:

```csharp
[McpServerTool(Name = "get_plc_checksums")]
[Description("Read the current compiled software checksum for one or all PLC devices. No exports or writes.")]
public CallToolResult GetPlcChecksums(string? plcName = null) =>
    Invoke("get_plc_checksums", () => _adapter.GetPlcChecksums(plcName));
```

- [ ] **Step 4: Run Engineering and Agent catalog tests**

Run: `dotnet test tests/Mcp.Engineering.Tests/Mcp.Engineering.Tests.csproj --no-restore`

Run: `dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-restore --filter "FullyQualifiedName~McpToolCatalogTests"`

Expected: PASS.

- [ ] **Step 5: Commit checksum access**

```bash
git add src/Contracts src/Mcp.Engineering tests/Mcp.Engineering.Tests tests/Agent.Tests/McpToolCatalogTests.cs
git commit -m "feat: expose live PLC software checksums"
```

### Task 2: Build deterministic XML source snapshots

**Files:**
- Create: `src/Agent/Workbench/SourceTreeReader.cs`
- Create: `src/Agent/Workbench/ConsistencyModels.cs`
- Create: `src/Mcp.Engineering/Export/SourceExportPath.cs`
- Create: `tests/Agent.Tests/SourceTreeReaderTests.cs`
- Create: `tests/Mcp.Engineering.Tests/SourceExportPathTests.cs`
- Modify: `src/Mcp.Engineering/Adapter/TiaV17Adapter.cs`
- Modify: `src/Contracts/Engineering/XmlCompare.cs`
- Modify: `tests/Contracts.Tests/XmlCompareTests.cs`

- [ ] **Step 1: Write failing normalization and tree-reader tests**

```csharp
[Theory]
[InlineData("<Created>one</Created>", "<Created>two</Created>")]
[InlineData("  <Created>one</Created>\r\n<X />", "  <Created>two</Created>\n<X />")]
public void TimestampAndLineEndingDifferencesHaveTheSameFingerprint(string left, string right)
{
    Assert.Equal(XmlContentHash.Compute(left), XmlContentHash.Compute(right));
}

[Fact]
public void ReadReturnsOnlyXmlWithStableIdentityAndSortedPaths()
{
    Write("Tags/Z.xml", "<Document><SW.Tags.PlcTagTable ID=\"1\" /></Document>");
    Write("Blocks/A.xml", "<Document><SW.Blocks.OB ID=\"2\" /></Document>");
    Write("metadata.json", "{}");

    var objects = new SourceTreeReader().Read(root);

    Assert.Equal(["Blocks/A.xml", "Tags/Z.xml"], objects.Select(x => x.RelativePath));
    Assert.All(objects, item => Assert.Equal(64, item.Sha256.Length));
}

[Theory]
[InlineData("Blocks", "Area/Conveyors", "Main [OB1].xml", "Blocks/Area/Conveyors/Main [OB1].xml")]
[InlineData("Tags", "LineA", "Inputs.xml", "Tags/LineA/Inputs.xml")]
[InlineData("UDT", null, "Motor.xml", "UDT/Motor.xml")]
public void ExportPathPreservesPlcGroupHierarchy(
    string category, string? groupPath, string fileName, string expected)
{
    Assert.Equal(expected, SourceExportPath.Build(category, groupPath, fileName));
}
```

- [ ] **Step 2: Run focused tests and verify failure**

Run: `dotnet test tests/Contracts.Tests/Contracts.Tests.csproj --no-restore --filter "FullyQualifiedName~TimestampAndLineEnding"`

Run: `dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-restore --filter "FullyQualifiedName~SourceTreeReaderTests"`

Run: `dotnet test tests/Mcp.Engineering.Tests/Mcp.Engineering.Tests.csproj --no-restore --filter "FullyQualifiedName~SourceExportPathTests"`

Expected: the reader and export-path tests fail because the types do not exist.

- [ ] **Step 3: Add snapshot records and reader**

```csharp
public sealed record SourceObjectSnapshot(
    string Identity,
    string RelativePath,
    string Category,
    string Name,
    string Sha256,
    long Length);

public sealed record DeviceSourceSnapshot(
    string DeviceId,
    string PlcName,
    string ProjectIdentity,
    string ProjectChecksum,
    IReadOnlyList<SourceObjectSnapshot> Objects);
```

`SourceTreeReader.Read(root)` recursively reads only `.xml`, rejects reparse points, validates every path below the prevalidated root, parses the first supported Siemens object element to determine category/name, falls back to directory category and filename, computes lowercase hexadecimal SHA-256 over `XmlCompare.Normalize`, and sorts by relative path.

- [ ] **Step 4: Preserve group paths in every full export**

`SourceExportPath.Build` validates each group segment, rejects rooted/traversal segments, and combines category, group path, and filename with `/`. Change block, DB, tag-table, and UDT export helpers to receive their enumerator `groupPath` and write under that hierarchy. The manifest may still record staging diagnostics, but the XML path itself is the stable object identity. This also prevents same-named objects in different PLC groups from overwriting each other.

- [ ] **Step 5: Run Contracts, Engineering, and Agent tests**

Run: `dotnet test tests/Contracts.Tests/Contracts.Tests.csproj --no-restore`

Run: `dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-restore --filter "FullyQualifiedName~SourceTreeReaderTests"`

Run: `dotnet test tests/Mcp.Engineering.Tests/Mcp.Engineering.Tests.csproj --no-restore --filter "FullyQualifiedName~SourceExportPathTests"`

Expected: PASS.

- [ ] **Step 6: Commit deterministic source snapshots**

```bash
git add src/Contracts src/Agent/Workbench/SourceTreeReader.cs src/Agent/Workbench/ConsistencyModels.cs src/Mcp.Engineering/Export/SourceExportPath.cs src/Mcp.Engineering/Adapter/TiaV17Adapter.cs tests/Contracts.Tests tests/Agent.Tests/SourceTreeReaderTests.cs tests/Mcp.Engineering.Tests/SourceExportPathTests.cs
git commit -m "feat: fingerprint PLC XML source trees"
```

### Task 3: Implement race-safe full device scanning

**Files:**
- Create: `src/Agent/Workbench/PlcSourceScanner.cs`
- Create: `tests/Agent.Tests/PlcSourceScannerTests.cs`
- Modify: `src/Agent/Workbench/SafeDeviceExportStager.cs`
- Modify: `src/Contracts/Engineering/SyncResult.cs`
- Modify: `src/Mcp.Engineering/Adapter/TiaV17Adapter.cs`

- [ ] **Step 1: Write failing scanner tests**

```csharp
[Fact]
public async Task ScanExportsThenReturnsStableNormalizedObjects()
{
    engineering
        .Respond("get_plc_checksums", Checksums("same"))
        .Respond("rebuild_export", ExportIntoStaging("Blocks/Main.xml", Xml("one")))
        .Respond("get_plc_checksums", Checksums("same"));

    var result = await scanner.ScanAsync(context, CancellationToken.None);

    Assert.Equal("same", result.ProjectChecksum);
    Assert.Single(result.Objects);
    Assert.Equal(
        ["get_plc_checksums", "rebuild_export", "get_plc_checksums"],
        engineering.Calls);
}

[Fact]
public async Task ScanRejectsChecksumMovementDuringExport()
{
    engineering
        .Respond("get_plc_checksums", Checksums("before"))
        .Respond("rebuild_export", ExportIntoStaging("Blocks/Main.xml", Xml("one")))
        .Respond("get_plc_checksums", Checksums("after"));

    var error = await Assert.ThrowsAsync<ReconciliationException>(() =>
        scanner.ScanAsync(context, CancellationToken.None));

    Assert.Equal("TIA_CHANGED_DURING_SCAN", error.Code);
}

[Fact]
public async Task ScanReportsObjectsThatOpennessCannotExport()
{
    ScriptStableChecksums("same");
    engineering.Respond("rebuild_export", RebuildWithUnsupported("F_Main", "FailSafeBlock"));

    var result = await scanner.ScanAsync(context, CancellationToken.None);

    var unsupported = Assert.Single(result.UnsupportedObjects);
    Assert.Equal("F_Main", unsupported.Name);
    Assert.Equal("TIA_EXPORT_UNSUPPORTED", unsupported.Reason);
}
```

- [ ] **Step 2: Run tests and verify failure**

Run: `dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-restore --filter "FullyQualifiedName~PlcSourceScannerTests"`

Expected: FAIL because `PlcSourceScanner` does not exist.

- [ ] **Step 3: Implement the scan sequence**

`ScanAsync` must hold the engineering-session semaphore and device operation lock, read the selected PLC checksum, reject a null checksum with `PLC_CHECKSUM_UNAVAILABLE`, use `SafeDeviceExportStager` for a full `rebuild_export` into a fresh staging tree, read the checksum again, compare ordinally, then read XML through `SourceTreeReader`. Manifests produced inside staging are tolerated but never copied or fingerprinted.

Extend `SyncResult` with an `Unsupported` collection and make `rebuild_export` report fail-safe or otherwise non-exportable PLC objects instead of only logging/skipping them. Carry these entries into `DeviceScanResult.UnsupportedObjects`. Comparison may display them, but no exact consistency evidence can be created while source coverage is incomplete.

Return:

```csharp
public sealed record DeviceScanResult(
    string DeviceId,
    string PlcName,
    string ProjectIdentity,
    string ProjectChecksum,
    IReadOnlyList<SourceObjectSnapshot> Objects,
    IReadOnlyList<UnsupportedSourceObject> UnsupportedObjects,
    string CompletedAt);
```

- [ ] **Step 4: Run scanner and stager tests**

Run: `dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-restore --filter "FullyQualifiedName~PlcSourceScannerTests|FullyQualifiedName~SafeDeviceExportStager"`

Expected: PASS.

- [ ] **Step 5: Commit the full scanner**

```bash
git add src/Agent/Workbench/PlcSourceScanner.cs src/Agent/Workbench/SafeDeviceExportStager.cs tests/Agent.Tests/PlcSourceScannerTests.cs
git commit -m "feat: scan TIA source with checksum race protection"
```

### Task 4: Implement the worktree-level fast gate and comparison

**Files:**
- Create: `src/Agent/Workbench/WorkbenchConsistencyService.cs`
- Create: `tests/Agent.Tests/WorkbenchConsistencyServiceTests.cs`
- Modify: `src/Agent/Workbench/ConsistencyModels.cs`
- Modify: `src/Agent/Workbench/WorkbenchCoordinator.cs`

- [ ] **Step 1: Write failing fast-gate tests**

```csharp
[Fact]
public async Task MatchingValidationChecksumsSkipEveryExport()
{
    versionControl.Respond("vc_validation_get", Validation("PLC_1", "one", "PLC_2", "two"));
    engineering.Respond("get_plc_checksums", Checksums(("PLC_1", "one"), ("PLC_2", "two")));

    var result = await service.CompareAsync(workbench, master, CancellationToken.None);

    Assert.Equal(ConsistencyState.Consistent, result.State);
    Assert.True(result.FastGatePassed);
    Assert.DoesNotContain("rebuild_export", engineering.Calls);
}

[Fact]
public async Task UnlabeledMasterScansEveryDevice()
{
    versionControl.Respond<VcValidationEvidence?>("vc_validation_get", null);
    ScriptStableScan("PLC_1", "one");
    ScriptStableScan("PLC_2", "two");

    var result = await service.CompareAsync(workbench, master, CancellationToken.None);

    Assert.False(result.FastGatePassed);
    Assert.Equal(2, engineering.Calls.Count(call => call == "rebuild_export"));
}

[Fact]
public async Task DirtyMasterSourceCannotPassFastGate()
{
    versionControl.Respond("vc_validation_get", Validation("PLC_1", "one", "PLC_2", "two"));
    versionControl.Respond("vc_status", Status(Changed("devices/PLC_1/source/Blocks/Main.xml")));
    engineering.Respond("get_plc_checksums", Checksums(("PLC_1", "one"), ("PLC_2", "two")));
    ScriptStableScan("PLC_1", "one");
    ScriptStableScan("PLC_2", "two");

    var result = await service.CompareAsync(workbench, master, CancellationToken.None);

    Assert.False(result.FastGatePassed);
    Assert.Equal(2, engineering.Calls.Count(call => call == "rebuild_export"));
}
```

Add a mixed case proving a matching device skips export while a mismatching device scans, but the workbench state is not `Consistent` until all devices agree.

- [ ] **Step 2: Run tests and verify failure**

Run: `dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-restore --filter "FullyQualifiedName~WorkbenchConsistencyServiceTests"`

Expected: FAIL because the service and result model do not exist.

- [ ] **Step 3: Add comparison models**

```csharp
public enum ConsistencyState { Consistent, Different, ScanRequired, Unavailable }
public enum SourceDifferenceKind { Unchanged, Changed, Added, Deleted }

public sealed record SourceDifference(
    string DeviceId,
    string PlcName,
    string RelativePath,
    string Identity,
    SourceDifferenceKind Kind,
    string? MasterFingerprint,
    string? TiaFingerprint,
    bool Supported);

public sealed record WorkbenchConsistencyResult(
    string ComparisonId,
    string MasterSha,
    bool FastGatePassed,
    ConsistencyState State,
    IReadOnlyDictionary<string, string?> LiveChecksums,
    IReadOnlyList<SourceDifference> Differences);
```

Compare against current master source in its linked checkout. A fast-gate pass requires all three conditions: the evidence targets current master `HEAD`; filtered source status is clean, which proves the checked-out XML still has the commit fingerprints represented by that evidence; and every live device checksum equals its evidence checksum. For a labeled master, call `get_plc_checksums` once for all devices. If all conditions hold, return immediately. Scan only checksum-mismatched devices when the evidence and source checkout are valid; scan every device for unlabeled/invalid evidence or dirty master source. Report dirty master paths separately as unauthorized local changes. Persist detailed comparison data under `.automation/comparisons/<id>.json`, not in Git.

- [ ] **Step 4: Expose coordinator methods**

Add `CompareMasterWithTiaAsync(workbenchId, token, progress)` and `GetComparison(workbenchId, comparisonId)`. Always resolve the registered master worktree even when the selected worktree is a feature.

- [ ] **Step 5: Run Agent tests**

Run: `dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-restore`

Expected: PASS.

- [ ] **Step 6: Commit fast-gate comparison**

```bash
git add src/Agent tests/Agent.Tests
git commit -m "feat: compare master with TIA using checksum fast gate"
```

### Task 5: Add selective TIA synchronization and protected manual master commits

**Files:**
- Modify: `src/Agent/Workbench/WorkbenchWritePolicy.cs`
- Modify: `src/Agent/Workbench/WorkbenchCoordinator.cs`
- Create: `tests/Agent.Tests/MasterSynchronizationTests.cs`
- Modify: `tests/Agent.Tests/WorkbenchWritePolicyTests.cs`

- [ ] **Step 1: Write failing selective-sync tests**

```csharp
[Fact]
public async Task ApplyCopiesOnlySelectedChangedObjectsAndDoesNotCommit()
{
    var comparison = fixture.Comparison(
        Changed("dev-1", "Blocks/A.xml", "old-a", "new-a"),
        Changed("dev-1", "Blocks/B.xml", "old-b", "new-b"));

    var result = await coordinator.ApplyTiaSynchronizationAsync(
        fixture.WorkbenchId,
        comparison.ComparisonId,
        ["devices/PLC_1/source/Blocks/A.xml"],
        CancellationToken.None);

    Assert.Equal(["devices/PLC_1/source/Blocks/A.xml"], result.PendingPaths);
    Assert.Equal("new A", File.ReadAllText(fixture.MasterSource("Blocks/A.xml")));
    Assert.Equal("old B", File.ReadAllText(fixture.MasterSource("Blocks/B.xml")));
    Assert.DoesNotContain("vc_commit_selected", versionControl.Calls);
}

[Fact]
public async Task MasterCommitRejectsAFileChangedAfterAuthorization()
{
    await fixture.AuthorizeFromTiaAsync("Blocks/A.xml");
    File.WriteAllText(fixture.MasterSource("Blocks/A.xml"), "local edit");

    var error = await Assert.ThrowsAsync<WorkbenchLifecycleException>(() =>
        coordinator.CommitSourceAsync(
            fixture.WorkbenchId,
            fixture.MasterWorktreeId,
            ["devices/PLC_1/source/Blocks/A.xml"],
            "accept A",
            CancellationToken.None));

    Assert.Equal("MASTER_CHANGE_NOT_AUTHORIZED", error.Code);
}
```

- [ ] **Step 2: Run tests and verify failure**

Run: `dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-restore --filter "FullyQualifiedName~MasterSynchronizationTests"`

Expected: FAIL because selective synchronization and pending authorization do not exist.

- [ ] **Step 3: Persist hash-bound authorization**

Use ignored `.automation/pending-master-sync.json` with:

```csharp
public sealed record PendingMasterSource(
    string RelativePath,
    string ComparisonId,
    string MasterHeadSha,
    string TiaFingerprint,
    string CopiedFileFingerprint);

public sealed record PendingMasterSynchronization(
    string SchemaVersion,
    string WorktreeId,
    IReadOnlyList<PendingMasterSource> Sources);
```

`ApplyTiaSynchronizationAsync` accepts only `Changed` or `Added` entries selected from the stored comparison, copies each staging XML atomically, and records the resulting hash. It rejects `Deleted` with `SOURCE_DELETE_UNSUPPORTED`. It never commits.

`CommitSourceAsync` allows arbitrary selected source changes on feature branches. On master, every path must have pending authorization, current file hash must equal `CopiedFileFingerprint`, and current HEAD must equal `MasterHeadSha` except for earlier commits from the same pending set. After `vc_commit_selected`, remove committed authorizations and update the remaining entries to the new HEAD.

- [ ] **Step 4: Cover partial commits**

Add a test authorizing A and B, committing A, then committing B. Both commits succeed; neither receives validation evidence automatically; no unselected file is staged or committed.

- [ ] **Step 5: Run Agent and version-control tests**

Run: `dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-restore`

Run: `dotnet test tests/Mcp.VersionControl.Tests/Mcp.VersionControl.Tests.csproj --no-restore`

Expected: PASS.

- [ ] **Step 6: Commit selective synchronization**

```bash
git add src/Agent tests/Agent.Tests
git commit -m "feat: authorize selective TIA source commits"
```

### Task 6: Create exact `tia-sync` evidence and API endpoints

**Files:**
- Modify: `src/Agent/Workbench/WorkbenchConsistencyService.cs`
- Modify: `src/Agent/Workbench/WorkbenchCoordinator.cs`
- Modify: `src/ApiHost/WorkbenchApiModels.cs`
- Modify: `tests/Agent.Tests/WorkbenchConsistencyServiceTests.cs`
- Modify: `tests/ApiHost.Tests/WorkbenchEndpointsTests.cs`
- Modify: `studio/src/api/client.ts`

- [ ] **Step 1: Write failing exact-label tests**

```csharp
[Fact]
public async Task ValidateSyncLabelsOnlyAnExactCommittedMaster()
{
    fixture.ScriptExactTwoDeviceScan();
    versionControl
        .Respond("vc_validation_get", (VcValidationEvidence?)null)
        .Respond("vc_validation_create", new { created = true });

    var evidence = await service.ValidateSynchronizedMasterAsync(
        fixture.Workbench,
        fixture.Master,
        "Test User <test@example.local>",
        CancellationToken.None);

    Assert.Equal("tia-sync", evidence.EvidenceKind);
    Assert.False(evidence.MachineValidated);
    Assert.Equal(2, evidence.Devices.Count);
    Assert.Contains("vc_validation_create", versionControl.Calls);
}

[Fact]
public async Task ValidateSyncRejectsAnyRemainingDifference()
{
    fixture.ScriptScanWithDifference("Blocks/A.xml");

    var error = await Assert.ThrowsAsync<WorkbenchLifecycleException>(() =>
        service.ValidateSynchronizedMasterAsync(
            fixture.Workbench,
            fixture.Master,
            "Test User",
            CancellationToken.None));

    Assert.Equal("TIA_MASTER_NOT_EXACT", error.Code);
}
```

- [ ] **Step 2: Run tests and verify failure**

Run: `dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-restore --filter "FullyQualifiedName~ValidateSync"`

Expected: FAIL because exact synchronization labeling is not implemented.

- [ ] **Step 3: Implement exact labeling**

Require a clean master source status, no pending authorization, valid checksums for every device, complete export coverage, and a stable full scan of every device. Compare every scanned object with master. If any fail-safe or otherwise non-exportable object is reported, return `SOURCE_COVERAGE_INCOMPLETE` and list it without creating evidence. Build `VcValidationEvidence` with `EvidenceKind = "tia-sync"`, `MachineValidated = false`, the current HEAD, user identity, checksums, and fingerprints, then call `vc_validation_create`.

- [ ] **Step 4: Add worktree-level API routes**

```text
POST /api/workbenches/{workbenchId}/vc/compare-tia
GET  /api/workbenches/{workbenchId}/vc/comparisons/{comparisonId}
POST /api/workbenches/{workbenchId}/vc/comparisons/{comparisonId}/accept
POST /api/workbenches/{workbenchId}/worktrees/{worktreeId}/vc/commit
POST /api/workbenches/{workbenchId}/vc/validate-sync
```

Use operation-status tracking for scans and validation. The accept body contains exact repository-relative XML paths. The validate body contains the confirming Git identity; do not accept checksums or fingerprints from the client.

- [ ] **Step 5: Update TypeScript contracts**

Add `WorkbenchConsistencyResult`, `SourceDifference`, `PendingSynchronizationResult`, and `VcValidationEvidence` types plus API functions. Do not build the final presentation yet.

- [ ] **Step 6: Run the full solution and Studio contract tests**

Run: `dotnet test AgentAssistPlcDev.sln --no-restore --verbosity minimal`

Run: `npm test` from `studio`

Run: `npm run build` from `studio`

Expected: PASS.

- [ ] **Step 7: Commit TIA consistency workflows**

```bash
git add src/Agent src/ApiHost studio/src/api/client.ts tests/Agent.Tests tests/ApiHost.Tests
git commit -m "feat: synchronize and label exact TIA source"
```

## Plan 2 completion gate

Do not start plan 3 until checksum-equal workbenches skip exports, unlabeled/mismatched devices scan safely, TIA changes can be accepted and manually committed per object, partial master commits remain unlabeled, and only an exact all-device source match can create immutable `tia-sync` evidence.
