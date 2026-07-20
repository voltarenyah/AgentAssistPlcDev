using System.Text.Json;
using System.Text.Json.Nodes;
using Agent.Chat;
using Contracts.Sandbox;
using Xunit;

namespace Agent.Tests;

/// <summary>
/// Sandbox gate in the tool-calling loop (agent sandbox, 2026-07-20): tier classification,
/// destructive confirmation, session grants, budget, fail-closed unknowns — all asserted through
/// the full HTTP → tool-call round trip with the scripted endpoint.
/// </summary>
public sealed class AgentLoopSandboxTests
{
    private static string SseText(string text) =>
        FakeHttpEndpoint.Sse(FakeHttpEndpoint.DeltaChunk(text), FakeHttpEndpoint.FinalChunk("stop", 10, 5));

    private static string SseToolCall(string id, string name, string arguments) =>
        FakeHttpEndpoint.Sse(FakeHttpEndpoint.ToolCallChunk(id, name, arguments), FakeHttpEndpoint.FinalChunk("tool_calls", 10, 5));

    private sealed class Harness
    {
        public required AgentLoop Loop { get; set; }
        public required FakeHttpEndpoint Endpoint { get; init; }
        public required FakeToolCaller Caller { get; init; }
        public required List<string> Progress { get; init; }
        public required List<ToolConfirmationRequest> ConfirmationRequests { get; init; }
        public Queue<ToolConfirmation> Confirmations { get; } = new();

        public static Harness Create(int budget = 20, SandboxPolicy? policy = null, bool withConfirmation = true, SandboxAudit? audit = null)
        {
            var endpoint = new FakeHttpEndpoint();
            var caller = new FakeToolCaller();
            var catalog = new McpToolCatalog(new[]
            {
                Spec("search", caller),
                Spec("save_project", caller),
                Spec("wipe_disk", caller), // exposed by the server but absent from the policy
            });
            var requests = new List<ToolConfirmationRequest>();
            var harness = new Harness
            {
                Endpoint = endpoint,
                Caller = caller,
                Progress = new List<string>(),
                ConfirmationRequests = requests,
                Loop = null!,
            };
            var sandbox = new AgentSandbox(
                policy ?? new SandboxPolicy(),
                budget,
                withConfirmation
                    ? request =>
                    {
                        requests.Add(request);
                        return Task.FromResult(harness.Confirmations.Count > 0
                            ? harness.Confirmations.Dequeue()
                            : ToolConfirmation.Deny);
                    }
                    : (Func<ToolConfirmationRequest, Task<ToolConfirmation>>?)null,
                audit);
            var client = new DeepSeekClient("sk-test", "https://api.deepseek.com", new HttpClient(endpoint));
            harness.Loop = new AgentLoop(client, catalog, () => "ctx", sandbox: sandbox);
            harness.Loop.Progress += harness.Progress.Add;
            return harness;
        }

        private static AgentToolSpec Spec(string name, FakeToolCaller caller) =>
            new(name, name, JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement, caller);

        public string LastToolMessageContent()
        {
            var messages = JsonNode.Parse(Endpoint.RequestBodies[^1])!["messages"]!.AsArray();
            return messages[^1]!["content"]!.GetValue<string>();
        }
    }

    [Fact]
    public async Task ReadToolRunsWithoutConfirmation()
    {
        var harness = Harness.Create();
        harness.Endpoint
            .RespondJson(SseToolCall("c1", "search", """{"text":"x"}"""))
            .RespondJson(SseText("found it"));
        harness.Caller.Respond("search", JsonDocument.Parse("{}").RootElement);

        var answer = await harness.Loop.RunAsync("find x");

        Assert.Equal("found it", answer);
        Assert.Equal(new[] { "search" }, harness.Caller.Calls.ToArray());
        Assert.Empty(harness.ConfirmationRequests);
    }

    [Fact]
    public async Task DestructiveDeniedByUserNeverReachesTheServer()
    {
        var harness = Harness.Create();
        harness.Endpoint
            .RespondJson(SseToolCall("c1", "save_project", "{}"))
            .RespondJson(SseText("I could not save."));
        harness.Confirmations.Enqueue(ToolConfirmation.Deny);

        await harness.Loop.RunAsync("save the project");

        Assert.Empty(harness.Caller.Calls);
        Assert.Single(harness.ConfirmationRequests);
        Assert.Contains("SANDBOX_USER_DENIED", harness.LastToolMessageContent());
        Assert.Contains(harness.Progress, line => line.Contains("⛔") && line.Contains("save_project"));
    }

    [Fact]
    public async Task DestructiveAllowOnceExecutes()
    {
        var harness = Harness.Create();
        harness.Endpoint
            .RespondJson(SseToolCall("c1", "save_project", "{}"))
            .RespondJson(SseText("saved"));
        harness.Caller.Respond("save_project", JsonDocument.Parse("{}").RootElement);
        harness.Confirmations.Enqueue(ToolConfirmation.AllowOnce);

        var answer = await harness.Loop.RunAsync("save the project");

        Assert.Equal("saved", answer);
        Assert.Equal(new[] { "save_project" }, harness.Caller.Calls.ToArray());
        Assert.Single(harness.ConfirmationRequests);
    }

    [Fact]
    public async Task AllowSessionGrantsTheRestOfTheSession()
    {
        var harness = Harness.Create();
        harness.Endpoint
            .RespondJson(SseToolCall("c1", "save_project", "{}"))
            .RespondJson(SseToolCall("c2", "save_project", "{}"))
            .RespondJson(SseText("saved twice"));
        harness.Caller
            .Respond("save_project", JsonDocument.Parse("{}").RootElement)
            .Respond("save_project", JsonDocument.Parse("{}").RootElement);
        harness.Confirmations.Enqueue(ToolConfirmation.AllowSession);

        await harness.Loop.RunAsync("save twice");

        Assert.Equal(2, harness.Caller.Calls.Count(name => name == "save_project"));
        Assert.Single(harness.ConfirmationRequests); // asked once, second call rode the session grant
    }

    [Fact]
    public async Task BudgetExhaustionBlocksBeforeAsking()
    {
        var harness = Harness.Create(budget: 0);
        harness.Endpoint
            .RespondJson(SseToolCall("c1", "save_project", "{}"))
            .RespondJson(SseText("blocked"));

        await harness.Loop.RunAsync("save");

        Assert.Empty(harness.Caller.Calls);
        Assert.Empty(harness.ConfirmationRequests);
        Assert.Contains("SANDBOX_BUDGET_EXCEEDED", harness.LastToolMessageContent());
    }

    [Fact]
    public async Task DestructiveWithoutConfirmationChannelIsDenied()
    {
        var harness = Harness.Create(withConfirmation: false);
        harness.Endpoint
            .RespondJson(SseToolCall("c1", "save_project", "{}"))
            .RespondJson(SseText("blocked"));

        await harness.Loop.RunAsync("save");

        Assert.Empty(harness.Caller.Calls);
        Assert.Contains("SANDBOX_NO_CONFIRMATION", harness.LastToolMessageContent());
    }

    [Fact]
    public async Task ToolMissingFromPolicyIsDeniedFailClosed()
    {
        var harness = Harness.Create();
        harness.Endpoint
            .RespondJson(SseToolCall("c1", "wipe_disk", "{}"))
            .RespondJson(SseText("that tool is blocked"));

        await harness.Loop.RunAsync("wipe the disk");

        Assert.Empty(harness.Caller.Calls);
        Assert.Contains("SANDBOX_TOOL_UNKNOWN", harness.LastToolMessageContent());
    }

    [Fact]
    public async Task DenyTierBlocksEvenWithConfirmationWired()
    {
        var policy = new SandboxPolicy(new Dictionary<string, SandboxTier>
        {
            ["search"] = SandboxTier.Denied,
        });
        var harness = Harness.Create(policy: policy);
        harness.Endpoint
            .RespondJson(SseToolCall("c1", "search", "{}"))
            .RespondJson(SseText("blocked"));

        await harness.Loop.RunAsync("search");

        Assert.Empty(harness.Caller.Calls);
        Assert.Empty(harness.ConfirmationRequests);
        Assert.Contains("SANDBOX_TOOL_DENIED", harness.LastToolMessageContent());
    }

    [Fact]
    public async Task DecisionsLandInTheAuditTrail()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agent-sandbox-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var audit = new SandboxAudit(directory, "agent");
            var harness = Harness.Create(audit: audit);
            harness.Endpoint
                .RespondJson(SseToolCall("c1", "save_project", "{}"))
                .RespondJson(SseText("no"));
            harness.Confirmations.Enqueue(ToolConfirmation.Deny);

            await harness.Loop.RunAsync("save");

            var lines = File.ReadAllLines(Path.Combine(directory, "agent.jsonl"));
            Assert.Single(lines);
            Assert.Contains("save_project", lines[0]);
            Assert.Contains("SANDBOX_USER_DENIED", lines[0]);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
