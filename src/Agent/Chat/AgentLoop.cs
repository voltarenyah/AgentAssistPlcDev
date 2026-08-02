using System.Text.Json;
using System.Text.Json.Nodes;
using Agent.Mcp;

namespace Agent.Chat;

/// <summary>
/// The tool-calling conversation loop (buildnote/plan/agent.md): user text → DeepSeek (streaming)
/// → MCP tool calls → final answer. Conversation history (including tool messages and assistant
/// reasoning_content — required by the API on tool-call turns) persists across turns. The system
/// message is static; the volatile runtime context rides in trailing marked user messages appended
/// only when it changes, so the byte-stable prompt prefix keeps hitting DeepSeek's context cache
/// (plan I11). When the history grows past <see cref="HistoryTokenThreshold"/>, old turns are
/// compacted at turn start (see <see cref="CompactHistoryIfOverThreshold"/>) so the prompt stays
/// inside the context window.
/// </summary>
public sealed class AgentLoop
{
    /// <summary>Default tool-calling round cap per turn (see <see cref="RoundLimit"/>).</summary>
    public const int MaxRounds = 12;

    /// <summary>Hard cap applied to every tool result before it enters the history.</summary>
    public int ToolResultMaxChars { get; set; } = 8000;

    /// <summary>Head kept when an old tool result is compacted (see <see cref="PromptTokenBudget"/>).</summary>
    public int ToolResultCompactChars { get; set; } = 500;

    private const string FinalRoundInstruction =
        "Tool budget exhausted. Answer now with what you have, cite what you found, and state clearly what remains unverified.";

    private readonly DeepSeekClient client;
    private readonly McpToolCatalog catalog;
    private readonly Func<string> contextProvider;
    private readonly AgentSandbox? sandbox;
    private readonly List<ChatMessage> messages = new();
    private readonly List<UsageInfo?> roundUsages = new();
    private readonly HashSet<string> failedToolCalls = new(StringComparer.Ordinal);
    private int successfulToolCalls;
    private int failedToolCallCount;

    /// <summary>Last runtime context appended to the history; null when none was appended this run.</summary>
    private string? lastContext;

    public AgentLoop(DeepSeekClient client, McpToolCatalog catalog, Func<string> contextProvider, ChatRequestSettings? settings = null, AgentSandbox? sandbox = null)
    {
        this.client = client;
        this.catalog = catalog;
        this.contextProvider = contextProvider;
        this.sandbox = sandbox;
        Settings = settings ?? new ChatRequestSettings();
    }

    /// <summary>UI-facing narration: tool calls and token usage, one line per event.</summary>
    public event Action<string>? Progress;

    /// <summary>Streamed response pieces: kind is "reasoning" or "content", value the text delta.</summary>
    public event Action<string, string>? StreamDelta;

    /// <summary>Per-request chat parameters (model, thinking, effort, temperature, top_p). Settable between turns.</summary>
    public ChatRequestSettings Settings { get; set; }

    /// <summary>ID of the persisted session this AgentLoop is currently working on, if any.</summary>
    public string? SessionId { get; set; }

    /// <summary>Project name the current session is bound to.</summary>
    public string? ProjectName { get; set; }

    /// <summary>Tool-calling rounds allowed per turn. Defaults to <see cref="MaxRounds"/>; extendable via <see cref="GrantMoreRounds"/>.</summary>
    public int RoundLimit { get; set; } = MaxRounds;

    /// <summary>Cumulative prompt tokens per turn beyond which old tool results are compacted to their head.</summary>
    public int PromptTokenBudget { get; set; } = 300_000;

    /// <summary>Estimated next-prompt size that triggers a <see cref="Progress"/> warning before the API call.</summary>
    public int PromptTokenWarningThreshold { get; set; } = 100_000;

    /// <summary>Estimated or last-billed prompt size that triggers cross-turn history compaction at turn start.</summary>
    public int HistoryTokenThreshold { get; set; } = 90_000;

    /// <summary>Newest user turns never touched by cross-turn compaction.</summary>
    public int RecentTurnsToKeep { get; set; } = 2;

    /// <summary>Head kept from a collapsed turn's final assistant answer (see <see cref="CollapseOldestTurn"/>).</summary>
    public int CollapsedAnswerChars { get; set; } = 500;

    /// <summary>Head kept from a collapsed turn's user message.</summary>
    public const int CollapsedUserChars = 1000;

    private const string CollapsedMarker = "…[earlier tool rounds omitted]";

    /// <summary>True when the last <see cref="RunAsync"/> hit <see cref="RoundLimit"/> and ended with a forced no-tools answer.</summary>
    public bool LastTurnHitRoundCap { get; private set; }

    /// <summary>Number of history-compaction events during the last <see cref="RunAsync"/> turn.</summary>
    public int LastTurnCompactions { get; private set; }

    /// <summary>The exact conversation sent to DeepSeek (static system prompt first).</summary>
    public IReadOnlyList<ChatMessage> History => messages;

    /// <summary>Token usage per API response, aligned 1:1 with the assistant messages in <see cref="History"/>.</summary>
    public IReadOnlyList<UsageInfo?> RoundUsages => roundUsages;

    /// <summary>Successful and failed MCP calls from the most recent turn.</summary>
    public ToolCallStats LastTurnToolCalls => new(successfulToolCalls, failedToolCallCount);

    /// <summary>Applies tunable loop limits (see <see cref="ChatLoopPolicy"/>).</summary>
    public void Apply(ChatLoopPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        RoundLimit = policy.RoundLimit;
        PromptTokenBudget = policy.PromptTokenBudget;
        PromptTokenWarningThreshold = policy.PromptTokenWarningThreshold;
        ToolResultMaxChars = policy.ToolResultMaxChars;
        ToolResultCompactChars = policy.ToolResultCompactChars;
        HistoryTokenThreshold = policy.HistoryTokenThreshold;
        RecentTurnsToKeep = policy.RecentTurnsToKeep;
        CollapsedAnswerChars = policy.CollapsedAnswerChars;
    }

    /// <summary>Extend this turn's tool-calling budget (the "continue" affordance after a cap).</summary>
    public void GrantMoreRounds(int additional)
    {
        if (additional <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(additional), "Additional rounds must be positive.");
        }

        RoundLimit += additional;
    }

    /// <summary>Heuristic size of the next request (history + tool definitions), before any billing happens.</summary>
    public int EstimateNextPromptTokens() => TokenEstimator.Estimate(messages, catalog.ToOpenAiToolsJson());

    public void ClearHistory()
    {
        messages.Clear();
        roundUsages.Clear();
        lastContext = null;
    }

    /// <summary>
    /// Replace the current in-memory conversation state with loaded session data.
    /// Does NOT persist — the caller must save separately via <see cref="SessionManager"/>.
    /// </summary>
    public void RestoreFrom(List<ChatMessage> history, List<UsageInfo?> usages)
    {
        messages.Clear();
        messages.AddRange(history);

        // Migrate sessions created before runtime context was reduced to device-level roots.
        // Keeping the legacy source-file listing would reintroduce the stale payload on the
        // next request even though the current context provider no longer emits it.
        messages.RemoveAll(message =>
            SystemPrompt.ContextBody(message)?.Contains("Source files (", StringComparison.Ordinal) == true);

        roundUsages.Clear();
        roundUsages.AddRange(usages);

        // Recover the last appended runtime context so an unchanged one is not duplicated.
        lastContext = null;
        for (var i = messages.Count - 1; i >= 0; i--)
        {
            if (SystemPrompt.ContextBody(messages[i]) is { } body)
            {
                lastContext = body;
                break;
            }
        }
    }

    /// <summary>Runs one user turn to completion; returns the assistant's final text.</summary>
    public async Task<string> RunAsync(string userText, CancellationToken cancellationToken = default)
    {
        LastTurnHitRoundCap = false;
        LastTurnCompactions = 0;
        CompactHistoryIfOverThreshold();
        failedToolCalls.Clear();
        successfulToolCalls = 0;
        failedToolCallCount = 0;
        RefreshSystemMessage();
        AppendContextIfChanged();
        messages.Add(ChatMessage.User(userText));
        var cumulativePromptTokens = 0;
        var estimateWarned = false;

        for (var round = 1; ; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CompactOldToolResultsIfOverBudget(cumulativePromptTokens);
            if (!estimateWarned)
            {
                var estimate = TokenEstimator.Estimate(messages, catalog.ToOpenAiToolsJson());
                if (estimate >= PromptTokenWarningThreshold)
                {
                    estimateWarned = true;
                    Progress?.Invoke($"warning: next prompt estimated at ~{estimate} tokens (threshold {PromptTokenWarningThreshold})");
                }
            }

            // ToOpenAiToolsJson per call: a JsonArray can only be parented into one request body.
            var response = await CallModelAsync(catalog.ToOpenAiToolsJson(), round, cancellationToken);
            cumulativePromptTokens += response.Usage?.PromptTokens ?? 0;

            if (response.ToolCalls.Count == 0)
            {
                var answer = response.Content ?? "(empty response from DeepSeek)";
                messages.Add(ChatMessage.Assistant(answer, reasoningContent: response.ReasoningContent));
                return answer;
            }

            await AppendToolRoundAsync(response, cancellationToken);

            if (round >= RoundLimit)
            {
                // Graceful final round (plan I3): pending tool calls are already in history; one last
                // call with no tools offered must produce an answer from the gathered evidence.
                LastTurnHitRoundCap = true;
                Progress?.Invoke($"round cap reached ({RoundLimit}); requesting a final answer without tools");
                messages.Add(ChatMessage.User(FinalRoundInstruction));
                var finalResponse = await CallModelAsync(null, round + 1, cancellationToken);
                var finalAnswer = finalResponse.Content ?? "(empty response from DeepSeek)";
                messages.Add(ChatMessage.Assistant(finalAnswer, reasoningContent: finalResponse.ReasoningContent));
                return finalAnswer;
            }
        }
    }

    private async Task<ChatResponse> CallModelAsync(JsonArray? tools, int round, CancellationToken cancellationToken)
    {
        Progress?.Invoke($"round {round}: calling model");
        var response = await client.CompleteStreamingAsync(
            messages,
            tools,
            Settings,
            delta => StreamDelta?.Invoke(
                delta.ReasoningContent != null ? "reasoning" : "content",
                delta.ReasoningContent ?? delta.Content ?? string.Empty),
            cancellationToken);
        roundUsages.Add(response.Usage);
        if (response.Usage != null)
        {
            var reasoning = response.Usage.ReasoningTokens > 0 ? $" ({response.Usage.ReasoningTokens} reasoning)" : string.Empty;
            var cache = response.Usage.PromptCacheHitTokens + response.Usage.PromptCacheMissTokens > 0
                ? $" (cache: {response.Usage.PromptCacheHitTokens} hit / {response.Usage.PromptCacheMissTokens} miss)"
                : string.Empty;
            Progress?.Invoke(
                $"usage: {response.Usage.PromptTokens} prompt + {response.Usage.CompletionTokens} completion{reasoning}{cache} tokens");
        }

        return response;
    }

    private async Task AppendToolRoundAsync(ChatResponse response, CancellationToken cancellationToken)
    {
        messages.Add(ChatMessage.Assistant(response.Content, response.ToolCalls, response.ReasoningContent));
        foreach (var call in response.ToolCalls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Progress?.Invoke($"→ {call.Name}({Summarize(call.ArgumentsJson)})");
            messages.Add(await ExecuteToolCallAsync(call, cancellationToken));
        }
    }

    /// <summary>
    /// Prompt-token budget guard (plan I7): once the turn's cumulative billed prompt tokens cross
    /// <see cref="PromptTokenBudget"/>, tool results older than the last two tool rounds are shrunk
    /// to their head. Only tool-role messages are touched; assistant↔tool_call pairing is preserved
    /// because messages are never removed or reordered.
    /// </summary>
    private void CompactOldToolResultsIfOverBudget(int cumulativePromptTokens)
    {
        if (cumulativePromptTokens <= PromptTokenBudget)
        {
            return;
        }

        // Cutoff: the assistant message opening the second-from-last tool round; that round and
        // the last one stay intact, everything older is eligible for compaction.
        var cutoff = -1;
        var toolRoundsSeen = 0;
        for (var i = messages.Count - 1; i > 0; i--)
        {
            if (messages[i].Role == "assistant" && messages[i].ToolCalls is { Count: > 0 } && ++toolRoundsSeen == 2)
            {
                cutoff = i;
                break;
            }
        }

        if (cutoff < 0)
        {
            return;
        }

        var compacted = 0;
        for (var i = 1; i < cutoff; i++) // index 0 is the system message; user messages are never compacted
        {
            var message = messages[i];
            if (message.Role != "tool" || message.Content == null || message.Content.Length <= ToolResultCompactChars)
            {
                continue;
            }

            messages[i] = message with { Content = CompactHistoricalToolResult(message.Content, ToolResultCompactChars) };
            compacted++;
        }

        if (compacted > 0)
        {
            LastTurnCompactions++;
            Progress?.Invoke(
                $"prompt budget exceeded ({cumulativePromptTokens} > {PromptTokenBudget} cumulative); compacted {compacted} old tool result(s)");
        }
    }

    /// <summary>
    /// Cross-turn history guard: when the conversation has grown past <see cref="HistoryTokenThreshold"/>
    /// (estimated for the next prompt, or actually billed on the last round), old turns are compacted
    /// at turn start in three stages — truncate old tool results, strip old reasoning_content, then
    /// collapse whole oldest turns to their user message plus a truncated final answer. Only turns
    /// older than the last <see cref="RecentTurnsToKeep"/> user turns are touched; the system message
    /// and assistant↔tool pairing are preserved, and RoundUsages stays aligned 1:1 with assistant
    /// messages. The per-turn <see cref="CompactOldToolResultsIfOverBudget"/> guard is unaffected.
    /// </summary>
    private void CompactHistoryIfOverThreshold()
    {
        var lastBilled = roundUsages.LastOrDefault(usage => usage is not null)?.PromptTokens ?? 0;
        var estimate = EstimateNextPromptTokens();
        if (estimate < HistoryTokenThreshold && lastBilled < HistoryTokenThreshold)
        {
            return;
        }

        Progress?.Invoke(
            $"history over threshold ({estimate} estimated / {lastBilled} last billed >= {HistoryTokenThreshold}); compacting old turns");
        var keepFrom = KeepWindowStart();

        // Stage 1: shrink tool results in old turns to their head (pairing untouched).
        var truncated = 0;
        for (var i = 1; i < keepFrom; i++) // index 0 is the system message
        {
            var message = messages[i];
            if (message.Role == "tool" && message.Content is { } content && content.Length > ToolResultCompactChars)
            {
                messages[i] = message with { Content = CompactHistoricalToolResult(content, ToolResultCompactChars) };
                truncated++;
            }
        }

        if (truncated > 0)
        {
            LastTurnCompactions++;
            Progress?.Invoke(
                $"history compacted: truncated {truncated} old tool result(s) (~{EstimateNextPromptTokens()} estimated tokens)");
        }

        // Stage 1b: drop stale runtime-context messages in old turns — the current one travels
        // with the keep window (see KeepWindowStart), and AppendContextIfChanged re-appends the
        // context at the tail when compaction removed every copy of it.
        var droppedContext = 0;
        for (var i = keepFrom - 1; i > 0; i--)
        {
            if (SystemPrompt.IsContextMessage(messages[i]))
            {
                messages.RemoveAt(i);
                droppedContext++;
            }
        }

        if (droppedContext > 0)
        {
            keepFrom = KeepWindowStart(); // removals shifted the indices below the keep window
            LastTurnCompactions++;
            Progress?.Invoke($"history compacted: dropped {droppedContext} stale runtime context message(s)");
        }

        // Stage 2: drop reasoning_content from old assistant messages — the API only needs it
        // replayed within the current tool-call chain, and it is large in thinking mode.
        var stripped = 0;
        for (var i = 1; i < keepFrom; i++)
        {
            if (messages[i].ReasoningContent != null)
            {
                messages[i] = messages[i] with { ReasoningContent = null };
                stripped++;
            }
        }

        if (stripped > 0)
        {
            LastTurnCompactions++;
            Progress?.Invoke($"history compacted: stripped reasoning from {stripped} old assistant message(s)");
        }

        // Stage 3: collapse whole oldest turns while still over threshold. Each iteration
        // strictly shrinks the history or stops, so the loop always terminates.
        while (EstimateNextPromptTokens() >= HistoryTokenThreshold && CollapseOldestTurn())
        {
        }
    }

    /// <summary>
    /// Index from which messages are protected from cross-turn compaction: the user message opening
    /// the last <see cref="RecentTurnsToKeep"/> turns, extended backwards over the runtime-context
    /// message traveling with that turn; 1 when fewer turns exist (everything protected).
    /// Machine-generated context messages never open a turn (see <see cref="IsTurnBoundary"/>).
    /// </summary>
    private int KeepWindowStart()
    {
        var seen = 0;
        for (var i = messages.Count - 1; i > 0; i--)
        {
            if (!IsTurnBoundary(messages[i]) || ++seen != RecentTurnsToKeep)
            {
                continue;
            }

            while (i > 1 && SystemPrompt.IsContextMessage(messages[i - 1]))
            {
                i--; // a context update immediately precedes its user message — keep them together
            }

            return i;
        }

        return 1;
    }

    /// <summary>True for real user turns; machine-generated context messages never open a turn.</summary>
    private static bool IsTurnBoundary(ChatMessage message) =>
        message.Role == "user" && !SystemPrompt.IsContextMessage(message);

    /// <summary>
    /// Collapses the oldest turn outside the keep window to its (truncated) user message plus its
    /// (truncated) final assistant answer, dropping the tool rounds in between together with their
    /// RoundUsages entries. Returns false when there is nothing left worth collapsing.
    /// </summary>
    private bool CollapseOldestTurn()
    {
        var keepFrom = KeepWindowStart();

        // Oldest eligible turn: [start, end), bounded by the next user message. Turns are only
        // ever cut at user-message boundaries, so assistant↔tool pairing survives intact.
        var start = -1;
        for (var i = 1; i < keepFrom; i++)
        {
            if (IsTurnBoundary(messages[i]))
            {
                start = i;
                break;
            }
        }

        if (start < 0)
        {
            return false;
        }

        var end = keepFrom;
        for (var i = start + 1; i < keepFrom; i++)
        {
            if (IsTurnBoundary(messages[i]))
            {
                end = i;
                break;
            }
        }

        var answerIndex = -1;
        var hadToolRounds = false;
        var assistantsBefore = 0;
        var assistantsInSpan = 0;
        long oldChars = 0;
        for (var i = 1; i < end; i++)
        {
            var message = messages[i];
            if (i > start)
            {
                oldChars += MessageChars(message);
                if (message.Role == "tool" || message.ToolCalls is { Count: > 0 })
                {
                    hadToolRounds = true;
                }
            }

            if (message.Role != "assistant")
            {
                continue;
            }

            if (i < start)
            {
                assistantsBefore++;
            }
            else if (i > start)
            {
                assistantsInSpan++;
                if (message.ToolCalls is not { Count: > 0 })
                {
                    answerIndex = i; // last plain assistant message: the turn's final answer
                }
            }
        }

        oldChars += MessageChars(messages[start]);
        var collapsedUser = Truncate(messages[start].Content ?? string.Empty, CollapsedUserChars);
        var collapsedAnswer = Truncate(
            answerIndex >= 0 ? messages[answerIndex].Content ?? "(empty response from DeepSeek)" : "(no final answer recorded)",
            CollapsedAnswerChars);
        if (hadToolRounds)
        {
            collapsedAnswer += CollapsedMarker;
        }

        // Stop when collapsing would not shrink the history (already-collapsed or minimal turn).
        if (collapsedUser.Length + collapsedAnswer.Length >= oldChars)
        {
            return false;
        }

        // RoundUsages is aligned 1:1 with assistant messages: drop the span's entries and add one
        // (null — the replacement answer is not an API round) to keep the alignment exact.
        if (assistantsInSpan > 0 && assistantsBefore < roundUsages.Count)
        {
            roundUsages.RemoveRange(assistantsBefore, Math.Min(assistantsInSpan, roundUsages.Count - assistantsBefore));
        }

        roundUsages.Insert(Math.Min(assistantsBefore, roundUsages.Count), null);

        var replacement = answerIndex >= 0
            ? messages[answerIndex] with { Content = collapsedAnswer, ReasoningContent = null, ToolCalls = null }
            : ChatMessage.Assistant(collapsedAnswer);
        messages[start] = messages[start] with { Content = collapsedUser };
        messages.RemoveRange(start + 1, end - start - 1);
        messages.Insert(start + 1, replacement);
        LastTurnCompactions++;
        Progress?.Invoke(
            $"history compacted: collapsed turn into user + truncated answer ({end - start} messages -> 2, ~{EstimateNextPromptTokens()} estimated tokens)");
        return true;
    }

    private static long MessageChars(ChatMessage message)
    {
        long chars = message.Content?.Length ?? 0;
        chars += message.ReasoningContent?.Length ?? 0;
        if (message.ToolCalls == null)
        {
            return chars;
        }

        foreach (var call in message.ToolCalls)
        {
            chars += call.Id.Length + call.Name.Length + call.ArgumentsJson.Length;
        }

        return chars;
    }

    private async Task<ChatMessage> ExecuteToolCallAsync(ChatToolCall call, CancellationToken cancellationToken)
    {
        if (string.Equals(call.Name, "query", StringComparison.Ordinal)
            && catalog.Tools.Any(tool => string.Equals(tool.Name, "get_schema", StringComparison.Ordinal))
            && !HasKnowledgeSchemaFact())
        {
            const string remediation =
                "Call get_schema first, inspect its ddl/nodeKinds/edgeTypes/exampleQueries, then retry the query using those exact table and column names.";
            Progress?.Invoke("  ! query blocked until get_schema is read in this chat");
            failedToolCallCount++;
            return ChatMessage.Tool(
                call.Id,
                JsonSerializer.Serialize(new
                {
                    error = new
                    {
                        code = "SCHEMA_REQUIRED_BEFORE_QUERY",
                        message = "The knowledge database schema has not been read in this chat.",
                        retryable = true,
                        remediation,
                    },
                }));
        }

        var fingerprint = ToolCallFingerprint(call);
        if (failedToolCalls.Contains(fingerprint))
        {
            failedToolCallCount++;
            return ChatMessage.Tool(
                call.Id,
                JsonSerializer.Serialize(new
                {
                    error = new
                    {
                        code = "REPEATED_TOOL_ERROR",
                        message = "This tool call already failed earlier in this turn.",
                        retryable = false,
                        remediation = "Change the arguments or use the remediation from the previous error.",
                    },
                }));
        }

        // Sandbox gate first: unknown/denied/budget-stopped/user-denied calls never reach the server.
        if (sandbox != null)
        {
            var verdict = await sandbox.CheckAsync(call, cancellationToken);
            if (verdict != null)
            {
                Progress?.Invoke($"  ⛔ {call.Name}: {verdict.Note}");
                failedToolCallCount++;
                return ChatMessage.Tool(call.Id, verdict.ErrorJson);
            }
        }

        string content;
        try
        {
            var spec = catalog.Resolve(call.Name);
            using var arguments = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            var validationError = ToolArgumentValidator.Validate(spec.InputSchema, arguments.RootElement);
            if (validationError != null)
            {
                MarkToolFailure(fingerprint);
                content = JsonSerializer.Serialize(new
                {
                    error = new
                    {
                        code = "TOOL_ARGUMENT_INVALID",
                        message = validationError,
                        retryable = false,
                        remediation = "Correct the arguments using the tool schema and call the tool again.",
                    },
                });
                Progress?.Invoke($"  ✗ {call.Name}: {validationError}");
                return ChatMessage.Tool(call.Id, content);
            }

            var result = await spec.Caller.CallAsync<JsonElement>(spec.Name, arguments.RootElement, cancellationToken);
            content = ToolResultCompactor.Compact(result, ToolResultMaxChars);
            successfulToolCalls++;
        }
        catch (ToolCallException ex)
        {
            MarkToolFailure(fingerprint);
            // Structured server error ({ code, message, remediation }) — hand it to the model so it can recover.
            content = JsonSerializer.Serialize(new
            {
                error = new
                {
                    code = ex.Code,
                    message = ex.Message,
                    retryable = IsRetryable(ex.Code),
                    remediation = ex.Remediation,
                },
            });
            Progress?.Invoke($"  ✗ {call.Name}: {ex.Code} — {ex.Message}");
        }
        catch (Exception ex) when (ex is KeyNotFoundException or JsonException)
        {
            MarkToolFailure(fingerprint);
            content = JsonSerializer.Serialize(new
            {
                error = new { code = "AGENT_TOOL_ERROR", message = ex.Message, retryable = false, remediation = (string?)null },
            });
            Progress?.Invoke($"  ✗ {call.Name}: {ex.Message}");
        }

        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Safety net: no tool-side failure (binder, transport, server crash) may kill the
            // whole turn — the model gets the error as a tool result and can recover or report.
            MarkToolFailure(fingerprint);
            content = JsonSerializer.Serialize(new
            {
                error = new { code = "AGENT_TOOL_ERROR", message = ex.Message, retryable = false, remediation = (string?)null },
            });
            Progress?.Invoke($"  ✗ {call.Name}: {ex.Message}");
        }

        return ChatMessage.Tool(call.Id, content);
    }

    private void MarkToolFailure(string fingerprint)
    {
        failedToolCalls.Add(fingerprint);
        failedToolCallCount++;
    }

    private bool HasKnowledgeSchemaFact()
    {
        var schemaCallIds = messages
            .Where(message => message.Role == "assistant" && message.ToolCalls is { Count: > 0 })
            .SelectMany(message => message.ToolCalls!)
            .Where(call => string.Equals(call.Name, "get_schema", StringComparison.Ordinal))
            .Select(call => call.Id)
            .ToHashSet(StringComparer.Ordinal);

        return schemaCallIds.Count > 0
            && messages.Any(message =>
                message.Role == "tool"
                && message.ToolCallId is { } toolCallId
                && schemaCallIds.Contains(toolCallId));
    }

    /// <summary>
    /// Appends the runtime context as a trailing marked user message, but only when it changed
    /// since the last append (DeepSeek context caching, plan I11): the system prompt and all prior
    /// history stay byte-stable, so a context update costs a small tail append instead of busting
    /// the cache for the whole conversation. Also re-appends when cross-turn compaction removed
    /// every copy of the current context.
    /// </summary>
    private void AppendContextIfChanged()
    {
        var context = contextProvider();
        if (string.IsNullOrWhiteSpace(context))
        {
            return;
        }

        if (context == lastContext && messages.Any(SystemPrompt.IsContextMessage))
        {
            return;
        }

        messages.Add(ChatMessage.User(SystemPrompt.ContextMessage(context)));
        if (lastContext != null)
        {
            Progress?.Invoke("runtime context refreshed; appended at the tail (cache prefix kept)");
        }

        lastContext = context;
    }

    private void RefreshSystemMessage()
    {
        // Static rules only — identical bytes every turn, so rewriting is cache-neutral and
        // transparently migrates restored sessions whose system message predates this shape.
        var system = ChatMessage.System(SystemPrompt.Build());
        if (messages.Count > 0 && messages[0].Role == "system")
        {
            messages[0] = system;
        }
        else
        {
            messages.Insert(0, system);
        }
    }

    private static string Summarize(string argumentsJson) =>
        Truncate(argumentsJson, 160);

    private static string ToolCallFingerprint(ChatToolCall call)
    {
        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            return call.Name + "|" + document.RootElement.GetRawText();
        }
        catch (JsonException)
        {
            return call.Name + "|" + call.ArgumentsJson.Trim();
        }
    }

    private static bool IsRetryable(string code) =>
        code is "DB_LOCKED" or "MCP_TRANSPORT_ERROR" or "UNEXPECTED_ERROR";

    private static string CompactHistoricalToolResult(string content, int maxChars)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            return ToolResultCompactor.Compact(document.RootElement, maxChars);
        }
        catch (JsonException)
        {
            return content[..maxChars] + "...[truncated]";
        }
    }

    private static string Truncate(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..maxChars] + "…";
}
