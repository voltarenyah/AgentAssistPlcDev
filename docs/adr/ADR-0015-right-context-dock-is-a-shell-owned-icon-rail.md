# ADR-0015 The right context dock is a shell-owned icon rail

## Status

Accepted

## Context

The right context dock is currently **page-owned**: `resolveContextDock`
(`studio/src/studio/workspace/contextDock.ts:41-68`) maps the current selection and focused workspace
view onto one of four content kinds (`hardware`, `device`, `knowledge`, `version-control`), and each
kind is a component that brings its own chrome:

| Kind | Component | Its own chrome |
|---|---|---|
| `version-control` | `VersionControlPanel` | a 48 px icon nav with two tabs (`Changes`, `History`) and a compare toolbar rendered *above both pages* (`VersionControlPanel.tsx:274-293`, `:295-350`) |
| `device` | `DevicePropertiesDock` | a 48 px header titled `Device properties`, `text-xs` field scale (`DevicePropertiesDock.tsx:63-71`) |
| `hardware` | `HardwarePropertiesDock` | a 48 px header titled `Object properties`, 8/10 px field scale (`HardwarePropertiesDock.tsx:27-31`) |
| `knowledge` | `KnowledgePropertiesDock` | a 48 px header titled `Properties`, 8/9/10 px field scale (`KnowledgePropertiesDock.tsx:93-101`) |

Three consequences follow, and all three are visible in the running app:

- **The dock is not app-wide.** `resolveContextDock` returns `visible: false` for a selected device on
  a chat, source, or inspector focus, so neither the dock shell nor its resize handle renders. A
  device on a chat view has no way to see its own properties, and a worktree has no way to see its
  uncommitted changes while a device is selected.
- **The strip changes identity with the page.** The only tab strip in the dock belongs to the
  version-control panel and exists only at the worktree level; the device level has none, so the same
  column is a different thing on every page.
- **The chrome is inconsistent.** Three property docks present the same kind of data under three
  titles at three type scales, and the version-control toolbar renders over a page (`History`) that
  none of its controls affect.

`docs/user-workflow.md:38` already describes the intended shape — "the right dock shows context for
the selected workbench, worktree, or device" — and the implementation no longer matches it.

The user rejected an earlier proposal of a shell-owned *tab strip* (`Properties | Changes | History`
in a 36 px row) in favour of an icon rail whose page opens like a drawer, and then chose the rail's
placement: **the outer (window) edge**, so that the icon under the cursor does not move when a page
opens.

## Decision Point

- **Question**: what owns the right dock, and how does the user reach and dismiss one of its pages?
- **Why a decision exists**: four materially distinct options are credible and each is implementable
  with the current components — keep page-owned docks; make the shell own a horizontal tab strip;
  make the shell own an icon rail on the *inner* (workspace) edge; or make the shell own an icon rail
  on the *outer* (window) edge. They differ in who owns the chrome, in whether the clicked control
  moves, and in the persisted layout shape, so the choice is not reversible for free.
- **Scope boundary**: the right column's ownership, chrome, reachability, animation, and persisted
  layout state. It does not decide the *content* of any page (`Properties`, `Changes`, `History` keep
  their data and operations), and it does not touch the left navigator (`ADR-0006`), the workspace's
  FlexLayout tabs, or the Workbench Assistant.

## Decision

The right dock is **owned by the shell** and is a **persistent icon rail on the outer (window) edge**
with exactly one page open beside it:

```
[ workspace ............................. ][ ⧉ ][  page 266 px  ][ rail 44 px ]
   ⇔ resize handle 4 px                        ⇔ one page at a time
```

- The shell renders the rail whenever the right column is shown, on every page of the application,
  including a project with no worktree selected and a device on a chat or source view.
- The rail has three stable pages, in this order: `Properties` (the selection's properties),
  `Changes` (the selected worktree's working tree), `History` (its commits and SVN savepoints).
- One click opens a page, a click on another page switches to it, and a click on the page that is
  already open collapses the page and leaves the rail. The last page stays marked while collapsed, so
  the next click returns to it.
- The **page** is user-resizable (240–420 px, default 266 px, so the default column is the 310 px it is
  today) and the shell persists the page, the page's width, and whether the whole column is shown. The
  rail is a fixed 44 px and is never squeezed out by the drag.
- The page animates with the curve the shell already uses for its docks
  (`width 280 ms cubic-bezier(.22, 1, .36, 1)`), and the page's content is **clipped, not reflowed**,
  while the width animates.

### Decision Details

| Item | Content |
|------|---------|
| **Decision** | The shell owns the right dock: a 44 px icon rail pinned to the window edge, one 266 px page beside it, and the three pages `Properties` / `Changes` / `History`. |
| **Why this** | The clicked icon never moves, so the open/switch/collapse gesture the user asked for stays under the cursor; the rail sits in the same corner as the title-bar dock toggle; icon-only tooltips open over the workspace instead of over the page they just opened; and one chrome row serves every page instead of four. |
| **Known unknowns** | Whether a fourth page is ever needed (the rail is sized for a growing list), and whether the `Changes` rail badge should count objects or devices — recorded as open decisions in the UI spec. |
| **Reconsider when** | Users routinely want two pages at once (for example `Changes` and `History` side by side), which a single-page rail cannot express; or the rail's three entries grow past roughly six, where icon-only recognition stops working. |

## Rationale

### Options Considered

| Option | Requirement and repository fit | Current-scope benefit | Lifecycle cost | Maintainability | Material trade-offs |
|---|---|---|---|---|---|
| Keep page-owned docks (status quo) | No change; every existing test keeps passing | Zero work | Every new page re-decides its dock; three header variants and three type scales stay | `resolveContextDock` keeps growing a state matrix that has to exclude stale focuses | The dock stays absent on chat/source/inspector and at the project level, which contradicts `docs/user-workflow.md:38` |
| Shell-owned tab strip (`Properties | Changes | History` as a 36 px row) | Reuses `Tabs`; smallest structural change | One dock everywhere; the three property docks merge into one `Properties` tab | A horizontal strip competes for width with the page header at 240 px, and the row is chrome the page cannot use | Tab labels must collapse to icons at narrow widths, which is a rail in all but name |
| Shell-owned rail on the **inner** edge | Same components, different flex order | Drawer reads as one object retracting to the right | Expanding moves every rail icon 266 px left, so the icon the user just clicked is no longer under the cursor and a second click to collapse must be re-aimed; tooltips must open over the page; the rail sits against the resize handle | Identical component cost to the outer-edge rail; only the flex order and the tooltip side differ | The `aria` and keyboard contract is unaffected, so this option is cheap to revisit later |
| Shell-owned rail on the **outer** edge | Matches the conventions this dock is modelled on (VS Code Activity Bar, IntelliJ tool window bars: bars live on the workbench's outer edges) | Clicked icon stays put; rail shares the title bar's top-right corner; tooltips open over the workspace | One fixed 44 px column is reserved even while collapsed (48 px with the handle) | One chrome implementation for every page; the three property docks lose their headers and one field scale is chosen | A collapsed rail still occupies 48 px that the status bar and the workspace cannot use |

**Selected**: the shell-owned rail on the outer edge. It delivers the drawer behaviour the request
asked for while keeping the interaction the request specified — a second click on the *same* icon
collapses the page — physically possible.

## Consequences

### Positive Consequences

- The right dock exists on every surface, so a device on a chat view can show its properties and a
  worktree's `Changes` are reachable while a device is selected.
- One chrome row (`title · scope · refresh · collapse`) and one field scale replace three dock
  headers and three scales.
- The version-control compare toolbar can be scoped to the page whose state it changes; it no longer
  renders above `History`, where none of its controls do anything.
- The open page and the width survive a reload, and `Settings → Reset layout` restores them together.
- A future page is one rail entry plus one content component; it does not need a placement decision.

### Negative Consequences

- A collapsed rail still takes 48 px. The user cannot reclaim the column's full width while keeping
  the rail; hiding the whole column remains the title-bar toggle's job.
- `resolveContextDock`'s `visible` matrix is gone, so the tests that asserted "no right dock for a
  device on a chat view" (five `MainStudio.*.test.tsx` files) must be re-pointed at the page's
  content instead of the dock's absence.
- The persisted layout format changes, so a stored v1 layout is migrated rather than read.

### Neutral Consequences

The three pages keep their existing data sources, API calls, and operations. No API, gateway route,
or engineering-state file changes; the rail is presentation and shell state only.

### Persisted Shape

The stored layout is versioned (`plc-studio.shell-layout.v2`) because the right column's fields change
meaning:

| v2 field | Meaning | Default |
|---|---|---|
| `rightColumnOpen` | the whole right column is shown (rail included); the title bar's toggle owns it | `true` |
| `rightPanelWidth` | the **page's** width; the rail (44 px) and the handle (4 px) are added to it | `266` |
| `rightPanel` | the page the rail marks, or `null` before the user has ever chosen | `null` |
| `rightPanelCollapsed` | whether that page is collapsed to the rail, kept apart from the page so the rail can keep marking it | `false` |

A stored v1 layout migrates as `rightColumnOpen = rightOpen`, `rightPanelWidth =
clamp(rightWidth - 44)`, `rightPanel = null`, `rightPanelCollapsed = false`. The migration is
width-preserving: a v1 `rightWidth` of 310 px (the default, and what a resized dock holds today)
becomes a 266 px page in a 310 px column, so the workspace does not move when the change lands. `null`
is what makes the first run after the change open the page the current selection implies (a worktree
selection opens `Changes`, which is what the worktree page shows today) instead of forcing a page on a
user who never chose one.

## Architecture Impact

`MainStudio` already owns the column's shell (`data-dock="right"`, the resize handle, the persisted
width) and keeps that ownership; what changes is that it renders the rail and the page switch instead
of delegating visibility to `resolveContextDock`. `contextDock.ts` changes responsibility from "is the
dock visible, and which single component is inside" to "what content does each rail page have for the
current selection". The three property docks and the version-control panel change responsibility from
"a right dock" to "the content of a page". `shellLayout.ts` gains the open page as a persisted field,
which is why the stored shape is versioned rather than extended in place.

## Implementation Guidance

- Keep the tablist semantics: the rail is a vertical `Tabs` (Radix) list, so `↑`/`↓`, `aria-selected`,
  and `aria-controls` come from the primitive instead of local code.
- Keep every page's content mounted while another page is shown, so a page switch does not remount the
  version-control surface and re-run a TIA comparison.
- Clip the page while the width animates; do not reflow it. The existing `.dock-shell` transition
  reflows its child on every frame, which makes wrapped toolbars jump mid-animation.
- Preserve the title bar's dock toggle as the "hide the whole column" control; the rail's own gesture
  only opens, switches, and collapses the page.

## Related Information

- `docs/ui-spec/right-dock-icon-rail-ui-spec.md` — the approved surface, states, and interactions
- `docs/design/right-dock-icon-rail-design.md` — the implementation approach and change surface
- `docs/plans/20261008-frontend-right-dock-icon-rail.md` — the executable plan
- `docs/adr/ADR-0006-studio-navigator-ownership-and-shape.md` — the left navigator, which this
  decision leaves untouched
- `docs/adr/ADR-0009-navigator-sessions-section.md` — the 2026-10-03 amendment whose clause "a device
  on a chat or source view resolves to no dock at all" this decision supersedes
- `studio/src/studio/workspace/contextDock.ts` — the component whose responsibility changes
- `studio/src/studio/shellLayout.ts` — the persisted layout whose shape changes
