# Workbench History Evidence Design

## Goal

Make Git and SVN history first-class, read-only evidence for the LangGraph Workbench App Assistant. Bootstrap should provide the ten newest Git commits and ten newest SVN log entries for the focused worktree. Explicit history questions should retrieve the depth implied by the user, including complete history when the user asks for it, while the model decides whether to summarize or display the records.

## Scope

This change covers the App Assistant path only:

- ApiHost remains the authority for workbench identity, focused worktree resolution, repository access, and unavailable-state reporting.
- The Python LangGraph service receives structured history evidence and chooses read actions based on the user request.
- The existing PLC AgentLoop and generic MCP surfaces remain unchanged except where the existing version-control log operation needs a read-only pagination/full-history capability.

## Architecture and data flow

### Bootstrap

1. LangGraph `bootstrap_context` calls ApiHost for the current workbench context.
2. ApiHost resolves the focused worktree from authoritative runtime/UI state.
3. When a worktree is focused, ApiHost reads:
   - the ten newest Git commits from the worktree repository;
   - the ten newest SVN log entries when the worktree has SVN metadata/configuration.
4. ApiHost attaches both results as structured, read-only context evidence. Runtime state remains authoritative for identity, revisions, and focus.
5. When no worktree is focused, the context includes an explicit no-focus reason and no invented history.
6. A Git or SVN read failure is represented per source; one unavailable source does not discard the other source or the runtime context.

### Explicit commands

The decision contract supports separate read actions:

- `read_commit_history` for Git commit records;
- `read_svn_history` for SVN log records;
- `read_svn_state` for branch/revision/validation facts;
- `read_worktree_todos` for work items.

History actions carry a requested depth. The model chooses recent/default depth for ordinary questions, an explicit count when the user requests one, and complete retrieval when the user asks for all/every/full history. The gateway returns structured records plus retrieval metadata such as requested depth, returned count, and whether the source was unavailable or incomplete.

The graph passes the retrieved records to the model summarization step. The final answer is therefore grounded in the returned history; it does not automatically print every record. If the user explicitly asks to display all history and the result fits the response, the model may render all records. If a repository is too large for one model context, the result must state the retrieval boundary instead of pretending that a partial result is complete.

## Repository access

ApiHost uses the existing version-control gateway and worktree path jail. Git and SVN access must remain read-only. The version-control log boundary should support bounded pages and a complete-history request without silently treating the existing 30/100-entry default as “all.” The App Assistant gateway owns pagination/aggregation and returns normalized records to Python.

## Contracts

- Add focused bootstrap history fields to the ApiHost App Assistant context contract.
- Add an SVN history record/response contract parallel to the existing Git history response.
- Extend the Python context models to preserve Git/SVN evidence and retrieval metadata.
- Extend `AssistantDecision` and the command prompt with the two history actions and a depth field.
- Keep existing aliases and backward-compatible request routes where possible.

## Error handling and safety

- No focused worktree: return a structured `NO_FOCUSED_WORKTREE` evidence state; do not inspect an arbitrary worktree.
- Missing Git repository: return Git history unavailable while preserving SVN/runtime evidence.
- Missing SVN configuration or working copy: return SVN history unavailable while preserving Git/runtime evidence.
- Invalid depth: return a stable validation error or force the model to clarify; never interpret an invalid value as “all.”
- Full-history retrieval must expose whether all records were retrieved. Partial results must be labeled partial.
- No mutation, branch selection, commit, SVN update, or other write is permitted through these reads.

## Testing

### Python

- Bootstrap context preserves focused Git/SVN history evidence.
- No-focus context does not call a detail reader and produces a clear clarification path.
- The model can select Git history, SVN history, and SVN state independently.
- Recent, explicit-count, and all-depth requests reach the gateway with the intended depth.
- Summaries use returned history and fall back safely when a source is unavailable.

### C#

- Context returns focused recent Git/SVN evidence.
- Each unavailable source is isolated and reported with a stable reason.
- History endpoints enforce worktree scope and depth validation.
- Full-history retrieval reports complete versus partial results.
- Existing ApiHost assistant endpoint and version-control tests remain green.

### Integration

Run the LangGraph tests, ApiHost App Assistant tests, and a live read-only bootstrap/history request against a workbench containing Git and SVN metadata when available.

## Non-goals

- Do not preload history for every worktree.
- Do not expose raw repository paths or execute Git/SVN commands from Python.
- Do not change the existing PLC-focused assistant.
- Do not automatically render all history in normal answers.
