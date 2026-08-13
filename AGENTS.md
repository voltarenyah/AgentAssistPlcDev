# Agent instructions for AgentAssistPlcDev

## Start the local test flow

When testing the application in development mode, use the repository launcher as the single service entry point. It starts the ASP.NET API, the Vite frontend, and the LangGraph Python sidecar.

From the repository root:

```powershell
.\launch.ps1
```

Use `-NoBuild` when the code is already built and a faster restart is useful:

```powershell
.\launch.ps1 -NoBuild
```

The launcher normally stops old ApiHost, Node, MCP, and LangGraph sidecar processes first. Use `-NoKill` only when intentionally keeping the existing processes.

Service URLs:

- Frontend: `http://localhost:5173/`
- ApiHost: `http://localhost:5239/`
- LangGraph sidecar: `http://localhost:8787/`

Wait for the launcher to finish its health check before starting UI tests. Confirm all services are reachable:

```powershell
Invoke-WebRequest -UseBasicParsing http://localhost:5173/
Invoke-WebRequest -UseBasicParsing http://localhost:5239/api/status
Invoke-RestMethod http://localhost:8787/health
```

The expected result is HTTP 200 from the frontend and ApiHost, plus `status: ok` from the sidecar. The sidecar health response also identifies the configured model and whether it is using the live model or deterministic fallback.

## Open and test the web application

After the health checks pass, open or navigate the in-app browser to:

```text
http://localhost:5173/
```

Use the browser automation capability when available. Keep browser checks grounded in the visible DOM and verify the result after every meaningful click or submission.

Recommended smoke flow:

1. Confirm the project list and workbench overview render.
2. Open **Workbench Assistant** from the overview while no worktree is selected. The panel must render without an error boundary or `worktreeId` exception.
3. Wait for the orientation response and confirm the assistant lists the available worktrees.
4. Send a read-only request, such as asking for recent commit history or todo items. Confirm the response describes the requested state.
5. Send a mutation request, such as creating a new worktree. If a baseline is ambiguous, choose the proposed baseline option.
6. Confirm the assistant shows an explicit approval card with **Approve** and **Reject** controls. Do not approve a mutation unless the test specifically requires exercising the mutation itself.

For the LangGraph approval workflow, the expected API/SSE sequence is:

```text
progress -> state -> interrupt -> answer
```

The response should contain `decision.kind = mutation_proposal`, a `pendingApproval` object, and an answer that clearly says the proposal is ready for approval. An old orientation answer is a failure because it hides the current action state.

## Automated test commands

Frontend tests:

```powershell
Push-Location studio
npm test -- --run
Pop-Location
```

Python sidecar tests:

```powershell
Push-Location agent-service
.\.venv\Scripts\python.exe -m pytest -q
Pop-Location
```

Useful focused sidecar tests:

```powershell
Push-Location agent-service
.\.venv\Scripts\python.exe -m pytest tests/test_mutations.py tests/test_graph.py tests/test_live_sidecar.py tests/test_observability.py -q
Pop-Location
```

ApiHost tests:

```powershell
dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj --no-build -v q
```

For a full .NET validation run, build or test the solution after stopping the development launcher if the ApiHost executable is locked:

```powershell
dotnet build AgentAssistPlcDev.sln -v q
dotnet test AgentAssistPlcDev.sln --no-build -v q
```

## Test-flow safety and handoff rules

- Inspect `git status --short` before changing files and preserve existing uncommitted work.
- Do not create or approve a real worktree merely to prove that the approval UI works; verifying the proposal card is sufficient unless mutation execution is explicitly requested.
- If a test changes the selected workbench, worktree, or device, restore the intended selection before handing the workspace back.
- Treat repeated `ECONNREFUSED localhost:3000` messages from frontend tests as test-environment noise only when the test command still reports all tests passed; investigate any actual test failure.
- Check browser console errors after UI tests. Ignore unrelated external telemetry timeout messages, but never ignore errors originating from the local application.
- Report service health, automated test totals, browser scenarios exercised, and any warnings when handing off a test run.
