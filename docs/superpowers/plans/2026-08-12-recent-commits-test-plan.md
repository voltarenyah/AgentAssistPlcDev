# Recent Commits Functional Test Plan

**Scope:** commits `2be21b9`, `d3cce69`, `e8b8649`, `3acbc57`, `d4ae6e7`, `ed644ad`, `5a981f5`, and `a316426`.

**Goal:** verify the LangGraph worktree-assistance flow, shared runtime-state reporting, and Git/SVN version-control timeline from unit level through the running application.

**Important repository state:** the worktree is not clean. Six files contain uncommitted changes. Those changes must be recorded separately from the committed results and tested as an additional scope:

- `agent-service/app_assistant/graph.py`
- `agent-service/tests/test_mutations.py`
- `src/ApiHost/WorkbenchApiModels.cs`
- `studio/src/studio/MainStudio.tsx`
- `studio/src/studio/workbench/ProjectLandingPage.tsx`
- `studio/src/studio/workbench/WorktreeVersionControlTimeline.tsx`

## 1. Change inventory

### `2be21b9` — LangGraph assistance and runtime state

- Adds structured clarification options to assistant decisions and returns the decision payload from the sidecar API.
- Infers `create_worktree.startPoint` from the focused worktree, UI focus, or the only available worktree.
- Asks the user to choose a baseline when multiple worktrees exist without focus.
- Moves history reads out of the initial assistant context; explicit Git/SVN history actions remain responsible for loading history.
- Refreshes runtime state before runtime-state and runtime-event responses.
- Expands the runtime snapshot with Git, SVN, validation, and device observations.
- Adds the Studio runtime-state status bar and assistant clarification-option buttons.
- Adds/updates unit and component tests for the above behavior.

### `d3cce69` — workflow design documentation

- Documentation only. Confirm the implementation still follows the create-workbench workflow described in `docs/superpowers/specs/2026-08-11-langgraph-create-workbench-workflow-design.md`.

### `a316426`, `5a981f5`, `ed644ad`, `d4ae6e7`, `3acbc57`, `e8b8649` — version-control timeline

- Aggregates Git commits and linked SVN revisions into a worktree timeline.
- Adds the API endpoint and page/offset validation.
- Corrects SVN history routing and repository-history reading.
- Adds the Studio timeline to the worktree overview between metadata and tasks.
- Adds hover/focus event details, pagination, Git/SVN links, checksum display, timestamp formatting, and responsive horizontal scrolling.

## 2. Automated test pass

Run from `C:\Users\Ansel\orca\projects\AgentAssistPlcDev`.

### Python assistant service

```powershell
Set-Location agent-service
py -3.13 -m pytest tests/test_graph.py tests/test_mutations.py tests/test_gateway.py tests/test_evaluation.py -q
Set-Location ..
```

Required assertions:

- Focused worktree supplies its branch as `startPoint`.
- UI focus is used when the runtime focus projection is stale.
- A single worktree supplies the default baseline.
- Multiple unfocused worktrees produce a clarification with concrete options.
- An explicit model-provided baseline is preserved.
- Approval uses the inferred baseline and still remains approval-gated.
- Missing or stale context does not execute a mutation.
- Explicit Git/SVN history commands call the gateway with the requested depth.
- The sidecar response includes `decision.options` when clarification is returned.

### .NET API, agent, and version-control tests

```powershell
dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj --no-restore
dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-restore
dotnet test tests/Mcp.VersionControl.Tests/Mcp.VersionControl.Tests.csproj --no-restore
dotnet test tests/E2E.Tests/E2E.Tests.csproj --no-restore
```

Focus the review on these existing test areas:

- `tests/ApiHost.Tests/RuntimeStateEndpointsTests.cs`
- `tests/ApiHost.Tests/RuntimeStateEventEndpointTests.cs`
- `tests/ApiHost.Tests/AppAssistantGatewayTests.cs`
- `tests/ApiHost.Tests/AppAssistantEndpointsTests.cs`
- `tests/ApiHost.Tests/ApiMcpGatewayRoutingTests.cs`
- `tests/ApiHost.Tests/WorktreeLandingEndpointsTests.cs`
- `tests/Agent.Tests/WorkbenchRuntimeStateTests.cs`
- `tests/Agent.Tests/WorkbenchCoordinatorTests.cs`
- `tests/Mcp.VersionControl.Tests/Svn/SvnRepositoryServiceTests.cs`
- `tests/E2E.Tests/WorkbenchLifecycleTests.cs`

Required assertions:

- Runtime-state GET and SSE endpoints refresh the workbench before producing a snapshot.
- The snapshot contains the current revision, focus, worktree observations, operation state, and available actions.
- Unknown workbenches return the established error response.
- Assistant context no longer performs hidden history reads during bootstrap.
- Explicit Git/SVN history endpoints route to the correct worktree and depth.
- Invalid timeline page arguments are rejected before the version-control service is called.
- Git commits and SVN revisions are linked deterministically and remain newest-first.
- Create-worktree lifecycle behavior remains approval-gated and revision-safe.

### Studio component tests

```powershell
Set-Location studio
npm test
Set-Location ..
```

Focus the review on:

- `studio/src/studio/appAssistant/AppAssistantPanel.test.tsx`
- `studio/src/studio/appAssistant/appAssistantState.test.ts`
- `studio/src/studio/workbench/RuntimeStateStatusBar.test.tsx`
- `studio/src/studio/workbench/WorktreeLandingPage.test.tsx`
- `studio/src/studio/workbench/WorktreeVersionControlTimeline.test.tsx`
- `studio/src/studio/workbench/versionControlTimeline.test.ts`

Add or verify coverage for:

- Assistant bootstrap shows busy state, then clears it on success or failure.
- Clarification options render labels/descriptions, are disabled while busy, and submit the selected value.
- A later runtime snapshot clears old clarification options.
- Focus changes are treated as consequential runtime changes.
- Runtime status renders null/unknown values safely and formats numeric/string operation states.
- Timeline loads page zero with ten items, appends page ten without duplicates, and hides Load more at the end.
- Empty, API-error, retry, malformed timestamp, and missing checksum cases render safely.
- Git and SVN shapes expose keyboard focus behavior and event details.
- Long Git hashes and device-prefixed TIA checksums display in the intended shortened form.

## 3. Running-stack smoke test

Start the application using the project workflow:

```powershell
.\launch.ps1
```

Wait for both readiness checks:

```powershell
Invoke-WebRequest http://localhost:5239/api/status
Invoke-WebRequest http://localhost:5173/
```

Then open `http://localhost:5173/` in the browser.

### Smoke scenarios

1. Open a workbench and confirm the runtime-state status item appears in the Studio status area.
2. Open the runtime-state details and verify revision, operation, focus, Git status, HEAD, todo count, SVN revisions, validation, device/TIA state, knowledge freshness, and observed time are readable.
3. Change worktree selection and confirm the runtime focus and status details update without a full page reload.
4. Open the App Assistant and confirm bootstrap completes without an error or indefinite spinner.
5. Ask for orientation. Confirm the answer reflects the selected worktree and current runtime state.
6. Request a new worktree while one worktree is focused. Confirm the proposed baseline is the focused branch and that no mutation occurs before approval.
7. Repeat with multiple worktrees and no focus. Confirm concrete baseline buttons are shown; select one, approve the proposal, and verify the new worktree appears.
8. Open a worktree overview. Confirm the order is metadata, version-control timeline, then tasks.
9. Confirm the timeline shows Git commits, linked SVN revisions, timestamps, shortened hashes/checksums, and linking lines.
10. Hover and keyboard-focus timeline events. Confirm details appear and contain the matching message, author, time, checksum, and linked identifier.
11. Use Load more. Confirm the next page is requested and prior events are not duplicated.
12. Verify the empty-history and API-error states, including Retry.

## 4. Targeted regression scenarios

| ID | Scenario | Expected result |
|---|---|---|
| R1 | Runtime filesystem or repository state changes after initial load | A runtime-state request observes the change and returns a newer or updated snapshot. |
| R2 | Runtime SSE connection receives an update | Studio updates focus/status/timeline-related state without reload. |
| R3 | Browser lacks `EventSource` | Initial runtime GET still works; the UI remains usable without subscription errors. |
| R4 | Assistant receives multiple worktrees with no focused worktree | Clarification options list each usable branch/name, with no false claim that creation is impossible. |
| R5 | Assistant receives no worktrees and no explicit baseline | No mutation proposal is executed; the user receives a clear clarification/error. |
| R6 | Approved mutation races with a changed workbench revision | The operation is rejected as stale and the assistant refreshes context. |
| R7 | SVN history endpoint is queried for the wrong worktree | The request is routed to the requested worktree and does not leak focused-worktree data. |
| R8 | Timeline has Git-only, SVN-only, and linked Git/SVN records | All valid records render; only valid links are drawn. |
| R9 | Timeline is viewed at a narrow viewport | Horizontal scrolling preserves labels and event columns; details remain readable. |
| R10 | Timeline event is near the browser edge | Hover/focus details stay inside the viewport. |
| R11 | Long root/source-project paths appear on the project landing page | Text truncates visually and the full value is available through the title attribute. |
| R12 | API returns unknown Git status or missing HEAD | Previously observed runtime values are retained where intended; otherwise UI shows `unknown` safely. |

## 5. Exit criteria

- Python, .NET, and Studio automated suites pass.
- No browser console errors occur during the smoke scenarios.
- No unexpected failed network requests occur during assistant, runtime-state, or timeline flows.
- Every scenario R1–R12 is either passed or has a captured defect with reproduction steps.
- Results identify whether they came from committed `HEAD` or from the six uncommitted working-tree changes.

## 6. Recommended follow-up automation

Add browser-level tests after the smoke pass for the highest-risk flows:

1. Runtime-state initial load and live focus update.
2. Assistant clarification-option selection followed by approval.
3. Timeline initial load, event details, pagination, and retry.

These tests should launch the stack through `launch.ps1`, poll `/api/status` and `/`, use a disposable workbench fixture, and collect browser console/network logs on failure.
