# Offline Device Context Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make device selection, block browsing, and knowledge status correct and usable without TIA, then expose TIA opening and comparison as explicit operations.

**Architecture:** Add an Agent-layer snapshot service that derives an effective block index from the tracked export manifest plus sparse overlays and calculates knowledge state from `device.json` and database existence. ApiHost returns this snapshot without calling engineering; Studio treats it as the selected-device source of truth. Existing engineering `connect`, staging, preview, reconciliation, and overlay import mechanisms remain isolated behind explicit TIA actions.

**Tech Stack:** C#/.NET 8, ASP.NET minimal APIs, xUnit, React 19, TypeScript, Vitest/happy-dom, Siemens TIA Openness through the existing MCP engineering adapter.

---

## File Structure

- Create `src/Agent/Workbench/DeviceSnapshot.cs`: snapshot DTOs and the offline snapshot reader.
- Create `tests/Agent.Tests/DeviceSnapshotReaderTests.cs`: manifest/overlay and knowledge-state unit tests.
- Modify `src/ApiHost/CompatibilityEndpoints.cs`: return the offline snapshot and remove implicit `list_blocks`.
- Modify `src/ApiHost/WorkbenchApiModels.cs`: add the explicit device TIA-open endpoint.
- Modify `src/Agent/Workbench/WorkbenchCoordinator.cs`: coordinate opening the registered project.
- Modify `tests/ApiHost.Tests/WorkbenchEndpointsTests.cs`: API selection, offline reads, and explicit TIA operation tests.
- Modify `studio/src/api/client.ts`: typed device snapshot and explicit TIA methods.
- Create `studio/src/studio/deviceSnapshot.ts`: pure UI state mapping that can be tested without rendering the full Studio.
- Create `studio/src/studio/deviceSnapshot.test.ts`: snapshot hydration and error-retention tests.
- Modify `studio/src/studio/MainStudio.tsx`: use persisted snapshot, display offline/TIA states, and rename staging as comparison.
- Modify `studio/src/studio/RefreshDialog.tsx`: comparison terminology and fingerprint presentation.
- Modify `tests/E2E.Tests/WorkbenchLifecycleTests.cs`: offline restart/lifecycle coverage.

### Task 1: Offline snapshot model and knowledge state

**Files:**
- Create: `src/Agent/Workbench/DeviceSnapshot.cs`
- Create: `tests/Agent.Tests/DeviceSnapshotReaderTests.cs`

- [ ] **Step 1: Write failing knowledge-state tests**

Create a fixture with `DeviceContext`, `DeviceMetadata`, and temporary roots. Add these tests:

```csharp
[Theory]
[InlineData(false, false, false, "missing")]
[InlineData(true, true, false, "stale")]
[InlineData(true, false, true, "stale")]
[InlineData(true, false, false, "current")]
public void KnowledgeStateUsesDatabaseExistenceAndPersistedFlags(
    bool databaseExists, bool stale, bool baselineStale, string expected)
{
    var fixture = SnapshotFixture.Create(stale, baselineStale);
    if (databaseExists)
        File.WriteAllBytes(fixture.Context.KnowledgeDbPath, [1]);

    var snapshot = new DeviceSnapshotReader().Read(fixture.Context, fixture.Metadata);

    Assert.Equal(expected, snapshot.Knowledge.State);
    Assert.Equal(fixture.Metadata.Knowledge.UpdatedAt, snapshot.Knowledge.UpdatedAt);
}
```

- [ ] **Step 2: Run the test and verify RED**

Run:

```powershell
dotnet test tests/Agent.Tests/Agent.Tests.csproj --filter FullyQualifiedName~DeviceSnapshotReaderTests
```

Expected: compilation fails because `DeviceSnapshotReader` and snapshot DTOs do not exist.

- [ ] **Step 3: Add the minimal snapshot types and state calculation**

Implement:

```csharp
namespace Agent.Workbench;

public sealed record DeviceKnowledgeSnapshot(string State, string? UpdatedAt);

public sealed record OfflineBlockInfo(
    string Id,
    string Name,
    int? Number,
    string BlockType,
    string? ProgrammingLanguage,
    string? GroupPath,
    string RelativePath,
    bool Modified);

public sealed record DeviceSnapshot(
    string WorkbenchId,
    string WorktreeId,
    string DeviceId,
    string PlcName,
    string EngineeringIdentity,
    string ExportedSourceRoot,
    string ModifiedSourceRoot,
    string KnowledgeDbPath,
    DeviceKnowledgeSnapshot Knowledge,
    IReadOnlyList<OfflineBlockInfo> Blocks,
    int OverlayCount,
    IReadOnlyList<string> Diagnostics);

public sealed class DeviceSnapshotReader
{
    public DeviceSnapshot Read(DeviceContext context, DeviceMetadata metadata)
    {
        var state = !File.Exists(context.KnowledgeDbPath)
            ? "missing"
            : metadata.Knowledge.Stale || metadata.Knowledge.BaselineStale
                ? "stale"
                : "current";

        return new(
            context.WorkbenchId,
            context.WorktreeId,
            context.DeviceId,
            metadata.PlcName,
            metadata.EngineeringIdentity,
            context.ExportedSourceRoot,
            context.ModifiedSourceRoot,
            context.KnowledgeDbPath,
            new(state, metadata.Knowledge.UpdatedAt),
            [],
            0,
            []);
    }
}
```

- [ ] **Step 4: Run the focused tests and verify GREEN**

Run the Task 1 command. Expected: all four cases pass.

- [ ] **Step 5: Commit**

```powershell
git add src/Agent/Workbench/DeviceSnapshot.cs tests/Agent.Tests/DeviceSnapshotReaderTests.cs
git commit -m "feat: derive persisted device knowledge state"
```

### Task 2: Effective offline block index

**Files:**
- Modify: `src/Agent/Workbench/DeviceSnapshot.cs`
- Modify: `tests/Agent.Tests/DeviceSnapshotReaderTests.cs`

- [ ] **Step 1: Write failing manifest and overlay tests**

Add tests that write a schema `1.0` `metadata.json` with exported OB, FB, DB, Tags, and UDT records. Assert only program blocks appear, their number/language/group/relative path are mapped, and order is deterministic:

```csharp
[Fact]
public void ReadBuildsOfflineBlockIndexFromExportManifest()
{
    var fixture = SnapshotFixture.Create();
    fixture.WriteManifest(
        Component("ob", "Main", "OB", "Blocks/Main [OB1].xml", 1, "LAD", "Area/Main"),
        Component("db", "Data", "DB", "DB/Data [DB4].xml", 4, null, "Area/Data"),
        Component("tags", "Tags", "Tags", "Tags/Tags.xml", null, null, "Tags"));

    var snapshot = new DeviceSnapshotReader().Read(fixture.Context, fixture.Metadata);

    Assert.Collection(
        snapshot.Blocks,
        block =>
        {
            Assert.Equal("Main", block.Name);
            Assert.Equal("OB", block.BlockType);
            Assert.Equal(1, block.Number);
            Assert.Equal("Area", block.GroupPath);
            Assert.False(block.Modified);
        },
        block =>
        {
            Assert.Equal("Data", block.Name);
            Assert.Equal("DB", block.BlockType);
        });
}
```

Add an overlay test that copies one manifest-referenced XML to `modified-source`, creates one valid overlay-only block XML, and asserts the first block is `Modified == true`, the overlay-only block is present, and `OverlayCount == 2`.

Add malformed/missing-manifest tests asserting `Blocks` is empty and `Diagnostics` contains a precise manifest message.

- [ ] **Step 2: Run and verify RED**

Run the Task 1 focused command. Expected: block assertions fail because `Blocks` is empty.

- [ ] **Step 3: Implement manifest parsing and overlay merge**

In `DeviceSnapshotReader`:

- Parse `metadata.json` with `JsonDocument` so Agent does not depend on the engineering assembly.
- Accept categories `OB`, `FB`, `FC`, and `DB` with status `Exported`.
- Validate each `exportedFile` through `WorkbenchPaths.ResolveRelative`.
- Set `Modified` from existence of the same normalized path below `modified-source`.
- Derive `GroupPath` from `sourcePath` by removing the final `/name`.
- Enumerate overlay XML not represented in the manifest.
- For overlay-only files, parse the Siemens block XML root/type/name/number/language using `XDocument`; reject unsupported XML with a path-specific diagnostic.
- Sort by block type, number, name, then relative path using ordinal comparison.

Keep the parsing helpers private and return diagnostics rather than throwing for per-file defects. Do not read TIA or `plc-knowledge.db` to construct blocks.

- [ ] **Step 4: Run focused and Agent test suites**

```powershell
dotnet test tests/Agent.Tests/Agent.Tests.csproj --filter FullyQualifiedName~DeviceSnapshotReaderTests
dotnet test tests/Agent.Tests/Agent.Tests.csproj
```

Expected: focused tests and the complete Agent suite pass.

- [ ] **Step 5: Commit**

```powershell
git add src/Agent/Workbench/DeviceSnapshot.cs tests/Agent.Tests/DeviceSnapshotReaderTests.cs
git commit -m "feat: index effective PLC source offline"
```

### Task 3: Selected-device snapshot API without engineering

**Files:**
- Modify: `src/ApiHost/Program.cs`
- Modify: `src/ApiHost/CompatibilityEndpoints.cs`
- Modify: `tests/ApiHost.Tests/WorkbenchEndpointsTests.cs`

- [ ] **Step 1: Write failing endpoint tests**

Register a selected device fixture whose manifest contains one block and whose engineering caller throws on every call. Assert:

```csharp
[Fact]
public async Task SelectedDeviceSnapshotWorksOfflineAndDoesNotCallEngineering()
{
    var fixture = await SelectedApiFixture.CreateAsync(root, databaseExists: true);
    fixture.WriteManifest(Component("Main", "OB", "Blocks/Main [OB1].xml", 1));

    var snapshot = await fixture.Client.GetFromJsonAsync<JsonElement>("/api/project/info");

    Assert.Equal("current", snapshot.GetProperty("knowledge").GetProperty("state").GetString());
    Assert.Single(snapshot.GetProperty("blocks").EnumerateArray());
    Assert.Empty(fixture.Engineering.Calls);
}
```

Add tests for missing DB, stale DB, and missing manifest diagnostic. Replace the old routing expectation that `/api/blocks` calls `list_blocks` with an assertion that `/api/blocks` returns the snapshot blocks and engineering receives no call.

- [ ] **Step 2: Run and verify RED**

```powershell
dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj --filter "FullyQualifiedName~SelectedDeviceSnapshot|FullyQualifiedName~KnowledgeState"
```

Expected: JSON lacks `knowledge` and `blocks`, or `/api/blocks` invokes the throwing engineering caller.

- [ ] **Step 3: Register and use the reader**

Add `DeviceSnapshotReader` as a singleton in `Program.cs`. Change `/api/project/info` to:

```csharp
app.MapGet("/api/project/info", (
    WorkbenchApiState state,
    DeviceSnapshotReader snapshots) =>
{
    var selected = state.Device(Device(state).DeviceId);
    return Results.Ok(snapshots.Read(selected.Context, selected.Metadata));
});
```

Change `/api/blocks` to return `snapshot.Blocks` using the same selected device. Remove `ApiMcpGateway` and `CancellationToken` from this endpoint. Do not catch snapshot errors into an empty array.

- [ ] **Step 4: Run API tests**

```powershell
dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj
```

Expected: all ApiHost tests pass and the offline test records zero engineering calls.

- [ ] **Step 5: Commit**

```powershell
git add src/ApiHost/Program.cs src/ApiHost/CompatibilityEndpoints.cs tests/ApiHost.Tests/WorkbenchEndpointsTests.cs
git commit -m "feat: serve selected device from persisted source"
```

### Task 4: Studio snapshot hydration and error retention

**Files:**
- Modify: `studio/src/api/client.ts`
- Create: `studio/src/studio/deviceSnapshot.ts`
- Create: `studio/src/studio/deviceSnapshot.test.ts`
- Modify: `studio/src/studio/MainStudio.tsx`

- [ ] **Step 1: Write failing pure state tests**

Define tests for the intended reducer:

```ts
import { describe, expect, it } from 'vitest'
import { applyDeviceSnapshot, retainSnapshotOnError } from './deviceSnapshot'

it('hydrates blocks and persisted knowledge from a selected-device snapshot', () => {
  const next = applyDeviceSnapshot(null, snapshot({
    knowledge: { state: 'current', updatedAt: '2026-07-29T08:00:00Z' },
    blocks: [block('Main', 'OB', 1)],
  }))
  expect(next.knowledgeState).toBe('current')
  expect(next.blocks).toHaveLength(1)
})

it('retains the last successful offline snapshot when refresh fails', () => {
  const previous = applyDeviceSnapshot(null, snapshot({
    knowledge: { state: 'stale', updatedAt: null },
    blocks: [block('Main', 'OB', 1)],
  }))
  expect(retainSnapshotOnError(previous, new Error('TIA unavailable'))).toBe(previous)
})
```

- [ ] **Step 2: Run and verify RED**

```powershell
Set-Location studio
npx vitest run src/studio/deviceSnapshot.test.ts
```

Expected: module/functions do not exist.

- [ ] **Step 3: Add types and pure mapping**

Extend `DeviceInfo` into `DeviceSnapshot`:

```ts
export type KnowledgeVisualState = 'current' | 'stale' | 'missing' | 'failed'

export type OfflineBlockInfo = {
  id: string
  name: string
  number: number | null
  blockType: string
  programmingLanguage: string | null
  groupPath: string | null
  relativePath: string
  modified: boolean
}

export type DeviceSnapshot = DeviceInfo & {
  knowledge: { state: KnowledgeVisualState; updatedAt: string | null }
  blocks: OfflineBlockInfo[]
  overlayCount: number
  diagnostics: string[]
}
```

Make `getSelectedDeviceInfo()` return `DeviceSnapshot`. Implement a small immutable `DeviceViewState` mapper in `deviceSnapshot.ts`; `retainSnapshotOnError` returns the previous value.

- [ ] **Step 4: Replace transient UI state**

In `MainStudio.tsx`:

- Store the last `DeviceSnapshot`.
- During selection call only `getSelectedDeviceInfo`, Git status, and sessions; remove `getBlocks()`.
- Populate blocks, overlay count, and knowledge from the snapshot.
- After prepare-overlay, apply-refresh, update/rebuild-knowledge, and import, reload the snapshot from the API rather than guessing state locally.
- Preserve the prior snapshot if reloading fails and show the error toast.
- Display diagnostics near the block index; say “No persisted block index” rather than “Connect TIA.”
- Display knowledge `updatedAt` and an “Offline ready” indicator.

- [ ] **Step 5: Run tests and build**

```powershell
Set-Location studio
npx vitest run src/studio/deviceSnapshot.test.ts
npm run build
```

Expected: tests pass and TypeScript/Vite build succeeds.

- [ ] **Step 6: Commit**

```powershell
git add studio/src/api/client.ts studio/src/studio/deviceSnapshot.ts studio/src/studio/deviceSnapshot.test.ts studio/src/studio/MainStudio.tsx
git commit -m "feat(ui): hydrate persisted offline device state"
```

### Task 5: Explicit Open project in TIA operation

**Files:**
- Modify: `src/Agent/Workbench/WorkbenchCoordinator.cs`
- Modify: `src/ApiHost/WorkbenchApiModels.cs`
- Modify: `tests/Agent.Tests/WorkbenchCoordinatorTests.cs`
- Modify: `tests/ApiHost.Tests/WorkbenchEndpointsTests.cs`
- Modify: `studio/src/api/client.ts`
- Modify: `studio/src/studio/MainStudio.tsx`

- [ ] **Step 1: Write failing coordinator test**

```csharp
[Fact]
public async Task OpenProjectInTiaUsesRegisteredProjectWithUi()
{
    var fixture = Fixture.Create(sourceProjectPath: @"C:\Projects\Line.ap17");
    fixture.Engineering.Respond("connect", new { connected = true });

    await fixture.Coordinator.OpenProjectInTiaAsync(fixture.Context, CancellationToken.None);

    var args = fixture.Engineering.ArgumentsFor("connect");
    Assert.Equal(@"C:\Projects\Line.ap17", Property<string>(args, "projectPath"));
    Assert.True(Property<bool>(args, "withUI"));
}
```

Also test a null/blank registered project path throws `WorkbenchCatalogException` with code `ENGINEERING_PROJECT_PATH_MISSING` before any engineering call.

- [ ] **Step 2: Run and verify RED**

```powershell
dotnet test tests/Agent.Tests/Agent.Tests.csproj --filter FullyQualifiedName~OpenProjectInTia
```

Expected: `OpenProjectInTiaAsync` does not exist.

- [ ] **Step 3: Implement coordinator and endpoint**

Resolve the worktree metadata for the device and call:

```csharp
await engineering.CallAsync<object>(
    "connect",
    new { projectPath = worktree.SourceProjectPath, withUI = true },
    cancellationToken);
```

Add `POST /api/devices/{device}/tia/open` through `RunOperationAsync`, with operation type `open-tia-project`. Do not modify device metadata or source artifacts.

- [ ] **Step 4: Add UI action**

Add `openDeviceProject(deviceId, operationId)` to the client. Add **Open project in TIA** to Device Overview, using existing operation polling. Keep its success/failure separate from snapshot state; on failure the displayed offline blocks and knowledge remain unchanged.

- [ ] **Step 5: Run focused, API, and Studio verification**

```powershell
dotnet test tests/Agent.Tests/Agent.Tests.csproj --filter FullyQualifiedName~OpenProjectInTia
dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj
Set-Location studio
npm run build
```

Expected: all commands pass.

- [ ] **Step 6: Commit**

```powershell
git add src/Agent/Workbench/WorkbenchCoordinator.cs src/ApiHost/WorkbenchApiModels.cs tests/Agent.Tests/WorkbenchCoordinatorTests.cs tests/ApiHost.Tests/WorkbenchEndpointsTests.cs studio/src/api/client.ts studio/src/studio/MainStudio.tsx
git commit -m "feat: open registered project in TIA explicitly"
```

### Task 6: Present staging as non-destructive TIA comparison

**Files:**
- Modify: `src/Agent/Workbench/ReconciliationModels.cs`
- Modify: `src/Agent/Workbench/DeviceReconciler.cs`
- Modify: `studio/src/studio/MainStudio.tsx`
- Modify: `studio/src/studio/RefreshDialog.tsx`
- Modify: `studio/src/api/client.ts`
- Modify: `tests/ApiHost.Tests/WorkbenchEndpointsTests.cs`

- [ ] **Step 1: Lock non-destructive comparison behavior in an API test**

Create a baseline and configure the staging caller. Capture baseline files, `device.json`,
and DB bytes before calling stage plus preview. Assert they are byte-identical afterward,
the preview contains fingerprints, and no version-control call occurred:

```csharp
var before = fixture.CapturePersistentDeviceFiles();
await fixture.Client.PostAsync($"/api/devices/{fixture.DeviceId}/refresh/stage", null);
var preview = await fixture.Client.GetFromJsonAsync<JsonElement>(
    $"/api/devices/{fixture.DeviceId}/refresh/preview");

Assert.Equal(before, fixture.CapturePersistentDeviceFiles());
Assert.True(preview.GetProperty("entries")[0].TryGetProperty("storedFingerprints", out _));
Assert.Empty(fixture.VersionControl.Calls);
```

- [ ] **Step 2: Run and verify RED**

Run the focused ApiHost test. Expected: it fails if fingerprint fields are not exposed by
the current reconciliation preview. If the non-destructive assertions already pass,
retain them as characterization and ensure the fingerprint assertion supplies RED.

- [ ] **Step 3: Carry fingerprint evidence through reconciliation**

Extend `ReconciliationEntry` and its client type with nullable `storedFingerprints`,
`liveFingerprints`, and `fingerprintsMatch`. Populate stored values from baseline manifest
and live values from staging manifest. For missing values return null, never a fabricated
match.

- [ ] **Step 4: Rename and clarify the UI workflow**

In `MainStudio.tsx`, change:

- “Stage full PLC refresh” to **Compare with TIA**;
- operation copy to “Exporting live PLC to temporary comparison staging…”;
- success copy to “Comparison ready; no tracked files changed.”

In `RefreshDialog.tsx`:

- title it “TIA comparison”;
- state that staging is temporary and comparison is non-destructive;
- show state plus stored/live fingerprints for changed, new, missing, and unverifiable
  rows;
- keep baseline apply as a distinct confirmed action;
- keep removal selection explicit.

Do not add automatic apply or automatic knowledge rebuild.

- [ ] **Step 5: Run verification**

```powershell
dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj
Set-Location studio
npm run build
```

Expected: API tests and Studio build pass.

- [ ] **Step 6: Commit**

```powershell
git add src/Agent/Workbench/ReconciliationModels.cs src/Agent/Workbench/DeviceReconciler.cs studio/src/studio/MainStudio.tsx studio/src/studio/RefreshDialog.tsx studio/src/api/client.ts tests/ApiHost.Tests/WorkbenchEndpointsTests.cs
git commit -m "feat(ui): present staged export as TIA comparison"
```

### Task 7: Offline lifecycle end-to-end verification

**Files:**
- Modify: `tests/E2E.Tests/WorkbenchLifecycleTests.cs`
- Modify: `README.md`
- Modify: `README.zh-CN.md`

- [ ] **Step 1: Write the failing offline restart scenario**

Extend the lifecycle fixture after initial export and knowledge ingest:

```csharp
[Fact]
public async Task PersistedDeviceRemainsUsableAfterEngineeringDisconnectAndApiRestart()
{
    var created = await fixture.CreateExportAndIngestAsync();
    var databaseHash = SHA256.HashData(File.ReadAllBytes(created.Context.KnowledgeDbPath));
    fixture.DisconnectEngineering();
    fixture.RestartApiHost();

    var snapshot = await fixture.SelectAndReadSnapshotAsync(created);

    Assert.NotEmpty(snapshot.Blocks);
    Assert.Equal("current", snapshot.Knowledge.State);
    Assert.Equal(
        databaseHash,
        SHA256.HashData(File.ReadAllBytes(created.Context.KnowledgeDbPath)));
    Assert.Empty(fixture.EngineeringCallsAfterRestart);
}
```

- [ ] **Step 2: Run and verify RED**

```powershell
dotnet test tests/E2E.Tests/E2E.Tests.csproj --filter FullyQualifiedName~PersistedDeviceRemainsUsable
```

Expected: fixture/snapshot support or offline behavior is missing.

- [ ] **Step 3: Complete only the required fixture wiring**

Use a fresh `WorkbenchApiState`/host against the same persisted workbench root, select the
workbench/worktree/device IDs, and read through `DeviceSnapshotReader`. Do not reconnect
the recording engineering caller. Keep the assertion against the same DB path and bytes.

- [ ] **Step 4: Document the operator workflow**

Update both READMEs with:

```text
Normal browsing, overlay editing, Git work, and knowledge queries use persisted
device artifacts and do not require TIA Portal. Use Open project in TIA before an
explicit Compare with TIA or Import & compile operation.
```

Document knowledge meanings (`missing`, `stale`, `current`) and clarify that Compare with
TIA changes only temporary staging until approval.

- [ ] **Step 5: Run the full verification matrix**

```powershell
dotnet test AgentAssistPlcDev.sln
Set-Location studio
npx vitest run
npm run build
npm run lint
```

Expected: all .NET and Studio tests pass; build and lint return exit code 0.

- [ ] **Step 6: Commit**

```powershell
git add tests/E2E.Tests/WorkbenchLifecycleTests.cs README.md README.zh-CN.md
git commit -m "test: verify persistent offline device lifecycle"
```

## Final Acceptance

- [ ] Launch the application with TIA closed and select a previously ingested device.
- [ ] Verify block count and block browser match `exported-source/metadata.json` plus overlays.
- [ ] Verify an existing current DB shows `current` immediately after selection.
- [ ] Prepare an overlay and verify the same DB shows `stale` after snapshot refresh.
- [ ] Update knowledge with TIA still closed and verify state returns to `current`.
- [ ] Use **Open project in TIA**, then **Compare with TIA**.
- [ ] Verify comparison shows fingerprint evidence and does not change tracked files before approval.
- [ ] Approve selected baseline changes and verify Git commit plus stale knowledge state.
- [ ] Close TIA, restart the stack, and verify blocks, Git data, overlays, and DB remain available.
