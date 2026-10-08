using Agent.Workbench;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace ApiHost.Tests;

/// <summary>
/// A device's conversation list is the worktree's sessions filtered to the device that owns them. A
/// worktree keeps every conversation in one directory, so the per-device route has to do that
/// filtering: without it every device received the whole worktree's list, and a surface that fans out
/// over the worktree's devices — the navigator's `SESSIONS` section — showed each conversation once per
/// device.
/// </summary>
public sealed class DeviceSessionListEndpointTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        "api-device-sessions-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DeviceSessionListReturnsOnlyTheConversationsThatDeviceOwns()
    {
        var fixture = await CreateFixtureAsync();
        await using var factory = fixture.Factory;
        using var client = fixture.Client;

        var first = await CreateConversationAsync(client, fixture, "dev-1");
        var second = await CreateConversationAsync(client, fixture, "dev-2");

        var firstList = await ListConversationsAsync(client, fixture, "dev-1");
        var secondList = await ListConversationsAsync(client, fixture, "dev-2");

        Assert.Equal(first, Assert.Single(firstList).GetProperty("sessionId").GetString());
        Assert.Equal(second, Assert.Single(secondList).GetProperty("sessionId").GetString());
        Assert.Equal("dev-1", firstList[0].GetProperty("deviceId").GetString());
        Assert.Equal("dev-2", secondList[0].GetProperty("deviceId").GetString());
    }

    [Fact]
    public async Task DeviceSessionListIsEmptyForADeviceThatOwnsNoConversation()
    {
        var fixture = await CreateFixtureAsync();
        await using var factory = fixture.Factory;
        using var client = fixture.Client;

        await CreateConversationAsync(client, fixture, "dev-1");

        var secondList = await ListConversationsAsync(client, fixture, "dev-2");

        Assert.Empty(secondList);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private async Task<SessionListFixture> CreateFixtureAsync()
    {
        var store = new AtomicJsonStore();
        var catalog = new WorkbenchCatalog(store, root);
        var workbench = catalog.Create("Line", null);
        const string worktreeId = "wt-1";
        workbench = catalog.RegisterWorktree(
            workbench,
            new WorkbenchWorktreeRegistration(worktreeId, "master", "master", "master"));
        var worktreeRoot = Path.Combine(workbench.RootPath, "worktrees", "master");
        Directory.CreateDirectory(worktreeRoot);
        store.Write(Path.Combine(worktreeRoot, "worktree.json"), new WorktreeMetadata(
            "1.0", worktreeId, workbench.WorkbenchId, "master", "master",
            DateTimeOffset.UtcNow.ToString("O"), null, null, null, ["dev-1", "dev-2"], null));
        WriteDevice(worktreeRoot, "PLC_1", "dev-1", worktreeId);
        WriteDevice(worktreeRoot, "PLC_2", "dev-2", worktreeId);

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Testing");
            host.ConfigureServices(services =>
            {
                services.RemoveAll<WorkbenchCatalog>();
                services.RemoveAll<AtomicJsonStore>();
                services.AddSingleton(store);
                services.AddSingleton(catalog);
            });
        });
        return await Task.FromResult(new SessionListFixture(
            factory, factory.CreateClient(), workbench.WorkbenchId, worktreeId));
    }

    private static async Task<string> CreateConversationAsync(
        HttpClient client,
        SessionListFixture fixture,
        string deviceId)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/workbenches/{fixture.WorkbenchId}/worktrees/{fixture.WorktreeId}/devices/{deviceId}/sessions",
            new { settings = new { }, runtimeContext = (string?)null });
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        return created.GetProperty("header").GetProperty("sessionId").GetString()!;
    }

    private static async Task<JsonElement[]> ListConversationsAsync(
        HttpClient client,
        SessionListFixture fixture,
        string deviceId)
    {
        var listed = await client.GetFromJsonAsync<JsonElement>(
            $"/api/workbenches/{fixture.WorkbenchId}/worktrees/{fixture.WorktreeId}/devices/{deviceId}/sessions");
        return listed.EnumerateArray().ToArray();
    }

    private void WriteDevice(string worktreeRoot, string folder, string deviceId, string worktreeId)
    {
        var deviceRoot = Path.Combine(worktreeRoot, "devices", folder);
        Directory.CreateDirectory(deviceRoot);
        new AtomicJsonStore().Write(
            Path.Combine(deviceRoot, "device.json"),
            new DeviceMetadata("1.0", deviceId, worktreeId, folder, folder, null, null, null,
                new KnowledgeState(true, new Dictionary<string, string>(), null), []));
    }

    private sealed record SessionListFixture(
        WebApplicationFactory<Program> Factory,
        HttpClient Client,
        string WorkbenchId,
        string WorktreeId);
}
