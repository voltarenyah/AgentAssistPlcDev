# Validated Worktree Merge Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Import committed non-overlapping feature objects, support user-controlled rollback, compile and verify every device, merge the exact prospective source into master, and publish permanent `feature-merge` validation evidence.

**Architecture:** The version-control service builds a prospective merge tree without moving refs and fingerprints every XML blob in that tree. The coordinator compares current TIA and feature changes against master, records an ignored import session, and runs an all-device compile/final-scan gate. A guarded validated-merge operation creates the no-fast-forward merge and annotated tag together, rolling master back to its original clean SHA if evidence publication fails.

**Tech Stack:** C#/.NET 8, LibGit2Sharp plus guarded Git commands, Siemens TIA Openness V17/.NET Framework 4.8, xUnit, ASP.NET minimal APIs.

---

## Execution relationship

This is plan 3 of 4. It requires plan 1 repository/tag primitives and plan 2 checksum scanning, consistency results, and protected master synchronization. Add/delete/rename remains unsupported; modified existing blocks, DBs, UDTs, and tag tables are in scope.

## File responsibility map

- `src/Contracts/Engineering/SourceObjectImport.cs`: generic existing-object overwrite contract.
- `src/Mcp.Engineering/Adapter/TiaV17Adapter.cs`: group-aware block/DB/UDT/tag-table overwrite.
- `src/Mcp.Engineering/Tools/EngineeringTools.cs`: destructive `import_source_object` tool.
- `src/Mcp.VersionControl/Git/MergePreviewService.cs`: merge base, conflicts, candidate tree, and blob fingerprints.
- `src/Mcp.VersionControl/Git/ValidatedMergeService.cs`: guarded no-ff merge plus immutable evidence publication.
- `src/Agent/Workbench/FeatureImportModels.cs`: import plan/session/outcome records.
- `src/Agent/Workbench/FeatureImportService.cs`: preflight, overlap, import, and selected rollback.
- `src/Agent/Workbench/ValidatedMergeCoordinator.cs`: compile, full scan, prospective-tree equality, and merge orchestration.
- `src/Agent/Workbench/RollbackFeatureService.cs`: create a feature containing historical XML reversions.
- `src/ApiHost/WorkbenchApiModels.cs`: feature plan/import/rollback/validation/merge endpoints.

### Task 1: Import modified existing blocks, UDTs, and tag tables

**Files:**
- Create: `src/Contracts/Engineering/SourceObjectImport.cs`
- Modify: `src/Contracts/IEngineeringPlatform.cs`
- Modify: `src/Mcp.Engineering/Adapter/TiaV17Adapter.cs`
- Modify: `src/Mcp.Engineering/Tools/EngineeringTools.cs`
- Modify: `src/Mcp.Engineering/Sandbox/EngineeringGuard.cs`
- Create: `tests/Mcp.Engineering.Tests/SourceObjectImportContractTests.cs`
- Modify: `tests/Agent.Tests/McpToolCatalogTests.cs`

- [ ] **Step 1: Write failing public-surface tests**

```csharp
[Theory]
[InlineData("Blocks/Area/Main [OB1].xml", "Block")]
[InlineData("DB/Recipes/Recipe [DB10].xml", "Block")]
[InlineData("Tags/LineA/Inputs.xml", "TagTable")]
[InlineData("UDT/Models/Motor.xml", "Udt")]
public void RelativePathClassifiesSupportedExistingObject(string path, string expected)
{
    Assert.Equal(expected, SourceObjectImport.Classify(path).ToString());
}

[Fact]
public void EngineeringSurfaceExposesGenericSourceImport()
{
    var method = typeof(EngineeringTools).GetMethod(nameof(EngineeringTools.ImportSourceObject));
    Assert.NotNull(method);
    Assert.Equal(
        "import_source_object",
        Assert.Single(method!.GetCustomAttributes<McpServerToolAttribute>()).Name);
}
```

- [ ] **Step 2: Run Engineering tests and verify failure**

Run: `dotnet test tests/Mcp.Engineering.Tests/Mcp.Engineering.Tests.csproj --no-restore --filter "FullyQualifiedName~SourceObjectImportContractTests"`

Expected: FAIL because the generic import contract does not exist.

- [ ] **Step 3: Add generic contracts**

```csharp
public enum SourceObjectKind { Block, TagTable, Udt }

public sealed class SourceObjectImportResult
{
    public string RelativePath { get; set; } = string.Empty;
    public string ObjectName { get; set; } = string.Empty;
    public string ObjectKind { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string[] Warnings { get; set; } = Array.Empty<string>();
    public string? Error { get; set; }
}

public static class SourceObjectImport
{
    public static SourceObjectKind Classify(string relativePath) =>
        relativePath.Replace('\\', '/').Split('/')[0] switch
        {
            "Blocks" or "DB" => SourceObjectKind.Block,
            "Tags" => SourceObjectKind.TagTable,
            "UDT" => SourceObjectKind.Udt,
            _ => throw new ArgumentException("Unsupported PLC source category.", nameof(relativePath)),
        };
}
```

Add `ImportSourceObject(relativePath, xmlFilePath, plcName)` to `IEngineeringPlatform` and expose it as a destructive MCP tool.

- [ ] **Step 4: Implement existing-object overwrite by group path**

Parse the category, zero or more group segments, and filename. Resolve the exact group from `PlcSoftware.BlockGroup`, `TagTableGroup`, or `TypeGroup`. Verify an object with the XML-declared/name-derived name already exists in that exact group; otherwise throw `SOURCE_ADD_UNSUPPORTED`.

Within exclusive access and a transaction, call:

```csharp
blockGroup.Blocks.Import(file, ImportOptions.Override);
tagGroup.TagTables.Import(file, ImportOptions.Override);
typeGroup.Types.Import(file, ImportOptions.Override);
```

For UDTs, use the overload without `SWImportOptions` first. Return per-object outcome. Keep `import_block` as compatibility wrapper around the generic block path.

- [ ] **Step 5: Verify reflection parity against the local V17 API**

Extend `SourceObjectImportContractTests` to reflect that `PlcTagTableComposition` and `PlcTypeComposition` expose `Import(FileInfo, ImportOptions)`. This test must run only when the Siemens assembly is available, following existing environment-sensitive test conventions.

- [ ] **Step 6: Run Engineering and catalog tests**

Run: `dotnet test tests/Mcp.Engineering.Tests/Mcp.Engineering.Tests.csproj --no-restore`

Run: `dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-restore --filter "FullyQualifiedName~McpToolCatalogTests"`

Expected: PASS.

- [ ] **Step 7: Commit generic source overwrite**

```bash
git add src/Contracts src/Mcp.Engineering tests/Mcp.Engineering.Tests tests/Agent.Tests/McpToolCatalogTests.cs
git commit -m "feat: import existing PLC XML object types"
```

### Task 2: Build prospective merge trees without changing branches

**Files:**
- Create: `src/Mcp.VersionControl/Git/MergePreviewService.cs`
- Modify: `src/Mcp.VersionControl/Git/Models.cs`
- Modify: `src/Mcp.VersionControl/Git/RepositoryService.cs`
- Modify: `src/Mcp.VersionControl/Tools/VersionControlTools.cs`
- Create: `tests/Mcp.VersionControl.Tests/MergePreviewServiceTests.cs`

- [ ] **Step 1: Write failing preview tests**

```csharp
[Fact]
public void PreviewReturnsCandidateTreeAndPreservesRefs()
{
    var fixture = MergeFixture.CreateDisjointFeature();
    var beforeMaster = fixture.MasterSha;
    var beforeFeature = fixture.FeatureSha;

    var preview = RepositoryService.PreviewMerge(fixture.MasterPath, "feature-a");

    Assert.False(preview.HasConflicts);
    Assert.Equal(beforeMaster, preview.TargetSha);
    Assert.Equal(beforeFeature, preview.SourceSha);
    Assert.NotEmpty(preview.CandidateTreeSha);
    Assert.Contains(preview.Objects, x => x.FilePath.EndsWith("Feature.xml"));
    Assert.Equal(beforeMaster, fixture.ReadBranchSha("master"));
    Assert.Equal(beforeFeature, fixture.ReadBranchSha("feature-a"));
}

[Fact]
public void PreviewReportsSameFileConflictWithoutMovingRefs()
{
    var fixture = MergeFixture.CreateConflictingFeature();
    var preview = RepositoryService.PreviewMerge(fixture.MasterPath, "feature-a");

    Assert.True(preview.HasConflicts);
    Assert.Contains("devices/PLC_1/source/Blocks/Main.xml", preview.ConflictPaths);
    Assert.Equal(fixture.MasterSha, fixture.ReadBranchSha("master"));
}
```

- [ ] **Step 2: Run tests and verify failure**

Run: `dotnet test tests/Mcp.VersionControl.Tests/Mcp.VersionControl.Tests.csproj --no-restore --filter "FullyQualifiedName~MergePreviewServiceTests"`

Expected: FAIL because merge preview does not exist.

- [ ] **Step 3: Add preview contracts**

```csharp
public sealed record VcTreeObject(
    string FilePath,
    string Sha256,
    long Length);

public sealed record VcMergePreviewResult(
    string TargetBranch,
    string SourceBranch,
    string MergeBaseSha,
    string TargetSha,
    string SourceSha,
    string? CandidateTreeSha,
    bool HasConflicts,
    IReadOnlyList<string> ConflictPaths,
    IReadOnlyList<string> FeaturePaths,
    IReadOnlyList<VcTreeObject> Objects);
```

Use `repo.ObjectDatabase.MergeCommits(target, source, new MergeTreeOptions())`. On success, enumerate only `SourcePathPolicy` XML blobs from the returned tree, decode text, normalize through `XmlCompare`, and hash lowercase SHA-256. Calculate `FeaturePaths` by diffing merge-base tree to source tree. On conflict, return normalized conflict paths and no candidate objects. Expose `vc_merge_preview`.

- [ ] **Step 4: Run version-control tests**

Run: `dotnet test tests/Mcp.VersionControl.Tests/Mcp.VersionControl.Tests.csproj --no-restore`

Expected: PASS.

- [ ] **Step 5: Commit merge preview**

```bash
git add src/Mcp.VersionControl tests/Mcp.VersionControl.Tests
git commit -m "feat: preview PLC source merge trees"
```

### Task 3: Plan importability with three-way overlap detection

**Files:**
- Create: `src/Agent/Workbench/FeatureImportModels.cs`
- Create: `src/Agent/Workbench/FeatureImportService.cs`
- Create: `tests/Agent.Tests/FeatureImportServiceTests.cs`
- Modify: `src/Agent/Workbench/WorkbenchCoordinator.cs`

- [ ] **Step 1: Write failing preflight tests**

```csharp
[Fact]
public async Task PreflightRequiresCommittedFeatureSource()
{
    versionControl.Respond("vc_status", Status(Changed("devices/PLC_1/source/Blocks/A.xml")));

    var error = await Assert.ThrowsAsync<WorkbenchLifecycleException>(() =>
        service.PlanAsync(workbench, feature, CancellationToken.None));

    Assert.Equal("FEATURE_SOURCE_UNCOMMITTED", error.Code);
}

[Fact]
public async Task PreflightDisablesOnlyPathChangedInTiaAndFeature()
{
    ScriptCleanFeature();
    ScriptPreview(featurePaths:
        ["devices/PLC_1/source/Blocks/A.xml", "devices/PLC_1/source/Blocks/B.xml"]);
    ScriptTiaComparison(differentPaths:
        ["devices/PLC_1/source/Blocks/A.xml"]);

    var plan = await service.PlanAsync(workbench, feature, CancellationToken.None);

    Assert.False(plan.Objects.Single(x => x.RelativePath.EndsWith("A.xml")).Importable);
    Assert.Equal("TIA_FEATURE_OVERLAP", plan.Objects.Single(x => x.RelativePath.EndsWith("A.xml")).Reason);
    Assert.True(plan.Objects.Single(x => x.RelativePath.EndsWith("B.xml")).Importable);
}
```

- [ ] **Step 2: Run tests and verify failure**

Run: `dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-restore --filter "FullyQualifiedName~FeatureImportServiceTests"`

Expected: FAIL because the feature import service does not exist.

- [ ] **Step 3: Add plan records**

```csharp
public sealed record FeatureImportObject(
    string DeviceId,
    string PlcName,
    string RelativePath,
    string FeatureFingerprint,
    bool Importable,
    string? Reason);

public sealed record FeatureImportPlan(
    string PlanId,
    string WorkbenchId,
    string FeatureWorktreeId,
    string FeatureSha,
    string MasterSha,
    string ComparisonId,
    IReadOnlyList<FeatureImportObject> Objects);
```

`PlanAsync` requires feature source status clean, calls `vc_merge_preview`, compares TIA to current master through plan 2, and intersects feature paths with TIA differences. Mark Git conflicts `GIT_MERGE_CONFLICT`, TIA overlap `TIA_FEATURE_OVERLAP`, and added/deleted paths `SOURCE_LIFECYCLE_UNSUPPORTED`. Persist the plan in ignored automation state and do not globally fail when at least one object remains importable.

- [ ] **Step 4: Run Agent tests**

Run: `dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-restore --filter "FullyQualifiedName~FeatureImportServiceTests"`

Expected: PASS.

- [ ] **Step 5: Commit feature preflight**

```bash
git add src/Agent/Workbench tests/Agent.Tests/FeatureImportServiceTests.cs
git commit -m "feat: detect feature and TIA source overlap"
```

### Task 4: Import selected objects and provide user-controlled rollback

**Files:**
- Modify: `src/Agent/Workbench/FeatureImportModels.cs`
- Modify: `src/Agent/Workbench/FeatureImportService.cs`
- Modify: `src/Agent/Workbench/WorkbenchCoordinator.cs`
- Modify: `tests/Agent.Tests/FeatureImportServiceTests.cs`
- Modify: `src/ApiHost/DeviceToolSecurity.cs`

- [ ] **Step 1: Write failing import and rollback tests**

```csharp
[Fact]
public async Task ImportRecordsPerObjectSuccessAndDoesNotCompileAutomatically()
{
    var plan = fixture.Plan(Importable("dev-1", "Blocks/A.xml"), Importable("dev-1", "UDT/Motor.xml"));
    engineering
        .Respond("import_source_object", Imported("Blocks/A.xml"))
        .Respond("import_source_object", Imported("UDT/Motor.xml"));

    var session = await service.ImportAsync(plan.PlanId, plan.Objects.Select(x => x.RelativePath), token);

    Assert.All(session.Objects, item => Assert.Equal(FeatureImportState.Imported, item.State));
    Assert.DoesNotContain("compile_plc", engineering.Calls);
}

[Fact]
public async Task RollbackImportsCurrentMasterForSelectedSuccessfulObjects()
{
    var session = await fixture.ImportOneAsync("Blocks/A.xml");
    engineering.Respond("import_source_object", Imported("Blocks/A.xml"));

    var result = await service.RollbackAsync(session.SessionId, ["Blocks/A.xml"], token);

    Assert.Equal(FeatureImportState.RolledBack, Assert.Single(result.Objects).State);
    Assert.Equal(fixture.MasterSource("Blocks/A.xml"),
        Property<string>(engineering.CallArgs["import_source_object"].Last(), "xmlFilePath"));
}
```

- [ ] **Step 2: Run tests and verify failure**

Run: `dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-restore --filter "FullyQualifiedName~ImportRecordsPerObject|FullyQualifiedName~RollbackImports"`

Expected: FAIL because session import and rollback do not exist.

- [ ] **Step 3: Add import session records**

```csharp
public enum FeatureImportState { Pending, Imported, Failed, KeptAfterCompileFailure, RolledBack }

public sealed record FeatureImportOutcome(
    string DeviceId,
    string RelativePath,
    FeatureImportState State,
    string? Error,
    IReadOnlyList<string> Warnings);

public sealed record FeatureImportSession(
    string SessionId,
    string PlanId,
    string FeatureSha,
    string MasterSha,
    string StartedAt,
    IReadOnlyList<FeatureImportOutcome> Objects);
```

Persist sessions under `.automation/import-sessions`. Revalidate plan SHA, master SHA, current feature status, selected membership, and `Importable` immediately before import. Call `import_source_object` sequentially under the engineering session lock and report each outcome. Continue after an object failure unless cancellation is requested.

Rollback accepts only successfully imported existing objects and calls the same tool with current master XML. It never changes Git. Reject unsupported lifecycle objects.

- [ ] **Step 4: Update sandbox binding**

Only coordinator-owned import operations may call `import_source_object`. Bind `relativePath`, `xmlFilePath`, and `plcName` from the stored plan/session and selected device; generic chat calls still require destructive confirmation and an existing source path.

- [ ] **Step 5: Run Agent and API security tests**

Run: `dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-restore --filter "FullyQualifiedName~FeatureImportServiceTests"`

Run: `dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj --no-restore --filter "FullyQualifiedName~ToolArgument|FullyQualifiedName~Sandbox"`

Expected: PASS.

- [ ] **Step 6: Commit import sessions and rollback**

```bash
git add src/Agent src/ApiHost/DeviceToolSecurity.cs tests/Agent.Tests tests/ApiHost.Tests
git commit -m "feat: import and roll back selected feature objects"
```

### Task 5: Gate merge on every device compile and exact prospective source

**Files:**
- Create: `src/Agent/Workbench/ValidatedMergeCoordinator.cs`
- Create: `tests/Agent.Tests/ValidatedMergeCoordinatorTests.cs`
- Modify: `src/Agent/Workbench/FeatureImportModels.cs`
- Modify: `src/Agent/Workbench/WorkbenchCoordinator.cs`

- [ ] **Step 1: Write failing all-device gate tests**

```csharp
[Fact]
public async Task OneDeviceCompileFailureWithholdsWholeMerge()
{
    ScriptPreviewExactCandidate();
    engineering
        .Respond("compile_plc", Compile("success"))
        .Respond("compile_plc", Compile("error"));

    var result = await coordinator.ValidateAsync(request, token);

    Assert.Equal(ValidatedMergeState.CompileFailed, result.State);
    Assert.DoesNotContain("vc_merge_validated", versionControl.Calls);
}

[Fact]
public async Task ExactCandidateAcrossEveryDeviceBecomesMergeReady()
{
    ScriptPreviewExactCandidate();
    ScriptSuccessfulCompileForEveryDevice();
    ScriptStableFullScanMatchingCandidateForEveryDevice();

    var result = await coordinator.ValidateAsync(request with { MachineValidated = true }, token);

    Assert.Equal(ValidatedMergeState.Ready, result.State);
    Assert.Equal(2, result.Devices.Count);
    Assert.All(result.Devices, item => Assert.NotNull(item.ProjectChecksum));
}
```

Add separate tests for machine confirmation missing, checksum unavailable, extra TIA object, missing feature object, source mismatch, scan checksum race, branch-tip movement, unsupported additions/deletions, and incomplete export coverage such as an Openness-protected fail-safe block.

- [ ] **Step 2: Run tests and verify failure**

Run: `dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-restore --filter "FullyQualifiedName~ValidatedMergeCoordinatorTests"`

Expected: FAIL because the validated merge coordinator does not exist.

- [ ] **Step 3: Implement the validation sequence**

Use this request/result boundary:

```csharp
public sealed record ValidateFeatureMergeRequest(
    string WorkbenchId,
    string FeatureWorktreeId,
    string ImportSessionId,
    bool MachineValidated,
    string ConfirmedBy);

public enum ValidatedMergeState { Ready, CompileFailed, SourceDifferent, BranchMoved }
```

Sequence:

1. Reload the clean feature and current master refs.
2. Rebuild `vc_merge_preview`; reject conflicts/unsupported lifecycle paths.
3. Require every prospective feature path to have an imported success in the session.
4. Compile each registered PLC with `compile_plc`; stop eligibility but preserve TIA imports on failure.
5. Require user machine confirmation.
6. Read valid checksums and run a stable full scan for every device.
7. Reject `SOURCE_COVERAGE_INCOMPLETE` if any PLC object could not be exported into source XML.
8. Compare the complete TIA object path/fingerprint map with `preview.Objects` exactly.
9. Re-read source and target refs; return `BranchMoved` if either changed.
10. Build a validation draft from the scans and preview SHAs.

Do not call Git merge from `ValidateAsync`; return an opaque validation ID stored under ignored automation state. This lets the UI show readiness before final confirmation.

- [ ] **Step 4: Model compile failure keep/rollback decisions**

Add `KeepAfterCompileFailure(sessionId, paths)` to mark selected imports without changing TIA and reuse Task 4 rollback for the rest. Neither action makes the merge ready until a later full successful validation.

- [ ] **Step 5: Run Agent tests**

Run: `dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-restore --filter "FullyQualifiedName~ValidatedMergeCoordinatorTests|FullyQualifiedName~FeatureImportServiceTests"`

Expected: PASS.

- [ ] **Step 6: Commit the strict merge gate**

```bash
git add src/Agent/Workbench tests/Agent.Tests
git commit -m "feat: validate all PLC devices before merge"
```

### Task 6: Publish the no-ff merge and validation tag with rollback recovery

**Files:**
- Create: `src/Mcp.VersionControl/Git/ValidatedMergeService.cs`
- Modify: `src/Mcp.VersionControl/Git/Models.cs`
- Modify: `src/Mcp.VersionControl/Git/RepositoryService.cs`
- Modify: `src/Mcp.VersionControl/Tools/VersionControlTools.cs`
- Create: `tests/Mcp.VersionControl.Tests/ValidatedMergeServiceTests.cs`
- Modify: `src/Agent/Workbench/ValidatedMergeCoordinator.cs`
- Modify: `tests/Agent.Tests/ValidatedMergeCoordinatorTests.cs`

- [ ] **Step 1: Write failing publication and recovery tests**

```csharp
[Fact]
public void ValidatedMergeCreatesNoFfCommitAndMatchingTag()
{
    var fixture = MergeFixture.CreateDisjointFeature();
    var preview = RepositoryService.PreviewMerge(fixture.MasterPath, "feature-a");

    var result = RepositoryService.MergeValidated(
        fixture.MasterPath,
        "feature-a",
        preview.TargetSha,
        preview.SourceSha,
        preview.CandidateTreeSha!,
        Draft(preview));

    Assert.True(result.Merged);
    Assert.Equal(2, fixture.Repository.Lookup<Commit>(result.Sha)!.Parents.Count());
    Assert.Equal(result.Sha, RepositoryService.GetValidation(fixture.MasterPath, result.Sha)!.CommitSha);
}

[Fact]
public void TagFailureRestoresCleanMasterToOriginalSha()
{
    var fixture = MergeFixture.CreateDisjointFeature();
    var preview = RepositoryService.PreviewMerge(fixture.MasterPath, "feature-a");
    fixture.FailTagCreation = true;

    Assert.Throws<VcInternalException>(() => fixture.MergeValidated(preview));

    Assert.Equal(preview.TargetSha, fixture.ReadBranchSha("master"));
    Assert.Empty(RepositoryService.Status(fixture.MasterPath).Entries);
}
```

- [ ] **Step 2: Run tests and verify failure**

Run: `dotnet test tests/Mcp.VersionControl.Tests/Mcp.VersionControl.Tests.csproj --no-restore --filter "FullyQualifiedName~ValidatedMergeServiceTests"`

Expected: FAIL because guarded validated merge does not exist.

- [ ] **Step 3: Implement guarded publication**

Before merge, require clean target status, current target/source SHAs equal expected values, and a fresh preview tree equal `candidateTreeSha`. Run `git merge --no-ff --no-edit` with a deterministic message. Verify the resulting commit has both expected parents and its tree equals the candidate tree. Fill `CommitSha` in the `feature-merge` evidence and create the validation tag.

If merge, tree verification, or tag creation fails after moving master, run guarded `git reset --hard <expectedTargetSha>` only after verifying target still points to the merge commit created by this operation. Remove any partially created app-owned tag. If recovery fails, return `VALIDATED_MERGE_RECOVERY_REQUIRED` with both SHAs; never report success.

Expose `vc_merge_validated`; stop using unrestricted `vc_merge` from `WorkbenchCoordinator`.

- [ ] **Step 4: Connect stored validation to publication**

`ValidatedMergeCoordinator.MergeAsync(validationId)` reloads the stored ready record, verifies it has not expired, calls `vc_merge_validated`, deletes the ready record on success, and returns commit/evidence details. It cannot accept checksums, fingerprints, or SHAs supplied by the browser.

- [ ] **Step 5: Run VersionControl and Agent tests**

Run: `dotnet test tests/Mcp.VersionControl.Tests/Mcp.VersionControl.Tests.csproj --no-restore`

Run: `dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-restore --filter "FullyQualifiedName~ValidatedMergeCoordinatorTests"`

Expected: PASS.

- [ ] **Step 6: Commit validated publication**

```bash
git add src/Mcp.VersionControl src/Agent/Workbench tests/Mcp.VersionControl.Tests tests/Agent.Tests
git commit -m "feat: publish validated PLC worktree merges"
```

### Task 7: Create rollback features and expose workflow endpoints

**Files:**
- Create: `src/Agent/Workbench/RollbackFeatureService.cs`
- Create: `tests/Agent.Tests/RollbackFeatureServiceTests.cs`
- Modify: `src/Mcp.VersionControl/Git/RepositoryService.cs`
- Modify: `src/Mcp.VersionControl/Tools/VersionControlTools.cs`
- Modify: `src/Agent/Workbench/WorkbenchCoordinator.cs`
- Modify: `src/ApiHost/WorkbenchApiModels.cs`
- Modify: `tests/ApiHost.Tests/WorkbenchEndpointsTests.cs`
- Modify: `studio/src/api/client.ts`

- [ ] **Step 1: Write failing rollback-feature tests**

```csharp
[Fact]
public async Task CreateRollbackFeatureRestoresSelectedHistoricalXmlWithoutMovingMaster()
{
    var currentMaster = fixture.CurrentMasterSha;

    var result = await service.CreateAsync(
        fixture.WorkbenchId,
        fixture.HistoricalSha,
        ["devices/PLC_1/source/Blocks/A.xml"],
        "rollback-a",
        CancellationToken.None);

    Assert.Equal(currentMaster, fixture.ReadMasterSha());
    Assert.Equal("historical A", File.ReadAllText(fixture.FeatureSource(result, "Blocks/A.xml")));
    Assert.Contains("devices/PLC_1/source/Blocks/A.xml", fixture.FeatureStatus(result));
}
```

- [ ] **Step 2: Run tests and verify failure**

Run: `dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-restore --filter "FullyQualifiedName~RollbackFeatureServiceTests"`

Expected: FAIL because historical paths cannot be applied to a new feature.

- [ ] **Step 3: Add historical path application**

Implement `vc_apply_historical_paths(repoPath, sourceSha, paths)` for XML-only paths. It writes the selected blob versions into the feature worktree, leaves them unstaged, and rejects paths missing at the historical commit with `SOURCE_DELETE_UNSUPPORTED`. The coordinator creates a normal feature worktree from current master, applies historical paths, and leaves manual commit/import/validation to the standard workflow.

- [ ] **Step 4: Add workflow endpoints**

```text
POST /api/workbenches/{workbenchId}/worktrees/{featureId}/vc/import-plan
POST /api/workbenches/{workbenchId}/vc/import-plans/{planId}/import
POST /api/workbenches/{workbenchId}/vc/import-sessions/{sessionId}/rollback
POST /api/workbenches/{workbenchId}/vc/import-sessions/{sessionId}/keep
POST /api/workbenches/{workbenchId}/worktrees/{featureId}/vc/validate-merge
POST /api/workbenches/{workbenchId}/vc/validated-merges/{validationId}/merge
POST /api/workbenches/{workbenchId}/vc/rollback-features
```

All long operations use operation-status progress. The validate request carries machine confirmation and confirming identity; merge carries only the server-issued validation ID.

- [ ] **Step 5: Add endpoint tests for every blocked state**

Cover uncommitted feature, overlapping object, compile failure, absent machine confirmation, stale validation ID, branch movement, exact success, and rollback feature creation. Assert a failed state never calls `vc_merge_validated`.

- [ ] **Step 6: Run all backend suites**

Run: `dotnet test AgentAssistPlcDev.sln --no-restore --verbosity minimal`

Expected: PASS. Run the timing-sensitive Agent suite separately if parallel solution load triggers its existing one-second performance threshold.

- [ ] **Step 7: Commit workflow APIs**

```bash
git add src/Agent src/ApiHost src/Mcp.VersionControl studio/src/api/client.ts tests
git commit -m "feat: expose validated feature and rollback workflows"
```

## Plan 3 completion gate

Do not start the final UI plan until modified existing blocks, DBs, UDTs, and tag tables can be selected independently; overlaps disable only affected objects; compile failure offers keep/rollback without merging; every device must compile and scan exactly; successful merge preserves feature commits and creates immutable evidence; and historical recovery creates a new feature without moving master.
