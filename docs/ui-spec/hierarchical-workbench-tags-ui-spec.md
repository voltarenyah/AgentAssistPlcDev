# Hierarchical Workbench Tags UI Specification

## Overview

- Outcome: Users assign global hierarchical tags to Projects (Workbenches) and worktrees, then filter the navigator with AND semantics while retaining Project → worktree context.
- Scope: `WorkbenchNavigator`, `ProjectLandingPage`, `WorktreeLandingPage`, and new `studio/src/studio/workbench/tags/` components.
- PRD or requirement carrier: User-provided Hierarchical Tag System brief, recorded in `docs/design/hierarchical-workbench-tags-design.md`.
- Explicit exclusions: move subtree, merge tags, deleting assigned tags, deleting non-leaf tags, saved filters, OR/NOT syntax, permissions, and tag-driven workflow behavior.

## Design Evidence

| Source | Path / identifier | Decision supplied |
|---|---|---|
| Existing explorer | `studio/src/studio/workbench/WorkbenchNavigator.tsx` | Preserve Workbench → worktree hierarchy and context menus. |
| Existing metadata pages | `studio/src/studio/workbench/ProjectLandingPage.tsx`, `WorktreeLandingPage.tsx` | Use local loading/error patterns and inline metadata sections. |
| Existing hierarchy interaction | `studio/src/studio/HardwareConfigurationView.tsx` | Use controlled expansion keyed by stable IDs. |
| Existing search primitive | `studio/src/components/ui/command.tsx` | Use command dialog/list semantics for autocomplete. |

## UI Surface and Flow

| View or state | Entry / trigger | User-visible result | Governing requirement / AC |
|---|---|---|---|
| Project tag editor | Open Project landing page | Displays direct Workbench tags as removable chips and an Add tag control. | AC-006 |
| Worktree tag editor | Open Worktree landing page | Separates removable direct tags from non-removable inherited Project tags. | AC-007 |
| Picker browse | Activate Add tag | Displays hierarchy and matching existing paths; selecting assigns the stable tag ID. | AC-008 |
| Picker create | Enter a valid nonexistent slash path | Displays an explicit create action; assignment occurs after successful creation. | AC-008 |
| Navigator filtering | Select one or more filter tags | Shows direct-matching Workbenches and parents containing matching worktrees; all selected tags are required. | AC-009 |

## Components and Interactions

| Component responsibility | Reuse / extend / new | Inputs or state | Interaction and response | Governing source |
|---|---|---|---|---|
| Tag chips | New `TagChip` | node, removable, remove action | Shows derived full path; direct chips expose remove, inherited chips do not. | AC-006, AC-007 |
| Tag picker | New `TagPicker` | current assignments, taxonomy, assign callback | Filters path suggestions; offers exactly one create action for a valid absent path. | AC-008 |
| Tree browser | New `TagTree` | nodes, expanded IDs, selection action | Expands/collapses by `tagId`; a node at any depth is selectable. | AC-008 |
| Navigator filter | New `TagFilter`, integrated into `WorkbenchNavigator` | selected tag IDs, taxonomy, search result | Adds/removes filters and replaces visible set with server result while preserving hierarchy. | AC-009 |
| Landing integration | Extend existing pages | direct/inherited tag response | Reloads tags after mutation and displays API errors using existing toast/error patterns. | AC-006, AC-007 |

### State / Display Detail

| Component | State or condition | Display | Recovery / transition | Source |
|---|---|---|---|---|
| TagPicker | Taxonomy loading | Disabled Add tag control with loading label | Re-enable when GET `/api/tags` resolves. | Existing landing-page loading pattern |
| TagPicker | Invalid/blank path | No create action | User continues editing. | AC-008 |
| TagPicker | API failure | Existing error toast with returned message | Current tags remain unchanged; user retries. | Existing metadata mutation behavior |
| Worktree tags | Inherited tag | Visually labelled “Inherited from Project”, no remove action | Remove only from the Project editor. | AC-007 |
| Navigator | No tag filters | Current unfiltered navigator behavior | Clearing final chip restores full list. | AC-009 |
| Navigator | Filter result includes unavailable worktree | Parent and worktree remain visible with unavailable state | Selection retains existing API failure behavior. | AC-005, AC-009 |

## Visual Constraints

| Element / view | Constraint | Repository or approved design source | Acceptance observation |
|---|---|---|---|
| Navigator filter | Lives below the Projects header and above the scrollable tree. | `WorkbenchNavigator.tsx` layout | Header actions and filtered hierarchy remain visible without horizontal overflow. |
| Tag chips | Use existing compact muted/accent control vocabulary. | Existing Studio button/chip styles | Direct and inherited tags are distinguishable at a glance. |
| Landing page tags | Belong in the existing metadata/card hierarchy, not a modal-only workflow. | `ProjectLandingPage.tsx`, `WorktreeLandingPage.tsx` | Tags appear with Project/worktree metadata on first load. |

## Accessibility Requirements

| Component / interaction | Keyboard, semantic, announcement, or contrast behavior | Source | Acceptance observation |
|---|---|---|---|
| Add/remove controls | Use labelled buttons; remove label includes the path. | Existing navigator button patterns | Screen-reader accessible name identifies the action and tag. |
| Picker | Uses `CommandDialog`/`CommandInput` semantics and keyboard item selection. | `components/ui/command.tsx` | Keyboard can open, search, select, and dismiss the picker. |
| Tree rows | Expose `aria-expanded` when a row has children. | `HardwareConfigurationView.tsx` pattern | Expanded state is observable. |

## Acceptance Traceability

| AC / requirement | View, component, or interaction | Observable UI proof |
|---|---|---|
| AC-006 | ProjectLandingPage + TagPicker | Assigning/removing a Project tag changes direct chips. |
| AC-007 | WorktreeLandingPage + TagChip | Direct and inherited tags render separately; inherited tag has no remove control. |
| AC-008 | TagPicker + TagTree | Deep path is created/selected and rendered as its derived path. |
| AC-009 | WorkbenchNavigator + TagFilter | `machine/press` and `state/commission` show only matching worktrees and their parents. |

## Update History

| Date | Version | Changes |
|---|---|---|
| 2026-09-08 | 1.0 | Initial specification |
