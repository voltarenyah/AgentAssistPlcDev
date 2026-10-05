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
    public void PromptExplainsHowToRecoverTruncatedNetworkLogic()
    {
        var prompt = SystemPrompt.Build();

        Assert.Contains("get_network_logic", prompt);
        Assert.Contains("nextOffset", prompt);
        Assert.Contains("hasMore", prompt);
        Assert.Contains("_truncated", prompt);
        Assert.Contains("logicTruncated", prompt);
    }

    [Fact]
    public void PromptRequiresKnowledgeSourceFileLookupForSourceEdits()
    {
        var prompt = SystemPrompt.Build();

        Assert.Contains("search", prompt);
        Assert.Contains("kind='FB'", prompt);
        Assert.Contains("sourceFile", prompt);
        Assert.Contains("PLC source", prompt);
        Assert.Contains("repoPath", prompt);
        Assert.Contains("host-bound", prompt);
        Assert.DoesNotContain("runtime context lists the device's exported source files", prompt);
    }

    [Fact]
    public void PromptDescribesOneCheckedOutSourceAndSeparatesItFromTiaState()
    {
        var prompt = SystemPrompt.Build();

        Assert.Contains("checked-out feature XML directly", prompt);
        Assert.Contains("TIA Portal is unchanged until", prompt);
        Assert.DoesNotContain("exported baseline", prompt);
        Assert.DoesNotContain("modified-source overlay", prompt);
        Assert.DoesNotContain("source roots", prompt);
    }

    [Fact]
    public void PromptDoesNotAskTheAgentToCompareAnInPlaceEditWithItself()
    {
        var prompt = SystemPrompt.Build();

        Assert.Contains("Do not diff the source file against itself", prompt);
        Assert.Contains("validate the edited XML without baselineFilePath", prompt);
        Assert.DoesNotContain("src_diff of baseline vs overlay", prompt);
        Assert.DoesNotContain("src_validate against the baseline", prompt);
    }

    [Fact]
    public void PromptPointsAtContextMessageForRuntimeContext()
    {
        var prompt = SystemPrompt.Build();

        Assert.Contains(SystemPrompt.ContextMessageMarker, prompt);
        Assert.Contains("latest runtime context message", prompt);
    }

    [Fact]
    public void PromptExplainsRecordingAFindingAsATaskAndItsApproval()
    {
        var prompt = SystemPrompt.Build();

        Assert.Contains("create_task", prompt);
        Assert.Contains("bound to the selected device", prompt);
        Assert.Contains("proposedSolutions", prompt);
        Assert.Contains("the approval card shows the whole brief", prompt);
        // An approved card is the only proof the task exists; a denial must not be reported as a task.
        Assert.Contains("do not say the task exists until the tool result names its id", prompt);
        Assert.DoesNotContain("create a task without approval", prompt);
    }

    [Fact]
    public void PromptExplainsStagingASourceObjectAndWhereItsIdComesFrom()
    {
        var prompt = SystemPrompt.Build();

        Assert.Contains("stage_task_source_object", prompt);
        Assert.Contains("list_source_objects", prompt);
        // A live conversation stalled because the model built an id out of a knowledge-base node id;
        // the rule has to say that the two id spaces are not the same one.
        Assert.Contains("never build one out of a knowledge-base node id", prompt);
        Assert.Contains("`block:Main` is a knowledge-base id, not a source object id", prompt);
        Assert.Contains("the tool cannot stage without it", prompt);
        Assert.Contains("takeOverFromTaskId", prompt);
    }

    [Fact]
    public void PromptMakesTheCurrentToolListBeatAStaleClaimFromAnEarlierTurn()
    {
        var prompt = SystemPrompt.Build();

        // A live conversation with the assistant's own older "I have no task-creation tool" answer in
        // its history refused to call create_task after the tool had shipped: the earlier statement won
        // over both the tool list and this prompt's create_task rule, so the rule has to say which of
        // the two is authoritative.
        Assert.Contains("Your tool list arrives with every request and is authoritative", prompt);
        Assert.Contains("Check the current tool list again before reporting a tool as unavailable", prompt);
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
