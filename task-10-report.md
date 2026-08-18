# Task 10 report

## Result

Implemented PR-close cleanup and pending deployment handoff.

- `Register-PrClosed.ps1` accepts repository, PR number, optional linked issue, merged flag, merge SHA, head branch, repository root, and data root.
- PR closing references are resolved fail-closed to exactly one same-repository issue; an explicit issue number is checked for consistency.
- Saved issue state is read under the worker lock. Cleanup uses injectable guarded worktree checks and preserves dirty, busy, outside-root, unregistered, and unpushed worktrees with a PR/issue blocker comment.
- Unmerged closes perform cleanup only; they never create deployment state or apply `codex:done`.
- Merged closes verify a full merge SHA is reachable from fetched `origin/master`, write atomic pending state, and apply `codex:done` after verification.
- Existing pending/snoozed deployments coalesce only to a newer verified master commit and preserve a later snooze. Pending targets are never moved backwards; remote branches are never deleted.

## Validation

```text
Invoke-Pester -Path scripts/tests/CodexWorker.Deployment.Tests.ps1
Passed: 4 Failed: 0
git diff --check
clean
```

The broader `scripts/tests` run on base `243ae85` currently reports unrelated existing failures in Codex summary/process, publication, state, linked-reference, and worktree tests; Task 10's focused suite passes.

## Follow-up

The Task 8 PR-close workflow currently gates invocation on `merged == true`. To exercise guarded cleanup for unmerged close events, remove that job-step gate while retaining merged-only deployment behavior inside `Register-PrClosed.ps1`.
