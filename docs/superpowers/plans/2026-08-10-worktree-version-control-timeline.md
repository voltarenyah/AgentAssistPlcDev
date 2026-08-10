# Worktree Version-Control Timeline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a paginated, two-lane Git/SVN/TIA history rail to the worktree Overview page.

**Architecture:** The API will assemble a worktree-scoped timeline from Git commit metadata and historical `engineering-state/revision.json` blobs, emitting SVN rows only for newly recorded SVN revisions and checksums only for commits that changed revision state. A focused React component will fetch 10 Git commits at a time, project linked data into aligned columns, and render keyboard-accessible Git/SVN shapes with hover/focus details.

**Tech Stack:** ASP.NET Core minimal APIs, C# workbench coordinator and MCP gateway, React 19, TypeScript, Tailwind CSS, Vitest, xUnit.

---

## File map

- Create `src/Agent/Workbench/VersionControlTimelineModels.cs`: API-facing timeline records and MCP SVN log DTOs used only by the timeline feature.
- Modify `src/Agent/Workbench/WorkbenchConsistencyService.cs`: retain Git commit author, timestamp, and changed files when deserializing `vc_log` results.
- Modify `src/Agent/Workbench/WorkbenchCoordinator.cs`: add the paginated history aggregation method and historical revision-state mapping.
- Modify `src/ApiHost/WorkbenchApiModels.cs`: expose the worktree timeline endpoint.
- Create `tests/Agent.Tests/WorkbenchVersionControlTimelineTests.cs`: coordinator mapping and pagination tests.
- Create `tests/ApiHost.Tests/WorktreeVersionControlTimelineEndpointTests.cs`: endpoint contract and validation tests.
- Modify `studio/src/api/client.ts`: add TypeScript timeline types and API client method.
- Create `studio/src/studio/workbench/versionControlTimeline.ts`: pure Git/SVN column projection helpers.
- Create `studio/src/studio/workbench/versionControlTimeline.test.ts`: projection and deduplication tests.
- Create `studio/src/studio/workbench/WorktreeVersionControlTimeline.tsx`: fetching, pagination, accessible rail, and hover/focus detail.
- Create `studio/src/studio/workbench/WorktreeVersionControlTimeline.test.tsx`: component behavior tests.
- Modify `studio/src/studio/workbench/WorktreeLandingPage.tsx`: render the timeline between metadata and tasks on Overview.
- Modify `studio/src/studio/workbench/WorktreeLandingPage.test.tsx`: mock the new request and assert the overview placement/content.

## Task 1: Add backend timeline records and Git metadata fields

**Files:**
- Create: `src/Agent/Workbench/VersionControlTimelineModels.cs`
- Modify: `src/Agent/Workbench/WorkbenchConsistencyService.cs`
- Test: `tests/Agent.Tests/WorkbenchVersionControlTimelineTests.cs`

- [ ] **Step 1: Write the failing model/deserialization test**

Add a test fixture that deserializes a `vc_log` payload containing `sha`, `message`, `author`, `timestamp`, and `files`, then asserts all fields survive in `ConsistencyCommit`:

```csharp
[Fact]
public void ConsistencyCommitPreservesTimelineMetadata()
{
    var commit = JsonSerializer.Deserialize<ConsistencyCommit>("""
        {"sha":"abc","message":"savepoint","author":"Ansel","timestamp":"2026-08-10T08:00:00Z","files":["engineering-state/revision.json"]}
        """)!;

    Assert.Equal("Ansel", commit.Author);
    Assert.Equal("2026-08-10T08:00:00Z", commit.Timestamp);
    Assert.Equal("engineering-state/revision.json", Assert.Single(commit.Files));
}
```

- [ ] **Step 2: Run the focused test and verify it fails**

Run:

```powershell
dotnet test tests/Agent.Tests/Agent.Tests.csproj --filter FullyQualifiedName~ConsistencyCommitPreservesTimelineMetadata
```

Expected: FAIL because `ConsistencyCommit` does not yet expose the timeline metadata properties.

- [ ] **Step 3: Add the minimal records and properties**

Add these records in `VersionControlTimelineModels.cs`:

```csharp
namespace Agent.Workbench;

public sealed record VersionControlTimelineResult(
    IReadOnlyList<VersionControlTimelineGitCommit> GitCommits,
    IReadOnlyList<VersionControlTimelineSvnRevision> SvnRevisions,
    bool HasMore);

public sealed record VersionControlTimelineGitCommit(
    string Sha,
    string Author,
    string Message,
    string Timestamp,
    IReadOnlyList<string> Files,
    string? TiaChecksum,
    long? SvnRevision);

public sealed record VersionControlTimelineSvnRevision(
    long Revision,
    string Author,
    string Message,
    string Timestamp,
    string? TiaChecksum,
    string GitCommitSha);

internal sealed class TimelineSvnLogResult
{
    public TimelineSvnLogEntry[] Entries { get; set; } = Array.Empty<TimelineSvnLogEntry>();
}

internal sealed class TimelineSvnLogEntry
{
    public long Revision { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public DateTime Time { get; set; }
}
```

Extend `ConsistencyCommit` with `Author`, `Timestamp`, and `Files`, defaulting to empty values so existing callers remain compatible.

- [ ] **Step 4: Run the focused test and verify it passes**

Run the same `dotnet test` command. Expected: PASS.

- [ ] **Step 5: Commit the model boundary**

```powershell
git add src/Agent/Workbench/VersionControlTimelineModels.cs src/Agent/Workbench/WorkbenchConsistencyService.cs tests/Agent.Tests/WorkbenchVersionControlTimelineTests.cs
git commit -m "feat: add worktree timeline models"
```

## Task 2: Implement coordinator history aggregation

**Files:**
- Modify: `src/Agent/Workbench/WorkbenchCoordinator.cs`
- Modify: `src/Agent/Workbench/VersionControlTimelineModels.cs`
- Test: `tests/Agent.Tests/WorkbenchVersionControlTimelineTests.cs`

- [ ] **Step 1: Write failing aggregation tests**

Add tests using the existing fake MCP caller setup in `WorkbenchCoordinatorTests` patterns. Cover the core mapping:

```csharp
[Fact]
public async Task TimelineMarksOnlyRevisionStateCommitsWithChecksumAndSvnLink()
{
    // vc_log returns newest-first: a Git-only commit followed by a combined savepoint.
    // vc_show_file returns revision.json only for the combined commit.
    // svn_log returns the matching SVN metadata.

    var result = await coordinator.ListVersionControlTimelineAsync("wb-1", "wt-1", 0, 10);

    Assert.Null(result.GitCommits[0].TiaChecksum);
    Assert.Null(result.GitCommits[0].SvnRevision);
    Assert.Equal("PLC_1:checksum-1", result.GitCommits[1].TiaChecksum);
    Assert.Equal(184, result.GitCommits[1].SvnRevision);
    var svn = Assert.Single(result.SvnRevisions);
    Assert.Equal(184, svn.Revision);
    Assert.Equal(result.GitCommits[1].Sha, svn.GitCommitSha);
}

[Fact]
public async Task TimelineUsesOffsetPagesAndReportsOlderHistory()
{
    var result = await coordinator.ListVersionControlTimelineAsync("wb-1", "wt-1", 10, 10);

    Assert.Equal(10, result.GitCommits.Count);
    Assert.True(result.HasMore);
}
```

Also add tests for malformed/missing `revision.json`, duplicate recorded SVN revisions, and a page whose extra look-ahead commit proves `HasMore`.

- [ ] **Step 2: Run the focused tests and verify they fail**

Run:

```powershell
dotnet test tests/Agent.Tests/Agent.Tests.csproj --filter FullyQualifiedName~WorkbenchVersionControlTimeline
```

Expected: FAIL because the coordinator method does not yet exist.

- [ ] **Step 3: Implement the paginated aggregation**

Add:

```csharp
public async Task<VersionControlTimelineResult> ListVersionControlTimelineAsync(
    string workbenchId,
    string worktreeId,
    int offset = 0,
    int limit = 10,
    CancellationToken token = default)
```

Implementation requirements:

1. Reject negative offsets and limits outside `1..50` with a `WorkbenchLifecycleException` code `TIMELINE_PAGE_INVALID`.
2. Resolve the registered worktree and root using the same catalog/path helpers as `ListSavepointsAsync`.
3. Call `vc_log` with `maxCount = offset + limit + 1` and preserve the newest-first order.
4. For every fetched commit, check whether its `Files` contains `EngineeringStateWriter.RelativePath`. Only those commits may produce a checksum or SVN link. Read the historical blob through `vc_show_file` and parse it with `EngineeringStateWriter.TryParse`; malformed content becomes absent metadata.
5. Use a `HashSet<long>` while walking newest-first so each newly encountered recorded SVN revision emits one `VersionControlTimelineSvnRevision`. A Git-only commit never inherits the previous checksum or revision.
6. Resolve each recorded SVN URL with the existing `ResolveSvnUrl` helper, call `svn_log` once per unique URL with enough entries for the requested window, and use exact revision matches for SVN message/author/time. If metadata is unavailable, fall back to the linked Git commit’s message/author/timestamp without failing the whole page.
7. Slice the fetched Git rows with `Skip(offset).Take(limit)`, retain only SVN rows whose linked Git SHA is in that page, and set `HasMore` when the look-ahead row exists.

- [ ] **Step 4: Run the focused tests and verify they pass**

Run the same `dotnet test` filter. Expected: PASS with no new warnings.

- [ ] **Step 5: Commit the coordinator implementation**

```powershell
git add src/Agent/Workbench/WorkbenchCoordinator.cs src/Agent/Workbench/VersionControlTimelineModels.cs tests/Agent.Tests/WorkbenchVersionControlTimelineTests.cs
git commit -m "feat: aggregate worktree version control timeline"
```

## Task 3: Expose the endpoint and TypeScript contract

**Files:**
- Modify: `src/ApiHost/WorkbenchApiModels.cs`
- Create: `tests/ApiHost.Tests/WorktreeVersionControlTimelineEndpointTests.cs`
- Modify: `studio/src/api/client.ts`

- [ ] **Step 1: Write the failing endpoint contract test**

Add a test using the existing `ApiHost.Tests` fixture pattern that requests:

```text
/api/workbenches/{workbenchId}/worktrees/{worktreeId}/vc/timeline?offset=0&limit=10
```

Assert the response is `200`, has `gitCommits`, `svnRevisions`, and `hasMore`, and that invalid values such as `limit=0` return the established problem response rather than reaching the MCP gateway.

- [ ] **Step 2: Run the endpoint test and verify it fails**

```powershell
dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj --filter FullyQualifiedName~WorktreeVersionControlTimelineEndpoint
```

Expected: FAIL with a 404 because the route is not registered.

- [ ] **Step 3: Add the minimal route and client types**

Register this route next to the existing worktree VC routes:

```csharp
app.MapGet("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/vc/timeline", async (
    string workbenchId,
    string worktreeId,
    int? offset,
    int? limit,
    WorkbenchApiState state,
    WorkbenchCoordinator coordinator,
    CancellationToken ct) =>
{
    coordinator.RegisterWorkbench(state.Workbench(workbenchId));
    return Results.Ok(await coordinator.ListVersionControlTimelineAsync(
        workbenchId,
        worktreeId,
        offset ?? 0,
        limit ?? 10,
        ct));
});
```

Add matching types and:

```ts
export const getWorktreeVersionControlTimeline = (
  workbenchId: string,
  worktreeId: string,
  offset = 0,
  limit = 10,
) => workbenchRequest<VersionControlTimelineResult>(
  `/workbenches/${encodeURIComponent(workbenchId)}/worktrees/${encodeURIComponent(worktreeId)}/vc/timeline?offset=${offset}&limit=${limit}`,
)
```

- [ ] **Step 4: Run the endpoint and client checks**

```powershell
dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj --filter FullyQualifiedName~WorktreeVersionControlTimelineEndpoint
cd studio
npm test -- --run src/api/client.appAssistant.test.ts
cd ..
```

Expected: PASS.

- [ ] **Step 5: Commit the API contract**

```powershell
git add src/ApiHost/WorkbenchApiModels.cs tests/ApiHost.Tests/WorktreeVersionControlTimelineEndpointTests.cs studio/src/api/client.ts
git commit -m "feat: expose worktree timeline endpoint"
```

## Task 4: Add pure timeline projection helpers

**Files:**
- Create: `studio/src/studio/workbench/versionControlTimeline.ts`
- Create: `studio/src/studio/workbench/versionControlTimeline.test.ts`

- [ ] **Step 1: Write failing projection tests**

Use a response containing four Git commits and two linked SVN revisions. Assert the helper returns four columns in oldest-to-newest order, places each SVN revision under its linked SHA, and leaves Git-only columns with `svn: null`.

```ts
it('aligns SVN revisions under their linked Git commits', () => {
  const columns = buildTimelineColumns({ gitCommits, svnRevisions, hasMore: false })
  expect(columns.map(column => column.git.sha)).toEqual(['old', 'middle', 'new', 'latest'])
  expect(columns[0].svn?.revision).toBe(182)
  expect(columns[1].svn).toBeNull()
  expect(columns[2].svn?.revision).toBe(183)
  expect(columns[3].svn).toBeNull()
})
```

- [ ] **Step 2: Run the helper test and verify it fails**

```powershell
cd studio
npm test -- --run src/studio/workbench/versionControlTimeline.test.ts
cd ..
```

Expected: FAIL because the helper module does not exist.

- [ ] **Step 3: Implement the projection**

Export `TimelineColumn` and `buildTimelineColumns(result)`. Build a map by `svn.gitCommitSha`, reverse the Git array for left-to-right chronological display, and return `{ git, svn }` for every Git commit. Ignore an SVN row whose linked Git SHA is not in the returned Git page.

- [ ] **Step 4: Run the helper test and verify it passes**

Run the same Vitest command. Expected: PASS.

- [ ] **Step 5: Commit the projection helper**

```powershell
git add studio/src/studio/workbench/versionControlTimeline.ts studio/src/studio/workbench/versionControlTimeline.test.ts
git commit -m "feat: project aligned worktree timeline columns"
```

## Task 5: Build the accessible React timeline component

**Files:**
- Create: `studio/src/studio/workbench/WorktreeVersionControlTimeline.tsx`
- Create: `studio/src/studio/workbench/WorktreeVersionControlTimeline.test.tsx`

- [ ] **Step 1: Write failing component tests**

Cover these behaviors with the existing `createRoot` + `act` test style:

```tsx
it('loads ten commits initially and shows linked Git/SVN labels', async () => {
  vi.spyOn(api, 'getWorktreeVersionControlTimeline').mockResolvedValue(page)
  const { host } = await render(<WorktreeVersionControlTimeline workbenchId="wb-1" worktreeId="wt-1" />)

  expect(api.getWorktreeVersionControlTimeline).toHaveBeenCalledWith('wb-1', 'wt-1', 0, 10)
  expect(host.textContent).toContain('a8f2c1d')
  expect(host.textContent).toContain('r184')
  expect(host.querySelector('[data-timeline-link="a8f2c1d-r184"]')).not.toBeNull()
})

it('loads the next page without duplicating prior commits', async () => {
  const load = vi.spyOn(api, 'getWorktreeVersionControlTimeline')
    .mockResolvedValueOnce({ ...page, hasMore: true })
    .mockResolvedValueOnce(nextPage)
  const { host } = await render(<WorktreeVersionControlTimeline workbenchId="wb-1" worktreeId="wt-1" />)

  await act(async () => host.querySelector<HTMLButtonElement>('[data-testid="timeline-load-more"]')?.click())
  expect(load).toHaveBeenLastCalledWith('wb-1', 'wt-1', 10, 10)
  expect(host.querySelectorAll('[data-timeline-git]').length).toBe(20)
})

it('shows event details on focus', async () => {
  const { host } = await render(<WorktreeVersionControlTimeline workbenchId="wb-1" worktreeId="wt-1" />)
  await act(async () => host.querySelector<HTMLButtonElement>('[data-timeline-git="abcdef1234567890"]')?.focus())
  expect(host.textContent).toContain('Validate Main block')
  expect(host.textContent).toContain('Ansel')
  expect(host.textContent).toContain('2026-08-04')
  expect(host.textContent).toContain('abcdef1234567890')
  expect(host.textContent).toContain('PLC_1:checksum-1')
})
```

Also cover loading, empty, request error with retry, `TIA —`, and a Git-only commit with no SVN shape.

- [ ] **Step 2: Run the component test and verify it fails**

```powershell
cd studio
npm test -- --run src/studio/workbench/WorktreeVersionControlTimeline.test.tsx
cd ..
```

Expected: FAIL because the component does not exist.

- [ ] **Step 3: Implement the minimal component**

Implement a `PAGE_SIZE = 10` fetch loop with `offset = loadedGitCount`. Keep `gitCommits`, `svnRevisions`, `hasMore`, `loading`, `loadingMore`, and `error` in local state. On retry, reload the current offset. Deduplicate Git commits by full SHA when appending.

Render:

```tsx
<section aria-label="Worktree version control" className="overflow-hidden rounded-xl border bg-card">
  <header className="flex items-center border-b px-4 py-3" style={{ borderColor: 'var(--border)' }}>
    <h2 className="text-sm font-semibold">Worktree version control</h2>
  </header>
  <div className="overflow-x-auto" aria-label="Git and SVN history">
    <div className="min-w-[640px]">{columns.map(column => <TimelineColumnView key={column.git.sha} column={column} onActivate={setActiveEvent} />)}</div>
  </div>
  {hasMore && <button data-testid="timeline-load-more">Load more</button>}
</section>
```

Use `<button>` elements for shapes. Add `data-timeline-git`, `data-timeline-svn`, and `data-timeline-link` markers for behavior tests. Show the detail surface for mouse enter or keyboard focus and include the full values in accessible text. Use a minimum column width and `overflow-x-auto`; do not add a time axis below the lanes.

- [ ] **Step 4: Run the component test and verify it passes**

Run the same Vitest command. Expected: PASS.

- [ ] **Step 5: Commit the component**

```powershell
git add studio/src/studio/workbench/WorktreeVersionControlTimeline.tsx studio/src/studio/workbench/WorktreeVersionControlTimeline.test.tsx
git commit -m "feat: add worktree version control timeline"
```

## Task 6: Integrate the timeline into the worktree Overview page

**Files:**
- Modify: `studio/src/studio/workbench/WorktreeLandingPage.tsx`
- Modify: `studio/src/studio/workbench/WorktreeLandingPage.test.tsx`

- [ ] **Step 1: Add the failing integration assertion**

Mock `getWorktreeVersionControlTimeline` in `WorktreeLandingPage.test.tsx` with an empty page, then assert the Overview render contains `Worktree version control` and that the timeline appears before `Tasks` in the document order.

- [ ] **Step 2: Run the focused page test and verify it fails**

```powershell
cd studio
npm test -- --run src/studio/workbench/WorktreeLandingPage.test.tsx
cd ..
```

Expected: FAIL because the page does not render the timeline.

- [ ] **Step 3: Integrate the component**

Import `WorktreeVersionControlTimeline` and render it in the Overview fragment immediately after the metadata section and before the Tasks section:

```tsx
<WorktreeVersionControlTimeline workbenchId={workbenchId} worktreeId={worktreeId} />
```

Keep the component mounted only for `tab === 'overview'`, matching the existing modified-block loading behavior.

- [ ] **Step 4: Run page tests and the Studio build**

```powershell
cd studio
npm test -- --run src/studio/workbench/WorktreeLandingPage.test.tsx
npm run lint
npm run build
cd ..
```

Expected: all commands PASS.

- [ ] **Step 5: Commit the page integration**

```powershell
git add studio/src/studio/workbench/WorktreeLandingPage.tsx studio/src/studio/workbench/WorktreeLandingPage.test.tsx
git commit -m "feat: show version control timeline on worktree overview"
```

## Task 7: Full verification and handoff

**Files:**
- No new files; verify all touched files and preserve unrelated existing worktree changes.

- [ ] **Step 1: Run focused backend tests**

```powershell
dotnet test tests/Agent.Tests/Agent.Tests.csproj --filter FullyQualifiedName~WorkbenchVersionControlTimeline
dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj --filter FullyQualifiedName~WorktreeVersionControlTimelineEndpoint
```

Expected: PASS.

- [ ] **Step 2: Run focused Studio tests**

```powershell
cd studio
npm test -- --run src/studio/workbench/versionControlTimeline.test.ts src/studio/workbench/WorktreeVersionControlTimeline.test.tsx src/studio/workbench/WorktreeLandingPage.test.tsx
cd ..
```

Expected: PASS.

- [ ] **Step 3: Run the complete relevant suites**

```powershell
dotnet test tests/Agent.Tests/Agent.Tests.csproj
dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj
cd studio
npm test
npm run lint
npm run build
cd ..
```

Expected: PASS with no new warnings or TypeScript errors.

- [ ] **Step 4: Inspect the final diff and status**

```powershell
git diff --check HEAD~7..HEAD
git status --short
```

Confirm the commits contain only the timeline feature and that pre-existing modifications in `src/ApiHost/CompatibilityEndpoints.cs` and `tests/ApiHost.Tests/ApiMcpGatewayRoutingTests.cs` remain untouched.
