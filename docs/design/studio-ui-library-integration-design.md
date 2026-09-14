# Design Document: Studio UI Library Integration

## Overview

- Outcome: Replace repeated hand-written workbench dialog controls with the existing Studio UI library while preserving all workbench behavior.
- Scope: First slice of five workbench dialogs and their focused tests.
- UI Spec: `docs/ui-spec/studio-ui-library-integration-ui-spec.md`
- Governing ADRs: None. The UI library and primitive selection are already established by `docs/STYLEGUIDE.md`.

## Requirement Boundary

- PRD or convergence carrier: Confirmed user request, 2026-09-15.
- Current requirements: Standardize Studio tokens, typography, buttons, fields, and dialogs; reuse the existing library; do not copy Orca business UI.
- Non-goals: New dependencies, a global state layer, API changes, or PLC workflow changes.
- Open requirement fields: None for the first migration slice.

## Acceptance Criteria

- **AC-001** — When a first-slice workbench dialog opens, the system shall use the existing Radix-backed dialog primitive with an accessible title and description. Source: user request and UI Spec.
- **AC-002** — When users operate forms and actions in a migrated dialog, the system shall preserve existing values, callbacks, validation, busy states, and safety semantics. Source: user request and existing behavior.
- **AC-003** — When a control role is covered by an existing primitive, the migrated dialog shall use that primitive rather than a duplicate custom control class. Source: `docs/STYLEGUIDE.md`.

## Existing Evidence

| Evidence | Location | Design effect |
|---|---|---|
| Existing library | `studio/src/components/ui/` | Reuse primitives; do not add a new dialog/button/form abstraction. |
| Representative composition | `studio/src/studio/PlcSourceCompareDialog.tsx` | Follow controlled `Dialog open` / `onOpenChange` composition. |
| Repeated legacy classes | `studio/src/assets/main.css` and workbench dialogs | Migrate callers first; remove a class only after it has no callers. |
| Verification | colocated workbench dialog tests and `npm test -- --run` | Preserve callbacks and user-observable gating. |

## Design

### Selected Design

Migrate each dialog directly to `Dialog`, `DialogContent`, `DialogHeader`,
`DialogTitle`, `DialogDescription`, and `DialogFooter`. Reuse `Button` for all
actions, applying `destructive` only to discard behavior. Reuse `Input` for
text fields; retain native selects where their existing options are the
smallest sufficient behavior. Existing local state and callbacks remain in the
same owning component.

The first slice deliberately does not create a `WorkbenchDialog` wrapper:
the existing `Dialog` composition is sufficient and the five dialogs have
different layout/busy lifecycles. A wrapper would add a new library surface
without eliminating a verified shared behavior gap.

### Change Surface

| Responsibility or expected file | Change | Governing source | Unaffected boundary to preserve |
|---|---|---|---|
| Workbench dialogs | Compose existing dialog, button, and field primitives. | AC-001–003 | Props, callbacks, validation, safety text, and local state. |
| Dialog tests | Update selectors/add interaction coverage when rendered primitive semantics change. | AC-002 | Observable business behavior. |
| `main.css` | Remove legacy utility classes only once the first slice no longer uses them. | AC-003 | Classes still used outside the slice. |

### Components and Flow

The parent retains conditional rendering and owns each callback. A migrated
dialog maps the current open state to `Dialog open`; dismissal maps to the
existing close callback. Controls invoke the same local event handlers, so no
state or API boundary changes.

## Implementation Approach

- Slicing: vertical, by dialog.
- Dependency order: add/adjust focused behavioral tests, migrate a dialog, run its test, then repeat; remove dead legacy styles only after migration.
- First observable checkpoint: Refresh dialog still blocks Apply until both selection and title are present while rendering standard buttons/field.
- Rationale: The existing primitive library covers the required semantics and allows low-risk replacement without changing domain ownership.

## Verification Strategy

| Claim / AC | Level | Repository command or operation | Observable pass condition |
|---|---|---|---|
| Refresh gating | L1 | `npm test -- --run src/studio/workbench/RefreshDialog.test.tsx` | Selected paths and title reach the unchanged callback only when valid. |
| Dialog flows | L1 | Focused colocated dialog tests | Inputs, callbacks, busy state, and safety actions retain behavior. |
| Studio compatibility | L2 | `npm test -- --run`, `npm run lint`, `npm run build` | All Studio tests, lint, and production build pass. |
| Visual modal behavior | L3 | `launch.ps1 -NoBuild` plus browser smoke | Modal surface, focus, and actions are visible in the real Studio. |

## Material Risks

| Risk | Evidence | In-scope response or verification |
|---|---|---|
| Radix defaults dismiss a dialog unexpectedly | Existing manual overlays did not expose explicit outside-dismiss behavior. | Preserve busy safeguards and test/inspect dismissal behavior per dialog. |
| Dense workbench dialogs lose compactness | Existing classes use smaller Studio-specific sizes. | Use supported `xs`, `sm`, `icon-sm`, and focused visual inspection. |

## References

- `docs/STYLEGUIDE.md`
- `docs/ui-spec/studio-ui-library-integration-ui-spec.md`
- `studio/src/components/ui/dialog.tsx`

## Update History

| Date | Version | Changes |
|---|---|---|
| 2026-09-15 | 1.0 | Define the first Studio UI library migration slice. |
