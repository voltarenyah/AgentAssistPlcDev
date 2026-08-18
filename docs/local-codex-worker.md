# Local Codex issue worker

This guide operates the repository-owned PowerShell worker that turns a trusted
GitHub issue label into an isolated Codex run and a draft pull request. It also
covers the post-merge handoff to the local A/B runtime notifier.

The workflow files must be merged into the repository's default branch before
issue-label events can run. Each workflow checks out that trusted default-branch
revision; a workflow file that exists only on a feature branch cannot receive a
usable label-triggered run.

## Safety and trust model

The worker is deliberately single-threaded. One exclusive `worker.lock` covers
issue execution, revision, pull-request cleanup, and deployment. Codex receives
only `workspace-write` access to the issue worktree. The wrapper retains GitHub
credentials and performs GitHub reads/writes, commits, pushes, comments, labels,
and draft-PR creation. Issue and review text is data in a prompt, never shell
code. Only GitHub actors with repository `write`, `maintain`, or `admin`
permission pass the trigger trust check; this is a GitHub permission check, not
an instruction to grant Codex broader filesystem access.

Every issue uses a branch named `codex/<issue-number>-<slug>` and a worktree
below the repository's `.worktrees` directory. The primary checkout is not an
automation worktree and must remain clean or retain its pre-existing user
changes. Do not reset, clean, stash, force-push, rewrite shared history, delete
remote branches, or merge automatically. All published changes are draft PRs
for human review.

## Prerequisites and installation

The installer probes PowerShell 7, Git, GitHub CLI, .NET 8, Node.js 20+, npm,
Bootstrap Python 3.11 through 3.13, and the Codex CLI. GitHub CLI must already
be authenticated; the installer checks this with `gh auth status --hostname
github.com`. The repository and default branch are the values in
`config.example.json` (`voltarenyah/AgentAssistPlcDev` and `master`).

The normal installation entry point is:

```powershell
.\scripts\codex-worker\Install-LocalWorker.ps1 `
  -Repository 'voltarenyah/AgentAssistPlcDev' `
  -RepositoryRoot 'C:\Users\Ansel\orca\projects\AgentAssistPlcDev'
```

Optional supported parameters are `-DataRoot`, `-ConfigPath`, and
`-SkipPrerequisiteProbe`. `-WhatIf` is also supported by the script and plans
the setup without performing installer mutations. The installer writes its
effective configuration to `config.json`; do not put credentials in that file.
`ConfigPath` must be the canonical `config.json` directly under `DataRoot`. If
it already exists, the installer reads that JSON object itself before doing
preflight work. It preserves supported operational settings, including
`defaultBranch`, `codexCommand`, `bootstrapPython`, `workerLockTimeoutSeconds`,
`codexTimeoutMinutes`, `notificationSeconds`, `snoozeMinutes`,
`healthTimeoutSeconds`, `runRetentionDays`, and `pollSeconds`. `runtimeSlots`
is not an arbitrary list: it is exactly `runtime-a`, then `runtime-b` (the
installer derives their storage paths). Missing settings receive the defaults
from `config.example.json`; a new config uses the successfully selected and
probed Bootstrap Python executable rather than a null value. With
`-SkipPrerequisiteProbe`, `bootstrapPython` must already be an explicit,
canonical existing non-reparse executable, or setup must safely discover one
from the repository virtual environment or `Get-Command`; it never persists a
blind `python.exe`. Repository, repository root, data root,
runner root, runner name, runner label, and config path are installer-derived
and overwrite conflicting input. Rooted `bootstrapPython` must be a canonical
existing executable (its installation location may be outside the repository);
rooted `activityLogPath` must remain under
`DataRoot`; rooted `tiaWhitelistPath` must remain under the trusted repository
root. Every existing ancestor of these paths and of `bootstrapPython` is
checked for reparse points before use. A supplied `runnerRelease` is accepted
only when its tag, checksum, Windows x64 asset, and HTTPS
`github.com/actions/runner/releases/download/...` URL agree; configuration
cannot redirect downloads. Malformed JSON, wrong field types, invalid slot
lists, and out-of-root paths
fail before setup state, runner, task, or GitHub mutations.

Normal installation probes and enforces the prerequisite versions. Use
`-SkipPrerequisiteProbe` only when those checks have been performed separately:
it deliberately does not call any prerequisite version command or install a
missing Codex CLI, but still performs non-probe planning, mandatory
`gh auth status`, the read-only Codex `exec` authentication smoke test, and the
mandatory `codex exec resume --help` capability check. This flag carries the
risk that a missing or unsupported tool will fail later in the runner or task
workflow. It is not a credentials or mutation bypass.

If the Codex CLI is missing, setup installs `@openai/codex` with `npm.cmd
install --global @openai/codex` and invokes `codex login`. If the authentication
smoke test is not `READY`, complete `codex login` interactively and rerun the
installer. Setup also probes `codex exec resume --help` and persists the boolean
`supportsResumeOutputControls` in `config.json`.

The runner is downloaded only after its GitHub release asset and SHA-256
checksum are unambiguous. Registration uses the derived repository, fixed
runner name `AutomationWorkbenchCodexRunner`, and fixed label
`agentassist-local`; it is an interactive runner, not a service.
The installer verifies that the registered runner is online with that label
before creating the notifier task. It also creates the lifecycle labels and
repository variables `CODEX_LOCAL_REPOSITORY` and `CODEX_WORKER_DATA_ROOT`.

The two logon tasks are owned by the installer and run in the interactive user
session:

* `AutomationWorkbenchCodexRunner` runs `Start-GitHubRunner.ps1` and
  `run.cmd`.
* `AutomationWorkbenchCodexDeploymentNotifier` runs
  `Invoke-DeploymentNotifier.ps1 -Watch` through PowerShell 7 `-Sta`.

Both tasks are hidden, use an interactive token, ignore a second instance, and
are configured to restart up to three times at one-minute intervals. The
runner task must remain interactive because deployment notification uses WPF.

## Labels and one-at-a-time lifecycle

Apply `codex` to a trusted, low-risk issue after the workflows are merged and
the runner is online. The issue workflow accepts `codex` and `codex:retry`.
The revision workflow accepts `codex:revise` on an issue or pull request. The
close workflow receives both merged and unmerged PR closes so cleanup can run;
only a validated merged close creates a deployment record.

The worker removes only the other `codex:*` status labels when transitioning
status; it preserves the trigger label `codex` and unrelated labels.

| Label | Meaning |
| --- | --- |
| `codex` | User trigger for initial issue work. |
| `codex:queued` | Trusted intake was accepted and queued. |
| `codex:running` | Worktree setup or Codex execution is active. |
| `codex:retry` | One bounded retry is being requested for a transient service-unavailable result. |
| `codex:pr-ready` | Validation completed and publication is ready or recovering. |
| `codex:blocked` | Human input, a failed required step, malformed output, or an unrecoverable error needs attention. |
| `codex:revise` | Reuse the existing open draft PR worktree for review changes. |
| `codex:done` | A merged PR close was validated and its deployment handoff was durably recorded. |

An initial run claims one issue, creates or reuses its branch/worktree, prepares
dependencies, invokes Codex, records milestones, validates the structured final
summary, commits, pushes, and creates or recovers one draft PR. A PR-ready
worktree remains parked for revision. A revision must use the existing branch,
open draft PR, and `master` base; it never force-pushes or rewrites published
history. A transient service-unavailable result gets at most one retry; other
failures become blocked.

## Monitoring and evidence

The durable root defaults to `%LOCALAPPDATA%\AutomationWorkbench\CodexWorker`.
Its important files are:

* `state.json`: schema-version-1 issue attempts, thread/branch/worktree,
  publication stage, cleanup state, active runtime slot, and pending/last
  deployment evidence.
* `config.json`: installer-persisted repository/data-root, runner identity,
  resume capability, and deployment settings. It contains no GitHub token.
* `worker.lock`: the exclusive cross-process lock. Never delete it recursively.
* `runs\issue-<N>\<attempt>\`: `events.jsonl` (sanitized raw Codex JSONL),
  `activity.log` (readable timestamped lines), `final-summary.json`, and, after
  publication, `pull-request.md`.
* `runs\issue-<N>\activity.log`: dependency setup output for the issue worktree.
* `runs\deployment-<UTC timestamp>.log` and
  `runs\deployment-<UTC timestamp>-rollback.log`: deployment/rollback command,
  stdout, and stderr evidence.

The run state records `threadId`, `runDirectory`, `publicationStage`, `commit`,
and `prUrl`. The final summary must contain `status`, `rootCauseOrApproach`,
`changedComponents`, `decisions`, `validation`, `warnings`, `remainingRisks`,
`commitMessage`, `prTitle`, `requiresHumanInput`, and `humanQuestion`.

For a live local activity log, use the actual run directory and attempt number:

```powershell
Get-Content "$env:LOCALAPPDATA\AutomationWorkbench\CodexWorker\runs\issue-42\1\activity.log" -Wait
```

GitHub Actions provides the queue and console evidence:

```powershell
gh run list --workflow "Codex local issue worker"
$RunId = gh run list --workflow "Codex local issue worker" --limit 1 --json databaseId --jq '.[0].databaseId'
gh run watch $RunId --log
```

Issue milestone comments include claimed, approach, validation, blocked, or
PR-ready headings and the workflow URL when the Actions environment supplies
one. The draft PR body contains Summary, Problem, Root Cause / Design, Changes,
Validation, Risks, and Issue sections. Review the raw JSONL, readable activity
log, Actions run, issue milestones, final summary, and draft PR together; one
surface alone is not complete evidence.

## Dry runs, retry, revision, and blocked work

Inspect the issue before triggering it:

```powershell
gh issue view 42 --comments
```

The local read-only entry point is:

```powershell
powershell.exe -NoProfile -File scripts/codex-worker/Invoke-Issue.ps1 `
  -Repository 'voltarenyah/AgentAssistPlcDev' `
  -IssueNumber 42 `
  -Actor 'trusted-user' `
  -EventName 'workflow_dispatch' `
  -DryRun
```

`-DryRun` checks actor permission, retrieves issue context and existing
development, computes the branch name, and returns `WorktreeCreated = false`
and `CodexInvoked = false`. It does not acquire `worker.lock`, write
`state.json`, change labels/comments, create a branch/worktree, invoke Codex,
commit, push, or create a PR. Use a real issue and a trusted actor; verify the
primary checkout and worktree/branch snapshots before and after.

For a blocked issue, resolve the human question or failure, then apply
`codex:retry` to request the bounded retry path. For review feedback, apply
`codex:revise` to the issue or its existing PR. The worker resolves the linked
issue, checks the saved branch/PR identity, and reuses the parked worktree. It
records a fresh attempt and appends a new non-force-pushed commit. Do not delete
a parked worktree merely to clear a blocked run.

Cleanup after an unmerged or merged PR is guarded. The worker refuses removal
when the path is outside `.worktrees`, is not the registered expected branch,
is referenced by an active process, has dirty or untracked files, or contains
commits not present on `origin/<branch>`. It records `cleanupStatus` and
`cleanupBlockers`, comments the exact blockers, and preserves the worktree.

## Merge handoff and deployment decisions

A validated merged close records a pending deployment tuple in `state.json`:
`targetCommit` (full 40-character SHA), `sourcePr`, `requestedAt`, optional
`snoozeUntil`, and `status` (`pending` or `snoozed`). It does not launch or
restart the application. Newer reachable merge candidates replace older ones;
divergent or untrusted ancestry fails closed.

When the worker is idle and the lock is available in an interactive desktop,
the notifier displays a visible topmost WPF dialog:

> Automation Workbench will rebuild in 10 seconds.

The one-second timer starts only after the window is rendered. **Later (5 min)**
releases the reservation, changes the pending record to `snoozed`, and retries
after exactly five minutes without blocking issue work. **Cancel** clears only
the current pending deployment. Closing the window behaves as **Later**. No
countdown begins when the session is unavailable, the worker is busy, or the
dialog cannot be shown.

Deployment uses exactly two automation-owned detached slots, `runtime-a` and
`runtime-b`, below `.worktrees`; it never updates the primary checkout. The
inactive slot is recreated at the exact verified `origin/master` merge SHA,
then the worker runs these implemented preparation commands in that slot:

* `dotnet restore AgentAssistPlcDev.sln`
* `dotnet build AgentAssistPlcDev.sln -v q`
* `npm.cmd ci --prefix studio`
* `npm.cmd run build --prefix studio`
* the configured Bootstrap Python `-m venv agent-service\.venv`
* the slot venv `-m pip install -e agent-service[test]`

After preparation, it launches the slot with `launch.ps1 -NoBuild` and records
command arguments, working directory, exit code, process ID, stdout, stderr,
service processes, and health responses. Health must be HTTP 200 for
`http://localhost:5173/` and `http://localhost:5239/api/status`, and HTTP 200
with `status: ok` for `http://localhost:8787/health`. The health record also
captures sidecar model/fallback fields when present.

If preparation fails, the current slot remains running. If switch or health
verification fails after preparation, the previous active slot is relaunched
with `launch.ps1 -NoBuild`; rollback health and process evidence is recorded.
An unverified rollback is marked `rollback-failed` and receives a high-priority
issue comment when an issue number is available.

## Uninstall and disaster recovery

There is no separate uninstall wrapper. The installer owns the two named tasks,
the runner registration, the runner directory below the data root, and the
installer metadata. Before removing anything, stop the runner/notifier through
the normal Task Scheduler UI or `schtasks.exe`, and verify the task names and
installer ownership marker. Do not remove a task or runner with the same name
unless its description/metadata proves it was created by this installer.

Preserve `%LOCALAPPDATA%\AutomationWorkbench\CodexWorker` and its `runs` data
before cleanup. It is the recovery evidence. If a runner restart occurs, the
worker reconstructs work from GitHub issue/PR state, the saved branch/worktree,
`state.json`, and the run logs. A failed publication leaves the local commit
and run directory for publication recovery; do not rerun implementation just
to retry a push or draft PR.

For a dirty, busy, unpushed, untrusted, or ambiguous worktree, leave it parked
and resolve the recorded blocker manually. For corrupt `state.json`, the state
reader quarantines it as `state.corrupt.<UTC stamp>.json` and starts a fresh
schema-version-1 state; retain the quarantine file for diagnosis. For failed
deployment persistence or rollback, keep both deployment logs and the recorded
`lastDeployment` evidence, restore the previous slot only after its health
checks pass, and require human review before another deployment attempt.

Never copy `GITHUB_TOKEN`, `GH_TOKEN`, `OPENAI_API_KEY`, `CODEX_API_KEY`, or
`DEEPSEEK_API_KEY` into config, prompts, issue text, or logs. Codex process
environment forwarding removes those names, and JSONL/activity output redacts
known token patterns, but operators must still treat run artifacts as sensitive.
