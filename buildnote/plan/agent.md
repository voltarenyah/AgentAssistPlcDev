# Agent — DeepSeek chat + MCP tool calling (Phase 2, step 6, chat-first slice)

Scope approved 2026-07-19: chat panel in the WPF App with **first-run API key setup in the UI**,
DeepSeek function calling over the two existing MCP servers ("whatever we have tested"), and
answers grounded in the SQLite knowledge base. This is the chat/Q&A slice of step 6; the
comment-generation workflow and `llm_runs` audit table stay open (see Non-goals).

## 1. Locked decisions

1. **`src/Agent` net8 library** — per `app.md` §2.3 the MCP host + workflows moved out of the App
   when the Agent landed: `Agent.Mcp` (McpHost, McpServerConnection, IMcpToolCaller,
   ToolCallException), `Agent.Workflows` (ReadProjectContextWorkflow/Result), `Agent.AssistantPaths`
   (export-root + knowledge-db path conventions, moved from AppSettings). App references Agent and
   stays pure UI; `tests/Agent.Tests` holds the workflow tests + the new chat tests.
2. **Thin hand-rolled DeepSeek client** (`Agent.Chat.DeepSeekClient`): HttpClient +
   System.Text.Json only, no OpenAI SDK. POST `{baseUrl}/chat/completions`, Bearer auth,
   **streaming (SSE) since 2026-07-19** (`stream:true` + `stream_options.include_usage`;
   content/reasoning deltas emitted live, tool_calls accumulated by index, usage on the final
   chunk). 401/403 → `DeepSeekAuthException` (UI offers key setup again).
   **Models (per /models + docs): `deepseek-v4-flash` / `deepseek-v4-pro`** — `deepseek-chat` /
   `deepseek-reasoner` retired 2026-07-24, mapping to v4-flash non-thinking/thinking.
   **Thinking mode** (`thinking:{type:enabled/disabled}`, default enabled) returns
   `reasoning_content`; `reasoning_effort` = high/max. On tool-call turns the assistant
   `reasoning_content` **must be replayed** in later requests (else API 400) — kept in
   `ChatMessage.ReasoningContent`. Temperature/top_p are ignored by the API in thinking mode and
   are therefore only sent when thinking is disabled.
3. **Tools discovered live** via `tools/list` on both servers at startup (`McpToolCatalog`);
   `McpClientTool.Name/Description/JsonSchema` map 1:1 to OpenAI function definitions.
   **`import_block` is excluded** — agent.md rule 6 forbids importing without a `vc_snapshot`,
   and the version-control MCP does not exist yet. Result: 19 tools (14 engineering − 1 + 6 knowledge).
4. **API key in the UI, first run** — PasswordBox → `AppSettings.SaveDeepSeekApiKey` merge-writes
   `%APPDATA%/PlcAiAssistant\config.json` (existing keys preserved). Never logged. Model/base URL
   also configurable there (`deepSeekModel` default `deepseek-chat`, `deepSeekBaseUrl` default
   `https://api.deepseek.com`).
5. **Grounding:** the system prompt (`Agent.Chat.SystemPrompt`) carries a runtime context block
   rebuilt before every turn: connection state, project name, export root, and the **knowledge db
   path** — auto-detected on connect (`AssistantPaths.ResolveKnowledgeDbPath(project)`, file-exists
   check) so DB Q&A works right after attach, and set from the ingest result after Read Project
   Context. Rules: never invent program facts; search → get_block/get_network for "what does X do";
   get_schema → read-only SQL for aggregates; read-only on the TIA side in this build.
6. **`AgentLoop`** — conversation persists across turns (tool messages included); system message
   refreshed per run; up to 12 tool-calling rounds; tool results truncated to 8 000 chars;
   `ToolCallException` is serialized back as tool content so the model can recover; Progress events
   (tool calls, token usage) render as gray chat entries.

## 2. UI

`MainWindow` right side is a **TabControl — Chat first, then Nodes by kind, Warnings** — so the
conversation always gets the full width (polished 2026-07-19 after user feedback on the cramped
420 px third column; window 1200 wide). Chat tab content: two states — key setup (PasswordBox +
Save + "Keep current key" when re-entering) and chat (message list with
user/assistant/tool/error/info entries, multiline input, Enter sends / Shift+Enter newline,
Send/Cancel/Clear, Change key). `ChatViewModel` exposed as `MainViewModel.Chat`; deferred
auto-scroll + Enter handling in code-behind. Catalog build failure degrades to a log line
(buttons keep working, chat disabled).

**Adjustable chat parameters (settings row):** model (v4-flash/v4-pro), thinking checkbox,
reasoning effort (high/max), temperature + top_p (enabled only with thinking off — the API
ignores them in thinking mode). Applied via `AppSettings.SaveDeepSeekChatSettings` (merge-writes
config.json) and pushed into the live `AgentLoop.Settings` — takes effect on the next turn.

**Session export ("Export" button):** `ChatSessionExporter` renders the exact data exchanged with
DeepSeek as Markdown — header (model, endpoint, turn/round counts, token totals), the current
system prompt (rebuilt per turn from live context), the full tool-definitions JSON sent with every
request, then the numbered conversation: user prompts, assistant answers with per-round usage,
tool calls with pretty-printed arguments, tool results as-sent (i.e. already truncated at the
8 000-char cap — the file shows what DeepSeek actually saw). Fidelity comes from exporting the
loop's real `History` + `RoundUsages` (paired 1:1 with assistant messages), not a parallel
description. Files land in `%LOCALAPPDATA%\PlcAiAssistant\chat-exports\chat-<yyyyMMdd-HHmmss>.md`;
the path is posted back into the chat. Payloads containing ``` fences are wrapped in `~~~~`.

## 3. Tests (23 in Agent.Tests)

- `DeepSeekClientTests` (scripted HttpMessageHandler): URL/auth/body shape, tool_calls parsing,
  assistant+tool message serialization, 401 → DeepSeekAuthException, 500 → DeepSeekException,
  empty key rejected, trailing-slash base URL.
- `AgentLoopTests` (scripted HTTP + FakeToolCaller): full tool round trip (asserts the tool result
  rides the next HTTP request and the system prompt carries the runtime context), tool error →
  tool content, 12-round cap, 8 000-char truncation, unknown tool → AGENT_TOOL_ERROR.
- `McpToolCatalogTests`: import_block excluded, name→caller routing, schema pass-through.
- `AppSettingsTests` (+3): key round-trip preserving existing keys, defaults when absent, empty rejected.
- FakeToolCaller clones JsonElement args on record (callers may dispose the backing document).

## 4. Verified live (2026-07-19, buildnote/log/agent-chat-20260719.md)

App start → both servers up → `agent: 19 MCP tools exposed to DeepSeek (import_block excluded)`.
UI-driven (computer-use): bogus key saved via PasswordBox → chat state; "hello" sent → real HTTP
round trip → `401 Authentication Fails` rendered as a friendly error entry and the key setup
reappeared. Full conversation with a valid key is exercised by the user (key never enters the repo).

## 5. Non-goals (rest of step 6 and later)

Streaming responses, `llm_runs` audit table, token-cost display, chat history persistence,
comment-generation workflow (UI-3), source-editor/version-control servers, import_block exposure
(blocked on vc_snapshot).

## 6. Risks

| Risk | Mitigation |
|---|---|
| DeepSeek format drift from OpenAI | thin client + live E2E; parse defensively (JsonDocument, not strict DTOs) |
| Context bloat from tool results | 8 000-char truncation + 12-round cap |
| Key leaks | PasswordBox, never logged, config.json is git-ignored |
| Model attempts destructive action | import_block not exposed; everything else read-only/export/compile |
