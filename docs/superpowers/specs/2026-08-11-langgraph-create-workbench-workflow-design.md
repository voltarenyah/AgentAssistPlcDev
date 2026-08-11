# LangGraph Create Workbench Workflow Design

## Goal

Teach the LangGraph Workbench App Assistant how to guide a user through creating
a new workbench project linked to Siemens TIA Portal. The workflow must collect
missing inputs conversationally, use read-only discovery when necessary, require
explicit acknowledgement before mutation, and submit one authoritative
long-running operation to ApiHost.

The existing PLC-focused assistant is unchanged. Its MCP sequencing rules are
reference material only.

## Terminology

- **Workbench project:** The application-managed project containing the managed
  TIA copy, PLC source baselines, knowledge data, and version-control metadata.
- **Origin TIA project:** The user-selected `.ap17` file used to bootstrap the
  workbench.
- **Running TIA instance:** A TIA Portal process returned by `list_sessions`.
- **Link mode:** Either `project_file` or `running_session`.

## User-visible workflow

```text
User asks to create a new project
        |
        v
Ask link mode
  +-- project file ------> ask for .ap17 path
  |
  +-- running instance --> list_sessions({})
                              |
                              v
                         user selects sessionId
        |
        v
Collect workbench name and optional root path
        |
        v
Show complete proposal
        |
        v
User acknowledges
        |
        v
ApiHost create-workbench operation
        |
        v
Progress and final result
```

The first link-mode question should expose exactly these choices:

1. **Choose a TIA project file**
2. **Attach to a running TIA instance**

The assistant asks only for the next missing value. It must not silently choose
a link mode, session, path, workbench name, or custom root.

## Conversation and state rules

The workflow state should preserve typed values independently from the raw chat
text:

```text
intent = create_workbench
linkMode = project_file | running_session
name = string?
rootPath = string?
engineeringProjectPath = string?
engineeringSessionId = integer?
selectedSession = session summary?
proposalPresented = boolean
acknowledged = boolean
operationId = string?
```

The following invariants apply:

- `engineeringProjectPath` and `engineeringSessionId` are mutually exclusive.
- A project-file workflow requires a user-provided `.ap17` path.
- A running-session workflow requires a `sessionId` returned by
  `list_sessions`; the model must not invent one.
- `name` is required before the proposal can be submitted.
- `rootPath` is optional. `null` delegates default-root selection to ApiHost.
- A natural-language confirmation counts as acknowledgement only when it
  clearly approves the exact pending proposal. A new or ambiguous request starts
  a new clarification turn.
- Changing any proposal field invalidates the previous acknowledgement.

## Discovery tool

When the user selects the running-instance mode, call the read-only engineering
MCP tool:

```json
{
  "tool": "list_sessions",
  "arguments": {}
}
```

The returned session list is the only source for selectable session IDs. Present
each session with its returned `sessionId`, project name, and project path when
available. The selected option value is the integer `sessionId`.

If the list is empty, explain that no running TIA instance is available and offer
the two valid next steps: open a project in TIA Portal, or switch to project-file
mode. Do not call `connect` merely to discover sessions.

For project-file mode, the path comes only from the user's answer. Do not infer a
path from the current workbench, export folders, recent files, or a session list.
ApiHost performs canonicalization, sandbox validation, and existence checks.

## Proposal and acknowledgement

Once all required values are available, show a proposal containing:

- workbench name;
- custom root path, or the statement that the default root will be used;
- selected project path, or selected TIA session details;
- the fact that managed-copy creation, PLC discovery, compilation, source
  export, and initial Git/SVN setup may take time and will run as one operation.

Then ask for explicit acknowledgement. No mutation tool or creation endpoint may
run before acknowledgement.

The proposal payload sent to ApiHost is:

```json
{
  "name": "<user-provided workbench name>",
  "rootPath": "<user-provided root path or null>",
  "engineeringSessionId": <selected sessionId or null>,
  "engineeringProjectPath": "<user-provided .ap17 path or null>"
}
```

The assistant must not add `targetDirectory`, `workbenchRoot`, repository paths,
managed TIA paths, or generated IDs. Those values belong to ApiHost.

## ApiHost execution boundary

LangGraph submits one high-level `create_workbench` application operation. It
does not independently orchestrate the internal MCP calls. This keeps path
jailing, generated storage paths, cleanup, failure handling, and operation state
under C# ownership.

The current ApiHost workbench creation implementation performs the following
internal sequence, subject to the selected link mode and discovered PLC devices:

1. `vc_init_shared({ workbenchRoot, masterWorktreePath })`
2. `svn_init_shared({ workbenchRoot })`
3. Connect to TIA:
   - project file: `connect({ projectPath, withUI: false })`;
   - running instance: `connect({ sessionId })`.
4. `get_project_info({})` to obtain the project identity and PLC names.
5. `save_project_as({ targetDirectory })`, where `targetDirectory` is generated
   by ApiHost for the managed TIA store.
6. `get_project_info({})` to verify that TIA switched to the managed copy.
7. `compile_plc({ plcName })` for each discovered PLC, followed by
   `get_plc_checksums({ plcName: null })`.
8. Export and reconcile the initial PLC source baseline for each device.
9. `disconnect({})` before native version-control operations.
10. `svn_checkout({ url, path, allowObstructions: true })`.
11. `svn_commit({ path, message })`.
12. `vc_commit_selected({ repoPath, paths, message })` for the initial source
    baseline.

The exact internal sequence remains an ApiHost implementation detail. The
LangGraph prompt may describe the high-level phases but must not manufacture
internal path arguments.

## Background execution and progress

The assistant should describe creation as a background operation and return
progress updates while ApiHost performs the work. The operation must have a
stable request/operation ID so progress can be correlated with the proposal.
The implementation must provide a non-blocking submission or detached worker
under this contract; an HTTP request that merely waits synchronously for the
entire creation sequence does not satisfy the background-execution requirement.

The UI may use the existing operation status resource:

```text
GET /api/operations/{operationId}
```

The assistant reports success only after ApiHost confirms completion. If the
operation fails, it reports the returned error and does not claim that a
workbench was created. If creation leaves recoverable partial state, the
ApiHost error and remediation are shown as returned; LangGraph must not invent
rollback claims.

## Error and correction behavior

- Invalid or missing `.ap17` path: report ApiHost validation failure and ask for
  another path; do not retry with a guessed path.
- Missing session: ask the user to select from the latest `list_sessions` result.
- Session disappears before acknowledgement or execution: refresh the session
  list and require a new selection.
- Both source fields supplied: reject the proposal and rebuild it with exactly
  one source field.
- Missing name: ask for the workbench name before proposing execution.
- User rejects the proposal: cancel without calling the creation operation.
- User changes link mode after discovery: discard the old session/path value and
  restart discovery for the new mode.
- ApiHost reports a busy or stale state: preserve the error, refresh context when
  supported, and ask the user whether to retry; never repeat a mutation blindly.

## Prompt instruction draft

The LangGraph command prompt should include a concise version of these rules:

```text
For create-new-project/create-workbench requests, first collect the TIA link mode:
project file or running TIA instance. In project-file mode, ask for and preserve
the user's .ap17 path. In running-instance mode, call list_sessions({}) and let
the user select one returned sessionId. Never invent paths or session IDs.

Collect a workbench name and optional root path. Before execution, show all
resolved values and ask for explicit acknowledgement. Do not execute any
mutation before acknowledgement. After acknowledgement, submit one ApiHost
create_workbench operation with name, rootPath, engineeringSessionId, and
engineeringProjectPath; exactly one of the two engineering fields must be set.
Do not independently call save_project_as, export, Git, or SVN tools for this
workflow. ApiHost owns those calls and all generated paths. Report progress and
claim completion only from the ApiHost operation result.
```

## Testing and acceptance criteria

- A create request with no link mode produces exactly the two link-mode options.
- Project-file mode asks for a path and never calls `list_sessions`.
- Running-instance mode calls `list_sessions({})` once and presents only returned
  sessions.
- A selected session ID is passed unchanged as `engineeringSessionId`.
- The path supplied by the user is passed unchanged as
  `engineeringProjectPath`.
- The final payload contains exactly one engineering source field.
- No create operation occurs before explicit acknowledgement.
- Rejection or ambiguous acknowledgement causes no mutation call.
- The ApiHost operation receives no assistant-invented storage or target paths.
- Progress and final success/failure are grounded in operation results.
- Existing PLC Assistant behavior and tests remain unchanged.

## Non-goals

- Changing the legacy PLC-focused assistant.
- Adding autonomous project creation without acknowledgement.
- Making LangGraph responsible for TIA lifecycle, storage paths, Git, SVN, or
  rollback orchestration.
- Designing all other PLC workflows in this first slice.
