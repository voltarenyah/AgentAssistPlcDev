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

## GitHub Issues and PR Workflow

This repository uses GitHub Issues as the source of truth for development work.
When asked to work on issue `#N`, treat the issue as the task specification.
Codex may investigate issues, implement changes, run validation, create commits,
push branches, and prepare pull requests. Human review is required before
integration into the default branch.

### Core Development Rules

1. Keep every change scoped to the requested GitHub issue.
2. Prefer the smallest change that correctly solves the issue.
3. Preserve existing architecture and conventions unless the issue explicitly
   requires an architectural change.
4. Do not perform unrelated refactoring while implementing an issue.
5. Do not silently change public interfaces, persisted formats, APIs, protocols,
   schemas, or architectural boundaries.
6. Do not modify generated files unless the project explicitly requires it.
7. Do not remove or weaken tests to make a change pass.
8. Do not bypass type checking, linting, validation, or safety checks.
9. Never merge directly into the default branch.
10. Never force-push, rewrite shared history, delete remote branches, or run
    destructive Git operations unless explicitly instructed.

### Read the Issue First

Before changing code for issue `#N`, run:

    gh issue view N --comments

Read the title, description, acceptance criteria, comments, labels, linked
work, reproduction information, and relevant design discussion. Do not
implement based only on the issue title. If the issue is ambiguous, inspect the
repository and existing behavior before deciding that clarification is needed.

### Check for Existing Work

Before implementation, check whether work already exists for the issue:

    gh issue develop --list N
    gh pr list --state open
    git status
    git branch --show-current
    git log -5 --oneline

Look for an existing linked branch, an existing PR, other PRs touching the same
subsystem, conflicting architecture, and local changes that do not belong to
the task. If another active branch or PR already implements the same issue, do
not create a competing implementation. If overlap creates a semantic or
architectural conflict, stop implementation and report the dependency or
conflict.

### Parallel Development and Branches

Assume multiple agents may work on this repository concurrently. Each
independently implemented issue must have its own Codex thread, Git worktree,
Git branch, commit history, and pull request. Never use one working tree for
multiple concurrent issues or make changes in another agent's worktree.

Use one branch for one logical issue, following this naming convention:

    codex/<issue-number>-<short-description>

Examples:

    codex/143-fix-layout-persistence
    codex/218-add-device-context
    codex/305-handle-import-error

Before modifying shared or architectural code, inspect active work. Pay
particular attention to application architecture, shared state,
persistence/database schemas, public interfaces, IPC/API contracts, shared
types, dependency and build configuration, package manifests, and common
infrastructure. A clean Git merge does not guarantee architectural
compatibility.

If issue B depends on issue A, do not duplicate issue A's implementation.
Clearly identify the dependency and prefer waiting for or basing work on the
dependency branch when appropriate. Do not copy unfinished code between
parallel issue branches merely to make a dependent task work.

### Investigation Rules

For bug issues:

1. Understand the expected behavior.
2. Locate the relevant execution path.
3. Reproduce the problem when reasonably possible.
4. Identify the root cause.
5. Check whether nearby code has the same failure mode.
6. Implement the smallest robust fix.
7. Add or update regression coverage where practical.

Do not patch symptoms when the root cause can reasonably be established.
Document the root cause in the final task summary.

For feature issues, identify the responsible architecture and similar existing
functionality, reuse existing abstractions where appropriate, identify
affected interfaces and persisted data, consider backward compatibility,
determine validation requirements, and implement within the existing
architecture whenever practical. Avoid creating a new framework or abstraction
for a single feature unless it solves a demonstrated architectural need.

For investigation-only issues, do not automatically modify production code.
Produce findings, evidence, relevant code locations, root cause if known,
possible solutions, tradeoffs, and a recommended next action. Implement only
when explicitly requested or when implementation is clearly part of the issue
acceptance criteria.

### Scope Control

Classify discovered problems during implementation:

- **Required:** necessary to satisfy the current issue; implement these.
- **Closely related:** directly caused by the same root problem; implement only
  when the change is small, low risk, and improves correctness.
- **Unrelated:** a separate bug, cleanup, refactor, or improvement; do not
  implement it. Mention it in the final report and recommend a separate issue.

### Code Modification Rules

Before editing a file, understand its role, inspect relevant callers and
consumers, search for related tests, and inspect nearby conventions.

When changing behavior, update affected tests, update documentation when
external behavior changes, preserve backward compatibility unless explicitly
changing it, avoid formatting unrelated files, and avoid mass renames unrelated
to the issue. Keep diffs reviewable.

### Validation

Every implementation must be validated before being considered complete. Run
the narrowest relevant checks first, then broader checks when appropriate.
Consider affected unit and integration tests, type checking, linting, builds,
runtime smoke tests, and UI/browser validation when behavior is visual.

Use the repository-specific launcher and test commands documented above for
local service and browser validation. Do not claim something was tested if it
was not actually executed. If a validation step cannot be run, explicitly
state which step was skipped, why it could not run, and what risk remains.

### UI Changes

For user-visible UI changes, run the application when practical, exercise the
changed workflow, verify expected state transitions, check obvious regressions,
and capture screenshots when they materially help review. Compilation alone is
insufficient evidence for behavior that requires runtime interaction.

### Commits

Commits should be logically scoped. Use:

    <type>: <short description> (#<issue>)

Examples:

    fix: restore workspace layout after reload (#143)
    feat: add branch selection to project context (#218)
    test: cover failed PLC import recovery (#305)

Avoid mixing unrelated changes into one commit. Co-author commits with Claude
when applicable using:

    Co-Authored-By: Claude <noreply@anthropic.com>

### Pull Requests

When implementation and validation are complete:

1. Ensure the branch is based on the intended target branch.
2. Review the complete diff.
3. Confirm no unrelated files were changed.
4. Push the issue branch.
5. Create a pull request.
6. Do not merge it.

The PR body should contain:

#### Summary

What changed.

#### Problem

What user or engineering problem caused the change.

#### Root Cause / Design

For bugs, explain the root cause. For features, explain the implementation
approach.

#### Changes

List the important implementation changes.

#### Validation

List commands and runtime checks actually performed.

#### Risks

Describe known risks, limitations, migration concerns, or areas requiring
review.

#### Issue

Include `Fixes #N` when merging the PR should complete the issue. Do not use
`Fixes #N` when the PR should leave the issue open.

### Review Before PR

Before creating a PR, inspect:

    git status
    git diff
    git diff --stat
    git log --oneline <base>..HEAD

Check for accidental changes, debug code, temporary files, generated
artifacts, secrets, commented-out code, unrelated formatting, incomplete
TODOs, missing tests, and unintended dependency changes. Do not submit a PR
with unexplained unrelated changes.

### Updating From the Base Branch

If the target branch changed significantly while the issue was being developed:

1. Fetch the latest remote state.
2. Inspect upstream changes.
3. Update the issue branch.
4. Resolve conflicts intentionally.
5. Rerun affected validation.

When resolving conflicts, preserve the intent of both changes. Do not
mechanically choose ours or theirs without understanding the conflicting
implementations.

### Safety Around Existing Work

Treat existing uncommitted changes as user-owned unless clearly created by the
current task. Do not discard, reset, overwrite, or stash them without reason,
and do not include them in the current issue commit. If unexpected existing
modifications are found, avoid disturbing them.

### Human-Controlled Decisions

Do not independently make high-impact product or architecture decisions when
multiple reasonable choices exist. Escalate decisions involving major
architecture changes, incompatible public API changes, persisted-data
migrations, security model changes, large dependency additions, removal of
major functionality, cross-subsystem redesign, or changes that substantially
expand issue scope. Implementation details within an established design can
be decided locally.

### Completion Report

When finishing an issue, report:

1. Issue
2. Root cause or design approach
3. Files/components changed
4. Important implementation decisions
5. Validation performed
6. Branch
7. Commit(s)
8. PR, if created
9. Remaining risks
10. Follow-up issues discovered

Keep the report concise and evidence-based.

### Definition of Done

An issue implementation is complete when acceptance criteria are satisfied,
relevant validation passes, unrelated changes are excluded, the diff has been
reviewed, the branch contains the intended commits, a PR is ready for human
review, and remaining risks are documented. The agent must not merge the PR
unless explicitly instructed.

### Unattended local Codex automation

Unattended issue, revision, cleanup, and deployment runs follow the same issue
scope, isolated `codex/<issue>-<slug>` worktree, focused/broad validation,
reviewed draft-PR, and no-merge rules above. They must preserve the primary
checkout and unrelated user changes; the wrapper owns commits, pushes, labels,
comments, and PR publication while Codex edits only the active issue worktree.
See [docs/local-codex-worker.md](docs/local-codex-worker.md) for the operational
trust, monitoring, recovery, and deployment procedures.

## Cost-controlled Codex orchestration

- `codex-workflows` is the project workflow. Use the lightest route: direct Codex for tiny work, `$recipe-task` for focused fixes, `$recipe-implement` for meaningful changes, and staged design/plan/build recipes only when durable design decisions require them.
- One parent owns delegation. Children must not recursively delegate, create agent trees, or silently expand the approved scope.
- Default maximum is 3 concurrently active subagents (4 only for clearly beneficial independent read-heavy work). Prefer <=6 fresh subagent contexts per request; 8 is the hard default ceiling. Stop and serialize, combine, reuse, or escalate before exceeding it.
- Prefer read parallelism and serialize overlapping write work. Reuse an existing executor/reviewer context for corrections instead of respawning it.
- Default lifecycle is implement -> focused tests -> one scoped review -> blocking fixes -> targeted verification. A third review cycle requires concrete evidence of an unresolved test, security issue, review finding, or material implementation change.
- Reviews inspect the changed diff, acceptance criteria, affected execution paths, correctness/regression/security, and meaningful missing tests. Do not reopen for style-only or unrelated cleanup.
- The parent/master should normally run as `gpt-5.6-terra` with medium reasoning; child TOMLs must pin their own tier so they do not inherit the parent model.
- Luna Medium handles routine analysis, execution, testing, and mechanical verification. Terra Medium/High handles selective design, review, security, and difficult judgment. `expert-solver` is the only Sol role and is advisory-only, read-only, and escalation-only.
- Escalate Luna -> Terra -> Sol only for unresolved root cause, contradictory evidence, non-obvious cross-layer state/lifecycle behavior, high-risk design ambiguity, or repeated failed attempts with an unknown cause. Distill the problem, evidence, ruled-out hypotheses, constraints, and exact decision before invoking Sol. Never escalate merely because work is large, has many files, or failed once.
