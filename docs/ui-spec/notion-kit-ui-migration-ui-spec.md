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
| 2b | `studio/src/studio/workbench/CreateWorkbenchDialog.tsx` | 4 | 9 | Deferred behind 2a because it is the only surface on this path needing two primitives notion-kit does not map 1:1: it has no `ToggleGroup` (the session/file mode switch must become `Tabs`, and its custom sliding indicator must be reworked) and its `Select` is Base UI rather than Radix, with `SelectValue` needing explicit `items` to render a label. Its `Input` also uses a leading icon, which notion-kit's `Input` does not support (`endIcon` only, no children). The `ToggleGroup` mapping is now decided in `docs/adr/ADR-0005-primitive-mapping-for-missing-notion-kit-counterparts.md`. |
| 2c | `OperationTimingList.tsx` (its `ToggleGroup`), `TagPicker.tsx`, `TagFilter.tsx` | — | 5 / 3 / 9 | Same two gaps as 2b. ADR-0005 decides them: `Tabs` for the `ToggleGroup`, and `Combobox`/`Autocomplete` for the tag surfaces, the latter two being interaction-shape changes that each need their own spec update rather than a primitive swap. |
| 4 | `WorktreeTasksPanel.tsx`, `ChatWorkspace.tsx`, `McpToolsHelper.tsx` | 6 / 6 / 11 | 8 / 21 / 1 | Higher density; `McpToolsHelper` is a developer surface so it can absorb early mistakes |
| 5 | Remaining `studio/src/**` files by density | 1–4 each | varies | Mechanical by this point |
| Dedicated | `studio/src/studio/workbench/WorkbenchNavigator.tsx` | 0 | 6 + 4 | 651 lines holding 43 menu items (26 `ContextMenuItem`, 17 `DropdownMenuItem`), 7 labels, 14 buttons, 6 dialogs. Migrate **by hand**, as one surface: it is the always-visible project tree, so a partially migrated state shows two menu styles at once in the most prominent place. |
| Last | `studio/src/studio/MainStudio.tsx` | 9 | many | Superseded by evidence: `MainStudio`, `VersionControlChanges` and `VersionControlHistory` import only the toast helper, a sonner wrapper that stays, so they have **nothing to migrate**. |

### notion-kit `Input`: className overrides need the important modifier

`Input` composes its own variant utilities and the caller's `className` without
deduping them, so the rendered class list can contain both `px-1.5` and `pl-9`. The
library value wins, because both are plain utilities and stylesheet order decides.
Measured: `className="pl-9"` produced 6px of padding, not 36px, which left leading
icons overlapping their field's text.

Any `className` on a notion-kit primitive that overrides a utility the variant also
sets — padding, height, width, font size — must therefore carry Tailwind v4's
important modifier (`pl-9!`, `h-8!`). Overrides that do not collide, such as
`font-mono` or `min-w-0`, are unaffected. This has been applied to the leading-icon
fields in `CreateWorkbenchDialog`, the `McpToolsHelper` search, the
`WorktreeTasksPanel` element-reference and details fields, and `ChatWorkspace`'s
Temperature and Top P inputs.

### Status

Migrated and independently verified (build, full suite, browser in both themes):

| Surface | Commit |
|---|---|
| Token foundation, 37 files | `e23bf09` |
| `SandboxDeniedDialog` (+ its first tests) | `0b3fc2b` |
| `WorktreeTasksPanel` | `04681ea` |
| `WorktreeLandingPage` | `dcaec73` |
| `ProjectLandingPage` | `63d9d91` |
| `StatusBadge` | `bce921f` |
| `ArchiveProjectDialog` (gained dialog semantics) | `5c5c0ba` |
| Tag chip + tag tree | `4569d75` |
| Settings switches + header theme toggle | `59a2b67` |
| TIA close dialog + MCP tools helper | `1860ea6` |
| `WorkbenchNavigator` menus, then its buttons/dialog/input | `be6436f`, `781fc66` |
| Navigator rename dialog (test + footer fix) | `ba4dde8` |
| Operation detail switch: `ToggleGroup` to `Tabs` (ADR-0005 gap 1, first site) | `d3b92fc` |
| `CreateWorkbenchDialog`: the last surface whose primitives map 1:1 | `c954a0f` |
| Duplicate close control removed from three dialogs | `e9f2aaf` |
| Navigator, assistant, settings, dock, TIA, header and MainStudio buttons | `caf63f6` … `e128fbe`, `6aba6d2` |
| Text fields across all product surfaces (the `field-input` layer) | `cbf04ee` … `de8e31f` |
| `TIA sessions panel` + `WindowControls` buttons | `5260a19` |
| Remaining small-surface, version-control and chat buttons | `cf7f642`, `ad0fb3a`, `97fcf01` |
| All `field-label` wrappers to notion-kit `Label` | `ccd1c4b`, `60a6edb` |
| Custom-styled action group in `FeatureValidationDialog` | `0f95b74` |

### Whole-application regression sweep

After the last 1:1 surface landed, a single scripted sweep walked every migrated
surface in both themes: the all-projects landing, the navigator dropdown and context
menu, the project landing, tag chips, the status menu, the worktree landing, the tasks
panel, the rename dialog, every Settings category, all nine catalog pages, the MCP tools
helper, the create dialog in both modes, and the archive dialog.

Result: **36 of 36 steps pass** (18 surfaces × 2 themes) with zero console errors, zero
4xx/5xx responses, zero horizontal overflow and no mutation requests beyond the project
selection. Destructive actions are opened but never confirmed.

### Milestone: the old button vocabulary is gone

The legacy CSS button classes — `secondary-button`, `primary-button`, `icon-button` — were
present **91 times** in `src/studio` when this phase began. There are now **zero**. Every
product surface uses notion-kit's `Button` with an explicit variant and size, including all
seven device surfaces, which were migrated on explicit approval while deliberately left
unwired. Checkboxes followed: all seven raw checkbox inputs now use notion-kit's `Checkbox`,
so no `type="checkbox"` remains either.

Two findings from that work belong here, because both cost real time and neither is
guessable from the documentation:

- **notion-kit's `Checkbox` renders a `<label>` containing an `aria-checked` indicator span
  and the real control as a *sibling* input, visually hidden with `position: fixed`.** Any
  query scoped inside the testid element finds no input, `aria-checked` is not on the
  control, and clicking the wrapper does not toggle in happy-dom — while a real click on the
  label does. Tests should click the sibling input and read its `checked` property.
- **Base UI's `Select` cannot be driven in the happy-dom test environment at all** — not by
  `pointerdown` plus `click` on the trigger, not by keyboard, and not by setting the value of
  the hidden native `<select>` it renders, which exists but is not the source of truth. This
  is why the ten native selects are a decision rather than a task.

What is left falls into five buckets, and none of them is unexplained work:

1. **The device surfaces' remaining raw buttons** — about 26 across `DeviceOverviewView`,
   `NodeEdgesView`, `BlockSourceView`, `SourceObjectInspectorPanel` and the rest. Their
   legacy-class buttons are migrated, including `PlcSourceCompareDialog`, which had to be
   done in one pass because its Studio dialog chrome and its buttons could not be split
   without leaving one component speaking both vocabularies. What is left is mostly list
   rows, tab strips and chips that should not become `Button` at all. They stay unrenderable
   while issue #109 blocks device selection, so any change there is verified by build and
   suite only.
2. **Two open decisions** — the tag surfaces, where `@notion-kit/ui/tags-input`'s `TagsInput` is the
   direct mapping for the picker and both `TagsInput` and `Autocomplete` are available (see ADR-0005),
   and the ten native `<select>` elements still using `field-input`, which need Base UI's `Select` and
   an `items` collection. `@notion-kit/ui/selectable` was checked for both and is a marquee-selection
   container, not a row or dropdown primitive, so it is not a candidate for either.
3. **By design** — the eight primitives with no notion-kit counterpart, and the catalog
   pages, which deliberately preview Studio's own set.
4. **Should not be converted.** An inventory of the raw `<button>` elements found roughly
   thirty more controls that never used one of the three legacy classes, and they are not
   uniformly "buttons to migrate": full-width list rows (the MCP tool list, version-control
   and settings rows), segmented controls that should become `Tabs` rather than `Button`
   (the hardware tab strip in `MainStudio`, and `VersionControlChanges`), and
   `cursor-default` chips. Converting those to `Button` would be wrong, so this is a
   classification task rather than a sweep.
5. **Findings worth separate issues**, not silent fixes:
   - `NativeStorePanel` has **no consumers**. Nothing imports it, which is why its refresh
     control never rendered during verification; its migrated buttons are dead code.
   - Several `cursor-default` chips are `<button>` elements that are not interactive at
     all — focusable non-actions, a pre-existing semantic smell.

Counting caution: the "legacy class" counts used throughout this migration **understate**
what remains, because the custom-styled category in bucket 4 is invisible to them. "Zero
legacy classes in this file" means exactly that, and not "all controls are notion-kit".

### Second-generation sweep: the surfaces migrated after that

The first sweep ran before roughly twenty further increments, so a second pass covered the
surfaces migrated since: the Workbench Assistant panel, the archive dialog's labels and
input, the tasks panel's per-task controls, the add-task dialog's labels and fields, the
settings search and API-key fields with the refresh and catalog controls, the catalog's
nine pages, and the TIA sessions panel.

All of it renders in both themes with zero console errors, zero 4xx/5xx, zero horizontal
overflow and no mutation requests beyond selection and the assistant's own bootstrap. The
count of legacy-class controls rendered anywhere in the application is **one** — the
DeepSeek balance refresh — on every surface checked, which is the clearest single number
for how far this migration got.

Three assertions in that pass were mis-specified and reported as failures while the
application was correct: two tasks produce two Start-chat controls (not one), the create
dialog does contain a "New task title" field (asserted absent), and the catalog has nine
navigation pages (not one). Recorded because a sweep is only evidence if its own
expectations are checked too.

### WorkbenchNavigator: what worked and what to watch

Migrated in two stages. Stage 1 (done) moves the **menu layer** — `ContextMenu*` and
`DropdownMenu*` — onto notion-kit, covering 43 menu items, 7 labels and 7 triggers.
The buttons, dialogs and inputs in the same file stay on Studio primitives for a
follow-up stage.

Recorded findings, each of which the type checker or the browser caught:

- The item and label shapes are regular enough to transform mechanically, **but a scripted
  trigger rewrite is not viable**: the triggers wrap whole rows, and the row bodies contain nested
  `div`s, so balanced-tag matching mis-terminates. Two scripted attempts were rejected by the type
  checker and reverted. `ContextMenuTrigger` renders a `div` by default, so for those four triggers
  `asChild` can simply be **deleted** — no restructuring at all. Only the three
  `DropdownMenuTrigger`s that wrap a `Button` need `render={<Button …/>}`, and they are small enough
  to hand-edit.
- `DropdownMenuLabel` and `ContextMenuLabel` are **Base UI group labels**: they require a
  `Menu.Group` ancestor and throw `MenuGroupContext is missing` otherwise, which tears down the tree
  through the error boundary. The tests do not open these menus, so only the browser caught it.
  Use the standalone **`MenuLabel`** for a menu heading instead — same `title` prop, no group needed.
- notion-kit sizes menu content to that heading, so item labels truncate. Content needs an explicit
  width (`w-max min-w-56` here) as it did on `ProjectLandingPage`.
- `DropdownMenuSubContent` does not exist in notion-kit; submenus use `DropdownMenuContent`.
- `ContextMenuItem` and `DropdownMenuItem` share one structured API (`icon`, `label`, `desc`,
  `variant`, `onClick`), and `variant="destructive"` maps to `error`.

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
| 2026-09-21 | 1.2 | WorkbenchNavigator menu layer migrated; record the group-label trap, the standalone MenuLabel, the content-width trap and the submenu content difference. |
| 2026-09-21 | 1.3 | Add the migration status with commits; point the remaining mapping gaps at ADR-0005; correct the MainStudio stage, which has nothing to migrate. |
| 2026-09-21 | 1.4 | Record the completion of the last 1:1 surface, the duplicate-close fix, and a whole-application regression sweep of 36 passing steps. |
| 2026-09-21 | 1.5 | Record the endgame inventory by gate, the custom-button classification, and two findings worth separate issues: `NativeStorePanel` has no consumers, and several non-interactive chips are buttons. |
| 2026-09-21 | 1.6 | Record the milestone: the legacy button vocabulary fell from 91 occurrences to zero, including all device surfaces, and the checkboxes followed. Adds the two structural findings that cost the most time (notion-kit's Checkbox DOM shape, and Base UI's Select being undrivable in happy-dom) and revises the remaining inventory by gate. |
