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
