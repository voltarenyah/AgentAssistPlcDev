# Open Project in TIA Design

## Goal

Add an **Open project in TIA** button beside **Stage full PLC refresh** on the
selected device overview. The button opens the selected workbench project in a
visible TIA Portal instance and switches the application's active engineering
connection to that instance.

## User Interface

The new control is a secondary button immediately after **Stage full PLC
refresh**. It is enabled only when:

- the selected workbench metadata contains a non-empty
  `engineeringProjectPath`; and
- no other operation is running.

When no TIA project path is available, the button remains visible but disabled.
This makes the unavailable action discoverable without allowing an invalid
request.

## Behavior and Data Flow

On click, the Studio:

1. Reads `engineeringProjectPath` from the selected workbench metadata.
2. Starts an operation through the existing operation-status mechanism.
3. Calls the existing connection-switch API with:
   - `projectPath` set to the metadata path;
   - `withUI` set to `true`.
4. Treats the returned connection as the active engineering connection.
5. Refreshes connection-dependent Studio state using the existing connection
   refresh path.
6. Completes or fails the operation through the existing status presentation.

The connection-switch API is the intended integration boundary. No new backend
endpoint or direct Windows file launch is introduced.

## Error Handling

The click handler defensively returns without issuing a request if the project
path is missing, even though the disabled state should normally prevent the
handler from being invoked.

API failures use the existing operation failure presentation. The current
selection remains intact, and the user can dismiss the error or retry.

## Testing

Tests will verify:

- path availability determines whether the action is enabled;
- the request contains the selected metadata path and `withUI: true`;
- a successful request uses the existing connection/state refresh behavior;
- failures are surfaced by the existing operation-status mechanism;
- the existing full-refresh action remains unchanged.

## Non-goals

- Opening a project without switching the active engineering connection.
- Adding a new API endpoint.
- Launching project files through Windows file associations.
- Inferring a TIA project path from export or worktree directories.
