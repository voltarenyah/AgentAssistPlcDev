# Plan 1 PLC Version-Control Handover

Date: 2026-08-04  
Branch: `codex/plc-version-control`  
Linked worktree: `C:\Users\Ansel\orca\projects\AgentAssistPlcDev\.worktrees\plc-version-control`

## Stop condition

The user asked to stop immediately because their coding-token plan was running out, then resumed this task. Finish Plan 1 only, and stop and wait for confirmation before beginning Plan 2.

The worktree contains intentional unstaged and untracked Plan 1 changes. Do not reset, clean, checkout, or otherwise discard them.

## Product decisions already agreed

- Git tracks only PLC source XML under `devices/<device>/source/**/*.xml`.
- Runtime metadata, staging files, and knowledge databases are not PLC history.
- Every worktree has one `source/` tree; the exported/modified overlay model is removed.
- Master receives no ordinary local editor changes. Feature worktrees are the editable form of master.
- Refreshing from TIA writes user-approved XML changes but does not auto-stage or auto-commit them.
- Users select individual XML objects, enter a message, and commit manually.
- XML byte differences mark an object as touched, while user-visible diff summaries suppress only the known TIA export timestamp and distinguish editable text from protected logic/structure.
- Feature/TIA validation evidence is permanent and checksum/fingerprint based, stored by Task 4 as immutable annotated tags.
- After Plan 1 is complete, stop before Plan 2 and wait for the user.

## Committed work

The following checkpoints are complete on this branch:

1. `a25c4c2 refactor: establish one PLC source tree`
   - One `SourceRoot` per device/worktree.
   - Knowledge ingest/update works directly from manifest-free source XML.
   - Source editor is jailed to the exact device source root and edits in place.
   - Snapshot, prompt, Studio properties, API fixtures, and security were migrated away from overlay semantics.
   - This combines foundation Tasks 1 and 6 because they were coupled.

2. `b4367aa feat: restrict Git operations to PLC source XML`
   - Shared workbenches use `repository.git/info/exclude`; no generated tracked `.gitignore`.
   - Status, add, commit, and snapshot enforce the XML source boundary.
   - Explicit and bulk staging cannot commit hidden runtime/non-source files.
   - Allowed XML deletion remains supported.

3. `bb81786 feat: commit selected PLC source objects`
   - Manual selected-path commits, accurate file history, working-tree/ref diff modes, source-only restore, and semantic XML summaries.
   - TIA export timestamp noise is suppressed while protected logic/structure changes remain visible.

4. `b141355 refactor: keep workbench metadata outside PLC history`
   - Feature worktrees inherit runtime metadata from master without relying on Git-tracked metadata.
   - Refresh/bootstrap leave approved XML changes pending for manual commit.
   - Failed feature creation removes both the checkout and newly-created branch safely.
   - Runtime source manifests are excluded from Git cleanliness checks.

5. `8ecae94 feat: store immutable TIA validation evidence`
   - Immutable `tia-validation/<full-sha>` annotated tags, deterministic schema 1.0 evidence, malformed/invalid state handling, and validation-aware log entries.
   - `vc_validation_get` and `vc_validation_create` enforce exact commit targeting and current-HEAD creation.

Earlier documentation checkpoints:

- `bfb0abd docs: plan PLC version control implementation`
- `b1735fd docs: design PLC source version control`

## Current uncommitted patch

After the completed Task 3 and Task 5 commits, the only intentional uncommitted file is the handover itself:

```text
?? docs/superpowers/PLAN1_HANDOVER_2026-08-04.md
```

`git diff --check` was clean when work stopped.

### Task 3: selected commits, history, and XML diffs — complete

Implemented and committed in `bb81786`:

- `vc_commit_selected` and atomic selected-path commits.
- `VcCommitResult.Files`.
- Accurate changed-file history rather than tree-root names.
- Working-tree, one-ref, two-ref, and new-ref diff semantics.
- Missing-ref errors and XML path-policy enforcement.
- Source-only restore, including recursive restore enumeration.
- TIA `Document/DocumentInfo/Created` timestamp suppression.
- Line-oriented Myers diff with localized hunks and context.
- Semantic summaries for blocks, DB forms, UDTs, and tag tables.
- Protected multilingual structure, stable owner identity, deterministic output, and malformed-XML safety.
- Exact Git index bytes are restored if selected staging/commit fails.

Final green evidence:

- focused header regressions: 3/3 passed;
- `Contracts.Tests`: 97/97 passed;
- `Mcp.VersionControl.Tests`: 81/81 passed;
- `git diff --check`: clean.

### Task 5: clean workbenches, feature metadata, and manual refresh — complete

Implemented and committed in `b141355`:

- Feature creation reads master `worktree.json` and every `device.json` before creating the linked checkout.
- Device IDs, order, project metadata, checksums, and knowledge state are inherited.
- Feature metadata gets the new worktree ID.
- Feature `source/` and `staging/` folders are created.
- Refresh/bootstrap no longer calls `vc_add` or `vc_commit`.
- Refresh states are now `Rejected`, `NoChanges`, and `FilesUpdated`.
- Successful refresh/bootstrap leaves XML unstaged for manual selected commit.
- `LastReconciliationCommit` is no longer written.
- Bootstrap still accepts legacy `CommitMessage` in the HTTP request but intentionally ignores it.

Final green evidence:

- `Mcp.VersionControl.Tests`: 84/84 passed.
- `Agent.Tests`: 218/218 passed.
- `ApiHost.Tests`: 99/99 passed.
- `E2E.Tests`: 6/6 passed, including real clean-status and failed-creation rollback checks.
- `git diff --check`: clean.

The former review findings are resolved: rollback protects master and unrelated branches, the real E2E boundary checks runtime artifacts, and the bootstrap API compatibility comment is consolidated.

After fixing these findings, rerun Agent, API, relevant E2E, and version-control tests, re-review, and commit Task 5 separately. Suggested message: `refactor: keep workbench metadata outside PLC history`.

## Plan 1 task status

| Task | Status | Notes |
|---|---|---|
| 1. One PLC source tree | Complete/committed | Included in `a25c4c2`. |
| 2. Internal excludes and XML-only paths | Complete/committed | Included in `b4367aa`. |
| 3. Selected commits/history/XML summaries | Complete/committed | `bb81786`; all focused and full VC/contracts tests pass. |
| 4. Immutable validation tags | Complete/committed | `8ecae94`; focused validation tests and full VC suite pass. |
| 5. Clean workbenches/feature metadata | Complete/committed | `b141355`; full Agent/API/VC/E2E verification passes. |
| 6. Knowledge/editor single source | Complete/committed | Combined with Task 1 in `a25c4c2`. |
| 7. Master write policy and worktree VC APIs | Complete/committed | `eaeaa0f`; policy, worktree routes, recovery endpoints, Studio selected-path calls, and workflow docs are implemented and verified. |

## Recommended continuation order

1. Plan 1 is complete. Stop here and wait for user confirmation.
2. Do not begin Plan 2 automatically.

## Detailed execution plans

The complete step-by-step plans are already in the repository:

- Plan 1, repository foundation: `docs/superpowers/plans/2026-08-04-plc-source-repository-foundation.md`
- Plan 2, TIA consistency and selective sync: `docs/superpowers/plans/2026-08-04-tia-consistency-import.md`
- Plan 3, validated feature import/merge/rollback: `docs/superpowers/plans/2026-08-04-validated-worktree-merge.md`
- Plan 4, version-control workspace UI: `docs/superpowers/plans/2026-08-04-version-control-workspace-ui.md`

The original design rationale is in `docs/superpowers/specs/2026-08-03-plc-version-control-design.md` (commit `b1735fd`).

## Verification commands for the next agent

Run from the linked worktree root:

```powershell
git status --short
git diff --check
dotnet test tests/Contracts.Tests/Contracts.Tests.csproj --no-restore
dotnet test tests/Mcp.VersionControl.Tests/Mcp.VersionControl.Tests.csproj --no-restore
dotnet test tests/Agent.Tests/Agent.Tests.csproj --no-restore
dotnet test tests/ApiHost.Tests/ApiHost.Tests.csproj --no-restore
dotnet test tests/E2E.Tests/E2E.Tests.csproj --no-restore
```

At the final Plan 1 gate, additionally run:

```powershell
dotnet test AgentAssistPlcDev.sln --no-restore --verbosity minimal
Set-Location studio
npm test
npm run build
```

Known non-blocking baseline warnings before this handover included an existing nullable warning in `ProjectMetadata.cs`, an xUnit analyzer warning in `TagAndUdtImportTests.cs`, expected Studio test connection-refused logs for localhost fixtures, and the existing large-chunk build advisory.

Latest Plan 1 verification before the final Task 7 checkpoint:

- Full .NET solution: 760 tests passed.
- API worktree VC routing test: 1/1 passed.
- Unauthorized master recovery E2E test: 1/1 passed.
- Studio tests: 117/117 passed.
- Studio production build: passed.
- `git diff --check`: clean.

Final Plan 1 commits: `a25c4c2`, `b4367aa`, `bb81786`, `b141355`, `8ecae94`, `eaeaa0f`.
