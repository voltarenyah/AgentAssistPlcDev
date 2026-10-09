using Agent.Chat;
using Agent.Mcp;
using Agent.Workbench;
using Contracts.Knowledge;
using Contracts.Sandbox;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace ApiHost.Tests;

/// <summary>
/// The device chat's knowledge freshness pair. A live conversation edited and committed a TIA program,
/// then asked the knowledge agent about it: the device knowledge database was stale, nothing said so,
/// the agent read the old content and reported the change as absent. <c>knowledge_status</c> tells the
/// agent the truth (including an edit the app did not make, which sets no flag), and
/// <c>refresh_knowledge</c> lets it repair the database through the coordinator's guarded path instead
/// of calling the raw knowledge tools, which left the device flagged stale.
/// </summary>
public sealed class KnowledgeToolTests : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), $"knowledge-tools-{Guid.NewGuid():N}");

    [Fact]
    public async Task StatusReportsAChangedSourceAsStaleAndWritesNothing()
    {
        var fixture = ToolFixture.Create(root, appliedHashes: true);
        fixture.WriteSource("Blocks/A.xml", "<changed />");
        var databaseBefore = File.ReadAllBytes(fixture.Device.KnowledgeDbPath);

        var result = await fixture.ReadStatusAsync();

        Assert.Equal("stale", result.GetProperty("state").GetString());
        Assert.Equal(new[] { "Blocks/A.xml" }, Paths(result, "changedPaths"));
        Assert.Equal(1, result.GetProperty("pendingComponentCount").GetInt32());
        Assert.False(result.GetProperty("requiresRebuild").GetBoolean());
        Assert.Contains("refresh_knowledge", result.GetProperty("advisory").GetString()!);
        Assert.Equal(databaseBefore, File.ReadAllBytes(fixture.Device.KnowledgeDbPath));
    }

    /// <summary>The case the persisted flags miss: the source moved without any in-app write, so only
    /// the applied hashes show the database is behind.</summary>
    [Fact]
    public async Task StatusDetectsAnEditThatSetNoFlag()
    {
        var fixture = ToolFixture.Create(root, appliedHashes: true, knowledgeStale: false);
        fixture.WriteSource("Blocks/A.xml", "<edited outside the app />");

        var result = await fixture.ReadStatusAsync();

        Assert.Equal("stale", result.GetProperty("state").GetString());
        Assert.False(result.GetProperty("flaggedStale").GetBoolean());
        Assert.Equal(new[] { "Blocks/A.xml" }, Paths(result, "changedPaths"));
    }

    [Fact]
    public async Task StatusReportsCurrentWhenTheHashesMatch()
    {
        var fixture = ToolFixture.Create(root, appliedHashes: true, knowledgeStale: false);

        var result = await fixture.ReadStatusAsync();

        Assert.Equal("current", result.GetProperty("state").GetString());
        Assert.Equal(0, result.GetProperty("pendingComponentCount").GetInt32());
        Assert.Empty(Paths(result, "changedPaths"));
        Assert.Empty(Paths(result, "addedPaths"));
        Assert.Empty(Paths(result, "removedPaths"));
    }

    [Fact]
    public async Task RefreshAppliesTheChangedComponentsAndClearsTheStaleState()
    {
        var fixture = ToolFixture.Create(root, appliedHashes: true);
        fixture.WriteSource("Blocks/A.xml", "<changed />");

        var result = await fixture.RefreshAsync();

        Assert.Equal(new[] { "update_components" }, fixture.Knowledge.Calls);
        var args = fixture.Knowledge.CallArgs["update_components"].Single();
        Assert.Equal(fixture.Device.SourceRoot, Property<string>(args, "sourceRoot"));
        Assert.Equal(fixture.Device.KnowledgeDbPath, Property<string>(args, "dbPath"));
        Assert.Equal(new[] { "Blocks/A.xml" }, Property<string[]>(args, "relativePaths"));
        Assert.Equal("update", result.GetProperty("mode").GetString());
        Assert.Equal("stale", result.GetProperty("stateBefore").GetString());
        Assert.Equal("current", result.GetProperty("state").GetString());
        Assert.False(fixture.ReadDevice().Knowledge.Stale);
        Assert.False(fixture.ReadDevice().Knowledge.BaselineStale);
    }

    [Fact]
    public async Task RefreshRebuildsWhenNoDatabaseExists()
    {
        var fixture = ToolFixture.Create(root, appliedHashes: false, writeDatabase: false);

        var result = await fixture.RefreshAsync();

        Assert.Equal(new[] { "ingest_source" }, fixture.Knowledge.Calls);
        var args = fixture.Knowledge.CallArgs["ingest_source"].Single();
        Assert.Equal(fixture.Device.SourceRoot, Property<string>(args, "sourceRoot"));
        Assert.Equal(fixture.Device.KnowledgeDbPath, Property<string>(args, "dbPath"));
        Assert.Equal("rebuild", result.GetProperty("mode").GetString());
        Assert.Equal("missing", result.GetProperty("stateBefore").GetString());
        Assert.Equal("current", result.GetProperty("state").GetString());
    }

    [Fact]
    public async Task RefreshRebuildsWhenTheBaselineIsStale()
    {
        var fixture = ToolFixture.Create(root, appliedHashes: true, baselineStale: true);

        var result = await fixture.RefreshAsync();

        Assert.Equal(new[] { "ingest_source" }, fixture.Knowledge.Calls);
        Assert.Equal("rebuild", result.GetProperty("mode").GetString());
        Assert.Equal("current", result.GetProperty("state").GetString());
    }

    /// <summary>The refresh is the path that must stay idempotent: a current database is left alone,
    /// which also keeps the model from rebuilding a large project for nothing.</summary>
    [Fact]
    public async Task RefreshOnACurrentDatabaseCallsNothing()
    {
        var fixture = ToolFixture.Create(root, appliedHashes: true, knowledgeStale: false);

        var result = await fixture.RefreshAsync();

        Assert.Empty(fixture.Knowledge.Calls);
        Assert.Equal("alreadycurrent", result.GetProperty("mode").GetString());
        Assert.Equal("current", result.GetProperty("state").GetString());
    }

    [Fact]
    public async Task BothToolsNeedASelectedDevice()
    {
        var fixture = ToolFixture.Create(root, appliedHashes: true);

        var status = await Assert.ThrowsAsync<ToolCallException>(() => fixture.InvokeWithoutDeviceAsync(
            KnowledgeStatusTool.ToolName));
        var refresh = await Assert.ThrowsAsync<ToolCallException>(() => fixture.InvokeWithoutDeviceAsync(
            KnowledgeRefreshTool.ToolName));

        Assert.Equal("DEVICE_SELECTION_REQUIRED", status.Code);
        Assert.Equal("DEVICE_SELECTION_REQUIRED", refresh.Code);
    }

    /// <summary>Reading freshness must not be able to move the database, and the refresh must be a
    /// write rather than a destructive action: it changes no PLC source, TIA state or Git state, so it
    /// needs no approval card.</summary>
    [Fact]
    public void TheToolsCarryTheirSandboxTiers()
    {
        var policy = new SandboxPolicy();

        Assert.Equal(SandboxTier.Read, policy.Classify(KnowledgeStatusTool.ToolName));
        Assert.Equal(SandboxTier.Write, policy.Classify(KnowledgeRefreshTool.ToolName));
    }

    /// <summary>The HTTP surface of the same answer, on the workbench-scoped route and its legacy
    /// alias.</summary>
    [Fact]
    public async Task TheStatusRouteServesTheDeviceState()
    {
        var fixture = ToolFixture.Create(root, appliedHashes: true);
        fixture.WriteSource("Blocks/A.xml", "<changed />");
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(host =>
            {
                host.UseEnvironment("Testing");
                host.ConfigureServices(services =>
                {
                    services.RemoveAll<WorkbenchCatalog>();
                    services.RemoveAll<AtomicJsonStore>();
                    services.AddSingleton(fixture.Store);
                    services.AddSingleton(fixture.Catalog);
                });
            });
        using var client = factory.CreateClient();

        var status = await client.GetFromJsonAsync<JsonElement>(
            $"/api/workbenches/{fixture.WorkbenchId}/worktrees/{ToolFixture.WorktreeId}"
            + $"/devices/{ToolFixture.DeviceId}/knowledge/status");

        Assert.Equal("stale", status.GetProperty("state").GetString());
        Assert.True(status.GetProperty("flaggedStale").GetBoolean());
        Assert.Equal(1, status.GetProperty("pendingComponentCount").GetInt32());
        Assert.Equal(new[] { "Blocks/A.xml" }, Paths(status, "changedPaths"));
        Assert.False(status.GetProperty("requiresRebuild").GetBoolean());

        // Without a selection the legacy alias refuses the request rather than being unmapped.
        var alias = await client.GetAsync($"/api/devices/{ToolFixture.DeviceId}/knowledge/status");
        Assert.NotEqual(HttpStatusCode.NotFound, alias.StatusCode);
    }

    private static string[] Paths(JsonElement result, string name) =>
        result.GetProperty(name).EnumerateArray().Select(item => item.GetString()!).ToArray();

    private static T Property<T>(object target, string name) =>
        (T)target.GetType().GetProperty(name)!.GetValue(target)!;

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>One device whose source tree, database file and applied hashes the test controls, with
    /// the real coordinator and both tools wired to a scripted knowledge caller.</summary>
    private sealed class ToolFixture
    {
        public const string WorktreeId = "wt-1";
        public const string DeviceId = "dev-1";

        private const string SourceRelativePath = "Blocks/A.xml";
        private const string OriginalContent = "<original />";

        private ToolFixture(
            DeviceContext device,
            AtomicJsonStore store,
            WorkbenchCatalog catalog,
            string workbenchId,
            ScriptedKnowledgeCaller knowledge,
            WorkbenchCoordinator coordinator)
        {
            Device = device;
            Store = store;
            Catalog = catalog;
            WorkbenchId = workbenchId;
            Knowledge = knowledge;
            Coordinator = coordinator;
        }

        public DeviceContext Device { get; }

        public AtomicJsonStore Store { get; }

        public WorkbenchCatalog Catalog { get; }

        public string WorkbenchId { get; }

        public ScriptedKnowledgeCaller Knowledge { get; }

        public WorkbenchCoordinator Coordinator { get; }

        public static ToolFixture Create(
            string parent,
            bool appliedHashes,
            bool writeDatabase = true,
            bool baselineStale = false,
            bool knowledgeStale = true)
        {
            var store = new AtomicJsonStore();
            var catalog = new WorkbenchCatalog(store, Path.Combine(parent, Guid.NewGuid().ToString("N")));
            var workbench = catalog.RegisterWorktree(
                catalog.Create("Line", null),
                new WorkbenchWorktreeRegistration(WorktreeId, "master", "master", "master"));
            var device = WorkbenchPaths.ResolveDevice(
                workbench.WorkbenchId, workbench.RootPath, WorktreeId, "master", DeviceId, "PLC_1");
            Directory.CreateDirectory(device.SourceRoot);
            Directory.CreateDirectory(device.StagingRoot);
            store.Write(Path.Combine(device.WorktreeRoot, "worktree.json"), new WorktreeMetadata(
                WorkbenchSchema.CurrentVersion, WorktreeId, workbench.WorkbenchId, "master", "master",
                DateTimeOffset.UtcNow.ToString("O"), null, null, null, new[] { DeviceId }, null));
            var sourcePath = Path.Combine(device.SourceRoot, "Blocks", "A.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            File.WriteAllText(sourcePath, OriginalContent);
            var hashes = appliedHashes
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [SourceRelativePath] = HashOf(sourcePath),
                }
                : new Dictionary<string, string>(StringComparer.Ordinal);
            store.Write(Path.Combine(device.DeviceRoot, "device.json"), new DeviceMetadata(
                WorkbenchSchema.CurrentVersion, DeviceId, WorktreeId, "PLC_1", "PLC_1",
                null, null, null,
                new KnowledgeState(knowledgeStale, hashes, null, baselineStale),
                Array.Empty<DeviceImportRecord>()));
            if (writeDatabase)
            {
                File.WriteAllText(device.KnowledgeDbPath, "knowledge");
            }

            var knowledge = new ScriptedKnowledgeCaller(device);
            var coordinator = new WorkbenchCoordinator(
                new ScriptedKnowledgeCaller(device),
                knowledge,
                new ScriptedKnowledgeCaller(device),
                catalog,
                store,
                new DeviceReconciler(),
                new DeviceSourceResolver(_ => { }));
            return new ToolFixture(
                device, store, catalog, workbench.WorkbenchId, knowledge, coordinator);
        }

        public void WriteSource(string relative, string content) =>
            File.WriteAllText(Path.Combine(Device.SourceRoot, relative), content);

        public DeviceMetadata ReadDevice() =>
            Store.Read<DeviceMetadata>(Path.Combine(Device.DeviceRoot, "device.json"));

        public Task<JsonElement> ReadStatusAsync() => InvokeAsync(KnowledgeStatusTool.ToolName);

        public Task<JsonElement> RefreshAsync() => InvokeAsync(KnowledgeRefreshTool.ToolName);

        public async Task<JsonElement> InvokeAsync(string tool)
        {
            using var arguments = JsonDocument.Parse("{}");
            return await Spec(tool, Device).Caller.CallAsync<JsonElement>(
                tool, arguments.RootElement, CancellationToken.None);
        }

        public async Task<JsonElement> InvokeWithoutDeviceAsync(string tool)
        {
            using var arguments = JsonDocument.Parse("{}");
            return await Spec(tool, null).Caller.CallAsync<JsonElement>(
                tool, arguments.RootElement, CancellationToken.None);
        }

        private AgentToolSpec Spec(string tool, DeviceContext? device) => tool switch
        {
            KnowledgeStatusTool.ToolName =>
                KnowledgeStatusTool.CreateSpec(new KnowledgeStatusTool(Coordinator), () => device),
            KnowledgeRefreshTool.ToolName =>
                KnowledgeRefreshTool.CreateSpec(new KnowledgeRefreshTool(Coordinator), () => device),
            _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, "Unknown tool."),
        };

        private static string HashOf(string path) =>
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    }

    /// <summary>The knowledge server the coordinator talks to: it records every call and answers with
    /// the result shape the real tool returns, including creating the database file an ingest writes.</summary>
    private sealed class ScriptedKnowledgeCaller(DeviceContext device) : IMcpToolCaller
    {
        public List<string> Calls { get; } = new();

        public Dictionary<string, List<object>> CallArgs { get; } = new(StringComparer.Ordinal);

        public Task<T> CallAsync<T>(string tool, object args, CancellationToken cancellationToken = default)
        {
            Calls.Add(tool);
            if (!CallArgs.TryGetValue(tool, out var list))
            {
                list = new List<object>();
                CallArgs[tool] = list;
            }

            list.Add(args);
            object result = tool switch
            {
                "update_components" => new KnowledgeUpdateResult(
                    device.KnowledgeDbPath,
                    new[] { "block:A" },
                    new Dictionary<string, string> { ["Blocks/A.xml"] = "component-hash" },
                    Array.Empty<string>()),
                "ingest_source" => Ingest(),
                _ => throw new InvalidOperationException($"Unexpected knowledge call '{tool}'."),
            };
            return Task.FromResult((T)result);
        }

        private IngestResult Ingest()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(device.KnowledgeDbPath)!);
            File.WriteAllText(device.KnowledgeDbPath, "knowledge");
            return new IngestResult { DbPath = device.KnowledgeDbPath };
        }
    }
}
