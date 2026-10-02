# ADR-0008 Navigator section sizing: content height with a dragged override

## Status

Accepted

## Context

Every navigator section is a flex item that claims an equal share of the left dock, whatever it
holds:

```tsx
// studio/src/studio/workbench/WorkbenchNavigator.tsx:148
<section data-navigator-section={id} className="flex min-h-0 flex-1 flex-col">
```

`flex-1` sets `flex-basis: 0%` with `flex-grow: 1`, so the sections present are divided evenly and
content never influences the split. A `PROJECTS` list holding one workbench reserves the same
quarter of the dock as a `TASKS` list holding twenty tasks, and the unused room inside each section
is dead space rather than space another section could use. The layout is also fixed: nothing the
user does can change the split.

What the current specification requires of this area
(`docs/ui-spec/studio-information-architecture-ui-spec.md`):

- **Visual Constraints**: "Each section gets its own bounded scroll region with a sticky header; the
  column itself does not scroll its headers away" and "The section containing the current scope keeps
  at least enough height to show its rows."
- **State / Display Detail**: a section is absent when the selection does not reach it, and an empty
  section shows its empty-state affordance.
- **Open User Decisions**: automatic collapse of shallower sections is deferred, and "sections must
  already support independent collapse and a bounded scroll region so it can be added without
  relayout".

The reported problem is exactly the `flex-1` split: the sections do not follow their content, they
leave empty space in the middle of the dock, and their heights cannot be adjusted.

## Decision Point

- **Question**: how does the navigator allocate vertical space between its sections?
- **Why a decision exists**: at least three materially distinct allocations fit this repository and
  each implies a different scroll owner, so the choice cannot be settled inside the component.
- **Scope boundary**: the vertical allocation of the navigator's sections, the scroll owner it
  implies, and how far a user can override it. It does not re-open the section set, the cascade, the
  filter rule, or what each section contains.

## Decision

A section's height follows its content. The user may override the height of two adjacent sections by
dragging the separator between them, for the current session. The column never scrolls its own
headers away: when the sections no longer fit, they shrink and each section's own body scrolls.

### Decision Details

| Item | Content |
|------|---------|
| **Decision** | Content-sized sections (`flex: 0 1 auto`) with a minimum height floor, plus a pairwise drag separator that redistributes height between two adjacent sections. |
| **Why this** | It removes the dead space at its cause instead of hiding it, and it keeps every section header on screen, which is the property the deeper sections depend on to stay reachable. |
| **Known unknowns** | Whether a user ever wants a height that outlives the session. The override is component state, so adding persistence later changes no contract. |
| **Reconsider when** | Users ask for their section split to survive a reload, or the section set becomes user-orderable, which would make the separators part of an ordering interaction too. |

### Sizing model

| Option | Fit | Cost |
|---|---|---|
| Keep `flex-1` and add separators | Smallest diff | The reported dead space stays: a section with one row still reserves an equal share until the user drags it |
| Content-sized sections, column scrolls | Simple CSS; long lists simply grow | A long list pushes the deeper sections' headers off screen, reversing the bounded-region decision this specification already took |
| Content-sized sections, each shrinks and scrolls its own body | Keeps every header visible and puts space where content is | Needs a minimum height floor, or a short section is squeezed to nothing by its neighbours |

**Selected**: the third. Space follows content, and the scroll owner stays the section.

## Rationale

### Options Considered

| Option | Requirement and repository fit | Current-scope benefit | Lifecycle cost | Maintainability | Material trade-offs |
|---|---|---|---|---|---|
| A. Equal share plus separators | Keeps the shipped scroll model; `WorktreeTasksPanel`'s column resize is a working precedent | Smallest change; no scroll-model risk | The dead space and the fixed initial split remain, so the reported problem only becomes adjustable, not solved | The separators would be the only thing making the layout usable | Solves "adjustable" and not "follows content" |
| B. Content-sized, whole column scrolls | Least code: drop `flex-1` and let the column scroll | Every section is exactly its content | Contradicts the bounded-region constraint; with several devices and tasks the `DEVICE` and `TASKS` headers scroll away and the deeper sections become hard to reach | One scroll owner instead of five, but it is the wrong one | Cheapest now, worst for a dock that must keep five section headers reachable |
| C. Content-sized, shrinking, per-section scroll, dragged override | Keeps the bounded-region and header-visibility constraints; reuses the existing resize contract | Fixes the cause; the override is an addition rather than the mechanism | One minimum-height floor and one piece of component state | One layout rule, stated once: content decides, the user may overrule, the section owns its scroll | A dragged section can be made taller than its content, which is the user's choice |

**Selected**: C. A makes the symptom adjustable, B trades the symptom for a worse one.

## Consequences

### Positive Consequences

- A section that holds one row occupies one row. The dock's empty space moves to where no section
  needs it, and a collapsed section releases its space so the sections below move up.
- Every section header stays visible while any single section scrolls, which is what keeps `DEVICE`
  and `TASKS` reachable when `PROJECTS` or `WORKTREE` is long.
- Automatic collapse of shallower sections stays addable without relayout: collapse already frees
  height, and the sections already size to what remains.

### Negative Consequences

- A dragged height is session state, so a reload returns to the content-sized layout. A user who
  wants a particular split every time has to redo it.
- A section squeezed to its floor scrolls internally at a smaller height than its content, which can
  show one row where two fit.
- Two adjacent segments can now differ in height where they were equal before, so the dock looks less
  uniform.

### Neutral Consequences

The section set, the cascade, the filter rule, the tag projection, and every row contract stay as
they are. No route, prop, API, or persisted shape changes.

## Architecture Impact

`WorkbenchNavigator` owns the section heights and the separators; nothing outside it learns about
either. The dock's own layout, the flexlayout host, and `MainStudio`'s props are untouched.

## Implementation Guidance

Derive the default from content, not from a measurement: a section is `flex: 0 1 auto`, so the
browser already knows its height. Only a drag needs numbers, so only a drag records them. Reuse the
resize contract `WorktreeTasksPanel` already implements for its list columns — `role="separator"`,
a pointer drag on `window`, arrow keys, and cleanup on unmount — with `aria-orientation="horizontal"`
and `ArrowUp`/`ArrowDown`, so both resize interactions behave the same way.

## Related Information

- `docs/ui-spec/studio-information-architecture-ui-spec.md` — the surface whose sizing rows this
  decision revises
- `docs/adr/ADR-0006-studio-navigator-ownership-and-shape.md` — owns the section set and the
  bounded-region decision this keeps
- `studio/src/studio/workbench/WorkbenchNavigator.tsx:148` — the `flex-1` split this replaces
- `studio/src/studio/workbench/WorktreeTasksPanel.tsx:222-285` — the resize contract to reuse
