# Local Codex GitHub Issue Automation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build an immediate, single-worker GitHub Issue to local Codex to draft-PR pipeline, plus a safe post-merge local rebuild prompt with a ten-second countdown, five-minute snooze, cancellation, health verification, and rollback.

**Architecture:** Three narrow GitHub Actions workflows dispatch trusted issue, revision, and PR-close events to one interactive Windows self-hosted runner. A repository-owned PowerShell module owns durable state, a cross-process lock, issue worktrees, `codex exec`, publication, monitoring, cleanup, and A/B runtime deployment; thin scripts expose those capabilities to workflows and Task Scheduler. Codex can edit only the active issue worktree, while the wrapper retains GitHub credentials and owns commits, pushes, labels, comments, and draft-PR creation.

**Tech Stack:** PowerShell 7, Pester 3-compatible tests, Git/Git worktrees, GitHub CLI and Actions, Codex CLI `codex exec`, WPF, Windows Task Scheduler, .NET 8, Node/npm, Python 3.11-3.13, existing `launch.ps1`.

---

## Scope and delivery phases

This is one plan because issue execution, deployment, worktree ownership, logs, and restart recovery share the same state and lock. Deliver it in two independently demonstrable phases:

1. **Issue automation:** trusted label -> isolated worktree -> Codex -> validation -> commit -> push -> draft PR -> parked worktree -> revision.
2. **Local deployment:** merged/closed PR -> guarded cleanup -> pending deployment -> visible countdown -> deploy/later/cancel -> A/B runtime slot -> health evidence/rollback.

Do not enable production labels or install the self-hosted runner until Phase 1 unit tests pass. Do not enable automatic deployment notification until Phase 2 unit tests and a dry-run runtime-slot preparation pass.

## File structure

Create these focused units:

- `.github/workflows/codex-issue.yml`: initial issue and retry event dispatch.
- `.github/workflows/codex-revise.yml`: existing-PR revision dispatch.
- `.github/workflows/codex-pr-closed.yml`: cleanup and merged-commit deployment handoff.
- `scripts/codex-worker/CodexWorker.psd1`: module manifest and explicit exports.
- `scripts/codex-worker/CodexWorker.psm1`: strict-mode module loader only.
- `scripts/codex-worker/config.example.json`: non-secret path, label, timeout, and runtime-slot settings.
- `scripts/codex-worker/schemas/final-summary.schema.json`: stable `codex exec` handoff contract.
- `scripts/codex-worker/prompts/issue.md`: initial issue execution policy.
- `scripts/codex-worker/prompts/revision.md`: PR-feedback execution policy.
- `scripts/codex-worker/Private/State.ps1`: atomic durable state and path resolution.
- `scripts/codex-worker/Private/Lock.ps1`: one exclusive cross-process worker lock.
- `scripts/codex-worker/Private/GitHub.ps1`: trust checks, issue/PR reads, labels, and comments.
- `scripts/codex-worker/Private/Worktree.ps1`: branch/worktree discovery, creation, preparation, parking, and cleanup guards.
- `scripts/codex-worker/Private/Codex.ps1`: prompt creation, secret-scrubbed process execution, JSONL logging, and summary parsing.
- `scripts/codex-worker/Private/Publish.ps1`: diff validation, commit, push, draft PR, and publication recovery.
- `scripts/codex-worker/Private/Deployment.ps1`: pending deployment, A/B slots, prebuild, launch, health checks, and rollback.
- `scripts/codex-worker/Private/Notification.ps1`: interactive-session probe and WPF countdown dialog.
- `scripts/codex-worker/Invoke-Issue.ps1`: initial/retry workflow entry point.
- `scripts/codex-worker/Invoke-Revision.ps1`: revision workflow entry point.
- `scripts/codex-worker/Register-PrClosed.ps1`: cleanup and merge-handoff entry point.
- `scripts/codex-worker/Invoke-DeploymentNotifier.ps1`: Task Scheduler entry point and watch loop.
- `scripts/codex-worker/Install-LocalWorker.ps1`: Codex CLI, runner, labels, config, and scheduled-task setup.
- `scripts/codex-worker/Test-CodexWorker.ps1`: one command that runs all worker Pester tests and returns a reliable exit code.
- `scripts/tests/CodexWorker.State.Tests.ps1`: state, paths, and lock tests.
- `scripts/tests/CodexWorker.GitHub.Tests.ps1`: actor permission, lifecycle labels, and issue-context tests.
- `scripts/tests/CodexWorker.Worktree.Tests.ps1`: idempotent branch/worktree and cleanup-guard tests.
- `scripts/tests/CodexWorker.Codex.Tests.ps1`: prompt, environment redaction, JSONL, schema, and resume tests.
- `scripts/tests/CodexWorker.Publish.Tests.ps1`: commit/PR and publication-recovery tests.
- `scripts/tests/CodexWorker.Install.Tests.ps1`: prerequisite, runner, label, config, and scheduled-task setup tests.
- `scripts/tests/CodexWorker.Deployment.Tests.ps1`: coalescing, runtime slots, health, and rollback tests.
- `scripts/tests/CodexWorker.Notification.Tests.ps1`: countdown, later, cancel, close-window, and session-availability tests.
- `scripts/tests/CodexWorker.Workflows.Tests.ps1`: static workflow trigger, permission, trusted-ref, and queue-preservation tests.
- `docs/local-codex-worker.md`: operator setup, monitoring, state recovery, labels, revisions, cleanup, and deployment guide.
- `launch.ps1`: make `-NoBuild` pass `--no-build` to `dotnet run` so the service switch performs no hidden build after killing the previous processes.

Durable data lives outside Git at `%LOCALAPPDATA%\AutomationWorkbench\CodexWorker`. Issue and runtime worktrees stay below the already ignored repository `.worktrees` directory.

### Task 1: Establish module configuration, paths, and test runner

**Files:**
- Create: `scripts/codex-worker/CodexWorker.psd1`
- Create: `scripts/codex-worker/CodexWorker.psm1`
- Create: `scripts/codex-worker/config.example.json`
- Create: `scripts/codex-worker/Private/State.ps1`
- Create: `scripts/codex-worker/Test-CodexWorker.ps1`
- Test: `scripts/tests/CodexWorker.State.Tests.ps1`

- [ ] **Step 1: Write failing configuration/path tests**

Create Pester tests that import the module and assert deterministic defaults without touching the real user profile:

```powershell
Describe 'Codex worker paths' {
    BeforeEach {
        Import-Module (Join-Path $PSScriptRoot '..\codex-worker\CodexWorker.psd1') -Force
    }

    It 'keeps durable state outside the repository' {
        $paths = Resolve-CodexWorkerPaths -RepositoryRoot 'C:\repo' -DataRoot (Join-Path $TestDrive 'worker')
        $paths.RepositoryRoot | Should Be 'C:\repo'
        $paths.WorktreeRoot | Should Be 'C:\repo\.worktrees'
        $paths.StatePath | Should Be (Join-Path $TestDrive 'worker\state.json')
        $paths.RunRoot | Should Be (Join-Path $TestDrive 'worker\runs')
    }

    It 'rejects a relative repository root' {
        { Resolve-CodexWorkerPaths -RepositoryRoot '.\repo' -DataRoot (Join-Path $TestDrive 'worker') } |
            Should Throw 'RepositoryRoot must be absolute.'
    }
}
```

- [ ] **Step 2: Run the test and verify the module is missing**

Run:

```powershell
powershell.exe -NoProfile -Command "Invoke-Pester -Script scripts/tests/CodexWorker.State.Tests.ps1 -PassThru"
```

Expected: FAIL because `CodexWorker.psd1` or `Resolve-CodexWorkerPaths` does not exist.

- [ ] **Step 3: Add the module loader and explicit configuration model**

Use `Set-StrictMode -Version Latest`. `CodexWorker.psm1` must dot-source every file in `Private` in sorted order. The manifest exports only the functions used by entry scripts and tests.

`config.example.json` must contain real defaults and no credentials:

```json
{
  "repository": "voltarenyah/AgentAssistPlcDev",
  "repositoryRoot": "C:\\Users\\Ansel\\orca\\projects\\AgentAssistPlcDev",
  "defaultBranch": "master",
  "runnerLabel": "agentassist-local",
  "codexCommand": "codex",
  "bootstrapPython": "C:\\Users\\Ansel\\orca\\projects\\AgentAssistPlcDev\\agent-service\\.venv\\Scripts\\python.exe",
  "workerLockTimeoutSeconds": 30,
  "codexTimeoutMinutes": 120,
  "notificationSeconds": 10,
  "snoozeMinutes": 5,
  "healthTimeoutSeconds": 60,
  "runRetentionDays": 30,
  "runtimeSlots": ["runtime-a", "runtime-b"]
}
```

Implement `Resolve-CodexWorkerPaths` with `[System.IO.Path]::GetFullPath`, absolute-path checks, and these returned fields: `RepositoryRoot`, `WorktreeRoot`, `DataRoot`, `StatePath`, `RunRoot`, `ConfigPath`, and `LockPath`. Default `DataRoot` to `Join-Path $env:LOCALAPPDATA 'AutomationWorkbench\CodexWorker'` only when the caller omits it.

- [ ] **Step 4: Add a reliable all-tests entry point**

`Test-CodexWorker.ps1` must collect `scripts/tests/CodexWorker.*.Tests.ps1`, run Pester with `-PassThru`, print totals, and `exit 1` when `FailedCount -gt 0`:

```powershell
$result = Invoke-Pester -Script $tests -PassThru
if ($result.FailedCount -gt 0) { exit 1 }
exit 0
```

- [ ] **Step 5: Run focused tests**

Run:

```powershell
powershell.exe -NoProfile -File scripts/codex-worker/Test-CodexWorker.ps1
```

Expected: PASS for the path tests with `FailedCount: 0`.

- [ ] **Step 6: Commit**

```powershell
git add scripts/codex-worker scripts/tests/CodexWorker.State.Tests.ps1
git commit -m "feat: add local Codex worker foundation"
```

### Task 2: Add atomic state and the exclusive worker lock

**Files:**
- Modify: `scripts/codex-worker/Private/State.ps1`
- Create: `scripts/codex-worker/Private/Lock.ps1`
- Modify: `scripts/tests/CodexWorker.State.Tests.ps1`

- [ ] **Step 1: Write failing state round-trip and contention tests**

Cover a missing-state default, atomic write/read, corrupt JSON quarantine, and exclusive lock contention:

```powershell
It 'round trips durable state atomically' {
    $path = Join-Path $TestDrive 'state.json'
    Write-CodexWorkerState -Path $path -State ([pscustomobject]@{
        schemaVersion = 1
        issues = @{}
        deployment = $null
    })
    (Read-CodexWorkerState -Path $path).schemaVersion | Should Be 1
    Test-Path "$path.tmp" | Should Be $false
}

It 'allows only one lock holder' {
    $lockPath = Join-Path $TestDrive 'worker.lock'
    $first = Enter-CodexWorkerLock -Path $lockPath -TimeoutSeconds 1
    try {
        { Enter-CodexWorkerLock -Path $lockPath -TimeoutSeconds 0 } | Should Throw 'Worker lock is busy.'
    } finally {
        Exit-CodexWorkerLock -Handle $first
    }
}
```

- [ ] **Step 2: Run the focused tests and verify failure**

Expected: FAIL because `Write-CodexWorkerState` and `Enter-CodexWorkerLock` are undefined.

- [ ] **Step 3: Implement atomic state and lock functions**

Use UTF-8 JSON, a same-directory temporary file, `Move-Item -Force`, and an exclusive `FileStream` opened with `FileShare.None`. A corrupt state file is moved to a UTC-stamped name such as `state.corrupt.20260817T143012Z.json`, and the function returns a fresh schema-version-1 state rather than overwriting evidence.

`Enter-CodexWorkerLock` polls every 250 ms until the timeout and returns the open stream. `Exit-CodexWorkerLock` disposes only the supplied handle. Never delete the lock path recursively.

- [ ] **Step 4: Run state tests**

Expected: all state and lock tests PASS.

- [ ] **Step 5: Commit**

```powershell
git add scripts/codex-worker/Private/State.ps1 scripts/codex-worker/Private/Lock.ps1 scripts/tests/CodexWorker.State.Tests.ps1
git commit -m "feat: persist and lock Codex worker state"
```

### Task 3: Add trusted GitHub intake and lifecycle status

**Files:**
- Create: `scripts/codex-worker/Private/GitHub.ps1`
- Create: `scripts/tests/CodexWorker.GitHub.Tests.ps1`

- [ ] **Step 1: Write failing GitHub adapter tests with a fake command runner**

Tests must prove that only `admin`, `maintain`, and `write` permissions pass; `triage`, `read`, missing, and malformed responses fail before state mutation. Also assert status transitions remove only `codex:*` state labels and retain the trigger label `codex` plus unrelated labels.

```powershell
It 'rejects a triage-only trigger actor' {
    $runner = { param($Arguments) '{"permission":"triage"}' }
    { Assert-TrustedGitHubActor -Repository 'owner/repo' -Actor 'reporter' -CommandRunner $runner } |
        Should Throw 'Actor reporter does not have write permission.'
}
```

- [ ] **Step 2: Run the GitHub tests and verify failure**

Expected: FAIL because the GitHub adapter functions are undefined.

- [ ] **Step 3: Implement the GitHub command boundary**

Implement one `Invoke-GhJson` function that accepts an argument array, invokes `gh.exe`, checks `$LASTEXITCODE`, and parses JSON. Build these functions on it:

- `Assert-TrustedGitHubActor`
- `Get-CodexIssueContext` using `gh issue view $IssueNumber --comments --json number,title,body,author,comments,labels,state,url`
- `Get-CodexIssueDevelopment` using `gh issue develop --list $IssueNumber` plus open PR lookup
- `Set-CodexIssueStatus`
- `Add-CodexIssueComment`
- `Get-CodexPullRequestContext` including review comments and changed files

Never construct a shell command string from issue content. Pass every value as a separate process argument.

- [ ] **Step 4: Add lifecycle-label constants**

Define exactly:

```powershell
$script:CodexStatusLabels = @(
    'codex:queued', 'codex:running', 'codex:pr-ready',
    'codex:blocked', 'codex:retry', 'codex:revise', 'codex:done'
)
```

Status updates remove the other status labels and add the requested one. They do not remove `codex`.

- [ ] **Step 5: Run GitHub tests and commit**

Expected: all GitHub adapter tests PASS.

```powershell
git add scripts/codex-worker/Private/GitHub.ps1 scripts/tests/CodexWorker.GitHub.Tests.ps1
git commit -m "feat: validate Codex GitHub issue triggers"
```

### Task 4: Add idempotent issue worktree management

**Files:**
- Create: `scripts/codex-worker/Private/Worktree.ps1`
- Create: `scripts/tests/CodexWorker.Worktree.Tests.ps1`

- [ ] **Step 1: Write failing slug, resume, and path-jail tests**

Test branch naming, existing local worktree reuse, remote-branch reconstruction, new worktree creation from `origin/master`, and rejection of a target outside `.worktrees`.

```powershell
It 'builds a bounded issue branch name' {
    Get-CodexIssueBranchName -IssueNumber 42 -Title 'Fix TIA / Status: Name!' |
        Should Be 'codex/42-fix-tia-status-name'
}

It 'rejects cleanup outside the automation worktree root' {
    { Assert-PathUnderRoot -Path 'C:\repo' -Root 'C:\repo\.worktrees' } |
        Should Throw 'Path is outside the automation worktree root.'
}
```

- [ ] **Step 2: Run worktree tests and verify failure**

Expected: FAIL because the worktree functions are undefined.

- [ ] **Step 3: Implement discovery and creation without touching the primary checkout**

Implement:

- `Get-CodexIssueBranchName`: lowercase, replace non-alphanumeric runs with `-`, trim, bound slug to 48 characters.
- `Get-RegisteredWorktrees`: parse `git worktree list --porcelain` records.
- `Get-OrCreateCodexIssueWorktree`: fetch `origin`, reuse a registered matching branch, reconstruct from `"origin/$BranchName"`, or create `-b $BranchName` from `origin/master`.
- `Assert-PathUnderRoot`: compare normalized paths with a trailing separator using `OrdinalIgnoreCase`.

Every Git invocation uses `git -C $RepositoryRoot` and argument arrays. The function must fail if the target directory exists but is not the registered worktree for the expected branch.

- [ ] **Step 4: Add dependency preparation with explicit commands**

Implement `Initialize-CodexIssueWorktree`:

```powershell
$solutionPath = Join-Path $Worktree 'AgentAssistPlcDev.sln'
$studioPath = Join-Path $Worktree 'studio'
$agentServicePath = Join-Path $Worktree 'agent-service'
$venvPath = Join-Path $agentServicePath '.venv'
dotnet restore $solutionPath
npm.cmd ci --prefix $studioPath
& $Config.bootstrapPython -m venv $venvPath
& (Join-Path $venvPath 'Scripts\python.exe') -m pip install -e "$agentServicePath[test]"
```

Skip an already valid Python venv only when its interpreter runs `import app_assistant, pytest` successfully. Skip `npm ci` only when `studio/node_modules/.package-lock.json` matches the checked-in lockfile metadata; otherwise run it. Capture setup output in the issue activity log without printing credentials.

- [ ] **Step 5: Add guarded cleanup checks**

`Test-CodexWorktreeCleanup` returns blockers for an unexpected path, an active process whose command line contains the path, dirty `git status --porcelain`, or commits in `$BranchName` not present in `"origin/$BranchName"`. `Remove-CodexWorktree` may call `git worktree remove` only when the blocker list is empty.

- [ ] **Step 6: Run worktree tests and commit**

```powershell
git add scripts/codex-worker/Private/Worktree.ps1 scripts/tests/CodexWorker.Worktree.Tests.ps1
git commit -m "feat: manage isolated Codex issue worktrees"
```

### Task 5: Add the Codex prompt, schema, process runner, and readable logs

**Files:**
- Create: `scripts/codex-worker/schemas/final-summary.schema.json`
- Create: `scripts/codex-worker/prompts/issue.md`
- Create: `scripts/codex-worker/prompts/revision.md`
- Create: `scripts/codex-worker/Private/Codex.ps1`
- Create: `scripts/tests/CodexWorker.Codex.Tests.ps1`

- [ ] **Step 1: Write failing prompt and summary-contract tests**

Assert that issue content is delimited as untrusted data, `AGENTS.md` remains authoritative, and the prompt prohibits commit, push, PR creation, merge, reset, clean, and access to other worktrees. Validate completed, blocked, and failed summaries against the checked-in schema; reject unknown properties.

- [ ] **Step 2: Define the final summary JSON Schema**

Use `additionalProperties: false` and require:

```json
{
  "status": "completed",
  "rootCauseOrApproach": "The implementation boundary and evidence.",
  "changedComponents": ["path or component"],
  "decisions": ["important decision"],
  "validation": [{"command":"command","outcome":"passed","details":"evidence"}],
  "warnings": [],
  "remainingRisks": [],
  "commitMessage": "fix: concise description (#42)",
  "prTitle": "fix: concise description (#42)",
  "requiresHumanInput": false,
  "humanQuestion": null
}
```

`status` is an enum of `completed`, `blocked`, and `failed`; validation outcome is `passed`, `failed`, or `skipped`; `humanQuestion` is string or null.

- [ ] **Step 3: Write complete prompt templates**

Both templates must instruct Codex to read the repository instructions, inspect current behavior, keep scope minimal, write regression tests, run focused then broader checks, perform runtime/browser validation when practical, review the diff, and return the required schema. The revision prompt additionally includes review comments and requires changing the existing branch without rewriting published history.

- [ ] **Step 4: Implement secret-scrubbed `codex exec`**

Use `System.Diagnostics.ProcessStartInfo` with `WorkingDirectory = $IssueWorktree`, redirected stdin/stdout/stderr, and `UseShellExecute = false`. Copy the current environment, then remove at least:

```text
GITHUB_TOKEN
GH_TOKEN
OPENAI_API_KEY
CODEX_API_KEY
DEEPSEEK_API_KEY
```

Build the initial-run argument array exactly as follows and write the prompt to redirected stdin:

```powershell
$arguments = @(
    'exec', '--json', '--sandbox', 'workspace-write',
    '--output-schema', $SchemaPath,
    '--output-last-message', $SummaryPath,
    '-'
)
```

During installation, run `codex exec resume --help` once and persist `supportsResumeOutputControls` in the generated config. When true, build the revision argument array as `@('exec', '--json', '--sandbox', 'workspace-write', '--output-schema', $SchemaPath, '--output-last-message', $SummaryPath, 'resume', $ThreadId, '-')`. When false, use the initial-run array with a complete revision prompt and record `resume output controls unavailable; started fresh revision thread` in `activity.log`. Capture the `thread.started` ID from JSONL in durable issue state.

- [ ] **Step 5: Convert JSONL to live readable activity**

For every stdout line, append the untouched line to `events.jsonl`, parse known event types, and emit one timestamped readable line to both `activity.log` and the Actions console. Log command text, exit status, file-change paths, agent messages, errors, and turn completion. Unknown valid JSON events are retained in raw JSONL and summarized as `event $($event.type)`.

- [ ] **Step 6: Add timeout and exit classification**

Kill the Codex process tree after `codexTimeoutMinutes`. Classify missing executable/authentication, timeout, malformed summary, and nonzero exit. Only network/service-unavailable classifications are transient; source/test failures are not wrapper retries.

- [ ] **Step 7: Run Codex unit tests and commit**

Use a fake executable script that emits fixed JSONL and summary files; do not call the real Codex service in unit tests.

```powershell
git add scripts/codex-worker/Private/Codex.ps1 scripts/codex-worker/prompts scripts/codex-worker/schemas scripts/tests/CodexWorker.Codex.Tests.ps1
git commit -m "feat: run Codex with structured local logs"
```

### Task 6: Orchestrate initial, dry-run, retry, and blocked issue runs

**Files:**
- Create: `scripts/codex-worker/Invoke-Issue.ps1`
- Modify: `scripts/codex-worker/Private/State.ps1`
- Modify: `scripts/codex-worker/Private/GitHub.ps1`
- Modify: `scripts/tests/CodexWorker.State.Tests.ps1`
- Modify: `scripts/tests/CodexWorker.GitHub.Tests.ps1`

- [ ] **Step 1: Write failing lifecycle tests**

Test `queued -> running -> pr-ready`, a human-input summary becoming `blocked`, one transient retry, a second transient failure becoming blocked, and `-DryRun` reading context without creating a branch/worktree or invoking Codex with write access.

- [ ] **Step 2: Implement the entry-point contract**

`Invoke-Issue.ps1` accepts:

```powershell
param(
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][int]$IssueNumber,
    [Parameter(Mandatory)][string]$Actor,
    [Parameter(Mandatory)][string]$EventName,
    [string]$RepositoryRoot,
    [string]$DataRoot,
    [switch]$DryRun
)
```

It imports the module, resolves config, verifies actor permission, acquires the lock, reads issue/existing-development context, and dispatches either a dry-run report, publication recovery, or implementation run.

- [ ] **Step 3: Implement durable issue attempt state**

Persist `issueNumber`, `status`, `attempt`, `branch`, `worktree`, `threadId`, `runDirectory`, `commit`, `prUrl`, `retryCount`, `publicationStage`, and `lastError`. Save state after every externally visible transition.

- [ ] **Step 4: Add milestone comments without flooding**

Post only: claimed, approach established if present in a completed summary, validation result, blocked question/error, and PR ready. Include workflow run URL from `GITHUB_SERVER_URL`, `GITHUB_REPOSITORY`, and `GITHUB_RUN_ID` when present.

- [ ] **Step 5: Run lifecycle tests**

Expected: all lifecycle tests PASS with fake GitHub, Git, setup, and Codex boundaries.

- [ ] **Step 6: Commit**

```powershell
git add scripts/codex-worker/Invoke-Issue.ps1 scripts/codex-worker/Private/State.ps1 scripts/codex-worker/Private/GitHub.ps1 scripts/tests/CodexWorker.State.Tests.ps1 scripts/tests/CodexWorker.GitHub.Tests.ps1
git commit -m "feat: orchestrate Codex issue runs"
```

### Task 7: Publish commits and draft PRs, then resume revisions

**Files:**
- Create: `scripts/codex-worker/Private/Publish.ps1`
- Create: `scripts/codex-worker/Invoke-Revision.ps1`
- Create: `scripts/tests/CodexWorker.Publish.Tests.ps1`
- Modify: `scripts/codex-worker/Private/GitHub.ps1`
- Modify: `scripts/codex-worker/Private/Codex.ps1`

- [ ] **Step 1: Write failing publication-safety tests**

Cover empty diffs, `git diff --check` failure, suspicious credential files, completed summaries with failed validation, commit success/push failure, push success/PR failure, idempotent retry, and reuse of the existing PR during revision.

- [ ] **Step 2: Implement pre-publication review**

`Test-CodexPublication` must require `status = completed`, `requiresHumanInput = false`, a nonempty worktree diff, no conflict markers/whitespace errors, and no changed path matching `.env`, `auth.json`, private keys, or the durable data root. Failed or skipped tests remain visible in the PR risks; an explicitly failed required validation blocks publication.

- [ ] **Step 3: Implement commit and push recovery stages**

Stage only the issue worktree with `git -C $Worktree add -A`. Normalize the model-suggested commit title to the repository format and ensure it ends with `"(#$IssueNumber)"`. Record `committed`, then `pushed`, then `pr-created` after each successful action so retry resumes from the last stage.

- [ ] **Step 4: Render the required PR body deterministically**

Generate these headings from the structured summary rather than accepting free-form PR Markdown:

```markdown
## Summary
## Problem
## Root Cause / Design
## Changes
## Validation
## Risks
## Issue
```

Use `"Fixes #$IssueNumber"` only for completed issue implementations. Create the PR with `gh pr create --draft --base master --head $BranchName --body-file $BodyPath` or update the existing PR. Never mark it ready or merge it.

- [ ] **Step 5: Implement revision intake**

`Invoke-Revision.ps1` verifies the actor, resolves the existing PR/issue/branch/worktree, preserves user edits, fetches review comments, invokes the revision prompt, validates the new diff, makes a new commit, pushes without force, and comments the new validation evidence. It creates no new PR.

- [ ] **Step 6: Run publication tests and commit**

```powershell
git add scripts/codex-worker/Private/Publish.ps1 scripts/codex-worker/Invoke-Revision.ps1 scripts/codex-worker/Private/GitHub.ps1 scripts/codex-worker/Private/Codex.ps1 scripts/tests/CodexWorker.Publish.Tests.ps1
git commit -m "feat: publish Codex changes as draft PRs"
```

### Task 8: Add trusted GitHub Actions workflows

**Files:**
- Create: `.github/workflows/codex-issue.yml`
- Create: `.github/workflows/codex-revise.yml`
- Create: `.github/workflows/codex-pr-closed.yml`
- Create: `scripts/tests/CodexWorker.Workflows.Tests.ps1`

- [ ] **Step 1: Write failing static workflow-contract tests**

Read the YAML as text and assert exact triggers, the unique `agentassist-local` runner label, absence of a workflow `concurrency` block, trusted default-branch checkout, explicit permissions, and the PowerShell entry script. The missing concurrency block is intentional: GitHub concurrency groups retain at most one pending run and can replace older pending work, while the one dedicated runner plus the local lock preserves every queued event. Also assert the issue workflow filters to `codex` or `codex:retry`, revision filters to `codex:revise`, and PR-close checks `github.event.pull_request.merged` before deployment handoff.

- [ ] **Step 2: Add the issue workflow**

Use:

```yaml
name: Codex local issue worker
on:
  issues:
    types: [labeled]
  workflow_dispatch:
    inputs:
      issue_number:
        required: true
        type: number
      dry_run:
        required: false
        type: boolean
        default: false
permissions:
  contents: write
  issues: write
  pull-requests: write
jobs:
  run:
    if: github.event_name == 'workflow_dispatch' || github.event.label.name == 'codex' || github.event.label.name == 'codex:retry'
    runs-on: [self-hosted, Windows, X64, agentassist-local]
```

Checkout `refs/heads/${{ github.event.repository.default_branch }}` with credentials disabled, then call `Invoke-Issue.ps1`. Pass event values through environment variables and typed PowerShell parameters; never interpolate issue body text into the shell.

- [ ] **Step 3: Add revision and PR-close workflows**

Revision accepts `issues:labeled` and `pull_request:labeled`, filters `codex:revise`, and calls `Invoke-Revision.ps1`. PR-close accepts `pull_request:closed`, uses read/write permissions needed for comments and cleanup, passes `merged`, merge SHA, head branch, and PR number to `Register-PrClosed.ps1`, and never invokes deployment for `merged = false`.

- [ ] **Step 4: Run workflow tests**

Expected: all static workflow contracts PASS. After the branch is pushed, GitHub must accept all three workflow files without syntax errors.

- [ ] **Step 5: Commit**

```powershell
git add .github/workflows scripts/tests/CodexWorker.Workflows.Tests.ps1
git commit -m "ci: dispatch GitHub issues to local Codex"
```

### Task 9: Automate local prerequisite and runner setup

**Files:**
- Create: `scripts/codex-worker/Install-LocalWorker.ps1`
- Create: `scripts/codex-worker/Start-GitHubRunner.ps1`
- Create: `scripts/tests/CodexWorker.Install.Tests.ps1`

- [ ] **Step 1: Write failing setup-plan tests**

With `-WhatIf` and fake command/download runners, assert setup checks PowerShell 7, Git, `gh`, .NET 8, Node/npm, bootstrap Python, and Codex; installs Codex only when missing; creates labels; configures one non-service runner; writes config outside Git; and registers interactive logon tasks.

- [ ] **Step 2: Implement Codex CLI installation and authentication gate**

When `codex` is missing, run:

```powershell
npm.cmd install --global @openai/codex
```

Then require the user to complete `codex login` in an interactive console. Verify authentication with a read-only smoke command from a temporary Git repository:

```powershell
codex exec --ephemeral --sandbox read-only "Reply exactly READY"
```

Do not store API keys in config, workflow variables, or repository files.

- [ ] **Step 3: Install the latest Windows x64 GitHub runner safely**

Use `gh api repos/actions/runner/releases/latest` to select the Windows x64 zip and its SHA-256 value from the release body, download the zip into a temporary directory, verify `Get-FileHash`, then extract to `%LOCALAPPDATA%\AutomationWorkbench\CodexWorker\runner`. Obtain a short-lived registration token through `gh api --method POST "repos/$($Config.repository)/actions/runners/registration-token"`, and run `config.cmd --unattended --replace --labels agentassist-local --work _work`. Do not configure the runner as a Windows service.

- [ ] **Step 4: Register interactive logon tasks**

Create `AutomationWorkbenchCodexRunner` to start `Start-GitHubRunner.ps1` and `AutomationWorkbenchCodexDeploymentNotifier` to start PowerShell 7 with `-Sta -File Invoke-DeploymentNotifier.ps1 -Watch`. Both run only when the current user is logged on, use hidden worker consoles, and restart after failure. The notification window itself remains visible.

- [ ] **Step 5: Create labels and repository variables**

Create/update all lifecycle labels with `gh label create --force`. Set `CODEX_LOCAL_REPOSITORY` and `CODEX_WORKER_DATA_ROOT` as repository Actions variables. Verify `gh auth status` and runner registration before starting tasks.

- [ ] **Step 6: Run setup tests and commit**

```powershell
git add scripts/codex-worker/Install-LocalWorker.ps1 scripts/codex-worker/Start-GitHubRunner.ps1 scripts/tests/CodexWorker.Install.Tests.ps1
git commit -m "feat: install the local Codex Actions runner"
```

### Task 10: Add PR-close cleanup and pending deployment handoff

**Files:**
- Create: `scripts/codex-worker/Register-PrClosed.ps1`
- Create: `scripts/codex-worker/Private/Deployment.ps1`
- Create: `scripts/tests/CodexWorker.Deployment.Tests.ps1`
- Modify: `scripts/codex-worker/Private/Worktree.ps1`

- [ ] **Step 1: Write failing merge, close, coalescing, and cleanup tests**

Prove an unmerged close performs guarded cleanup but creates no deployment; a merge records the exact SHA; two pending merges coalesce to the newer commit reachable from `origin/master`; and dirty, busy, outside-root, or unpushed worktrees are preserved with blockers.

- [ ] **Step 2: Implement PR-close registration**

`Register-PrClosed.ps1` accepts repository, PR number, issue number, head branch, merged boolean, merge SHA, repository root, and data root. It acquires the lock, resolves saved issue state, runs cleanup guards, updates `codex:done` only for a merged PR, and records cleanup blockers in a PR/issue comment.

- [ ] **Step 3: Implement pending deployment coalescing**

For merged PRs, fetch `origin/master`, verify the SHA is an ancestor, and write:

```json
{
  "targetCommit": "full sha",
  "sourcePr": 123,
  "requestedAt": "UTC ISO-8601",
  "snoozeUntil": null,
  "status": "pending"
}
```

If a deployment is already pending or snoozed, replace `targetCommit` only with the newer verified `origin/master` commit and retain a later existing `snoozeUntil`.

- [ ] **Step 4: Run deployment handoff tests and commit**

```powershell
git add scripts/codex-worker/Register-PrClosed.ps1 scripts/codex-worker/Private/Deployment.ps1 scripts/codex-worker/Private/Worktree.ps1 scripts/tests/CodexWorker.Deployment.Tests.ps1
git commit -m "feat: register merged Codex deployments"
```

### Task 11: Add the visible countdown, five-minute snooze, and cancellation

**Files:**
- Create: `scripts/codex-worker/Private/Notification.ps1`
- Create: `scripts/codex-worker/Invoke-DeploymentNotifier.ps1`
- Create: `scripts/tests/CodexWorker.Notification.Tests.ps1`

- [ ] **Step 1: Write failing notifier state-machine tests**

Inject a fake clock, session probe, lock provider, dialog provider, and deploy action. Assert:

- no dialog/countdown without an interactive unlocked session;
- no dialog while the worker lock is busy;
- no response returns `Deploy` after ten seconds;
- **Later** writes `snoozeUntil = now + 5 minutes`, releases the lock, and performs no deployment;
- **Cancel** clears pending deployment;
- closing the window behaves as **Later**;
- a new merge during snooze retains the snooze and updates the target commit.

- [ ] **Step 2: Implement the session-availability probe**

Return available only when `[Environment]::UserInteractive` is true, the current process has a nonzero session ID, `explorer.exe` exists in that session, and `LogonUI.exe` does not indicate the session is locked. Keep the probe injectable because CI tests cannot depend on a real desktop.

- [ ] **Step 3: Implement the WPF dialog**

Create a topmost WPF window with this message and two buttons:

```text
Automation Workbench will rebuild in 10 seconds.
[Later (5 min)] [Cancel]
```

Use a one-second `DispatcherTimer` to update the countdown. Return `Deploy` at zero, `Later` from the later button or window close, and `Cancel` from cancel. Do not begin the timer before the `ContentRendered` event.

- [ ] **Step 4: Implement the watch loop**

`Invoke-DeploymentNotifier.ps1 -Watch` checks durable state every five seconds. If pending and due, it attempts the worker lock without blocking; if unavailable it retries. It shows the dialog only when the session probe passes. **Later** saves the five-minute snooze and releases the lock. **Cancel** clears state. **Deploy** keeps the lock through deployment.

- [ ] **Step 5: Run notification tests and commit**

Run unit tests without displaying a real window by injecting the dialog provider.

```powershell
git add scripts/codex-worker/Private/Notification.ps1 scripts/codex-worker/Invoke-DeploymentNotifier.ps1 scripts/tests/CodexWorker.Notification.Tests.ps1
git commit -m "feat: confirm local rebuilds with a countdown"
```

### Task 12: Add A/B runtime deployment, health checks, and rollback

**Files:**
- Modify: `scripts/codex-worker/Private/Deployment.ps1`
- Modify: `scripts/tests/CodexWorker.Deployment.Tests.ps1`
- Modify: `launch.ps1`
- Create: `scripts/tests/Launch.Tests.ps1`

- [ ] **Step 1: Write failing launcher and slot tests**

Assert `launch.ps1 -NoBuild` includes `--no-build` in the `dotnet run` arguments. Test slot selection, exact-SHA verification, path jailing, inactive-slot recreation, build-before-switch ordering, three health probes, activation only after health, and rollback to the unchanged prior slot.

- [ ] **Step 2: Make `launch.ps1 -NoBuild` truly skip ApiHost build**

Build the ApiHost argument list before `Start-Process`:

```powershell
$apiArguments = @('run')
if ($NoBuild) {
    $apiArguments += '--no-build'
}
$apiArguments += @('--project', $apiProject, '--', 'Application:OpenBrowserOnStart=false')
```

Pass `$apiArguments` to `Start-Process`. Preserve all existing kill, sidecar, Studio, and checkpoint behavior.

- [ ] **Step 3: Implement exact-commit A/B slot preparation**

Use `.worktrees/runtime-a` and `.worktrees/runtime-b` as detached worktrees. Select the inactive slot from durable `activeSlot`. Before removal, verify its resolved path is exactly one configured runtime slot and no process command line references it. Recreate only the inactive slot at the verified target SHA.

- [ ] **Step 4: Prepare dependencies and prebuild without stopping the current app**

Run, in order, from the inactive slot:

```powershell
dotnet restore AgentAssistPlcDev.sln
dotnet build AgentAssistPlcDev.sln -v q
npm.cmd ci --prefix studio
npm.cmd run build --prefix studio
& $Config.bootstrapPython -m venv agent-service\.venv
agent-service\.venv\Scripts\python.exe -m pip install -e "agent-service[test]"
```

If the generated TIA whitelist `.reg` exists, import it before switching. Any preparation failure leaves the current services running and the deployment pending as failed with logs.

- [ ] **Step 5: Switch through the canonical launcher and verify health**

Run `powershell.exe -ExecutionPolicy Bypass -File (Join-Path $InactiveSlot 'launch.ps1') -NoBuild`. Then poll until timeout:

- `http://localhost:5173/` returns HTTP 200;
- `http://localhost:5239/api/status` returns HTTP 200;
- `http://localhost:8787/health` returns HTTP 200 and JSON `status = ok`.

Record target SHA, slot, launcher exit, process IDs/commands, endpoint status, and sidecar model/fallback fields.

- [ ] **Step 6: Implement rollback**

On failed launch or health, invoke the previous slot's `launch.ps1 -NoBuild`, run the same health probes, and retain the previous `activeSlot`. Record both the failed target evidence and rollback evidence. If rollback also fails, mark `rollback-failed`, preserve both slot logs, and surface a high-priority GitHub issue comment.

- [ ] **Step 7: Run launcher and deployment unit tests**

Use fake command and HTTP runners; unit tests must not kill real processes or bind ports.

- [ ] **Step 8: Commit**

```powershell
git add launch.ps1 scripts/codex-worker/Private/Deployment.ps1 scripts/tests/CodexWorker.Deployment.Tests.ps1 scripts/tests/Launch.Tests.ps1
git commit -m "feat: deploy merged builds with local rollback"
```

### Task 13: Document operations and complete staged validation

**Files:**
- Create: `docs/local-codex-worker.md`
- Modify: `README.md`
- Modify: `AGENTS.md`

- [ ] **Step 1: Write the operator guide**

Document prerequisites, trust model, installation, ChatGPT sign-in, runner lifecycle, label meanings, monitoring commands, log/state locations, dry run, retry, revision, cleanup blockers, deployment countdown, five-minute snooze, cancellation, runtime slots, rollback, uninstall, and disaster recovery. Include:

```powershell
Get-Content "$env:LOCALAPPDATA\AutomationWorkbench\CodexWorker\runs\issue-42\1\activity.log" -Wait
gh run list --workflow "Codex local issue worker"
$RunId = gh run list --workflow "Codex local issue worker" --limit 1 --json databaseId --jq '.[0].databaseId'
gh run watch $RunId --log
```

State explicitly that workflow files must be merged into the default branch before issue-label events can run.

- [ ] **Step 2: Update repository instructions**

Add a short `AGENTS.md` automation section requiring the same issue scope, worktree, validation, draft-PR, no-merge, and preserved-primary-checkout rules for unattended runs. Link the detailed operator guide from `README.md`; do not duplicate it.

- [ ] **Step 3: Commit documentation updates**

```powershell
git add docs/local-codex-worker.md README.md AGENTS.md
git commit -m "docs: document local Codex issue automation"
```

- [ ] **Step 4: Run all worker and regression checks**

Run:

```powershell
powershell.exe -NoProfile -File scripts/codex-worker/Test-CodexWorker.ps1
git diff --check
dotnet build AgentAssistPlcDev.sln -v q
dotnet test AgentAssistPlcDev.sln --no-build -v q
Push-Location studio
npm test -- --run
npm run lint
npm run build
Pop-Location
Push-Location agent-service
.\.venv\Scripts\python.exe -m pytest -q
Pop-Location
```

Expected: all worker tests, existing .NET tests, frontend tests/lint/build, and Python tests pass. Repeated frontend `ECONNREFUSED localhost:3000` text is environmental noise only if the command exits successfully with all tests passed.

- [ ] **Step 5: Perform a local dry run before enabling labels**

Use a real trusted issue number but pass `-DryRun`. Verify issue retrieval, trust check, plan output, Actions/local logs, and zero new branch/worktree/commit/PR. Confirm the primary checkout still contains exactly its pre-existing user changes.

- [ ] **Step 6: Push and create a draft implementation PR**

Review `git status`, `git diff`, `git diff --stat`, and `git log --oneline master..HEAD`. Push the implementation branch and create a draft PR with the required repository sections. Do not merge it.

- [ ] **Step 7: After human merge, install and verify the local runner**

From the merged default branch, run `Install-LocalWorker.ps1`, complete `codex login`, verify the runner is online with the `agentassist-local` label, and confirm both logon tasks run in the interactive user session.

- [ ] **Step 8: Run a small pilot issue end to end**

Create or select a low-risk issue, apply `codex`, and verify immediate dispatch, one active lock, issue worktree creation, live logs, focused/broad validation, draft PR, parked worktree, and processing of the next queued issue. Apply `codex:revise` and verify the same worktree/branch/PR receives a new non-force-pushed commit.

- [ ] **Step 9: Exercise deployment decisions and rollback**

Merge the pilot PR. Verify the visible ten-second countdown starts only when idle and unlocked. Choose **Later**, verify issue work can proceed and the prompt returns after five minutes, then allow deployment. Confirm the exact merged SHA and all three health checks. Finally inject a disposable health failure and prove the previous slot is relaunched.

## Final handoff checklist

- [ ] All design acceptance criteria map to a completed task above.
- [ ] The primary checkout and its unrelated modifications were never changed.
- [ ] No secret appears in Git history, logs, Actions artifacts, issue comments, or Codex environment.
- [ ] One lock serializes issue, revision, cleanup, and deployment mutation.
- [ ] Existing PR worktrees remain parked until guarded cleanup.
- [ ] Workflows execute trusted default-branch automation code.
- [ ] Every published change is a draft PR; no automated merge exists.
- [ ] `Later` is exactly five minutes; `Cancel` clears only the current pending deployment.
- [ ] The countdown is never started invisibly.
- [ ] Deployment uses the exact verified `origin/master` commit and A/B detached runtime slots.
- [ ] Failed preparation preserves the current app; failed switch proves rollback or reports rollback failure.
- [ ] Service evidence covers ports 5173, 5239, and 8787.
