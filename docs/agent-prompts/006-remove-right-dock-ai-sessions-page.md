# 006. Remove the right dock's AI sessions page and move its operations into the navigator's conversations

Status: done
Created: 2026-09-30
Depends on: 001

## Goal

The right context dock no longer hosts an AI sessions list, and nothing that page offered is lost: its
operations become reachable from the navigator's `SESSIONS` section, in each conversation row's
three-dot menu and in the section's header action. No operation stays reachable only from the removed
page, and no empty right dock or resize handle is left behind.

## Context

What the page is: `studio/src/studio/workspace/contextDock.ts` resolves `{ kind: 'sessions' }` for a
selected device whenever the focused workspace view is not `overview` or `knowledge`;
`MainStudio.tsx:2810-2823` renders `SessionDock` there, titled "AI sessions"
(`studio/src/studio/chat/SessionDock.tsx:46`). The dock itself renders only when
`contextDock.visible` (`MainStudio.tsx:2758`), so the content rule is the switch that removes it.

The operation inventory — this is the part that decides the scope, because an operation with no other
entry point is a capability the removal would delete:

| Operation | `SessionDock` | Navigator row (`WorkbenchNavigator.tsx:412-452`) | Anywhere else |
|---|---|---|---|
| Open conversation | `onActivate` (`:99-109`) | row click, menu "Open conversation" (`:435-438`) | task surfaces |
| Rename | `:110-112` | menu (`:439-442`) | — |
| Delete | `:116-118`, confirmed `:130-141` | menu (`:444-447`), confirmation | — |
| Export | `:113-115` | **missing** | nowhere: `MainStudio.exportChatSession` (`:1427`) is only wired here (`:2820`) |
| Attach / reassign / remove task | `:124-128`, `window.prompt` | **missing** | nowhere: `MainStudio.setChatSessionTask` (`:1499`) is only wired here (`:2821`) |
| New session | header `+` (`:48-50`) | header action (`:869-879`), **offered only while a task is selected** | chat empty state only (`ChatWorkspace.tsx:405-425`) |

Governing documents that this change must amend rather than contradict:

- `docs/adr/ADR-0009-navigator-sessions-section.md` fixes the row menu to "exactly the three
  operations the repository performs" and states the header action is offered only while a task is
  selected (`:85-88`, `:182`).
- `docs/design/studio-navigator-sessions-design.md` lists "Re-binding a conversation to another task
  is out of scope for now" as a user-decided non-goal (`:39`) and repeats the three-item menu
  (`:97`, `:175`), with an update-history table (`:209-217`).
- `docs/ui-spec/studio-information-architecture-ui-spec.md` carries the session row's menu
  requirements; the same commit that added ADR-0009 changed it (`32bfd6c`), so read it for the current
  wording before editing.

Facts that keep the migration cheap:

- The navigator already receives `tasksByWorktree` and `sessionsByWorktree`, and each loaded
  conversation carries its device (`WorkbenchNavigator.tsx:74-77`, `:458`), so a task picker needs no
  new route, type, or stored shape.
- The searchable-picker precedent is `CommandDialog` + `CommandInput` + `CommandList` in
  `studio/src/studio/workbench/tags/TagPicker.tsx:59-82`, with removable chips as in
  `WorktreeLandingPage.tsx:341-363`.
- Delete already uses the ADR-0010 path (`docs/adr/ADR-0010-deleting-a-task-conversation.md`,
  `DELETE …/devices/{device}/sessions/{session}`), so it must not be reimplemented.
- ADR-0009 AC-018 requires opening a row to leave the navigator's selection untouched; the navigator's
  row currently calls `onOpenSession` (`:889`), which `MainStudio` satisfies — keep that contract.
- `ChatWorkspace.tsx:411-413` tells the user to "Use the session dock to start a new chat or resume a
  saved one". That copy becomes false with the dock gone.

## Constraints

- With the sessions kind gone, a selected device on a chat or source view must resolve to no dock at
  all: `contextDock` returns `visible: false`, so neither `[data-dock="right"]` nor its resize handle
  renders. Cover it in `studio/src/studio/workspace/contextDock.test.ts`.
- Every operation in the inventory stays reachable from the navigator: open, rename, delete (already
  there), export, task attach / reassign / remove, and starting a conversation while no task is
  selected. The menu is per row, so "start a conversation" belongs in the section's header action
  instead: generalize it to the scope the section is showing — bound to the selected task, or
  device-scoped and task-less when no task is selected, which is exactly the list the section shows in
  that state.
- Task binding must not use `window.prompt`; use a picker over the worktree's tasks. `setChatSessionTask`
  and its API keep their current semantics, including clearing the binding.
- Deleting a conversation keeps using the ADR-0010 path and its confirmation.
- Delete `studio/src/studio/chat/SessionDock.tsx` and `SessionDock.test.tsx` rather than leaving a dead
  component, and remove the now-unused `sessions` kind from `contextDock.ts`'s
  `ContextDockContent`/`resolveContextDock`.
- Amend `ADR-0009` and `docs/design/studio-navigator-sessions-design.md` in the same change — row-menu
  contents, the header-action rule, the non-goals, acceptance criteria, and their update-history rows —
  and align the UI-spec wording, so no decision document contradicts the code.
- Keep the rest of the navigator untouched: the four other sections, their menus, the collapse and
  separator contracts, the tag-filter rule, the deepest-section rule, and the row content.
- Follow `docs/STYLEGUIDE.md` and the colocated-test convention in `studio/AGENTS.md`; add no
  dependency.

## Done when

1. `grep -rn "SessionDock" studio/src` returns nothing, and no right dock renders for a selected device
   on a chat or source view — proven by a `contextDock.test.ts` case.
2. Navigator tests prove the row menu offers export and the task binding operations in addition to
   open, rename and delete, that binding a conversation picks from the worktree's tasks (no
   `window.prompt`), and that binding or clearing it moves the conversation between the section's task
   list and its task-less list.
3. A navigator test proves the header action starts a conversation bound to the selected task, and a
   device-scoped task-less one when no task is selected.
4. A checklist in the evidence enumerates all six inventory operations and names the test that proves
   each one is still reachable.
5. ADR-0009, the navigator sessions design document, and the IA UI-spec describe the menu and header
   action as implemented, with their update-history rows added.
6. `cd studio && npm test -- --run` passes and `npm run build` (`tsc -b && vite build`) succeeds; the
   `ChatWorkspace` empty-state copy no longer refers to a session dock.
7. Runtime check with `.\launch.ps1`: open a device chat, confirm no right dock appears for it, and
   perform export, task binding and a new conversation from the navigator's `SESSIONS` section; report
   any browser console error.

## Evidence

Executed 2026-10-03 in the isolated worktree `.worktrees/006-remove-ai-sessions-dock`.

**Done-when 7 — the `.\launch.ps1` runtime and browser pass — was not run.** The unattended-run
instruction defers every runtime/browser/TIA step because the launcher binds the shared ports 5173/5239.
The six other Done-when checks were verified; see *Checks skipped* for what that leaves unproven and
*Residual risk* for what it costs.

- **Branch**: `codex/006-remove-ai-sessions-dock` (the branch this item was given).
- **Start state**: `git status --short` empty, `git log -2 --oneline` = `4e5409d`, `0e95939`. Item 001's
  changes are absent from this branch, as intended: neither `selectWorktree` nor
  `createChatSessionFromEmptyState` was touched, and the two items' edits to `MainStudio.tsx` are in
  disjoint regions.
- **Commits** (local only; nothing pushed, no PR, no merge):
  - `b9e56c1` — `refactor: retire the right dock's AI sessions page (006)`
  - `b6fd78d` — `docs: align the sessions decisions with the retired dock page (006)`
  - `8dd8fb5` — `test: drive export and task binding through MainStudio (006)`
  - the item-and-index commit that carries this evidence.

The removal and the operation migration are one commit rather than two: `MainStudio`'s export and
re-binding handlers change signature with the dock's call sites, and the navigator only receives the two
new callbacks at the same time, so any split would leave an intermediate commit that either renders an
empty right dock or wires a menu item to a handler that does nothing.

### Files changed

| File | Purpose |
|---|---|
| `studio/src/studio/workspace/contextDock.ts` | Drops the `sessions` content kind; a device on a chat, source, inspector or stale focus resolves to `visible: false` |
| `studio/src/studio/workspace/contextDock.test.ts` | Replaces the two session-dock cases with the no-dock-at-all case, keeping the device dock's overview case as the contrast |
| `studio/src/studio/chat/SessionDock.tsx`, `SessionDock.test.tsx` | Deleted |
| `studio/src/studio/MainStudio.tsx` | Drops the `SessionDock` import and render block and the legacy `removeChatSession`; export and re-binding now act on the device the conversation names; the task-less start clears the worktree's active task; wires the two new navigator callbacks |
| `studio/src/studio/workbench/WorkbenchNavigator.tsx` | Row menu gains export, attach/reassign (a `CommandDialog` picker over the worktree's device-bound tasks) and remove task; the header action is generalized to the scope the section is showing |
| `studio/src/studio/chat/ChatWorkspace.tsx` | Empty-state copy no longer tells the user to use a session dock |
| `studio/src/studio/workbench/WorkbenchNavigator.test.tsx` | Row-menu contents, export, the binding picker, the binding move, and the generalized header action |
| `studio/src/studio/MainStudio.taskChat.test.tsx` | Rename and delete now driven from the row menu through the ADR-0010 route; a new case drives export and binding; asserts no `[data-dock="right"]` on a device chat |
| `studio/src/studio/MainStudio.chatConfirm.test.tsx`, `MainStudio.chatFailure.test.tsx` | Start their conversation from the chat surface instead of the retired dock's `New session` button (same behaviour proven) |
| `studio/src/studio/MainStudio.layout.test.ts` | The retired dock is no longer read for the shared right-dock panel style |
| `docs/adr/ADR-0009-navigator-sessions-section.md` | Header-action and row-menu decisions, the dock page's retirement, and a new update-history table |
| `docs/design/studio-navigator-sessions-design.md` | Row menu, header action, contracts, change surface, AC-016, new AC-019, verification rows, risks, update history v1.5 |
| `docs/ui-spec/studio-information-architecture-ui-spec.md` | Session row-menu and header-action wording, AC-016, new AC-019, state table, update history v1.8 |
| `docs/agent-prompts/006-remove-right-dock-ai-sessions-page.md`, `docs/agent-prompts/README.md` | This evidence and the queue row's status |

The rest of the navigator is untouched: the other four sections and their menus, the collapse and
separator contracts, the tag-filter rule, the deepest-section rule, the row content, and ADR-0009
AC-018's behaviour (an open row leaves the navigator's selection alone, still asserted by
`MainStudio.taskChat.test.tsx`'s navigator case).

### Commands run and observed results

| # | Command (from `studio/`) | Observed result |
|---|---|---|
| 1 | `npx tsc -b` | exit 0, no output; re-run after the final test addition, still exit 0 |
| 2 | `npm run build` (`tsc -b && vite build`) | exit 0, `✓ built in 571ms`; the only warning is the pre-existing >500 kB chunk notice for `index-*.js` |
| 3 | `npm test -- --run src/studio/workbench/WorkbenchNavigator.test.tsx src/studio/workspace/contextDock.test.ts` | `Test Files 2 passed`, `Tests 45 passed` |
| 4 | `npm test -- --run src/studio/MainStudio.taskChat.test.tsx src/studio/MainStudio.layout.test.ts src/studio/MainStudio.deviceSelect.test.tsx` | `Test Files 3 passed`, `Tests 16 passed` |
| 5 | `npm test -- --run src/studio/MainStudio.chatConfirm.test.tsx src/studio/MainStudio.chatFailure.test.tsx` | `Test Files 2 passed`, `Tests 5 passed` (these two failed on their first full-suite run, because they started their conversation from the retired dock's `New session` button; they now use the chat empty state) |
| 6 | `npm test -- --run` | `Test Files 82 passed (82)`, `Tests 529 passed (529)`, exit 0. The suite held 531 cases before this item: minus the five retired `SessionDock` cases and one redundant `contextDock` case, plus four navigator cases and one `MainStudio` case |
| 7 | `npm run lint` | `Found 15 warnings and 0 errors`; all 15 are in untouched files (`src/api/client.tags.test.ts`, five `src/components/ui/*`, `OperationTimingList.tsx`, `TaskSessionsDisclosure.tsx`, `TiaSessionsPanel.tsx`, `WorktreeTasksPanel.tsx`), i.e. no new warning |
| 8 | `rg -n "SessionDock" studio/src` | no matches (exit 1); `rg -n "session dock" studio/src` matches only the retrofit comments in `contextDock.ts`/`contextDock.test.ts` that record the page's retirement |

### Six-operation checklist

Every operation the removed page offered, and the test that proves it is still reachable:

| # | Operation | Entry point after this change | Test proving reachability |
|---|---|---|---|
| 1 | Open conversation | Row click and the row menu's `Open conversation` | `WorkbenchNavigator.test.tsx` — "opens a conversation from its row (AC-015)"; `MainStudio.taskChat.test.tsx` — "opens a navigator conversation without dropping the selected device or its task" (also proves the row click end to end) |
| 2 | Rename | Row menu `Rename conversation` → dialog | `WorkbenchNavigator.test.tsx` — "renames a conversation from its row menu"; `MainStudio.taskChat.test.tsx` — "follows a conversation renamed from its SESSIONS row menu" |
| 3 | Delete | Row menu `Delete conversation` → confirmation, on the ADR-0010 device-scoped route (not reimplemented) | `WorkbenchNavigator.test.tsx` — "asks before deleting a conversation, and only deletes when confirmed (AC-017)"; `MainStudio.taskChat.test.tsx` — "drops a conversation deleted from its SESSIONS row menu" (asserts `deleteDeviceSession('wb1','wt1','dev1','s1')`) |
| 4 | Export | Row menu `Export conversation` | `WorkbenchNavigator.test.tsx` — "exports a conversation from its row menu"; `MainStudio.taskChat.test.tsx` — "exports a conversation and binds a task-less one from the SESSIONS row menu" (asserts `exportChatSession('s1')`) |
| 5 | Attach / reassign / remove task | Row menu `Attach task` / `Reassign task` → `CommandDialog` picker over the worktree's device-bound tasks, and `Remove task` while bound | `WorkbenchNavigator.test.tsx` — "binds a conversation to one of the worktree's tasks through a picker, and clears it" (asserts the offered tasks, the chosen id and the `null` clear), "offers every operation the repository performs on a conversation row (AC-017)" (asserts the bound/unbound wording) and "moves a conversation between the task list and the task-less list when its binding changes"; `MainStudio.taskChat.test.tsx` — the export/bind case asserts `setChatSessionTask('s1','task1')` and that no `window.prompt` was called |
| 6 | New session | `SESSIONS` header action, bound to the selected task or device-scoped and task-less when none is selected; the chat empty state also still creates one | `WorkbenchNavigator.test.tsx` — "starts a conversation in the scope the section is showing from its header (AC-016)" (asserts `onAddSession(null)` and `onAddSession(task)`); `MainStudio.chatConfirm.test.tsx` and `MainStudio.chatFailure.test.tsx` start a session from the chat empty state |

Done-when 1's dock half is covered twice: the unit case in `contextDock.test.ts` (a device on chat,
source, inspector or a stale focus returns `{ visible: false, content: { kind: 'none' } }`, while the
overview focus still returns the device dock) and the DOM case in `MainStudio.taskChat.test.tsx` (after
opening a conversation with a device selected, neither `[data-dock="right"]` nor
`[aria-label="Resize context dock"]` is rendered).

### Checks skipped

- **Done-when 7 (runtime/browser)** — skipped, as the unattended-run instruction directs: `.\launch.ps1`
  binds the shared ports 5173/5239, which a parallel run cannot own. Everything it would have checked is
  covered by L1 component tests instead (no right dock for a device chat; export, task binding and the
  new conversation from the `SESSIONS` section; the console-error check has no L1 equivalent).
- No ApiHost/`dotnet` lane was run: the change touches no C# code, and this branch does not carry 001's
  or any other item's backend work.

### Residual risk

- **Unverified in a real browser**: (a) that the dock's absence leaves no layout gap and the workspace
  reclaims the width (the React tree no longer renders the shell, so the flex row should close, but a
  screenshot was not taken); (b) the focus handoff from the row's `DropdownMenu` to the `CommandDialog`
  picker — the component test proves the dialog renders and its item is selectable, not that the browser
  focuses the search input on open; (c) that no console error accompanies either flow.
- **`MainStudio`'s export and re-binding handlers** are now covered by one end-to-end component case each;
  their error branches (`showErrorToast` on a missing device context or a failed call) stay untested, as
  they were before.
- **The task-less header action clears the worktree's active task** (`setActiveWorktreeTask(…, null)`)
  before creating, so the new conversation cannot inherit a binding. That is a server-side selection
  change, and no test asserts the server's active task afterwards; the component case binds a task-less
  conversation without exercising the create path, which only the deferred runtime check would.
- **The picker's task list is the worktree's device-bound tasks.** A binding to a task under another
  device is therefore offered but lands the conversation in a list the selected device does not show
  until that device is selected; the design document's risk table records this.
- Item 001 is not merged into this branch, so the eventual merge must reconcile `MainStudio.tsx`'s
  `selectWorktree` / `createChatSessionFromEmptyState` region with 001's edit; no conflict is expected
  because the regions are disjoint, but the merge is a human step this run did not perform.

