# Unattended run report: the six queued prompts

Run date: 2026-10-03 (overnight). Base commit: `e8270bd` (master). Orchestrator: the DSH agent session
that owns this queue.

This report is written by the orchestrator, not by the item sessions. Every number below was either
re-run by the orchestrator in the item's own worktree after its session stopped, or is quoted from the
item's Evidence and explicitly marked as reported. Items were complete on their own branch; nothing was
pushed and no pull request was opened. At the maintainer's request, items 001 and 006 were then merged
into master (`ee1036e`, `4d931bf`); that merge surfaced one cross-item test interaction, recorded under
"Cross-branch composition" below and fixed in `567b633`.

## How this run was organised

- One item per branch, one branch per git worktree under `.worktrees/`, so no two sessions ever wrote
  in the same tree.
- Dependency order honoured: item 002 first (it establishes the stage baseline the others build on),
  then 003/004/005 based on 002's branch tip, and 006 from master alongside 001.
- Default cap of three concurrently active sessions was kept; a fourth was never started, even when a
  slot was free, because the remaining work was write-heavy.
- `studio/node_modules` in each worktree is a junction to the primary checkout, so no session
  re-installed dependencies and none added one.
- Runtime, TIA and browser checks were deferred for every item by the run's instruction. Each item's
  Evidence names the skipped check and its residual risk; this report repeats the ones that matter.
- Baseline measured on master before any change, so a failure can be attributed rather than guessed:
  frontend `npm test` 83 files / 531 tests green; `ApiHost.Tests` 177 green; `Agent.Tests` 453 green;
  `Mcp.Knowledge.Tests` 168 green; `Mcp.VersionControl.Tests` 158 green; `tests/E2E.Tests` **already
  failing 14 of 16** (`InvalidOperationException : close_session` — the E2E fake has no such case);
  `dotnet build` of the solution fails while the development ApiHost is running, because it locks
  `src/Agent/bin/Debug/net8.0/Agent.dll`.

## Verification method

For each finished item the orchestrator, after the item's session stopped:

1. read the complete diff and the item's Evidence;
2. re-ran the item's own lanes in the item's worktree and compared the counts with the item's report;
3. where the item's central claim could be vacuous, ran a discrimination check — reverted the fixed
   file to its pre-fix state and confirmed the new test turns red;
4. confirmed the item's `Status` and `Evidence` were actually written, and that the primary checkout
   stayed untouched.

Both discrimination checks performed are recorded under items 001 and 006. Two claims were verified
against the code rather than trusted: that `vc_show_file` with a null commit reads HEAD's blob (item
002) and that an ordinary source commit does not carry the source manifest (item 002's residual risk).

---

## 001 — Remove every right-dock auto-expand

| | |
|---|---|
| Branch | `codex/001-right-dock-auto-expand` |
| Commit | `c4168cd` `fix: remove right-dock auto-expand (001)` |
| Files | `studio/src/studio/MainStudio.tsx` (−4 lines), `studio/src/studio/MainStudio.rightDock.test.tsx` (new, 197 lines), item + README |
| Status | `done` |

**What changed.** The forced `setShellLayout(... rightOpen: true ...)` and its stale comment are gone
from `selectWorktree`, and the forced open is gone from `createChatSessionFromEmptyState`, which now
only creates the session. The dock toggle, the resize handlers, the settings reset-layout action, the
storage key/shape and `DEFAULT_SHELL_LAYOUT` are untouched.

**Evidence.** Orchestrator re-run: frontend **84 files / 535 tests green**; `npm run build` clean;
worktree clean. The count matches the reported one and cross-checks against the baseline
(531 + 4 new cases = 535). **Discrimination check**: with master's `MainStudio.tsx` swapped back in,
exactly the two collapsed-dock cases fail with `expected 'open' to be 'closed'` — the test reproduces
both forced opens and is not vacuous.

**Deferred / risk.** The `.\launch.ps1` browser reload pass was not run. No production code path that
stores or restores `rightOpen` changed, so the unexercised part is the browser round trip itself.

**Human note.** `DEFAULT_SHELL_LAYOUT.rightOpen` is still `true`, so a first-run browser and the
settings reset-layout action still show the right dock. That is the default state, not an auto-expand,
and it was deliberately left alone. Say the word and it is a one-line change.

---

## 002 — Task page: editable staged source objects with a Git-bound baseline

| | |
|---|---|
| Branch | `codex/002-task-stage-baseline` |
| Commits | `09becc0` backend, `d2260eb` frontend, `27b0f7d` docs |
| Files | 16, of which new: `CommittedSourceManifest.cs`, `TaskStageBaselineTests.cs`, `CommittedSourceManifestTests.cs`, `TaskSourceObjectsSection.tsx` + its test |
| Status | `done` |

**What changed.** This item closes the execution layer of the already-accepted ADR-0003.

- The stage route now derives the baseline itself: `WorkbenchCoordinator.StageTaskSourceObjectAsync`
  reads the per-device export manifest **at Git HEAD** through `vc_show_file` with `commitSha: null`,
  builds `ManagedSourceEvidenceObject` from it, and stores it. A client-supplied baseline is accepted
  for wire compatibility and **ignored**, documented on the DTO, so a live-TIA value can never become
  task evidence. An object the committed manifest does not list keeps a null baseline, which
  `TASK_STAGE_BASELINE_MISSING` reports — with a corrected message that names the real remedy instead
  of the "full scan and assign" flow that does not exist anywhere in the code.
- New `GET …/worktrees/{wt}/source-stages` returns the worktree's active stages with the owning task
  title, so a picker can show who owns an object before a take-over.
- The task page gains an editable **Source objects** section (active stages) with a `CommandDialog`
  search/type-filter picker reusing `plcSourceState`'s pure helpers, release on remove, and explicit
  take-over (release-then-stage). Its rows state a missing baseline rather than implying the object is
  comparable. The remaining traceability edges moved into a **Related records** group, so no two
  sections carry the same name or meaning, and **Commits** was left as it is for item 004.

**Evidence.** Orchestrator re-run: frontend **84 files / 539 tests green**; `Agent.Tests` **465
passed**; `ApiHost.Tests` **177 passed**; `npm run build` clean; worktree clean. The counts
cross-check: 531 + 8 = 539, 453 + 12 = 465, and the ApiHost lane is unchanged from the baseline.

The load-bearing claim was verified in the code: `RepositoryService.ShowFile` resolves a null commit
to `repo.Head.Tip` and returns the blob's text, so the baseline really is committed content, not the
working tree.

**Deferred / risk.** No runtime pass; no TIA, so a live task compare was never exercised.

**Residual risks, and one that needs your judgement.** An ordinary source commit does **not** carry
the source manifest: `CommitSourceAsync` commits only the selected source paths
(`WorkbenchCoordinator.cs:3443-3449`), while the savepoint appends every manifest
(`:3828` via `BuildCombinedCommitPaths`) and a safety change refreshes and adds them (`:3440-3448`).
A baseline read from the manifest at HEAD can therefore describe the last export, safety refresh or
savepoint rather than the immediately preceding XML commit. The failure direction is safe — it reports
a difference that is not there, rather than hiding one — and the after-commit writer still refreshes a
committing task's own stage. If you want the manifest in every task commit, that is a separate,
deliberate change to the commit path, not a detail of this item.

---

## 003 — Compare task mode in the version-control surface

| | |
|---|---|
| Branch | `codex/003-compare-task-mode` |
| Commits | `3010877` implementation, `ec665f5` docs, `4d3c489` correction, `840fc4b` docs |
| Files | `VersionControlCompare.tsx`, `VersionControlPanel.tsx`, `VersionControlChanges.tsx`, three test files |
| Status | `done` |

**What changed.** The version-control surface distinguishes **Full scan** (default, unchanged) from
**Compare task**, which calls the existing `compareTaskWithTia` and renders only that task's result.
A clean result says `This task is in sync` and carries no project-clean wording; the task comparison
never feeds the project commit selection and never produces a savepoint. Every backend problem code
gets an explanatory state, and the mode is unavailable with a stated reason when there is no active
task or the task has no staged objects.

**Correction the orchestrator required.** The active task is now read from the worktree's server-side
active task (`GET …/active-task`, the `ActiveTaskContextService` value `MainStudio` already maintains
when a task is selected), not from the open task detail — the item's Context named that service, and
the open-detail source left the mode unavailable for a task that was active server-side. The panel
reads it only in Compare task mode, and `MainStudio.tsx` is back to its base state, so this branch no
longer touches the file the other branches edit.

**Evidence.** Orchestrator re-run: frontend **83 files / 550 tests green**; `npm run build` clean;
worktree clean. The route assertion is the item's central proof and lives in
`VersionControlWorkflow.test.tsx:74-75`: the request path **contains**
`/workbenches/wb-1/worktrees/wt-1/tasks/task-1/compare-tia` and **does not contain** `/vc/compare-tia`.

**Deferred / risk.** No live task compare (no TIA). Remaining risk: switching tasks while already in
Compare task mode and comparing without a refresh compares the previously active task; the result
heading names the task that was compared, and re-selecting the mode re-reads it.

---

## 004 — Task page Commits section: which source objects each commit touched

| | |
|---|---|
| Branch | `codex/004-task-commit-blocks` |
| Commits | `eddd0aa` backend read, `c5d8e23` task page section, `14c8acf` docs |
| Files | `EngineeringGraphApi.cs`, `WorkbenchApiModels.cs`, `WorkbenchEndpointsTests.cs`, `client.ts`, `TaskCommitsSection.tsx` + its test (new), `TaskDetail.tsx` + its test, `WorktreeVersionControlTimeline.test.tsx` |
| Status | `done` |

**What changed.** The read side is extended additively:
`EngineeringGraphEntityDetailApiResponse` gains `SourceObjects` and `UnresolvedFiles`, filled **only**
for a `git_commit` entity, from the two reads that already exist
(`GetEdges(GitCommit, id, SourceObject)` and `GetFileEvidence(id)`); `Tasks` and `Commits` keep their
exact meaning, and the new fields are empty for every other entity kind so a task's own stage edges
cannot be mistaken for commit evidence. Its endpoint test asserts both directions, including the empty
case. The task page's new `TaskCommitsSection` renders the task's commits as a disclosure (not a link
to nowhere), names the touched source objects with their category, states the unresolved-file count,
and states plainly when a commit resolved to no source object.

**Orchestrator observation, not a blocker.** The section loads a device snapshot per device on mount to
resolve object names, even when no commit is expanded. Device snapshots are the heavy call in this
codebase; fetching them on first expand instead would remove that cost for anyone who never expands a
commit. Recommended as a follow-up, not as a change to this branch.

**Evidence.** Orchestrator re-run: frontend **85 files / 548 tests green** (`539 + 9` new);
`ApiHost.Tests` **178 passed** (`177 + 1`); `npm run build` clean; worktree clean. Both counts match
the item's report and cross-check against the baseline. Its new endpoint test asserts the additive
contract from both sides — for a commit entity the pre-existing `tasks` field still reports the task
while `commits` is empty, the two new fields carry the touched objects (`provenance: evidence`) and the
unresolved paths in order, and for a source-object entity both new fields are empty.

**Deferred / risk.** No runtime pass. Two limitations the item names rather than hides: message, author
and timestamp come from the worktree's Git log, which is capped at 100 entries, so an older task commit
renders its short hash plus an explicit "Message unavailable" note and is never backfilled — closing
that needs a per-SHA commit-metadata read that does not exist today; and name/category come from the
devices' current manifests, so an object no longer listed renders its `{deviceId}:{sourceId}` id with
"Category unavailable" instead of a guessed name, while the touched set itself still comes from the
persisted edges. **Decision for you**: whether the 100-commit window is acceptable or needs its own
item.

---

## 005 — Agent stages source objects for the current task through an approval card

| | |
|---|---|
| Branch | `codex/005-agent-stage-approval` |
| Commits | `db0c671` implementation, `0959302` docs |
| Files | `TaskSourceStagingTool.cs` (new), `CompatibilityEndpoints.cs`, `SandboxPolicy.cs`, `AgentSandbox.cs`, `MainStudio.tsx`, `TaskDetail.tsx`, `TaskSourceObjectsSection.tsx` + its test, `TaskSourceStagingToolTests.cs` (new), `MainStudio.stageApproval.test.tsx` (new) |
| Status | `done` |

**What has been reviewed so far.** The tool is registered in-process in ApiHost (the workbench graph
and the guarded stage path live there; the engineering MCP server cannot reach them), classified
`Destructive` in `SandboxPolicy.Defaults` — unclassified tools are refused fail-closed — and added to
the small list of tools whose **full** arguments reach the approval card, so the user sees every object
and the owner a take-over would release instead of a 160-character summary. `TaskSourceStagingTool`
resolves the active task from `ActiveTaskContextService`, refuses a conversation whose device differs
from the task's device, requires a take-over to name the live owner (matching id or title) and reports
the real owner otherwise, validates every requested object before the first write so an approved call
stages exactly what was approved, and reuses `StageTaskSourceObjectAsync` so the baseline rule stays in
one place.

**Orchestrator decision recorded here.** The item stays device-chat only. Registering the same tool in
the Workbench Assistant panel would let one test hold the card and the task page in one DOM, but it
exposes a new surface, which is your product decision to make rather than this item's to assume. It is
worth considering as a follow-up.

**Evidence.** Orchestrator re-run: frontend **85 files / 542 tests green** (`539 + 3` new on this
base); `ApiHost.Tests` **182 passed** (`177 + 5`); `Agent.Tests` **465 passed** (unchanged);
`Contracts.Tests` **112 passed** (the tier table changed, so this lane had to move); solution build
**0 errors**; `npm run build` clean; worktree clean. Every count matches the item's report.

**Deferred / risk.** Done-when 3 is proved in both halves rather than as one DOM: the card renders for
this call in the device conversation and approving answers it through `confirmTool(id, 'allowOnce')`,
and the section's token-driven reload is proved at component level. A single-DOM proof is impossible
here because `MainStudio` renders the task detail in place of the workspace host, and the section also
re-reads on mount — recorded in the item's Evidence. Done-when 5 was skipped: no `launch.ps1`, no live
model, no TIA, so the live model → card → confirm round trip still needs one runtime pass.

---

## 006 — Remove the right dock's AI sessions page and move its operations into the navigator

| | |
|---|---|
| Branch | `codex/006-remove-ai-sessions-dock` |
| Commits | `b9e56c1` code, `b6fd78d` decision docs, `8dd8fb5` integration tests, `17657e2` item + evidence |
| Files | 15, including the deletion of `SessionDock.tsx` and its test |
| Status | `done` |

**What changed.** The `sessions` content kind is gone, so a device on a chat, source, inspector or
stale focus resolves to no dock at all — neither the panel nor its resize handle renders. The
navigator's conversation row menu gains export and task binding (a picker over the worktree's
device-bound tasks, never a prompt for a raw id) plus remove-task, and the `SESSIONS` header action
now starts a conversation in the scope the list is showing. Delete keeps the ADR-0010 route and its
confirmation. ADR-0009, the navigator-sessions design document and the IA UI-spec were amended in the
same change, including an update-history table and a new AC-019.

**Evidence.** Orchestrator re-run: frontend **82 files / 529 tests green**; `npm run build` clean;
`SessionDock` has no remaining references; worktree clean. **Discrimination check**: with master's
`contextDock.ts` swapped back in, the new no-dock test fails
(`expected { visible: true, …} to deeply equal { visible: false, …}`), so it is not vacuous.

Its six-operation checklist maps every operation the removed page offered to its new entry point and
the test proving it is still reachable, and its Evidence records why two other test files had to
change (they started their conversation from the retired dock's button and now use the chat empty
state — the same behaviour, proven from the new entry point).

**Deferred / risk.** No browser pass: the width reclamation after the dock disappears, the focus
handoff from the row menu to the picker dialog, and the absence of console errors are unproven. Error
branches of the two reworked `MainStudio` handlers stay untested, as they were before.

**Documentation nit.** Its Evidence explains the suite moving 531 → 529 as
"−5 retired cases, −1 redundant case, +4 navigator, +1 MainStudio", which adds to 530; the measured
number is 529, confirmed independently. The observation is right, the arithmetic in the sentence is off
by one; no code effect.

---

## Cross-branch composition

A scratch branch was built off master and all six item branches were merged into it one at a time with
`--no-ff`, then the scratch worktree and branch were removed. Result:

| Branch merged | Result |
|---|---|
| `codex/001-right-dock-auto-expand` | clean |
| `codex/006-remove-ai-sessions-dock` | clean |
| `codex/002-task-stage-baseline` | conflicts **only** on `docs/agent-prompts/README.md` |
| `codex/003-compare-task-mode` | clean |
| `codex/004-task-commit-blocks` | conflicts **only** on `docs/agent-prompts/README.md` |
| `codex/005-agent-stage-approval` | clean |

Six branches that all touch `MainStudio.tsx`, `TaskDetail.tsx`, `client.ts`, the graph API, the sandbox
policy and the navigator produce **no code conflict**. In the real sequential merge onto master, every
one of 002, 003, 004 and 005 conflicted on `docs/agent-prompts/README.md` and on nothing else — a branch
cut from an older README updates its own row while master had already taken another branch's rows. The
resolution was identical each time: keep master's paragraphs and table, take the branch's own row, and
set that row to `done`. 001 and 006 merged with no conflict at all. After all six, the index reads
`done` for every item.

### The interaction the merge check could not see

The textual merge of 001 and 006 was clean, but running master's suite after merging them found **two
failing tests** — `MainStudio.rightDock.test.tsx`, the two chat-empty-state cases, with
`expected undefined to be 'closed' / 'open'`. The cause is a semantic overlap, not a conflict: 001
asserted the right dock's `data-dock-state`, while 006 (as its item required) makes a device on a chat
view render **no right dock at all**, so the attribute no longer exists to assert on. Both branches were
green on their own; only the combined tree was red. A clean `git merge` cannot detect this.

Fix (`567b633`): the two cases now assert the state 001 is actually about — the **persisted
`shellLayout.rightOpen`** in `plc-studio.shell-layout.v1` — plus the merged design (no
`[data-dock="right"]` on the chat view). It is stricter, not weaker: re-injecting the forced open into
`createChatSessionFromEmptyState` turns the collapsed case red with `expected true to be false`, so the
case still catches exactly the regression item 001 fixed. Master's suite is 83 files / **533 tests
green** after the fix.

The lesson worth carrying: a per-branch green suite plus a clean merge is not evidence that the merged
system works. Run the suite on the merged tree before believing the branches compose.

### The same interaction class, a second time

Merging 002–005 surfaced one further failure of exactly the same shape, again caused by 006 and again
**in a test, not in product behaviour**: `MainStudio.stageApproval.test.tsx` (item 005) started its
conversation by clicking `aria-label="New session"` — the button of the AI sessions page that 006
retired — so on the merged tree the element no longer existed and the helper crashed on `null` before
naming what it was looking for. Fixed in `142619b` by starting the conversation from the chat surface's
own empty state (`aria-label="Create new chat session"`), which is where 006 moved that entry point, and
by tightening the helper's assertion from `toBeDefined()` to `not.toBeNull()` — `null` satisfies
`toBeDefined()`, which is what turned "element missing" into a confusing `dispatchEvent` TypeError.

Both interactions were the same kind: **a branch whose tests used a UI surface as a fixture, written
before another branch deleted that surface.** Neither was a product defect, and neither would have been
caught by reviewing the diffs — only by running the merged suite. When one item retires a surface,
expect the other items' tests to be its hidden consumers.

### Merged-tree verification (all six merged)

Run on master after the six merges and the two test fixes above:

| Lane | Result |
|---|---|
| `cd studio && npm test` | **86 files / 572 tests green** |
| `cd studio && npm run build` | `tsc -b` + `vite build` clean |
| `dotnet build AgentAssistPlcDev.sln -v q` | 0 errors (8 pre-existing analyzer warnings) |
| `dotnet test tests/ApiHost.Tests` | **183 passed** (177 baseline + 1 from 004 + 5 from 005) |
| `dotnet test tests/Agent.Tests` | **465 passed** (453 baseline + 12 from 002) |
| `dotnet test tests/Contracts.Tests` | **112 passed** |

`Agent.dll` is locked while the development ApiHost runs, so the .NET lane was built after stopping it
and the services were then restarted with `.\launch.ps1 -NoBuild`; both are healthy (`http://localhost:5173/`
and `http://localhost:5239/api/status` return 200) and the dev server serves the new modules
(`TaskSourceObjectsSection.tsx`, `TaskCommitsSection.tsx`).

## A defect manual acceptance found, and its fix

### A stage row can say the wrong reason for a missing baseline

The task page tells a staged object with no baseline: "No committed Git content yet, so this object has
no fingerprint baseline. Commit it once; comparison reports it until then." For part of the catalogue
that advice is wrong. Measured on the maintainer's live workbench (`Fix PEI Import Error`, device
`Sino_PEI`): the committed manifest lists **1,314** components, of which **540 are instance DBs**, and
`ManagedSourceEvidenceKind.InstanceDb` is excluded from managed-source evidence and comparison by
design, so `CommittedSourceManifest` returns no evidence for them and the stage baseline is null by
construction. Their own `task_source_stages` history agrees: of 10 rows, the only two with a null
baseline are `PC_Clock` and `G_Automatic_Director_Cav_A` — both `DB/InstanceDB`, while every OB, FB, FC
and UDT row carries a baseline.

Both consequences are now handled (`15f015e`). The server reports the object's fingerprint-comparison
kind (`SourceObjectInfo.EvidenceKind`, from `CommittedSourceManifest.EvidenceKindOf`), and the row says
which case it is: an instance DB reads "Instance DBs are excluded from fingerprint comparison, so this
object has no baseline and committing it will not create one" instead of the false advice to commit it.
Instance DBs stay stageable, because the stage list is also the commit whitelist that
`TASK_COMMIT_STAGE_MISMATCH` enforces — hiding them from the picker would make their changes
uncommittable.

### The picker no longer pays for the whole device snapshot

The section read `GET …/devices/{device}` for its candidate list. That reader also crawls every block on
disk (`DeviceSnapshotReader.Read` → `ReadBlocks`), which on the maintainer's 1,314-object export cost
**825–1,869 ms**, while none of that work is shown in the picker. The new
`GET …/devices/{device}/source-objects` returns the same 1,314 objects from the manifest alone:
**52–128 ms**, and it carries the `evidenceKind` the row explanation needs. The section calls it instead
(`listDeviceSourceObjects`), and fails and retries as before when the list cannot be read.

### A stage change no longer blanks the page

`onStagesChanged` — and the approved-agent-call path — went through `openTaskDetail`, which clears the
detail and raises the loading flag, so every refresh replaced the page with "Loading task details…",
flashed the view and dropped the reader's scroll position. `reloadTaskDetail` now refreshes **in place**
(`51700e5`): it fetches through the extracted `loadTaskDetail`, replaces the detail when it arrives, and
keeps what is on screen if the refresh fails. A test renders the task page, triggers a refresh whose
fetch hangs, and asserts the detail is still there and the loading text never appears.

### Instance DBs are no longer offered as a task basis

They were 540 of the maintainer's 1,314 candidates, and not one of them can ever carry a baseline. The
picker's endpoint now drops them (`ReadSourceObjects(context, comparableOnly: true)` →
`ComparableSourceObjects`), so the list is 774 objects read in 118 ms, while the device snapshot — the
source browser, which should show what is on disk — still lists all 1,314. The row explanation stays,
because a stage taken before this change still shows up in the task's own stage list.

Checking the premise against the repository found it only half implemented. The compare design
(`docs/design/tia-compare-fingerprint-first-design.md`) says instance DBs are "excluded from evidence
snapshots, **XML export**, XML comparison, and deletion detection", and its table says "never export or
diff"; ADR-0001 calls them "deliberately unmanaged native elements". The evidence half is real —
`SourceEvidencePlanner` filters them and `ValidationTagStore` refuses them in schema v2 source evidence.
The export half is not: `git ls-files` matches **537 of the 540** instance DBs in the maintainer's
worktree, so they are exported and committed today. Whether to stop exporting them, and what to do
about the 537 already tracked, changes what a baseline contains and is therefore a product decision
rather than a defect fix.

### The database lock that froze the page

**Symptom, after the six merges.** Adding a source object on the task page froze the page for about a
minute and then returned a 500: `Microsoft.Data.Sqlite.SqliteException: SQLite Error 5: 'database is
locked'` at `EngineeringGraphService.RegisterEntity`, reached from the stage route.

**Root cause, proven by a test rather than inferred.** `EngineeringGraphStore`'s constructor opened the
schema-migration transaction unconditionally, and `SqliteConnection.BeginTransaction()` issues
`BEGIN IMMEDIATE` — so **every** graph scope took the writer lock at open, even for a pure read with the
schema already current. The task page opens several scopes per interaction (its stage listings plus the
stage write), so concurrent scopes collided, and the driver retried a blocked `BEGIN IMMEDIATE` until
its command timeout. That is the ~1 minute: the driver's retry window, not a slow query.

**Fix (`4fd75d7`, two files).** The store opens the migration transaction only when the schema is
actually behind, keeping the "newer than supported" guard as a cheap check instead; and it sets
`PRAGMA busy_timeout = 5000` so a blocked statement waits briefly instead of failing. WAL was
considered and deliberately rejected: it would change the workbench's on-disk file set and would make
`FailedMigrationLeavesPriorSchemaAndDataReadable`'s byte-identity assertion false.

**Regression test.** `OpeningASecondStoreTakesNoWriterLockWhileTheSchemaIsCurrent` holds a write
transaction on one store and opens a second, which is the shape of the failure. With the old behaviour
injected it fails after ~34 s with `database is locked` at `EngineeringGraphStore..ctor`; with the fix
the whole class runs in ~165 ms. `AppliesABusyTimeoutSoABlockedStatementWaitsInsteadOfFailing` pins the
timeout. Verified after the fix: solution build 0 errors; `Agent.Tests` 467; `ApiHost.Tests` 183;
`Contracts.Tests` 112.

**Two things worth carrying forward.**

- The stage route registers the **whole device manifest** on every stage call. Narrowing it to the
  requested object was written during diagnosis and then **reverted on purpose**: the loop is also what
  makes a source-object entity exist for the source panel's traceability read, so removing it changes
  visible behaviour. Instead the manifest is now registered in **one transaction**
  (`EngineeringGraphService.RegisterEntities`, `2e7a696`), which keeps every registration and removes
  the per-object autocommit. Measured on the live workbench, whose manifest holds **1,314** components:
  a stage click went from **9.7 s to 0.16 s** (first call 0.53 s), and the .NET lanes stayed green
  (Agent.Tests 467, ApiHost.Tests 183).
- The device snapshot the task page loads for its candidate list still costs about **1.9 s** on a
  manifest of that size; sourcing the picker from a lighter manifest read is the next obvious step.
- The first verification of the fix looked like a failure. It was not: restoring the source file with
  `Copy-Item` preserves the *backup's* timestamp, so MSBuild judged the restored source older than the
  assembly built from the patched source and skipped recompiling — the test ran the old binary. Force a
  rebuild (`--no-incremental`) whenever a file is restored that way, or you will draw the wrong
  conclusion about your own fix.

## Recommended review order

1. `codex/001-right-dock-auto-expand` and `codex/006-remove-ai-sessions-dock` — independent, small, and
   both already verified end to end at the component level.
2. `codex/002-task-stage-baseline` — the foundation of the task-scoped compare story.
3. `codex/003-compare-task-mode`, then `codex/004-task-commit-blocks`, then
   `codex/005-agent-stage-approval` — each is based on 002's tip, so their diffs include 002 until it
   lands.
4. Run `.\launch.ps1` and do the runtime passes the run deferred: collapse the right dock and select
   another worktree; add a source object to a worktree task and confirm it appears with a baseline;
   run Compare task and confirm it reports only that task; expand a commit and confirm the blocks it
   touched; ask the agent in a device conversation to stage a block and approve the card.

## Follow-ups recommended, not implemented

- **`tests/E2E.Tests` cannot run in this environment**: 14 of 16 cases fail before any change with
  `InvalidOperationException : close_session`, because the E2E `EngineeringBoundary` fake has no such
  case (item 002 traced it to `WorkbenchCoordinator.cs:682-686`, outside its diff). Worth its own item.
- **Ordinary source commits do not carry the source manifest** (verified above). Decide whether the
  stage baseline should also be refreshable from the commit path rather than only from the
  export/safety/savepoint paths.
- **`TaskCommitsSection` loads device snapshots eagerly** (item 004). Deferring them to first expand
  would remove the cost for a user who never expands a commit.
- **Item 005's tool is device-chat only.** Offering it in the Workbench Assistant panel is a product
  decision.

## Environment notes for the next run

- `pwsh`-spawned processes could not write to the workspace for part of this session (git commits and
  builds were refused at the OS level) until the file policy was widened; the file tools were never
  affected.
- The development ApiHost holds a lock on `src/Agent/bin/Debug/net8.0/Agent.dll`, so a solution build
  in the primary checkout fails until the launcher is stopped. Every item's `.NET` lane names
  `tests/ApiHost.Tests`, which is unaffected.
