using System.Text.Json;
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
    public const int MaxRounds = 12;
    public const int ToolResultMaxChars = 8000;

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

    /// <summary>The exact conversation sent to DeepSeek (system prompt first, rebuilt per turn).</summary>
    public IReadOnlyList<ChatMessage> History => messages;

    /// <summary>Token usage per API response, aligned 1:1 with the assistant messages in <see cref="History"/>.</summary>
    public IReadOnlyList<UsageInfo?> RoundUsages => roundUsages;

    public void ClearHistory()
    {
        messages.Clear();
        roundUsages.Clear();
    }

    /// <summary>Runs one user turn to completion; returns the assistant's final text.</summary>
    public async Task<string> RunAsync(string userText, CancellationToken cancellationToken = default)
    {
        RefreshSystemMessage();
        messages.Add(ChatMessage.User(userText));

        for (var round = 1; ; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await client.CompleteStreamingAsync(
                messages,
                catalog.ToOpenAiToolsJson(),
                Settings,
                delta => StreamDelta?.Invoke(
                    delta.ReasoningContent != null ? "reasoning" : "content",
                    delta.ReasoningContent ?? delta.Content ?? string.Empty),
                cancellationToken);
            roundUsages.Add(response.Usage);
            if (response.Usage != null)
            {
                var reasoning = response.Usage.ReasoningTokens > 0 ? $" ({response.Usage.ReasoningTokens} reasoning)" : string.Empty;
                Progress?.Invoke(
                    $"usage: {response.Usage.PromptTokens} prompt + {response.Usage.CompletionTokens} completion{reasoning} tokens");
            }

            if (response.ToolCalls.Count == 0)
            {
                var answer = response.Content ?? "(empty response from DeepSeek)";
                messages.Add(ChatMessage.Assistant(answer, reasoningContent: response.ReasoningContent));
                return answer;
            }

            if (round >= MaxRounds)
            {
                var note = $"Stopped after {MaxRounds} tool-calling rounds without a final answer.";
                messages.Add(ChatMessage.Assistant(note, reasoningContent: response.ReasoningContent));
                return note;
            }

            messages.Add(ChatMessage.Assistant(response.Content, response.ToolCalls, response.ReasoningContent));
            foreach (var call in response.ToolCalls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Progress?.Invoke($"→ {call.Name}({Summarize(call.ArgumentsJson)})");
                messages.Add(await ExecuteToolCallAsync(call, cancellationToken));
            }
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
