# TIA Compare Optional Hardware Verification UI Specification

## Overview

- Outcome: users can omit the expensive hardware verification while still receiving an unambiguous managed-source and safety result.
- Scope: Version Control Compare trigger and its completed-result presentation.
- Requirement carrier: GitHub issue #89.
- Explicit exclusions: safety verification remains enabled; the preference is not persisted; no hardware result cache is introduced.

## Design Evidence

| Source | Path / identifier | Decision supplied |
|---|---|---|
| Existing Compare trigger | `studio/src/studio/version-control/VersionControlPanel.tsx` | Place the option beside the existing Compare with TIA action. |
| Existing result presentation | `studio/src/studio/version-control/VersionControlCompare.tsx` | Reuse the inline completed-result card and timing list. |
| Requirement | GitHub issue #89 | Default-on hardware selection and partial-result wording. |

## UI Surface and Flow

| View or state | Entry / trigger | User-visible result | Governing requirement |
|---|---|---|---|
| Version Control header | Panel is visible | A checked `Verify hardware configuration` checkbox appears beside Compare with TIA. | #89 |
| Full compare | User leaves the checkbox checked and clicks Compare | Existing source, safety, and hardware verification run; a clean result may say `TIA matches master`. | #89 |
| Partial compare | User clears the checkbox and clicks Compare | Source and safety verification run; hardware is not invoked. A clean result says managed source and safety match while hardware was not checked. | #89 |
| Partial result | Hardware was skipped | No hardware-difference/accept control appears; timing details explicitly record the skipped coverage. | #89 |

## Components and Interactions

| Component responsibility | Reuse / extend | Inputs or state | Interaction and response | Governing source |
|---|---|---|---|---|
| Compare trigger | Extend `VersionControlPanel` | local `verifyHardware` state, initially `true` | Checkbox changes only the next Compare request. | #89 |
| Compare request | Extend existing prop flow | `includeHardware` boolean | Passes the choice through Changes and Compare to the API client. | #89 |
| Completed result | Extend `VersionControlCompare` | result coverage | Uses coverage, not a null hardware object, to choose full or partial wording. | #89 |

## Accessibility Requirements

| Component / interaction | Behavior | Source | Acceptance observation |
|---|---|---|---|
| Hardware checkbox | Native labeled checkbox, keyboard operable, checked by default. | #89 | It can be focused, toggled, and its label communicates the covered domain. |

## Acceptance Traceability

| AC / requirement | View, component, or interaction | Observable UI proof |
|---|---|---|
| Default full compare | Header checkbox | It is checked initially and existing callers run the full request. |
| Partial coverage clarity | Completed result | It never says `TIA matches master` when hardware was skipped. |
| No hardware acceptance | Partial result | No hardware accept control is rendered. |

## Update History

| Date | Version | Changes |
|---|---|---|
| 2026-09-08 | 1.0 | Initial specification for #89. |
