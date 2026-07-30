using System.Text.Json.Nodes;

namespace Agent.Chat;

/// <summary>
/// Heuristic pre-send prompt-size estimate (no tokenizer dependency): ~4 chars/token over
/// message content, reasoning_content, tool-call payloads and the tool-definition JSON,
/// plus a small fixed overhead per message. Expect ±20%; used for warnings, not billing.
/// </summary>
public static class TokenEstimator
{
    public const int CharsPerToken = 4;

    private const int PerMessageOverheadTokens = 4;

    public static int Estimate(IReadOnlyList<ChatMessage> messages, JsonArray? tools)
    {
        long chars = 0;
        var overheadTokens = 0;
        foreach (var message in messages)
        {
            overheadTokens += PerMessageOverheadTokens;
            chars += message.Content?.Length ?? 0;
            chars += message.ReasoningContent?.Length ?? 0;
            if (message.ToolCalls == null)
            {
                continue;
            }

            foreach (var call in message.ToolCalls)
            {
                chars += call.Id.Length + call.Name.Length + call.ArgumentsJson.Length;
            }
        }

        if (tools != null)
        {
            chars += tools.ToJsonString().Length;
        }

        return (int)Math.Min(int.MaxValue, chars / CharsPerToken + overheadTokens + 1);
    }
}
