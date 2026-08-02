# Dockable Workbench Shell Design

## Goal

Make the Studio shell behave like the reference workbench: the project tree on the left and the context/session dock on the right are independent, docked panels that can each collapse completely. When a panel is collapsed, the center workspace expands into its space. The only reopen affordance is the corresponding title-bar button; no edge rail remains visible.

## Current context

`MainStudio` currently renders a fixed-width `WorkbenchNavigator` before the main content and conditionally renders one of three fixed-width right-side docks after it. The right dock has a single visibility toggle in the header. The left navigator cannot be collapsed, dock widths are not user-controlled, and there is no persistent bottom status bar/settings control matching the reference shell.

## Design

### Shell layout

Keep the existing top header and main content responsibilities, but make the body a controlled flex shell:

```text
title bar
  left dock toggle · right dock toggle · identity/status · theme
body
  [left dock?] [left resize handle?] [main workspace] [right resize handle?] [right dock?]
status bar
```

The left and right dock columns are rendered only while open. A closed dock contributes zero width and no visible rail. The main workspace remains the flexible column and takes all released space.

### Dock state

`MainStudio` owns the shell state:

- `leftDockOpen: boolean`, default `true`
- `rightDockOpen: boolean`, default `true`
- `leftDockWidth: number`, default `310`
- `rightDockWidth: number`, default `320`

The widths are clamped to usable bounds (left: 240–420px; right: 240–420px). Closing a dock does not reset its width. Reopening restores the last width used before collapse.

Persist the four values in local storage under a versioned key such as `plc-studio.shell-layout.v1`. Invalid or stale stored values fall back to defaults without blocking startup. Persistence is UI-only and must not call the API.

### Title-bar controls

Add two independent icon buttons to the title bar:

- left control: `Show/hide workbench project tree`
- right control: `Show/hide context dock`

Each button has an accessible label and tooltip, reflects open/closed state, and remains available regardless of the selected tab or device. The existing session-dock toggle becomes the right-dock control rather than a separate conditional behavior. The left control is available even when no device is selected.

### Resizing

When open, each dock has a narrow vertical resize handle between the dock and the center workspace. Pointer dragging updates the corresponding width and keeps the center workspace above a safe minimum. The handle is hidden when its dock is closed. Keyboard users can still use the collapse controls; resizing is an enhancement and must not be required to use the shell.

### Dock contents

Do not change the existing domain content or selection flow:

- `WorkbenchNavigator` remains the left project/worktree/device tree.
- Overview keeps `DevicePropertiesDock` on the right.
- Knowledge keeps `KnowledgePropertiesDock` on the right.
- Other tabs keep `SessionDock` on the right.

The shell controls whether the right column is present; the selected tab controls which right-dock component is inside it.

### Bottom status bar

Add a compact full-width status bar below the body. It should contain:

- current operation/readiness indicator;
- active device and branch context when available;
- flexible spacer;
- lightweight usage/session summary;
- refresh/status affordance where already available;
- settings button at the far right.

The settings button opens the existing API-key/settings flow, or routes to the current settings action if that surface is expanded later. It must not be decorative-only.

### Visual language

Use the existing Geist typography, theme variables, Lucide icons, borders, and color tokens. Refine the shell toward the reference: compact title-bar controls, low-contrast panel surfaces, clear active tree rows, restrained separators, and a quiet status strip. Preserve dark-mode behavior and avoid introducing a second visual system.

## Component boundaries

- `MainStudio`: owns dock visibility, widths, persistence, resize handlers, title-bar controls, and status-bar composition.
- `WorkbenchNavigator`: remains responsible for project tree rendering and its own actions; receives shell sizing only through its parent layout.
- `SessionDock`, `DevicePropertiesDock`, `KnowledgePropertiesDock`: remain content-only right docks; their existing `hidden` behavior should be removed or narrowed so the shell controls visibility.
- Optional `DockResizeHandle` helper: owns pointer capture, clamping, and drag cursor behavior if extracting it improves readability; it should have no API/data dependencies.

## Interaction details

1. Startup loads the shell defaults, then applies valid local-storage values.
2. Clicking the left title-bar button toggles the project tree column.
3. Clicking the right title-bar button toggles whichever context dock is active.
4. Closing either dock leaves the other dock and the center workspace untouched.
5. Dragging a visible divider changes only that dock's stored width.
6. Switching tabs while the right dock is closed does not reopen it or lose the selected tab.
7. Selecting a device while the left dock is closed still works through existing content/actions; reopening shows the current selection.

## Error handling and compatibility

- Malformed local-storage JSON, unknown schema versions, and out-of-range widths are ignored safely.
- Dock layout must not affect workbench API calls, loading states, fatal-error rendering, or dialog flows.
- The shell must remain usable at the existing minimum window height and at narrow desktop widths; when space is constrained, dock widths clamp before the center workspace becomes unusable.
- Existing tests that assert `data-api-status`, session-dock behavior, tab rendering, and selection flow must continue to pass.

## Verification

Add or update focused UI tests for:

- both docks open by default;
- left dock collapse/reopen;
- right dock collapse/reopen across Overview, Knowledge, and chat tabs;
- independent dock state (closing one leaves the other open);
- persisted widths/state are restored and malformed storage is ignored;
- status bar and settings control remain visible when both docks are collapsed;
- active selection and tab content remain unchanged by shell toggles.

Run the Studio typecheck/build and the relevant Vitest suite. Manually verify the three visual states: both open, left collapsed/right open, and both collapsed.

## Scope boundaries

Included: dock collapse/expand, title-bar affordances, remembered widths, divider resizing, compact status bar, working settings entry point, responsive/dark-mode polish, and focused regression tests.

Not included: changing the workbench data model, moving domain actions between docks, redesigning the individual device/knowledge/session panels, or introducing a new routing/layout library.
