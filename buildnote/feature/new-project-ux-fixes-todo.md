# New-project UX fixes — TODO note (2026-07-30)

Points to fix in a later modification round. Not yet implemented.

## 1. Refresh TIA session button on "create new project"

When creating a new workbench project, the session list can go stale: the user
may click "create new project" first and only then open a TIA instance. Add a
**Refresh TIA session** button that re-queries the latest session info on
demand, so the user does not have to back out and re-enter the dialog.

## 2. Create project from an .apxx file on disk

Besides creating a new workbench project from an already-connected TIA
instance, allow the user to **browse the disk for a TIA project file
(.apxx)**. Selecting one should automatically launch a new TIA instance, open
that project in it, and then proceed with the create-new-project flow.

## 3. "Generate PLC context" bootstrap for brand-new projects

After creating a new project, the device overview shows 0 PLC blocks and
missing knowledge, leaving the user with no clear next step ("Compare with
TIA" is not the right action here). For a brand-new project (no metadata):

- Add a **Generate PLC context** button that, without asking for user
  confirmation:
  1. Exports all source files (full initial export).
  2. Auto-commits the initial baseline.
  3. Starts generating `plc-knowledge.db` automatically.
- On success, switch to the chat page and guide the user to start chatting.

This button should only appear for brand-new projects (no metadata / empty
baseline); existing projects keep the normal compare/sync flow.

## 4. Dark-mode scrollbar in AI chat session window

With dark mode enabled, the AI chat session window's scrollbar still renders
with the light theme, which looks broken. The scrollbar should follow the dark
theme as well.

## 5. API key management entry via title-bar status

The DeepSeek API key currently cannot be changed or set from the UI. Turn the
**API online status indicator in the top-right title bar into the API
management entrance**:

- Fresh install with no key set: display **"No valid API key"** instead of an
  online status.
- Clicking the indicator opens a dialog asking for the API key, with a
  **Save** button to confirm.

## 6. Model / thinking controls below the chat input box

For DeepSeek V4, let the user adjust generation options **below the AI chat
input text box**:

- Model variant selection: **Pro / Flash**.
- **Think mode on/off** toggle.
- **Temperature** parameter.
- **Think effort** selection (when think mode is on).

## 7. Render Markdown in the chat window

DeepSeek replies are Markdown, but the chat window is a plain-text viewer —
bold, lists, etc. show as raw syntax. Upgrade the message viewer to **render
Markdown**. Survey mature open-source Markdown rendering options on the market
and introduce one that fits the stack.

## 8. Re-attach to a running TIA instance after app restart

Repro: launch the app, open a TIA instance under a project, close the app,
restart it. The TIA instance is still running, but the app cannot re-attach to
it. Clicking **Open project in TIA** then fails with "project cannot be opened
because another user has it open" — the user must manually close the TIA
session first. Frustrating.

Fix: when a running TIA instance matches the selected project, show a
**Re-attach TIA instance** button next to **Open project in TIA** that
reconnects the app to the existing instance instead of trying to open a new
one.

## 9. Validate TIA project path against sandbox whitelist at creation time

Repro: open a TIA project whose root path is outside the sandbox whitelist.
Project creation succeeds, but **Open project in TIA** later fails with a
sandbox error.

Correct behavior: during **create new project**, check whether the TIA project
file path is allowed by the sandbox list. If not:

- Block creation (or at least warn clearly) at that point.
- Ask the user to move the TIA project file under an allowed path.
- Show the current whitelist to the user so they know which paths are allowed.

## 10. Duplicate export-status notification bar

During block export, the runtime exporting status bar is displayed **twice**:
once in the app title bar and once in the center window's upper-right corner.
Keep the **title bar** one and remove the center-window duplicate.

## 11. Human-readable tool call display in agent chat

The agent chat text viewer shows tool calls as plain text labeled "Progress",
which tells the user nothing. Improvements:

- Replace the generic **"Progress"** label with the actual **MCP server and
  tool name** being called (e.g. `engineering.export_blocks`).
- Render the tool call in a user-friendly format instead of raw plain text:
  show the **arguments** for the call and its **output/result** in a
  human-readable way.

## 12. Export session chat history as Markdown

The session management right dock currently only has **Rename** and **Delete**
buttons. Add an **Export as MD** button that exports the session's chat
history in Markdown format to a `sessionexport` folder under the project root
path.

## 13. Delete workbench project via context menu

Workbench projects can be created but never removed. Add a **Delete this
project** option to the workbench project's **right-click context menu**.

## 14. "Check all" button in TIA comparison window

The TIA comparison window makes users tick items one by one. Add a **Check
all** button so users can select every item in one click.
