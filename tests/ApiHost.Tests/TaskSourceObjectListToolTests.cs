using Agent.Chat;
using Agent.Mcp;
using Agent.Workbench;
using Agent.Workbench.EngineeringGraph;
using Contracts.Sandbox;
using System.Text.Json;
using Xunit;

/// <summary>
/// Item 008: the read half of the staging flow. A live conversation could not obtain a
/// <c>sourceObjectId</c> at all — the only tool that exposed one needed a live TIA session and returned
/// the whole project — so the model invented one and stalled. This listing reports the id staging
/// accepts, who already stages each object, and the active task, and it writes nothing.
/// </summary>
public sealed class TaskSourceObjectListToolTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"source-object-list-{Guid.NewGuid():N}");

    [Fact]
    public async Task ListsTheComparableObjectsWithTheIdStagingAccepts()
    {
        var fixture = ListToolFixture.Create(root);

        var result = await fixture.InvokeAsync("{}");

        var objects = result.GetProperty("sourceObjects").EnumerateArray().ToArray();
        Assert.Equal(
            new[] { "device-1:fb-door", "device-1:ob-main", "device-1:tag-motor", "device-1:udt-model" },
            objects.Select(item => item.GetProperty("sourceObjectId").GetString()).OrderBy(id => id, StringComparer.Ordinal));
        // Instance DBs are outside the managed-source evidence domain, so they are never offered as a
        // compare basis — and the response says how many were dropped instead of hiding them silently.
        Assert.DoesNotContain(objects, item => item.GetProperty("sourceId").GetString() == "db-instance");
        Assert.Equal(1, result.GetProperty("excludedNotComparableCount").GetInt32());
        Assert.Equal(4, result.GetProperty("totalCount").GetInt32());
        Assert.Equal(4, result.GetProperty("matchingCount").GetInt32());
        Assert.Equal(4, result.GetProperty("returned").GetInt32());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("nextOffset").ValueKind);

        var door = objects.Single(item => item.GetProperty("sourceId").GetString() == "fb-door");
        Assert.Equal("202_VocDoorGeneral", door.GetProperty("name").GetString());
        Assert.Equal("FB", door.GetProperty("category").GetString());
        Assert.Equal("15_VOC_Door", door.GetProperty("groupPath").GetString());
        Assert.Equal("Blocks/15_VOC_Door/202_VocDoorGeneral [FB170].xml", door.GetProperty("relativePath").GetString());
        Assert.Equal("standard-block", door.GetProperty("evidenceKind").GetString());
    }

    [Fact]
    public async Task QueryAndCategoryNarrowTheList()
    {
        var fixture = ListToolFixture.Create(root);

        var byQuery = await fixture.InvokeAsync("""{"query":"vocdoor"}""");
        var byQueryIds = byQuery.GetProperty("sourceObjects").EnumerateArray()
            .Select(item => item.GetProperty("sourceId").GetString()).ToArray();
        Assert.Equal(new[] { "fb-door" }, byQueryIds); // case-insensitive, and the instance DB is not a candidate
        Assert.Equal(1, byQuery.GetProperty("matchingCount").GetInt32());
        Assert.Equal(4, byQuery.GetProperty("totalCount").GetInt32());

        // The exported path is searchable too, so a model holding the source file it just read finds the id.
        var byPath = await fixture.InvokeAsync("""{"query":"Blocks/15_VOC_Door"}""");
        Assert.Equal("fb-door", Assert.Single(byPath.GetProperty("sourceObjects").EnumerateArray())
            .GetProperty("sourceId").GetString());

        var byCategory = await fixture.InvokeAsync("""{"category":"tags"}""");
        Assert.Equal("tag-motor", Assert.Single(byCategory.GetProperty("sourceObjects").EnumerateArray())
            .GetProperty("sourceId").GetString());

        var both = await fixture.InvokeAsync("""{"query":"Main","category":"Tags"}""");
        Assert.Empty(both.GetProperty("sourceObjects").EnumerateArray());
        Assert.Equal(0, both.GetProperty("matchingCount").GetInt32());
    }

    [Fact]
    public async Task PagesWithLimitAndOffset()
    {
        var fixture = ListToolFixture.Create(root);

        var first = await fixture.InvokeAsync("""{"limit":2}""");
        Assert.Equal(2, first.GetProperty("returned").GetInt32());
        Assert.Equal(2, first.GetProperty("nextOffset").GetInt32());
        Assert.Equal(4, first.GetProperty("matchingCount").GetInt32());

        var second = await fixture.InvokeAsync("""{"limit":2,"offset":2}""");
        Assert.Equal(2, second.GetProperty("returned").GetInt32());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextOffset").ValueKind);

        // The page is stable: the two pages together are the whole list, with no repeats.
        var ids = first.GetProperty("sourceObjects").EnumerateArray()
            .Concat(second.GetProperty("sourceObjects").EnumerateArray())
            .Select(item => item.GetProperty("sourceObjectId").GetString())
            .ToArray();
        Assert.Equal(4, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task ReportsTheActiveTaskThatStagingWouldAddTo()
    {
        var withoutTask = await ListToolFixture.Create(root).InvokeAsync("{}");
        Assert.Equal(JsonValueKind.Null, withoutTask.GetProperty("activeTask").ValueKind);

        var fixture = ListToolFixture.Create(root, active: true);
        var result = await fixture.InvokeAsync("{}");

        var active = result.GetProperty("activeTask");
        Assert.Equal(ListToolFixture.TaskId, active.GetProperty("taskId").GetString());
        Assert.Equal("Stage work", active.GetProperty("title").GetString());
    }

    [Fact]
    public async Task ReportsWhoAlreadyStagesAnObject()
    {
        var fixture = ListToolFixture.Create(root, active: true);
        fixture.StageForOtherTask("device-1:ob-main");

        var result = await fixture.InvokeAsync("""{"query":"Main"}""");

        var main = Assert.Single(result.GetProperty("sourceObjects").EnumerateArray());
        Assert.Equal("device-1:ob-main", main.GetProperty("sourceObjectId").GetString());
        Assert.Equal(ListToolFixture.OtherTaskId, main.GetProperty("stagedByTaskId").GetString());
        Assert.Equal("Other work", main.GetProperty("stagedByTaskTitle").GetString());

        var unstaged = await fixture.InvokeAsync("""{"query":"202_VocDoorGeneral","category":"FB"}""");
        var door = Assert.Single(unstaged.GetProperty("sourceObjects").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, door.GetProperty("stagedByTaskId").ValueKind);
    }

    /// <summary>The listing is the read half of a destructive flow: it must not register the entities a
    /// stage registers, or merely asking would move the graph.</summary>
    [Fact]
    public async Task ListingIsReadTierAndRegistersNothing()
    {
        var fixture = ListToolFixture.Create(root);

        await fixture.InvokeAsync("{}");

        Assert.Equal(SandboxTier.Read, new SandboxPolicy().Classify(TaskSourceObjectListTool.ToolName));
        Assert.Null(fixture.ReadSourceObject("device-1:ob-main"));
        Assert.Empty(fixture.ReadWorktreeStages());
    }

    [Fact]
    public async Task WithoutASelectedDeviceTheListingIsRefused()
    {
        var fixture = ListToolFixture.Create(root);

        var error = await Assert.ThrowsAsync<ToolCallException>(() => fixture.InvokeWithoutDeviceAsync("{}"));

        Assert.Equal("DEVICE_SELECTION_REQUIRED", error.Code);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>One device-bound worktree with two tasks and a manifest that covers all four comparable
    /// categories plus an instance DB, wired to the real listing tool.</summary>
    private sealed class ListToolFixture
    {
        public const string WorktreeId = "master-1";
        public const string DeviceId = "device-1";
        public const string TaskId = "task-stage";
        public const string OtherTaskId = "task-other";

        private const string ManifestJson = """
            {
              "schemaVersion": "1.0",
              "components": [
                {
                  "id": "ob-main",
                  "name": "Main",
                  "category": "OB",
                  "exportedFile": "Blocks/Main.xml",
                  "fingerprints": { "Code": "AAAA1111" }
                },
                {
                  "id": "fb-door",
                  "name": "202_VocDoorGeneral",
                  "category": "FB",
                  "siemensTypeName": "FB",
                  "exportedFile": "Blocks/15_VOC_Door/202_VocDoorGeneral [FB170].xml",
                  "fingerprints": { "Code": "BBBB2222" }
                },
                {
                  "id": "db-instance",
                  "name": "202_VocDoorGeneral_DB",
                  "category": "DB",
                  "siemensTypeName": "InstanceDB",
                  "exportedFile": "DB/202_VocDoorGeneral_DB.xml",
                  "fingerprints": { "Code": "CCCC3333" }
                },
                {
                  "id": "tag-motor",
                  "name": "Motor",
                  "category": "Tags",
                  "exportedFile": "Tags/Motor.xml",
                  "modifiedDate": "2026-08-02T13:10:02+08:00"
                },
                {
                  "id": "udt-model",
                  "name": "MotorModel",
                  "category": "UDT",
                  "exportedFile": "UDT/MotorModel.xml",
                  "fingerprints": { "Interface": "DDDD4444" }
                }
              ]
            }
            """;

        private readonly string workbenchRoot;
        private readonly string workbenchId;
        private readonly TaskSourceObjectListTool tool;

        private ListToolFixture(
            string workbenchRoot, string workbenchId,
            TaskSourceObjectListTool tool, AgentToolSpec spec)
        {
            this.workbenchRoot = workbenchRoot;
            this.workbenchId = workbenchId;
            this.tool = tool;
            Spec = spec;
        }

        public AgentToolSpec Spec { get; }

        public static ListToolFixture Create(string parent, bool active = false)
        {
            var fixtureRoot = Path.Combine(parent, Guid.NewGuid().ToString("N"));
            var store = new AtomicJsonStore();
            var catalog = new WorkbenchCatalog(store, fixtureRoot);
            var workbench = catalog.RegisterWorktree(
                catalog.Create("Line", null),
                new WorkbenchWorktreeRegistration(WorktreeId, "master", "master", "master"));
            var device = WorkbenchPaths.ResolveDevice(
                workbench.WorkbenchId, workbench.RootPath, WorktreeId, "master", DeviceId, "PLC_1");
            Directory.CreateDirectory(device.SourceRoot);
            store.Write(Path.Combine(device.WorktreeRoot, "worktree.json"), new WorktreeMetadata(
                "1.2", WorktreeId, workbench.WorkbenchId, "master", "master",
                DateTimeOffset.UtcNow.ToString("O"), "head-1", "project-1", null, new[] { DeviceId }, null));
            store.Write(Path.Combine(device.DeviceRoot, "device.json"), new DeviceMetadata(
                "1.2", DeviceId, WorktreeId, "PLC_1", "project-1", null, null, null,
                new KnowledgeState(false, new Dictionary<string, string>(), null), Array.Empty<DeviceImportRecord>()));
            File.WriteAllText(Path.Combine(device.SourceRoot, "metadata.json"), ManifestJson);

            using (var graphStore = new EngineeringGraphStore(workbench.RootPath))
            {
                var graph = new EngineeringGraphService(graphStore, workbench.WorkbenchId, id => id == WorktreeId);
                graph.CreateTask(TaskId, GraphTaskScopeKind.Worktree, WorktreeId, "Stage work",
                    GraphTaskType.Feature, intent: "intent", expectedResult: "result", deviceId: DeviceId);
                graph.CreateTask(OtherTaskId, GraphTaskScopeKind.Worktree, WorktreeId, "Other work",
                    GraphTaskType.Issue, intent: "intent", expectedResult: "result", deviceId: DeviceId);
            }

            var state = new WorkbenchApiState(catalog, store);
            state.Add(workbench);
            var activeTasks = new ActiveTaskContextService();
            if (active)
            {
                using var graphStore = new EngineeringGraphStore(workbench.RootPath);
                activeTasks.Select(
                    new EngineeringGraphService(graphStore, workbench.WorkbenchId, id => id == WorktreeId),
                    WorktreeId, TaskId);
            }

            var tool = new TaskSourceObjectListTool(state, new EngineeringGraphApiFactory(), activeTasks);
            return new ListToolFixture(
                workbench.RootPath, workbench.WorkbenchId, tool,
                TaskSourceObjectListTool.CreateSpec(tool, () => device));
        }

        public async Task<JsonElement> InvokeAsync(string argumentsJson)
        {
            using var arguments = JsonDocument.Parse(argumentsJson);
            return await Spec.Caller.CallAsync<JsonElement>(
                TaskSourceObjectListTool.ToolName, arguments.RootElement, CancellationToken.None);
        }

        /// <summary>The same call with no selected device, which is the chat's own precondition.</summary>
        public async Task<JsonElement> InvokeWithoutDeviceAsync(string argumentsJson)
        {
            var spec = TaskSourceObjectListTool.CreateSpec(tool, () => null);
            using var arguments = JsonDocument.Parse(argumentsJson);
            return await spec.Caller.CallAsync<JsonElement>(
                TaskSourceObjectListTool.ToolName, arguments.RootElement, CancellationToken.None);
        }

        public void StageForOtherTask(string sourceObjectId)
        {
            using var graphStore = new EngineeringGraphStore(workbenchRoot);
            var graph = new EngineeringGraphService(graphStore, workbenchId, id => id == WorktreeId);
            graph.RegisterEntity(new GraphEntity(GraphEntityKind.SourceObject, sourceObjectId,
                workbenchId, WorktreeId, DeviceId, "Blocks/Main.xml"));
            graph.StageSourceObject(OtherTaskId, sourceObjectId, "baseline-other");
        }

        public GraphEntity? ReadSourceObject(string sourceObjectId)
        {
            using var graphStore = new EngineeringGraphStore(workbenchRoot);
            return new EngineeringGraphService(graphStore, workbenchId, id => id == WorktreeId)
                .GetEntity(GraphEntityKind.SourceObject, sourceObjectId);
        }

        public IReadOnlyList<WorktreeSourceStage> ReadWorktreeStages()
        {
            using var graphStore = new EngineeringGraphStore(workbenchRoot);
            return new EngineeringGraphService(graphStore, workbenchId, id => id == WorktreeId)
                .ListWorktreeActiveStages(WorktreeId);
        }
    }
}
