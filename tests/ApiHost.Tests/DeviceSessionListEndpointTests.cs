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
/// A device's conversation list is the worktree's sessions filtered to the device that owns them, and
/// the worktree's own list is the whole directory. A worktree keeps every conversation in one place, so
/// the per-device route has to do that filtering: without it every device received the whole worktree's
/// list, and a surface that fans out over the worktree's devices — the navigator's `SESSIONS` section —
/// showed each conversation once per device. The worktree route is the other half: it is the only list
/// that can reach a conversation whose own header names no device, which no device route may return.
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

    /// <summary>
    /// The worktree's own list is the whole session directory, so a surface that is about the
    /// worktree's conversations rather than one device's — the worktree page's own conversation list —
    /// reads every one of them in a single request.
    /// </summary>
    [Fact]
    public async Task WorktreeSessionListReturnsEveryConversationWhicheverDeviceOwnsIt()
    {
        var fixture = await CreateFixtureAsync();
        await using var factory = fixture.Factory;
        using var client = fixture.Client;

        var first = await CreateConversationAsync(client, fixture, "dev-1");
        var second = await CreateConversationAsync(client, fixture, "dev-2");

        var listed = await ListWorktreeConversationsAsync(client, fixture);

        Assert.Equal(
            new[] { first, second }.OrderBy(id => id, StringComparer.Ordinal),
            listed.Select(item => item.GetProperty("sessionId").GetString()!).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(
            new[] { "dev-1", "dev-2" },
            listed.Select(item => item.GetProperty("deviceId").GetString()!).OrderBy(id => id, StringComparer.Ordinal));
    }

    /// <summary>
    /// A conversation whose own header names no device is still the worktree's. It belongs to no
    /// device, so the per-device routes are right to omit it — which is exactly why the worktree route
    /// has to list it: it is the only list that can, and a conversation no surface can reach is a
    /// conversation the user cannot delete or read.
    /// </summary>
    [Fact]
    public async Task WorktreeSessionListIncludesAConversationNoDeviceOwns()
    {
        var fixture = await CreateFixtureAsync();
        await using var factory = fixture.Factory;
        using var client = fixture.Client;

        var owned = await CreateConversationAsync(client, fixture, "dev-1");
        var orphan = await WriteConversationWithoutADeviceAsync(fixture);

        var listed = await ListWorktreeConversationsAsync(client, fixture);

        Assert.Equal(
            new[] { orphan, owned }.OrderBy(id => id, StringComparer.Ordinal),
            listed.Select(item => item.GetProperty("sessionId").GetString()!).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(
            JsonValueKind.Null,
            listed.Single(item => item.GetProperty("sessionId").GetString() == orphan).GetProperty("deviceId").ValueKind);
        // The per-device lists stay what they were: this conversation is not any device's.
        Assert.Empty(await ListConversationsAsync(client, fixture, "dev-2"));
        Assert.Equal(owned, Assert.Single(await ListConversationsAsync(client, fixture, "dev-1"))
            .GetProperty("sessionId").GetString());
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
            factory, factory.CreateClient(), workbench.WorkbenchId, worktreeId, worktreeRoot));
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

    /// <summary>
    /// Writes the one shape no route creates any more: a conversation file whose header names no
    /// device, which is what a worktree written before conversations carried a device holds. It is
    /// written as the file itself, because the only writer goes through a device that the header would
    /// then have to match.
    /// </summary>
    private static async Task<string> WriteConversationWithoutADeviceAsync(SessionListFixture fixture)
    {
        const string sessionId = "orphan-session";
        var directory = Path.Combine(fixture.WorktreeRoot, ".automation", "sessions");
        Directory.CreateDirectory(directory);
        var now = DateTimeOffset.UtcNow.ToString("O");
        await File.WriteAllTextAsync(
            Path.Combine(directory, sessionId + ".json"),
            $$"""
            {
              "header": {
                "sessionId": "{{sessionId}}",
                "workbenchId": "{{fixture.WorkbenchId}}",
                "worktreeId": "{{fixture.WorktreeId}}",
                "worktreeRoot": {{JsonSerializer.Serialize(fixture.WorktreeRoot)}},
                "knowledgeDbPath": {{JsonSerializer.Serialize(Path.Combine(fixture.WorktreeRoot, "knowledge.db"))}},
                "createdAt": "{{now}}",
                "updatedAt": "{{now}}",
                "title": "Orphan conversation"
              },
              "messages": []
            }
            """);
        return sessionId;
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

    private static async Task<JsonElement[]> ListWorktreeConversationsAsync(
        HttpClient client,
        SessionListFixture fixture)
    {
        var listed = await client.GetFromJsonAsync<JsonElement>(
            $"/api/workbenches/{fixture.WorkbenchId}/worktrees/{fixture.WorktreeId}/sessions");
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
        string WorktreeId,
        string WorktreeRoot);
}
