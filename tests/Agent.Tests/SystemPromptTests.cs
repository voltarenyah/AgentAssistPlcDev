using Agent.Chat;
using Xunit;

namespace Agent.Tests;

public sealed class SystemPromptTests
{
    [Fact]
    public void PromptPrefersOfflineKnowledgeBeforeLiveEngineering()
    {
        var prompt = SystemPrompt.Build("Knowledge DB: C:\\db\\plc-knowledge.db");

        Assert.Contains("use the offline knowledge DB first", prompt);
        Assert.Contains("Do not call live engineering tools", prompt);
        Assert.Contains("dbPath exists", prompt);
    }

    [Fact]
    public void PromptConstrainsCommonFbInterfaceWorkflow()
    {
        var prompt = SystemPrompt.Build("Knowledge DB: C:\\db\\plc-knowledge.db");

        Assert.Contains("For FB/interface questions", prompt);
        Assert.Contains("get_block", prompt);
        Assert.Contains("instance DB", prompt);
        Assert.Contains("call-site network", prompt);
        Assert.Contains("Prefer 1-3 tool calls", prompt);
    }
}
