using Agent.Chat;
using Xunit;

namespace Agent.Tests;

public sealed class SystemPromptTests
{
    [Fact]
    public void PromptPrefersOfflineKnowledgeBeforeLiveEngineering()
    {
        var prompt = SystemPrompt.Build();

        Assert.Contains("use the offline knowledge DB first", prompt);
        Assert.Contains("Do not call live engineering tools", prompt);
        Assert.Contains("dbPath exists", prompt);
    }

    [Fact]
    public void PromptRequiresSchemaCheckBeforeTheFirstKnowledgeQueryInEachChat()
    {
        var prompt = SystemPrompt.Build();

        Assert.Contains("get_schema", prompt);
        Assert.Contains("Before the first `query` call in each chat", prompt);
        Assert.Contains("ddl", prompt);
        Assert.Contains("nodeKinds", prompt);
        Assert.Contains("edgeTypes", prompt);
        Assert.Contains("exampleQueries", prompt);
        Assert.DoesNotContain("call get_schema only if needed", prompt);
    }

    [Fact]
    public void PromptConstrainsCommonFbInterfaceWorkflow()
    {
        var prompt = SystemPrompt.Build();

        Assert.Contains("For FB/interface questions", prompt);
        Assert.Contains("get_block", prompt);
        Assert.Contains("instance DB", prompt);
        Assert.Contains("call-site network", prompt);
        Assert.Contains("Prefer 1-3 tool calls", prompt);
    }

    [Fact]
    public void PromptRequiresKnowledgeSourceFileLookupForSourceEdits()
    {
        var prompt = SystemPrompt.Build();

        Assert.Contains("search", prompt);
        Assert.Contains("kind='FB'", prompt);
        Assert.Contains("sourceFile", prompt);
        Assert.Contains("device roots", prompt);
        Assert.Contains("repoPath", prompt);
        Assert.Contains("host-bound", prompt);
        Assert.DoesNotContain("runtime context lists the device's exported source files", prompt);
    }

    [Fact]
    public void PromptPointsAtContextMessageForRuntimeContext()
    {
        var prompt = SystemPrompt.Build();

        Assert.Contains(SystemPrompt.ContextMessageMarker, prompt);
        Assert.Contains("latest runtime context message", prompt);
    }

    [Fact]
    public void ContextMessageCarriesMarkerAndBody()
    {
        var message = ChatMessage.User(SystemPrompt.ContextMessage("Knowledge DB: C:\\db\\k.db"));

        Assert.True(SystemPrompt.IsContextMessage(message));
        Assert.Equal("Knowledge DB: C:\\db\\k.db", SystemPrompt.ContextBody(message));
        Assert.False(SystemPrompt.IsContextMessage(ChatMessage.User("ordinary question")));
        Assert.False(SystemPrompt.IsContextMessage(ChatMessage.Assistant(SystemPrompt.ContextMessageMarker)));
    }
}
