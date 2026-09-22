# ADR-0005 Remove the LangGraph app-assistant sidecar

## Status

Accepted

## Context

Two assistant surfaces exist, and only one of them is an agent.

The **device-scoped chat** (`ChatWorkspace`) runs `src/Agent/Chat/AgentLoop.cs` over the
four MCP servers. It offers the model the live `tools/list` of every server, runs up to 12
tool-calling rounds with a "continue" grant, streams reasoning and content deltas, feeds
structured `{code, message, retryable, remediation}` errors back to the model, refuses to
repeat a failed call, and gates destructive calls through `AgentSandbox` (deny tiers,
per-call or session grant, per-session budget, JSONL audit). It is **already
workbench-aware**: its runtime-context provider emits `Workbench:`, `Worktree:`, `Device:`,
`PLC source:` and `Knowledge DB:` (`CompatibilityEndpoints.cs:826-831`), and its callers are
bound to the selected device by `BoundMcpCaller`.

The **workbench-scoped App Assistant** (`AppAssistantPanel`) is a Python LangGraph sidecar
on port 8787 behind `AppAssistantGateway` and `AppAssistantClient`.

| | C# `AgentLoop` | Python sidecar |
|---|---|---|
| Tool calling | OpenAI tools, live `tools/list` | none |
| Tools reachable | 97 | 4 reads + 2 mutations |
| Rounds per turn | 12, extendable | 1 read hop |
| On model/tool error | structured error to the model, retry guidance | `except Exception: pass` → canned text |
| Approval | `AgentSandbox`: tiers, grant, budget, audit | LangGraph `interrupt()` |
| Streaming | reasoning + content deltas | buffered response |
| History | session-persisted with compaction | last message only |

The gap is not a tuning problem. The sidecar has no tool binding at all: `graph.py` asks the
model to emit `{"kind":"read_tool","toolName":...}` and dispatches by hand through an
`if/elif` chain (`_read_detail`, `graph.py:324-349`) over exactly four hardcoded read
actions. Its graph is acyclic — seven nodes, linear edges, one conditional, at most one tool
hop — so LangGraph contributes only `interrupt()` and `SqliteSaver`, both of which the C#
side already provides in stronger form (`ToolConfirmation`/`AgentSandbox`, and
`SessionManager` for persistence).

Its failure mode is silent. Every model call is wrapped in a bare `except Exception: pass`
(`graph.py:206`, `:241`, `:398`) that degrades to a deterministic template. The prompt
demands "Return only JSON with no markdown or surrounding explanation", so a prose response
is the most likely failure — and it produces a plausible-looking answer with no error.
`test_invalid_model_tool_decision_falls_back_without_gateway_call` asserts this as correct
behaviour, so the suite can never report it. `_message_text` reads only `messages[-1]`, so
the graph's own state carries no tool-call history.

The sidecar is also architecturally redundant. `AppAssistantGateway` is C# and calls the MCP
tools **in-process** (`vc_log` at `:83`, `svn_log` at `:144`) to build the very payloads
`gateway.py` then fetches back over HTTP. The assistant is a third process, in a second
language, with a second credential path, reaching over a socket what `ApiHost` already holds
in memory.

Removing it deletes roughly 4,400 lines of dedicated code and tests — ~1,280 app plus ~1,170
test lines under `agent-service/`, ~805 under `src/ApiHost/AppAssistant/`, ~375 ApiHost test
lines, and ~800 in Studio — plus wiring in the launcher, `scripts/AppAssistantRuntime.ps1`,
`scripts/Reset-AppAssistantState.ps1`, `BackendProcessHost.cs`, `RuntimePaths.cs` and
`build-release.ps1`, which currently ships **Python 3.13 as an install prerequisite**. It
also removes the malformed-`no_proxy` fragility that only exists because a local Python HTTP
client must reach loopback.

## Decision Point

- **Question**: Should the workbench-scoped App Assistant keep its own Python/LangGraph
  service, or be served by the existing C# `AgentLoop`?
- **Why a decision exists**: The sidecar is a second runtime, a second model-credential
  path, and an installable Python prerequisite, and it is a strictly weaker agent than the
  one already in the host process. But it is a shipped, user-visible, contract-tested
  surface, so removing it changes product behaviour rather than only internals.
- **Scope boundary**: the App Assistant sidecar and the Studio panel that fronts it. This
  ADR does not govern the device-scoped chat, the MCP servers, or the model configuration
  both assistants read from `%APPDATA%/PlcAiAssistant/config.json`.

## Decision

There is one assistant. The C# `AgentLoop` serves both the device-scoped chat view and the
workbench-scoped Workbench Assistant panel. The Python sidecar, LangGraph and
`AppAssistantClient` — the HTTP hop to it — are removed.

### Decision Details

| Item | Content |
|---|---|
| **Decision** | Delete `agent-service/`, `AppAssistantClient`, and the launcher/packaging/desktop wiring that exists only to run the sidecar. Remove `AppAssistantGateway`'s internal endpoints unless another caller needs them. The panel keeps its `/api/app-assistant/*` contract, re-backed **in-process** by `ApiChatService` under `ChatScopes.Workbench`. |
| **Why this** | The sidecar duplicates reach the host already has, and its weakness is structural (no tool binding, silent fallback), not tunable. |
| **What must survive** | The Workbench Assistant panel; the create-worktree/create-workbench approval card; server-side conversation persistence. |
| **How each survives** | The panel keeps its own HTTP contract and its own persisted session, so its UI, state module and tests are not rewritten — the panel is a real surface, and re-pointing it at `/api/chat` would be a large frontend change for no user-visible gain. The approval card is the **existing** `AgentSandbox` card: `pending.Add` → `{kind:"confirmation"}` on `/api/logs` → `data-confirmation` card in `ChatWorkspace.tsx:466` → `POST /api/chat/confirm/{id}` (covered by `MainStudio.chatConfirm.test.tsx`), not the panel's amber duplicate. Persistence is `SessionManager`, which is already per-device and already server-side. The two mutations are already MCP tools (`create_project`, `vc_add_worktree`) already classified destructive, so they inherit the audit trail. |
| **Consequence of that choice** | The approval card cannot arrive as an `interrupt` frame. The panel reads a buffered response (`parseAssistantEvents(await response.text())`), so a frame queued while the turn is suspended on approval is not visible until the turn ends — which is exactly the moment the user needed it. The card is therefore driven from `/api/logs` filtered to the panel's own session id, the mechanism the chat view already uses. |
| **Accepted non-goal** | Chatting with **no device selected**. `DeviceContext` requires a `DeviceId`, and the user decided this is not required. The panel therefore prompts for a worktree/device selection instead of answering; the device-less orientation flow is not carried over. |
| **Known unknowns** | Whether the panel and the chat view should present the *same* session (one conversation, two views) or the panel should own a separate session for the same device. The user chose the separate session. |
| **Reconsider when** | A requirement appears that genuinely needs a Python-side agent runtime — for example an ML or graph library with no .NET equivalent. "The framework is nicer" is not that requirement. |

### Verification rules this produced

- **A capability comparison must be against the architecture, not the framework name.** The
  sidecar was judged "stupid because LangGraph". LangGraph was incidental: the graph is
  acyclic and the weakness lives in the classify-then-dispatch prompt and the hand-written
  `if/elif` dispatch. Removing the framework without redesigning that path would have changed
  nothing, so "delete the dependency" was the wrong first move and the wrong framing.
- **A bare `except Exception: pass` around a model call turns a loud failure into a
  plausible wrong answer.** Worse, the sidecar's suite asserted the fallback text as correct,
  so no test could ever report it. Any agent path must surface a model-parse failure to the
  user rather than substituting a template, and a fallback asserted as intended behaviour is
  a blind spot, not coverage.
- **Before adding a process, check whether it is reaching over a boundary what the host
  already holds.** `AppAssistantGateway` called the MCP tools in-process to build the
  payloads the sidecar then fetched back over HTTP. An HTTP hop between two parts of one
  application is a signal to check whether the split is real.
- **"The user-visible surface" and "the assistant implementation" are separable.** The
  costly part of this removal was never the panel; it was the second runtime, credential
  path, prompt stack and install prerequisite behind it. Deciding which of the two the user
  actually valued should come before deciding to keep the whole stack.
