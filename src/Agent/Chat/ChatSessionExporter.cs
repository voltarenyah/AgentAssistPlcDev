using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Agent.Chat;

/// <summary>
/// Renders a chat session as a Markdown audit file (buildnote/plan/agent.md §7): exactly what was
/// sent to and received from DeepSeek — system prompt, tool definitions, user prompts, assistant
/// answers, tool calls with arguments, tool results as-sent, and token usage per API round.
/// </summary>
public static class ChatSessionExporter
{
    public static string Export(
        IReadOnlyList<ChatMessage> history,
        IReadOnlyList<UsageInfo?> roundUsages,
        JsonArray toolsJson,
        int toolCount,
        string model,
        string requestUri)
    {
        var userTurns = history.Count(message => message.Role == "user");
        var promptTokens = roundUsages.Sum(usage => usage?.PromptTokens ?? 0);
        var completionTokens = roundUsages.Sum(usage => usage?.CompletionTokens ?? 0);
        var reasoningTokens = roundUsages.Sum(usage => usage?.ReasoningTokens ?? 0);

        var markdown = new StringBuilder();
        markdown.AppendLine("# Chat session export — PLC AI Assistant");
        markdown.AppendLine();
        markdown.AppendLine($"- Exported: {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} (local)");
        markdown.AppendLine($"- Model: `{model}`");
        markdown.AppendLine($"- Endpoint: `{requestUri}`");
        markdown.AppendLine($"- Turns: {userTurns} user message(s) · {roundUsages.Count} API round(s)");
        markdown.AppendLine(
            reasoningTokens > 0
                ? $"- Tokens: {promptTokens} prompt + {completionTokens} completion = {promptTokens + completionTokens} total ({reasoningTokens} reasoning)"
                : $"- Tokens: {promptTokens} prompt + {completionTokens} completion = {promptTokens + completionTokens} total");
        markdown.AppendLine();

        var system = history.FirstOrDefault(message => message.Role == "system");
        markdown.AppendLine("## System prompt (current — rebuilt from live context before every turn)");
        markdown.AppendLine();
        AppendFenced(markdown, "text", system?.Content ?? "(none)");
        markdown.AppendLine();

        markdown.AppendLine($"## Tool definitions sent with every request ({toolCount})");
        markdown.AppendLine();
        AppendFenced(markdown, "json", toolsJson.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        markdown.AppendLine();

        markdown.AppendLine("## Conversation");
        markdown.AppendLine();

        var usageIndex = 0;
        var messageIndex = 0;
        foreach (var message in history)
        {
            messageIndex++;
            var timestamp = message.Timestamp?.ToString("HH:mm:ss") ?? "??:??:??";
            switch (message.Role)
            {
                case "system":
                    continue; // rendered above
                case "user":
                    markdown.AppendLine($"### {messageIndex}. user — {timestamp}");
                    markdown.AppendLine();
                    markdown.AppendLine(message.Content);
                    markdown.AppendLine();
                    break;
                case "assistant":
                {
                    var usage = usageIndex < roundUsages.Count ? roundUsages[usageIndex] : null;
                    usageIndex++;
                    var suffix = message.ToolCalls is { Count: > 0 } ? " (tool calls)" : string.Empty;
                    markdown.AppendLine($"### {messageIndex}. assistant — {timestamp}{suffix}");
                    markdown.AppendLine();
                    if (usage != null)
                    {
                        var reasoning = usage.ReasoningTokens > 0 ? $" ({usage.ReasoningTokens} reasoning)" : string.Empty;
                        markdown.AppendLine($"*usage: {usage.PromptTokens} prompt + {usage.CompletionTokens} completion = {usage.TotalTokens} tokens{reasoning}*");
                        markdown.AppendLine();
                    }

                    if (!string.IsNullOrWhiteSpace(message.ReasoningContent))
                    {
                        markdown.AppendLine("Thinking (`reasoning_content`, passed back to the API on tool-call turns):");
                        markdown.AppendLine();
                        AppendFenced(markdown, "text", message.ReasoningContent);
                        markdown.AppendLine();
                    }

                    if (!string.IsNullOrWhiteSpace(message.Content))
                    {
                        markdown.AppendLine(message.Content);
                        markdown.AppendLine();
                    }

                    if (message.ToolCalls is { Count: > 0 })
                    {
                        foreach (var call in message.ToolCalls)
                        {
                            markdown.AppendLine($"Requested tool `{call.Name}` (call id `{call.Id}`) with arguments:");
                            AppendFenced(markdown, "json", PrettyJson(call.ArgumentsJson));
                            markdown.AppendLine();
                        }
                    }

                    break;
                }
                case "tool":
                    markdown.AppendLine($"### {messageIndex}. tool result (call id `{message.ToolCallId}`) — {timestamp}");
                    markdown.AppendLine();
                    AppendFenced(markdown, "json", PrettyJson(message.Content ?? string.Empty));
                    markdown.AppendLine();
                    break;
            }
        }

        return markdown.ToString();
    }

    /// <summary>Default export path: %LOCALAPPDATA%\PlcAiAssistant\chat-exports\chat-&lt;timestamp&gt;.md.</summary>
    public static string ResolveExportPath()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PlcAiAssistant",
            "chat-exports");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"chat-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.md");
    }

    private static void AppendFenced(StringBuilder markdown, string language, string content)
    {
        // A payload may itself contain ``` fences; switch to tildes so the block stays intact.
        var fence = content.Contains("```", StringComparison.Ordinal) ? "~~~~" : "```";
        markdown.AppendLine(fence + language);
        markdown.AppendLine(content);
        markdown.AppendLine(fence);
    }

    private static string PrettyJson(string json)
    {
        try
        {
            return JsonNode.Parse(json)?.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) ?? json;
        }
        catch (JsonException)
        {
            return json;
        }
    }
}
