# PLC Source Repository Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the exported/modified overlay and tracked application metadata with one XML-only source tree per worktree, manual selected-object commits, and permanent Git validation-tag primitives.

**Architecture:** `DeviceContext.SourceRoot` becomes the only editable/ingestable PLC source location. The shared bare repository stores ignore rules internally and the version-control service filters and commits only `devices/<device>/source/**/*.xml`. App metadata remains physically available in each linked checkout but is ignored; validation evidence is stored as immutable annotated Git tags.

**Tech Stack:** C#/.NET 8, .NET Framework 4.8 compatibility projects, LibGit2Sharp, xUnit, ASP.NET minimal APIs, React/TypeScript contract types, Git annotated tags.

---

## Execution relationship

This is plan 1 of 4 and must land first. It leaves TIA comparison and validated merging on their current behavior until the next plans replace them. There is no existing-workbench migration: all tests and runtime paths target newly created workbenches.

## File responsibility map

- `src/Agent/Workbench/WorkbenchModels.cs`: expose one `SourceRoot` in `DeviceContext`.
- `src/Agent/Workbench/WorkbenchPaths.cs`: resolve `devices/<plc>/source`, staging, and runtime paths.
- `src/Agent/Workbench/DeviceSourceResolver.cs`: resolve and edit files directly in the branch source tree.
- `src/Agent/Workbench/WorkbenchWritePolicy.cs`: enforce feature editing and authorize selected TIA-originated pending master files.
- `src/Mcp.VersionControl/Git/SourcePathPolicy.cs`: validate the only paths Git may report or commit.
- `src/Mcp.VersionControl/Git/ValidationTagStore.cs`: serialize, read, and immutably create TIA validation tags.
- `src/Mcp.VersionControl/Git/Models.cs`: source status, selected commit, history, and validation contracts.
- `src/Mcp.VersionControl/Git/RepositoryService.cs`: internal excludes, filtered status, selected commit, correct history, and tag operations.
- `src/Mcp.VersionControl/Tools/VersionControlTools.cs`: MCP endpoints for source status, selected commits, and validation evidence.
- `src/Mcp.Knowledge/Tools/KnowledgeTools.cs`: ingest and update from one source root.
- `src/Agent/Workbench/DeviceSnapshot.cs`: derive offline object details from source XML rather than a tracked manifest.
- `src/ApiHost/DeviceToolSecurity.cs`: bind source editor and knowledge tools to `SourceRoot` and enforce branch role.
- `src/ApiHost/WorkbenchApiModels.cs`: worktree-level source status and selected commit endpoints.
- `studio/src/api/client.ts`: compile-time API contract updates; full UI follows in plan 4.

### Task 1: Introduce the single source-root domain model

**Files:**
- Modify: `src/Agent/Workbench/WorkbenchModels.cs`
- Modify: `src/Agent/Workbench/WorkbenchPaths.cs`
- Modify: `src/Agent/Workbench/DeviceSourceResolver.cs`
- Modify: `tests/Agent.Tests/WorkbenchPathsTests.cs`
- Replace overlay cases in: `tests/Agent.Tests/DeviceSourceResolverTests.cs`

- [ ] **Step 1: Write failing path and resolver tests**

Add assertions that a device has one source root and editing returns that exact tracked path:

```csharp
[Fact]
public void ResolveDeviceUsesOneTrackedSourceDirectory()
{
    var context = WorkbenchPaths.ResolveDevice(
        "wb-1", root, "wt-1", "master", "dev-1", "PLC_1");

    Assert.Equal(Path.Combine(context.DeviceRoot, "source"), context.SourceRoot);
    Assert.Equal(Path.Combine(context.DeviceRoot, "staging"), context.StagingRoot);
}

[Fact]
public void PrepareEditableReturnsExistingSourceWithoutCreatingAnOverlay()
{
    var context = CreateContext();
    var source = Write(Path.Combine(context.SourceRoot, "Blocks", "Main.xml"), "<Document />");
    var stale = 0;
    var resolver = new DeviceSourceResolver(_ => stale++);

    Assert.Equal(source, resolver.ResolveEffective(context, "Blocks/Main.xml"));
    Assert.Equal(source, resolver.PrepareEditable(context, "Blocks/Main.xml"));
    Assert.Equal(1, stale);
    Assert.False(Directory.Exists(Path.Combine(context.DeviceRoot, "modified-source")));
}
```

- [ ] **Step 2: Run the focused tests and verify the old model fails**

Run: `dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-restore --filter "FullyQualifiedName~WorkbenchPathsTests|FullyQualifiedName~DeviceSourceResolverTests"`

Expected: FAIL because `SourceRoot` does not exist and the resolver still creates `modified-source` copies.

- [ ] **Step 3: Replace the context and resolver implementation**

Use this context shape:

```csharp
public sealed record DeviceContext(
    string WorkbenchId,
    string WorktreeId,
    string DeviceId,
    string WorkbenchRoot,
    string WorktreeRoot,
    string DeviceRoot,
    string SourceRoot,
    string StagingRoot,
    string KnowledgeDbPath);
```

Resolve `SourceRoot` with `ResolveRelative(deviceRoot, "source")`. Make `ResolveEffective` and `PrepareEditable` resolve beneath `SourceRoot`; require the file to exist; keep path-jail and reparse-point checks; mark knowledge stale only from `PrepareEditable`. Make `CreateNew` write directly beneath `SourceRoot`. Replace `EnumerateModified` with `EnumerateSource` and return all XML paths in deterministic ordinal order.

- [ ] **Step 4: Update all `DeviceContext` construction sites mechanically**

Update fixtures in `tests/Agent.Tests`, `tests/ApiHost.Tests`, and Studio contract fixtures to pass `source` once instead of exported and modified roots. Do not add compatibility properties; compilation errors are the checklist for complete migration.

- [ ] **Step 5: Run the Agent tests**

Run: `dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-restore`

Expected: PASS, including direct-source path traversal and reparse-point rejection tests.

- [ ] **Step 6: Commit the domain migration**

```bash
git add src/Agent/Workbench/WorkbenchModels.cs src/Agent/Workbench/WorkbenchPaths.cs src/Agent/Workbench/DeviceSourceResolver.cs tests/Agent.Tests
git commit -m "refactor: use one PLC source tree per worktree"
```

### Task 2: Make shared repository excludes internal and enforce XML-only paths

**Files:**
- Create: `src/Mcp.VersionControl/Git/SourcePathPolicy.cs`
- Modify: `src/Mcp.VersionControl/Git/RepositoryService.cs`
- Modify: `src/Mcp.VersionControl/Git/Models.cs`
- Modify: `tests/Mcp.VersionControl.Tests/LinkedWorktreeTests.cs`
- Modify: `tests/Mcp.VersionControl.Tests/VersionControlToolsTests.cs`

- [ ] **Step 1: Write failing tests for a clean new worktree and filtered status**

```csharp
[Fact]
public void SharedInitUsesInternalExcludesAndDoesNotCreateGitIgnore()
{
    var result = RepositoryService.InitShared(workbenchRoot, masterPath);

    Assert.False(File.Exists(Path.Combine(masterPath, ".gitignore")));
    var exclude = File.ReadAllText(Path.Combine(result.RepositoryPath, "info", "exclude"));
    Assert.Contains("worktree.json", exclude);
    Assert.Contains("devices/*/device.json", exclude);
    Assert.Contains("devices/*/staging/", exclude);
}

[Fact]
public void StatusReturnsOnlyPlcSourceXml()
{
    var result = CreateSharedRepositoryWithInitialCommit();
    Write(masterPath, "worktree.json", "{}");
    Write(masterPath, "devices/PLC_1/device.json", "{}");
    Write(masterPath, "devices/PLC_1/source/Blocks/Main.xml", "<Document />");
    Write(masterPath, "notes.txt", "ignore me");

    var status = RepositoryService.Status(masterPath);

    var entry = Assert.Single(status.Entries);
    Assert.Equal("devices/PLC_1/source/Blocks/Main.xml", entry.FilePath);
}
```

- [ ] **Step 2: Run the version-control tests and verify failure**

Run: `dotnet test tests/Mcp.VersionControl.Tests/Mcp.VersionControl.Tests.csproj --no-restore --filter "FullyQualifiedName~SharedInitUsesInternalExcludes|FullyQualifiedName~StatusReturnsOnlyPlcSourceXml"`

Expected: FAIL because `.gitignore` is created and status includes arbitrary files.

- [ ] **Step 3: Add one path policy used by every write and read surface**

```csharp
internal static class SourcePathPolicy
{
    private static readonly Regex SourceXml = new(
        @"^devices/[^/]+/source/.+\.xml$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static string Require(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (normalized.Contains("../", StringComparison.Ordinal)
            || !SourceXml.IsMatch(normalized))
        {
            throw new VcInternalException(
                "SOURCE_PATH_REQUIRED",
                $"'{path}' is not a tracked PLC source XML path.");
        }
        return normalized;
    }

    public static bool IsAllowed(string path)
    {
        try { _ = Require(path); return true; }
        catch (VcInternalException) { return false; }
    }
}
```

Replace `WriteSharedGitIgnore` with `WriteSharedExclude(repositoryPath)`. Preserve user-created exclude lines and append only these app rules: `worktree.json`, `devices/*/device.json`, `devices/*/staging/`, `devices/*/plc-knowledge.db*`, `.automation/`, and `sessionexport/`. Filter `Status` through `SourcePathPolicy.IsAllowed` and set `IncludeIgnored = false`.

- [ ] **Step 4: Run the complete version-control test project**

Run: `dotnet test tests/Mcp.VersionControl.Tests/Mcp.VersionControl.Tests.csproj --no-restore`

Expected: PASS with updated assertions that no tracked `.gitignore` is needed.

- [ ] **Step 5: Commit the repository boundary**

```bash
git add src/Mcp.VersionControl/Git tests/Mcp.VersionControl.Tests
git commit -m "feat: restrict Git status to PLC source XML"
```

### Task 3: Replace stage/commit with an atomic selected-path commit

**Files:**
- Modify: `src/Mcp.VersionControl/Git/Models.cs`
- Modify: `src/Mcp.VersionControl/Git/RepositoryService.cs`
- Modify: `src/Mcp.VersionControl/Tools/VersionControlTools.cs`
- Create: `src/Contracts/Engineering/PlcXmlChangeSummary.cs`
- Create: `tests/Contracts.Tests/PlcXmlChangeSummaryTests.cs`
- Modify: `tests/Mcp.VersionControl.Tests/VersionControlToolsTests.cs`

- [ ] **Step 1: Write failing selected-commit and history tests**

```csharp
[Fact]
public void CommitSelectedCommitsOnlyRequestedXmlAndLeavesOtherChanges()
{
    var first = _fixture.CommitFile("devices/PLC_1/source/Blocks/A.xml", "a", "base");
    _fixture.WriteFile("devices/PLC_1/source/Blocks/A.xml", "a2");
    _fixture.WriteFile("devices/PLC_1/source/Blocks/B.xml", "b");

    var result = RepositoryService.CommitSelected(
        _fixture.RootPath,
        ["devices/PLC_1/source/Blocks/A.xml"],
        "change A",
        null);

    Assert.Equal(["devices/PLC_1/source/Blocks/A.xml"], result.Files);
    Assert.Contains(
        RepositoryService.Status(_fixture.RootPath).Entries,
        entry => entry.FilePath.EndsWith("B.xml", StringComparison.Ordinal));
    Assert.NotEqual(first, result.Sha);
}

[Fact]
public void LogListsFilesChangedByEachCommitRatherThanTreeRoots()
{
    _fixture.CommitFile("devices/PLC_1/source/Blocks/A.xml", "a", "base");
    _fixture.WriteFile("devices/PLC_1/source/Blocks/A.xml", "a2");
    var commit = RepositoryService.CommitSelected(
        _fixture.RootPath,
        ["devices/PLC_1/source/Blocks/A.xml"],
        "change A",
        null);

    var entry = Assert.Single(RepositoryService.Log(_fixture.RootPath, 1).Commits);
    Assert.Equal(commit.Sha, entry.Sha);
    Assert.Equal(["devices/PLC_1/source/Blocks/A.xml"], entry.Files);
}
```

- [ ] **Step 2: Run focused tests and verify failure**

Run: `dotnet test tests/Mcp.VersionControl.Tests/Mcp.VersionControl.Tests.csproj --no-restore --filter "FullyQualifiedName~CommitSelected|FullyQualifiedName~LogListsFiles"`

Expected: FAIL because the selected commit operation does not exist and history currently lists tree entries.

- [ ] **Step 3: Add the selected commit contract and operation**

```csharp
public sealed class VcCommitResult
{
    public string Sha { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string[] Files { get; set; } = Array.Empty<string>();
}
```

Implement `CommitSelected(repoPath, paths, message, author)` by validating a non-empty distinct path list through `SourcePathPolicy`, unstaging the index without touching the worktree, staging exactly those paths including deletions, committing, and returning the normalized paths. Reject a path that has no change with `SOURCE_PATH_UNCHANGED`. Keep `vc_add` only for backward MCP compatibility and stop using it from the app.

Compute each log entry's `Files` by diffing the commit tree against its first parent, or against an empty tree for the root commit. Filter the result through `SourcePathPolicy`.

Expose:

```csharp
[McpServerTool(Name = "vc_commit_selected")]
public CallToolResult VcCommitSelected(
    string repoPath,
    string[] paths,
    string message,
    string? author = null) =>
    Invoke(() => RepositoryService.CommitSelected(repoPath, paths, message, author));
```

- [ ] **Step 4: Correct historical diff arguments while this surface is under test**

Pass `oldSha` and `newSha` through the API later; in `RepositoryService.Diff`, cover working-tree, one-ref-to-working-tree, and two-ref cases with tests. Normalize the diff output with `XmlCompare.Normalize` before hunk parsing so `<Created>` lines do not appear.

Add `PlcXmlChangeSummary.Compare(oldXml, newXml)`. It reports changed safe header fields and multilingual title/comment values, plus `LogicOrStructureChanged` when the remaining protected canonical XML differs. Add the resulting summary to `VcDiffResult`. If either side cannot be parsed, return `SummaryAvailable = false`; never infer semantic meaning from raw lines.

- [ ] **Step 5: Run version-control tests**

Run: `dotnet test tests/Mcp.VersionControl.Tests/Mcp.VersionControl.Tests.csproj --no-restore`

Expected: PASS.

- [ ] **Step 6: Commit manual object commits and accurate history**

```bash
git add src/Contracts src/Mcp.VersionControl tests/Contracts.Tests tests/Mcp.VersionControl.Tests
git commit -m "feat: commit selected PLC source objects"
```

### Task 4: Add immutable validation-tag storage

**Files:**
- Create: `src/Mcp.VersionControl/Git/ValidationTagStore.cs`
- Modify: `src/Mcp.VersionControl/Git/Models.cs`
- Modify: `src/Mcp.VersionControl/Git/RepositoryService.cs`
- Modify: `src/Mcp.VersionControl/Tools/VersionControlTools.cs`
- Create: `tests/Mcp.VersionControl.Tests/ValidationTagStoreTests.cs`

- [ ] **Step 1: Write failing round-trip and immutability tests**

```csharp
[Fact]
public void ValidationEvidenceRoundTripsFromAnnotatedTag()
{
    var commit = fixture.CommitFile("devices/PLC_1/source/Blocks/A.xml", "a", "base");
    var evidence = Evidence(commit, "tia-sync", "checksum-1");

    RepositoryService.CreateValidation(fixture.RootPath, evidence);
    var loaded = RepositoryService.GetValidation(fixture.RootPath, commit);

    Assert.NotNull(loaded);
    Assert.Equal("checksum-1", Assert.Single(loaded!.Devices).ProjectChecksum);
}

[Fact]
public void ExistingValidationTagCannotBeReplaced()
{
    var commit = fixture.CommitFile("devices/PLC_1/source/Blocks/A.xml", "a", "base");
    RepositoryService.CreateValidation(fixture.RootPath, Evidence(commit, "tia-sync", "one"));

    var error = Assert.Throws<VcInternalException>(() =>
        RepositoryService.CreateValidation(fixture.RootPath, Evidence(commit, "tia-sync", "two")));

    Assert.Equal("VALIDATION_EXISTS", error.Code);
}
```

- [ ] **Step 2: Run the tests and verify failure**

Run: `dotnet test tests/Mcp.VersionControl.Tests/Mcp.VersionControl.Tests.csproj --no-restore --filter "FullyQualifiedName~ValidationTagStoreTests"`

Expected: FAIL because validation records and tags do not exist.

- [ ] **Step 3: Add versioned evidence records**

```csharp
public sealed record VcObjectFingerprint(
    string Identity,
    string RelativePath,
    string Sha256);

public sealed record VcDeviceValidation(
    string DeviceId,
    string PlcName,
    string ProjectIdentity,
    string ProjectChecksum,
    IReadOnlyList<VcObjectFingerprint> Objects);

public sealed record VcValidationEvidence(
    string SchemaVersion,
    string EvidenceKind,
    string CommitSha,
    string WorkbenchId,
    string? SourceWorktreeId,
    string ConfirmedAt,
    string ConfirmedBy,
    bool MachineValidated,
    IReadOnlyList<VcDeviceValidation> Devices);
```

`ValidationTagStore` must use deterministic `System.Text.Json` serialization, validate `SchemaVersion == "1.0"`, require `EvidenceKind` to be `tia-sync` or `feature-merge`, verify the target commit exists, and use `tia-validation/<full-sha>` as the annotated tag name. Read only annotated tags whose target SHA matches the record. Treat malformed or mismatched data as invalid evidence, not as a successful validation.

- [ ] **Step 4: Expose read/create MCP tools and history state**

Add `vc_validation_get` and `vc_validation_create`. `vc_validation_create` is app-internal and validates that `CommitSha` is current `HEAD`. Add `ValidationState` (`Validated`, `Unlabeled`, `Invalid`) and evidence kind to `VcCommitEntry`; `Log` resolves tags without changing history.

- [ ] **Step 5: Run version-control tests**

Run: `dotnet test tests/Mcp.VersionControl.Tests/Mcp.VersionControl.Tests.csproj --no-restore`

Expected: PASS, including malformed-tag and wrong-target tests.

- [ ] **Step 6: Commit validation evidence primitives**

```bash
git add src/Mcp.VersionControl tests/Mcp.VersionControl.Tests
git commit -m "feat: store immutable TIA validation evidence"
```

### Task 5: Create clean workbenches and copy ignored metadata to features

**Files:**
- Modify: `src/Agent/Workbench/WorkbenchCoordinator.cs`
- Modify: `src/Agent/Workbench/WorkbenchCatalog.cs`
- Modify: `tests/Agent.Tests/WorkbenchCoordinatorTests.cs`
- Modify: `tests/Agent.Tests/WorkbenchCatalogTests.cs`
- Modify: `tests/ApiHost.Tests/WorkbenchEndpointsTests.cs`

- [ ] **Step 1: Write failing creation tests**

```csharp
[Fact]
public async Task CreateWorkbenchCreatesOnlySourceStagingAndRuntimeDeviceArtifacts()
{
    var result = await fixture.Coordinator.CreateWorkbenchAsync(fixture.Request);
    var context = fixture.Resolve(result.Devices[0]);

    Assert.True(Directory.Exists(context.SourceRoot));
    Assert.True(Directory.Exists(context.StagingRoot));
    Assert.False(Directory.Exists(Path.Combine(context.DeviceRoot, "exported-source")));
    Assert.False(Directory.Exists(Path.Combine(context.DeviceRoot, "modified-source")));
}

[Fact]
public async Task CreateFeatureCopiesIgnoredDeviceMetadataFromMaster()
{
    var created = await fixture.CreateWorkbenchAsync();
    var feature = await fixture.Coordinator.CreateWorktreeAsync(
        new(created.Workbench, "feature-a", "feature-a", null));

    Assert.Equal(created.Worktree.DeviceIds, feature.DeviceIds);
    Assert.All(feature.DeviceIds, id =>
        Assert.True(File.Exists(fixture.DeviceMetadataPath(feature.WorktreeId, id))));
}
```

- [ ] **Step 2: Run focused coordinator tests and verify failure**

Run: `dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-restore --filter "FullyQualifiedName~CreateWorkbenchCreatesOnlySource|FullyQualifiedName~CreateFeatureCopiesIgnored"`

Expected: FAIL because creation still makes two roots and feature metadata currently depends on tracked files.

- [ ] **Step 3: Update creation and feature inheritance**

Create only `SourceRoot` and `StagingRoot`. Keep `worktree.json` and `device.json` in their existing operational locations, relying on repository internal excludes. Before adding a feature checkout, load the master registration's worktree/device metadata into memory; after `vc_add_worktree`, write cloned metadata with the new `WorktreeId`. Never discover inherited devices from Git checkout contents.

Remove automatic metadata staging from refresh. Remove `LastReconciliationCommit` writes that exist only to support auto-commit; later plans replace them with validation evidence and pending-sync state.

- [ ] **Step 4: Verify a newly created workbench is clean**

Add an integration assertion through `vc_status` immediately after creation: `Entries` is empty even though `worktree.json`, `device.json`, staging, and the database paths exist.

- [ ] **Step 5: Run Agent and API tests**

Run: `dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-restore`

Run: `dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj --no-restore`

Expected: PASS.

- [ ] **Step 6: Commit clean workbench creation**

```bash
git add src/Agent tests/Agent.Tests tests/ApiHost.Tests
git commit -m "refactor: keep workbench metadata outside PLC history"
```

### Task 6: Move knowledge and source tools to the single source tree

**Files:**
- Modify: `src/Mcp.Knowledge/Tools/KnowledgeTools.cs`
- Modify: `src/Mcp.Knowledge/Import/EffectiveSourceImporter.cs`
- Modify: `src/Mcp.SourceEditor/Tools/SourceEditorTools.cs`
- Modify: `src/Agent/Workbench/DeviceSnapshot.cs`
- Modify: `src/Agent/Workbench/WorkbenchCoordinator.cs`
- Modify: `src/ApiHost/DeviceToolSecurity.cs`
- Modify: `src/Agent/Chat/SystemPrompt.cs`
- Modify: `src/Agent/Chat/SessionManager.cs`
- Modify: `tests/Mcp.Knowledge.Tests/EffectiveSourceImporterTests.cs`
- Modify: `tests/Mcp.Knowledge.Tests/ComponentUpdateToolTests.cs`
- Modify: `tests/Agent.Tests/DeviceSnapshotReaderTests.cs`
- Modify: `tests/ApiHost.Tests/WorkbenchEndpointsTests.cs`

- [ ] **Step 1: Write failing single-root knowledge tests**

```csharp
[Fact]
public void IngestSourceBuildsKnowledgeByCrawlingXmlWithoutManifest()
{
    using var source = new TempExportTree();
    source.AddFixture("Blocks/Main [OB1].xml", "Main [OB1].xml");
    var db = Path.Combine(source.Root, "knowledge.db");

    var result = ToolResults.OkJson(
        new KnowledgeTools().IngestSource(source.Root, db, sourceRoot: source.Root));

    Assert.Equal("crawl", result.GetProperty("sourceMode").GetString());
    Assert.True(File.Exists(db));
}
```

Add a component-update test that changes one XML file directly in `sourceRoot` and updates the matching database component without an overlay or manifest.

- [ ] **Step 2: Run Knowledge tests and verify failure**

Run: `dotnet test tests/Mcp.Knowledge.Tests/Mcp.Knowledge.Tests.csproj --no-restore --filter "FullyQualifiedName~IngestSourceBuildsKnowledgeByCrawlingXmlWithoutManifest|FullyQualifiedName~SingleSource"`

Expected: FAIL because the public contract expects exported/modified roots.

- [ ] **Step 3: Add a first-class `sourceRoot` contract**

Keep `exportRoot` as compatibility input for non-workbench callers, but make workbench calls bind only `sourceRoot` and `dbPath`. `IngestSource` uses `ExportFolderCrawler` when no manifest exists. Change `UpdateComponents` to resolve selected relative paths directly under `sourceRoot` and validate existing database provenance before mutation.

In `WorkbenchCoordinator`, pass:

```csharp
new
{
    sourceRoot = device.SourceRoot,
    dbPath = device.KnowledgeDbPath,
}
```

Update `DeviceSnapshotReader` to crawl XML and derive name/type/number/path from the XML parser and filename fallback. Remove overlay count; expose `sourceObjectCount` instead.

- [ ] **Step 4: Bind editor, parser, import, and knowledge paths to `SourceRoot`**

`DeviceToolArgumentBinder` resolves every readable or writable XML path under `SourceRoot`. `src_apply_edits` writes in place atomically with `inPlace=true` and `confirmInPlace=true`. Update `SourceEditorTools` descriptions and root validation to refer to the selected device source root, not `modified-source`. `import_block` requires an existing committed-source relative path; plan 3 adds commit and overlap checks before execution. Update the system prompt and session context to say `PLC source` once, removing baseline/overlay claims.

- [ ] **Step 5: Run Knowledge, Agent, and API tests**

Run: `dotnet test tests/Mcp.Knowledge.Tests/Mcp.Knowledge.Tests.csproj --no-restore`

Run: `dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-restore`

Run: `dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj --no-restore`

Expected: PASS.

- [ ] **Step 6: Commit single-source knowledge integration**

```bash
git add src/Mcp.Knowledge src/Mcp.SourceEditor src/Agent src/ApiHost tests/Mcp.Knowledge.Tests tests/Mcp.SourceEditor.Tests tests/Agent.Tests tests/ApiHost.Tests
git commit -m "refactor: build knowledge from tracked PLC source"
```

### Task 7: Enforce feature editing and expose selected commits through the API

**Files:**
- Create: `src/Agent/Workbench/WorkbenchWritePolicy.cs`
- Create: `tests/Agent.Tests/WorkbenchWritePolicyTests.cs`
- Modify: `src/ApiHost/DeviceToolSecurity.cs`
- Modify: `src/ApiHost/WorkbenchApiModels.cs`
- Modify: `tests/ApiHost.Tests/WorkbenchEndpointsTests.cs`
- Modify: `studio/src/api/client.ts`

- [ ] **Step 1: Write failing master/feature policy tests**

```csharp
[Fact]
public void NormalEditIsRejectedOnMaster()
{
    var policy = fixture.PolicyFor(branch: "master");
    var error = Assert.Throws<WorkbenchLifecycleException>(() =>
        policy.RequireFeatureEdit(fixture.Context));
    Assert.Equal("MASTER_EDIT_NOT_ALLOWED", error.Code);
}

[Fact]
public void NormalEditIsAllowedOnFeature()
{
    var policy = fixture.PolicyFor(branch: "feature-a");
    policy.RequireFeatureEdit(fixture.Context);
}
```

- [ ] **Step 2: Run tests and verify failure**

Run: `dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-restore --filter "FullyQualifiedName~WorkbenchWritePolicyTests"`

Expected: FAIL because the policy does not exist.

- [ ] **Step 3: Implement one write policy**

`WorkbenchWritePolicy` reads the ignored `worktree.json`, treats branch `master` as protected, and exposes `RequireFeatureEdit(DeviceContext)`. Plan 2 will add hash-bound pending TIA authorization. Inject the policy into `DeviceToolArgumentBinder` and the prepare-edit API path so UI and MCP editing have the same backend rule.

- [ ] **Step 4: Replace device-scoped Git mutation endpoints**

Add worktree-level endpoints:

```text
GET  /api/workbenches/{workbenchId}/worktrees/{worktreeId}/vc/status
GET  /api/workbenches/{workbenchId}/worktrees/{worktreeId}/vc/log
GET  /api/workbenches/{workbenchId}/worktrees/{worktreeId}/vc/diff
POST /api/workbenches/{workbenchId}/worktrees/{worktreeId}/vc/commit
GET  /api/workbenches/{workbenchId}/worktrees/{worktreeId}/vc/validation/{sha}
```

The commit body is:

```csharp
public sealed record CommitSourceApiRequest(string[] Paths, string Message);
```

Resolve the worktree root once; do not require a selected device. Forward `oldSha` and `newSha` for diff. Keep old device endpoints only as temporary compatibility wrappers with no use from Studio.

- [ ] **Step 5: Implement recovery for unauthorized master edits**

Add `MoveUnauthorizedMasterChangesAsync(workbenchId, paths, featureName)` and `DiscardUnauthorizedMasterChangesAsync(workbenchId, paths)`. For move, capture the selected file bytes and hashes under ignored `.automation/recovery`, create a feature from current master, write and verify the captured XML in the feature, then restore the selected master paths to HEAD. If feature creation or verification fails, leave master untouched. For discard, require the API's destructive confirmation and restore only the selected XML paths. Neither operation creates a master commit.

Expose:

```text
POST /api/workbenches/{workbenchId}/worktrees/{masterId}/vc/unauthorized/move
POST /api/workbenches/{workbenchId}/worktrees/{masterId}/vc/unauthorized/discard
```

- [ ] **Step 6: Update TypeScript contracts and API contract tests**

Make `DeviceInfo` expose `sourceRoot`; remove `exportedSourceRoot` and `modifiedSourceRoot`. Add worktree-level `getVcStatus`, `getVcLog`, `getVcDiff`, and `commitVcPaths`. Add API tests proving selected paths and both historical SHAs reach the MCP caller.

- [ ] **Step 7: Run all baseline suites**

Run: `dotnet test AgentAssistPlcDev.sln --no-restore --verbosity minimal`

Run: `npm test` from `studio`

Run: `npm run build` from `studio`

Expected: all tests and the production build pass.

- [ ] **Step 8: Update current workflow documentation and commit**

Replace `docs/version-control-workflow.md` with the implemented foundation: one tracked `source/`, internal excludes, manual selected commits, and no overlay/auto-commit. State that TIA validation workflows arrive in plans 2 and 3.

```bash
git add src/Agent src/ApiHost studio/src/api/client.ts tests docs/version-control-workflow.md
git commit -m "feat: expose protected PLC source commits"
```

## Plan 1 completion gate

Do not start plan 2 until a newly created workbench is clean, feature edits modify only `source/`, master editor writes are rejected, selected XML commits work without persistent staging, knowledge ingest needs no manifest, and validation tags round-trip immutably.
