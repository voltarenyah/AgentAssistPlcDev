# Open Project in TIA Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an enabled-when-configured Studio action that opens the selected workbench project in a visible TIA Portal instance and makes it the active engineering connection.

**Architecture:** Extend the compatibility connection-switch endpoint so it accepts either an existing `connectionId` or a new `projectPath` request. Keep UI availability logic in a small tested helper, and have `MainStudio` call the typed client with `withUI: true` while using the existing operation-status registry.

**Tech Stack:** React 19, TypeScript, Vitest, ASP.NET Core minimal APIs, xUnit

---

## File Structure

- Modify `src/ApiHost/CompatibilityEndpoints.cs`: accept project-path switch requests and track their operation status.
- Modify `tests/ApiHost.Tests/WorkbenchEndpointsTests.cs`: prove project-path switching delegates to engineering connect and updates operation state.
- Modify `studio/src/api/client.ts`: send project-path switch requests with an operation ID.
- Create `studio/src/studio/workbench/openProjectInTia.ts`: own path normalization and request construction.
- Create `studio/src/studio/workbench/openProjectInTia.test.ts`: test availability and exact request arguments.
- Create `studio/src/studio/workbench/OpenProjectInTiaButton.tsx`: render the path-aware action.
- Create `studio/src/studio/workbench/OpenProjectInTiaButton.test.tsx`: test enabled and disabled rendering.
- Modify `studio/src/studio/MainStudio.tsx`: add the handler and button.
- Modify `studio/package.json` and `studio/package-lock.json`: enable the existing Vitest-based frontend tests.

### Task 1: Project-path connection switching

**Files:**
- Test: `tests/ApiHost.Tests/WorkbenchEndpointsTests.cs`
- Modify: `src/ApiHost/CompatibilityEndpoints.cs`

- [ ] **Step 1: Write a failing endpoint test**

Add an integration test that posts:

```csharp
new { projectPath = @"C:\Projects\Line.ap17", withUI = true }
```

to `/api/connections/switch` with `X-Operation-Id: open-tia-1`, then asserts the
engineering fake received `connect` with the same arguments and
`OperationStatusRegistry` reports `succeeded`.

- [ ] **Step 2: Run the test and verify RED**

Run:

```powershell
dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj --filter OpenProjectInTia
```

Expected: FAIL because `/api/connections/switch` currently requires
`connectionId`.

- [ ] **Step 3: Implement the minimal endpoint behavior**

Change the endpoint to:

```csharp
app.MapPost("/api/connections/switch", async (
    HttpContext http,
    JsonElement body,
    ApiMcpGateway gateway,
    CompatibilityRuntimeState state,
    OperationStatusRegistry operations,
    CancellationToken ct) =>
{
    return await RunOperationAsync(
        http,
        operations,
        "open-project-in-tia",
        "Opening project in TIA Portal...",
        async _ =>
        {
            JsonElement request;
            string id;
            if (body.TryGetProperty("connectionId", out var suppliedId))
            {
                id = suppliedId.GetString() ?? throw new ArgumentException("connectionId is required.");
                if (!state.Connections.TryGetValue(id, out request))
                    throw new KeyNotFoundException("CONNECTION_NOT_FOUND");
            }
            else
            {
                var projectPath = body.GetProperty("projectPath").GetString();
                if (string.IsNullOrWhiteSpace(projectPath))
                    throw new ArgumentException("projectPath is required.");
                id = Guid.NewGuid().ToString("N");
                request = body.Clone();
                state.Connections[id] = request;
            }

            var result = await gateway.For("connect").CallAsync<object>("connect", request, ct);
            state.ActiveConnectionId = id;
            return result;
        },
        "Project opened in TIA Portal.");
});
```

- [ ] **Step 4: Run the focused and full API tests**

Run:

```powershell
dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj
```

Expected: PASS.

### Task 2: Tested frontend request construction

**Files:**
- Create: `studio/src/studio/workbench/openProjectInTia.ts`
- Create: `studio/src/studio/workbench/openProjectInTia.test.ts`
- Modify: `studio/package.json`
- Modify: `studio/package-lock.json`
- Modify: `studio/src/api/client.ts`

- [ ] **Step 1: Enable and write the failing Vitest test**

Add `"test": "vitest run"` and Vitest as a development dependency. Test:

```ts
expect(canOpenProjectInTia(null)).toBe(false)
expect(canOpenProjectInTia('   ')).toBe(false)
expect(canOpenProjectInTia('C:\\Projects\\Line.ap17')).toBe(true)
expect(openProjectInTiaRequest(' C:\\Projects\\Line.ap17 ')).toEqual({
  projectPath: 'C:\\Projects\\Line.ap17',
  withUI: true,
})
```

- [ ] **Step 2: Run the focused test and verify RED**

Run:

```powershell
npm test -- openProjectInTia.test.ts
```

Expected: FAIL because the helper module does not exist.

- [ ] **Step 3: Add the minimal helper**

```ts
export const canOpenProjectInTia = (projectPath: string | null | undefined) =>
  Boolean(projectPath?.trim())

export const openProjectInTiaRequest = (projectPath: string) => ({
  projectPath: projectPath.trim(),
  withUI: true as const,
})
```

Update `switchConnection` to accept an optional `operationId` and add it through
the `X-Operation-Id` header.

- [ ] **Step 4: Run the focused frontend test**

Run:

```powershell
npm test -- openProjectInTia.test.ts
```

Expected: PASS.

### Task 3: Studio button and operation flow

**Files:**
- Create: `studio/src/studio/workbench/OpenProjectInTiaButton.tsx`
- Create: `studio/src/studio/workbench/OpenProjectInTiaButton.test.tsx`
- Modify: `studio/src/studio/MainStudio.tsx`

- [ ] **Step 1: Add a failing render assertion**

Render `OpenProjectInTiaButton` with a configured project path and assert its
button is enabled; repeat with a null path and assert its button is disabled.

- [ ] **Step 2: Run the test and verify RED**

Run:

```powershell
npm test -- OpenProjectInTiaButton.test.tsx
```

Expected: FAIL because the action is absent.

- [ ] **Step 3: Implement the click handler and button**

Implement `OpenProjectInTiaButton` as a small presentation component that uses
`canOpenProjectInTia` for its disabled state. In `MainStudio`, use
`activeWorkbench?.sourceProjectPath` as the persisted TIA project path. Guard a
missing/blank path, begin an `open-project-in-tia` operation, call:

```ts
await api.switchConnection(openProjectInTiaRequest(projectPath), op.id)
```

then refresh sessions and show a success toast. Add the secondary button beside
the refresh button with:

```tsx
disabled={Boolean(operation) || !canOpenProjectInTia(activeWorkbench?.sourceProjectPath)}
```

- [ ] **Step 4: Run frontend verification**

Run:

```powershell
npm test
npm run build
npm run lint
```

Expected: all commands PASS.

### Task 4: Full verification and commit

- [ ] **Step 1: Run solution verification**

Run:

```powershell
dotnet test AgentAssistPlcDev.sln
```

Expected: PASS.

- [ ] **Step 2: Inspect the final diff**

Run:

```powershell
git diff --check
git status --short
```

Expected: no whitespace errors and only the planned files plus pre-existing
unrelated user files.

- [ ] **Step 3: Commit the implementation**

```powershell
git add -- src/ApiHost/CompatibilityEndpoints.cs tests/ApiHost.Tests/WorkbenchEndpointsTests.cs studio/src/api/client.ts studio/src/studio/MainStudio.tsx studio/src/studio/workbench/openProjectInTia.ts studio/src/studio/workbench/openProjectInTia.test.ts studio/src/studio/workbench/OpenProjectInTiaButton.tsx studio/src/studio/workbench/OpenProjectInTiaButton.test.tsx studio/package.json studio/package-lock.json docs/superpowers/plans/2026-07-29-open-project-in-tia.md
git commit -m "feat: open workbench project in TIA"
```
