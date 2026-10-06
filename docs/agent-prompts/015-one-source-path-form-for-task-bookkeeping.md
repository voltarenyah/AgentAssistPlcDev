# 015. One source path form for task bookkeeping

Status: done
Created: 2026-10-06
Depends on: 012

## Goal

Every task-side path comparison resolves the same object — a device's registered source object and a
commit's selected path — through one rule, so a task commit advances the baselines of the objects it
contained and the task guard accepts the objects the task actually stages.

## Context

Found by verifying the user's own task-only commit (`ade4a89`, "task only commit"):

- The commit side was correct: one file (`devices/Sino_PEI/source/Blocks/15_VOC_Door/202_VocDoorGeneral
  [FB170].xml`, +2/−2 lines), attributed to the task `5efacc2e…` (`default`, primary), linked to the
  staged source object, carrying an ADR-0001 `tia-managed-source` tag bound to its own SHA with
  `managedSourceConsistent` false (012's AC-002 guard), and Git-only (SVN still r1, no pending commit).
- The task side was not. The task's stage baseline still held the **pre-change** fingerprint:
  `Code = bMLA21xSs…`, which is exactly the comparison's `stored` value, not its `live` (`wgkFSoFo…`).
  Item 012's `TryRecordCommittedStageEvidenceAsync` therefore matched **no** stage and advanced nothing —
  silently.
- Root cause, two recorded forms of one path:
  - a commit selects `devices/Sino_PEI/source/Blocks/15_VOC_Door/202_VocDoorGeneral [FB170].xml`
    (relative to the worktree);
  - the graph records it as `Blocks/15_VOC_Door/202_VocDoorGeneral [FB170].xml` (relative to the
    device's source root), because the staging route registers
    `DeviceSnapshotReader.ReadManifestSourceObjects(context.SourceRoot)` paths — confirmed against
    `GET …/devices/{id}/source-objects`, whose `relativePath` is the source-root form.
- 012 compared those two strings directly, and so did the `/vc/commit` task guard
  (`stagedPaths.Contains(path)`), which means every legitimate task commit through the Changes list
  was rejected with `TASK_COMMIT_STAGE_MISMATCH` / "Not staged on this task: …". The guard's owner note
  (`DescribeUnstagedPaths`) had the same mismatch, so it never named the owning task either.

## Constraints

- One rule, used by every task-side comparison; no guessing from suffixes alone, and no silent
  no-op when nothing matches (012's failure mode).
- Accept a legacy registration that already holds the worktree-relative path, so older graphs keep
  working.
- Keep the task guard's meaning: only objects staged on the committing task may be committed, and a
  message-only commit still needs no paths.

## Done when

1. A test fails without the rule and passes with it: a task commit advances only the committed
   object's baseline when the object is registered in the source-root form (the real form).
2. A route test proves a staged object with the real registration form is accepted by the task guard,
   while an object outside its stages is still refused and the refusal names its owning task.
3. `dotnet build AgentAssistPlcDev.sln`, `tests/Agent.Tests`, `tests/ApiHost.Tests` pass; the studio is
   untouched.
4. The user is told how to clear the stale baseline their already-committed object left behind.

## Evidence

Branch `codex/010-task-only-compare`. Nothing pushed; no PR.

### What changed

- `src/Agent/Workbench/SourcePathForms.cs` (new) — `CommitPath` resolves a registered object path
  against the device source root into the commit's form, and `Matches` accepts either recorded form.
- `src/Agent/Workbench/WorkbenchCoordinator.cs` — `TryRecordCommittedStageEvidenceAsync` loads the task
  device first, derives the source root relative to the worktree, and matches each stage through
  `SourcePathForms.Matches` instead of comparing raw strings.
- `src/ApiHost/WorkbenchApiModels.cs` — the `/vc/commit` task guard and `DescribeUnstagedPaths` resolve
  both forms; the owner note now names the task that actually stages an object.
- Tests: `tests/Agent.Tests/MasterSynchronizationTests.cs` now registers staged objects exactly as the
  staging route does (source-root form) instead of the worktree form that hid the defect;
  `tests/ApiHost.Tests/WorkbenchEndpointsTests.cs` adds
  `ActiveTaskCommitAcceptsAStagedObjectInItsRegisteredPathForm` and registers the owner-case object in
  the real form.

### Validation run

| Command | Observed result |
|---|---|
| `dotnet test tests/Agent.Tests --filter TaskCommitAdvancesOnlyTheCommittedObjectsStageBaseline` with the rule neutered (old comparison) | **Failed** — the regression is caught (RED) |
| the same filter with the rule restored | passed |
| `dotnet test tests/Agent.Tests` | 518 passed |
| `dotnet build AgentAssistPlcDev.sln -v q` | 0 errors |
| `dotnet test tests/ApiHost.Tests` | 228 passed (227 before, +1) |
| studio | untouched |

The route test's own RED could not be observed directly: with the rule neutered, rebuilding
`tests/ApiHost.Tests` needs `src/ApiHost`'s output, which the running app held. Both sites call the
same rule, and the Agent-level RED exercises it.

### Done when checks

1. **Passed** — the RED/GREEN pair in the table.
2. **Passed** — `ActiveTaskCommitAcceptsAStagedObjectInItsRegisteredPathForm` (the guard resolves the
   real form and does not refuse) and the existing refusal test, whose owner-case object now uses the
   real registration form, still asserts `staged on 'Other task'`.
3. **Passed** — see the table.
4. **Delivered in the handoff**, with the finding that re-staging does **not** clear it: a stage
   baseline is re-derived from the device manifest at HEAD, and an accept commit rewrites only the XML,
   so that manifest still holds the old fingerprints.

### Notes, decisions and risks

- **The already-committed object stays stale until one of:** an explicit "re-read this task's baselines
  from TIA" action (not built — proposed as 016, since it is 012's capture exposed deliberately), or a
  source manifest refresh followed by a re-stage. Committing again cannot clear it: the object matches
  what is already committed, so the commit reports nothing to commit.
- **Both forms accepted:** the raw-form match exists only so a legacy registration keeps working. A
  commit path is worktree-relative in every current flow, so it cannot collide across devices; the
  prefix resolution is what decides the real case.
- **Not verified end to end on real TIA:** the fix is proven by the tests above and by the recorded
  defect; the user's next task commit is what shows a baseline advancing in their own data.