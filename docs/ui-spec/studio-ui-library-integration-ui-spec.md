# Studio UI Library Integration UI Specification

## Overview

- Outcome: Studio presents consistent, accessible forms and confirmation dialogs using its existing Orca-derived Shadcn/Radix primitives.
- Scope: The first migration slice covers the workbench creation, archive, TIA comparison, sandbox-denial, and TIA-close dialogs.
- PRD or requirement carrier: User request, 2026-09-15; `docs/STYLEGUIDE.md`.
- Explicit exclusions: PLC/version-control behavior, API contracts, workspace geometry, Orca application-shell components, and new runtime dependencies.

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

## Visual Constraints

| Element / view | Constraint | Repository or approved design source | Acceptance observation |
|---|---|---|---|
| Dialog layer | Use the existing translucent `DialogContent` surface and overlay. | `components/ui/dialog.tsx` | Every migrated dialog uses the same modal surface in light and dark modes. |
| Actions | Use existing button sizes/variants rather than `primary-button`, `secondary-button`, or `icon-button`. | `docs/STYLEGUIDE.md` | Primary, neutral, and destructive actions are visually and semantically distinct. |
| Fields | Use existing input/select focus treatment rather than `field-input`. | `docs/STYLEGUIDE.md` | Keyboard focus is visible and disabled controls retain their existing availability. |

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

## Update History

| Date | Version | Changes |
|---|---|---|
| 2026-09-15 | 1.0 | Specify the first Studio UI library migration slice. |
