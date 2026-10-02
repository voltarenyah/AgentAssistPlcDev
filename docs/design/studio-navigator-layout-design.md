# Design Document: Studio navigator section sizing

## Overview

The left navigator's four sections claim an equal share of the dock whatever they hold:
`WorkbenchNavigator.tsx:148` gives every section `flex min-h-0 flex-1`, so `flex-basis: 0%` and
`flex-grow: 1` divide the column evenly and content never influences the split. A `PROJECTS` list
holding one workbench reserves the same height as a `TASKS` list holding twenty tasks, the unused
room shows as empty space between the sections, and nothing the user does changes the split.

This design makes a section's height follow its content by default, keeps every section header on
screen when the content does not fit, and lets the user override a pair of adjacent sections by
dragging the separator between them.

Governing decisions: [ADR-0008](../adr/ADR-0008-navigator-section-sizing.md) (the sizing model),
[ADR-0006](../adr/ADR-0006-studio-navigator-ownership-and-shape.md) (the section set and the
bounded-region rule this keeps). UI surface:
[studio-information-architecture-ui-spec.md](../ui-spec/studio-information-architecture-ui-spec.md)
v1.1.

## Requirement Boundary

In scope: the vertical allocation of dock space between the navigator's sections, the scroll owner it
implies, and the separator interaction that overrides it.

Out of scope: the section set and their contents, the scope cascade, the tag-filter rule, the row
contracts and their menus, the dock's width, section reordering, automatic collapse of shallower
sections, and persisting a dragged height across sessions.

## Acceptance Criteria

- **AC-012** — **When** the user drags the separator between two adjacent sections, or focuses it and
  presses `ArrowUp`/`ArrowDown`, **then** the upper section shall grow and the lower one shall shrink
  by the same amount, each staying at or above its minimum height, and no third section shall change
  height. Source: ADR-0008.
- **AC-013** — **The** system **shall** size each section to its content by default, so a section
  holding one row occupies one row's height and a collapsed section occupies its header only, leaving
  no reserved space between the sections. Source: ADR-0008.
- **AC-014** — **When** the sections' content is taller than the dock, **the** system **shall** shrink
  the sections and scroll each section's own body, keeping every section header visible, rather than
  scrolling the column. Source: ADR-0006, ADR-0008.

## Existing Evidence

| Evidence | Location | Design effect |
|---|---|---|
| Every section is an equal flex share | `WorkbenchNavigator.tsx:148` (`flex min-h-0 flex-1`) | The reported cause. The section stops growing and starts sizing to content. |
| A section body already owns a bounded scroll region | `WorkbenchNavigator.tsx:164` (`min-h-0 flex-1 overflow-y-auto`) | The scroll owner already exists and stays; only the section's outer share changes. |
| The column is a flex column with `overflow-hidden` | `WorkbenchNavigator.tsx` (`flex min-h-0 flex-1 flex-col gap-2 overflow-hidden p-2`) | The column keeps this: it is what stops a long section from pushing the headers away. |
| A pointer and keyboard resize contract already exists in this subsystem | `WorktreeTasksPanel.tsx:222-285` (`role="separator"`, window pointer handlers, `ArrowLeft`/`ArrowRight`, cleanup on unmount, `body.style.cursor`) | The separator reuses that contract with `aria-orientation="horizontal"` and `ArrowUp`/`ArrowDown` instead of inventing a second interaction. |
| A resize starts from measured geometry | `WorktreeTasksPanel.tsx:230` (`measureTaskListColumns` on the DOM at drag start) | A drag measures the two neighbours the same way; only a dragged section needs a stored number. |
| Sections are conditionally rendered | `WorkbenchNavigator.tsx` (`showWorktreeSection`, `renderDeviceSection`, `renderTasksSection`) | Separators go between the sections that are actually rendered, so the count changes with the selection. |

## Design

### Selected Design

| Layer | Change | Why this shape |
|---|---|---|
| Section box | `flex min-h-0 flex-1 flex-col` → `flex min-h-0 flex-initial flex-col` (`flex: 0 1 auto`) plus a minimum height when expanded | `flex-grow: 0` makes the box its content height; `flex-shrink: 1` lets it give height back when the column overflows; the floor keeps its header reachable. |
| Section body | Stays `min-h-0 flex-1 overflow-y-auto` (`flex: 1 1 auto`) | Its `auto` basis is its content, which is what the section box measures; when the box is dragged taller the body grows into it, and when the box is squeezed the body scrolls. |
| Column | Unchanged (`gap-2 overflow-hidden`) | It is the reason a long section cannot push its neighbours' headers off screen (AC-014). |
| Height overrides | `useState<Record<string, number>>` keyed by section id, applied as an inline `height` only while the section is expanded | Collapse must release the height, or the sections below could not move up (AC-013). Nothing outside the navigator reads it. |
| Separators | Rendered between adjacent rendered sections, `role="separator"` + pointer and keyboard handlers | One separator per adjacent pair, none before the first or after the last (AC-012). |

The default path measures nothing: a section with no override is `flex: 0 1 auto`, so the browser
already knows its height. Numbers appear only after a drag, and only for the two sections that drag
touched.

### Change Surface

| File | Change | ACs | Preserved |
|---|---|---|---|
| `studio/src/studio/workbench/WorkbenchNavigator.tsx` | Move `PROJECTS` and `WORKTREE` into one ordered list of rendered sections; render a separator between adjacent ones; give each section a content-sized box with a minimum height and an optional dragged height; add the separator's pointer and keyboard resize | AC-012, AC-013, AC-014 | The section set, their titles and header actions, the collapse contract (`aria-expanded`/`aria-controls`), the tag-filter rule, every row and its menu, the cascade |
| `studio/src/studio/workbench/WorkbenchNavigator.test.tsx` | Cases for the three criteria | AC-012, AC-013, AC-014 | The existing AC-001…AC-011 cases |
| `docs/ui-spec/studio-information-architecture-ui-spec.md` | The sizing, scroll and separator rows | — | Every other criterion |

No API, route, prop, contract, or persisted shape changes. `MainStudio` is untouched.

### Components and Flow

| Component | Input | Interaction and response |
|---|---|---|
| `NavigatorSection` | section id, title, header action, collapsed state, optional dragged height, minimum height, resize callbacks | Header toggles collapse. Expanded, the box is `flex: 0 1 auto` at `max(content, floor)` or at its dragged height; collapsed, the body is `hidden` and the box holds the header only. |
| Section separator | the two adjacent section ids and their heights | `pointerdown` measures both boxes, then `pointermove` on the window applies `upper += Δ` / `lower -= Δ` clamped so neither goes below its floor; `pointerup`/`pointercancel` release. `ArrowUp`/`ArrowDown` apply a fixed step with the same clamp. |

### Contracts, State, and Persistence

| Boundary | Input / exact format | Output | Error or state behaviour | Compatibility |
|---|---|---|---|---|
| Section height override | component state only: `Record<sectionId, number>` in CSS pixels | inline `height` on the section while expanded | Never below the floor, never more than the column can give; cleared by a reload | Internal only; no prop, route, or stored shape |
| Separator accessibility | `role="separator"`, `aria-orientation="horizontal"`, `aria-label="Resize <section> section"`, `aria-valuenow`/`aria-valuemin`/`aria-valuemax` | the same values a screen reader announces for the task list's column separators | `aria-valuenow` reflects the dragged height, or the measured height before any drag | Matches the existing resize contract |

### Repository-Owned Migration, Flag, or Deployment Behavior

None. No persisted shape exists, so there is nothing to migrate, and the change is reversible by
removing the state and the separators.

## Implementation Approach

- **Order**: content sizing and the collapse reflow first (AC-013, AC-014) — they are the reported
  problem and need no new interaction; then the separators (AC-012), which only make sense once the
  default height is content-driven; then the browser pass.
- **Separator placement**: build the rendered sections as an ordered array and interleave separators,
  so the count follows the selection instead of being hardcoded per pair.
- **Reuse over invention**: the separator copies `WorktreeTasksPanel`'s resize contract rather than
  introducing a second interaction model. A shared hook is deferred: the two sites measure different
  geometry (columns vs rows) and share only the handler shape, so extracting one now would add an
  abstraction with two call sites and no third.
- **First observable checkpoint**: with one workbench and one device selected, the dock shows compact
  `PROJECTS` and `DEVICE` sections with no reserved gaps, and collapsing `PROJECTS` moves `WORKTREE`
  up.

## Verification Strategy

| Claim / AC | Level | Command or operation | Observable pass condition |
|---|---|---|---|
| A section is content-sized until dragged, and collapse releases its height | L1 | `npm test -- --run` in `studio/`, new cases in `WorkbenchNavigator.test.tsx` | No section carries an explicit height before a drag; a collapsed section carries none and its body is `hidden` |
| A separator resizes exactly its pair, by pointer and by keyboard | L1 | the same lane | `ArrowUp`/`ArrowDown` give the upper section the step and take it from the lower one, both clamped at the floor, and a third section's height is unchanged |
| Every section header stays visible while a body scrolls | L1 | the same lane | The column still does not scroll (`overflow-hidden`) and every section keeps its own scrollable body |
| The dock follows content, and dragging follows the cursor | L3 | the running app via `.\launch.ps1`, driven with Playwright | A screenshot shows no gap reserved between sections; a drag changes the two boxes and no third; collapsing an upper section moves the lower ones up; the console shows no local-application error |
| Nothing else regressed | L1 | `npx tsc -b`, `npm run lint`, the full `vitest` lane | Type check clean; no new lint warning; the suite passes as before |

## Material Risks

| Risk | Why it could happen | Mitigation |
|---|---|---|
| A section is squeezed to an unusable height | `flex-shrink` applies to every section once the content overflows | Every expanded section has a minimum height of its header plus one row, and a resize clamps to it |
| The content-sized default does not resolve, so sections collapse to their headers | A body with `flex-basis: 0%` contributes no height to an `auto`-basis parent | The body keeps `flex: 1 1 auto`, whose basis is its content; the first checkpoint verifies the rendered result, not just the class names |
| A stored height becomes wrong when the selection changes the section count | Overrides are keyed by section id and sections appear and disappear with the target | An override applies only to its own section; the other sections keep sizing to content, and the column's shrink absorbs the difference |
| The separators are unreachable or invisible | A one-pixel target, or a drag that only works with a pointer | The hit area is wider than the rule, the separator is focusable, and the arrow keys resize it |

## References

- `docs/adr/ADR-0008-navigator-section-sizing.md`
- `docs/adr/ADR-0006-studio-navigator-ownership-and-shape.md`
- `docs/ui-spec/studio-information-architecture-ui-spec.md`
- `studio/src/studio/workbench/WorkbenchNavigator.tsx`
- `studio/src/studio/workbench/WorktreeTasksPanel.tsx`

## Update History

| Date | Version | Changes |
|---|---|---|
| 2026-10-02 | 1.0 | Initial design for content-sized, resizable navigator sections. |
