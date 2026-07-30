using System.Text.Json;
using System.Text.Json.Nodes;
using Agent.Mcp;

namespace Agent.Chat;

/// <summary>
/// The tool-calling conversation loop (buildnote/plan/agent.md): user text → DeepSeek (streaming)
/// → MCP tool calls → final answer. Conversation history (including tool messages and assistant
/// reasoning_content — required by the API on tool-call turns) persists across turns; the system
/// message is rebuilt per run from the live context.
/// </summary>
public sealed class AgentLoop
{
    /// <summary>Default tool-calling round cap per turn (see <see cref="RoundLimit"/>).</summary>
    public const int MaxRounds = 12;

    public const int ToolResultMaxChars = 8000;

    /// <summary>Head kept when an old tool result is compacted (see <see cref="PromptTokenBudget"/>).</summary>
    public const int ToolResultCompactChars = 500;

    private const string FinalRoundInstruction =
        "Tool budget exhausted. Answer now with what you have, cite what you found, and state clearly what remains unverified.";

    private readonly DeepSeekClient client;
    private readonly McpToolCatalog catalog;
    private readonly Func<string> contextProvider;
    private readonly AgentSandbox? sandbox;
    private readonly List<ChatMessage> messages = new();
    private readonly List<UsageInfo?> roundUsages = new();

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

    /// <summary>True when the last <see cref="RunAsync"/> hit <see cref="RoundLimit"/> and ended with a forced no-tools answer.</summary>
    public bool LastTurnHitRoundCap { get; private set; }

    /// <summary>The exact conversation sent to DeepSeek (system prompt first, rebuilt per turn).</summary>
    public IReadOnlyList<ChatMessage> History => messages;

    /// <summary>Token usage per API response, aligned 1:1 with the assistant messages in <see cref="History"/>.</summary>
    public IReadOnlyList<UsageInfo?> RoundUsages => roundUsages;

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
    }

    /// <summary>
    /// Replace the current in-memory conversation state with loaded session data.
    /// Does NOT persist — the caller must save separately via <see cref="SessionManager"/>.
    /// </summary>
    public void RestoreFrom(List<ChatMessage> history, List<UsageInfo?> usages)
    {
        messages.Clear();
        messages.AddRange(history);
        roundUsages.Clear();
        roundUsages.AddRange(usages);
    }

    /// <summary>Runs one user turn to completion; returns the assistant's final text.</summary>
    public async Task<string> RunAsync(string userText, CancellationToken cancellationToken = default)
    {
        LastTurnHitRoundCap = false;
        RefreshSystemMessage();
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

            messages[i] = message with { Content = message.Content[..ToolResultCompactChars] + "…[truncated]" };
            compacted++;
        }

        if (compacted > 0)
        {
            Progress?.Invoke(
                $"prompt budget exceeded ({cumulativePromptTokens} > {PromptTokenBudget} cumulative); compacted {compacted} old tool result(s)");
        }
    }

    private async Task<ChatMessage> ExecuteToolCallAsync(ChatToolCall call, CancellationToken cancellationToken)
    {
        // Sandbox gate first: unknown/denied/budget-stopped/user-denied calls never reach the server.
        if (sandbox != null)
        {
            var verdict = await sandbox.CheckAsync(call, cancellationToken);
            if (verdict != null)
            {
                Progress?.Invoke($"  ⛔ {call.Name}: {verdict.Note}");
                return ChatMessage.Tool(call.Id, verdict.ErrorJson);
            }
        }

        string content;
        try
        {
            var spec = catalog.Resolve(call.Name);
            using var arguments = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            var result = await spec.Caller.CallAsync<JsonElement>(spec.Name, arguments.RootElement, cancellationToken);
            content = Truncate(result.GetRawText(), ToolResultMaxChars);
        }
        catch (ToolCallException ex)
        {
            // Structured server error ({ code, message, remediation }) — hand it to the model so it can recover.
            content = JsonSerializer.Serialize(new
            {
                error = new { code = ex.Code, message = ex.Message, remediation = ex.Remediation },
            });
            Progress?.Invoke($"  ✗ {call.Name}: {ex.Code} — {ex.Message}");
        }
        catch (Exception ex) when (ex is KeyNotFoundException or JsonException)
        {
            content = JsonSerializer.Serialize(new
            {
                error = new { code = "AGENT_TOOL_ERROR", message = ex.Message, remediation = (string?)null },
            });
            Progress?.Invoke($"  ✗ {call.Name}: {ex.Message}");
        }

        return ChatMessage.Tool(call.Id, content);
    }

    private void RefreshSystemMessage()
    {
        var system = ChatMessage.System(SystemPrompt.Build(contextProvider()));
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

    private static string Truncate(string text, int maxChars) =>
        text.Length <= maxChars ? text : text[..maxChars] + "…";
}
