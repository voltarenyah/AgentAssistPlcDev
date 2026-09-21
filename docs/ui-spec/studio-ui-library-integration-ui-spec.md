# Studio UI Library Integration UI Specification

> **Partly superseded.** The primitive-reuse rule below — reuse the existing Orca-derived
> Shadcn/Radix primitives and add no new dependencies — is replaced by
> `docs/adr/ADR-0004-notion-kit-design-token-authority.md` and
> `docs/ui-spec/notion-kit-ui-migration-ui-spec.md`, which make `@notion-kit/ui` the primitive and
> token foundation. The accessibility requirements, the Visual Checkpoint Rule, and the preserved
> dialog and dock behaviors recorded here still apply.

## Overview

- Outcome: Studio presents consistent, accessible forms and confirmation dialogs using its existing Orca-derived Shadcn/Radix primitives.
- Scope: The first migration slice covers the workbench creation, archive, TIA comparison, sandbox-denial, and TIA-close dialogs, plus the left Projects Dock navigation surface and the main landing-page typography pass.
- PRD or requirement carrier: User request, 2026-09-15; `docs/STYLEGUIDE.md`.
- Explicit exclusions: PLC/version-control behavior, API contracts, workspace geometry, Orca application-shell components, new runtime dependencies, and a global color-token/theme redesign. The existing light/dark color tokens remain the source of truth for this slice; global color improvements are deferred to a separate project.

## Design Evidence

| Source | Path / identifier | Decision supplied |
|---|---|---|
| UI policy | `docs/STYLEGUIDE.md` | Reuse `Button`, fields, and `Dialog` primitives; no one-off replacements. |
| Existing dialog pattern | `studio/src/studio/PlcSourceCompareDialog.tsx` | Controlled `Dialog` calls `onClose` only when it is dismissed. |
| Existing primitive | `studio/src/components/ui/dialog.tsx` | Provides modal semantics, focus management, overlay, title, description, and footer. |
| Existing primitives | `studio/src/components/ui/button.tsx`, `input.tsx`, `select.tsx` | Provide standard variants, sizing, focus and disabled presentation. |

## UI Surface and Flow

| View or state | Entry / trigger | User-visible result | Governing requirement / AC |
|---|---|---|---|
| Create workbench | User selects create workbench | A labelled modal retains project/session choice, validation, browse, and submit behavior. | AC-001 |
| Archive project | User starts an archive | A labelled modal retains export path, archive mode, busy state, and error message. | AC-002 |
| TIA comparison | User reviews a comparison | The dialog retains approval selection and commit-title gating before apply. | AC-003 |
| Sandbox or TIA-close confirmation | A guarded operation needs user acknowledgement | The modal preserves its safety wording and action ordering. | AC-004 |

## Components and Interactions

| Component responsibility | Reuse / extend / new | Inputs or state | Interaction and response | Governing source |
|---|---|---|---|---|
| Modal root/content | Reuse `Dialog` / `DialogContent` | Existing open condition and close callback | Escape, overlay, and close action call the existing close callback; busy flows remain non-dismissible where they already are. | AC-001–004 |
| Titles and descriptions | Reuse `DialogHeader`, `DialogTitle`, `DialogDescription` | Existing copy | Dialog receives an accessible name and description. | AC-001–004 |
| Action controls | Reuse `Button` variants | Existing disabled/busy states and callbacks | Default is affirmative; outline/secondary is neutral; destructive is reserved for discard/unsafe action. | AC-001–004 |
| Inputs and selects | Reuse `Input` / `Select` where native behavior is compatible | Existing value, validation, and `onChange` | Preserve labels, placeholders, read-only state, and keyboard interaction. | AC-001–003 |
| Create-workbench mode switch | Reuse `ToggleGroup` with a local animated active-surface indicator | Existing `session` / `file` mode state | The two options remain a single accessible selection while the active surface slides between equal-width options. | AC-001 |
| Create-workbench progress detail | Reuse `ToggleGroup` to switch the existing workflow/source lists | Existing operation-status rows | Only one detail list is mounted at a time; switching views preserves the same status data and scroll affordance without changing the modal size. | AC-001 |
| Projects Dock header actions | Reuse `Button` icon variants | Existing refresh/create callbacks and loading state | Header actions retain their callbacks, have accessible names, and use the same 32px control height. | Dock-001 |
| Projects Dock tree rows | Reuse existing icons and context menus | Workbench, worktree, hardware, and device projections | Rows use a shared compact rhythm (`min-h-8` for workbenches, `min-h-7` for children), `text-xs` labels, and preserve context-menu actions and selection callbacks. | Dock-002 |
| Main landing-page typography | Reuse existing Tailwind typography tokens | MainStudio error/empty states, ProjectLandingPage, WorktreeLandingPage | Replace one-off 8–11px labels with the shared title/body/annotation hierarchy without changing content or behavior. | Main-001 |

## Visual Constraints

| Element / view | Constraint | Repository or approved design source | Acceptance observation |
|---|---|---|---|
| Dialog layer | Use the existing translucent `DialogContent` surface and overlay. | `components/ui/dialog.tsx` | Every migrated dialog uses the same modal surface in light and dark modes. |
| Actions | Use existing button sizes/variants rather than `primary-button`, `secondary-button`, or `icon-button`. | `docs/STYLEGUIDE.md` | Primary, neutral, and destructive actions are visually and semantically distinct. |
| Fields | Use existing input/select focus treatment rather than `field-input`. | `docs/STYLEGUIDE.md` | Keyboard focus is visible and disabled controls retain their existing availability. |
| Dialog typography | Use the compact-dialog hierarchy: `text-sm` semibold title, then `text-xs` for fields, actions, descriptions, and annotations. | `docs/STYLEGUIDE.md` | A dialog uses no more than three font scales; explanatory text is differentiated by color or weight, not a string of tiny sizes. |
| Create-workbench project path | Use ordinary `text-xs` application text in the editable project-path field and resolved-location preview. | User feedback, 2026-09-15 | The path remains readable without a bold or monospaced visual emphasis. |
| Projects Dock rhythm | Keep Dock width unchanged; align row heights, indentation, icon sizing, and label scale across the tree. | User feedback, 2026-09-15 | Expanding or selecting a project does not introduce inconsistent row density or tiny unreadable labels. |
| Projects Dock selection | Use existing `accent` and `border` tokens for workbench, worktree, hardware, and device selection. | Existing Studio tokens | The active row is recognizable in both themes without introducing a new color system. |
| Main landing-page hierarchy | Keep page titles at `text-lg`/`text-xl`, body and controls at `text-xs`/`text-sm`, and annotations at `text-xs`. | `docs/STYLEGUIDE.md`, user feedback, 2026-09-15 | Project, Worktree, Hardware and empty/error states use no more than three readable scales. |

### Visual Checkpoint Rule

Before moving from one dialog or panel group to the next, render the changed
worktree in the browser and compare it with the pre-migration surface. Verify
the modal width, title/action hierarchy, dense-control height, overflow, and
light/dark contrast at ordinary desktop width. A passing component test or
production build is not sufficient proof of visual proportion.

## Accessibility Requirements

| Component / interaction | Keyboard, semantic, announcement, or contrast behavior | Source | Acceptance observation |
|---|---|---|---|
| Modal | Dialog has an accessible title/description and focus is contained while open. | `Dialog` primitive | Keyboard focus does not reach content behind the modal. |
| Close control | Close is named, keyboard reachable, and disabled during existing busy states. | Current dialog behavior | Assistive technology announces its purpose and disabled state. |
| Safety actions | Destructive close-without-save action is labelled as destructive, while Cancel remains neutral. | `docs/STYLEGUIDE.md` | Actions cannot be mistaken for one another. |

## Acceptance Traceability

| AC / requirement | View, component, or interaction | Observable UI proof |
|---|---|---|
| AC-001 | Create workbench dialog | Existing session/file selection and submit validation work inside the shared modal primitive. |
| AC-002 | Archive dialog | Browse, busy, error, Cancel, and Archive behaviors are unchanged. |
| AC-003 | Refresh dialog | Selecting a change and entering a title still gates Apply and forwards the same values. |
| AC-004 | Safety dialogs | Sandbox acknowledgement and TIA close choices preserve their callbacks and safety labels. |
| Dock-001 | Projects Dock header | Refresh and create actions remain keyboard reachable and preserve their callbacks. |
| Dock-002 | Projects Dock tree | Row selection, expansion, availability labels, and context-menu actions remain functional with the normalized layout. |
| Main-001 | Main landing pages | Loading, error, empty, metadata, tab, table, and modified-block views retain their behavior with normalized readable typography. |

## Update History

| Date | Version | Changes |
|---|---|---|
| 2026-09-15 | 1.0 | Specify the first Studio UI library migration slice. |
| 2026-09-15 | 1.1 | Add the first left Projects Dock layout and typography pass; Dock width remains unchanged. |
| 2026-09-15 | 1.2 | Add the main landing-page typography pass. |
