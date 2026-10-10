# Right dock icon rail UI Specification

## Overview

The right context dock becomes a shell-owned icon rail: a 44 px column of three icons pinned to the
window's outer (right) edge, and exactly one page open beside it. The page the rail opens shows the
selection's properties, the selected worktree's uncommitted changes, or its commit and savepoint
history. `docs/adr/ADR-0015-right-context-dock-is-a-shell-owned-icon-rail.md` fixes the ownership,
the placement, and the persisted shape; this document fixes the surface, its states, and its
interactions.

## Design Evidence

| Evidence | Source |
|---|---|
| Ownership, placement, animation, persisted shape | `docs/adr/ADR-0015-right-context-dock-is-a-shell-owned-icon-rail.md` |
| The dock as it is today (four page-owned contents, three header titles, three type scales, a compare toolbar above both version-control pages) | `studio/src/studio/workspace/contextDock.ts`, `studio/src/studio/DevicePropertiesDock.tsx`, `studio/src/studio/HardwarePropertiesDock.tsx`, `studio/src/studio/KnowledgePropertiesDock.tsx`, `studio/src/studio/version-control/VersionControlPanel.tsx` |
| The shell's existing dock chrome to reuse (resize handle, width clamp, dock transition) | `studio/src/studio/MainStudio.tsx:2866-2920`, `studio/src/studio/workspace/shellLayout.ts`, `studio/src/assets/main.css` (`.dock-shell`, `.dock-resize-handle`) |
| The tab idiom the rail's page switch follows | `studio/src/studio/workbench/WorktreeLandingPage.tsx:242-256` (icon + label, `rounded-md`, `bg-accent` when active) |
| The component library contract | `docs/STYLEGUIDE.md` (`Tabs`, `Tooltip`, `.icon-button`, token rules) |
| Reference implementations for the rail model | VS Code Activity Bar (bar on the workbench's outer edge, context indicators), IntelliJ tool window bars (bars on the outer edges), MUI mini-variant drawer, GoDaddy Antares `InlineDrawer` "Sidebar Nav" (numeric px sizes so the width animates) |
| Approved mockups (rendered from the live screenshot and the real component markup) | review evidence from 2026-10-08, attached to the change's PR; the repository keeps UI specs text-only, so the images are not committed |

## UI Surface and Flow

| View or state | Entry / trigger | User-visible result | Governing requirement |
|---|---|---|---|
| Rail, page collapsed | Always, whenever the right column is shown | Three icons on the outer edge, the last page still marked; the workspace takes the released width | AC-001, AC-003 |
| Rail, page open | Click a rail icon | That page fills the column between the workspace and the rail, and the icon is marked | AC-002, AC-003 |
| Page switch | Click a different rail icon | The open page is replaced by the clicked one; the rail does not move | AC-002, AC-003, AC-014 |
| Collapse | Click the open page's own icon, or the page header's collapse control | The page narrows to nothing and its content retracts toward the rail; only the rail is left | AC-003, AC-010 |
| Column hide/show | The title bar's right-dock control | The whole column, rail included, is hidden or shown, on every surface | AC-013 |
| Resize | Drag the handle between the workspace and the page | The page grows and shrinks within 240–420 px; the rail keeps its 44 px | AC-004 |
| `Properties` page | Open it, with a device, a hardware object, or a knowledge node selected | The selected object's properties, named at the top of the page | AC-006 |
| `Properties` page | Open it, with nothing selected | The page names what to select instead of rendering blank | AC-006, AC-009 |
| `Changes` page | Open it, with a worktree selected | The working-tree surface: branch, compare scope, verify, the change list, commit controls, and the TIA savepoint area | AC-007 |
| `Changes` page | Open it, with no worktree selected | The page says a worktree has to be selected | AC-009 |
| `History` page | Open it, with a worktree selected | The timeline of commits and SVN savepoints | AC-008 |
| Clean worktree | Open `Changes` on a worktree with no uncommitted object | The clean-state message, and no badge on the rail's `Changes` icon | AC-012 |
| Dirty worktree | Open `Changes` on a worktree with uncommitted objects | A count badge on the rail's `Changes` icon | AC-012 |
| First run after the change | No stored v2 layout | The page the selection implies opens (a selected worktree opens `Changes`) | AC-005 |
| Stored v1 layout | Reload with a v1 layout in local storage | The column keeps its previous total width; the page is 44 px narrower than the old dock | AC-005 |

## Components and Interactions

| Component responsibility | Reuse / extend / new | Inputs or state | Interaction and response | Governing source |
|---|---|---|---|---|
| Right column shell | Extend `MainStudio`'s existing `data-dock="right"` block | layout state (column shown, page, page width), the current selection | Renders the handle, the open page, and the rail; owns the drag; persists every change | AC-001, AC-004, AC-005, AC-013 |
| Page switch (the rail) | New domain component under `studio/src/studio/workspace/`, composing the `Tabs` primitive vertically | the open page, the pages that have content | Clicking a page opens it, clicking the open page collapses it; `↑`/`↓` move between pages | AC-002, AC-003, AC-011 |
| Rail item | New, composing `Button`/`Tabs.Trigger` + `Tooltip` | page id, label, icon, optional count | Icon-only control; its accessible name and tooltip are the page's name, and its tooltip says whether the click opens or collapses the page | AC-011, AC-012 |
| Page header | New, shared by the three pages | page title, scope text, refresh action, collapse action | The collapse action is the rail gesture's keyboard-reachable twin | AC-003, AC-011 |
| `Properties` page | Extend the three existing property docks into content-only panels behind one header | the selection (device, hardware node, knowledge node/edge) | Renders the panel that matches the selection, or the "nothing selected" state | AC-006, AC-009 |
| `Changes` page | Extend `VersionControlPanel`'s changes half | worktree, compare scope, task context | The compare scope controls move inside this page; the snapshot area stays pinned at its bottom | AC-007 |
| `History` page | Extend `VersionControlPanel`'s history half | worktree, timeline reads | Renders the timeline; never renders the compare controls | AC-008 |
| Content derivation | Extend `resolveContextDock` | selection, focused workspace view, knowledge context | Returns each page's content kind (or none) instead of the dock's visibility | AC-001, AC-006, AC-009 |
| Empty state | New, `RightDockEmptyState` | icon, title, what to select, the scope | One component for every page's empty state, so a page with nothing to show still says what it is for | AC-009 |
| Uncommitted count | New, `version-control/sourceEntries.ts` | a worktree's status read | The one mapping from status entries to source objects; the changes page lists them and the shell counts them for the badge and the header chip | AC-012 |
| Stored layout | Extend `shellLayout` | local storage | Reads and writes v2, migrates v1, clamps the page width | AC-005 |

### State / Display Detail

| Component | State or condition | Display | Recovery / transition | Governing requirement |
|---|---|---|---|---|
| Rail | A page is open | That icon carries the active treatment (`bg-accent`, foreground icon) | Clicking it collapses the page | AC-003 |
| Rail | No page is open | The last page is still marked, so the next click returns to it | Clicking it opens that page | AC-003 |
| Rail | The user has never chosen a page | The page follows the selection, the way the dock did before the rail existed: a device selection shows `Properties`, a worktree shows `Changes` | The first rail click stores a page, and the dock stops following: a later selection change never moves a page the user picked | AC-003 |
| Rail | The startup read is still in flight, with no stored page | The rail is collapsed rather than showing a page that is about to be replaced | The selection arrives and the page it implies opens | AC-005 |
| `Changes` page | Its header | Names the page only: the page carries the worktree's branch row itself, so a second scope line would repeat it | n/a | AC-007 |
| Rail | The worktree has uncommitted objects | A count badge on the `Changes` icon | The badge disappears when the working tree is clean | AC-012 |
| Page | Width animating | The page's box narrows or widens while its content keeps its layout and is clipped | The animation ends at the page's persisted width | AC-010 |
| Page | Reduced motion is requested | The width and opacity change without a transition | n/a | AC-010 |
| `Properties` page | Selection is a knowledge edge or node with no properties yet | The loading state, then the properties or the empty property list | Re-reads when the selection changes | AC-006 |
| `Changes` page | No worktree selected | "Select a worktree" state | Selecting a worktree loads the surface | AC-009 |
| Every page | Nothing to show for the current selection | A named empty state; never a blank column | The state names what to select | AC-009 |

## Visual Constraints

| Concern | Constraint | Source |
|---|---|---|
| Rail geometry | 44 px wide, 30 px square items, 6 px radius, 16 px icons, 4 px gap, centred on the column's outer edge | `docs/STYLEGUIDE.md`, `.icon-button` vocabulary |
| Page chrome | One 44 px header: page title, scope, refresh, collapse; a single 1 px border separates it from the page body | `docs/STYLEGUIDE.md` |
| Type scale | One scale across the three pages: 13 px semibold title, 12 px body, 10–11 px secondary, `var(--font-mono)` for identifiers and paths | `docs/STYLEGUIDE.md` |
| Color | `sidebar` for the rail and the page surface, `background` for the property cards, `accent` for the active rail item, `chart-*`/`emerald`/`amber` as the domain surfaces already use them | `studio/src/assets/main.css` |
| Separation | The rail is divided from the page by the ordinary border; no shadow tier is introduced | `docs/STYLEGUIDE.md` |
| Motion | Page width 280 ms `cubic-bezier(.22, 1, .36, 1)`; content opacity ~140–180 ms; reduced motion at 1 ms | `studio/src/assets/main.css`, ADR-0015 |

## Accessibility Requirements

| Requirement | Detail | Governing requirement |
|---|---|---|
| Rail semantics | A vertical tablist: `role="tablist"` with `aria-orientation="vertical"`, each item `role="tab"` with `aria-selected` and `aria-controls`, and the page as the `tabpanel` it names | AC-011 |
| Keyboard | `↑`/`↓` move between rail items; `Enter`/`Space` activate, so a page can be opened, switched, and collapsed without a pointer. Activation is manual: moving focus does not open a page | AC-011 |
| Names | Every rail item has an accessible name (the page's name); it is not conveyed by the icon alone | AC-011 |
| Tooltips | Icon-only controls expose a tooltip through the existing primitive, not a native `title` | AC-011 |
| Focus | Collapsing a page keeps focus on the rail item that collapsed it; hiding the column moves focus to the title bar control that did it | AC-011, AC-013 |
| Reduced motion | The animation is suppressed, and no state depends on it | AC-010 |

## Acceptance Traceability

| AC | Requirement | How it is proven |
|---|---|---|
| AC-001 | The right column and its rail exist on every surface | L1: `contextDock.test.ts` over the full selection matrix; L2: the `MainStudio` dock suites assert the rail renders for a device on a chat, source, and inspector focus, and at the project level |
| AC-002 | The rail carries `Properties`, `Changes`, `History` in that order | L1: a rail component test asserting the item order and names |
| AC-003 | Click opens, click on another switches, click on the open one collapses and keeps it marked, and a collapsed page offers a labelled expand handle at the rail's foot | L1: a rail component test; L2: `MainStudio` tests driving the three clicks, asserting the page state, the marking, and the handle's appearance and target |
| AC-004 | The page clamps to 266–420 px and the rail is never squeezed | L1: `shellLayout.test.ts` over the per-side clamp; L2: a drag test asserting the page width and the rail's fixed width. The floor is the default page width, which is the narrowest the version-control toolbar fits on one row |
| AC-005 | Page, page width, and column visibility persist; v1 migrates width-preservingly; reset restores defaults | L1: `shellLayout.test.ts` (read/write/migrate/reset); L2: a reload test asserting the restored state |
| AC-006 | `Properties` follows the selection and names what it shows, including the worktree's hardware node on its configuration, BOM and network pages | L1: the existing three dock tests, re-pointed at the page, plus `contextDock.test.ts`; L2: selecting a device and a hardware node, then switching the hardware page, and asserting the page still describes that node |
| AC-007 | `Changes` is the working-tree surface with its controls inside it | L1: `VersionControlPanel`/`VersionControlChanges` tests; L2: a run asserting the compare controls are absent from `History` |
| AC-008 | `History` renders the timeline | L1: `VersionControlHistory` test through the panel |
| AC-009 | Every page has an empty state that names the page's purpose, what to select, and the scope it is looking at, through one shared component | L1: `RightDockEmptyState` test plus the page tests for the no-selection and empty cases |
| AC-010 | The expand/collapse animation follows the shell's dock motion, and reduced motion is honoured | L1: a class/attribute assertion on the animated container; L3: the running app, driven with Playwright, recording the page's width over the transition |
| AC-011 | The rail is a keyboard-reachable, named, tooltipped vertical tablist | L1: a rail test asserting roles, names, and `↑`/`↓` behaviour |
| AC-012 | The `Changes` icon carries an uncommitted-object count read by the shell, so it is there before the changes page has ever been opened; no badge when clean; the page header repeats the count as a chip | L1: `sourceEntries.test.ts` for the mapping and count, a rail test with and without a count, and a `MainStudio` test whose status read is answered while no page is open |
| AC-013 | The title bar control hides and shows the whole column, rail included, and says so; the rail's own gesture only folds the page | L2: the existing dock-toggle suites plus a `MainStudio` test asserting both states and their labels |
| AC-014 | A page switch does not remount the version-control surface | L1: a `MainStudio` test asserting the version-control page keeps its state across a switch |

## Open User Decisions

| Decision | Current behaviour | Options |
|---|---|---|
| What the rail badge counts | Uncommitted objects of the selected worktree | the object count, or the number of affected devices |
| Whether the page's content fades immediately | Fades over ~140–180 ms as the width animates | start at once, or delay briefly (the dock's existing 70 ms) |
| Whether the rail ever needs a second group | One group of three pages | a lower group for future global pages |

## Update History

| Date | Version | Change |
|---|---|---|
| 2026-10-08 | 1.0 | Initial specification. Supersedes the clause, added to `docs/adr/ADR-0009-navigator-sessions-section.md` on 2026-10-03, that a device on a chat or source view resolves to no dock at all: the dock now exists on every surface, and the navigator's `SESSIONS` section remains the only surface that lists a device's conversations. |
| 2026-10-10 | 1.1 | Review of the running app. **The open page was clipped out of the column**: `forceMount` kept all three page wrappers mounted *and in the layout*, so three 266 px boxes packed to the track's right edge left the open page outside the clip unless it happened to be the last one (measured: the open `Properties` page at x=438 in a track at x=970). The wrappers are now hidden explicitly, which keeps every page mounted without letting a hidden one take space. Also: the rail items are centred 30 px squares; a collapsed page grows a labelled expand handle, and the title bar's control says it hides the whole column, so "fold the page" and "hide the dock" are no longer the same gesture twice; the `Properties` page describes the worktree's hardware node on the configuration, BOM and network pages alike; the uncommitted count is the shell's own read (`sourceEntries.ts` holds the one mapping), so the rail badges and the page header chips it before the changes page has ever been opened; the page floor rises to the default 266 px, the narrowest the toolbar fits on one row; and every empty state goes through `RightDockEmptyState`. |
