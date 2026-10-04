using Agent.Workbench;
using Agent.Workbench.EngineeringGraph;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

/// <summary>
/// AC-004 at the HTTP boundary: the device routes and the selection routes re-project a device whose
/// stored manifest digest no longer matches the manifest on disk — or whose projection a write point
/// flagged — before they serve, and a failed re-projection is reported as a projection failure rather
/// than as stale facts.
/// </summary>
public sealed class DeviceFactFreshnessEndpointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"api-freshness-{Guid.NewGuid():N}");

    /// <summary>AC-004: an edit outside the application that moves a projected manifest field but not
    /// the exported content is detected at the read boundary, and the response reflects it.</summary>
    [Fact]
    public async Task DeviceReadReprojectsBeforeServingWhenAProjectedManifestFieldChanged()
    {
        await using var fixture = await FreshnessApiFixture.CreateAsync(_root);
        fixture.WriteManifest("Main", status: "Exported");

        var first = await fixture.Client.GetFromJsonAsync<JsonElement>(fixture.DeviceRoute);
        var before = Assert.Single(first.GetProperty("sourceObjects").EnumerateArray());
        Assert.Equal("Main", before.GetProperty("name").GetString());
        Assert.Equal("Exported", before.GetProperty("status").GetString());
        var digestBefore = fixture.StoredText(DevicePropertyNames.ProjectionManifestDigest);

        // An out-of-app edit: the status moves, the exported content hash does not.
        fixture.WriteManifest("Main", status: "Modified");

        var second = await fixture.Client.GetFromJsonAsync<JsonElement>(fixture.DeviceRoute);
        var after = Assert.Single(second.GetProperty("sourceObjects").EnumerateArray());
        Assert.Equal("Main", after.GetProperty("name").GetString());
        Assert.Equal("Modified", after.GetProperty("status").GetString());
        Assert.NotEqual(digestBefore, fixture.StoredText(DevicePropertyNames.ProjectionManifestDigest));
        Assert.Equal("HASH-A", fixture.StoredText(SourceObjectPropertyNames.ContentHash));
        Assert.False(fixture.StoredFlag(DevicePropertyNames.ProjectionInvalidated));
    }

    /// <summary>AC-004: the device selection route runs the boundary itself — the projection a write
    /// point flagged is re-projected at selection, before any read of it.</summary>
    [Fact]
    public async Task DeviceSelectionReprojectsAProjectionAWritePointInvalidated()
    {
        await using var fixture = await FreshnessApiFixture.CreateAsync(_root);
        fixture.WriteManifest("Main", status: "Exported");
        var first = await fixture.Client.GetFromJsonAsync<JsonElement>(fixture.DeviceRoute);
        Assert.Equal("Main", Assert.Single(first.GetProperty("sourceObjects").EnumerateArray()).GetProperty("name").GetString());

        // A write point changed the facts and said so.
        fixture.WriteManifest("Renamed", status: "Exported");
        fixture.InvalidateProjection();
        Assert.True(fixture.StoredFlag(DevicePropertyNames.ProjectionInvalidated));

        var selected = await fixture.Client.PostAsync(fixture.SelectRoute, null);

        Assert.Equal(HttpStatusCode.NoContent, selected.StatusCode);
        Assert.False(fixture.StoredFlag(DevicePropertyNames.ProjectionInvalidated));
        Assert.Equal("Renamed", fixture.StoredText(SourceObjectPropertyNames.Name));
    }

    /// <summary>AC-004: a re-projection that fails at the selection boundary is reported as a projection
    /// failure — never as a successful selection serving the stale facts.</summary>
    [Fact]
    public async Task SelectionBoundaryReportsAFailedReprojectionAsAProjectionFailure()
    {
        await using var fixture = await FreshnessApiFixture.CreateAsync(_root);
        fixture.WriteManifest("Main", status: "Exported");
        await fixture.Client.GetFromJsonAsync<JsonElement>(fixture.DeviceRoute);

        fixture.WriteManifest("Renamed", status: "Exported");
        fixture.RefuseProjectionWrites();

        var response = await fixture.Client.PostAsync(fixture.SelectRoute, null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(EngineeringGraphProjectionException.FailureCode, body.GetProperty("error").GetString());
        Assert.False(fixture.StoredFlag(DevicePropertyNames.ProjectionInvalidated));
        Assert.Equal("Main", fixture.StoredText(SourceObjectPropertyNames.Name));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FreshnessApiFixture : IAsyncDisposable
    {
        private readonly WebApplicationFactory<Program> _factory;

        private FreshnessApiFixture(WebApplicationFactory<Program> factory, HttpClient client, WorkbenchMetadata workbench)
        {
            _factory = factory;
            Client = client;
            WorkbenchRoot = workbench.RootPath;
            WorkbenchId = workbench.WorkbenchId;
        }

        public HttpClient Client { get; }
        public string WorkbenchRoot { get; }
        public string WorkbenchId { get; }
        public string DeviceRoute => $"/api/workbenches/{WorkbenchId}/worktrees/wt-1/devices/dev-1";
        public string SelectRoute => $"{DeviceRoute}/select";

        private string SourceRoot => Path.Combine(WorkbenchRoot, "worktrees", "master", "devices", "PLC_1", "source");

        public static Task<FreshnessApiFixture> CreateAsync(string fixtureRoot)
        {
            var store = new AtomicJsonStore();
            var catalog = new WorkbenchCatalog(store, fixtureRoot);
            var workbench = catalog.Create("Line", null);
            workbench = catalog.RegisterWorktree(
                workbench,
                new WorkbenchWorktreeRegistration("wt-1", "master", "master", "master"));
            var worktreeRoot = Path.Combine(workbench.RootPath, "worktrees", "master");
            var deviceRoot = Path.Combine(worktreeRoot, "devices", "PLC_1");
            Directory.CreateDirectory(Path.Combine(deviceRoot, "source"));
            store.Write(
                Path.Combine(worktreeRoot, "worktree.json"),
                new WorktreeMetadata(
                    WorkbenchSchema.CurrentVersion, "wt-1", workbench.WorkbenchId, "master", "master",
                    DateTimeOffset.UtcNow.ToString("O"), null, null, null, ["dev-1"], null));
            store.Write(
                Path.Combine(deviceRoot, "device.json"),
                new DeviceMetadata(
                    WorkbenchSchema.CurrentVersion, "dev-1", "wt-1", "PLC_1", "engineering-dev-1", null, null, null,
                    new KnowledgeState(false, new Dictionary<string, string>(), "2026-07-29T08:00:00Z", false),
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
            return Task.FromResult(new FreshnessApiFixture(factory, client, workbench));
        }

        public void WriteManifest(string name, string status)
        {
            Directory.CreateDirectory(SourceRoot);
            File.WriteAllText(
                Path.Combine(SourceRoot, "metadata.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = "1.0",
                    device = new { plcName = "PLC_1" },
                    components = new object[]
                    {
                        new
                        {
                            id = "block-1",
                            name,
                            sourcePath = "Area/Main",
                            category = "OB",
                            status,
                            exportedFile = "Blocks/Area/Main [OB1].xml",
                            number = 1,
                            programmingLanguage = "LAD",
                            contentHash = "HASH-A",
                            siemensTypeName = "OB",
                            isKnowHowProtected = false,
                            modifiedDate = "2026-07-20T10:00:00.0000000+00:00",
                            fingerprints = new Dictionary<string, string> { ["Code"] = "AAAA1111" },
                        },
                    },
                }));
        }

        public void InvalidateProjection() => WithGraph(graph => graph.InvalidateDeviceProjection("dev-1"));

        /// <summary>Every read still works; only the projection's write fails.</summary>
        public void RefuseProjectionWrites()
        {
            using var store = new EngineeringGraphStore(WorkbenchRoot);
            using var command = store.Connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER refuse_projection_write BEFORE INSERT ON graph_entity_properties
                BEGIN SELECT RAISE(ABORT, 'projection write refused'); END;
                """;
            command.ExecuteNonQuery();
        }

        public string? StoredText(string name) => Stored(name).Text;

        public bool? StoredFlag(string name) => Stored(name).Flag;

        private GraphProperty Stored(string name)
        {
            using var store = new EngineeringGraphStore(WorkbenchRoot);
            var graph = new EngineeringGraphService(store, WorkbenchId, id => id == "wt-1");
            return graph.GetProperties(GraphEntityKind.Device, "dev-1")
                .Concat(graph.GetProperties(GraphEntityKind.SourceObject, "dev-1:block-1"))
                .Single(property => property.Name == name);
        }

        private void WithGraph(Action<EngineeringGraphService> action)
        {
            using var store = new EngineeringGraphStore(WorkbenchRoot);
            action(new EngineeringGraphService(store, WorkbenchId, id => id == "wt-1"));
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _factory.DisposeAsync();
        }
    }
}
