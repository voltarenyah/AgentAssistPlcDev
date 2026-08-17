# Local Codex GitHub Issue Automation Design

Date: 2026-08-17

## Goal

Turn a trusted GitHub issue label into an immediate, reviewable local Codex development run. The worker should implement and validate one issue at a time in an isolated worktree, publish a draft pull request, retain the worktree for review revisions, and continue with the next queued issue. After a pull request is merged, the local machine should offer a short, cancelable countdown before rebuilding and launching the merged application.

## User workflow

1. Create or update a GitHub issue with a complete task specification.
2. Apply the `codex` label.
3. GitHub immediately dispatches the issue to the local Windows worker.
4. Monitor progress in GitHub Actions, concise issue comments, or local run logs.
5. Review the draft pull request and its validation evidence.
6. Merge the pull request after human review.
7. When the worker is idle, receive a local Windows dialog announcing that the merged application will rebuild in ten seconds.
8. Let the countdown finish, choose **Later** to snooze for five minutes, or choose **Cancel** to cancel that pending deployment.
9. Check the relaunched application after its automated health checks pass.

Human review remains mandatory. The automation never merges a pull request.

## Chosen architecture

Use a GitHub Actions self-hosted runner on the local Windows machine. The runner receives GitHub events over its outbound connection, so the design requires no inbound port, webhook listener, or public tunnel.

Run the worker in the logged-in Windows user session rather than as a non-interactive Windows service. This permits local notifications and preserves access to the development environment needed by browser, desktop, and TIA-related validation. The machine must be powered on, the user session available, and the runner active for immediate execution. GitHub may queue jobs while the machine is unavailable.

```text
GitHub issue labeled codex
        |
        v
GitHub Actions workflow
        |
        v
Self-hosted Windows runner
        |
        v
Local coordinator and single execution lock
        |
        v
Issue worktree -> codex exec -> validation -> commit -> push -> draft PR
        |
        v
Park worktree and release the worker for the next queued issue
```

The runner is the event transport. A repository-owned PowerShell coordinator is the workflow authority for claiming issues, creating or resuming worktrees, invoking Codex, recording status, publishing changes, and handing merged commits to the local deployment notifier.

## Components

### GitHub workflows

The repository will contain narrowly scoped workflows for:

- issue intake when the `codex` label is added;
- revision intake when `codex:revise` is added to an issue or pull request;
- merge handoff when a linked Codex pull request closes with `merged = true`;
- optional manual retry and operational diagnostics.

Jobs target a dedicated runner label in addition to `self-hosted`, `Windows`, and `X64`. GitHub concurrency uses one repository-wide group with cancellation disabled. A local cross-process lock provides a second guard against overlap between issue work, revisions, cleanup, and deployment.

### Local coordinator

The coordinator maintains durable, restart-safe state outside tracked source files. State includes:

- queued and active issue numbers;
- issue branch, worktree, run, commit, and pull-request identities;
- parked worktrees awaiting review or revision;
- pending deployment commit and notification state;
- retry counts and the last actionable failure.

All state transitions are idempotent. Re-delivered events, workflow retries, runner restarts, and repeated labels must resume or report existing work rather than create duplicate branches, worktrees, commits, or pull requests.

### Codex CLI

Install the Codex CLI on the worker and authenticate it for the local user. Invoke `codex exec` non-interactively in the issue worktree with:

- workspace-write sandboxing limited to the issue checkout;
- the repository `AGENTS.md` as authoritative workflow guidance;
- the full issue description, comments, labels, and linked-work context as task data;
- machine-readable JSONL events;
- a structured final-output schema for status and handoff information;
- no permission to merge, force-push, reset, clean, or modify the primary checkout.

Non-interactive execution removes the terminal chat UI; it does not remove observability. The event stream exposes agent messages, commands, file changes, tool activity, test progress, errors, and the final result. Internal hidden reasoning is not exposed, but evidence-backed diagnosis and decisions must appear in the final summary.

The wrapper, rather than Codex, owns GitHub credentials and performs final push and pull-request creation. The issue body is untrusted task content and cannot override `AGENTS.md`, sandbox boundaries, publishing policy, or cleanup guards.

## Trigger trust and security

Only an authorized repository actor may apply a trigger label. The workflow must verify the event actor's current repository permission before sending issue content to the local worker. Issue authorship alone does not grant execution authority; a trusted collaborator may deliberately label an externally reported issue after reviewing it.

The design follows these boundaries:

- no inbound listener or public tunnel on the Windows machine;
- no OpenAI or GitHub secret included in the Codex prompt or run artifact;
- least-privilege GitHub workflow permissions for each job;
- Codex writes only in the active issue worktree;
- publishing and status updates occur in wrapper-controlled steps;
- no direct write, stash, reset, clean, or branch switch in the user's primary checkout;
- no automatic merge, force-push, shared-history rewrite, or remote-branch deletion;
- dependency and network access is allowlisted only where required by repository setup and tests.

Self-hosted execution of untrusted repository content is inherently sensitive. Workflow changes and executable scripts from a feature branch must not replace the trusted coordinator before review. The coordinator and workflow definitions execute from the trusted default-branch revision, while the Codex sandbox receives the issue worktree as data and workspace.

## Queue and state model

Only one Codex or deployment operation may hold the execution lock at a time.

```text
codex label
  -> codex:queued
  -> codex:running
  -> codex:pr-ready
  -> codex:done after merge

exceptional states
  -> codex:blocked
  -> codex:retry
  -> codex:revise
```

Rules:

- New issues and revision requests are processed sequentially in the order the self-hosted runner receives their jobs.
- A `codex:revise` job never interrupts a running operation. If faster review turnaround is needed, the user may cancel a queued, not-yet-started issue run from GitHub before retrying the revision event.
- One transient infrastructure failure may retry automatically with bounded backoff.
- Missing requirements, unsafe ambiguity, architectural decisions, persistent infrastructure failures, and unresolved test failures stop in `codex:blocked` with a concrete explanation.
- `codex:retry` resumes the existing branch and worktree after the blocking condition is corrected.
- Duplicate or stale events become no-ops with an explanatory log entry.

## Issue worktree lifecycle

Each issue owns:

- branch `codex/<issue-number>-<short-description>`;
- worktree `.worktrees/issue-<issue-number>-<short-description>`;
- local run history keyed by issue and attempt;
- at most one open pull request for that branch.

Before creating anything, the coordinator checks linked issue branches, open pull requests, local branches, registered worktrees, and saved run state. Existing work is resumed.

"One issue at a time" means one automation-active job and one automation-active issue worktree at a time. Completed issue worktrees remain parked on disk while their pull requests await review. The coordinator may create and activate another issue worktree after the previous pull request is ready. Parked worktrees are not mutated by the automation unless their revision job holds the execution lock; user edits in a parked worktree are detected and preserved during revision preflight.

When review changes are requested, `codex:revise` reactivates the parked worktree and resumes the same branch. If the worktree is unavailable but the branch is safely pushed, it may be reconstructed from the remote branch.

After a pull request is merged or deliberately closed, cleanup is allowed only when:

- the worktree resolves beneath the automation-owned `.worktrees` directory;
- no process is using it;
- it has no uncommitted changes;
- it has no unpushed commits;
- its pull-request state matches the cleanup event.

If any guard fails, preserve the worktree and report cleanup as blocked. Remote branch deletion is not part of this automation.

## Codex task contract

For every issue, Codex must:

1. Read the complete issue and comments.
2. Check existing branches, pull requests, and overlapping work.
3. Reproduce or establish the current behavior when practical.
4. Identify root cause for bugs or the responsible design boundary for features.
5. Implement the smallest scoped change.
6. Add or update regression coverage where practical.
7. Run focused validation first and broader relevant validation afterward.
8. Perform runtime and browser validation for user-visible behavior when practical.
9. Review the complete diff and exclude unrelated changes.
10. Return structured handoff data to the wrapper.

The final output schema includes:

- completion status;
- root cause or design approach;
- changed components;
- important decisions;
- validation commands and outcomes;
- warnings, skipped checks, and remaining risk;
- suggested commit title and pull-request body fields;
- whether human clarification is required.

The wrapper validates the worktree status and final schema before committing. It then creates logically scoped commits, pushes the issue branch, and creates or updates a draft pull request using the repository's required summary, problem, root-cause/design, changes, validation, risks, and issue sections.

## Monitoring and review

### Live visibility

The GitHub Actions run is the remote live view. The coordinator converts JSONL events into concise, timestamped log lines suitable for the Actions console. A local operator can also tail the readable activity log while the raw JSONL stream remains available for diagnosis.

Each run records:

```text
runs/issue-<number>/<attempt>/
  events.jsonl
  activity.log
  final-summary.json
  validation/
```

Run storage is ignored by Git and has a bounded retention policy.

### GitHub milestones

Issue comments remain concise and are posted only for meaningful transitions:

- work claimed, with branch, worktree, and Actions-run links;
- root cause or implementation direction established;
- validation in progress;
- blocked state and required user action;
- draft pull request ready.

### Pull-request review

The draft pull request is the primary review artifact. It contains the complete diff, structured summary, validation evidence, remaining risks, commit identity, Actions-run link, and retained worktree path. Full logs are uploaded as a bounded-retention Actions artifact.

Human review may continue in GitHub or directly in the retained local worktree. A revision trigger resumes that same issue context without competing with the currently active job.

## Post-merge local deployment

Merging a Codex pull request creates a pending local deployment but does not immediately stop or replace the running application.

The merge workflow hands the exact merged commit to a durable local notifier and exits. If multiple merges arrive before deployment, the notifier coalesces them and targets the newest verified `origin/master` commit. A canceled deployment applies only to the currently pending target; a later merge creates a new pending deployment.

Deployment scheduling follows these rules:

1. Wait for any active Codex, test, revision, or cleanup operation to release the execution lock.
2. Acquire a deployment reservation so no new issue job starts during the decision or deployment window.
3. Wait for an interactive Windows session in which the confirmation dialog can be shown.
4. Display: **Automation Workbench will rebuild in 10 seconds.**
5. Start the countdown only after the dialog is visible.
6. At zero, deploy automatically.
7. **Later** releases the reservation, snoozes for five minutes, and repeats from step 1.
8. **Cancel** clears that pending deployment without changing the running application.

The five-minute snooze does not block issue work. When the snooze expires, the notifier again waits for the worker to become idle before showing a new countdown.

## Runtime worktrees and rollback

Deployment never updates the user's primary checkout. It uses two automation-owned detached runtime worktrees, `runtime-a` and `runtime-b`, pinned to exact commits from `origin/master`. Durable deployment state records which slot is active and its verified commit. The inactive slot is the only slot prepared for a new deployment.

The deployment sequence is:

1. Fetch remote state without modifying the primary checkout.
2. Verify the target commit is the expected merged commit and is reachable from `origin/master`.
3. Verify the inactive slot path is automation-owned, then recreate that detached worktree cleanly at the exact target commit.
4. Restore dependencies and build the application while the currently deployed version continues running.
5. If the build fails, preserve the current application and report the failure.
6. After a successful build, launch the new slot through the repository `launch.ps1` entry point using the already-built output.
7. Verify HTTP 200 from the frontend and ApiHost plus `status: ok` from the LangGraph sidecar:
   - `http://localhost:5173/`
   - `http://localhost:5239/api/status`
   - `http://localhost:8787/health`
8. Record the deployed commit, processes, endpoints, health responses, and logs.
9. If launch or health verification fails, run the launcher from the unchanged previous active slot using its already-built output, verify its health, and report rollback evidence.
10. Mark the new slot active only after all health checks pass.

Before implementation, inspect and test the launcher's exact build/no-build behavior so the prebuild and rollback commands match the repository entry point on Windows.

## Failure handling

- **Worker offline:** GitHub retains the queued job; no local state is changed.
- **Authentication missing or expired:** stop before worktree mutation and report setup instructions.
- **Existing implementation or conflicting pull request:** do not compete; link the existing work and block for direction when needed.
- **Codex process failure:** preserve JSONL, the worktree, and partial changes; retry only a classified transient failure.
- **Tests fail:** Codex diagnoses failures within the issue run; unresolved failures block publication rather than weakening tests.
- **Push or PR creation fails:** preserve the local commit and retry publication without rerunning implementation.
- **Runner restart:** reconstruct state from GitHub, Git branches/worktrees, and durable local run state.
- **Dirty or unpushed cleanup target:** preserve it and report the exact guard that failed.
- **Notification unavailable:** keep deployment pending; never begin the ten-second countdown invisibly.
- **Build failure:** keep the current application running.
- **Launch or health failure:** restore the previous known-good runtime slot and surface logs.

## Validation strategy

### Coordinator tests

- label trust and event filtering;
- queue ordering and the single execution lock;
- duplicate-event idempotency;
- branch and worktree discovery, creation, parking, resume, and guarded cleanup;
- sequential revision handling without interruption;
- transient retry classification and retry bounds;
- structured Codex output parsing and secret redaction;
- push and pull-request publication recovery;
- durable restart recovery.

### Deployment tests

- merged-commit handoff and coalescing to the newest `origin/master`;
- waiting for active work and releasing the reservation on **Later**;
- five-minute snooze and repeated prompt;
- **Cancel** behavior;
- countdown beginning only after a visible dialog;
- build-before-switch behavior;
- endpoint health evidence;
- rollback to the previous runtime slot.

### End-to-end rollout

1. Run a dry-run label that reads and plans an issue without editing files.
2. Process a small pilot issue through label, worktree, Codex, validation, commit, push, and draft PR.
3. Request a revision and prove the parked worktree is reused.
4. Merge the pilot and verify the local ten-second prompt.
5. Exercise **Later** and verify a five-minute re-prompt without blocking issue work.
6. Merge or select a disposable change, allow deployment, and verify the exact commit plus all three services.
7. Simulate a failed launch and verify rollback evidence.

## Non-goals

- Automatically merging pull requests.
- Allowing multiple Codex issue jobs to edit concurrently.
- Exposing a local webhook endpoint to the internet.
- Modifying or cleaning the user's primary checkout.
- Automatically applying high-impact architecture, schema, security, or dependency decisions without human input.
- Replacing GitHub Issues and pull requests as the development source of truth.
- Building a general-purpose multi-repository agent platform in the first version.

## Acceptance criteria

- Applying `codex` by a trusted actor dispatches work to the online local worker without manual issue URL copying.
- Exactly one issue or deployment operation is active at a time.
- Every issue is implemented in its own branch and worktree, with no changes to the primary checkout.
- Draft pull requests contain complete, evidence-based summaries and validation results.
- Actions logs, local logs, issue milestones, and PR artifacts make work reviewable while it runs and afterward.
- PR-ready worktrees remain available for revisions while the worker processes later issues.
- Duplicate events and restarts do not duplicate work.
- Merging never directly updates or restarts the local application.
- A visible local ten-second confirmation appears when the worker is idle; **Later** snoozes for five minutes and **Cancel** cancels the pending deployment.
- The selected merged commit is built in an automation-owned runtime slot, all three services are verified, and a failed switch restores the previous known-good version.
- Unrelated local changes, including changes in the primary checkout, remain untouched.
