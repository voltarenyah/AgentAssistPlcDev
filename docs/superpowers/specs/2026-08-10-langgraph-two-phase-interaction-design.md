# LangGraph Two-Phase App Assistant Interaction Design

## Goal

Change the Workbench App Assistant from keyword-routed responses into a two-phase, LLM-guided workflow: opening the assistant produces a context-grounded orientation proposal, while later user messages let the LLM decide whether to answer, ask a question, read state, or propose an approved mutation.

## Scope

This design changes only the new Workbench App Assistant. The existing PLC-focused `AgentLoop` remains available and continues to own PLC knowledge-db queries, PLC diagnosis, and PLC source workflows.

The first assistant response is proposal-only. It cannot call a tool, execute a mutation, change UI selection, or claim that an action was completed. Execution begins only after a later user message confirms or requests an action.

## User-visible lifecycle

```text
User opens assistant
        |
        v
ApiHost reads current workbench context
        |
        v
LLM orientation response
  - explain the observed project/worktree state
  - infer the most likely user intention
  - propose one useful next move
  - ask whether the user wants to proceed
  - execute nothing
        |
        v
User confirms, changes direction, or asks a question
        |
        v
ApiHost refreshes context again
        |
        v
LLM decision response
  +-- direct answer --------------------> response
  +-- clarification --------------------> ask user
  +-- read-only tool -------------------> C# gateway -> MCP -> LLM summary
  +-- mutation proposal ----------------> approval interrupt
                                             |
                                             +-- reject -> response
                                             +-- approve -> C# gateway -> response
```

## Graph architecture

The graph has explicit request modes instead of inferring the first turn from message text:

```text
START
  |
  v
bootstrap_context
  |
  +-- requestMode=orientation --> orient_with_llm --> END
  |
  +-- requestMode=command ------> decide_with_llm
                                      |
                                      +-- answer/clarification --> END
                                      +-- read_tool --> execute_read --> summarize_with_llm --> END
                                      +-- mutation --> interrupt --> execute_mutation --> summarize --> END
```

`bootstrap_context` calls the C# App Assistant gateway and stores the refreshed `WorkbenchRuntimeSnapshot` in LangGraph state. It runs for both modes, so a consequential command never relies only on the context captured when the panel was opened.

The current keyword `_classify` node is removed from the decision path. The LLM produces a validated structured decision. Tool execution remains a separate graph node and can only execute allowlisted actions through the C# gateway.

## LangGraph state

The checkpoint remains scoped to one workbench:

```text
thread_id = app-assistant:{workbenchId}
```

The state adds explicit lifecycle fields:

```text
requestMode: orientation | command
orientationComplete: boolean
runtimeSnapshot
contextRevision
messages
orientationProposal
decision
toolRequest
toolResult
pendingApproval
answer
```

The runtime snapshot remains application-owned. LangGraph stores a projection for reasoning and conversation continuity, but C# remains authoritative for identity, permissions, revisions, and execution.

## LLM contracts

### Orientation output

The orientation model call must produce:

```json
{
  "likelyIntent": "string",
  "observations": ["string"],
  "proposedNextStep": "string",
  "confirmationQuestion": "string"
}
```

The UI displays this as the first assistant message. The response must be based only on the bootstrap context and must not contain an executable tool call or mutation request.

### Command decision output

The command model call must produce one of:

```json
{
  "kind": "answer | clarification | read_tool | mutation_proposal",
  "answer": "string",
  "question": "string",
  "toolName": "read_worktree_todos | read_commit_history | read_svn_state",
  "toolReason": "string",
  "mutation": { "kind": "create_worktree", "name": "string", "branch": "string", "startPoint": "string" }
}
```

Fields not relevant to the selected `kind` are omitted. The graph rejects malformed or unsupported decisions and falls back to a clarification response; it never guesses a tool name or executes an unvalidated model output.

## System prompts

The orientation prompt is versioned separately from the command prompt and explains the typical Workbench workflow:

1. A workbench is the selected project scope.
2. A worktree is a branch/work area within that project.
3. The user selects worktrees and devices in the UI; the assistant does not silently change selection.
4. Todos, Git history, and SVN state are read-only observations used to guide the next move.
5. PLC-program questions are handed to the existing PLC Assistant.
6. Read-only tools may be used after the user gives a command.
7. Mutations require a clear proposal, explicit approval, expected runtime revision, and C# gateway validation.
8. The assistant must distinguish observed facts from recommendations and must not invent missing state.

The command prompt includes the refreshed runtime snapshot, the user message, the conversation history, and the allowlisted action capabilities. It instructs the model to choose exactly one decision kind and to ask a clarification question when the user intention or target worktree is ambiguous.

## Safety and failure handling

- Orientation has no tool-capable graph edge.
- Read-only tools are selected from a fixed server-side allowlist.
- Mutation proposals pause at a LangGraph interrupt and require explicit UI approval.
- C# validates workbench scope, worktree identity, expected runtime revision, permissions, and idempotency.
- A stale revision causes a context refresh and a new proposal; it never executes against stale state.
- LLM timeout, invalid structured output, or unavailable model produces a deterministic context-based response or clarification.
- The existing PLC AgentLoop remains independent if the LangGraph service is unavailable.

## API and UI changes

The LangGraph service distinguishes the endpoints semantically:

- `POST /bootstrap`: sends `requestMode=orientation` and no user command.
- `POST /chat`: sends `requestMode=command` and the user message.

The assistant panel renders the orientation proposal as a normal assistant message and waits for the user. It does not automatically submit a confirmation or invoke a tool. Subsequent tool progress, clarification, approval, and final responses remain visible in the existing panel.

## Testing and acceptance criteria

The implementation is complete when:

- Opening the panel makes one context read and one orientation LLM call.
- The orientation path makes zero detail-tool or mutation calls.
- The orientation response contains a likely intention, one proposed next step, and a confirmation question.
- A later command is decided by the LLM rather than keyword routing.
- A clarification decision makes no tool call.
- A read-tool decision calls only the selected allowlisted gateway method and then produces a grounded summary.
- A mutation decision pauses for approval and rejects stale revisions before execution.
- Invalid/failed model output cannot trigger a tool call.
- Context is refreshed before every command and remains scoped when switching projects with the chat window open.
- Existing PLC AgentLoop tests and current App Assistant factual grounding tests continue to pass.

## Non-goals

- Replacing the existing PLC AgentLoop.
- Allowing autonomous mutations without approval.
- Adding a general multi-agent planner.
- Moving MCP execution or authoritative state into Python.
- Training or fine-tuning model weights; workflow behavior is controlled through prompts, structured decisions, graph nodes, and evaluation tests.
