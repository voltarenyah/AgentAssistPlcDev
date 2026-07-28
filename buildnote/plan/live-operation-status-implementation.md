# Live Operation Status Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show one live operation status line in Studio while long-running backend work is still running, including the current PLC export item when available.

**Architecture:** The browser creates an operation id and passes it in `X-Operation-Id` on existing synchronous requests. ApiHost stores only the latest in-memory snapshot per operation and exposes read/dismiss endpoints. The coordinator writes coarse stage messages and MCP engineering export tools forward native progress messages into the same operation snapshot.

**Tech Stack:** ASP.NET Core minimal APIs, xUnit, ModelContextProtocol .NET SDK, React, TypeScript, Vite, lucide-react.

---

## File Structure

- Create `src/ApiHost/OperationStatus.cs`: in-memory status records, registry, progress reporter, expiration, dismiss.
- Modify `src/ApiHost/Program.cs`: register `OperationStatusRegistry`.
- Modify `src/ApiHost/WorkbenchApiModels.cs`: map `/api/operations/{id}`, `/api/operations/{id}` DELETE, and wrap long-running workbench endpoints with operation lifecycle reporting.
- Modify `src/Agent/Mcp/IMcpToolCaller.cs`: add optional progress-aware MCP caller interface without breaking existing callers.
- Modify `src/Agent/Mcp/McpServerConnection.cs`: pass `IProgress<ProgressNotificationValue>` into `CallToolAsync`.
- Modify `src/ApiHost/Program.cs`: let runtime MCP callers forward optional progress.
- Modify `src/Agent/Workbench/WorkbenchCoordinator.cs`: accept optional `IOperationProgress` and report coarse stages.
- Modify `src/Agent/Workbench/SafeDeviceExportStager.cs`: accept optional progress, report staging/export/metadata stages, and pass MCP progress through when the caller supports it.
- Modify `src/Mcp.Engineering/Tools/EngineeringTools.cs`: receive MCP progress for export tools and pass it to the adapter.
- Modify `src/Mcp.Engineering/Adapter/TiaV17Adapter.cs`: report `Exporting block/tag table/UDT <name>...` immediately before each export.
- Modify `tests/ApiHost.Tests/WorkbenchEndpointsTests.cs`: cover operation API lifecycle and workbench endpoint header behavior.
- Modify `tests/Agent.Tests/WorkbenchCoordinatorTests.cs`: cover coordinator stage messages and progress-aware staging call.
- Add or modify MCP connection tests if the SDK overload is testable without starting real processes.
- Modify `studio/src/api/client.ts`: add operation types, operation-id header support, status polling calls.
- Create `studio/src/studio/workbench/OperationStatusLine.tsx`: one-line spinner/success/error display with dismiss for failure.
- Modify `studio/src/studio/workbench/CreateWorkbenchDialog.tsx`: replace the legacy migration notice with `OperationStatusLine`.
- Modify `studio/src/studio/MainStudio.tsx`: keep active operation state above dialogs, poll while pending, show contextual and global status line, and pass operation ids to long-running calls.

## Task 1: Backend Operation Registry

**Files:**
- Create: `src/ApiHost/OperationStatus.cs`
- Modify: `src/ApiHost/Program.cs`
- Modify: `src/ApiHost/WorkbenchApiModels.cs`
- Test: `tests/ApiHost.Tests/WorkbenchEndpointsTests.cs`

- [ ] **Step 1: Write failing registry tests**

Add tests that construct `OperationStatusRegistry`, call `Start`, `Report`, `Succeed`, `Fail`, and `Dismiss`, then assert:

```csharp
Assert.Equal("running", snapshot.State);
Assert.Equal("second", snapshot.Message);
Assert.False(registry.TryGet("missing", out _));
Assert.False(registry.TryGet("op-1", out _)); // after dismiss
Assert.Equal("failed", failed.State);
Assert.Contains("last stage", failed.Message);
```

- [ ] **Step 2: Run registry tests**

Run: `dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj --filter Operation`

Expected: fail because `OperationStatusRegistry` is not defined.

- [ ] **Step 3: Implement registry**

Create `OperationStatusSnapshot`, `OperationState`, `OperationStatusRegistry`, and `IOperationProgress`. Store snapshots in a `ConcurrentDictionary<string, OperationStatusSnapshot>`. `Report` replaces the latest message. `Succeed` and `Fail` set terminal state and `UpdatedAt`. `Dismiss` removes the operation. `TryGet` expires terminal entries older than 60 minutes.

- [ ] **Step 4: Map API endpoints**

Add:

```csharp
app.MapGet("/api/operations/{id}", (string id, OperationStatusRegistry registry) =>
    registry.TryGet(id, out var snapshot) ? Results.Ok(snapshot) : Results.NotFound());
app.MapDelete("/api/operations/{id}", (string id, OperationStatusRegistry registry) =>
{
    registry.Dismiss(id);
    return Results.NoContent();
});
```

Register the registry in `Program.cs`.

- [ ] **Step 5: Run API tests**

Run: `dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj --filter Operation`

Expected: pass.

## Task 2: Workbench Coordinator Stages

**Files:**
- Modify: `src/Agent/Workbench/WorkbenchCoordinator.cs`
- Modify: `src/Agent/Workbench/SafeDeviceExportStager.cs`
- Test: `tests/Agent.Tests/WorkbenchCoordinatorTests.cs`

- [ ] **Step 1: Write failing coordinator tests**

Add a recorder progress implementation:

```csharp
private sealed class RecordingProgress : IOperationProgress
{
    public List<string> Messages { get; } = new();
    public void Report(string message) => Messages.Add(message);
}
```

Assert create stages include:

```csharp
Assert.Equal(new[]
{
    "Preparing workbench storage...",
    "Initializing Git repository...",
    "Attaching to TIA Portal...",
    "Discovering PLC devices...",
    "Creating device folders...",
}, progress.Messages);
```

Assert refresh staging reports:

```csharp
Assert.Contains("Preparing export staging area...", progress.Messages);
Assert.Contains("Exporting PLC source...", progress.Messages);
Assert.Contains("Writing export metadata...", progress.Messages);
Assert.Contains("Preparing refresh preview...", progress.Messages);
```

- [ ] **Step 2: Run coordinator tests**

Run: `dotnet test tests/Agent.Tests/Agent.Tests.csproj --filter WorkbenchCoordinator`

Expected: fail because coordinator methods do not accept or report progress.

- [ ] **Step 3: Add optional progress parameters**

Update long-running public methods to accept `IOperationProgress? progress = null`. Call `progress?.Report("<message>")` immediately before long steps. Keep existing call sites compiling by placing the parameter before `CancellationToken` only where named calls are updated, or after token with a default where the method is already widely called.

- [ ] **Step 4: Wire staging progress**

Add optional progress to `SafeDeviceExportStager.StageAsync` and `StageCoreAsync`. Report before creating incoming staging, before `rebuild_export`, after export returns, and before returning the selected result.

- [ ] **Step 5: Run coordinator tests**

Run: `dotnet test tests/Agent.Tests/Agent.Tests.csproj --filter WorkbenchCoordinator`

Expected: pass.

## Task 3: MCP Progress Propagation

**Files:**
- Modify: `src/Agent/Mcp/IMcpToolCaller.cs`
- Modify: `src/Agent/Mcp/McpServerConnection.cs`
- Modify: `src/ApiHost/Program.cs`
- Modify: `src/Agent/Workbench/SafeDeviceExportStager.cs`
- Test: `tests/Agent.Tests/WorkbenchCoordinatorTests.cs`

- [ ] **Step 1: Add progress-aware interface**

Add:

```csharp
public interface IProgressMcpToolCaller : IMcpToolCaller
{
    Task<T> CallAsync<T>(
        string tool,
        object args,
        IProgress<ProgressNotificationValue>? progress,
        CancellationToken cancellationToken = default);
}
```

Use `ModelContextProtocol.Protocol` in the file.

- [ ] **Step 2: Implement MCP client forwarding**

In `McpServerConnection`, implement the new interface and call the SDK progress overload:

```csharp
var result = progress is null
    ? await client.CallToolAsync(tool, ToArguments(args), cancellationToken: cancellationToken)
    : await client.CallToolAsync(tool, ToArguments(args), progress, cancellationToken: cancellationToken);
```

If the exact SDK signature requires `requestOptions`, pass `requestOptions: null`.

- [ ] **Step 3: Forward through runtime callers**

Make `RuntimeCaller` implement `IProgressMcpToolCaller`. If `Resolve(runtime.Host)` also implements `IProgressMcpToolCaller`, call the progress overload; otherwise fall back to the plain call.

- [ ] **Step 4: Use progress-aware call in staging**

In `SafeDeviceExportStager`, when `engineering is IProgressMcpToolCaller progressCaller`, call the progress-aware overload and translate progress notifications into `IOperationProgress.Report`.

- [ ] **Step 5: Verify focused tests**

Run: `dotnet test tests/Agent.Tests/Agent.Tests.csproj --filter WorkbenchCoordinator`

Expected: pass.

## Task 4: Engineering Export Item Progress

**Files:**
- Modify: `src/Mcp.Engineering/Tools/EngineeringTools.cs`
- Modify: `src/Mcp.Engineering/Adapter/TiaV17Adapter.cs`
- Optional Modify: `src/Contracts/IEngineeringPlatform.cs`
- Test: build and any existing MCP engineering tests

- [ ] **Step 1: Thread progress from tool methods**

Add optional `IProgress<ProgressNotificationValue>? progress = null` parameters to export tool methods: `ExportAllBlocks`, `ExportTagTables`, `ExportUdts`, `SyncExport`, and `RebuildExport`.

- [ ] **Step 2: Add adapter reporting helper**

Use a helper like:

```csharp
private static void Report(IProgress<ProgressNotificationValue>? progress, string message)
{
    try { progress?.Report(new ProgressNotificationValue { Message = message }); }
    catch { }
}
```

If the SDK property is named differently, use the package-defined property that carries the human-readable message.

- [ ] **Step 3: Report before exports**

Before each item export, report:

```csharp
Report(progress, $"Exporting block {block.Name}...");
Report(progress, $"Exporting tag table {table.Name}...");
Report(progress, $"Exporting UDT {type.Name}...");
```

For incremental sync re-export, report from `ReExportComponent` according to `live.Category`.

- [ ] **Step 4: Build engineering project**

Run: `dotnet build src/Mcp.Engineering/Mcp.Engineering.csproj`

Expected: pass.

## Task 5: API Operation Wrapping

**Files:**
- Modify: `src/ApiHost/WorkbenchApiModels.cs`
- Test: `tests/ApiHost.Tests/WorkbenchEndpointsTests.cs`

- [ ] **Step 1: Add endpoint tests**

Use `X-Operation-Id: op-create` on `POST /api/workbenches`; make fake engineering/version callers pause until the test reads `/api/operations/op-create`, then assert the visible running message. Add failure test that a thrown `WorkbenchLifecycleException` leaves `/api/operations/op-fail` with `state == "failed"`.

- [ ] **Step 2: Implement operation wrapper helper**

Add a helper in `WorkbenchEndpoints` that reads `X-Operation-Id`, calls `registry.Start`, passes `registry.For(operationId)` into coordinator methods, and calls `Succeed` or `Fail` in `try/catch`.

- [ ] **Step 3: Wrap long-running endpoints**

Wrap workbench creation, create worktree, stage refresh, apply refresh, knowledge update/rebuild, import source, and merge worktree. Keep endpoint routes and response bodies unchanged.

- [ ] **Step 4: Run API tests**

Run: `dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj`

Expected: pass.

## Task 6: Studio API Client and Status State

**Files:**
- Modify: `studio/src/api/client.ts`
- Create: `studio/src/studio/workbench/OperationStatusLine.tsx`
- Modify: `studio/src/studio/MainStudio.tsx`
- Modify: `studio/src/studio/workbench/CreateWorkbenchDialog.tsx`

- [ ] **Step 1: Add TypeScript API types**

Add:

```ts
export type OperationState = 'running' | 'succeeded' | 'failed'
export type OperationStatus = {
  operationId: string
  operationType: string
  state: OperationState
  message: string
  updatedAt: string
  errorMessage: string | null
}
```

Allow workbench requests to include:

```ts
const withOperation = (init: RequestInit, operationId?: string): RequestInit => ({
  ...init,
  headers: operationId
    ? { ...(init.headers as Record<string, string>), 'X-Operation-Id': operationId }
    : init.headers,
})
```

- [ ] **Step 2: Add status API functions**

Add:

```ts
export const getOperationStatus = (operationId: string) =>
  workbenchRequest<OperationStatus>(`/operations/${encodeURIComponent(operationId)}`)

export const dismissOperationStatus = (operationId: string) =>
  workbenchRequest<void>(`/operations/${encodeURIComponent(operationId)}`, { method: 'DELETE' })
```

- [ ] **Step 3: Create status-line component**

`OperationStatusLine` renders one row: spinner for running, success icon for succeeded, failure icon and dismiss button for failed. It receives `status`, `fallback`, and `onDismiss`.

- [ ] **Step 4: Keep active operation in MainStudio**

Replace the string-only `operation` state with:

```ts
type ActiveOperation = {
  id: string
  kind: string
  label: string
  status: api.OperationStatus | null
}
```

Start operations with `crypto.randomUUID()`, pass ids into API calls, poll every second while the operation exists, dismiss success after about three seconds, retain failure until dismissed, and keep the global strip visible even if the dialog closes.

- [ ] **Step 5: Replace create dialog text**

Pass the active create status into `CreateWorkbenchDialog` and render `OperationStatusLine` where `Existing legacy exports are not migrated.` used to be.

- [ ] **Step 6: Build Studio**

Run:

```powershell
npm --prefix studio run lint
npm --prefix studio run build
```

Expected: lint exits 0; build exits 0.

## Task 7: Full Verification and Commits

**Files:**
- Parent repo and nested `studio` repo

- [ ] **Step 1: Run focused backend tests**

Run:

```powershell
dotnet test tests/Agent.Tests/Agent.Tests.csproj --filter WorkbenchCoordinator
dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj --filter Operation
```

Expected: pass.

- [ ] **Step 2: Run broader tests touched by previous fixes**

Run:

```powershell
dotnet test tests/Mcp.VersionControl.Tests/Mcp.VersionControl.Tests.csproj
dotnet test tests/E2E.Tests/E2E.Tests.csproj
```

Expected: pass.

- [ ] **Step 3: Run Studio verification**

Run:

```powershell
npm --prefix studio run lint
npm --prefix studio run build
```

Expected: pass. Existing warnings are acceptable only if they match the already-known warnings and do not increase.

- [ ] **Step 4: Commit nested Studio changes**

Run:

```powershell
git -C studio add src/api/client.ts src/studio/MainStudio.tsx src/studio/workbench/CreateWorkbenchDialog.tsx src/studio/workbench/OperationStatusLine.tsx
git -C studio commit -m "feat: show live operation status"
```

- [ ] **Step 5: Commit parent changes**

Run:

```powershell
git add buildnote/plan/live-operation-status-implementation.md src tests studio
git commit -m "feat: show live operation status"
```

## Self-Review

- Spec coverage: The plan covers status registry, status endpoints, operation header, coordinator stages, MCP progress forwarding, per-export item status, one-line UI rendering, dialog replacement, global strip, success cleanup, and failure retention.
- Non-goals checked: There is no log, no progress bar, no count, no cancellation, and no persisted history.
- Placeholder scan: No `TBD`, `TODO`, `implement later`, or unspecified testing step remains.
- Type consistency: Backend uses `OperationStatusSnapshot`, `OperationStatusRegistry`, and `IOperationProgress`; frontend uses `OperationStatus` and `ActiveOperation`.
