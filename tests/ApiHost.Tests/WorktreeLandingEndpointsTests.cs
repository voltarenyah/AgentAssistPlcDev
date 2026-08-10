using Agent.Workbench;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net.Http.Json;
using System.Text.Json;
using System.Net;
using Xunit;

/// <summary>Endpoints behind the project/worktree landing pages
/// (buildnote/plan/project-worktree-landing-pages.md): overview aggregate, metadata
/// PATCH semantics, and the worktree task list.</summary>
public sealed class WorktreeLandingEndpointsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "api-landing-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task OverviewAggregatesWorktreeMetadataAndTaskCounts()
    {
        await using var fixture = LandingFixture.Create(root);
        fixture.WriteWorktree(
            "wt-1", "master", "master",
            purpose: "Baseline", owner: "Ansel", status: WorktreeStatus.Ongoing);
        fixture.WriteWorktree(
            "wt-2", "Feature A", "feature/a",
            purpose: "Cylinder retrofit", owner: null, status: WorktreeStatus.Finished,
            finishedUtc: new DateTimeOffset(2026, 8, 1, 10, 0, 0, TimeSpan.Zero));
        var tasks = new WorktreeTaskStore(fixture.Store);
        var wt2Root = fixture.WorktreeRoot("feature-a");
        tasks.Add(wt2Root, "Adapt FB_Motor_Control");
        tasks.Add(wt2Root, "Update HMI tags");
        var done = tasks.Add(wt2Root, "Review interlocks");
        tasks.Update(wt2Root, done.TaskId, task => task with { Status = WorktreeTaskStatus.Done });
        // A registered worktree without any files on disk still shows up with defaults.
        fixture.RegisterWorktree("wt-3", "Empty", "empty", "empty");

        var overview = await fixture.Client.GetFromJsonAsync<JsonElement>(
            $"/api/workbenches/{fixture.WorkbenchId}/overview");

        Assert.Equal(fixture.WorkbenchId, overview.GetProperty("workbenchId").GetString());
        Assert.Equal("Line", overview.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, overview.GetProperty("purpose").ValueKind);
        var entries = overview.GetProperty("worktrees").EnumerateArray().ToArray();
        Assert.Equal(3, entries.Length);

        var master = Assert.Single(entries, e => e.GetProperty("worktreeId").GetString() == "wt-1");
        Assert.Equal("master", master.GetProperty("branch").GetString());
        Assert.Equal("Baseline", master.GetProperty("purpose").GetString());
        Assert.Equal("Ansel", master.GetProperty("owner").GetString());
        Assert.Equal("ongoing", master.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, master.GetProperty("finishedUtc").ValueKind);
        Assert.Equal(0, master.GetProperty("openTasks").GetInt32());
        Assert.Equal(0, master.GetProperty("totalTasks").GetInt32());

        var feature = Assert.Single(entries, e => e.GetProperty("worktreeId").GetString() == "wt-2");
        Assert.Equal("Feature A", feature.GetProperty("name").GetString());
        Assert.Equal("finished", feature.GetProperty("status").GetString());
        Assert.Equal("2026-08-01T10:00:00+00:00", feature.GetProperty("finishedUtc").GetString());
        Assert.Equal(2, feature.GetProperty("openTasks").GetInt32());
        Assert.Equal(3, feature.GetProperty("totalTasks").GetInt32());

        var empty = Assert.Single(entries, e => e.GetProperty("worktreeId").GetString() == "wt-3");
        Assert.Equal("Empty", empty.GetProperty("name").GetString());
        Assert.Equal("ongoing", empty.GetProperty("status").GetString());
        Assert.Equal(0, empty.GetProperty("openTasks").GetInt32());
        Assert.Equal(0, empty.GetProperty("totalTasks").GetInt32());
    }

    [Fact]
    public async Task OverviewToleratesCorruptWorktreeMetadata()
    {
        await using var fixture = LandingFixture.Create(root);
        fixture.RegisterWorktree("wt-1", "master", "master", "master");
        Directory.CreateDirectory(fixture.WorktreeRoot("master"));
        File.WriteAllText(
            Path.Combine(fixture.WorktreeRoot("master"), "worktree.json"),
            "{ not json");

        var response = await fixture.Client.GetAsync(
            $"/api/workbenches/{fixture.WorkbenchId}/overview");

        response.EnsureSuccessStatusCode();
        var overview = await response.Content.ReadFromJsonAsync<JsonElement>();
        var entry = Assert.Single(overview.GetProperty("worktrees").EnumerateArray());
        Assert.Equal("master", entry.GetProperty("name").GetString());
        Assert.Equal("ongoing", entry.GetProperty("status").GetString());
    }

    [Fact]
    public async Task WorktreeDetailReturnsFullMetadata()
    {
        await using var fixture = LandingFixture.Create(root);
        fixture.WriteWorktree(
            "wt-1", "master", "master",
            purpose: "Baseline", owner: "Ansel",
            sourceProjectPath: @"C:\Projects\Line.ap17");

        var detail = await fixture.Client.GetFromJsonAsync<JsonElement>(
            $"/api/workbenches/{fixture.WorkbenchId}/worktrees/wt-1");

        Assert.Equal("wt-1", detail.GetProperty("worktreeId").GetString());
        Assert.Equal(fixture.WorkbenchId, detail.GetProperty("workbenchId").GetString());
        Assert.Equal("master", detail.GetProperty("name").GetString());
        Assert.Equal("master", detail.GetProperty("branch").GetString());
        Assert.Equal(@"C:\Projects\Line.ap17", detail.GetProperty("sourceProjectPath").GetString());
        Assert.Equal("dev-1", Assert.Single(detail.GetProperty("deviceIds").EnumerateArray()).GetString());
        Assert.Equal("Baseline", detail.GetProperty("purpose").GetString());
        Assert.Equal("Ansel", detail.GetProperty("owner").GetString());
        Assert.Equal("ongoing", detail.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("baseCommit").ValueKind);
    }

    [Fact]
    public async Task PatchWorkbenchUpdatesOnlySuppliedFields()
    {
        await using var fixture = LandingFixture.Create(root);
        fixture.WriteWorktree("wt-1", "master", "master", owner: "Ansel");
        var route = $"/api/workbenches/{fixture.WorkbenchId}";

        var first = await fixture.Patch(route, new { owner = "Ansel" });
        first.EnsureSuccessStatusCode();
        var second = await fixture.Patch(route, new { purpose = "Ramp-up line" });
        second.EnsureSuccessStatusCode();
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Ramp-up line", body.GetProperty("purpose").GetString());
        Assert.Equal("Ansel", body.GetProperty("owner").GetString());

        // A follow-up GET sees the same persisted values.
        var reloaded = await fixture.Client.GetFromJsonAsync<JsonElement>(route);
        Assert.Equal("Ramp-up line", reloaded.GetProperty("purpose").GetString());
        Assert.Equal("Ansel", reloaded.GetProperty("owner").GetString());

        // Empty string is a real value and overwrites.
        var cleared = await fixture.Patch(route, new { owner = "" });
        cleared.EnsureSuccessStatusCode();
        var clearedBody = await cleared.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("", clearedBody.GetProperty("owner").GetString());
        Assert.Equal("Ramp-up line", clearedBody.GetProperty("purpose").GetString());
    }

    [Fact]
    public async Task PatchWorktreeManagesFinishedUtcOnStatusTransitions()
    {
        await using var fixture = LandingFixture.Create(root);
        fixture.WriteWorktree("wt-1", "master", "master", purpose: "Baseline");
        var route = $"/api/workbenches/{fixture.WorkbenchId}/worktrees/wt-1";

        var finished = await fixture.Patch(route, new { status = "finished" });
        finished.EnsureSuccessStatusCode();
        var finishedBody = await finished.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("finished", finishedBody.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.String, finishedBody.GetProperty("finishedUtc").ValueKind);
        Assert.Equal("Baseline", finishedBody.GetProperty("purpose").GetString());

        // Unrelated edits while finished keep the recorded timestamp.
        var recorded = finishedBody.GetProperty("finishedUtc").GetString();
        var edited = await fixture.Patch(route, new { owner = "Ansel" });
        var editedBody = await edited.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(recorded, editedBody.GetProperty("finishedUtc").GetString());

        var reopened = await fixture.Patch(route, new { status = "ongoing" });
        reopened.EnsureSuccessStatusCode();
        var reopenedBody = await reopened.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ongoing", reopenedBody.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, reopenedBody.GetProperty("finishedUtc").ValueKind);
    }

    [Fact]
    public async Task PatchWorktreeRejectsUnknownStatus()
    {
        await using var fixture = LandingFixture.Create(root);
        fixture.WriteWorktree("wt-1", "master", "master");

        var response = await fixture.Patch(
            $"/api/workbenches/{fixture.WorkbenchId}/worktrees/wt-1",
            new { status = "archived" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TaskCrudRoutesRoundTripWithCamelCaseEnums()
    {
        await using var fixture = LandingFixture.Create(root);
        fixture.WriteWorktree("wt-1", "master", "master");
        var tasksRoute = $"/api/workbenches/{fixture.WorkbenchId}/worktrees/wt-1/tasks";

        var created = await fixture.Client.PostAsJsonAsync(tasksRoute, new
        {
            title = "Adapt FB_Motor_Control",
            details = "Rework the interlock logic",
            elementRefs = new[] { "Device01/FB_Motor_Control" },
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdTask = await created.Content.ReadFromJsonAsync<JsonElement>();
        var taskId = createdTask.GetProperty("taskId").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(taskId));
        Assert.Equal("todo", createdTask.GetProperty("status").GetString());
        Assert.Equal("Device01/FB_Motor_Control",
            Assert.Single(createdTask.GetProperty("elementRefs").EnumerateArray()).GetString());
        Assert.Equal(JsonValueKind.Null, createdTask.GetProperty("doneUtc").ValueKind);

        var list = await fixture.Client.GetFromJsonAsync<JsonElement>(tasksRoute);
        Assert.Equal(1, list.GetProperty("version").GetInt32());
        Assert.Equal(taskId,
            Assert.Single(list.GetProperty("tasks").EnumerateArray()).GetProperty("taskId").GetString());

        var patched = await fixture.Patch($"{tasksRoute}/{taskId}", new { status = "done" });
        patched.EnsureSuccessStatusCode();
        var patchedTask = await patched.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("done", patchedTask.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.String, patchedTask.GetProperty("doneUtc").ValueKind);

        var deleted = await fixture.Client.DeleteAsync($"{tasksRoute}/{taskId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var afterDelete = await fixture.Client.GetFromJsonAsync<JsonElement>(tasksRoute);
        Assert.Empty(afterDelete.GetProperty("tasks").EnumerateArray());
    }

    [Fact]
    public async Task TaskPatchUpdatesOnlySuppliedFields()
    {
        await using var fixture = LandingFixture.Create(root);
        fixture.WriteWorktree("wt-1", "master", "master");
        var tasksRoute = $"/api/workbenches/{fixture.WorkbenchId}/worktrees/wt-1/tasks";
        var created = await fixture.Client.PostAsJsonAsync(tasksRoute, new
        {
            title = "Original",
            details = "Plan A",
        });
        var taskId = (await created.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("taskId").GetString()!;

        var patched = await fixture.Patch($"{tasksRoute}/{taskId}", new { title = "Renamed" });
        patched.EnsureSuccessStatusCode();
        var body = await patched.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Renamed", body.GetProperty("title").GetString());
        Assert.Equal("Plan A", body.GetProperty("details").GetString());
        Assert.Equal("todo", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task UnknownIdsMapToNotFound()
    {
        await using var fixture = LandingFixture.Create(root);
        fixture.WriteWorktree("wt-1", "master", "master");
        var wb = fixture.WorkbenchId;

        Assert.Equal(HttpStatusCode.NotFound,
            (await fixture.Client.GetAsync("/api/workbenches/missing/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await fixture.Client.GetAsync($"/api/workbenches/{wb}/worktrees/missing")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await fixture.Client.GetAsync($"/api/workbenches/{wb}/worktrees/missing/tasks")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await fixture.Client.PostAsJsonAsync(
                $"/api/workbenches/{wb}/worktrees/missing/tasks",
                new { title = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await fixture.Patch(
                $"/api/workbenches/{wb}/worktrees/wt-1/tasks/missing",
                new { title = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await fixture.Client.DeleteAsync(
                $"/api/workbenches/{wb}/worktrees/wt-1/tasks/missing")).StatusCode);
    }

    [Fact]
    public async Task VersionControlTimelineRejectsAnInvalidPageBeforeCallingVersionControl()
    {
        await using var fixture = LandingFixture.Create(root);
        fixture.WriteWorktree("wt-1", "master", "master");

        var response = await fixture.Client.GetAsync(
            $"/api/workbenches/{fixture.WorkbenchId}/worktrees/wt-1/vc/timeline?offset=0&limit=0");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("TIMELINE_PAGE_INVALID", body.GetProperty("error").GetString());
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class LandingFixture : IAsyncDisposable
    {
        private readonly WebApplicationFactory<Program> factory;
        private readonly WorkbenchCatalog catalog;

        private LandingFixture(
            WebApplicationFactory<Program> factory,
            HttpClient client,
            AtomicJsonStore store,
            WorkbenchCatalog catalog,
            WorkbenchMetadata workbench)
        {
            this.factory = factory;
            Client = client;
            Store = store;
            this.catalog = catalog;
            WorkbenchId = workbench.WorkbenchId;
            WorkbenchRootPath = workbench.RootPath;
        }

        public HttpClient Client { get; }
        public AtomicJsonStore Store { get; }
        public string WorkbenchId { get; }
        public string WorkbenchRootPath { get; }

        public static LandingFixture Create(string fixtureRoot)
        {
            var store = new AtomicJsonStore();
            var catalog = new WorkbenchCatalog(store, fixtureRoot);
            var workbench = catalog.Create("Line", null);
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
            return new LandingFixture(factory, factory.CreateClient(), store, catalog, workbench);
        }

        public string WorktreeRoot(string relativePath) =>
            Path.Combine(WorkbenchRootPath, "worktrees", relativePath);

        public void RegisterWorktree(string worktreeId, string name, string branch, string relativePath)
        {
            var workbench = catalog.Load(WorkbenchRootPath);
            catalog.RegisterWorktree(
                workbench,
                new WorkbenchWorktreeRegistration(worktreeId, name, branch, relativePath));
        }

        public void WriteWorktree(
            string worktreeId,
            string name,
            string branch,
            string? purpose = null,
            string? owner = null,
            WorktreeStatus status = WorktreeStatus.Ongoing,
            DateTimeOffset? finishedUtc = null,
            string? sourceProjectPath = null)
        {
            var relativePath = branch.Replace('/', '-');
            RegisterWorktree(worktreeId, name, branch, relativePath);
            var worktreeRoot = WorktreeRoot(relativePath);
            Directory.CreateDirectory(worktreeRoot);
            Store.Write(
                Path.Combine(worktreeRoot, "worktree.json"),
                new WorktreeMetadata(
                    WorkbenchSchema.CurrentVersion,
                    worktreeId,
                    WorkbenchId,
                    name,
                    branch,
                    DateTimeOffset.UtcNow.ToString("O"),
                    null,
                    null,
                    sourceProjectPath,
                    ["dev-1"],
                    null,
                    Purpose: purpose,
                    Owner: owner,
                    Status: status,
                    FinishedUtc: finishedUtc));
        }

        public Task<HttpResponseMessage> Patch(string route, object body) =>
            Client.SendAsync(new HttpRequestMessage(HttpMethod.Patch, route)
            {
                Content = JsonContent.Create(body),
            });

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await factory.DisposeAsync();
        }
    }
}
