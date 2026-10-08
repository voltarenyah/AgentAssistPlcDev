using Agent.Workbench;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

public sealed class ChatTurnEndpointsTests
{
    [Fact]
    public async Task ChatProducerRunsEvenWhenRequestCancellationHasAlreadyBeenSignaled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var producer = ApiChatService.StartProducerAsync(() =>
        {
            started.SetResult(true);
            return Task.CompletedTask;
        }, cancellation.Token);

        await producer;

        Assert.True(started.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ChatSettingsExposeDefaultContextWindow()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();

        var settings = await client.GetFromJsonAsync<JsonElement>("/api/config/settings");

        Assert.Equal(128_000, settings.GetProperty("contextWindow").GetInt32());
    }

    [Fact]
    public async Task ContextWindowIsConfigurable()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseSetting("chatSettings:contextWindow", "65536");
            });
        using var client = factory.CreateClient();

        var settings = await client.GetFromJsonAsync<JsonElement>("/api/config/settings");

        Assert.Equal(65_536, settings.GetProperty("contextWindow").GetInt32());
    }

    [Fact]
    public async Task ChatSettingsExposeDefaultLoopPolicy()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();

        var settings = await client.GetFromJsonAsync<JsonElement>("/api/config/settings");

        Assert.Equal(12, settings.GetProperty("roundLimit").GetInt32());
        Assert.Equal(300_000, settings.GetProperty("promptTokenBudget").GetInt32());
        Assert.Equal(100_000, settings.GetProperty("promptTokenWarningThreshold").GetInt32());
        Assert.Equal(8_000, settings.GetProperty("toolResultMaxChars").GetInt32());
        Assert.Equal(500, settings.GetProperty("toolResultCompactChars").GetInt32());
        Assert.Equal(90_000, settings.GetProperty("historyTokenThreshold").GetInt32());
        Assert.Equal(2, settings.GetProperty("recentTurnsToKeep").GetInt32());
        Assert.Equal(500, settings.GetProperty("collapsedAnswerChars").GetInt32());
    }

    [Fact]
    public async Task LoopPolicyIsConfigurable()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.UseSetting("chatSettings:historyTokenThreshold", "12345");
                builder.UseSetting("chatSettings:recentTurnsToKeep", "4");
            });
        using var client = factory.CreateClient();

        var settings = await client.GetFromJsonAsync<JsonElement>("/api/config/settings");

        Assert.Equal(12_345, settings.GetProperty("historyTokenThreshold").GetInt32());
        Assert.Equal(4, settings.GetProperty("recentTurnsToKeep").GetInt32());
    }

    [Fact]
    public async Task GrantRoundsWithoutDeviceSelectionReturnsBadRequest()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();

        var response = await client.PostAsync(
            "/api/chat/grant-rounds",
            JsonContent.Create(new { additional = 6 }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "DEVICE_SELECTION_REQUIRED",
            await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task GrantRoundsWithoutActiveChatReturnsNotFound()
    {
        await using var fixture = await SelectedDeviceFixture.CreateAsync(
            Path.Combine(Path.GetTempPath(), "api-chat-turn-" + Guid.NewGuid().ToString("N")));

        var response = await fixture.Client.PostAsync(
            "/api/chat/grant-rounds",
            JsonContent.Create(new { additional = 6 }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The device chat must name the conversation's device by TIA's own PLC name. The internal device id
    /// is a GUID, so a tool that takes a PLC name — open_block_in_editor among them — could not be called
    /// correctly: a live conversation stalled on exactly that with AMBIGUOUS_PLC.
    /// </summary>
    [Fact]
    public void DeviceRuntimeContextNamesTheDeviceByItsPlcName()
    {
        var device = Context();
        const string knowledgeState = "stale — the PLC source changed after the last knowledge update";
        var context = ApiChatService.DeviceRuntimeContext(
            device,
            new WorkbenchMetadata(
                WorkbenchSchema.CurrentVersion, device.WorkbenchId, "Packaging Line",
                DateTimeOffset.UtcNow.ToString("O"), device.WorkbenchRoot, device.WorkbenchRoot,
                null, null, []),
            new WorktreeMetadata(
                WorkbenchSchema.CurrentVersion, device.WorktreeId, device.WorkbenchId, "Valve tuning",
                "feature/valves", DateTimeOffset.UtcNow.ToString("O"), null, null, null,
                [device.DeviceId], null),
            new DeviceMetadata(
                WorkbenchSchema.CurrentVersion, device.DeviceId, device.WorktreeId, "PLC_1", "PLC_1",
                null, null, null,
                new KnowledgeState(true, new Dictionary<string, string>(), null, false),
                []),
            knowledgeState);

        Assert.Contains($"Device: PLC_1 ({device.DeviceId})", context);
        Assert.Contains($"Workbench: Packaging Line ({device.WorkbenchId})", context);
        Assert.Contains("Worktree: Valve tuning [feature/valves]", context);
        Assert.Contains($"PLC source: {device.SourceRoot}", context);
        Assert.Contains($"Knowledge DB: {device.KnowledgeDbPath}", context);
        // One formatter, one knowledge line: the caller's state text is what the model sees.
        Assert.Contains($"Knowledge state: {knowledgeState}", context);
        // The id alone is what the model used to get, and it could do nothing with it.
        Assert.DoesNotContain($"Device: {device.DeviceId} (", context);
    }

    /// <summary>Metadata this process cannot read degrades to the id form instead of failing the turn.</summary>
    [Fact]
    public void DeviceRuntimeContextFallsBackToTheDeviceIdWithoutMetadata()
    {
        var device = Context();

        var context = ApiChatService.DeviceRuntimeContext(device, null, null, null, "current");

        Assert.Contains($"Device: {device.DeviceId} ({device.DeviceId})", context);
        Assert.Contains($"Worktree: {device.WorktreeId} [-]", context);
        Assert.Contains("Knowledge state: current", context);
    }

    /// <summary>
    /// The knowledge line's four states, with the action each one implies. The authoritative
    /// hash-based answer stays with <c>knowledge_status</c>; this is the cheap per-turn signal.
    /// </summary>
    [Fact]
    public void KnowledgeStateTextNamesTheStateAndTheActionItImplies()
    {
        var device = Context();
        var fresh = new DeviceMetadata(
            WorkbenchSchema.CurrentVersion, device.DeviceId, device.WorktreeId, "PLC_1", "PLC_1",
            null, null, null,
            new KnowledgeState(false, new Dictionary<string, string>(), null, false),
            []);
        var stale = fresh with
        {
            Knowledge = new KnowledgeState(true, new Dictionary<string, string>(), null, false),
        };
        var baselineStale = fresh with
        {
            Knowledge = new KnowledgeState(false, new Dictionary<string, string>(), null, true),
        };

        var missing = ApiChatService.KnowledgeStateText(dbExists: false, metadata: null);
        Assert.StartsWith("missing —", missing);
        Assert.Contains("refresh_knowledge", missing);

        var unknown = ApiChatService.KnowledgeStateText(dbExists: true, metadata: null);
        Assert.StartsWith("unknown —", unknown);
        Assert.Contains("knowledge_status", unknown);

        var current = ApiChatService.KnowledgeStateText(dbExists: true, metadata: fresh);
        Assert.StartsWith("current", current);

        Assert.StartsWith("stale —", ApiChatService.KnowledgeStateText(dbExists: true, metadata: stale));
        // The baseline flag is part of the same answer: the knowledge predates the source either way.
        Assert.StartsWith("stale —", ApiChatService.KnowledgeStateText(dbExists: true, metadata: baselineStale));
    }

    private static DeviceContext Context()
    {
        var root = Path.Combine(Path.GetTempPath(), "api-chat-context");
        var deviceRoot = Path.Combine(root, "worktrees", "master", "devices", "PLC_1");
        return new DeviceContext(
            "wb-1",
            "wt-1",
            "d7f3c1e5a9b24c8e8f0a1b2c3d4e5f60",
            root,
            Path.Combine(root, "worktrees", "master"),
            deviceRoot,
            Path.Combine(deviceRoot, "source"),
            Path.Combine(deviceRoot, "staging"),
            Path.Combine(deviceRoot, "plc-knowledge.db"));
    }

    /// <summary>Minimal stand-in for WorkbenchEndpointsTests.SelectedApiFixture: one selected device.</summary>
    private sealed class SelectedDeviceFixture : IAsyncDisposable
    {
        private readonly WebApplicationFactory<Program> factory;

        private SelectedDeviceFixture(WebApplicationFactory<Program> factory, HttpClient client)
        {
            this.factory = factory;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<SelectedDeviceFixture> CreateAsync(string fixtureRoot)
        {
            var store = new AtomicJsonStore();
            var catalog = new WorkbenchCatalog(store, fixtureRoot);
            var workbench = catalog.Create("Line", null);
            const string worktreeId = "wt-1";
            const string deviceId = "dev-1";
            workbench = catalog.RegisterWorktree(
                workbench,
                new WorkbenchWorktreeRegistration(worktreeId, "master", "master", "master"));
            var worktreeRoot = Path.Combine(workbench.RootPath, "worktrees", "master");
            var deviceRoot = Path.Combine(worktreeRoot, "devices", "PLC_1");
            Directory.CreateDirectory(deviceRoot);
            store.Write(
                Path.Combine(worktreeRoot, "worktree.json"),
                new WorktreeMetadata(
                    "1.0", worktreeId, workbench.WorkbenchId, "master", "master",
                    DateTimeOffset.UtcNow.ToString("O"), null, null, null, [deviceId], null));
            store.Write(
                Path.Combine(deviceRoot, "device.json"),
                new DeviceMetadata(
                    "1.0", deviceId, worktreeId, "PLC_1", "PLC:1", null, null, null,
                    new KnowledgeState(false, new Dictionary<string, string>(), null, false),
                    []));

            var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
            {
                host.UseEnvironment("Testing");
                host.ConfigureServices(services =>
                {
                    services.RemoveAll<WorkbenchCatalog>();
                    services.RemoveAll<AtomicJsonStore>();
                    services.RemoveAll<WorkbenchApiState>();
                    services.AddSingleton(store);
                    services.AddSingleton(catalog);
                    services.AddSingleton<WorkbenchApiState>();
                });
            });
            var client = factory.CreateClient();
            var select = await client.PostAsync(
                $"/api/workbenches/{workbench.WorkbenchId}/worktrees/{worktreeId}/devices/{deviceId}/select",
                null);
            select.EnsureSuccessStatusCode();
            return new SelectedDeviceFixture(factory, client);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await factory.DisposeAsync();
        }
    }
}
