# Kimi GitHub Issue Worker Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add Kimi CLI as an explicitly selected second issue-worker provider, triggered only by the `kimi` GitHub label, while retaining the existing Codex workflow unchanged.

**Architecture:** Keep worktree creation, trusted GitHub access, validation, commit, push, draft-PR publication, cleanup, deployment handoff, logs, and console milestones in the existing PowerShell wrapper. Introduce a provider boundary that dispatches to the existing Codex adapter or a new Kimi adapter. Kimi runs non-interactively with `kimi --auto --prompt ... --output-format stream-json`; its output is converted into the existing validated final-summary contract before publication proceeds.

**Tech Stack:** GitHub Actions YAML, Windows PowerShell 5.1/PowerShell 7, GitHub CLI, Codex CLI, Kimi Code CLI 0.31+, Pester 3.4-compatible tests.

---

## Label contract

| Provider | Initial trigger | Lifecycle labels | Revision trigger |
| --- | --- | --- | --- |
| Codex | `codex` | existing `codex:*` labels | `codex:revise` |
| Kimi | `kimi` | `kimi-queued`, `kimi-running`, `kimi-retry`, `kimi-ready`, `kimi-blocked`, `kimi-done` | `kimi-revise` |

`kimi` is the only Kimi initial trigger. Kimi must never be selected as a fallback after a Codex failure and must never run from an unlabeled issue. `kimi-ready` means the wrapper has accepted Kimi's validated final summary and has created or updated one draft PR.

## Files and responsibilities

- Modify: `.github/workflows/codex-issue.yml` — accept explicit Kimi issue and retry labels; pass a provider input to the trusted entry point.
- Modify: `.github/workflows/codex-revise.yml` — accept `kimi-revise` from an issue or PR; pass the resolved provider.
- Modify: `scripts/codex-worker/Invoke-Issue.ps1` and `scripts/codex-worker/Invoke-Revision.ps1` — validate and forward `Codex` or `Kimi` provider selection.
- Create: `scripts/codex-worker/Private/Agent.ps1` — provider-neutral selection, validation, and lifecycle-label mapping.
- Create: `scripts/codex-worker/Private/Kimi.ps1` — Kimi process invocation, JSONL parsing, transcript logging, and canonical final-summary extraction.
- Modify: `scripts/codex-worker/Private/State.ps1`, `Private/Publish.ps1`, `Private/GitHub.ps1`, and `Private/Deployment.ps1` — persist provider identity and use provider-specific lifecycle labels through initial, revision, close, and completion paths.
- Modify: `scripts/codex-worker/Private/Install.ps1` and `config.example.json` — validate optional Kimi configuration, create Kimi labels, and run Kimi preflight only when Kimi is enabled.
- Create: `scripts/codex-worker/prompts/kimi-issue.md` and `scripts/codex-worker/prompts/kimi-revision.md` — bounded prompts that preserve wrapper ownership of Git/GitHub publication and require the canonical JSON summary.
- Modify: `docs/local-codex-worker.md` — explain Kimi prerequisites, labels, monitoring, and recovery.
- Modify/Create tests under `scripts/tests/CodexWorker.*.Tests.ps1` — cover provider selection, Kimi JSONL conversion, label transitions, workflow routing, installation checks, and no-fallback behavior.

### Task 1: Define provider and label primitives

**Files:**
- Create: `scripts/codex-worker/Private/Agent.ps1`
- Modify: `scripts/codex-worker/CodexWorker.psd1`
- Test: `scripts/tests/CodexWorker.Agent.Tests.ps1`

- [ ] **Step 1: Write failing provider-selection tests**

```powershell
Describe 'worker provider selection' {
    BeforeEach { Import-Module (Join-Path $PSScriptRoot '..\codex-worker\CodexWorker.psd1') -Force }

    It 'maps the explicit kimi trigger to Kimi lifecycle labels' {
        $provider = Resolve-CodexWorkerProvider -Provider 'Kimi' -EventName 'kimi'
        $provider.Name | Should Be 'Kimi'
        $provider.TriggerLabel | Should Be 'kimi'
        $provider.StatusLabels.'pr-ready' | Should Be 'kimi-ready'
    }

    It 'refuses an implicit Kimi selection' {
        { Resolve-CodexWorkerProvider -Provider '' -EventName 'kimi' } | Should Throw
        { Resolve-CodexWorkerProvider -Provider 'Kimi' -EventName 'codex:retry' } | Should Throw
    }
}
```

- [ ] **Step 2: Run the new test and confirm it fails because the provider boundary does not exist**

Run:

```powershell
Invoke-Pester .\scripts\tests\CodexWorker.Agent.Tests.ps1
```

Expected: a failure stating that `Resolve-CodexWorkerProvider` is not recognized.

- [ ] **Step 3: Implement the provider contract**

Create `Private/Agent.ps1` with a single source of truth for provider names, trigger labels, revision labels, and statuses:

```powershell
$script:CodexWorkerProviders = [ordered]@{
    Codex = [pscustomobject]@{
        Name = 'Codex'; TriggerLabel = 'codex'; RevisionLabel = 'codex:revise'
        StatusLabels = @{ queued='codex:queued'; running='codex:running'; retry='codex:retry'; 'pr-ready'='codex:pr-ready'; blocked='codex:blocked'; done='codex:done' }
    }
    Kimi = [pscustomobject]@{
        Name = 'Kimi'; TriggerLabel = 'kimi'; RevisionLabel = 'kimi-revise'
        StatusLabels = @{ queued='kimi-queued'; running='kimi-running'; retry='kimi-retry'; 'pr-ready'='kimi-ready'; blocked='kimi-blocked'; done='kimi-done' }
    }
}

function Resolve-CodexWorkerProvider {
    param([string] $Provider, [Parameter(Mandatory=$true)][string] $EventName)
    if ([string]::IsNullOrWhiteSpace($Provider)) {
        if ($EventName -match '^codex(?::|$)') { $Provider = 'Codex' }
        else { throw "A provider is required for event '$EventName'." }
    }
    $selected = $script:CodexWorkerProviders[$Provider]
    if ($null -eq $selected) { throw "Unsupported worker provider '$Provider'." }
    if ($EventName -notmatch ('^' + [regex]::Escape($selected.TriggerLabel) + '($|[-:])')) {
        throw "Event '$EventName' is not valid for provider '$Provider'."
    }
    return $selected
}
```

Export only the provider resolver from `CodexWorker.psd1`; retain existing public function names for backward compatibility.

- [ ] **Step 4: Run the provider test and existing GitHub label tests**

Run:

```powershell
Invoke-Pester .\scripts\tests\CodexWorker.Agent.Tests.ps1
Invoke-Pester .\scripts\tests\CodexWorker.GitHub.Tests.ps1
```

Expected: both pass with no changed Codex label behavior.

- [ ] **Step 5: Commit the provider primitives**

```powershell
git add -- scripts/codex-worker/Private/Agent.ps1 scripts/codex-worker/CodexWorker.psd1 scripts/tests/CodexWorker.Agent.Tests.ps1
git commit -m "feat: define Kimi worker provider labels"
```

### Task 2: Add the Kimi adapter and canonical summary conversion

**Files:**
- Create: `scripts/codex-worker/Private/Kimi.ps1`
- Create: `scripts/codex-worker/prompts/kimi-issue.md`
- Create: `scripts/codex-worker/prompts/kimi-revision.md`
- Modify: `scripts/codex-worker/Private/Agent.ps1`
- Test: `scripts/tests/CodexWorker.Kimi.Tests.ps1`

- [ ] **Step 1: Write failing Kimi adapter tests using a local fake executable**

```powershell
It 'converts Kimi stream-json final output into the worker summary contract' {
    $result = Invoke-KimiRun -IssueWorktree $TestDrive -IssueContext ([pscustomobject]@{ number=71; title='Kimi pilot'; body='body' }) `
        -Config ([pscustomobject]@{ kimiCommand=$fakeKimi; kimiTimeoutMinutes=5 }) -RunDirectory $run -StatePath $state

    $result.Classification | Should Be 'completed'
    $result.Status | Should Be 'completed'
    $result.Summary.prTitle | Should Be 'fix: Kimi pilot'
    (Get-Content -Raw (Join-Path $run 'events.jsonl')) | Should Match 'assistant'
}

It 'blocks malformed Kimi final output before publication' {
    $result = Invoke-KimiRun -IssueWorktree $TestDrive -IssueContext $issue -Config $config -RunDirectory $run -StatePath $state
    $result.Classification | Should Be 'malformed_summary'
    $result.Status | Should Be 'failed'
}
```

The fake executable must emit `stream-json` lines containing a final assistant message whose only fenced block is the required JSON object; the malformed fixture must omit `commitMessage`.

- [ ] **Step 2: Run the Kimi tests and confirm they fail**

Run:

```powershell
Invoke-Pester .\scripts\tests\CodexWorker.Kimi.Tests.ps1
```

Expected: failure because `Invoke-KimiRun` does not exist.

- [ ] **Step 3: Implement non-interactive Kimi execution**

`Invoke-KimiRun` must use the existing safe process-launcher conventions: explicit argument array, working directory set to the issue worktree, bounded timeout, stdout/stderr capture, secret redaction, and `activity.log` updates. Its process arguments are:

```powershell
$arguments = @('--auto', '--prompt', $promptText, '--output-format', 'stream-json')
if (-not [string]::IsNullOrWhiteSpace($Config.kimiModel)) { $arguments += @('--model', [string]$Config.kimiModel) }
```

Parse each stdout JSONL line. Preserve it in `events.jsonl`; emit concise readable events using the same `Write-CodexReadableLine` convention. Collect the final assistant text, extract exactly one JSON object or fenced `json` block, and require `Test-CodexSummary` to accept it. Return the existing shape:

```powershell
[pscustomobject]@{ Status=$status; Classification=$classification; ThreadId=$null; Summary=$summary; EventsPath=$EventsPath; ActivityLogPath=$ActivityLogPath }
```

Do not use `--session` in the first Kimi release. Revisions receive the issue, PR review comments, and preserved-user-edit snapshot in a fresh prompt; this avoids relying on undocumented session-plus-prompt behavior.

Kimi prompts must include the same untrusted-content delimiters and wrapper boundary as Codex prompts: edit only the supplied worktree; do not run GitHub CLI; do not commit, push, open PRs, merge, reset, clean, or alter other worktrees; final response must be the exact canonical JSON summary.

- [ ] **Step 4: Add adapter dispatch without changing Codex execution**

Add a provider-neutral dispatcher in `Private/Agent.ps1`:

```powershell
function Invoke-CodexWorkerAgentRun {
    param([object]$Provider, [hashtable]$RunParameters)
    if ($Provider.Name -eq 'Codex') { return Invoke-CodexRun @RunParameters }
    if ($Provider.Name -eq 'Kimi') { return Invoke-KimiRun @RunParameters }
    throw "Unsupported worker provider '$($Provider.Name)'."
}
```

Change only the two call sites in `State.ps1` and `Publish.ps1` to call this dispatcher. Preserve `Invoke-CodexRun` unchanged and continue passing `-Revision` and review text for Codex.

- [ ] **Step 5: Run adapter and Codex regression tests**

Run:

```powershell
Invoke-Pester .\scripts\tests\CodexWorker.Kimi.Tests.ps1
Invoke-Pester .\scripts\tests\CodexWorker.Codex.Tests.ps1
```

Expected: Kimi tests pass. Report any known Pester 3 `Should Throw` baseline failures separately; do not treat them as Kimi failures.

- [ ] **Step 6: Commit the Kimi adapter**

```powershell
git add -- scripts/codex-worker/Private/Agent.ps1 scripts/codex-worker/Private/Kimi.ps1 scripts/codex-worker/prompts/kimi-issue.md scripts/codex-worker/prompts/kimi-revision.md scripts/codex-worker/Private/State.ps1 scripts/codex-worker/Private/Publish.ps1 scripts/tests/CodexWorker.Kimi.Tests.ps1
git commit -m "feat: add Kimi issue worker adapter"
```

### Task 3: Make state, labels, and publication provider-aware

**Files:**
- Modify: `scripts/codex-worker/Private/GitHub.ps1`
- Modify: `scripts/codex-worker/Private/State.ps1`
- Modify: `scripts/codex-worker/Private/Publish.ps1`
- Modify: `scripts/codex-worker/Private/Deployment.ps1`
- Test: `scripts/tests/CodexWorker.GitHub.Tests.ps1`
- Test: `scripts/tests/CodexWorker.State.Tests.ps1`
- Test: `scripts/tests/CodexWorker.Publish.Tests.ps1`
- Test: `scripts/tests/CodexWorker.Deployment.Tests.ps1`

- [ ] **Step 1: Add failing lifecycle tests for a Kimi issue**

```powershell
It 'moves a Kimi issue from kimi to kimi-ready without altering Codex labels' {
    Set-CodexIssueStatus -Repository 'owner/repo' -IssueNumber 71 -Status 'pr-ready' -Provider $kimi -CurrentLabels @('kimi','kimi-running','codex:done') -CommandRunner $github
    $events | Should Contain 'add:kimi-ready'
    $events | Should Contain 'remove:kimi-running'
    $events | Should Not Contain 'remove:codex:done'
}

It 'persists Kimi provider identity through merged completion' {
    $attempt.agentProvider | Should Be 'Kimi'
    $issueLabels | Should Contain 'kimi-done'
}
```

- [ ] **Step 2: Run the focused tests and confirm Kimi behavior is missing**

Run:

```powershell
Invoke-Pester .\scripts\tests\CodexWorker.GitHub.Tests.ps1
Invoke-Pester .\scripts\tests\CodexWorker.State.Tests.ps1
```

Expected: failures for unsupported Kimi status/labels.

- [ ] **Step 3: Implement provider-aware status transitions**

Add `agentProvider` to every new attempt state, defaulting missing legacy state to `Codex`. Pass a resolved provider object into `Set-CodexIssueStatus`, and remove only status labels belonging to that provider. Do not remove `codex`, `kimi`, or unrelated labels.

Use the provider mapping instead of hard-coded status text in all initial-run, retry, blocked, publication-ready, revision, merged-close, and idempotent recovery paths. Keep durable status values (`queued`, `running`, `pr-ready`, `blocked`, `done`) provider-neutral; use provider identity only to select visible labels and adapter behavior.

- [ ] **Step 4: Make publication and close output provider-specific**

Use the existing console milestone format, adding the provider name to details:

```text
CODEX WORKER | Issue #71 | KIMI AGENT STARTED | Attempt 1
CODEX WORKER | Issue #71 | KIMI READY | PR #72: https://github.com/owner/repo/pull/72
```

The close handler must read `agentProvider` from durable state before applying `codex:done` or `kimi-done`. For legacy attempts with no field, select Codex.

- [ ] **Step 5: Run lifecycle and close-handler regression tests**

Run:

```powershell
Invoke-Pester .\scripts\tests\CodexWorker.GitHub.Tests.ps1
Invoke-Pester .\scripts\tests\CodexWorker.State.Tests.ps1
Invoke-Pester .\scripts\tests\CodexWorker.Publish.Tests.ps1
Invoke-Pester .\scripts\tests\CodexWorker.Deployment.Tests.ps1
```

Expected: new Kimi tests pass and all non-baseline Codex tests remain green.

- [ ] **Step 6: Commit provider-aware lifecycle behavior**

```powershell
git add -- scripts/codex-worker/Private/GitHub.ps1 scripts/codex-worker/Private/State.ps1 scripts/codex-worker/Private/Publish.ps1 scripts/codex-worker/Private/Deployment.ps1 scripts/tests/CodexWorker.GitHub.Tests.ps1 scripts/tests/CodexWorker.State.Tests.ps1 scripts/tests/CodexWorker.Publish.Tests.ps1 scripts/tests/CodexWorker.Deployment.Tests.ps1
git commit -m "feat: track Kimi issue lifecycle labels"
```

### Task 4: Route explicit Kimi labels through GitHub Actions

**Files:**
- Modify: `.github/workflows/codex-issue.yml`
- Modify: `.github/workflows/codex-revise.yml`
- Modify: `scripts/codex-worker/Invoke-Issue.ps1`
- Modify: `scripts/codex-worker/Invoke-Revision.ps1`
- Test: `scripts/tests/CodexWorker.Workflows.Tests.ps1`

- [ ] **Step 1: Write failing workflow-routing tests**

```powershell
It 'routes only kimi and kimi-retry labels to the Kimi provider' {
    $issueWorkflow | Should Match "github\.event\.label\.name == 'kimi'"
    $issueWorkflow | Should Match "github\.event\.label\.name == 'kimi-retry'"
    $issueWorkflow | Should Match 'CODEX_PROVIDER:'
    $issueWorkflow | Should Not Match 'fallback.*Kimi|Kimi.*fallback'
}

It 'routes kimi-revise from issues and PRs to the revision worker' {
    $revisionWorkflow | Should Match "github\.event\.label\.name == 'kimi-revise'"
}
```

- [ ] **Step 2: Run workflow tests and confirm they fail**

Run:

```powershell
Invoke-Pester .\scripts\tests\CodexWorker.Workflows.Tests.ps1
```

Expected: failure because no Kimi labels or provider environment value are present.

- [ ] **Step 3: Implement explicit routing**

Add workflow environment selection based only on the label:

```yaml
CODEX_PROVIDER: ${{ startsWith(github.event.label.name, 'kimi') && 'Kimi' || 'Codex' }}
```

Extend the issue job condition to include `kimi` and `kimi-retry`; extend the revision condition to include `kimi-revise`. Add a typed `provider` choice (`Codex`, `Kimi`) to manual dispatch, defaulting to `Codex`. Pass `-Provider ([string]$env:CODEX_PROVIDER)` to both entry scripts.

In both entry scripts, accept only `Codex` or `Kimi`, resolve the provider before worker invocation, and forward it into `Invoke-CodexIssueRun`/`Invoke-CodexRevision`. Do not infer Kimi from title, body, comments, model availability, or a Codex error.

- [ ] **Step 4: Run workflow tests**

Run:

```powershell
Invoke-Pester .\scripts\tests\CodexWorker.Workflows.Tests.ps1
```

Expected: all workflow tests pass.

- [ ] **Step 5: Commit Actions routing**

```powershell
git add -- .github/workflows/codex-issue.yml .github/workflows/codex-revise.yml scripts/codex-worker/Invoke-Issue.ps1 scripts/codex-worker/Invoke-Revision.ps1 scripts/tests/CodexWorker.Workflows.Tests.ps1
git commit -m "feat: route explicit Kimi issue labels"
```

### Task 5: Add configuration, preflight, labels, documentation, and pilot validation

**Files:**
- Modify: `scripts/codex-worker/config.example.json`
- Modify: `scripts/codex-worker/Private/Install.ps1`
- Modify: `docs/local-codex-worker.md`
- Modify: `scripts/tests/CodexWorker.Install.Tests.ps1`
- Create: `scripts/tests/CodexWorker.Kimi.E2E.Tests.ps1`

- [ ] **Step 1: Write failing installer and dry-run tests**

```powershell
It 'creates the Kimi labels only when Kimi is enabled' {
    $plan = Get-CodexLocalWorkerPlan -Config ([pscustomobject]@{ enabledProviders=@('Codex','Kimi'); kimiCommand='kimi' }) @common
    $plan.LifecycleLabels.Name | Should Contain 'kimi'
    $plan.LifecycleLabels.Name | Should Contain 'kimi-ready'
}

It 'rejects a Kimi-enabled configuration when kimi --version fails' {
    { Test-CodexPrerequisitePolicy -Config $kimiConfig -CommandRunner $missingKimi } | Should Throw
}
```

- [ ] **Step 2: Run installer tests and confirm they fail**

Run:

```powershell
Invoke-Pester .\scripts\tests\CodexWorker.Install.Tests.ps1
```

Expected: failure because Kimi configuration and labels are absent.

- [ ] **Step 3: Implement opt-in Kimi configuration and preflight**

Use this documented config shape:

```json
{
  "enabledProviders": ["Codex", "Kimi"],
  "codexCommand": "codex",
  "kimiCommand": "kimi",
  "kimiModel": "",
  "kimiTimeoutMinutes": 120
}
```

`enabledProviders` defaults to `["Codex"]`, preserving existing installations. When `Kimi` is enabled, require a bare executable name for `kimiCommand`, run `kimi --version`, then run a bounded read-only smoke check:

```powershell
kimi --auto --prompt 'Reply exactly READY' --output-format text
```

If the smoke check fails, instruct the user to complete `kimi login` interactively; do not embed credentials, invoke a provider-management command that writes credentials, or silently disable Kimi. Create the eight Kimi labels listed in the contract, including trigger and revision labels.

- [ ] **Step 4: Document operation and recovery**

Update `docs/local-codex-worker.md` with:

```markdown
Apply `kimi` to an issue to start Kimi. Apply `kimi-revise` to its open draft PR or issue for a revision. Kimi is never selected automatically and a `codex` retry remains a Codex retry.

Monitor: `gh run list --workflow "Codex local issue worker"`; inspect `state.json` `agentProvider`; follow the issue's persisted `runDirectory` `activity.log`.
```

Document `kimi-ready`, `kimi-blocked`, and `kimi-done`; state that the wrapper, not Kimi, commits, pushes, labels, opens PRs, or deploys.

- [ ] **Step 5: Run installation and end-to-end dry-run validation**

Run:

```powershell
Invoke-Pester .\scripts\tests\CodexWorker.Install.Tests.ps1
Invoke-Pester .\scripts\tests\CodexWorker.Kimi.E2E.Tests.ps1
.\scripts\codex-worker\Install-LocalWorker.ps1 -Repository 'voltarenyah/AgentAssistPlcDev' -RepositoryRoot 'C:\Users\Ansel\orca\projects\AgentAssistPlcDev' -WhatIf
$pilotUrl = gh issue create --repo voltarenyah/AgentAssistPlcDev --title 'Kimi worker dry-run pilot' --body 'Validate explicit Kimi routing only. No code change is expected.'
$pilotIssue = [int]([uri]$pilotUrl).Segments[-1]
gh workflow run 'Codex local issue worker' --repo voltarenyah/AgentAssistPlcDev -f issue_number=$pilotIssue -f provider=Kimi -f dry_run=true
```

Expected: all new tests pass; installer plan includes Kimi only when enabled; the manual dry-run displays `KIMI` console milestones and makes no state, label, worktree, agent, commit, or PR mutation.


- [ ] **Step 6: Perform one explicit low-risk live pilot after user approval**

Create or use one issue whose acceptance criterion is a documentation-only edit. Apply `kimi`; verify one worktree, `agentProvider=Kimi`, `kimi-ready`, a draft PR, Kimi activity JSONL, canonical final summary, and no Codex lifecycle-label changes. Do not merge the PR during the pilot.

- [ ] **Step 7: Commit configuration and documentation**

```powershell
git add -- scripts/codex-worker/config.example.json scripts/codex-worker/Private/Install.ps1 docs/local-codex-worker.md scripts/tests/CodexWorker.Install.Tests.ps1 scripts/tests/CodexWorker.Kimi.E2E.Tests.ps1
git commit -m "docs: document explicit Kimi issue worker"
```

## Final verification checklist

- [ ] `git diff --check` succeeds.
- [ ] All new Kimi, agent, workflow, installer, state, publication, GitHub, and deployment tests pass.
- [ ] Existing Codex workflow tests remain green; separately report known Pester 3 baseline assertion failures until the test-compatibility cleanup is completed.
- [ ] Kimi is unavailable unless `enabledProviders` contains `Kimi` and the issue has `kimi`, `kimi-retry`, or `kimi-revise` for its applicable lifecycle action.
- [ ] An issue labelled `codex` still produces only Codex behavior and Codex labels.
- [ ] A Kimi run cannot receive GitHub credentials, commit, push, create a PR, merge, deploy, reset, clean, or modify another worktree.
- [ ] The first real Kimi pilot is a human-reviewed draft PR only.
