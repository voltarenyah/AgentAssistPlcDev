# 001. Disable right-dock auto-expand when a worktree is selected

Status: pending
Created: 2026-09-30
Depends on: none

## Goal

Selecting a worktree no longer changes the right dock's open/closed state. The dock keeps the state
the user chose, and that choice survives a page reload. Today, selecting a worktree after collapsing
the right dock re-opens it.

## Context

Right-dock state is `shellLayout.rightOpen` in `studio/src/studio/MainStudio.tsx`. Exactly three
places write it, and only one of them is driven by worktree selection:

1. `selectWorktree` (~lines 1031–1061) — **the cause**:

   ```tsx
   setSelection({ workbenchId: workbench.workbenchId, worktreeId: worktree.worktreeId, deviceId: null })
   setMainView({ kind: 'worktree', tab: 'overview' })
   // Version control lives in the right dock of the worktree page; make
   // sure the dock is visible when navigating there.
   setShellLayout(previous => previous.rightOpen ? previous : { ...previous, rightOpen: true })
   ```

2. The user's dock toggle (~line 613): `setShellLayout(previous => ({ ...previous, rightOpen: !previous.rightOpen }))`
   — the button labelled `Show context dock` / `Hide context dock` (`data-dock-toggle="right"`).
3. `createChatSessionFromEmptyState` (~line 1320): opens the dock because that action creates dock
   content (a chat tab). Out of scope for this item.

Supporting facts:

- `studio/src/studio/shellLayout.ts` persists the layout under `SHELL_LAYOUT_STORAGE_KEY =
  'plc-studio.shell-layout.v1'`, and `DEFAULT_SHELL_LAYOUT.rightOpen` is `true`. With no stored
  layout a first-time browser therefore still shows the right dock open — that default is the
  intended first-run state and this item does not change it.
- `studio/src/studio/workspace/contextDock.ts` derives the dock *content* (the worktree landing page
  resolves to `{ kind: 'version-control' }`). Content resolution is independent of `rightOpen`, so no
  change is needed there; the dock simply stays hidden while the user keeps it collapsed.
- Every entry point inherits the fix, because they all route through `selectWorktree`: the project
  landing page, `WorkbenchNavigator`, the app-assistant worktree selection, device/task
  navigation, `createWorktree` auto-select, and the bootstrap master auto-select.
- `studio/src/studio/MainStudio.deviceSelect.test.tsx:105` ("collapses and reopens the left and right
  docks independently") asserts `data-dock-state` values. It starts from the default layout, not from
  the selection-driven open, so it must keep passing unchanged — verify rather than assume.
- The worktree page's version control panel is still reachable through the right dock toggle. This
  item intentionally accepts that a user who collapsed the dock does not see it automatically.

## Constraints

- Remove the forced `setShellLayout(... rightOpen: true ...)` call and its explanatory comment from
  `selectWorktree` only. Leave the toggle handler and `createChatSessionFromEmptyState` as they are.
- Keep `shellLayout.rightOpen` as the single source of truth for dock visibility, and keep
  `SHELL_LAYOUT_STORAGE_KEY`, the persisted layout shape, and the `data-dock` / `data-dock-state`
  attributes unchanged — no persisted-format, API, or contract change.
- Keep the change inside `studio/src/studio/`; follow `docs/STYLEGUIDE.md` and the colocated-test
  convention in `studio/AGENTS.md`. Add no dependency.
- `studio/src/studio/MainStudio.tsx` currently has unrelated uncommitted work (an `openTaskInTia`
  function and its `WorktreeLandingPage` prop). Preserve it, and keep it out of this item's commit.

## Done when

1. A colocated test in `studio/src/studio/` proves the behavior: with the right dock collapsed,
   selecting a worktree leaves `document.querySelector('[data-dock="right"]')?.getAttribute('data-dock-state')`
   at `closed`; extend `MainStudio.deviceSelect.test.tsx` or add a sibling test file.
2. Selecting a worktree while the dock is open leaves it open — no forced state in either direction.
3. A browser with no `plc-studio.shell-layout.v1` entry still renders the right dock open
   (`DEFAULT_SHELL_LAYOUT`).
4. `cd studio && npm test -- --run` reports all tests passed, and `npm run build` (`tsc -b && vite build`)
   succeeds.
5. Manual run with `.\launch.ps1`: at `http://localhost:5173/` collapse the right dock, select a
   different worktree, and confirm the dock stays collapsed and is still collapsed after a page
   reload.

## Evidence

Not yet executed. Record commands, results, branch, commit, and any skipped check here, then move the
index row in `README.md` to the same status.
