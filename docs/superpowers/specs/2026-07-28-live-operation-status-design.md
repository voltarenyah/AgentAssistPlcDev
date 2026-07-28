# Live Operation Status Design

Date: 2026-07-28

## Purpose

Long-running backend work currently leaves the Studio UI showing only a generic busy state. A PLC source export can take five to ten minutes, so the user cannot tell whether the backend is still running or which item it is processing.

The UI will show one live status line that answers:

1. Is the operation still running?
2. What is the backend doing now?

## Goals

- Replace the workbench-creation dialog text `Existing legacy exports are not migrated.` with the current backend stage.
- Show the current stage for long-running workbench, refresh/export, knowledge, import, and merge operations.
- During PLC export, show the name of the block, tag table, or UDT currently being exported.
- Keep the status visible in the active dialog and in a compact global Studio strip if the dialog closes.
- Leave the failed status visible so the user can see where the operation stopped.

## Non-goals

- No scrolling activity log.
- No persisted operation history.
- No progress bar, percentage, completed count, or total count.
- No cancellation workflow.
- No background job queue or concurrent-operation management.
- No parsing of unstructured MCP stderr logs.

## Architecture

The frontend creates an opaque `operationId` for each long-running action and sends it with the existing API request. The original request remains synchronous. While it is pending, the frontend polls a lightweight operation-status endpoint approximately once per second.

The API owns an in-memory `OperationStatusRegistry`. It stores only the latest snapshot for each operation:

```text
operationId
operationType
state: running | succeeded | failed
message
updatedAt
errorMessage
```

Reporting a new stage atomically replaces the previous message. The registry does not retain an event list.

The coordinator reports application-level stages. Engineering tools report item-level export status through the Model Context Protocol SDK's native progress-notification mechanism. The MCP client forwards the latest notification into the same API registry.

## Status producers

### Workbench creation

Expected messages include:

- `Preparing workbench storage…`
- `Initializing Git repository…`
- `Attaching to TIA Portal…`
- `Discovering PLC devices…`
- `Creating device folders…`

Workbench creation does not export PLC source files.

### PLC refresh and export

Expected messages include:

- `Preparing export staging area…`
- `Reading program blocks…`
- `Exporting block Main_OB1…`
- `Exporting tag table MachineTags…`
- `Exporting UDT MotorData…`
- `Writing export metadata…`
- `Comparing exported source…`
- `Preparing refresh preview…`

The engineering adapter reports immediately before it exports each block, tag table, or UDT. If an individual export call stalls, the last reported item name remains visible.

### Other long operations

The coordinator reports meaningful coarse stages for:

- Applying an approved refresh
- Updating or rebuilding device knowledge
- Preparing an editable source overlay
- Importing and compiling modified source
- Creating and merging worktrees

These operations use the same status contract and UI component.

## API behavior

Long-running requests accept an operation identifier through the `X-Operation-Id` request header. The API registers the operation before starting work and updates it through an injected progress reporter.

The status endpoint returns the latest snapshot for one operation. A dismiss endpoint removes a terminal snapshot after the UI has displayed or dismissed it.

Successful operations transition to `succeeded`, allowing the UI to show a short completion result. The UI dismisses that snapshot after displaying it for a few seconds. Failed operations transition to `failed` and remain available until the user dismisses them or the API process exits. Terminal snapshots also expire after 60 minutes to bound server memory if a browser disconnects.

Unknown operation identifiers return not found and never expose another operation's status.

## Studio behavior

A reusable one-line component renders:

- An animated spinner while `state` is `running`
- The latest status message
- A success icon and short result when `state` is `succeeded`
- A failure icon and error text when `state` is `failed`

The workbench-creation dialog displays this component in place of the legacy migration notice. Other operation dialogs and panels use the same component.

While an operation is running, Studio also shows a compact global status strip. If the contextual dialog closes, polling continues and the global strip remains visible. Successful status disappears after its short completion result. Failed status remains until dismissed.

There is no activity-history expansion, numeric progress, or progress bar.

## Polling and lifecycle

- Poll interval: approximately one second.
- Only the latest backend message matters; fast items may pass between polls.
- Polling stops after success, failure, component disposal, or an API-not-found response.
- Closing a dialog within the Studio single-page application does not lose the operation identifier or global status.
- Recovery after a full browser refresh is out of scope.
- API restart clears all operation status because persistence is out of scope.

## Error handling

- The API records `failed` before returning an operation error.
- The failed message identifies the last stage or item and includes the final error.
- The frontend keeps the failed line visible even after the request promise rejects.
- Progress-reporting failure must not fail the underlying engineering operation.
- Status data must not include PLC source content, credentials, or unrestricted filesystem details.

## Testing

Automated tests will verify:

- Registering and atomically replacing the latest operation status
- Isolation between operation identifiers
- Success cleanup and failure retention
- Coordinator stage ordering for workbench creation
- MCP progress propagation through `McpServerConnection`
- Per-item progress before block, tag-table, and UDT export
- API status and dismiss behavior
- API client request/header and status-response type checks through the TypeScript production build
- Manual browser verification of polling termination and running, succeeded, and failed rendering

Build and live verification will confirm:

- Studio lint and production build pass
- Existing Agent, API, engineering, version-control, and E2E tests remain green
- Workbench creation shows Git, TIA attachment, and device-discovery stages
- A device refresh shows the current exported item without a log or progress bar
