using System.Text.Json.Nodes;
using Agent.Chat;
using Xunit;

namespace Agent.Tests;

public sealed class TokenEstimatorTests
{
    private static readonly ChatMessage[] Conversation =
    {
        ChatMessage.System("You are the PLC assistant."),
        ChatMessage.User("where is Motor_Start used?"),
        ChatMessage.Assistant(null, new[] { new ChatToolCall("call_1", "search", """{"text":"Motor_Start"}""") }, "reasoning about the search plan"),
        ChatMessage.Tool("call_1", """{"matches":[{"id":"network:000_Main:3"}]}"""),
    };

    private static readonly JsonArray Tools = new()
    {
        new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = "search",
                ["description"] = "find text in the knowledge base",
                ["parameters"] = new JsonObject { ["type"] = "object" },
            },
        },
    };

    [Fact]
    public void EstimateIsPositiveAndInCharHeuristicRange()
    {
        var estimate = TokenEstimator.Estimate(Conversation, Tools);

        var totalChars = Conversation.Sum(m =>
            (m.Content?.Length ?? 0) + (m.ReasoningContent?.Length ?? 0) +
            (m.ToolCalls?.Sum(c => c.Id.Length + c.Name.Length + c.ArgumentsJson.Length) ?? 0)) + Tools.ToJsonString().Length;

        // ~chars/4 plus per-message overhead: must stay positive and within a sane band.
        Assert.InRange(estimate, totalChars / 8, totalChars);
    }

    [Fact]
    public void EstimateCountsToolsAndReasoningContent()
    {
        var withTools = TokenEstimator.Estimate(Conversation, Tools);
        var withoutTools = TokenEstimator.Estimate(Conversation, null);
        Assert.True(withTools > withoutTools);

        var withoutReasoning = Conversation
            .Select(m => m with { ReasoningContent = null })
            .ToArray();
        Assert.True(withTools > TokenEstimator.Estimate(withoutReasoning, Tools));
    }
}
