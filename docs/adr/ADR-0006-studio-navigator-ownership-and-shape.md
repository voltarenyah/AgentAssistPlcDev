# ADR-0006 Studio navigator ownership and shape

## Status

Accepted

## Context

Studio currently offers three independent ways to reach the same scope: the left project tree, the
top-bar `Select project` / `Select worktree` selectors, and the three landing pages
(`AllProjectsLandingPage`, `ProjectLandingPage`, `WorktreeLandingPage`). The left navigator alone
carries 24 callbacks and 10 data props (`studio/src/studio/workbench/WorkbenchNavigator.tsx:58-102`)
because it simultaneously acts as a global catalog, a row-action host, and a task list.

The cost of that overlap is measurable:

- Adding one device entry point to the worktree page produced a fourth way to reach a device.
- Selecting a scope replaces the whole main area, so the navigator and the main area disagree about
  "where am I" (`studio/src/studio/workspace/contextDock.ts:1-9` documents exactly this staleness and
  patches around it with exclusion rules).
- Navigation state is spread over four axes: `mainView.kind`, the worktree tab strip, the hardware tab
  strip, and the FlexLayout tabs. The worktree tab strip is rendered by the page
  (`WorktreeLandingPage.tsx`) while the sibling hardware tab strip is rendered by the shell
  (`MainStudio.tsx`), so every new page re-decides where its tabs and state live.
- The navigator's device and hardware rows sit behind a constant
  (`WorkbenchNavigator.tsx:116`, `:530`), and that gate also removed the only entry point to the
  hardware pages: `onSelectHardware` is wired from `MainStudio.tsx:2359` and referenced nowhere else,
  so the `tree` / `bom` / `network` pages the shell still renders are currently unreachable.

This decision supersedes two acceptance criteria of
`docs/ui-spec/task-device-navigation-ui-spec.md`, which chose task-first navigation over a device
subtree:

- **AC-004** — the worktree row shows its tasks "instead of a device subtree".
- **AC-002** — selecting a device-bound task opens task detail "without loading a device snapshot".

Both were a deliberate trade at the time: a tree that mixed devices and tasks spent horizontal space
on indentation and could not give either collection enough room. Flat, independently collapsible
sections remove that contention, so the trade that produced AC-004/AC-002 no longer applies.

## Decision Point

- **Question**: what owns Studio navigation, and in what order are scopes presented to the user?
- **Why a decision exists**: two materially distinct options are credible, and the repository already
  contains partial implementations of both — keep the global tree and hang new pages from it, or move
  scope selection to the landing pages and top-bar selectors and make the navigator scope-local.
- **Scope boundary**: the left navigator, its relationship to the landing pages and top-bar selectors,
  the order in which scopes are presented, and each section's header action. It does not decide the
  contents of any feature page, and it does not decide main-area view or tab-strip ownership.

## Decision

Studio navigation is owned by the scope cascade, not by a global catalog. The left navigator stops
presenting the whole project tree and becomes four flat, independent sections whose visibility is
driven by the current selection:

```
PROJECTS  →  WORKTREE  →  DEVICE  →  TASKS
```

Each section is one level of one cascade: the content of a section is the set of children of the item
selected in the section above it. Sections are not peer filters over the same data.

### Decision Details

| Item | Content |
|------|---------|
| **Decision** | The left navigator is a scope cascade of four flat sections, and the landing pages plus top-bar selectors are the only scope-selection entry points for the catalog level. |
| **Why this** | It matches how the work is actually done (one worktree at a time), removes the duplicated catalog role, and lets each section row keep a 3-dots menu for that object's operations instead of parking them in a subtree. |
| **Known unknowns** | Resolved by ADR-0007: hardware is not a menu entry but one row inside the `DEVICE` section, so a hardware task and a program task are distinguishable by their target. |
| **Reconsider when** | Users need to compare or switch between many worktrees or devices in one session often enough that a scope-local navigator forces repeated round-trips through the landing pages. |

## Rationale

### Options Considered

| Option | Requirement and repository fit | Current-scope benefit | Lifecycle cost | Maintainability | Material trade-offs |
|---|---|---|---|---|---|
| Keep the global tree, add device and task affordances beneath it | Fits today's component; `tasksByWorktree` and `devicesByWorktree` are already passed in | No migration; every existing row action keeps its home | Grows with every new collection; indentation already consumes row width at task depth | One component keeps every catalog, row-action, and task concern | The duplication that caused this ADR remains, and each new page still needs a placement decision |
| Scope-local flat sections | Fits the confirmed usage pattern; the data needed (`workbenches`, `worktrees`, tasks with `deviceId`, devices) is already fetched | Removes the duplicated catalog role and the indentation cost; sections collapse independently | New section, collapse, and scroll state; per-section scroll containers | Each section has one responsibility and one object-scoped row menu, which puts the device callbacks back to work and restores a hardware entry point | Loses the at-a-glance cross-worktree overview, which moves to the project landing page |

**Selected**: scope-local flat sections. The cross-worktree overview it gives up is duplicated by the
landing pages, while the complexity it removes is not duplicated anywhere.

## Consequences

### Positive Consequences

- One cascade defines "where am I": selecting a device no longer invalidates the navigator's meaning.
- Every section row keeps its 3-dots menu, so an object's operations stay next to the object and are
  reachable without navigating to it first.
- The device callbacks that only the removed subtree could reach become live again in the device row
  menu, and the hardware page regains an entry point as a row in the `DEVICE` section (ADR-0007).
- A new collection (for example a worktree-level document list) becomes one more section rather than a
  new tree level.
- The unbound-task group gives device-less tasks a visible home instead of silently dropping them.

### Negative Consequences

- Users can no longer see all worktrees and their task counts at once; they must return to the project
  landing page.
- Four sections need their own scroll and collapse behavior, which the tree did not need.
- Two acceptance criteria of an accepted UI spec must be revised, so that document and any test that
  cites it must change in the same implementation change.

### Neutral Consequences

The cascade does not change any API, persisted format, or task/device relationship. Device binding
already exists on every graph worktree task (`deviceId`), so grouping by device needs no new data.

## Architecture Impact

`WorkbenchNavigator` changes responsibility from "global catalog plus row actions plus task list" to
"render the current cascade". `MainStudio` keeps ownership of selection state and continues to pass
already-fetched collections; the cascade derives section content from the existing
`workbenches` / `worktrees` / `tasksByWorktree` / `devicesByWorktree` inputs. No new dependency,
route, or persisted shape is introduced.

## Implementation Guidance

Derive rather than duplicate: every section's content must be a pure function of the selection one
level above it, and the unbound-task group must be derived from the same task collection as the
device-scoped groups. Keep the top bar and tag filter as fixed regions outside the scrolling sections.

## Related Information

- `docs/ui-spec/studio-information-architecture-ui-spec.md` — the approved surface and interactions
- `docs/design/studio-information-architecture-design.md` — the implementation approach
- `docs/adr/ADR-0007-task-target-model.md` — the hardware target that the `DEVICE` section presents
- `docs/ui-spec/task-device-navigation-ui-spec.md` — AC-002 and AC-004 superseded by this decision
- `studio/src/studio/workbench/WorkbenchNavigator.tsx` — the component that changes responsibility
