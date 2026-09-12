# Source Object Inspector UI Specification

## Overview

- Outcome: An engineer can inspect any exported PLC source object from the source-object context menu, understand its source-specific content, and assemble exact networks from several blocks in one temporary workspace.
- Scope: `PlcSourcePanel`, a dockable inspector workspace view, source-inspection client types, and the read-only API behind them.
- PRD or requirement carrier: Confirmed user conversation, 2026-09-11.
- Explicit exclusions: Editing/importing XML; saving card position or order; callers/callees and generic add-to-workspace context actions in the first release.

## Design Evidence

| Source | Path / identifier | Decision supplied |
|---|---|---|
| Existing UI | `studio/src/studio/PlcSourcePanel.tsx` | Reuse each source row's Radix context menu and preserve existing TIA/compare/chat actions. |
| Existing UI | `studio/src/studio/PlcSourceCompareDialog.tsx` | Use the established dialog focus, close, and bounded-scroll behavior. |
| XML fixture | `tests/Mcp.Knowledge.Tests/Fixtures/FC_LAD_SimulateCylinder_Call [FC1].xml` | Interface sections and LAD parts/wires are source evidence for the inspector projection. |
| User reference | Attached TIA block-editor screenshot | Use a dense engineering workspace: interface table above and visual LAD networks below. |

## UI Surface and Flow

| View or state | Entry / trigger | User-visible result | Governing requirement / AC |
|---|---|---|---|
| Source row context menu | Right-click any source object | Switches to the dockable `Source inspector` workspace tab; existing actions remain. | AC-001 |
| Program-block inspector | Inspect an OB, FB, or FC | Header, interface table, and independently loadable network cards. | AC-002 |
| Simple-object inspector | Inspect a DB, tag table, or UDT | Object-specific, spreadsheet-like table and hierarchy where present. | AC-003 |
| LAD network card | A LAD network is loaded | A graphical ladder view with selectable contacts, coils, calls, function blocks, branches, and wires. | AC-004 |
| SCL network card | An SCL network is loaded | Read-only formatted SCL source. | AC-005 |
| Usage workspace | Right-click a tag and select read/write-network inspection | Read/write/mention evidence finds networks across blocks; cards show origin and render together. | AC-006 |
| Referenced object | Right-click a selected rendered element and select `Open referenced object` | Inspector navigates to an unambiguous referenced source object; ambiguity is explained. | AC-007 |

## Components and Interactions

| Component responsibility | Reuse / extend / new | Inputs or state | Interaction and response | Governing source |
|---|---|---|---|---|
| Source row menu | Extend `PlcSourcePanel` | `SourceObjectInfo` | Opens inspector without changing selected workbench/device. | AC-001 |
| Source inspector view | New FlexLayout workspace tab | Selected object or network-card descriptor; session-local card collection | User can drag/split/dock it like PLC Source, Knowledge, and AI chat; no inspector content/layout persistence is added. | User decision |
| Object table | New, type-specific renderer | DB/tag/UDT projection | Dense columns use source fields; no edit controls. | AC-003 |
| Interface table | New | Interface sections and members | Shows section, name, data type, default value, accessibility, and comment when exported. | AC-002 |
| Network workspace | New | Ordered session-local cards | Move-up/move-down controls reorder cards only in the active inspector view. | User decision |
| LAD renderer | New | Parts, access labels, pins, and wire topology | Elements are keyboard-focusable/selectable and expose an element context menu. | AC-004, AC-007 |
| SCL renderer | New | SCL tokens/source lines | Presents source in a readable monospace view. | AC-005 |

### State / Display Detail

| Component | State or condition | Display | Recovery / transition | Source |
|---|---|---|---|---|
| Inspector | XML malformed/unsupported | Clear object/path diagnostic; no invented source view. | User may close and refresh/export the source. | AC-008 |
| Usage workspace | Knowledge DB missing/stale | Explain that usage discovery requires current knowledge; direct object inspection remains available. | Link/affordance to existing knowledge refresh is not added in this scope. | AC-006 |
| Usage workspace | Direction is text-only evidence | Show `mention`, distinct from `read` and `write`. | The network may still be inspected. | AC-006 |
| Referenced object | No unique source object | Disable/decline navigation with an ambiguity reason. | Keep current card selected. | AC-007 |

## Visual Constraints

| Element / view | Constraint | Repository or approved design source | Acceptance observation |
|---|---|---|---|
| Inspector | Dark, dense, industrial engineering workspace consistent with Studio tokens; content scrolls inside its dockable workspace tab. | Existing Studio panels and attached TIA reference. | Header and interface table remain visible while networks are reviewed. |
| Network card | Origin chip always shows block, network number, language, and evidence direction when applicable. | User requirement. | A mixed-block workspace is understandable without opening raw XML. |
| LAD view | Power rails, branch/merge paths, and selectable source parts are visible; timer and set/reset connections terminate at their exact XML named pins. Part labels retain the XML-connected operand. | Attached TIA reference; accessibility requirement. | A branched contact/timer/set-reset rung is visible at normal desktop width. |

## Accessibility Requirements

| Component / interaction | Keyboard, semantic, announcement, or contrast behavior | Source | Acceptance observation |
|---|---|---|---|
| Context menu | `Inspect object` is a named menu item. | Existing Radix menu pattern. | Keyboard context-menu navigation reaches it. |
| Source inspector view | Has a labelled heading and a clear empty-selection instruction. | Existing workspace-tab pattern. | Keyboard tab navigation reaches the inspector after source inspection is requested. |
| Selectable LAD elements | Each element has an accessible name from kind and operand; context action is reachable by keyboard. | AC-004, AC-007. | A contact/coil can be selected and its menu opened without a pointer. |

## Acceptance Traceability

| AC / requirement | View, component, or interaction | Observable UI proof |
|---|---|---|
| AC-001 | Source row menu | Right-click a block, DB, tag table, and UDT; each exposes `Inspect object`. |
| AC-002 | Program-block inspector | An FB shows sectioned interface members with type/name fields and independently loadable networks. |
| AC-003 | Simple-object inspector | A DB, tag table, and UDT each show their source-derived table rather than an empty program-block view. |
| AC-004 | LAD renderer | A fixture LAD network displays connected selectable ladder elements and its XML wire-count/topology evidence. |
| AC-005 | SCL renderer | An SCL network displays formatted source. |
| AC-006 | Usage workspace | A selected tag produces mixed-block cards labeled read/write/mention and exact origins. |
| AC-007 | Element menu | A resolvable call/type reference opens its source object inspector. |
| AC-008 | Error state | Malformed XML and missing/stale knowledge evidence have distinct, actionable displays. |

## Update History

| Date | Version | Changes |
|---|---|---|
| 2026-09-11 | 1.0 | Initial specification from confirmed inspector requirements. |
| 2026-09-12 | 1.1 | Specify pin-level SVG routing for branched LAD networks. |
