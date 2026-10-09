# 013. A native savepoint needs a clean Full scan at that commit

Status: done
Created: 2026-10-06
Depends on: 010

## Goal

Creating a native SVN savepoint requires a project-wide TIA comparison against the commit the savepoint
will record, and that comparison must have found nothing unresolved. Without one the action is refused
with a message that names the Full scan the user has to run, so a TIA-only change outside every task's
stages — which leaves no Git trace — cannot slip into the "complete" restore point.

## Context

- Requirement (human, 2026-10-06): "唯一完整的存盘节点是svn savepoint，所以在执行svn存盘之前，必须做一次全盘扫描确认没有之前漏掉的块，所有块都是最新的，没有等待提交的块，再做save point."
- Design `docs/design/task-scoped-tia-compare-design.md` AC-004: "A full scan exposes changed unassigned source objects, and unresolved objects block native savepoints." UI Spec row: "Create SVN savepoint | User requests a native savepoint | Shows the unresolved source objects and directs the user to full scan/assignment instead of creating a savepoint."
- The only implemented guard was Git-based: `CreateNativeSavepointAsync` rejected uncommitted managed-source XML (`SVN_SAVEPOINT_SOURCE_UNCOMMITTED`) and said in a comment that it is "intentionally independent of any task-scoped compare verdict". Nothing referenced a full scan, no test asserted such a gate, and the dock's Snapshot button only required a description. A change made only in TIA, outside every stage, was therefore invisible to both the Changes list and the savepoint.
- Comparisons are persisted per id under `.automation/comparisons/<id>.json` (`WorkbenchConsistencyService.Persist`) and carry `ComparedWorktreeId`, `ComparedTaskId`, `MasterSha` and `State`, which is what makes a "clean project-wide comparison at this commit" checkable.

## Constraints

- Keep the existing Git-status guard and its code unchanged; this adds a requirement, it does not replace one.
- A task-scoped comparison must never satisfy the gate, however clean it is.
- The refusal must be actionable: name the Full scan and what to do with what it reports.
- Do not make the savepoint itself run a compare: the design blocks and directs the user instead.

## Done when

1. A test proves the savepoint is refused with `SVN_SAVEPOINT_SCAN_REQUIRED` when no clean project-wide comparison exists at the commit, and that neither SVN nor Git advanced.
2. A test proves an equally clean but task-scoped comparison does not satisfy the gate.
3. The dock shows the refusal to the user.
4. `cd studio && npm test -- --run`, `npm run build`, `npm run lint`, `dotnet build AgentAssistPlcDev.sln` and the `Agent.Tests` / `ApiHost.Tests` suites pass, and the pre-existing E2E failure count does not grow.
5. Runtime check with `.\launch.ps1`: a Snapshot on a worktree without a clean Full scan is refused and says so.

## Evidence

Branch `codex/010-task-only-compare` (item 010's branch, continued). Nothing pushed; no PR.

### What changed

- `src/Agent/Workbench/WorkbenchConsistencyService.cs` — `FindCleanProjectWideComparison` returns a
  persisted comparison that is project-wide (`ComparedTaskId` null), covered the worktree whose TIA
  project it read, was taken at exactly the commit, and reported `Consistent`; otherwise null.
- `src/Agent/Workbench/WorkbenchCoordinator.cs` — `CreateNativeSavepointAsync` reads the master commit
  and refuses with `SVN_SAVEPOINT_SCAN_REQUIRED` when that lookup finds nothing. The existing
  Git-status guard stays first, and a feature worktree is excluded (see the limitation below).
- `tests/Agent.Tests/CombinedCommitTests.cs` — the fixture now seeds the clean project-wide
  comparison a real user gets from a Full scan (so the nine existing savepoint tests describe the
  real precondition), its `vc_log` queue accounts for the gate's HEAD read, and two tests prove the
  refusal: none at all, and an equally clean but task-scoped comparison.
- `studio/src/studio/version-control/VersionControlChanges.test.tsx` — proves the refusal reaches the
  user as an error that names the Full scan.

### Validation run

| Command | Observed result |
|---|---|
| `dotnet build AgentAssistPlcDev.sln -v q` | 0 errors |
| `dotnet test tests/Agent.Tests/Agent.Tests.csproj` | 518 passed (516 before, +2) |
| `dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj` | 227 passed |
| `dotnet test tests/E2E.Tests/E2E.Tests.csproj` | 14 failed, 2 passed — **identical to the base-commit baseline**, so the gate adds no E2E failure (those tests abort in workbench creation, before any savepoint) |
| `cd studio && npx vitest run src/studio/version-control` | 8 files, 98 passed (97 before, +1) |
| `cd studio && npm test -- --run` | 89 files, 604 passed |
| `cd studio && npm run build` / `npm run lint` | built; 0 errors, 16 warnings (none in a changed file) |

### Done when checks

1. **Passed.** `NativeSavepointRequiresAFullScanAtThisCommit`: with no comparison at all the savepoint
   is refused with `SVN_SAVEPOINT_SCAN_REQUIRED`, the message names the Full scan, and neither
   `svn_commit` nor a revision change happened.
2. **Passed.** `NativeSavepointRejectsATaskScopedComparisonAsItsGate`: the same refusal for a clean
   comparison that carries `ComparedTaskId`.
3. **Passed (component test).** The dock shows the refusal, including the "Full scan" wording.
4. **Passed** — see the table, including the unchanged E2E count.
5. **Partly verified live, and this is the one gap.** On the real workbench
   (`SWT2-PEI-N/master`, no clean project-wide comparison: all six stored comparisons report
   differences) a headless Snapshot click left the state untouched — SVN revision still 1,
   `engineering-state/revision.json` still dated 2026-10-03, no pending commit, no new Git commit —
   so nothing was mutated and the gate is consistent with that outcome. I did **not** capture the
   refusal message in the page: no toast appeared in the probe, while the title bar showed a
   TIA-related operation that may belong to the worktree-load connect rather than to the savepoint.
   A human with TIA attached should confirm the refusal text in one click. Screenshot:
   `tmp/vc-runtime/06-savepoint-refused.png`.

### Notes, decisions and risks

- **Deliberate narrowing:** the gate applies to the master worktree. A project-wide comparison is
  baselined on master's source tree (`CompareAsync` loads master's devices), so no persisted
  comparison certifies a *feature* worktree's own commit; requiring one there would block legitimate
  feature savepoints with a check that cannot prove anything. Feature savepoints keep the Git-status
  guard only, so the TIA-only-change gap remains open there — closing it needs a worktree-baselined
  full scan, which is a design change of its own.
- **Accepted as sufficient:** a comparison that passed the checksum fast gate counts as clean. The
  service only takes that path when every device's compiled checksum matches the evidence bound to
  the same commit, which is the same verdict with less work.
- **Not part of the gate:** hardware. AC-004 is about unresolved source objects, so a hardware
  difference does not block a savepoint; the savepoint records the native state either way.
- **Cost:** one extra `vc_log` read per savepoint, and the user must run a Full scan and keep it
  clean at the commit being recorded.
