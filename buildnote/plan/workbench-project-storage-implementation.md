# Workbench Project Storage Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the global project-name export directory with user-created workbench projects containing a shared bare Git repository, complete linked engineering worktrees, device-scoped exported and modified source, safe staged reconciliation, and one knowledge database per device.

**Architecture:** Add a storage/context domain to `Agent` and make it the only component that resolves workbench, worktree, and device paths. Extend `Mcp.VersionControl` for bare-repository and linked-worktree operations, keep complete PLC exports in ignored staging directories, and reconcile only approved content differences into tracked baselines. Extend `Mcp.Knowledge` with source provenance so a device database can rebuild from baseline plus overlay and transactionally replace selected components.

**Tech Stack:** C#/.NET 8, ASP.NET Core minimal APIs, MCP .NET SDK 1.4.1, LibGit2Sharp 0.27, SQLite, xUnit.

**Scope boundary:** This plan implements the storage domain, MCP capabilities, backend orchestration, and backend APIs. Studio/React UI changes are deliberately deferred and recorded in `buildnote/plan/workbench-project-storage-future-ui.md`.

---

## File Structure

### New domain files

- `src/Agent/Workbench/WorkbenchModels.cs` — versioned metadata and immutable context records.
- `src/Agent/Workbench/WorkbenchPaths.cs` — default/custom root resolution, safe directory names, and containment checks.
- `src/Agent/Workbench/AtomicJsonStore.cs` — atomic JSON reads/writes with schema validation.
- `src/Agent/Workbench/WorkbenchCatalog.cs` — create/load/list workbenches and resolve IDs.
- `src/Agent/Workbench/DeviceSourceResolver.cs` — exported/modified/effective source resolution.
- `src/Agent/Workbench/ReconciliationModels.cs` — immutable preview, entries, hashes, and outcomes.
- `src/Agent/Workbench/DeviceReconciler.cs` — compare staging with baseline and apply an approved preview.
- `src/Agent/Workbench/DeviceOperationLock.cs` — per-device mutation serialization.
- `src/Agent/Workbench/WorkbenchCoordinator.cs` — application orchestration across engineering, knowledge, and version-control MCP clients.

### Existing backend files

- `src/Agent/AssistantPaths.cs` — retain only legacy helpers during transition and mark them legacy.
- `src/Agent/Chat/SessionFileFormat.cs` — persist workbench/worktree/device IDs instead of project-name-derived paths.
- `src/Agent/Chat/SessionManager.cs` — store sessions beneath the selected worktree’s ignored `.automation/sessions`.
- `src/Agent/Workflows/ReadProjectContextWorkflow.cs` — accept an explicit device context and use its paths.
- `src/Agent/Workflows/ReadProjectContextResult.cs` — return workbench/worktree/device identity and device database path.
- `src/ApiHost/Program.cs` — replace name-based selection/path auto-fill with explicit context endpoints and coordinator calls.
- `src/Contracts/Sandbox/SandboxPolicy.cs` — classify new Git and knowledge mutation tools.

### Version control files

- `src/Mcp.VersionControl/Git/Models.cs` — linked-worktree and merge result contracts.
- `src/Mcp.VersionControl/Git/RepositoryService.cs` — initialize shared bare repository, create/remove worktrees, and merge branches.
- `src/Mcp.VersionControl/Tools/VersionControlTools.cs` — expose the new MCP operations.

### Knowledge files

- `src/Contracts/Knowledge/KnowledgeUpdateResult.cs` — full/partial device update result.
- `src/Mcp.Knowledge/Import/ComponentImport.cs` — component identity plus touched node/edge provenance.
- `src/Mcp.Knowledge/Import/EffectiveSourceImporter.cs` — baseline manifest with sparse overlay substitution.
- `src/Mcp.Knowledge/Graph/SemanticPlcGraph.cs` — return touched IDs from upserts.
- `src/Mcp.Knowledge/Graph/ComponentProvenanceStore.cs` — provenance schema and transactional component replacement.
- `src/Mcp.Knowledge/Tools/KnowledgeTools.cs` — device rebuild parameters and `update_components` tool.

### Tests

- `tests/Agent.Tests/WorkbenchPathsTests.cs`
- `tests/Agent.Tests/AtomicJsonStoreTests.cs`
- `tests/Agent.Tests/WorkbenchCatalogTests.cs`
- `tests/Agent.Tests/DeviceSourceResolverTests.cs`
- `tests/Agent.Tests/DeviceReconcilerTests.cs`
- `tests/Agent.Tests/WorkbenchCoordinatorTests.cs`
- `tests/Agent.Tests/SessionManagerTests.cs`
- `tests/Agent.Tests/ReadProjectContextWorkflowTests.cs`
- `tests/Mcp.VersionControl.Tests/LinkedWorktreeTests.cs`
- `tests/Mcp.Knowledge.Tests/EffectiveSourceImporterTests.cs`
- `tests/Mcp.Knowledge.Tests/ComponentUpdateToolTests.cs`
- `tests/Contracts.Tests/SandboxPolicyTests.cs`
- `scripts/e2e-workbench.json`

## Task 1: Introduce stable workbench contexts and safe path resolution

**Files:**
- Create: `src/Agent/Workbench/WorkbenchModels.cs`
- Create: `src/Agent/Workbench/WorkbenchPaths.cs`
- Create: `tests/Agent.Tests/WorkbenchPathsTests.cs`
- Modify: `src/Agent/AssistantPaths.cs`

- [ ] **Step 1: Write failing path and identity tests**

```csharp
[Fact]
public void DefaultRootUsesAutomationWorkbenchProjectAndSanitizedName()
{
    var root = WorkbenchPaths.DefaultRoot("Line:1");
    Assert.EndsWith(Path.Combine("AutomationWorkbench", "Project", "Line_1"), root);
}

[Fact]
public void DeviceContextExposesOnlyDeviceScopedPaths()
{
    var context = WorkbenchPaths.ResolveDevice(
        @"D:\wb", "wt-1", "feature-a", "dev-1", "PLC:1");

    Assert.Equal(@"D:\wb\worktrees\feature-a\devices\PLC_1", context.DeviceRoot);
    Assert.Equal(Path.Combine(context.DeviceRoot, "exported-source"), context.ExportedSourceRoot);
    Assert.Equal(Path.Combine(context.DeviceRoot, "modified-source"), context.ModifiedSourceRoot);
    Assert.Equal(Path.Combine(context.DeviceRoot, "staging"), context.StagingRoot);
    Assert.Equal(Path.Combine(context.DeviceRoot, "plc-knowledge.db"), context.KnowledgeDbPath);
}

[Fact]
public void ResolveRelativeRejectsTraversal()
{
    Assert.Throws<WorkbenchPathException>(() =>
        WorkbenchPaths.ResolveRelative(@"D:\wb\worktrees\master", @"..\..\escape.xml"));
}
```

- [ ] **Step 2: Run the focused tests and verify they fail**

Run:

```powershell
dotnet test tests/Agent.Tests/Agent.Tests.csproj --filter FullyQualifiedName~WorkbenchPathsTests
```

Expected: FAIL because `WorkbenchPaths`, `DeviceContext`, and `WorkbenchPathException` do not exist.

- [ ] **Step 3: Add the versioned models**

Define these records in `WorkbenchModels.cs`:

```csharp
public sealed record WorkbenchMetadata(
    string SchemaVersion,
    string WorkbenchId,
    string Name,
    string CreatedAt,
    string RootPath,
    string RepositoryPath,
    string? EngineeringProjectId,
    string? SourceProjectPath,
    IReadOnlyList<WorkbenchWorktreeRegistration> Worktrees);

public sealed record WorkbenchWorktreeRegistration(
    string WorktreeId, string Name, string Branch, string RelativePath);

public sealed record WorktreeMetadata(
    string SchemaVersion,
    string WorktreeId,
    string WorkbenchId,
    string Name,
    string Branch,
    string CreatedAt,
    string? BaseCommit,
    string? EngineeringProjectId,
    string? SourceProjectPath,
    IReadOnlyList<string> DeviceIds,
    string? LastReconciliationCommit);

public sealed record DeviceMetadata(
    string SchemaVersion,
    string DeviceId,
    string WorktreeId,
    string PlcName,
    string EngineeringIdentity,
    string? LastExportChecksum,
    string? LastExportUtc,
    string? LastReconciliationCommit,
    KnowledgeState Knowledge,
    IReadOnlyList<DeviceImportRecord> Imports);

public sealed record KnowledgeState(
    bool Stale,
    IReadOnlyDictionary<string, string> AppliedOverlayHashes,
    string? UpdatedAt);

public sealed record DeviceContext(
    string WorkbenchId,
    string WorktreeId,
    string DeviceId,
    string WorkbenchRoot,
    string WorktreeRoot,
    string DeviceRoot,
    string ExportedSourceRoot,
    string ModifiedSourceRoot,
    string StagingRoot,
    string KnowledgeDbPath);
```

Use schema version `"1.0"` and ISO-8601 UTC timestamps.

- [ ] **Step 4: Implement safe paths**

`WorkbenchPaths` must:

- default to `%LOCALAPPDATA%\AutomationWorkbench\Project\<sanitized-name>`;
- replace invalid filename characters with `_`;
- reject blank, `"."`, and `".."` names;
- canonicalize custom roots with `Path.GetFullPath`;
- resolve registered relative paths only below their parent;
- reject existing reparse points in any traversed segment;
- provide `ResolveWorkbench`, `ResolveWorktree`, `ResolveDevice`, and `ResolveRelative`.

Keep `AssistantPaths.ResolveExportRoot` and `ResolveKnowledgeDbPath` unchanged but mark their XML documentation as legacy-only so existing tests remain valid until Task 10 removes production callers.

- [ ] **Step 5: Run tests**

Run:

```powershell
dotnet test tests/Agent.Tests/Agent.Tests.csproj --filter "FullyQualifiedName~WorkbenchPathsTests|FullyQualifiedName~AssistantPathsTests"
```

Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add src/Agent/Workbench/WorkbenchModels.cs src/Agent/Workbench/WorkbenchPaths.cs src/Agent/AssistantPaths.cs tests/Agent.Tests/WorkbenchPathsTests.cs
git commit -m "feat(workbench): add stable contexts and safe paths"
```

## Task 2: Add atomic metadata storage and a workbench catalog

**Files:**
- Create: `src/Agent/Workbench/AtomicJsonStore.cs`
- Create: `src/Agent/Workbench/WorkbenchCatalog.cs`
- Create: `tests/Agent.Tests/AtomicJsonStoreTests.cs`
- Create: `tests/Agent.Tests/WorkbenchCatalogTests.cs`

- [ ] **Step 1: Write failing metadata tests**

Cover:

```csharp
[Fact]
public void WriteThenReadRoundTripsWorkbenchMetadata()
{
    var metadata = new WorkbenchMetadata(
        "1.0", "wb-1", "Line 1", "2026-07-27T00:00:00.0000000Z",
        root, Path.Combine(root, "repository.git"), "eng-1", @"D:\TIA\Line1.ap17",
        Array.Empty<WorkbenchWorktreeRegistration>());

    store.Write(path, metadata);
    var loaded = store.Read<WorkbenchMetadata>(path);

    Assert.Equal(metadata, loaded);
}

[Fact]
public void UnsupportedSchemaDoesNotGetOverwritten()
{
    File.WriteAllText(path, """{"schemaVersion":"99.0"}""");
    Assert.Throws<MetadataSchemaException>(() => store.Read<WorkbenchMetadata>(path));
}

[Fact]
public void CatalogCreateRejectsExistingNonEmptyDirectory()
{
    Directory.CreateDirectory(root);
    File.WriteAllText(Path.Combine(root, "sentinel.txt"), "keep");

    var error = Assert.Throws<WorkbenchCatalogException>(() =>
        catalog.Create("Line 1", root));

    Assert.Equal("WORKBENCH_CONFLICT", error.Code);
    Assert.True(File.Exists(Path.Combine(root, "sentinel.txt")));
}

[Fact]
public void CatalogUsesCustomRootWithoutMovingItUnderDefaultRoot()
{
    var custom = Path.Combine(testRoot, "chosen", "Line1");
    var created = catalog.Create("Line 1", custom);

    Assert.Equal(Path.GetFullPath(custom), created.RootPath);
    Assert.True(File.Exists(Path.Combine(custom, "workbench.json")));
}
```

Use a disposable test directory; do not write into `%LOCALAPPDATA%`.

- [ ] **Step 2: Verify failure**

Run:

```powershell
dotnet test tests/Agent.Tests/Agent.Tests.csproj --filter "FullyQualifiedName~AtomicJsonStoreTests|FullyQualifiedName~WorkbenchCatalogTests"
```

Expected: FAIL because the stores do not exist.

- [ ] **Step 3: Implement atomic JSON persistence**

`AtomicJsonStore` exposes:

```csharp
public T Read<T>(string path);
public T? TryRead<T>(string path);
public void Write<T>(string path, T value);
```

Serialize camelCase, indented JSON to a sibling `.<filename>.<guid>.tmp`, flush it, and replace/move it into place. Delete the temporary file after failure. Validate `schemaVersion == "1.0"` before returning any metadata model.

- [ ] **Step 4: Implement catalog creation and loading**

`WorkbenchCatalog` exposes:

```csharp
public WorkbenchMetadata Create(string name, string? requestedRoot);
public WorkbenchMetadata Load(string workbenchRoot);
public IReadOnlyList<WorkbenchMetadata> ListDefaultRoot();
public WorkbenchMetadata RegisterWorktree(
    WorkbenchMetadata workbench, WorkbenchWorktreeRegistration registration);
public DeviceContext ResolveDevice(
    WorkbenchMetadata workbench, WorktreeMetadata worktree, DeviceMetadata device);
```

Creation writes only `workbench.json` and creates `worktrees`; repository initialization belongs to Task 3. On partial failure, remove only directories created by this invocation and only when still empty.

- [ ] **Step 5: Run tests**

Run:

```powershell
dotnet test tests/Agent.Tests/Agent.Tests.csproj --filter "FullyQualifiedName~AtomicJsonStoreTests|FullyQualifiedName~WorkbenchCatalogTests"
```

Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add src/Agent/Workbench/AtomicJsonStore.cs src/Agent/Workbench/WorkbenchCatalog.cs tests/Agent.Tests/AtomicJsonStoreTests.cs tests/Agent.Tests/WorkbenchCatalogTests.cs
git commit -m "feat(workbench): persist versioned metadata"
```

## Task 3: Support shared bare repositories and linked Git worktrees

**Files:**
- Modify: `src/Mcp.VersionControl/Git/Models.cs`
- Modify: `src/Mcp.VersionControl/Git/RepositoryService.cs`
- Modify: `src/Mcp.VersionControl/Tools/VersionControlTools.cs`
- Create: `tests/Mcp.VersionControl.Tests/LinkedWorktreeTests.cs`
- Modify: `src/Contracts/Sandbox/SandboxPolicy.cs`
- Modify: `tests/Contracts.Tests/SandboxPolicyTests.cs`

- [ ] **Step 1: Write failing linked-worktree tests**

Test this exact lifecycle:

```csharp
var init = RepositoryService.InitShared(workbenchRoot, masterPath);
Assert.True(Directory.Exists(Path.Combine(workbenchRoot, "repository.git")));
Assert.True(File.Exists(Path.Combine(masterPath, ".git")));

File.WriteAllText(Path.Combine(masterPath, "seed.txt"), "seed");
RepositoryService.Add(masterPath);
var first = RepositoryService.Commit(masterPath, "initial", null);

var feature = RepositoryService.AddWorktree(
    Path.Combine(workbenchRoot, "repository.git"),
    featurePath, "feature-a", first.Sha);
Assert.Equal("feature-a", feature.Branch);
Assert.Equal("seed", File.ReadAllText(Path.Combine(featurePath, "seed.txt")));

File.WriteAllText(Path.Combine(featurePath, "change.txt"), "feature");
RepositoryService.Add(featurePath);
RepositoryService.Commit(featurePath, "feature change", null);
var merge = RepositoryService.Merge(masterPath, "feature-a");
Assert.True(merge.Merged);
Assert.Equal("feature", File.ReadAllText(Path.Combine(masterPath, "change.txt")));
```

Also test dirty-master merge rejection, duplicate branch/worktree rejection, and containment rejection.

- [ ] **Step 2: Verify failure**

Run:

```powershell
dotnet test tests/Mcp.VersionControl.Tests/Mcp.VersionControl.Tests.csproj --filter FullyQualifiedName~LinkedWorktreeTests
```

Expected: FAIL because the worktree methods do not exist.

- [ ] **Step 3: Implement Git service methods**

Add:

```csharp
public static VcSharedInitResult InitShared(string workbenchRoot, string masterWorktreePath);
public static VcWorktreeResult AddWorktree(
    string repositoryPath, string worktreePath, string branchName, string? startPoint);
public static VcWorktreeListResult Worktrees(string repositoryPath);
public static VcMergeResult Merge(string targetWorktreePath, string sourceBranch);
```

Use LibGit2Sharp where its 0.27 API supports the operation. If linked-worktree creation is unsupported, invoke `git` through `ProcessStartInfo` with `ArgumentList` (never a shell command string), capture stdout/stderr, and map non-zero exit codes to `VcInternalException`. Commands are:

```text
git init --bare <repository.git>
git --git-dir <repository.git> worktree add --orphan master <master-path>
git --git-dir <repository.git> worktree add -b <branch> <path> <start-point>
git -C <target-worktree> merge --no-ff <source-branch>
```

After creating `master`, write `.gitignore` with:

```gitignore
**/staging/
**/plc-knowledge.db
**/plc-knowledge.db-*
.automation/
```

- [ ] **Step 4: Expose and classify MCP tools**

Add:

```csharp
vc_init_shared(workbenchRoot, masterWorktreePath)
vc_add_worktree(repositoryPath, worktreePath, branchName, startPoint?)
vc_worktrees(repositoryPath)
vc_merge(targetWorktreePath, sourceBranch)
```

Classify init/add/merge as `Write` and listing as `Read`. Add all four names to `EveryCurrentMcpToolIsClassified`.

- [ ] **Step 5: Run tests**

Run:

```powershell
dotnet test tests/Mcp.VersionControl.Tests/Mcp.VersionControl.Tests.csproj
dotnet test tests/Contracts.Tests/Contracts.Tests.csproj
```

Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add src/Mcp.VersionControl src/Contracts/Sandbox/SandboxPolicy.cs tests/Mcp.VersionControl.Tests/LinkedWorktreeTests.cs tests/Contracts.Tests/SandboxPolicyTests.cs
git commit -m "feat(git): manage shared engineering worktrees"
```

## Task 4: Build immutable export previews and approved reconciliation

**Files:**
- Create: `src/Agent/Workbench/ReconciliationModels.cs`
- Create: `src/Agent/Workbench/DeviceReconciler.cs`
- Create: `src/Agent/Workbench/DeviceOperationLock.cs`
- Create: `tests/Agent.Tests/DeviceReconcilerTests.cs`

- [ ] **Step 1: Write failing reconciliation tests**

Create staging and baseline fixtures covering:

- identical file remains byte- and timestamp-untouched;
- added/changed/removed files appear in preview;
- applying without approval throws `RECONCILIATION_APPROVAL_REQUIRED`;
- a changed staging hash after preview throws `RECONCILIATION_PREVIEW_STALE`;
- only approved removals are deleted;
- `modified-source` is never read or written by reconciliation;
- malformed/missing staging manifest prevents preview;
- two mutations for the same device are serialized.

Use these contracts:

```csharp
public enum ReconciliationChangeKind { Added, Changed, Removed, Unchanged }

public sealed record ReconciliationEntry(
    string RelativePath,
    ReconciliationChangeKind Kind,
    string? BaselineHash,
    string? StagingHash,
    string? ComponentIdentity);

public sealed record ReconciliationPreview(
    string PreviewId,
    string WorktreeId,
    string DeviceId,
    string BaselineTreeHash,
    string StagingTreeHash,
    IReadOnlyList<ReconciliationEntry> Entries);
```

- [ ] **Step 2: Verify failure**

Run:

```powershell
dotnet test tests/Agent.Tests/Agent.Tests.csproj --filter FullyQualifiedName~DeviceReconcilerTests
```

Expected: FAIL.

- [ ] **Step 3: Implement preview generation**

`DeviceReconciler.Preview(DeviceContext context)` must:

- require a valid staging `metadata.json`;
- enumerate manifest-controlled files by normalized relative path;
- SHA-256 hash staging and baseline contents;
- detect removed baseline manifest entries;
- compute deterministic tree hashes from sorted `relativePath + contentHash`;
- return a preview without modifying tracked files.

- [ ] **Step 4: Implement approved apply**

Expose:

```csharp
public ReconciliationOutcome Apply(
    DeviceContext context,
    ReconciliationPreview approvedPreview,
    IReadOnlySet<string> approvedRemovalPaths);
```

Recompute both tree hashes before any write. Apply additions/changes through temporary sibling files and atomic moves. Delete only removed paths listed in `approvedRemovalPaths`. Write the staging manifest into the baseline last. Return exact changed paths for Git staging.

- [ ] **Step 5: Run tests**

Run:

```powershell
dotnet test tests/Agent.Tests/Agent.Tests.csproj --filter FullyQualifiedName~DeviceReconcilerTests
```

Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add src/Agent/Workbench/ReconciliationModels.cs src/Agent/Workbench/DeviceReconciler.cs src/Agent/Workbench/DeviceOperationLock.cs tests/Agent.Tests/DeviceReconcilerTests.cs
git commit -m "feat(workbench): reconcile staged PLC exports safely"
```

## Task 5: Add sparse modified-source overlays

**Files:**
- Create: `src/Agent/Workbench/DeviceSourceResolver.cs`
- Create: `tests/Agent.Tests/DeviceSourceResolverTests.cs`
- Modify: `src/Mcp.SourceEditor/Tools/SourceEditorTools.cs`
- Modify: `tests/Mcp.SourceEditor.Tests/SourceEditorToolsTests.cs`

- [ ] **Step 1: Write failing overlay tests**

Test:

```csharp
Assert.Equal(baselinePath, resolver.ResolveEffective(context, "Blocks/Main.xml"));

var editable = resolver.PrepareEditable(context, "Blocks/Main.xml");
Assert.Equal(modifiedPath, editable);
Assert.Equal(File.ReadAllText(baselinePath), File.ReadAllText(modifiedPath));
Assert.Equal(modifiedPath, resolver.ResolveEffective(context, "Blocks/Main.xml"));
```

Also verify:

- preparing an already modified file does not recopy the baseline;
- new relative paths can be created only under `modified-source`;
- traversal, rooted paths, and reparse-point escapes fail;
- an edit marks `DeviceMetadata.Knowledge.Stale` true;
- source-editor APIs reject output paths in `exported-source`.

- [ ] **Step 2: Verify failure**

Run:

```powershell
dotnet test tests/Agent.Tests/Agent.Tests.csproj --filter FullyQualifiedName~DeviceSourceResolverTests
```

Expected: FAIL.

- [ ] **Step 3: Implement overlay resolution**

Add:

```csharp
public string ResolveEffective(DeviceContext context, string relativePath);
public string PrepareEditable(DeviceContext context, string relativePath);
public IReadOnlyList<string> EnumerateModified(DeviceContext context);
```

`PrepareEditable` atomically copies the exported file only when the overlay does not exist. It invokes a metadata callback to mark knowledge stale after a successful creation or write.

- [ ] **Step 4: Restrict source-editor integration**

API-host callers must pass the prepared overlay path as `outputFilePath`. Add defense in `SourceEditorTools` so a composed edit may read baseline/effective input but may write only to an allowed modified-source root supplied by the host sandbox. Do not permit direct baseline overwrite.

- [ ] **Step 5: Run tests**

Run:

```powershell
dotnet test tests/Agent.Tests/Agent.Tests.csproj --filter FullyQualifiedName~DeviceSourceResolverTests
dotnet test tests/Mcp.SourceEditor.Tests/Mcp.SourceEditor.Tests.csproj
```

Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add src/Agent/Workbench/DeviceSourceResolver.cs tests/Agent.Tests/DeviceSourceResolverTests.cs src/Mcp.SourceEditor tests/Mcp.SourceEditor.Tests
git commit -m "feat(source): add sparse device edit overlays"
```

## Task 6: Add component provenance to device knowledge databases

**Files:**
- Create: `src/Mcp.Knowledge/Import/ComponentImport.cs`
- Create: `src/Mcp.Knowledge/Graph/ComponentProvenanceStore.cs`
- Modify: `src/Mcp.Knowledge/Graph/SemanticPlcGraph.cs`
- Modify: `src/Mcp.Knowledge/Import/ManifestImporter.cs`
- Modify: `src/Mcp.Knowledge/Import/ExportFolderCrawler.cs`
- Create: `tests/Mcp.Knowledge.Tests/ComponentUpdateToolTests.cs`

- [ ] **Step 1: Write failing provenance tests**

Create two source components sharing a referenced symbol/callee. Assert:

1. Full save records each component’s owned node and edge IDs.
2. Replacing component A removes A-owned networks and edges.
3. Nodes still referenced or owned by component B remain.
4. A malformed replacement rolls back all A deletions.
5. A path/identity mismatch returns `COMPONENT_IDENTITY_MISMATCH`.

- [ ] **Step 2: Verify failure**

Run:

```powershell
dotnet test tests/Mcp.Knowledge.Tests/Mcp.Knowledge.Tests.csproj --filter FullyQualifiedName~ComponentUpdateToolTests
```

Expected: FAIL because provenance tables and replacement do not exist.

- [ ] **Step 3: Track touched graph IDs during import**

Introduce:

```csharp
public sealed record ComponentImport(
    string ComponentKey,
    string RelativePath,
    string ContentHash,
    IReadOnlySet<string> NodeIds,
    IReadOnlySet<string> EdgeIds);
```

Make graph upserts report touched IDs. Each manifest component import captures every node/edge it creates or upserts, including shared symbol and placeholder nodes.

- [ ] **Step 4: Add provenance tables**

Extend the SQLite schema with:

```sql
CREATE TABLE source_components (
  component_key TEXT PRIMARY KEY,
  relative_path TEXT NOT NULL UNIQUE,
  content_hash TEXT NOT NULL
);
CREATE TABLE source_component_nodes (
  component_key TEXT NOT NULL,
  node_id TEXT NOT NULL,
  PRIMARY KEY (component_key, node_id)
);
CREATE TABLE source_component_edges (
  component_key TEXT NOT NULL,
  edge_id TEXT NOT NULL,
  PRIMARY KEY (component_key, edge_id)
);
```

`ComponentProvenanceStore.Replace` performs one transaction:

1. parse replacement into an isolated graph;
2. delete the old component’s edge ownership;
3. delete old edges with no remaining owner;
4. delete old node ownership;
5. delete old nodes with no remaining owner and no remaining incident edge;
6. upsert replacement nodes/edges/properties;
7. insert new ownership and source hash;
8. commit.

- [ ] **Step 5: Run all knowledge tests**

Run:

```powershell
dotnet test tests/Mcp.Knowledge.Tests/Mcp.Knowledge.Tests.csproj
```

Expected: PASS with the existing graph behavior unchanged.

- [ ] **Step 6: Commit**

```powershell
git add src/Mcp.Knowledge/Import src/Mcp.Knowledge/Graph tests/Mcp.Knowledge.Tests/ComponentUpdateToolTests.cs
git commit -m "feat(knowledge): track component graph provenance"
```

## Task 7: Rebuild and partially update one device database

**Files:**
- Create: `src/Contracts/Knowledge/KnowledgeUpdateResult.cs`
- Create: `src/Mcp.Knowledge/Import/EffectiveSourceImporter.cs`
- Modify: `src/Mcp.Knowledge/Tools/KnowledgeTools.cs`
- Create: `tests/Mcp.Knowledge.Tests/EffectiveSourceImporterTests.cs`
- Modify: `tests/Mcp.Knowledge.Tests/ComponentUpdateToolTests.cs`
- Modify: `src/Contracts/Sandbox/SandboxPolicy.cs`
- Modify: `tests/Contracts.Tests/SandboxPolicyTests.cs`

- [ ] **Step 1: Write failing effective-source tests**

Build one device fixture:

```text
exported-source/metadata.json
exported-source/Blocks/A.xml
exported-source/Blocks/B.xml
modified-source/Blocks/A.xml
```

Assert a full rebuild imports modified A and baseline B into the device DB. Assert `update_components` replaces only A, records its new hash, and returns the list of updated component keys.

- [ ] **Step 2: Verify failure**

Run:

```powershell
dotnet test tests/Mcp.Knowledge.Tests/Mcp.Knowledge.Tests.csproj --filter "FullyQualifiedName~EffectiveSourceImporterTests|FullyQualifiedName~ComponentUpdateToolTests"
```

Expected: FAIL.

- [ ] **Step 3: Implement overlay-aware full import**

Add:

```csharp
EffectiveSourceImportResult Import(
    string exportedSourceRoot,
    string modifiedSourceRoot);
```

Read the baseline manifest as authoritative. For each manifest relative path, use the overlay file when present. Include overlay-only new components after classifying and validating them. Reject an overlay whose XML identity differs from the baseline identity at the same relative path.

- [ ] **Step 4: Change knowledge tool contracts**

Replace project-root assumptions with:

```csharp
ingest_source(
    string exportedSourceRoot,
    string dbPath,
    string? modifiedSourceRoot = null)

update_components(
    string exportedSourceRoot,
    string modifiedSourceRoot,
    string dbPath,
    string[] relativePaths)
```

`update_components` requires at least one path, normalizes/deduplicates paths, rejects paths absent from `modified-source`, and applies each component transactionally. Return:

```csharp
public sealed record KnowledgeUpdateResult(
    string DbPath,
    string[] UpdatedComponents,
    IReadOnlyDictionary<string, string> AppliedHashes,
    string[] Warnings);
```

- [ ] **Step 5: Classify the tool**

Classify `update_components` as `Write` because it mutates SQLite. Keep `ingest_source` at its existing tier for compatibility unless the sandbox policy is separately reclassified for all derived-database writes.

- [ ] **Step 6: Run tests**

Run:

```powershell
dotnet test tests/Mcp.Knowledge.Tests/Mcp.Knowledge.Tests.csproj
dotnet test tests/Contracts.Tests/Contracts.Tests.csproj
```

Expected: PASS.

- [ ] **Step 7: Commit**

```powershell
git add src/Contracts/Knowledge src/Mcp.Knowledge tests/Mcp.Knowledge.Tests src/Contracts/Sandbox/SandboxPolicy.cs tests/Contracts.Tests/SandboxPolicyTests.cs
git commit -m "feat(knowledge): update device databases from overlays"
```

## Task 8: Orchestrate creation, refresh, commit, and import

**Files:**
- Create: `src/Agent/Workbench/WorkbenchCoordinator.cs`
- Create: `tests/Agent.Tests/WorkbenchCoordinatorTests.cs`
- Modify: `src/Agent/Workflows/ReadProjectContextWorkflow.cs`
- Modify: `src/Agent/Workflows/ReadProjectContextResult.cs`
- Modify: `tests/Agent.Tests/ReadProjectContextWorkflowTests.cs`

- [ ] **Step 1: Write failing coordinator tests**

Use fake MCP callers to verify exact call order:

```text
create:
vc_init_shared -> engineering connect/get_project_info -> metadata/device creation

refresh preview:
engineering rebuild_export(outputDir=device.stagingRoot, plcName=device.plcName)
-> local preview only

approved refresh:
local apply -> vc_add(changed relative paths) -> vc_commit(generated message)

import:
import_block(modified file) -> compile_block -> persist outcome

knowledge:
update_components(all stale overlay paths) -> persist hashes -> clear stale
```

Test that rejection never calls `vc_add`/`vc_commit`, and that a commit failure returns `FilesUpdatedCommitFailed` with the reconciled paths.

- [ ] **Step 2: Verify failure**

Run:

```powershell
dotnet test tests/Agent.Tests/Agent.Tests.csproj --filter FullyQualifiedName~WorkbenchCoordinatorTests
```

Expected: FAIL.

- [ ] **Step 3: Implement coordinator operations**

Expose:

```csharp
CreateWorkbenchAsync(CreateWorkbenchRequest request)
CreateWorktreeAsync(CreateWorktreeRequest request)
StageRefreshAsync(DeviceContext device, CancellationToken token)
PreviewRefresh(DeviceContext device)
ApplyRefreshAsync(DeviceContext device, ApprovedReconciliation approval, CancellationToken token)
UpdateKnowledgeAsync(DeviceContext device, CancellationToken token)
ImportModifiedAsync(DeviceContext device, string relativePath, CancellationToken token)
MergeWorktreeAsync(string workbenchId, string sourceWorktreeId, string targetWorktreeId)
```

The coordinator owns the `DeviceOperationLock`. It persists metadata after each externally visible state transition.

- [ ] **Step 4: Make project-context workflow device-scoped**

Change `RunAsync` to accept `DeviceContext` and PLC name. Call:

```text
sync_export(outputDir = device.StagingRoot, plcName = selected PLC)
```

Do not reconcile without approval. Build knowledge only from `ExportedSourceRoot` plus `ModifiedSourceRoot`, writing `device.KnowledgeDbPath`.

- [ ] **Step 5: Run tests**

Run:

```powershell
dotnet test tests/Agent.Tests/Agent.Tests.csproj
```

Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add src/Agent/Workbench/WorkbenchCoordinator.cs src/Agent/Workflows tests/Agent.Tests
git commit -m "feat(workbench): orchestrate device lifecycle"
```

## Task 9: Move sessions and runtime context to worktree/device identity

**Files:**
- Modify: `src/Agent/Chat/SessionFileFormat.cs`
- Modify: `src/Agent/Chat/SessionManager.cs`
- Modify: `tests/Agent.Tests/SessionManagerTests.cs`

- [ ] **Step 1: Rewrite failing session tests**

Replace real `%LOCALAPPDATA%` usage with a supplied worktree root. Assert:

```text
<worktree>/.automation/sessions/<sessionId>.json
```

Header fields become:

```csharp
string WorkbenchId,
string WorktreeId,
string DeviceId,
string WorktreeRoot,
string KnowledgeDbPath
```

Keep `ProjectName` only as a nullable legacy-deserialization field so old session JSON can be read but is never used to resolve new paths.

- [ ] **Step 2: Verify failure**

Run:

```powershell
dotnet test tests/Agent.Tests/Agent.Tests.csproj --filter FullyQualifiedName~SessionManagerTests
```

Expected: FAIL against the old project-name API.

- [ ] **Step 3: Implement context-based sessions**

Change public methods to accept `DeviceContext` or explicit worktree root and IDs. Ensure `.automation/` is ignored by Task 3’s `.gitignore`. Build runtime context with:

```text
Workbench: <name> (<id>)
Worktree: <name> [branch]
Device: <PLC name> (<id>)
Exported source: <path>
Modified source: <path>
Knowledge DB: <path>
Knowledge state: current|stale; run update_components before reuse
```

- [ ] **Step 4: Run tests**

Run:

```powershell
dotnet test tests/Agent.Tests/Agent.Tests.csproj --filter FullyQualifiedName~SessionManagerTests
```

Expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add src/Agent/Chat/SessionFileFormat.cs src/Agent/Chat/SessionManager.cs tests/Agent.Tests/SessionManagerTests.cs
git commit -m "feat(chat): scope sessions to device worktrees"
```

## Task 10: Replace ApiHost’s name-based project state and path auto-fill

**Files:**
- Modify: `src/ApiHost/Program.cs`
- Create: `src/ApiHost/WorkbenchApiModels.cs`
- Modify: `src/ApiHost/ApiHost.csproj`
- Create: `tests/ApiHost.Tests/ApiHost.Tests.csproj`
- Create: `tests/ApiHost.Tests/WorkbenchEndpointsTests.cs`
- Modify: `AgentAssistPlcDev.sln`

- [ ] **Step 1: Add an API test project and failing endpoint tests**

Add `Microsoft.AspNetCore.Mvc.Testing` and reference `ApiHost`. Append `public partial class Program { }` to `src/ApiHost/Program.cs`, then use `WebApplicationFactory<Program>` in `WorkbenchEndpointsTests`. Register fake MCP callers and a temporary `WorkbenchCatalog` root through test-only service overrides so endpoint tests never start TIA or write to `%LOCALAPPDATA%`.

Test:

- create with default/custom root;
- list and select by immutable IDs;
- create linked feature worktree;
- list/select devices;
- refresh preview returns an approval token;
- apply rejects stale/unknown approval;
- update knowledge clears stale state;
- all tool auto-fill paths use the active device;
- legacy exports are not listed as new workbenches.

- [ ] **Step 2: Verify failure**

Run:

```powershell
dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj
```

Expected: FAIL because the endpoints do not exist.

- [ ] **Step 3: Add explicit API models and endpoints**

Add:

```text
GET    /api/workbenches
POST   /api/workbenches
GET    /api/workbenches/{workbenchId}
POST   /api/workbenches/{workbenchId}/select
GET    /api/workbenches/{workbenchId}/worktrees
POST   /api/workbenches/{workbenchId}/worktrees
POST   /api/workbenches/{workbenchId}/worktrees/{worktreeId}/select
GET    /api/workbenches/{workbenchId}/worktrees/{worktreeId}/devices
POST   /api/workbenches/{workbenchId}/worktrees/{worktreeId}/devices/{deviceId}/select
POST   /api/devices/{deviceId}/refresh/stage
GET    /api/devices/{deviceId}/refresh/preview
POST   /api/devices/{deviceId}/refresh/apply
POST   /api/devices/{deviceId}/knowledge/rebuild
POST   /api/devices/{deviceId}/knowledge/update
POST   /api/devices/{deviceId}/source/prepare-edit
POST   /api/devices/{deviceId}/source/import
POST   /api/worktrees/{sourceWorktreeId}/merge
```

Maintain one selected `WorkbenchSelection(workbenchId, worktreeId, deviceId)` instead of `_selectedProjectName`.

- [ ] **Step 4: Replace all `AssistantPaths` production callers**

In `Program.cs`, replace every call around current lines 185, 278, 333, 517, 540, 626, 637, 649, 895, 959, 1434, 1456, 1675, and 1723 with paths from the resolved selection. Version-control calls use `WorktreeRoot`; knowledge calls use `KnowledgeDbPath`; engineering exports use `StagingRoot`; source edits use effective input and modified output.

Retire `/api/projects/create` for new creation. Keep legacy list/read endpoints clearly named `/api/legacy/projects` if the UI still needs transition access; they must never write new metadata.

- [ ] **Step 5: Run backend tests**

Run:

```powershell
dotnet test AgentAssistPlcDev.sln
```

Expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add src/ApiHost tests/ApiHost.Tests AgentAssistPlcDev.sln
git commit -m "feat(api): expose workbench device contexts"
```

## Task 11: Add end-to-end verification and remove obsolete assumptions

> **Verification amendment (2026-07-27):** The proposed JSON scenario for
> `scripts/mcp-e2e.mjs` is superseded by `tests/E2E.Tests`. The script runner is
> designed for independently launched stdio servers and cannot safely exercise the
> host-owned approval/session/selection lifecycle or a mocked TIA boundary. The xUnit
> E2E drives `WorkbenchCoordinator`, real linked Git worktrees, and real device SQLite
> updates behind typed MCP caller seams. Therefore no `scripts/e2e-workbench.json`
> command is claimed for this feature.

**Files:**
- Create: `tests/E2E.Tests/E2E.Tests.csproj`
- Create: `tests/E2E.Tests/WorkbenchLifecycleTests.cs`
- Modify: `AgentAssistPlcDev.sln`
- Modify: `agent.md`
- Modify: `buildnote/plan/app.md`
- Modify: `buildnote/plan/export-sync.md`
- Create: `buildnote/verification/workbench-project-storage.md`
- Modify: `tests/Agent.Tests/AssistantPathsTests.cs`

- [ ] **Step 1: Add the end-to-end scenario**

The xUnit lifecycle test must:

1. create a temporary custom-root workbench;
2. initialize shared Git storage and `master`;
3. register two device fixtures;
4. stage and approve initial exports;
5. verify separate device databases;
6. create a feature worktree;
7. edit one overlay;
8. verify only that device becomes stale;
9. run one batched `update_components`;
10. import through a mocked engineering boundary when live TIA is unavailable;
11. refresh and auto-commit;
12. merge into master;
13. verify shared history and complete checkout files;
14. verify a sentinel under legacy `%LOCALAPPDATA%\PlcAiAssistant\exports` is untouched.

- [ ] **Step 2: Update architecture documentation**

Replace statements that define `%LOCALAPPDATA%\PlcAiAssistant\exports\<project>` as the current writable root. Document:

```text
%LOCALAPPDATA%\AutomationWorkbench\Project\<workbench>\
  repository.git\
  worktrees\<worktree>\devices\<device>\
```

Retain the old path only in an explicitly labeled legacy/no-migration section.

- [ ] **Step 3: Remove obsolete production assumptions**

Run:

```powershell
rg -n -S "ResolveExportRoot|PlcAiAssistant.{0,20}exports|exports\\\\<project" src studio tests agent.md buildnote/plan
```

Expected: matches remain only in legacy compatibility code/tests and historical documents that are explicitly labeled.

- [ ] **Step 4: Run the full verification suite**

Run:

```powershell
dotnet test tests/E2E.Tests/E2E.Tests.csproj --no-restore
dotnet test AgentAssistPlcDev.sln --no-restore
```

Expected: the coordinator-driven lifecycle E2E and all unit/integration tests pass.
There is no `scripts/e2e-workbench.json` invocation for this feature; the amendment
above records why the xUnit boundary replaced it.

- [ ] **Step 5: Perform the live acceptance pass**

With a real TIA V17 project containing two devices:

1. create a custom-root workbench;
2. approve the two initial device baselines;
3. create a feature worktree;
4. modify one block;
5. batch-update that device database;
6. import and compile the block;
7. refresh, approve, and auto-commit;
8. merge into `master`;
9. confirm both worktrees contain complete source trees and share the commit history.

Record the command output, commit SHAs, device paths, and screenshots in `buildnote/verification/workbench-project-storage.md`.

- [ ] **Step 6: Commit**

```powershell
git add AgentAssistPlcDev.sln tests/E2E.Tests agent.md buildnote/plan/app.md buildnote/plan/export-sync.md buildnote/verification/workbench-project-storage.md tests/Agent.Tests/AssistantPathsTests.cs
git commit -m "test(workbench): verify project lifecycle end to end"
```

## Plan Self-Review Checklist

- Every approved design requirement maps to at least one task.
- Workbench, worktree, and device IDs are stable and distinct from display names.
- Every worktree is a complete linked checkout; `repository.git` is shared storage only.
- Each device has its own ignored knowledge database.
- Full exports touch ignored staging first.
- Tracked baseline changes require a fresh preview and user confirmation.
- Approved refreshes commit automatically.
- Modified-source remains sparse, tracked, and persistent after import.
- Knowledge updates are batched rather than triggered after every edit.
- Component replacement uses provenance and transactions, not filename-only deletion.
- Existing `%LOCALAPPDATA%\PlcAiAssistant\exports` data is never migrated or mutated.
- Studio/React changes are outside this implementation plan and are captured in the future-UI note.
