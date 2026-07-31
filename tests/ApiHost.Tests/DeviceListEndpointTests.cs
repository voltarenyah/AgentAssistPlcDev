using Agent.Chat;
using Agent.Workbench;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace ApiHost.Tests;

/// <summary>The devices list endpoint feeds the navigator tree: entries must carry the
/// human-readable PLC name from device.json alongside the opaque device object id.</summary>
public sealed class DeviceListEndpointTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "api-device-list-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DevicesListReturnsPlcNamesFromDeviceMetadata()
    {
        var store = new AtomicJsonStore();
        var catalog = new WorkbenchCatalog(store, root);
        var wb = catalog.Create("Line", null);
        const string wtId = "wt-1";
        wb = catalog.RegisterWorktree(wb, new(wtId, "master", "master", "master"));
        var wtRoot = Path.Combine(wb.RootPath, "worktrees", "master");
        Directory.CreateDirectory(wtRoot);
        var wt = new WorktreeMetadata("1.0", wtId, wb.WorkbenchId, "master", "master",
            DateTimeOffset.UtcNow.ToString("O"), null, null, null, ["dev-1", "dev-2"], null);
        store.Write(Path.Combine(wtRoot, "worktree.json"), wt);
        WriteDevice(wtRoot, "PLC_1", "dev-1", "PLC_1", wtId);
        WriteDevice(wtRoot, "Sino_PEI", "dev-2", "Sino_PEI", wtId);

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Testing");
            host.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["chatSettings:reasoningEffort"] = ChatRequestSettings.DefaultReasoningEffort,
                }));
            host.ConfigureServices(services =>
            {
                services.RemoveAll<WorkbenchCatalog>();
                services.RemoveAll<AtomicJsonStore>();
                services.AddSingleton(store);
                services.AddSingleton(catalog);
            });
        });
        using var client = factory.CreateClient();

        var devices = await client.GetFromJsonAsync<JsonElement>(
            $"/api/workbenches/{wb.WorkbenchId}/worktrees/{wtId}/devices");

        Assert.Equal(
            new[] { "dev-1", "dev-2" },
            devices!.EnumerateArray().Select(d => d.GetProperty("deviceId").GetString()).ToArray());
        Assert.Equal(
            new[] { "PLC_1", "Sino_PEI" },
            devices!.EnumerateArray().Select(d => d.GetProperty("plcName").GetString()).ToArray());
    }

    private void WriteDevice(string wtRoot, string folder, string deviceId, string plcName, string worktreeId)
    {
        var deviceRoot = Path.Combine(wtRoot, "devices", folder);
        Directory.CreateDirectory(deviceRoot);
        new AtomicJsonStore().Write(
            Path.Combine(deviceRoot, "device.json"),
            new DeviceMetadata("1.0", deviceId, worktreeId, plcName, plcName, null, null, null,
                new KnowledgeState(true, new Dictionary<string, string>(), null), []));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
