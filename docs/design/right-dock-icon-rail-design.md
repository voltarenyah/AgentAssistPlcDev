# Design Document: Right dock icon rail

## Overview

The right context dock becomes one shell-owned column: a 44 px icon rail on the window's outer edge
and one page (240–420 px, default 266 px) open beside it. `Properties`, `Changes`, and `History` stop
being page-owned docks and become the rail's three pages. Ownership, placement, animation, and the
persisted shape are fixed by `docs/adr/ADR-0015-right-context-dock-is-a-shell-owned-icon-rail.md`; the
approved surface and its acceptance criteria are in
`docs/ui-spec/right-dock-icon-rail-ui-spec.md`.

## Requirement Boundary

In scope: the right column's chrome and ownership, the rail and its page switch, the three pages'
headers and empty states, the persisted layout shape and its migration, the animation, and the tests
and documents that assert the old behaviour.

Out of scope: the content of each page (its data reads, operations, and domain components), the left
navigator and its sections (ADR-0006), the workspace's FlexLayout tabs, the Workbench Assistant, and
the top bar's dock control's own styling.

## Acceptance Criteria

The acceptance criteria are the AC-001…AC-014 rows of
`docs/ui-spec/right-dock-icon-rail-ui-spec.md` and are not restated here. AC-010 (animation) and
AC-014 (no remount on switch) are the two that constrain the implementation's shape rather than its
markup, so the selected design below addresses them explicitly.

## Selected Design

### Ownership and layout

`MainStudio` keeps the column it already renders (`data-dock="right"`, the `role="separator"` handle,
the persisted width) and stops asking `resolveContextDock` whether the dock exists. The column becomes

```tsx
<div data-dock="right" data-dock-state={pageOpen ? 'open' : 'collapsed'} style={{ width: pageOpen ? pageWidth : 0 }}>
  <div className="dock-track">                 {/* clips; the page keeps its layout */}
    <RightDockPanel page={page} … />           {/* fixed width = pageWidth target */}
  </div>
  <RightDockRail page={page} onSelectPage={…} badge={…} />   {/* fixed 44 px */}
</div>
```

The rail is a sibling of the clipping track, so it never moves and never narrows while the page
animates. The title bar's toggle keeps owning "the whole column is shown", which is the only state in
which the rail is not rendered.

### The rail

`RightDockRail` composes the existing `Tabs` primitive (`orientation="vertical"`) so the tablist
semantics, `↑`/`↓` movement, `aria-selected`, and `aria-controls` come from Radix rather than local
code. Each item is a `Tabs.Trigger` styled with the app's icon-button vocabulary, wrapped in the
existing `Tooltip` with the page's name; the `Changes` item renders a count badge when the worktree
has uncommitted objects.

The page state is one value: `'properties' | 'changes' | 'history' | 'collapsed'`. A click on the open
page sets `'collapsed'`; a click on another page sets that page. The collapsed rail keeps the last
page's `aria-selected`/active treatment so the next click restores it, which is why the state keeps
the page and the open/closed flag separate in the component even though the persisted field folds
them into one string.

### Pages stay mounted

The three pages are rendered together, with the two inactive ones hidden (`hidden` attribute plus the
existing pattern of keeping the version-control surface mounted), because a switch must not remount
`VersionControlChanges` and re-run a TIA comparison (AC-014). Only the `Properties` panel that matches
the selection is mounted, since those three components read different endpoints.

### Animation without reflow

`.dock-shell-right` already transitions its width, but it transitions the *shell* and lets its child
reflow every frame, which makes a wrapping toolbar jump mid-animation. The rail model instead keeps
the page at its target width inside a clipping track:

```css
.dock-shell-right        { transition: width 280ms cubic-bezier(.22, 1, .36, 1); }
.dock-track              { display: flex; justify-content: flex-end; min-width: 0; overflow: hidden; }
.dock-page               { flex: 0 0 auto; transition: transform 280ms cubic-bezier(.22, 1, .36, 1), opacity 160ms ease-out; }
.dock-shell-right[data-dock-state='collapsed'] .dock-page { transform: translateX(10px); opacity: 0; }
@media (prefers-reduced-motion: reduce) { … 1ms … }
```

`justify-content: flex-end` is what anchors the page to its right edge, so the collapsing box is
revealed and hidden on its left — the direction the approved mockups show.

### Content derivation

`resolveContextDock` keeps its shape as a pure function with a test, but answers a different question:
`{ properties: 'device' | 'hardware' | 'knowledge' | null, worktree: { workbenchId, worktreeId } | null }`
plus the page a fresh session should open. The `visible` gate disappears; no selection and no focus
combination resolves to "no dock".

### Persisted layout

`shellLayout` gains the v2 shape documented in ADR-0015: `rightColumnOpen`, `rightPanelWidth`,
`rightPanel` (`'properties' | 'changes' | 'history' | 'collapsed' | null`), with a v1 read path that
migrates width-preservingly and a v1 key that is left in place rather than deleted.

## Change Surface

| File | Change |
|---|---|
| `studio/src/studio/shellLayout.ts` (+ test) | v2 shape, v1 migration, page-width clamp, page field |
| `studio/src/studio/workspace/contextDock.ts` (+ test) | visibility matrix → page content derivation and default page |
| `studio/src/studio/workspace/RightDockRail.tsx` (+ test) | new: the rail, its items, badge, tooltips, keyboard contract |
| `studio/src/studio/workspace/RightDock.tsx` (+ test) | new: the column — rail, one open page, the pages kept mounted, the clipping track |
| `studio/src/studio/workspace/RightDockRail.tsx` | new: the rail items (vertical `TabsList`), tooltips, the `Changes` badge (part of `RightDock`) |
| `studio/src/studio/workspace/RightDockPage.tsx` | new: the shared page frame (title, scope, refresh, collapse) |
| `studio/src/studio/MainStudio.tsx` | render the rail column on every surface; own the page state and the refresh signal; drag the page width; keep the title bar toggle as column visibility |
| `studio/src/studio/DevicePropertiesDock.tsx`, `HardwarePropertiesDock.tsx`, `KnowledgePropertiesDock.tsx` (+ tests) | drop each dock's own header row; become content-only panels on one type scale; `DevicePropertiesDock` and `KnowledgePropertiesDock` take `refreshSignal?: number` |
| `studio/src/studio/version-control/VersionControlPanel.tsx` (+ test) | `section: 'changes' \| 'history'` replaces the internal nav; `refreshSignal?: number`; `onUncommittedCountChange?: (count: number) => void`; no in-page refresh button; each section fetches what it shows |
| `studio/src/studio/settings/SettingsPage.tsx` | reset-layout copy unchanged; it now resets the page and its width together |
| `studio/src/studio/MainStudio.rightDock.test.tsx`, `MainStudio.taskChat.test.tsx`, `MainStudio.chatFailure.test.tsx`, `MainStudio.chatConfirm.test.tsx`, `MainStudio.deviceSelect.test.tsx`, `MainStudio.apiKey.test.tsx` | re-point "no right dock" assertions at the page's content |
| `docs/adr/ADR-0015-…md`, `docs/ui-spec/right-dock-icon-rail-ui-spec.md`, this document, `docs/plans/20261008-frontend-right-dock-icon-rail.md` | new |
| `docs/adr/ADR-0009-navigator-sessions-section.md`, `docs/design/studio-navigator-sessions-design.md`, `docs/ui-spec/studio-information-architecture-ui-spec.md` | the clause and rows that say a device on a chat or source view has no dock |

## Verification Strategy

| Boundary | Proof |
|---|---|
| Pure layout state | `shellLayout.test.ts`: v2 round-trip, v1 migration width-preservation, clamp, reset |
| Content derivation | `contextDock.test.ts`: the full selection × focus matrix resolves to a page content or a named empty state, never to "no dock" |
| Rail behaviour | `RightDockRail.test.tsx`: order, names, roles, `↑`/`↓`, click-to-open/switch/collapse, badge presence and absence |
| Page switching | `RightDockPanel.test.tsx` and a `MainStudio` test: one page visible at a time, the version-control surface keeps its state across a switch |
| The shell | the `MainStudio` dock suites: the rail renders on every surface, the drag clamps, the stored layout is restored, the title bar toggle hides the column |
| The running app | `.\launch.ps1`, then Playwright: open, switch, collapse, resize, reload, and a device on a chat view showing `Properties`; screenshots of the collapsed and open states; the browser console checked for application errors |
| Whole suite | `cd studio; npm test -- --run`, `npm run lint`, `npm run build` (the build type-checks) |

## References

- `docs/adr/ADR-0015-right-context-dock-is-a-shell-owned-icon-rail.md`
- `docs/ui-spec/right-dock-icon-rail-ui-spec.md`
- `docs/plans/20261008-frontend-right-dock-icon-rail.md`
- `docs/STYLEGUIDE.md`
- `studio/src/studio/MainStudio.tsx`, `studio/src/studio/workspace/`, `studio/src/assets/main.css`
