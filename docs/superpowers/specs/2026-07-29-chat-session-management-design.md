# Chat Session Management Design

## Goal

Let a user browse the selected PLC device's saved chats, rename or remove them,
open several chats as center-area tabs, and immediately resume any chat by
selecting it.

## Scope

The feature adds a right-side session dock and the center chat workspace needed
to exercise it. Saved sessions remain owned by the active device context.
Session data persists across application restarts; open tabs and dock visibility
do not.

## Persistence and Compatibility

`ChatSessionHeader` gains an optional `Title`. New sessions start as `New chat`.
After the first successful user message, a session still named `New chat`
receives a shortened title derived from that message. A manual rename is never
overwritten.

Legacy session files without a title remain readable. Their displayed title is
derived from the first user message, falling back to `New chat`. Session identity
continues to use the immutable session ID, so duplicate titles are valid.

The existing per-device JSON files remain under
`.automation/sessions/{sessionId}.json`. Session lists are ordered by
`UpdatedAt` descending.

## Backend and API

`SessionManager` remains responsible for validation, normalization, persistence,
listing, loading, and deletion. It adds rename behavior and title derivation.
Titles are trimmed and an empty title is rejected.

The HTTP API adds a rename endpoint and includes titles in session list and load
responses. Loading a session makes it active. A failed load must not replace the
currently active session. Switching a visible chat tab loads that session before
the user can send another message.

## User Interface

A collapsible dock appears at the right when a device is selected. A show/hide
button sits in the upper-right corner of the application title bar. The dock is
open by default and remembers visibility only for the current application run.

The dock lists only the selected device's sessions. Each entry shows its title
and useful recency metadata. Clicking an entry immediately loads the full saved
history, opens a center chat tab if necessary, and focuses it. Selecting an
already-open session focuses its existing tab.

Inactive chat panes remain mounted but hidden, preserving their rendered
content and local UI state. Switching devices clears the open-tab set before
loading the new device's sessions.

Rename is inline in the dock, persists immediately, and updates the matching tab
label. Rename failures retain the former title and show an error.

Removal requires confirmation. A successful removal deletes the saved file,
removes the dock entry, and closes its tab. If it was active, the most recently
used remaining tab receives focus. When no tabs remain, the center displays an
empty chat state. Delete failures leave the UI unchanged and show an error.

## Consistency and Error Handling

Message sending is disabled while a session is loading. Missing, corrupt, or
context-mismatched sessions show an error without changing the visible or active
session. If a session was removed outside the app, an attempted open refreshes
the dock after reporting that it no longer exists.

## Verification

Backend tests cover default and derived titles, manual rename, validation,
persistence, legacy compatibility, ordering, and device isolation. API tests
cover list/load/rename/delete and ensure failed loads do not alter the active
session.

Studio component tests cover opening and focusing tabs, mounted hidden panes,
session activation before send, rename propagation, confirmed removal, fallback
focus, device-change reset, and the title-bar dock toggle. The complete .NET and
Studio test suites and both production builds must pass.
