using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agent.Workbench;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

public sealed class WorkbenchTagEndpointsTests
{
    [Fact]
    public async Task TagRoutesPersistTaxonomyExposeEffectiveTagsAndSearchRegisteredUnavailableWorktrees()
    {
        await using var fixture = await TagApiFixture.CreateAsync();

        var area = await CreatePathAsync(fixture.Client, "area");
        var assembly = await CreatePathAsync(fixture.Client, "area/assembly");
        var priority = await CreatePathAsync(fixture.Client, "priority/high");

        using var taxonomy = await fixture.Client.GetAsync("/api/tags");
        taxonomy.EnsureSuccessStatusCode();
        var taxonomyBody = await taxonomy.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(4, taxonomyBody.GetProperty("nodes").GetArrayLength());
        Assert.Equal(4, new WorkbenchTagStore(fixture.TagFile).Load().Nodes.Count);

        var removable = await CreatePathAsync(fixture.Client, "removable");
        using var deleteTag = await fixture.Client.DeleteAsync($"/api/tags/{removable}");
        Assert.Equal(HttpStatusCode.NoContent, deleteTag.StatusCode);

        using var invalidPath = await fixture.Client.PostAsJsonAsync("/api/tags/path", new { path = "area//invalid" });
        await AssertErrorAsync(invalidPath, HttpStatusCode.BadRequest, "invalid_path");

        var categoryOne = await CreatePathAsync(fixture.Client, "category/one");
        _ = await CreatePathAsync(fixture.Client, "category/two");
        using var rename = await fixture.Client.PatchAsJsonAsync($"/api/tags/{categoryOne}", new { name = "uno" });
        rename.EnsureSuccessStatusCode();
        using var collision = await fixture.Client.PatchAsJsonAsync($"/api/tags/{categoryOne}", new { name = "two" });
        await AssertErrorAsync(collision, HttpStatusCode.Conflict, "sibling_name_conflict");

        using var beforeAssign = await fixture.Client.GetAsync($"/api/workbenches/{fixture.WorkbenchId}/tags");
        var beforeTags = await beforeAssign.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, beforeTags.GetProperty("direct").GetArrayLength());
        Assert.Equal(0, beforeTags.GetProperty("inherited").GetArrayLength());
        Assert.Equal(0, beforeTags.GetProperty("effective").GetArrayLength());

        using var assignWorkbench = await fixture.Client.PostAsync(
            $"/api/workbenches/{fixture.WorkbenchId}/tags/{assembly}", null);
        Assert.Equal(HttpStatusCode.NoContent, assignWorkbench.StatusCode);

        using var afterWorkbenchAssign = await fixture.Client.GetAsync($"/api/workbenches/{fixture.WorkbenchId}/tags");
        var workbenchTags = await afterWorkbenchAssign.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal([assembly], StringArray(workbenchTags, "direct"));
        Assert.Equal(0, workbenchTags.GetProperty("inherited").GetArrayLength());
        Assert.Equal([assembly], StringArray(workbenchTags, "effective"));

        using var assignMissingWorktree = await fixture.Client.PostAsync(
            $"/api/workbenches/{fixture.WorkbenchId}/worktrees/{fixture.MissingWorktreeId}/tags/{priority}", null);
        Assert.Equal(HttpStatusCode.NoContent, assignMissingWorktree.StatusCode);

        using var worktreeTagsResponse = await fixture.Client.GetAsync(
            $"/api/workbenches/{fixture.WorkbenchId}/worktrees/{fixture.MissingWorktreeId}/tags");
        var worktreeTags = await worktreeTagsResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal([priority], StringArray(worktreeTags, "direct"));
        Assert.Equal([assembly], StringArray(worktreeTags, "inherited"));
        Assert.Equal([assembly, priority], StringArray(worktreeTags, "effective"));

        using var unassignWorktree = await fixture.Client.DeleteAsync(
            $"/api/workbenches/{fixture.WorkbenchId}/worktrees/{fixture.MissingWorktreeId}/tags/{priority}");
        Assert.Equal(HttpStatusCode.NoContent, unassignWorktree.StatusCode);
        using var afterWorktreeUnassign = await fixture.Client.GetAsync(
            $"/api/workbenches/{fixture.WorkbenchId}/worktrees/{fixture.MissingWorktreeId}/tags");
        var afterWorktreeUnassignTags = await afterWorktreeUnassign.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, afterWorktreeUnassignTags.GetProperty("direct").GetArrayLength());
        Assert.Equal([assembly], StringArray(afterWorktreeUnassignTags, "effective"));
        using var reassignMissingWorktree = await fixture.Client.PostAsync(
            $"/api/workbenches/{fixture.WorkbenchId}/worktrees/{fixture.MissingWorktreeId}/tags/{priority}", null);
        Assert.Equal(HttpStatusCode.NoContent, reassignMissingWorktree.StatusCode);

        using var unknownTag = await fixture.Client.PostAsync(
            $"/api/workbenches/{fixture.WorkbenchId}/tags/unknown", null);
        await AssertErrorAsync(unknownTag, HttpStatusCode.NotFound, "tag_not_found");

        using var unknownWorktree = await fixture.Client.PostAsync(
            $"/api/workbenches/{fixture.WorkbenchId}/worktrees/unknown/tags/{area}", null);
        await AssertErrorAsync(unknownWorktree, HttpStatusCode.NotFound, "worktree_not_found");

        using var search = await fixture.Client.PostAsJsonAsync("/api/workbenches/search", new { tagIds = new[] { area, priority } });
        search.EnsureSuccessStatusCode();
        var searchBody = await search.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, searchBody.GetProperty("workbenches").GetArrayLength());
        var matchingWorktree = Assert.Single(searchBody.GetProperty("worktrees").EnumerateArray());
        Assert.Equal("worktree", matchingWorktree.GetProperty("entityType").GetString());
        Assert.Equal(fixture.MissingWorktreeId, matchingWorktree.GetProperty("entityId").GetString());
        Assert.Equal(fixture.WorkbenchId, matchingWorktree.GetProperty("workbenchId").GetString());
        Assert.False(matchingWorktree.GetProperty("available").GetBoolean());
        Assert.Equal([priority], StringArray(matchingWorktree, "direct"));
        Assert.Equal([assembly, priority], StringArray(matchingWorktree, "effective"));

        using var unassignWorkbench = await fixture.Client.DeleteAsync(
            $"/api/workbenches/{fixture.WorkbenchId}/tags/{assembly}");
        Assert.Equal(HttpStatusCode.NoContent, unassignWorkbench.StatusCode);
        using var afterWorkbenchUnassign = await fixture.Client.GetAsync($"/api/workbenches/{fixture.WorkbenchId}/tags");
        var afterWorkbenchUnassignTags = await afterWorkbenchUnassign.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, afterWorkbenchUnassignTags.GetProperty("direct").GetArrayLength());
        Assert.Equal(0, afterWorkbenchUnassignTags.GetProperty("effective").GetArrayLength());

        using var unknownSearchTag = await fixture.Client.PostAsJsonAsync("/api/workbenches/search", new { tagIds = new[] { "unknown" } });
        await AssertErrorAsync(unknownSearchTag, HttpStatusCode.NotFound, "tag_not_found");
    }

    private static async Task<string> CreatePathAsync(HttpClient client, string path)
    {
        using var response = await client.PostAsJsonAsync("/api/tags/path", new { path });
        response.EnsureSuccessStatusCode();
        var node = await response.Content.ReadFromJsonAsync<JsonElement>();
        return node.GetProperty("tagId").GetString()!;
    }

    private static string[] StringArray(JsonElement body, string property) =>
        body.GetProperty(property).EnumerateArray().Select(value => value.GetString()!).ToArray();

    private static async Task AssertErrorAsync(HttpResponseMessage response, HttpStatusCode statusCode, string error)
    {
        Assert.Equal(statusCode, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(error, body.GetProperty("error").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("message").GetString()));
    }

    private sealed class TagApiFixture : IAsyncDisposable
    {
        private readonly string _root;
        private readonly WebApplicationFactory<Program> _factory;

        private TagApiFixture(string root, WebApplicationFactory<Program> factory, HttpClient client, string workbenchId, string missingWorktreeId, string tagFile)
        {
            _root = root;
            _factory = factory;
            Client = client;
            WorkbenchId = workbenchId;
            MissingWorktreeId = missingWorktreeId;
            TagFile = tagFile;
        }

        public HttpClient Client { get; }
        public string WorkbenchId { get; }
        public string MissingWorktreeId { get; }
        public string TagFile { get; }

        public static Task<TagApiFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "api-workbench-tags-" + Guid.NewGuid().ToString("N"));
            var store = new AtomicJsonStore();
            var catalog = new WorkbenchCatalog(store, root);
            var workbench = catalog.Create("Fixture", null);
            workbench = catalog.RegisterWorktree(workbench, new WorkbenchWorktreeRegistration(
                "wt-present", "Present", "present", "present"));
            workbench = catalog.RegisterWorktree(workbench, new WorkbenchWorktreeRegistration(
                "wt-missing", "Missing", "missing", "missing"));
            var presentRoot = Path.Combine(workbench.RootPath, "worktrees", "present");
            Directory.CreateDirectory(presentRoot);
            File.WriteAllText(Path.Combine(presentRoot, "worktree.json"), "{}");
            var tagFile = Path.Combine(root, "tags", "tags.json");

            var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Testing");
                builder.ConfigureServices(services =>
                {
                    services.RemoveAll<AtomicJsonStore>();
                    services.RemoveAll<WorkbenchCatalog>();
                    services.RemoveAll<WorkbenchApiState>();
                    services.RemoveAll<WorkbenchTagStore>();
                    services.AddSingleton(store);
                    services.AddSingleton(catalog);
                    services.AddSingleton<WorkbenchApiState>();
                    services.AddSingleton(new WorkbenchTagStore(tagFile, store));
                });
            });
            return Task.FromResult(new TagApiFixture(
                root,
                factory,
                factory.CreateClient(),
                workbench.WorkbenchId,
                "wt-missing",
                tagFile));
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _factory.DisposeAsync();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }
}
