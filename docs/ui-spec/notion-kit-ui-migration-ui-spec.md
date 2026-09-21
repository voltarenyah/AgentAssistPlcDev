# Notion-kit UI Migration UI Specification

## Overview

- Outcome: Studio speaks one design vocabulary. notion-kit's tokens are authoritative for the names
  both systems declare, Studio's colliding surface semantics are renamed out of the collision, and
  product surfaces are then redesigned on notion-kit primitives surface by surface.
- Scope: the token switch and its 37-file rename; a thin wrapper policy for the library's missing
  defaults; one pilot product surface redesigned end to end; the migration order for the rest.
- PRD or requirement carrier: no PRD exists in this repository. The requirement carrier is
  `docs/STYLEGUIDE.md` together with its canonical external source, the Notion design analysis
  (`docs/project-context/external-resources.md` records access), and decision
  `docs/adr/ADR-0004-notion-kit-design-token-authority.md`.
- Explicit exclusions: backend, API, persistence, and protocol behavior; the eight Studio primitives
  with no notion-kit counterpart beyond moving them onto the new tokens; adopting notion-kit's
  `ThemeProvider`, `ThemeToggle`, or `Toaster`; the disabled legacy device tree (issue #109); adding
  runtime dependencies.

## Design Evidence

| Source | Path / identifier | Decision supplied |
|---|---|---|
| Design authority | `docs/STYLEGUIDE.md` and the Notion design analysis | Notion's design language is canonical and replaces the former local Studio visual rules. |
| Decision record | `docs/adr/ADR-0004-notion-kit-design-token-authority.md` | notion-kit owns the shared token names; Studio surface semantics are renamed. |
| Existing UI | `studio/src/assets/main.css` | Today's token values and the `.notion-kit-surface` workaround to be retired. |
| Existing UI | `studio/src/catalog/pages/NotionKitPage.tsx` | The only migrated surface; establishes the `Button` size and `CardContent` padding findings. |
| Library inventory | `https://notion-ui.vercel.app/docs` | Available components and the per-component import path. |
| Repository rules | `studio/AGENTS.md` | Import subpaths, the ~250 kB `primitives` barrel cost, registry status. |
| Existing UI | `studio/src/components/ui/*` (28 primitives) | The set being replaced, and the eight components with no counterpart to preserve. |
| Measured evidence | `git grep -c 'bg-muted\|bg-secondary' -- studio/src` | 37 files, 85 sites, ranked; drives the migration order. |

## UI Surface and Flow

| View or state | Entry / trigger | User-visible result | Governing requirement / AC |
|---|---|---|---|
| Foundation switch | The token commit lands | Every surface renders as before, except the declared deltas: corners 2px tighter, borders slightly more visible, `--primary` marginally brighter | AC-001 |
| Catalog preview | Settings → Appearance → Open catalog | Notion Kit page renders correctly **without** `.notion-kit-surface`; descriptions, labels and the gray badge stay readable | AC-002 |
| Any migrated surface | Developer opens it in the browser | Buttons carry notion-kit's size scale; no label sits against its own border; no zero-padding control | AC-003 |
| Pilot surface (create project) | Projects dock → create project | The dialog is rebuilt on notion-kit primitives with its fields, validation, mode switch, progress detail and submit behavior preserved | AC-004 |
| Pilot surface, both themes | Theme toggle | Layout, proportion and contrast hold in light and dark at ordinary desktop width, with no horizontal overflow | AC-005 |
| Migration status | Any not-yet-migrated surface | Remains on Studio primitives but on the new tokens; it must not regress visually because of the switch | AC-006 |

Each surface is migrated independently, so a partially migrated application is a normal intermediate
state. What must never appear is a single surface mixing both vocabularies.

## Components and Interactions

| Component responsibility | Reuse / extend / new | Inputs or state | Interaction and response | Governing source |
|---|---|---|---|---|
| Shared token names (`--primary`, `--secondary`, `--muted`, `--border`, `--radius`, `--ring`) | Extend: values become notion-kit's | Light and dark theme | Declared once in `main.css`; no component overrides them | ADR-0004, AC-001 |
| Studio surface tokens (`--surface-muted`, `--surface-secondary`) | New | Today's Studio values preserved | Replace the 85 colliding `bg-muted`/`bg-secondary` call sites | ADR-0004, AC-001 |
| `.notion-kit-surface` | Remove | — | Deleted with its use on the catalog preview page | AC-002 |
| notion-kit `Button` | Reuse via a thin Studio wrapper | `variant`, `size` | The wrapper supplies the `size` the library does not default (`md` = `h-9 px-4 py-2`, scale-identical to Studio's default button) | AC-003 |
| notion-kit `CardContent` | Reuse via the same wrapper layer | children | The wrapper supplies the padding the library defaults to `p-0`, so content is not flush against the card border | AC-003 |
| Studio primitives with no counterpart (`accordion`, `button-group`, `collapsible`, `color-picker`, `hover-card`, `slider`, `toggle`, `toggle-group`) | Preserve | Existing props | Stay in `studio/src/components/ui` on the new tokens; no behavior change | AC-006 |
| Pilot dialog | Extend: rebuild `CreateWorkbenchDialog.tsx` on notion-kit primitives | Existing session/file mode, project path, validation, progress | Preserved behavior and callbacks; visual language becomes Notion's | AC-004, AC-005 |

The wrapper layer is deliberately narrow: it exists only for primitives whose library defaults are
missing or surprising, not as a blanket re-export. Its exact shape and location belong to the Design
Doc; this spec fixes only the visible consequence — no control renders with a missing size or a
missing padding.

## Visual Constraints

| Element / view | Constraint | Repository or approved design source | Acceptance observation |
|---|---|---|---|
| Every rounded corner | notion-kit's radius scale applies globally, 2px tighter than before | ADR-0004, notion-kit `--radius: 0.5rem` | Corners are consistent across migrated and unmigrated surfaces; no mixed radii on one control |
| Card headers and content | Header inset equals content inset on every card | notion-kit `CardHeader` `p-6`, `CardContent` `p-0` | The first content row aligns with the card title, not with the card border |
| Action rows | Buttons use notion-kit's size scale, never bare text plus a border | notion-kit `buttonVariants.size` | A button's label has visible horizontal padding; all buttons in a row share one height |
| Badges | No `size="sm"` badge carrying a text label | notion-kit defines `sm` as `px-1.5 text-[9px]/none` | Any badge showing text is at least the `md` scale and legible |
| Caption and description text | Must be legible in both themes; must not repeat the 1.31:1 / 1.05:1 failure | Commit `efb6d8f` | Measured contrast of caption text against its effective background is recorded, not assumed |
| Ordinary desktop width | No horizontal overflow introduced | `docs/ui-spec/studio-ui-library-integration-ui-spec.md` Visual Checkpoint Rule | `documentElement` and the scrolling container report zero horizontal overflow; nothing is clipped |

### Visual Checkpoint Rule

83 colocated tests run under happy-dom, which has no layout engine, so they cannot observe layout,
colour, or overflow. Before a surface is considered migrated, render it in the browser in both
themes and compare proportion, overflow, and contrast against the pre-migration surface. A passing
component suite or a production build is not evidence of visual proportion.

## Accessibility Requirements

| Component / interaction | Keyboard, semantic, announcement, or contrast behavior | Source | Acceptance observation |
|---|---|---|---|
| Caption and description text | Body text contrast must reach WCAG AA (4.5:1); notion-kit's own `--muted` (1.05:1 light, 2.61:1 dark) must not be adopted | ADR-0004 decision; `AGENTS.md` accessibility requirement | Pixel-sampled after the switch: 4.74:1 light, 4.83:1 dark on the Notion Kit page |
| Form labels and menu text | notion-kit's light `--secondary` (3.39:1) must not be adopted | ADR-0004 decision | Pixel-sampled: "Task name" label is 5.23:1 light, 7.97:1 dark |
| Gray status badge | Remaining known miss: notion-kit's own chip renders 4.18:1 in light mode, about 7% under AA | ADR-0004 accepted residual; tracked as an open decision below | Recorded as 4.18:1 by pixel sampling; must be resolved during the badge's own surface migration |
| Focus rings | Focus stays clearly visible; notion-kit's 7%-white `--ring` must not replace Studio's focus-ring colour | ADR-0004 decision | Keyboard focus is visible on inputs, buttons, and menu items in both themes |
| Migrated controls | Accessible names, roles and disabled states are preserved by the migration | `docs/ui-spec/studio-ui-library-integration-ui-spec.md` | Existing accessible names and disabled behavior survive the rebuild |
| Badges and status chips | Text is not conveyed at an unreadable scale | notion-kit `Badge size` definition | Status text stays legible without relying on colour alone |

## Acceptance Traceability

| AC / requirement | View, component, or interaction | Observable UI proof |
|---|---|---|
| AC-001 | Foundation token switch | A dense surface renders identically in both themes apart from corners, border visibility and `--primary`; no Studio surface picks up a translucent-white background |
| AC-002 | Catalog Notion Kit page | Readable card descriptions, `Task name` label and gray badge with `.notion-kit-surface` deleted |
| AC-003 | Buttons and cards on any migrated surface | Button labels have horizontal padding; card content aligns with the card title |
| AC-004 | Pilot create-project dialog | Mode switch, project path, validation, progress detail and submit callbacks all behave as before; its colocated tests pass |
| AC-005 | Pilot surface, light and dark, ordinary desktop width | No horizontal overflow; proportion and contrast checked in both themes |
| AC-006 | Unmigrated surfaces | No visual regression other than the three declared deltas; the eight preserved primitives still function |

## Migration Order

| Stage | Surface | Collision sites | Isolated tests | Reason for position |
|---|---|---|---|---|
| 1 | Foundation: token switch + rename | 85 sites / 37 files | full suite | Must land first and alone so no state mixes vocabularies |
| 2a | `SandboxDeniedDialog.tsx`, `TiaCloseConfirmationDialog.tsx`, `RefreshDialog.tsx` | 2 each | 0 / 2 / 2 | Dialog + Button + Input only, so they establish the dialog mapping before the big one and de-risk it. `SandboxDeniedDialog` gained a colocated test during migration. |
| 2b | `studio/src/studio/workbench/CreateWorkbenchDialog.tsx` | 4 | 9 | Deferred behind 2a because it is the only surface on this path needing two primitives notion-kit does not map 1:1: it has no `ToggleGroup` (the session/file mode switch must become `Tabs`, and its custom sliding indicator must be reworked) and its `Select` is Base UI rather than Radix, with `SelectValue` needing explicit `items` to render a label. Its `Input` also uses a leading icon, which notion-kit's `Input` does not support (`endIcon` only, no children). |
| 4 | `WorktreeTasksPanel.tsx`, `ChatWorkspace.tsx`, `McpToolsHelper.tsx` | 6 / 6 / 11 | 8 / 21 / 1 | Higher density; `McpToolsHelper` is a developer surface so it can absorb early mistakes |
| 5 | Remaining `studio/src/**` files by density | 1–4 each | varies | Mechanical by this point |
| Dedicated | `studio/src/studio/workbench/WorkbenchNavigator.tsx` | 0 | 6 + 4 | 651 lines holding 43 menu items (26 `ContextMenuItem`, 17 `DropdownMenuItem`), 7 labels, 14 buttons, 6 dialogs. Migrate **by hand**, as one surface: it is the always-visible project tree, so a partially migrated state shows two menu styles at once in the most prominent place. |
| Last | `studio/src/studio/MainStudio.tsx` | 9 | many | 2702-line component and the carrier of issue #109; migrate only after that issue is resolved |

### WorkbenchNavigator: do not script the JSX rewrite

Recorded after two failed attempts. The item and label shapes are regular and a
brace-aware transformer converts all 43 items and 7 labels correctly, but the
**trigger blocks are not safely scriptable**: seven `asChild` triggers wrap the
whole row, and two of them wrap a `div` whose body contains nested `div`s, so
balanced-tag matching mis-terminates and produces unbalanced JSX. The type checker
rejected both attempts and the file was reverted each time.

Hand-edit the triggers individually. Also note that notion-kit's `ContextMenuItem`
and `DropdownMenuItem` share one structured API (`icon`, `label`, `desc`,
`variant`, `onClick`), that `variant="destructive"` maps to `error`, and that the
label components are Base UI group labels requiring a `title` prop.

## Open User Decisions

Resolved during specification and implementation: captions use per-theme AA values (`#737373` light,
`#807d78` dark) instead of notion-kit's 1.05:1 / 2.61:1 `--muted`; labels use `#6f6c67` light
instead of notion-kit's 3.39:1 `--secondary`; and `--ring` stays Studio's so keyboard focus remains
visible. All are recorded in `docs/adr/ADR-0004-notion-kit-design-token-authority.md`.

| Decision | Effect on current UI |
|---|---|
| Grey badge in light mode samples 4.18:1, about 7% under AA | Either accept the library's chip design as-is, or adjust the badge during its own surface migration rather than darkening the global `--secondary` further |
| Pilot surface: `CreateWorkbenchDialog.tsx` as recommended above, or another surface | Changes which callbacks, validation and tests the pilot must preserve |
| Whether to adopt notion-kit's `ThemeProvider`/`ThemeToggle`/`Toaster` in place of Studio's theme module and toast wrapper | Affects how the theme toggle and toasts are owned; excluded from this slice |
| Fate of the eight primitives with no notion-kit counterpart | Whether Studio keeps maintaining them, replaces them with notion-kit blocks, or retires the features that use them |
| Whether to add a screenshot baseline (a new test dependency, so a maintainer decision) | Determines whether AC-001/AC-005 can be verified automatically over time or only by manual browser passes |

## Update History

| Date | Version | Changes |
|---|---|---|
| 2026-09-21 | 1.0 | Initial specification for the notion-kit foundation slice and migration order. |
| 2026-09-21 | 1.1 | Add the WorkbenchNavigator dedicated stage and record that its trigger blocks must be hand-edited rather than scripted. |
