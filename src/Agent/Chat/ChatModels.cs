namespace Agent.Chat;

/// <summary>One message in the OpenAI-compatible chat-completions conversation.</summary>
public sealed record ChatMessage(
    string Role,
    string? Content,
    string? ToolCallId = null,
    IReadOnlyList<ChatToolCall>? ToolCalls = null,
    string? ReasoningContent = null,
    DateTimeOffset? Timestamp = null)
{
    public static ChatMessage System(string content) => new("system", content, Timestamp: DateTimeOffset.Now);

    public static ChatMessage User(string content) => new("user", content, Timestamp: DateTimeOffset.Now);

    public static ChatMessage Assistant(
        string? content,
        IReadOnlyList<ChatToolCall>? toolCalls = null,
        string? reasoningContent = null) =>
        new("assistant", content, ToolCalls: toolCalls, ReasoningContent: reasoningContent, Timestamp: DateTimeOffset.Now);

    public static ChatMessage Tool(string toolCallId, string content) =>
        new("tool", content, ToolCallId: toolCallId, Timestamp: DateTimeOffset.Now);
}

/// <summary>One function call requested by the model (arguments kept as raw JSON).</summary>
public sealed record ChatToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>Parsed assistant response: text and/or tool calls, plus chain-of-thought and token usage.</summary>
public sealed record ChatResponse(
    string? Content,
    IReadOnlyList<ChatToolCall> ToolCalls,
    string? FinishReason,
    UsageInfo? Usage,
    string? ReasoningContent = null);

public sealed record UsageInfo(int PromptTokens, int CompletionTokens, int TotalTokens, int ReasoningTokens = 0);

/// <summary>One streamed piece of a response (SSE delta).</summary>
public sealed record ChatDelta(string? ReasoningContent, string? Content);

/// <summary>
/// Per-request chat parameters (api-docs.deepseek.com/guides/thinking_mode).
/// Thinking mode ignores temperature/top_p — they are only sent when thinking is disabled.
/// </summary>
public sealed record ChatRequestSettings
{
    public const string DefaultModel = "deepseek-v4-flash";
    public const string DefaultReasoningEffort = "high";

    public string Model { get; init; } = DefaultModel;

    /// <summary>thinking: { type: enabled/disabled } — API default is enabled.</summary>
    public bool ThinkingEnabled { get; init; } = true;

    /// <summary>"high" | "max" (API maps low/medium → high). Only relevant when thinking is enabled.</summary>
    public string ReasoningEffort { get; init; } = DefaultReasoningEffort;

    /// <summary>Only sent when thinking is disabled (ignored by the API in thinking mode).</summary>
    public double Temperature { get; init; } = 1.0;

    /// <summary>Only sent when thinking is disabled (ignored by the API in thinking mode).</summary>
    public double TopP { get; init; } = 1.0;
}
