# Safety Change Detail UI Specification

## Overview

- Outcome: TIA comparison identifies changed safety F-blocks as individually selectable, commit-ready safety rows, while retaining an honest aggregate-only presentation for legacy baselines and independent Safety change evidence in history.
- Scope: `VersionControlCompare`, `VersionControlChanges`, `VersionControlHistory`, and the mounted Changes/History views owned by `VersionControlPanel`.
- PRD or requirement carrier: Confirmed Medium fullstack outcome supplied to this workflow: structured safety change rows, legacy aggregate fallback, existing commit-message selection flow, and history safety badges.
- Explicit exclusions: no new action button; no automatic SVN savepoint or archive; no user selection or automatic selection of `Untrackable change` when committing safety rows; no change to ordinary source-change behavior.

## Design Evidence

| Source | Path / identifier | Decision supplied |
|---|---|---|
| Existing UI | `studio/src/studio/version-control/VersionControlCompare.tsx` | TIA comparison owns comparison result rows and the amber compact alert treatment for safety information. |
| Existing UI | `studio/src/studio/version-control/VersionControlChanges.tsx` | Changes owns the shared message field, selected TIA paths, and one global commit path for selected comparison rows. |
| Existing UI | `studio/src/studio/version-control/VersionControlHistory.tsx` | History owns compact `TriangleAlert` amber badges for untrackable and savepoint safety evidence. |
| Existing UI | `studio/src/studio/version-control/VersionControlPanel.tsx` | Changes and History remain mounted while inactive; it joins Git commits and SVN savepoints for the History view. |

## UI Surface and Flow

| View or state | Entry / trigger | User-visible result | Governing requirement / AC |
|---|---|---|---|
| Changes: detailed safety comparison | A completed TIA comparison has per-F-block safety records that differ from the baseline. | A Safety program changed banner appears before ordinary source differences. It contains one selectable F-block row for each returned safety record and ordinary source rows remain visible and independently selectable. | R1, AC-1 |
| Changes: select safety row | The user checks or clears one detailed safety row. | Only that row's path is added to or removed from the selected TIA paths sent to the existing global commit workflow. Other safety rows and ordinary source rows keep their current selections. | R1, R3, AC-1, AC-3 |
| Changes: commit selected safety rows | At least one safety row is selected and the existing commit message is non-empty. | The existing Commit control commits the selected paths through the established TIA synchronization flow. The comparison selection and message clear after a successful commit as they do for ordinary selected TIA rows. | R3, AC-3 |
| Changes: safety commit confirmation | A Commit action succeeds and its committed selection contained one or more detailed safety rows. | Show the existing transient notification treatment with this exact text: `Safety change committed to Git. Create an SVN savepoint separately to capture the TIA project.` The notice does not present a new action or start an SVN savepoint. | R3, R4, AC-3, AC-5 |
| Changes: legacy or live aggregate fallback | Safety signatures differ but the baseline or live result lacks per-block signature maps. | The amber safety banner displays the PLC/device identity, aggregate baseline signature, aggregate current signature, and an unavailable-detail message. It displays no selectable safety rows. | R2, AC-2 |
| Changes: clean source comparison with safety difference | TIA comparison has no ordinary source differences but has detailed or aggregate safety differences. | The clean-source copy says tracked PLC source matches master while the safety banner remains visible; it does not claim that TIA fully matches master. | Preserved `VersionControlCompare` behavior, AC-1, AC-2 |
| History: Git safety evidence | A Git history item represents a safety change, whether or not it also lists ordinary source files. | That Git commit displays an amber `TriangleAlert` badge labelled `Safety change`, independently of the ordinary source file summary and any separate untrackable badge. | R4, AC-4 |
| History: native savepoint/archive evidence | A manual SVN savepoint/archive record indicates a safety change. | The savepoint/archive timeline item keeps its existing native identity and shows a separate amber `TriangleAlert` badge labelled `Safety change`. Its presence does not imply that a savepoint was created by the Git safety commit. | R4, AC-4 |

## Components and Interactions

| Component responsibility | Reuse / extend / new | Inputs or state | Interaction and response | Governing source |
|---|---|---|---|---|
| Safety comparison container | Extend `VersionControlCompare`; no new top-level view or action. | Completed comparison safety result: device/PLC identity, aggregate baseline/current signatures, and optional per-block records. | Renders one amber warning container alongside normal compare content. It is descriptive, not a separate commit workflow. | R1-R3 |
| Detailed F-block safety row | Extend the existing native-checkbox comparison-row pattern in `VersionControlCompare`. | Stable row key/path, F-block identity, baseline signature, current signature, and state. | A native checkbox selects exactly its path. Label-click and Space toggle its checkbox; selection reports through the current `onSelectionChanged(comparisonId, paths)` contract. | R1, R3 |
| Existing commit message and Commit control | Reuse `VersionControlChanges`; no safety-specific button. | Existing message, current local selection, and selected safety/TIA paths. | A non-empty message plus selected safety path enables the existing Commit action. Selecting safety rows neither checks nor requires `Untrackable change`; `Untrackable change` remains a separate message-only commit choice. | R3 |
| Safety commit confirmation | Extend the existing `VersionControlChanges` success-notification path; no new persistent UI. | Whether the successful commit contained one or more selected detailed safety rows. | After that successful commit only, display `Safety change committed to Git. Create an SVN savepoint separately to capture the TIA project.` It replaces archive-oriented follow-up wording for this workflow and does not create or offer a savepoint action. | R3, R4; explicit exclusion |
| Aggregate-only safety detail | Extend `VersionControlCompare`; no synthetic or guessed rows. | PLC/device identity plus aggregate old/current safety signatures; absence of one side's per-block map. | Shows signatures and `Block-level detail unavailable` copy. No checkbox, selectable path, or commit eligibility is generated for unavailable details. | R2 |
| Git history safety badge | Extend `VersionControlHistory` commit item. | Git history item's safety-change classification. | Renders the same compact amber `TriangleAlert` badge labelled `Safety change` even when the commit also has ordinary source changes. | R4 |
| Savepoint/archive safety badge | Preserve and clarify the existing `VersionControlHistory` savepoint item presentation. | Manual savepoint/archive safety-change classification. | Renders a separately-derived `Safety change` badge on the native record. It does not merge with, replace, or trigger Git safety evidence. | R4; explicit exclusion |

### State / Display Detail

| Component | State or condition | Display | Recovery / transition | Source |
|---|---|---|---|---|
| Detailed F-block row | `changed` | Amber safety row with F-block path, `Baseline` signature, `Current` signature, and `Changed` state label. | User may select or clear the row independently. | R1 |
| Detailed F-block row | `added` | Same row structure with path, baseline value shown as unavailable/none when not supplied, current signature, and `Added` state label. | User may select or clear the row independently. | R1 |
| Detailed F-block row | `removed` | Same row structure with path, baseline signature, current value shown as unavailable/none when not supplied, and `Removed` state label. | User may select or clear the row independently. | R1 |
| Detailed F-block row | `invalidated` or current signature `00000000` | Same row structure with path and both signature fields; visibly label the row `Invalidated` and render `00000000` as the current signature. | User may select or clear the row independently; no automatic savepoint/archive follows. | R1; explicit exclusion |
| Safety commit notification | Successful Commit included one or more selected detailed safety rows | Display `Safety change committed to Git. Create an SVN savepoint separately to capture the TIA project.` using the existing transient success-notification treatment. | It clears with the normal transient notification lifecycle. It does not create an SVN savepoint, open a savepoint UI, or add a button. Ordinary-only and untrackable-only commits retain their current confirmation behavior. | R3, R4; explicit exclusion |
| Safety comparison | Per-block maps absent from the legacy baseline or current live result | Amber aggregate presentation with old/current aggregate signatures and a message that block-level detail is unavailable because a per-block map is unavailable. | A future comparison with both maps may replace this display with detailed rows. | R2 |
| Safety comparison | No safety difference | No safety banner or safety rows. Existing normal-source and clean-state displays remain unchanged. | A subsequent comparison may show detailed or aggregate safety state. | Explicit exclusion |
| History item | Safety classification absent | No `Safety change` badge. Existing ordinary source, untrackable, and validation presentations remain unchanged. | Refresh supplies the current item classification. | R4; preserved behavior |

## Visual Constraints

| Element / view | Constraint | Repository or approved design source | Acceptance observation |
|---|---|---|---|
| Safety banner and rows | Use the compact dock utility-class language already used by comparison alerts: rounded border, amber/yellow border and low-opacity background, compact text, and `TriangleAlert` where an icon is shown. | `VersionControlCompare.tsx`; `VersionControlHistory.tsx` | Safety content is visually distinguishable from ordinary rows without changing panel layout or adding a page. |
| Detailed F-block row | Preserve the normal comparison-row layout: checkbox at start, primary identity/state text, monospace path and signature values, and a visible row boundary. Do not rely on amber color alone to convey safety or state. | Existing comparison-row pattern; R1 | Each F-block can be identified by text and selected without ambiguity in the compact Changes pane. |
| Signature display | Render baseline and current signature values in monospace and label their source. Keep `00000000` visible as a value, paired with the `Invalidated` state label. | R1 | A reviewer can distinguish the old signature, new signature, and an invalidated current signature from the row alone. |
| History badges | Reuse the compact amber badge styling and `TriangleAlert`; `Safety change` remains readable beside ordinary file/change information. | `VersionControlHistory.tsx`; R4 | A safety badge appears independently on an eligible Git commit and on an eligible native savepoint/archive item. |
| Mounted views | Keep Changes and History mounted when the other tab is active, following `VersionControlPanel`. | `VersionControlPanel.tsx` | Switching tabs does not discard comparison selection, message text, or loaded history state. |

## Accessibility Requirements

| Component / interaction | Keyboard, semantic, announcement, or contrast behavior | Source | Acceptance observation |
|---|---|---|---|
| Detailed F-block row selection | Use a native checkbox inside an associated label. The checkbox exposes its checked state to assistive technology and is operable by Tab and Space. | Confirmed accessible-native-controls requirement; existing comparison pattern | Keyboard focus reaches every detailed safety row, and toggling one changes only its own checked state. |
| Safety state and signatures | Expose safety meaning, state label, path, baseline signature, and current signature as text. Amber styling supplements this text. | Confirmed accessible-native-controls requirement; R1 | A screen-reader user can identify the F-block and state without color or icon interpretation. |
| Aggregate fallback | Present the unavailable-detail message and aggregate signatures as text in the safety banner; do not expose unavailable details as disabled selectable controls. | R2 | A screen-reader user receives the reason rows cannot be selected. |
| History badge | Provide the text `Safety change` with the alert icon; the badge title may add context but is not the sole label. | Existing `TriangleAlert` badge pattern; R4 | The safety classification is available in the history item's accessible text. |

## Acceptance Traceability

| AC / requirement | View, component, or interaction | Observable UI proof |
|---|---|---|
| AC-1 / R1 | Changes detailed safety comparison | A TIA comparison with multiple F-block changes shows one amber safety row per F-block, each with path, baseline/current signatures, a Changed/Added/Removed/Invalidated label, and an independent checkbox. An invalidated row visibly shows `00000000`. |
| AC-2 / R2 | Changes aggregate fallback | A legacy baseline or live result without a per-block map shows aggregate old/current signatures and unavailable-detail text, with no generated safety row checkbox. |
| AC-3 / R3 | Existing Changes commit flow | Selecting one safety row and entering a message enables the existing Commit action; the selected path reaches the established TIA selection set. `Untrackable change` is neither selected nor required by that interaction. |
| AC-4 / R4 | History Git commit and savepoint/archive items | A classified Git commit displays `Safety change` with its ordinary source information. A separately classified manual SVN savepoint/archive item also displays `Safety change`; neither display creates a savepoint. |
| AC-5 / exclusions | Safety commit confirmation and panel structure | After a successful commit containing selected safety rows, the user sees exactly `Safety change committed to Git. Create an SVN savepoint separately to capture the TIA project.` No safety-specific action button appears, no interaction automatically creates or opens a savepoint/archive, and switching mounted tabs preserves current UI state. |

## Update History

| Date | Version | Changes |
|---|---|---|
| 2026-09-05 | 1.1 | Added the exact post-safety-commit notification and its separate-savepoint boundary. |
| 2026-09-05 | 1.0 | Initial specification for detailed F-block safety comparison and history evidence. |
