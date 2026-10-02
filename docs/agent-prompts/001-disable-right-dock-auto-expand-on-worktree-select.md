# 001. Remove every right-dock auto-expand

Status: pending
Created: 2026-09-30
Scope broadened: 2026-09-30 — was "auto-expand on worktree selection"; now every action-driven
auto-expand.
Depends on: none

## Goal

No user action opens the right dock by itself. Selecting a worktree and starting a conversation from
the chat empty state both leave `shellLayout.rightOpen` exactly as the user left it, so a collapsed
dock stays collapsed — including across a page reload, because the persisted layout is unchanged.

## Context

`shellLayout.rightOpen` in `studio/src/studio/MainStudio.tsx` is the only source of right-dock
visibility. Its writers, with the disposition this item requires:

| Writer | Location | Disposition |
|---|---|---|
| Dock toggle | `MainStudio.tsx:618-622` (`toggleDock`, `data-dock-toggle="right"`) | Keep — the user's own action. |
| Dock resize | `MainStudio.tsx:624-627` and its pointer-move handler (~`:637`) | Keep — writes widths only. |
| **`selectWorktree`** | `MainStudio.tsx:1072-1074` | **Remove** the forced open and its comment. |
| **`createChatSessionFromEmptyState`** | `MainStudio.tsx:1375-1378` | **Remove** the forced open. |
| Settings "reset layout" | `MainStudio.tsx:2409-2415` → `setShellLayout(DEFAULT_SHELL_LAYOUT)` | Keep — an explicit user action that restores the documented default. |
| Persistence | `MainStudio.tsx:614-616`, `studio/src/studio/shellLayout.ts` | Keep — storage key `plc-studio.shell-layout.v1`, same shape. |

The two removals, as the code reads today:

```tsx
// MainStudio.tsx:1071-1074, inside selectWorktree
setMainView({ kind: 'worktree', tab: 'overview' })
// Version control lives in the right dock of the worktree page; make
// sure the dock is visible when navigating there.
setShellLayout(previous => previous.rightOpen ? previous : { ...previous, rightOpen: true })

// MainStudio.tsx:1375-1378
const createChatSessionFromEmptyState = () => {
  setShellLayout(previous => previous.rightOpen ? previous : { ...previous, rightOpen: true })
  void createChatSession()
}
```

Supporting facts:

- `DEFAULT_SHELL_LAYOUT.rightOpen` is `true` (`studio/src/studio/shellLayout.ts:13-19`), so a browser
  with no stored layout still shows the right dock on first run. That is the default state, not an
  auto-expand, and this item does not change it.
- `studio/src/studio/workspace/contextDock.ts` derives dock *content* independently of `rightOpen`,
  and the dock renders only when `contextDock.visible` (`MainStudio.tsx:2758`). No change there.
- Every worktree entry point routes through `selectWorktree` — the project landing page,
  `WorkbenchNavigator`, the app-assistant worktree selection, device and task navigation,
  `createWorktree` auto-select, and the bootstrap master auto-select — so one removal covers all of
  them.
- Line numbers are from commit `32bfd6c`. Locate the sites by searching for `rightOpen: true` rather
  than trusting the numbers.
- Related item: 006 removes the right dock's AI sessions page. After 006,
  `createChatSessionFromEmptyState` only creates a session, and the forced open would have been
  pointless as well as wrong. Run 001 first so the two edits to this function do not overlap.

## Constraints

- Remove the forced `setShellLayout(... rightOpen: true ...)` from `selectWorktree` and from
  `createChatSessionFromEmptyState`, including the stale comment in the first one, and change nothing
  else in those functions.
- Keep the dock toggle, the resize handlers, and the settings reset-layout action working as they do
  today.
- Keep `shellLayout.rightOpen` as the single source of truth for visibility, and keep the storage key,
  the persisted shape, `DEFAULT_SHELL_LAYOUT`, and the `data-dock` / `data-dock-state` attributes
  unchanged. Do not change the first-run default in this item.
- Keep the change inside `studio/src/studio/`; follow `docs/STYLEGUIDE.md` and the colocated-test
  convention in `studio/AGENTS.md`. Add no dependency.

## Done when

1. A colocated test in `studio/src/studio/` proves both removals: with the right dock collapsed,
   selecting a worktree leaves `[data-dock="right"]` at `data-dock-state="closed"`, and starting a
   conversation from the chat empty state leaves it `closed` too (extend
   `MainStudio.deviceSelect.test.tsx` or add a sibling file).
2. With the dock open, both actions leave it open — no forced state in either direction.
3. `grep -rn "rightOpen: true" studio/src` matches only `DEFAULT_SHELL_LAYOUT` in
   `shellLayout.ts`; no `{ ...previous, rightOpen: true }` remains anywhere.
4. The dock-independence test `MainStudio.deviceSelect.test.tsx:105` and the "both docks collapsed"
   case in `MainStudio.apiKey.test.tsx:105` still pass. They start from the default layout, so verify
   rather than assume.
5. `cd studio && npm test -- --run` passes and `npm run build` (`tsc -b && vite build`) succeeds.
6. Runtime check with `.\launch.ps1`: collapse the right dock, select another worktree, start a
   conversation from the chat empty state, and confirm the dock stays collapsed and is still collapsed
   after a reload.

## Evidence

Not yet executed. Record commands, results, branch, commit, and any skipped check here, then move the
index row in `README.md` to the same status.
