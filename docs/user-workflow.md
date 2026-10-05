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
| Hardware target | The one hardware configuration of a worktree | The worktree's other task target. A hardware task binds no PLC device and is not device-scoped. |
| Offline source and knowledge | Exported source XML plus a device-owned graph | Usable without TIA. Show `current`, `stale`, `missing`, or `failed` before it is relied on. |
| Git commit | Readable semantic source history | Ordinary commits are Git-only; never present one as a native TIA snapshot. |
| SVN savepoint | Restorable, byte-exact native TIA state | Created only through **Create SVN savepoint** and linked by `engineering-state/revision.json`. |

## Typical workflow

### 1. Launch and orient

1. The user launches the app and first sees a loading page while the local runtime and the workbench catalog are initialized.
2. Within the normal local-startup target of 3–5 seconds, the main screen replaces the loading page. If initialization takes longer or fails, the loading state shows that work is continuing or presents a recoverable error; it does not appear frozen.
3. The main screen has a persistent navigation cascade, main area, and context dock, with Workbench Assistant available in the top bar:
   - The left dock is the navigation cascade: the `PROJECTS`, `WORKTREE`, `DEVICE`, and `TASKS` sections appear one level at a time as the user selects deeper scope, and each collapses independently. A worktree's tasks that resolve to no target appear under `WORKTREE`; its PLC devices and its single hardware target are rows of `DEVICE`.
   - The main area is the home overview. It highlights recent ongoing projects, worktrees, tasks, and a **highlighted information** section. The exact content and ranking rules for highlighted information are intentionally **defined later**; its purpose is to help the user find current work and understand recent activity quickly.
   - The right dock shows context for the selected workbench, worktree, or device.
   - The top bar has a compact **Workbench Assistant** text box. A click focuses it; the user can enter and send a command without opening the conversation, and the latest short reply appears in the box. A double-click or the adjacent conversation button opens the floating conversation directly below the top bar, with the same text box moving down as its messages unfold. The user can drag its bottom edge to adjust the height; long content scrolls inside, and the chosen height remains when the conversation is reopened. Collapsing reverses the motion and folds the messages away. The same input is reused in both positions, so the draft, messages, and pending approval remain available; the collapsed box is bounded by the space the top bar's own controls leave, so it never covers them, and on a window too narrow to show the composer at its full width the conversation opens wider than the collapsed box. The assistant remains available while navigating, including Settings and Tools.
4. The user selects a workbench, then the intended worktree, then a target — one of that worktree's PLC devices, or its hardware configuration — before working on its tasks or opening version control. Only the sections for the selected scope are shown, so the next choice is the only one presented.

**Design acceptance:** loading visibly transitions to the main screen; each dock has its stated purpose; the home overview helps users resume work instead of requiring a search through one deep tree; the assistant remains available in the top bar while navigating.

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

1. The user selects a workbench and worktree, then opens the Tasks view to check whether an existing task already covers the work. The user can switch between cards and a column-based list with task name, status, type, bound PLC, and actions; scope and the expandable brief sit with the task name. The brief shows the full goal and expected result. The user creates a task when needed with a title, type, target, goal, and expected result. A target is either one of the worktree's PLC devices or the worktree's hardware configuration: a hardware task covers hardware-configuration work, binds no PLC device, and is listed under the hardware row rather than with the device's tasks. Source blocks can be staged on that task as the work becomes concrete; a hardware task has no source staging, because it is not device-scoped.
2. Starting a chat on the task uses its target and supplies its goal, expected result, and context to the assistant. The user does not need to select the device separately.
3. The user can open **Workbench Assistant** with no selected project, worktree, or device. It lists available projects, asks which one the user means when needed, and helps the user select a worktree and device. It answers project, task, and history questions before a device is selected; PLC-specific tools require a device.
4. For workbench, worktree, and task creation, the assistant asks only for missing details, presents selectable source, baseline, or device choices, and runs the same managed operations as the UI after the user approves the tool call. It reports the created entity and refreshes the workbench view. It does not send the user to a dialog or use raw TIA/Git creation tools.

**Design acceptance:** task chat starts from the task's target without a separate device selection and receives the task goal and context. A read-only answer requires no approval card. Assistant creation shows choices and a managed-operation approval card, then completes the action without asking for UI navigation. Task creation records distinct goal and expected-result text. An old orientation answer never hides the pending proposal.

### 4. Understand a device offline

1. The user selects a device and reads its identity, source counts, and knowledge state.
2. The user browses source objects, networks, tags, or cross-references and asks grounded PLC questions against that device graph.
3. When those questions establish a defect, a risk, or an improvement, the user can ask the agent to record it as a task. The agent proposes a task bound to the conversation's worktree and device, carrying what the conversation established — background, evidence (the blocks, networks, and tags it used), and the solutions under consideration — and records it only after the user approves the card. The brief is the task's description; the created task appears in the navigator's `TASKS` section without re-selecting the worktree. The agent does not send the user to the task dialog.
4. After an edit batch, the user chooses **Update knowledge** before relying on graph context. The state clears from `stale` only after validated applied hashes. A full rebuild is for a full source-tree rebuild.

**Design acceptance:** knowledge status and last-update time are visible. A finding recorded from the device chat creates a device-bound worktree task whose brief carries the conversation's background, evidence, and proposed solutions, and nothing is created when the user rejects the card. The UI directs one update after an edit batch, not one per edit. Knowledge is never shared across devices or worktrees.

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
| Commit local source work | Stage source objects on the active task, select files, and submit a message | A task commit contains only that task's staged source objects; the Git commit is linked to the task in the engineering graph and remains Git-only. |
| Import local source to TIA | Select eligible objects; on a feature review **Prepare feature import** and resolve conflicts | Only selected eligible objects import. The app compiles all devices and records validation before publishing a no-fast-forward feature merge. |
| Record native state | Enter a description and choose **Create SVN savepoint** | Managed TIA project saves, compiles successfully, freezes, commits to SVN, then writes linked `revision.json` in Git. |

An empty source commit is rejected unless the user explicitly marks an **Untrackable change**. That is a visible Git marker, not a replacement for a native savepoint.

**Design acceptance:** validate the commit message before the action; reject files outside the active task's stages; explain disabled imports; retain retry context after failure; never claim that an ordinary source commit created an SVN revision. A native savepoint associated with a task is visible in that task's engineering history.

### 7. Review, recover, and finish

1. The user opens **History** to inspect a unified Git/SVN timeline. Expanded entries show author, time, files, validation, checksum, and linked native revision.
2. To inspect a historical native state, the user exports its savepoint. This creates a separate lean inspection copy and never replaces the live managed `tia/` copy.
3. To recover source, the user creates a rollback feature from selected historical files. The app does not reset `master`.
4. After the feature import/compile/validation path passes, the user merges it through the feature workflow and returns to the intended worktree.

**Design acceptance:** distinguish Git-only commits, validation evidence, untrackable markers, and savepoints. Recovery starts a feature or inspection copy, never silently rewinds active engineering state.

## Automated acceptance map

Use `./launch.ps1`, wait for health checks, then verify `http://localhost:5173/` and `http://localhost:5239/api/status` before browser tests. Check visible DOM after every meaningful action and inspect local-app console errors afterwards. Fixtures or deterministic model responses are acceptable; do not create or approve a real engineering worktree merely to prove that an approval card renders.

| Scenario | Browser actions | Observable proof |
| --- | --- | --- |
| Startup and home orientation | Launch the app and wait for initialization | Loading state is visible; the main screen appears; project tree, home overview, and compact assistant text box render. |
| Create workbench from session | Open creation; name project; select an open TIA session; set tags/root; confirm | Step-specific timed progress is visible; success notice appears; the new project is present on the home overview. |
| Create workbench from file | Open creation; name project; select `.ap17` with the file browser; confirm | File attachment is visible before submit; success/failure identifies the resulting workbench or failed runtime step. |
| Catalog orientation | Load app; select workbench/worktree/target | Navigator reveals one level at a time and preserves Workbench → worktree → target context, including the hardware row and its own task list; selected surface renders. |
| Assistant read-only | Open assistant with no worktree; wait for orientation; ask for history/todos | No error boundary or worktree-id error; worktrees listed; answer matches request. |
| Assistant mutation gate | Request worktree creation; choose base if prompted | Current **Approve**/**Reject** card renders; smoke test does not approve it. |
| Task from a device conversation | Ask the device chat to record a finding it established as a task; review the brief on the card | The card names `create_task` with the full brief; approving creates a device-bound worktree task whose description carries the sections, and the `TASKS` section lists it without re-selection; rejecting creates nothing. |
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
