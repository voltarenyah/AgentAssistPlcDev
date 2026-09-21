# Typical user workflow and interaction contract

This is the maintained interaction contract for Automation Workbench. It describes the normal path from finding a project to reviewing, validating, and preserving a PLC change. Use it when designing a surface, changing an interaction, or selecting browser-level automated tests.

It complements [PLC source workflow](plc-workflow.md), [device knowledge workflow](knowledge-workflow.md), and [version-control workflow](version-control-workflow.md). If a governing subsystem document differs, update this document to match it in the same change.

## Product promise

The user can organize parallel engineering states, inspect exported PLC content without opening TIA, and make changes deliberately. At a decision point, the app shows the selected workbench, worktree, device, source/knowledge freshness, and any pending approval.

```text
inspect or ask → stage/preview → select → explicit approval → validate → record history
```

No action silently writes to the managed TIA project. Browsing and grounded questions work offline. Creating engineering state, applying TIA differences, importing source into TIA, creating a savepoint, or discarding work requires an explicit user action or approval.

## Essential concepts

| Concept | User meaning | Interaction rule |
| --- | --- | --- |
| Workbench | One named engineering project and shared history | Top-level selection; contains worktrees. |
| Worktree | An independently editable engineering state, such as `master`, a fix, or commissioning work | Every edit, task, comparison, commit, and TIA action is worktree-scoped. |
| Device | A PLC in a worktree | Source browsing, knowledge, and device operations are device-scoped. A workbench can have multiple devices. |
| Offline source and knowledge | Exported source XML plus a device-owned graph | Usable without TIA. Show `current`, `stale`, `missing`, or `failed` before it is relied on. |
| Git commit | Readable semantic source history | Ordinary commits are Git-only; never present one as a native TIA snapshot. |
| SVN savepoint | Restorable, byte-exact native TIA state | Created only through **Create SVN savepoint** and linked by `engineering-state/revision.json`. |

## Typical workflow

### 1. Launch and orient

1. The user launches the app and first sees a loading page while the local runtime and the workbench catalog are initialized.
2. Within the normal local-startup target of 3–5 seconds, the main screen replaces the loading page. If initialization takes longer or fails, the loading state shows that work is continuing or presents a recoverable error; it does not appear frozen.
3. The main screen has three persistent regions:
   - The left dock is the project tree: workbenches, their worktrees, and their devices.
   - The main area is the home overview. It highlights recent ongoing projects, worktrees, tasks, and a **highlighted information** section. The exact content and ranking rules for highlighted information are intentionally **defined later**; its purpose is to help the user find current work and understand recent activity quickly.
   - The right dock is the **Workbench Assistant** chat. It remains available from the home overview so the user can ask for help creating a project or worktree, finding tasks or commits, and reading recent history without leaving the current context.
4. The user selects a workbench, then the intended worktree, before selecting a device or opening version control.

**Design acceptance:** loading visibly transitions to the main screen; each dock has its stated purpose; the home overview helps users resume work instead of requiring a tree search; the assistant remains available while navigating.

### 2. Create a workbench project

1. The user chooses **Create workbench** and enters the project name.
2. The user selects the TIA source-project attachment method:
   - When the desired project is already open in TIA, the user chooses its session from the session list.
   - Otherwise, the user opens the file browser and chooses the target `.ap17` project.
3. The user may choose a custom root for this workbench or use the dedicated control to change the system default project-root path. The app makes clear whether the chosen root applies only to this workbench or changes the future default.
4. The user can assign project tags during creation.
5. The user confirms **Create workbench**. A modal progress window remains visible while the app creates managed storage, imports the TIA project, exports initial source, and builds device knowledge. Each runtime step displays its status and elapsed time.
6. When creation succeeds, the progress modal closes and the app shows a confirmation message. The user returns to the home overview, where the newly created project is visible and ready for selection.

The imported origin is bootstrap-only thereafter; later operations use the managed project. For longer-running parallel work, the user creates a linked worktree from an eligible native savepoint, or a Git start point for Git-only workbenches. The new worktree does not change `master`.

**Design acceptance:** the user can complete creation from either a live TIA session or a file; root scope is unambiguous; tags are retained; progress is step-specific and timed; success reveals the new workbench without a manual refresh; failure identifies the failed step and preserves enough input to retry.

### 3. Orient and plan

1. The user may add or select a focused task: title, type, optional device, modification plan, and related source blocks.
2. The user can open **Workbench Assistant** with no selected worktree. It orients against the available worktrees and answers read-only questions.
3. For a mutation such as creating a worktree, the assistant proposes the operation, requests a base choice when necessary, and shows **Approve** and **Reject**. It does not silently alter selection.

**Design acceptance:** a read-only answer requires no approval card. A mutation proposal displays target and base context, then actionable approval/rejection controls; an old orientation answer never hides the pending proposal.

### Settings and UI component catalog

1. The user opens **Settings**, chooses **Appearance**, and selects **Open catalog** under **UI components**.
2. The app opens the component catalog without changing the selected workbench, worktree, or device.
3. The user browses the component groups and chooses **Back to settings** when finished.

**Design acceptance:** the catalog is reachable from Settings, displays its component navigation and previews, and returns to the same Settings page without changing engineering context.

### 4. Understand a device offline

1. The user selects a device and reads its identity, source counts, and knowledge state.
2. The user browses source objects, networks, tags, or cross-references and asks grounded PLC questions against that device graph.
3. After an edit batch, the user chooses **Update knowledge** before relying on graph context. The state clears from `stale` only after validated applied hashes. A full rebuild is for a full source-tree rebuild.

**Design acceptance:** knowledge status and last-update time are visible. The UI directs one update after an edit batch, not one per edit. Knowledge is never shared across devices or worktrees.

### 5. Compare with live TIA

1. The user chooses **Compare with TIA** from a device or worktree **Changes** page.
2. The app stages a managed-project export and compares it with the selected worktree source. It reports source, hardware, safety, or clean results inline and does not overwrite tracked source during comparison.
3. The user expands evidence and selects only intended rows. When safety block detail is unavailable, it remains descriptive and cannot produce synthetic selectable rows.
4. If the result is an untrackable native change, the app directs the user to a native snapshot rather than claiming source history fully represents it.

**Design acceptance:** comparisons stay on the current feature or master worktree. Selection persists while reviewing. A checksum match is not presented as clean if hardware differs or a full scan is required.

### 6. Apply, commit, and synchronize deliberately

| Goal | User interaction | Required outcome |
| --- | --- | --- |
| Accept TIA changes into source history | Select staged rows, enter a message, approve/apply | Only selected paths enter the current worktree; ordinary commit is Git-only. |
| Commit local source work | Select modified files and submit a message | Commit belongs to the current worktree, not native storage. |
| Import local source to TIA | Select eligible objects; on a feature review **Prepare feature import** and resolve conflicts | Only selected eligible objects import. The app compiles all devices and records validation before publishing a no-fast-forward feature merge. |
| Record native state | Enter a description and choose **Create SVN savepoint** | Managed TIA project saves, compiles successfully, freezes, commits to SVN, then writes linked `revision.json` in Git. |

An empty source commit is rejected unless the user explicitly marks an **Untrackable change**. That is a visible Git marker, not a replacement for a native savepoint.

**Design acceptance:** validate the commit message before the action; explain disabled imports; retain retry context after failure; never claim that an ordinary source commit created an SVN revision.

### 7. Review, recover, and finish

1. The user opens **History** to inspect a unified Git/SVN timeline. Expanded entries show author, time, files, validation, checksum, and linked native revision.
2. To inspect a historical native state, the user exports its savepoint. This creates a separate lean inspection copy and never replaces the live managed `tia/` copy.
3. To recover source, the user creates a rollback feature from selected historical files. The app does not reset `master`.
4. After the feature import/compile/validation path passes, the user merges it through the feature workflow and returns to the intended worktree.

**Design acceptance:** distinguish Git-only commits, validation evidence, untrackable markers, and savepoints. Recovery starts a feature or inspection copy, never silently rewinds active engineering state.

## Automated acceptance map

Use `./launch.ps1`, wait for health checks, then verify `http://localhost:5173/`, `http://localhost:5239/api/status`, and `http://localhost:8787/health` before browser tests. Check visible DOM after every meaningful action and inspect local-app console errors afterwards. Fixtures or deterministic sidecar responses are acceptable; do not create or approve a real engineering worktree merely to prove that an approval card renders.

| Scenario | Browser actions | Observable proof |
| --- | --- | --- |
| Startup and home orientation | Launch the app and wait for initialization | Loading state is visible; the main screen appears; project tree, home overview, and assistant dock render. |
| Create workbench from session | Open creation; name project; select an open TIA session; set tags/root; confirm | Step-specific timed progress is visible; success notice appears; the new project is present on the home overview. |
| Create workbench from file | Open creation; name project; select `.ap17` with the file browser; confirm | File attachment is visible before submit; success/failure identifies the resulting workbench or failed runtime step. |
| Component catalog | Open Settings; select Appearance; choose Open catalog; choose Back to settings | Component catalog renders; returning restores Settings without changing engineering context. |
| Assistant read-only | Open assistant with no worktree; wait for orientation; ask for history/todos | No error boundary or worktree-id error; worktrees listed; answer matches request. |
| Assistant mutation gate | Request worktree creation; choose base if prompted | Current **Approve**/**Reject** card renders; smoke test does not approve it. |
| Knowledge freshness | Open stale/missing/current device fixtures and update through supported path | State/timestamp and stale guidance visible; success clears stale only after API success. |
| TIA comparison | Trigger source, hardware, safety, and clean fixtures | Inline categories are clear; only supported rows selectable; nothing applies before selection and approval. |
| Commit/savepoint boundary | Use fixtures for commits, accepted TIA paths, savepoints | Ordinary commit does not claim native creation; savepoint requires description and exposes result. |
| Safe recovery | Inspect history/savepoint and start recovery | UI offers export/rollback feature; no destructive master reset. |

## Maintenance rules

- Update this file in the same change whenever a user-visible workflow, action name, confirmation point, state label, or testable outcome changes.
- Keep it outcome-oriented and link detailed persistence, transport, and TIA behavior to governing workflow documents rather than duplicating it.
- Preserve the definitions of **workbench**, **worktree**, **device**, **Git commit**, and **SVN savepoint** above.
- Add an acceptance scenario when a new interaction changes a safety boundary, state transition, or approval decision. Remove one only when the capability is removed.
- If implementation intentionally lags this contract, mark the exact step `Not implemented` with its owning issue/plan; do not silently represent it as complete.

## Source map for maintainers

- [Product layout and runtime](../README.md)
- [Native PLC lifecycle](plc-workflow.md)
- [Device knowledge lifecycle](knowledge-workflow.md)
- [Git/SVN, compare, validation, and recovery](version-control-workflow.md)
- User-facing code: `studio/src/studio/MainStudio.tsx`, `studio/src/studio/DeviceOverviewView.tsx`, `studio/src/studio/workbench/WorkbenchNavigator.tsx`, `studio/src/studio/appAssistant/AppAssistantPanel.tsx`, and `studio/src/studio/version-control/`.
