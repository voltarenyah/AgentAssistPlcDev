# 006. Remove the right dock's AI sessions page and move its operations into the navigator's conversations

Status: pending
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

Not yet executed.
