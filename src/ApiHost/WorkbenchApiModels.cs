using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;
using Agent.Chat;
using Agent.Mcp;
using Agent.Workbench;
using Agent.Workbench.EngineeringGraph;
using Contracts.Sandbox;
using ApiHost.AppAssistant;

public sealed record WorkbenchSelection(string WorkbenchId, string? WorktreeId, string? DeviceId);
public sealed record CreateWorkbenchApiRequest(
    string Name,
    string? RootPath,
    int? EngineeringSessionId,
    string? EngineeringProjectPath);
public sealed record AttachTiaInstanceApiRequest(int SessionId);
public sealed record OpenTiaProjectApiRequest(
    bool WithUI = true,
    bool Upgrade = false,
    string? AuthenticationMode = null);
public sealed record OpenWorkbenchApiRequest(string RootPath);
public sealed record CreateWorktreeApiRequest(
    string Name, string Branch, string? StartPoint,
    SourceSavepointSelection? SourceSavepoint = null);
public sealed record RefreshApplyApiRequest(
    string PreviewId,
    string[]? ApprovedPaths,
    string[]? ApprovedRemovalPaths = null,
    string? CommitMessage = null);
public sealed record SourcePathApiRequest(string RelativePath);
public sealed record CommitSourceApiRequest(string[] Paths, string Message, bool UntrackableChange, bool SafetyChange = false, string? TaskId = null);
public sealed record RestoreTiaProjectApiRequest(string? GitCommit = null);
public sealed record NativeSavepointApiRequest(string Message);
public sealed record TiaSynchronizationAcceptApiRequest(string[] Paths, string Message);
public sealed record TiaValidationApiRequest(string ConfirmedBy);
public sealed record UnauthorizedMasterPathsRequest(string[] Paths, string? FeatureName = null, bool Confirm = false);
public sealed record FeaturePathsApiRequest(string[] Paths);
public sealed record ValidateFeatureMergeApiRequest(string ImportSessionId, bool MachineValidated, string ConfirmedBy);
public sealed record RollbackFeatureApiRequest(string HistoricalSha, string[] Paths, string FeatureName);
public sealed record CreateTagPathApiRequest(string? Path);
public sealed record RenameTagApiRequest(string? Name);
public sealed record WorkbenchTagSearchApiRequest(IReadOnlyList<string>? TagIds);
public sealed record TagTaxonomyApiResponse(IReadOnlyList<TagNode> Nodes);
public sealed record EntityTagsApiResponse(
    IReadOnlyList<string> Direct,
    IReadOnlyList<string> Inherited,
    IReadOnlyList<string> Effective);
public sealed record WorkbenchTagSearchResultApiResponse(
    TagEntityType EntityType,
    string EntityId,
    string? WorkbenchId,
    IReadOnlyList<string> Direct,
    IReadOnlyList<string> Effective,
    bool Available);
public sealed record WorkbenchTagSearchApiResponse(
    IReadOnlyList<WorkbenchTagSearchResultApiResponse> Workbenches,
    IReadOnlyList<WorkbenchTagSearchResultApiResponse> Worktrees);

public sealed record WorkbenchLandingResponse(IReadOnlyList<WorkbenchLandingCard> Projects);
public sealed record WorkbenchLandingCard(
    string WorkbenchId, string Name, string CreatedAt, string? UpdatedAt, string? ModifiedAt,
    string? Purpose, string? Owner, string? CoverAssetId,
    IReadOnlyList<string> EffectiveTagIds, IReadOnlyList<WorktreeLandingSummary> Worktrees);
public sealed record WorktreeLandingSummary(
    string WorktreeId, string Name, string Branch, string? CreatedAt, string? UpdatedAt,
    int? CompletedTasks, int? TotalTasks, int? DirtySourceFiles, int? SessionCount,
    string Availability);

/// <summary>Optional bootstrap body. CommitMessage customizes the first baseline commit title.</summary>
public sealed record BootstrapApiRequest(string? CommitMessage);
public sealed record HardwareOverwriteApiRequest(bool ConfirmOverwrite, string? Message = null);

/// <summary>Project landing page payload: workbench metadata plus a per-worktree summary
/// with task counts, aggregated server-side in one call.</summary>
public sealed record WorkbenchOverviewResponse(
    string WorkbenchId,
    string Name,
    string CreatedAt,
    string RootPath,
    string RepositoryPath,
    string? EngineeringProjectId,
    string? SourceProjectPath,
    string? Purpose,
    string? Owner,
    WorktreeOverviewEntry[] Worktrees);

public sealed record WorktreeOverviewEntry(
    string WorktreeId,
    string Name,
    string Branch,
    string RelativePath,
    string? CreatedAt,
    string? Purpose,
    string? Owner,
    WorktreeStatus Status,
    DateTimeOffset? FinishedUtc,
    int OpenTasks,
    int TotalTasks);

/// <summary>Worktree landing page header payload: the full worktree.json metadata.</summary>
public sealed record WorktreeDetailResponse(
    string WorktreeId,
    string WorkbenchId,
    string Name,
    string Branch,
    string CreatedAt,
    string? BaseCommit,
    string? EngineeringProjectId,
    string? SourceProjectPath,
    IReadOnlyList<string> DeviceIds,
    string? LastReconciliationCommit,
    string? Purpose,
    string? Owner,
    WorktreeStatus Status,
    DateTimeOffset? FinishedUtc);

public sealed record CreateWorktreeTaskApiRequest(
    string Title,
    string? Details,
    string[]? ElementRefs,
    string? DeviceId = null,
    [property: JsonConverter(typeof(JsonStringEnumConverter<GraphTaskTargetKind>))]
    GraphTaskTargetKind TargetKind = GraphTaskTargetKind.Device);

/// <summary>Device list entry: opaque object id plus the human-readable PLC name from device.json.</summary>
public sealed record DeviceSummary(string DeviceId, string PlcName);
public sealed record MergeWorktreeApiRequest(string TargetWorktreeId);
public sealed record SessionCreateApiRequest(Agent.Chat.ChatRequestSettings Settings, string? RuntimeContext, string? TaskId = null);
public sealed record SessionSaveApiRequest(ChatSessionData Session);
public sealed record SessionTaskApiRequest(string? TaskId);

public sealed class WorkbenchApiState
{
    private readonly WorkbenchCatalog catalog;
    private readonly AtomicJsonStore store;
    private readonly WorktreeTaskStore taskStore;
    private readonly TrustedWorkbenchRootRegistry? trustedRoots;
    private WorkbenchRuntimeStateCoordinator? runtimeStateCoordinator;
    private readonly ConcurrentDictionary<string, WorkbenchMetadata> workbenches = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ReconciliationPreview> previews = new(StringComparer.Ordinal);

    public WorkbenchApiState(WorkbenchCatalog catalog, AtomicJsonStore store)
        : this(catalog, store, null)
    {
    }

    public WorkbenchApiState(
        WorkbenchCatalog catalog,
        AtomicJsonStore store,
        TrustedWorkbenchRootRegistry? trustedRoots)
    {
        this.catalog = catalog;
        this.store = store;
        taskStore = new WorktreeTaskStore(store);
        this.trustedRoots = trustedRoots;
        foreach (var item in catalog.ListDefaultRoot()) Add(item);
    }

    public WorkbenchSelection? Selection { get; private set; }
    public void AttachRuntimeStateCoordinator(WorkbenchRuntimeStateCoordinator coordinator) =>
        runtimeStateCoordinator = coordinator;
    public IReadOnlyList<WorkbenchMetadata> List()
    {
        ReconcileCatalog();
        return workbenches.Values.OrderBy(x => x.Name).ToArray();
    }
    public WorkbenchMetadata Add(WorkbenchMetadata value)
    {
        var persisted = catalog.Load(value.RootPath);
        if (!string.Equals(persisted.WorkbenchId, value.WorkbenchId, StringComparison.Ordinal))
            throw new WorkbenchCatalogException(
                "WORKBENCH_RELATIONSHIP_MISMATCH",
                "Workbench metadata does not match the persisted catalog entry.");
        workbenches[persisted.WorkbenchId] = persisted;
        ReconcileTrustedRoots();
        return persisted;
    }
    public WorkbenchMetadata Refresh(string id)
    {
        var workbench = Add(catalog.Load(Workbench(id).RootPath));
        runtimeStateCoordinator?.Refresh(id, BuildRuntimeSummaries(workbench));
        return workbench;
    }

    /// <summary>Refreshes the runtime projection when its observed worktree facts changed.
    /// Re-reading unchanged data must not advance the runtime revision, otherwise an assistant
    /// bootstrap would create its own consequential-change refresh loop.</summary>
    public WorkbenchMetadata RefreshRuntimeIfChanged(string id)
    {
        var workbench = Add(catalog.Load(Workbench(id).RootPath));
        if (runtimeStateCoordinator is null) return workbench;

        var summaries = BuildRuntimeSummaries(workbench);
        var current = runtimeStateCoordinator.GetSnapshot(id);
        summaries = MergeRuntimeObservations(current.Worktrees, summaries);
        if (!string.Equals(
                JsonSerializer.Serialize(current.Worktrees),
                JsonSerializer.Serialize(summaries),
                StringComparison.Ordinal))
        {
            runtimeStateCoordinator.Refresh(id, summaries);
        }

        return workbench;
    }

    private static IReadOnlyList<WorktreeRuntimeSummary> MergeRuntimeObservations(
        IReadOnlyList<WorktreeRuntimeSummary> current,
        IReadOnlyList<WorktreeRuntimeSummary> refreshed) =>
        refreshed.Select(next =>
        {
            var previous = current.FirstOrDefault(item => item.WorktreeId == next.WorktreeId);
            if (previous is null) return next;
            return next with
            {
                GitStatus = next.GitStatus == "unknown" ? previous.GitStatus : next.GitStatus,
                Head = next.GitStatus == "unknown" && previous.Head is not null ? previous.Head : next.Head,
            };
        }).ToArray();
    public WorkbenchMetadata Open(string root) => Add(catalog.Load(root));

    private IReadOnlyList<WorktreeRuntimeSummary> BuildRuntimeSummaries(WorkbenchMetadata workbench) =>
        workbench.Worktrees.Select(registration =>
        {
            var worktreeRoot = WorkbenchPaths.ResolveWorktree(workbench.RootPath, registration.RelativePath);
            WorktreeMetadata? metadata = null;
            try
            {
                metadata = store.Read<WorktreeMetadata>(Path.Combine(worktreeRoot, "worktree.json"));
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                // The catalog is authoritative for membership; a missing worktree file is
                // represented as an unknown runtime observation until the next refresh.
            }

            var devices = metadata?.DeviceIds
                .Select(deviceId => ReadDeviceRuntimeSummary(worktreeRoot, deviceId))
                .ToArray()
                ?? Array.Empty<DeviceRuntimeSummary>();
            var todoCount = taskStore.Load(worktreeRoot).Tasks
                .Count(task => task.Status != WorktreeTaskStatus.Done);
            var revision = ReadEngineeringRevision(worktreeRoot);
            return new WorktreeRuntimeSummary(
                registration.WorktreeId,
                metadata?.Name ?? registration.Name,
                metadata?.Branch ?? registration.Branch,
                "unknown",
                metadata?.BaseCommit,
                todoCount,
                metadata?.BaseSvnRevision,
                revision?.Svn.Revision,
                revision?.Validation.CompileStatus
                    ?? metadata?.Status.ToString().ToLowerInvariant()
                    ?? "unknown",
                devices);
        }).ToArray();

    private static EngineeringRevisionState? ReadEngineeringRevision(string worktreeRoot)
    {
        var path = WorkbenchPaths.ResolveRevisionState(worktreeRoot);
        if (!File.Exists(path)) return null;
        try
        {
            return EngineeringStateWriter.Read(path);
        }
        catch (Exception exception) when (exception is IOException or JsonException or WorkbenchLifecycleException)
        {
            return null;
        }
    }

    private DeviceRuntimeSummary ReadDeviceRuntimeSummary(string worktreeRoot, string deviceId)
    {
        try
        {
            var metadata = store.Read<DeviceMetadata>(
                Path.Combine(WorkbenchPaths.ResolveRelative(worktreeRoot, "devices"), deviceId, "device.json"));
            return new(
                deviceId,
                metadata.PlcName,
                "unknown",
                metadata.Knowledge.Stale ? "stale" : "fresh");
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return new(deviceId, null, "unknown", "unknown");
        }
    }

    public void ReconcileCatalog()
    {
        foreach (var existing in workbenches.ToArray())
        {
            try
            {
                workbenches[existing.Key] = catalog.Load(existing.Value.RootPath);
            }
            catch (WorkbenchCatalogException exception) when (
                exception.Code is "WORKBENCH_NOT_FOUND" or "WORKBENCH_RELATIONSHIP_MISMATCH")
            {
                workbenches.TryRemove(existing.Key, out _);
            }
        }

        foreach (var discovered in catalog.ListDefaultRoot())
        {
            workbenches[discovered.WorkbenchId] = discovered;
        }

        ReconcileTrustedRoots();
    }

    private void ReconcileTrustedRoots() =>
        trustedRoots?.Reconcile(workbenches.Values.Select(workbench =>
            new TrustedWorkbenchRoot(workbench.WorkbenchId, workbench.RootPath)));
    public WorkbenchMetadata Workbench(string id) => workbenches.TryGetValue(id, out var value) ? value : throw new KeyNotFoundException("WORKBENCH_NOT_FOUND");
    public WorktreeMetadata Worktree(string workbenchId, string worktreeId)
    {
        var wb = Workbench(workbenchId);
        var registration = wb.Worktrees.SingleOrDefault(x => x.WorktreeId == worktreeId) ?? throw new KeyNotFoundException("WORKTREE_NOT_FOUND");
        return store.Read<WorktreeMetadata>(Path.Combine(WorkbenchPaths.ResolveWorktree(wb.RootPath, registration.RelativePath), "worktree.json"));
    }
    public string WorktreeRoot(string workbenchId, string worktreeId)
    {
        var wb = Workbench(workbenchId);
        var wt = Worktree(workbenchId, worktreeId);
        var registration = wb.Worktrees.Single(x => x.WorktreeId == wt.WorktreeId);
        return WorkbenchPaths.ResolveWorktree(wb.RootPath, registration.RelativePath);
    }
    public (DeviceContext Context, DeviceMetadata Metadata) Device(string deviceId)
    {
        var selection = Selection;
        if (selection?.WorkbenchId is null || selection.WorktreeId is null) throw new InvalidOperationException("WORKBENCH_SELECTION_REQUIRED");
        return Device(selection.WorkbenchId, selection.WorktreeId, deviceId);
    }
    public (DeviceContext Context, DeviceMetadata Metadata) Device(
        string workbenchId,
        string worktreeId,
        string deviceId)
    {
        var wb = Workbench(workbenchId);
        var wt = Worktree(workbenchId, worktreeId);
        if (!wt.DeviceIds.Contains(deviceId, StringComparer.Ordinal)) throw new KeyNotFoundException("DEVICE_NOT_FOUND");
        var reg = wb.Worktrees.Single(x => x.WorktreeId == wt.WorktreeId);
        var wtRoot = WorkbenchPaths.ResolveWorktree(wb.RootPath, reg.RelativePath);
        var devicesRoot = WorkbenchPaths.ResolveRelative(wtRoot, "devices");
        var candidates = new List<(string path, DeviceMetadata device)>();
        foreach (var directory in Directory.EnumerateDirectories(devicesRoot))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new WorkbenchPathException($"Device directory '{directory}' is a reparse point.");
            var path = Path.Combine(directory, "device.json");
            if (File.Exists(path)) candidates.Add((path, store.Read<DeviceMetadata>(path)));
        }
        var metadataPath = candidates.SingleOrDefault(x => x.device.DeviceId == deviceId);
        if (metadataPath.device is null) throw new KeyNotFoundException("DEVICE_NOT_FOUND");
        return (catalog.ResolveDevice(wb, wt, metadataPath.device), metadataPath.device);
    }
    public void Select(string wb, string? wt = null, string? device = null)
    {
        Selection = new(wb, wt, device);
        runtimeStateCoordinator?.SetFocus(wb, wt, device);
    }

    /// <summary>Registered devices of a worktree with their human-readable PLC names (from each
    /// device.json; falls back to the device folder name, then the raw id). The navigator displays
    /// these instead of the opaque device object ids.</summary>
    public IReadOnlyList<DeviceSummary> ListDevices(string workbenchId, string worktreeId)
    {
        var wb = Workbench(workbenchId);
        var wt = Worktree(workbenchId, worktreeId);
        var reg = wb.Worktrees.Single(x => x.WorktreeId == wt.WorktreeId);
        var wtRoot = WorkbenchPaths.ResolveWorktree(wb.RootPath, reg.RelativePath);
        var devicesRoot = WorkbenchPaths.ResolveRelative(wtRoot, "devices");
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Directory.Exists(devicesRoot))
        {
            foreach (var directory in Directory.EnumerateDirectories(devicesRoot))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new WorkbenchPathException($"Device directory '{directory}' is a reparse point.");
                var path = Path.Combine(directory, "device.json");
                if (!File.Exists(path)) continue;
                var metadata = store.Read<DeviceMetadata>(path);
                names[metadata.DeviceId] = string.IsNullOrWhiteSpace(metadata.PlcName)
                    ? Path.GetFileName(directory)
                    : metadata.PlcName;
            }
        }

        return wt.DeviceIds
            .Select(id => new DeviceSummary(id, names.TryGetValue(id, out var name) ? name : id))
            .ToArray();
    }
    /// <summary>Drops a deleted workbench from memory and clears a selection that referenced it.</summary>
    public void Remove(string id)
    {
        workbenches.TryRemove(id, out _);
        if (Selection?.WorkbenchId == id)
        {
            Selection = null;
        }

        ReconcileTrustedRoots();
    }
    public void Remember(ReconciliationPreview preview) => previews[preview.PreviewId] = preview;
    public ReconciliationPreview Take(string id, string deviceId, string? worktreeId = null)
    {
        if (!previews.TryGetValue(id, out var preview) || preview.DeviceId != deviceId
            || (worktreeId is not null && preview.WorktreeId != worktreeId)
            || !previews.TryRemove(new KeyValuePair<string, ReconciliationPreview>(id, preview)))
            throw new KeyNotFoundException("RECONCILIATION_PREVIEW_UNKNOWN");
        return preview;
    }
}

public static class WorkbenchEndpoints
{
    public static IEndpointRouteBuilder MapWorkbenchEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/operations/{id}", (string id, OperationStatusRegistry operations) =>
            operations.TryGet(id, out var snapshot) ? Results.Ok(snapshot) : Results.NotFound());
        app.MapDelete("/api/operations/{id}", (string id, OperationStatusRegistry operations) =>
        {
            operations.Dismiss(id);
            return Results.NoContent();
        });
        app.MapGet("/api/tags", (WorkbenchTagService tags) =>
            new TagTaxonomyApiResponse(tags.GetTaxonomy()));
        app.MapPost("/api/tags/path", (CreateTagPathApiRequest request, WorkbenchTagService tags) =>
        {
            var node = tags.CreatePath(request.Path!);
            return Results.Created($"/api/tags/{node.TagId}", node);
        });
        app.MapPatch("/api/tags/{tagId}", (string tagId, RenameTagApiRequest request, WorkbenchTagService tags) =>
            Results.Ok(tags.Rename(tagId, request.Name!)));
        app.MapDelete("/api/tags/{tagId}", (string tagId, WorkbenchTagService tags) =>
        {
            tags.Delete(tagId);
            return Results.NoContent();
        });
        app.MapGet("/api/workbenches/{id}/tags", (string id, WorkbenchTagService tags) =>
            Results.Ok(ToEntityTagsResponse(tags.GetWorkbenchTags(id))));
        app.MapPost("/api/workbenches/{id}/tags/{tagId}", (string id, string tagId, WorkbenchTagService tags) =>
        {
            tags.AssignWorkbenchTag(tagId, id);
            return Results.NoContent();
        });
        app.MapDelete("/api/workbenches/{id}/tags/{tagId}", (string id, string tagId, WorkbenchTagService tags) =>
        {
            tags.UnassignWorkbenchTag(tagId, id);
            return Results.NoContent();
        });
        app.MapGet("/api/workbenches/{id}/worktrees/{wt}/tags", (string id, string wt, WorkbenchTagService tags) =>
            Results.Ok(ToEntityTagsResponse(tags.GetWorktreeTags(id, wt))));
        app.MapPost("/api/workbenches/{id}/worktrees/{wt}/tags/{tagId}", (string id, string wt, string tagId, WorkbenchTagService tags) =>
        {
            tags.AssignWorktreeTag(tagId, id, wt);
            return Results.NoContent();
        });
        app.MapDelete("/api/workbenches/{id}/worktrees/{wt}/tags/{tagId}", (string id, string wt, string tagId, WorkbenchTagService tags) =>
        {
            tags.UnassignWorktreeTag(tagId, id, wt);
            return Results.NoContent();
        });
        app.MapPost("/api/workbenches/search", (WorkbenchTagSearchApiRequest request, WorkbenchTagService tags) =>
        {
            if (request.TagIds is null)
            {
                throw new ArgumentException("Field 'tagIds' is required.");
            }

            var results = tags.Search(request.TagIds);
            return Results.Ok(new WorkbenchTagSearchApiResponse(
                results.Workbenches.Select(ToSearchResultResponse).ToArray(),
                results.Worktrees.Select(ToSearchResultResponse).ToArray()));
        });
        app.MapGet("/api/workbenches", (WorkbenchApiState s, WorkbenchCoordinator coordinator) =>
        {
            var workbenches = s.List();
            foreach (var workbench in workbenches)
                coordinator.RegisterWorkbench(workbench);
            return workbenches;
        });
        app.MapGet("/api/workbenches/landing", async (WorkbenchApiState s, WorkbenchTagService tags, AtomicJsonStore store, WorktreeTaskStore tasks, ApiMcpGateway gateway, CancellationToken ct) =>
        {
            var cards = new List<WorkbenchLandingCard>();
            foreach (var workbench in s.List())
                cards.Add(await BuildLandingCard(workbench, tags, store, tasks, gateway, ct));
            return Results.Ok(new WorkbenchLandingResponse(cards));
        });
        app.MapGet("/api/sandbox/roots", (SandboxConfig sandbox) =>
            new { roots = sandbox.PathJail.Roots });
        app.MapPost("/api/workbenches/open", (OpenWorkbenchApiRequest r, WorkbenchApiState s, WorkbenchCoordinator coordinator) =>
        {
            var workbench = s.Open(r.RootPath);
            coordinator.RegisterWorkbench(workbench);
            return workbench;
        });
        app.MapPost("/api/workbenches", async (
            CreateWorkbenchApiRequest r,
            WorkbenchCoordinator c,
            WorkbenchApiState s,
            OperationStatusRegistry operations,
            HttpContext http,
            CancellationToken ct) =>
            await RunOperationAsync(
                http,
                operations,
                "create-workbench",
                "Preparing workbench storage...",
                async progress => s.Add((await c.CreateWorkbenchAsync(
                    new(r.Name, r.RootPath, r.EngineeringSessionId, r.EngineeringProjectPath),
                    ct,
                    progress)).Workbench),
                "Workbench created.").ConfigureAwait(false));
        app.MapGet("/api/workbenches/{id}", (string id, WorkbenchApiState s) => s.Workbench(id));
        app.MapPost("/api/workbenches/{id}/root-folder", (string id, WorkbenchApiState s) =>
        {
            var rootPath = s.Workbench(id).RootPath;
            if (!Directory.Exists(rootPath))
                throw new DirectoryNotFoundException($"Workbench root does not exist: {rootPath}");
            Process.Start(new ProcessStartInfo(rootPath) { UseShellExecute = true });
            return Results.NoContent();
        });
        app.MapDelete("/api/workbenches/{id}", async (
            string id,
            WorkbenchApiState s,
            WorkbenchCoordinator c,
            WorkbenchTagService tags,
            OperationStatusRegistry operations,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await RunOperationAsync(
                http,
                operations,
                "delete-workbench",
                "Deleting workbench...",
                async progress =>
                {
                    await c.DeleteWorkbenchAsync(s.Workbench(id), ct, progress).ConfigureAwait(false);
                    tags.RemoveWorkbenchAssignments(id);
                    return new { deleted = true };
                },
                "Workbench deleted.").ConfigureAwait(false);
            s.Remove(id);
            return result;
        });
        app.MapPost("/api/workbenches/{id}/cover", async (string id, HttpRequest request, WorkbenchApiState s, WorkbenchCatalog catalog) =>
        {
            var workbench = s.Workbench(id);
            var form = await request.ReadFormAsync();
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file is null || file.Length <= 0 || file.Length > 5 * 1024 * 1024)
                throw new ArgumentException("Cover image is missing or exceeds the 5 MB limit.");
            var header = new byte[16];
            await using var input = file.OpenReadStream();
            var read = await input.ReadAsync(header);
            var (extension, valid) = DetectCover(header.AsSpan(0, read));
            if (!valid) throw new ArgumentException("Cover image must be a supported PNG, JPEG, GIF, or WebP image.");
            var assetId = Guid.NewGuid().ToString("N") + extension;
            var directory = catalog.ManagedCoverDirectory(id);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, assetId);
            try
            {
                await using (var output = File.Create(path))
                {
                    input.Position = 0;
                    await input.CopyToAsync(output);
                }
                if (!File.Exists(path) || new FileInfo(path).Length != file.Length)
                    throw new IOException("Cover image could not be stored completely.");
                var previous = workbench.CoverAssetId;
                WorkbenchMetadata updated;
                try
                {
                    updated = catalog.UpdateWorkbenchCover(workbench, assetId);
                    s.Add(updated);
                }
                catch
                {
                    try { catalog.UpdateWorkbenchCover(workbench, previous); } catch { /* preserve original failure */ }
                    throw;
                }
                if (!string.IsNullOrWhiteSpace(previous))
                {
                    var old = Path.Combine(directory, Path.GetFileName(previous));
                    if (!string.Equals(old, path, StringComparison.OrdinalIgnoreCase) && File.Exists(old)) File.Delete(old);
                }
                return Results.Ok(new { coverAssetId = updated.CoverAssetId });
            }
            catch
            {
                if (File.Exists(path)) File.Delete(path);
                throw;
            }
        });
        app.MapGet("/api/workbenches/{id}/cover", (string id, WorkbenchApiState s, WorkbenchCatalog catalog) =>
        {
            var workbench = s.Workbench(id);
            if (string.IsNullOrWhiteSpace(workbench.CoverAssetId)) return Results.NoContent();
            var name = Path.GetFileName(workbench.CoverAssetId);
            var path = Path.Combine(catalog.ManagedCoverDirectory(id), name);
            if (!File.Exists(path)) return Results.NoContent();
            return Results.File(path, DetectCoverContentType(Path.GetExtension(path)));
        });
        app.MapPost("/api/workbenches/{id}/select", (string id, WorkbenchApiState s, WorkbenchCoordinator coordinator) =>
        {
            var workbench = s.Workbench(id);
            coordinator.RegisterWorkbench(workbench);
            s.Select(id);
            return Results.NoContent();
        });
        app.MapGet("/api/workbenches/{id}/worktrees", (string id, WorkbenchApiState s) => s.Workbench(id).Worktrees);
        app.MapGet("/api/workbenches/{id}/overview", (
            string id,
            WorkbenchApiState s,
            AtomicJsonStore store,
            WorktreeTaskStore tasks) =>
        {
            var workbench = s.Workbench(id);
            var entries = workbench.Worktrees.Select(registration =>
            {
                var worktreeRoot = WorkbenchPaths.ResolveWorktree(
                    workbench.RootPath,
                    registration.RelativePath);
                WorktreeMetadata? metadata = null;
                try
                {
                    metadata = store.Read<WorktreeMetadata>(
                        Path.Combine(worktreeRoot, "worktree.json"));
                }
                catch (Exception exception) when (exception is IOException
                    or UnauthorizedAccessException
                    or JsonException
                    or MetadataSchemaException)
                {
                    // A missing/corrupt worktree.json must not sink the whole project
                    // overview; the entry still appears with registration data and defaults.
                }

                var taskList = tasks.Load(worktreeRoot);
                return new WorktreeOverviewEntry(
                    registration.WorktreeId,
                    metadata?.Name ?? registration.Name,
                    metadata?.Branch ?? registration.Branch,
                    registration.RelativePath,
                    metadata?.CreatedAt,
                    metadata?.Purpose,
                    metadata?.Owner,
                    metadata?.Status ?? WorktreeStatus.Ongoing,
                    metadata?.FinishedUtc,
                    taskList.Tasks.Count(task => task.Status != WorktreeTaskStatus.Done),
                    taskList.Tasks.Count);
            }).ToArray();

            return new WorkbenchOverviewResponse(
                workbench.WorkbenchId,
                workbench.Name,
                workbench.CreatedAt,
                workbench.RootPath,
                workbench.RepositoryPath,
                workbench.EngineeringProjectId,
                workbench.SourceProjectPath,
                workbench.Purpose,
                workbench.Owner,
                entries);
        });
        app.MapGet("/api/workbenches/{id}/tasks", (string id, WorkbenchApiState state, EngineeringGraphApiFactory graphs) =>
        {
            using var scope = graphs.Open(state.Workbench(id));
            return Results.Ok(scope.Service.ListTasks()
                .Where(task => task.ScopeKind == GraphTaskScopeKind.Project)
                .Select(ToEngineeringTaskResponse));
        });
        app.MapPost("/api/workbenches/{id}/tasks", (
            string id, EngineeringTaskApiRequest request, WorkbenchApiState state, EngineeringGraphApiFactory graphs) =>
        {
            using var scope = graphs.Open(state.Workbench(id));
            var task = scope.Service.CreateTask(
                Guid.NewGuid().ToString("N"), GraphTaskScopeKind.Project, null, request.Title,
                request.Type, request.Status, request.Description, request.Priority,
                request.Intent, request.ExpectedResult);
            return Results.Created($"/api/workbenches/{id}/tasks/{task.TaskId}", ToEngineeringTaskResponse(task));
        });
        app.MapGet("/api/workbenches/{id}/tasks/{taskId}", (
            string id, string taskId, WorkbenchApiState state, EngineeringGraphApiFactory graphs) =>
        {
            using var scope = graphs.Open(state.Workbench(id));
            var task = scope.Service.FindTask(taskId);
            if (task is null || task.ScopeKind != GraphTaskScopeKind.Project)
                throw new KeyNotFoundException("TASK_NOT_FOUND");
            return ToEngineeringTaskDetailResult(scope.Service, taskId);
        });
        app.MapPost("/api/workbenches/{id}/tasks/{taskId}/relationships", (
            string id, string taskId, EngineeringTaskRelationshipApiRequest request,
            WorkbenchApiState state, EngineeringGraphApiFactory graphs) =>
        {
            using var scope = graphs.Open(state.Workbench(id));
            var kind = ParseGraphEntityKind(request.TargetKind);
            var edge = scope.Service.AddEdge(GraphEntityKind.Task, taskId, kind, request.TargetId,
                GraphProvenance.Manual, request.IsPrimary);
            return Results.Created($"/api/workbenches/{id}/tasks/{taskId}/relationships/{edge.EdgeId}", ToRelationshipMutation(edge));
        });
        app.MapPut("/api/workbenches/{id}/tasks/{taskId}/relationships/{targetKind}/{targetId}", (
            string id, string taskId, string targetKind, string targetId, EngineeringTaskRelationshipApiRequest? request,
            WorkbenchApiState state, EngineeringGraphApiFactory graphs) =>
        {
            using var scope = graphs.Open(state.Workbench(id));
            var targetGraphKind = ParseGraphEntityKind(targetKind);
            var replacementTaskId = request?.NewTaskId;
            if (!string.IsNullOrWhiteSpace(replacementTaskId) && !string.IsNullOrWhiteSpace(request?.CurrentEdgeId))
            {
                var oldEdge = scope.Service.GetEdges(GraphEntityKind.Task, taskId, targetGraphKind)
                    .SingleOrDefault(edge => edge.EdgeId == request.CurrentEdgeId && edge.ToId == targetId)
                    ?? throw new KeyNotFoundException("RELATIONSHIP_NOT_FOUND");
                var replacement = scope.Service.ReassignTaskRelationship(taskId, replacementTaskId, targetGraphKind, targetId, oldEdge.EdgeId, GraphProvenance.Manual, request.IsPrimary);
                return Results.Ok(ToRelationshipMutation(replacement));
            }
            var edge = scope.Service.ReplaceTaskRelationship(taskId, targetGraphKind, targetId,
                GraphProvenance.Manual, request?.IsPrimary ?? false);
            return Results.Ok(ToRelationshipMutation(edge!));
        });
        app.MapDelete("/api/workbenches/{id}/tasks/{taskId}/relationships/{edgeId}", (
            string id, string taskId, string edgeId, WorkbenchApiState state, EngineeringGraphApiFactory graphs) =>
        {
            using var scope = graphs.Open(state.Workbench(id));
            var edge = scope.Service.GetEdges(GraphEntityKind.Task, taskId).SingleOrDefault(item => item.EdgeId == edgeId)
                ?? throw new KeyNotFoundException("RELATIONSHIP_NOT_FOUND");
            scope.Service.RemoveEdge(edge.EdgeId);
            return Results.NoContent();
        });
        app.MapGet("/api/workbenches/{id}/engineering-graph/{entityKind}/{entityId}", (
            string id, string entityKind, string entityId, WorkbenchApiState state, EngineeringGraphApiFactory graphs) =>
        {
            using var scope = graphs.Open(state.Workbench(id));
            var kind = ParseGraphEntityKind(entityKind);
            // An exact entity id wins (AC-005); otherwise the id is resolved to the one source-object
            // identity the graph stores, so the bare manifest id the source panel sends and the
            // reader's own `source:{relativePath}` form reach the object the graph holds links for.
            var entity = scope.Service.GetEntity(kind, entityId)
                ?? ResolveGraphEntityAnchor(id, kind, entityId, state, scope.Service)
                ?? throw new KeyNotFoundException("GRAPH_ENTITY_NOT_FOUND");
            var resolvedId = entity.EntityId;
            var directTasks = scope.Service.GetIncomingEdges(kind, resolvedId)
                .Where(edge => edge.FromKind == GraphEntityKind.Task);
            var evidenceCommits = scope.Service.GetIncomingEdges(kind, resolvedId)
                .Where(edge => edge.FromKind == GraphEntityKind.GitCommit)
                .ToArray();
            var traversedTasks = evidenceCommits.SelectMany(commit => scope.Service.GetIncomingEdges(GraphEntityKind.GitCommit, commit.FromId)
                .Where(edge => edge.FromKind == GraphEntityKind.Task)
                .Select(edge => (edge.FromId, edge.Provenance, edge.IsPrimary)));
            var tasks = directTasks.Select(edge => (edge.FromId, edge.Provenance, edge.IsPrimary))
                .Concat(traversedTasks).GroupBy(item => item.FromId, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new EngineeringTaskRelationshipApiResponse(group.Key, "",
                    JsonNamingPolicy.CamelCase.ConvertName(group.First().Provenance.ToString()), group.Any(item => item.IsPrimary))).ToArray();
            var commits = evidenceCommits.OrderBy(edge => edge.FromId, StringComparer.Ordinal)
                .Select(edge => new EngineeringTaskRelationshipApiResponse(edge.FromId, edge.EdgeId,
                    JsonNamingPolicy.CamelCase.ConvertName(edge.Provenance.ToString()), edge.IsPrimary)).ToArray();
            // A commit's own evidence is the direction the response above cannot express: the source
            // objects it touched and the changed files no source object could be resolved for. Both
            // are filled for a commit entity only, so `tasks`/`commits` keep their exact meaning.
            var sourceObjectEdges = kind == GraphEntityKind.GitCommit
                ? scope.Service.GetEdges(GraphEntityKind.GitCommit, resolvedId, GraphEntityKind.SourceObject)
                    .OrderBy(edge => edge.ToId, StringComparer.Ordinal)
                    .Select(edge => new EngineeringTaskRelationshipApiResponse(edge.ToId, edge.EdgeId,
                        JsonNamingPolicy.CamelCase.ConvertName(edge.Provenance.ToString()), edge.IsPrimary)).ToArray()
                : Array.Empty<EngineeringTaskRelationshipApiResponse>();
            var unresolvedFiles = kind == GraphEntityKind.GitCommit
                ? scope.Service.GetFileEvidence(resolvedId).Select(item => item.RelativePath).ToArray()
                : Array.Empty<string>();
            return Results.Ok(new EngineeringGraphEntityDetailApiResponse(
                JsonNamingPolicy.CamelCase.ConvertName(kind.ToString()), entity.EntityId,
                entity.WorkbenchId, entity.WorktreeId, tasks, commits, sourceObjectEdges, unresolvedFiles));
        });

        app.MapPatch("/api/workbenches/{id}/tasks/{taskId}", (
            string id, string taskId, JsonElement body, WorkbenchApiState state, EngineeringGraphApiFactory graphs) =>
        {
            using var scope = graphs.Open(state.Workbench(id));
            var current = scope.Service.FindTask(taskId);
            if (current is null || current.ScopeKind != GraphTaskScopeKind.Project)
                throw new KeyNotFoundException("TASK_NOT_FOUND");
            var updated = scope.Service.UpdateTask(taskId, current => current with
            {
                Title = TryGetOptionalString(body, "title", out var title) ? title! : current.Title,
                Description = TryGetOptionalString(body, "description", out var description) ? description : current.Description,
                Type = TryGetOptionalEnum<GraphTaskType>(body, "type", out var type) ? type : current.Type,
                Status = TryGetOptionalEnum<GraphTaskStatus>(body, "status", out var status) ? status : current.Status,
                Priority = TryGetOptionalInt(body, "priority", out var priority) ? priority : current.Priority,
                Intent = TryGetOptionalString(body, "intent", out var intent) ? intent! : current.Intent,
                ExpectedResult = TryGetOptionalString(body, "expectedResult", out var expected) ? expected! : current.ExpectedResult,
            });
            return updated is null ? throw new KeyNotFoundException("TASK_NOT_FOUND") : Results.Ok(ToEngineeringTaskResponse(updated));
        });
        app.MapDelete("/api/workbenches/{id}/tasks/{taskId}", (
            string id, string taskId, WorkbenchApiState state, EngineeringGraphApiFactory graphs) =>
        {
            using var scope = graphs.Open(state.Workbench(id));
            var current = scope.Service.FindTask(taskId);
            if (current is null || current.ScopeKind != GraphTaskScopeKind.Project)
                throw new KeyNotFoundException("TASK_NOT_FOUND");
            return scope.Service.DeleteTask(taskId) ? Results.NoContent() : throw new KeyNotFoundException("TASK_NOT_FOUND");
        });
        app.MapGet("/api/workbenches/{id}/active-task", (
            string id, WorkbenchApiState state, EngineeringGraphApiFactory graphs, ActiveTaskContextService activeTasks) =>
        {
            using var scope = graphs.Open(state.Workbench(id));
            return Results.Ok(new { activeTask = activeTasks.Get(scope.Service, null) is { } task ? ToEngineeringTaskResponse(task) : null });
        });
        app.MapMethods("/api/workbenches/{id}/active-task", new[] { "PUT", "POST" }, (
            string id, ActiveTaskApiRequest request, WorkbenchApiState state, EngineeringGraphApiFactory graphs,
            ActiveTaskContextService activeTasks) =>
        {
            using var scope = graphs.Open(state.Workbench(id));
            var task = activeTasks.Select(scope.Service, null, request.TaskId);
            return Results.Ok(new { activeTask = task is null ? null : ToEngineeringTaskResponse(task) });
        });
        app.MapPatch("/api/workbenches/{id}", (
            string id,
            JsonElement body,
            WorkbenchApiState s,
            WorkbenchCatalog catalog) =>
        {
            var workbench = s.Workbench(id);
            catalog.UpdateWorkbenchInfo(
                workbench,
                TryGetOptionalString(body, "purpose", out var purpose) ? purpose : workbench.Purpose,
                TryGetOptionalString(body, "owner", out var owner) ? owner : workbench.Owner);
            return s.Refresh(id);
        });
        app.MapGet("/api/workbenches/{id}/worktrees/{wt}", (string id, string wt, WorkbenchApiState s) =>
            ToDetail(s.Worktree(id, wt)));
        app.MapPatch("/api/workbenches/{id}/worktrees/{wt}", (
            string id,
            string wt,
            JsonElement body,
            WorkbenchApiState s,
            WorkbenchCatalog catalog) =>
        {
            var workbench = s.Workbench(id);
            var worktree = s.Worktree(id, wt);
            var status = TryGetOptionalEnum<WorktreeStatus>(body, "status", out var parsed)
                ? parsed
                : worktree.Status;
            var updated = worktree with
            {
                Purpose = TryGetOptionalString(body, "purpose", out var purpose) ? purpose : worktree.Purpose,
                Owner = TryGetOptionalString(body, "owner", out var owner) ? owner : worktree.Owner,
                Status = status,
            };
            // The server owns FinishedUtc: set on the transition to finished, cleared on
            // the transition back to ongoing; untouched when the status does not change.
            if (status == WorktreeStatus.Finished && worktree.Status != WorktreeStatus.Finished)
            {
                updated = updated with { FinishedUtc = DateTimeOffset.UtcNow };
            }
            else if (status == WorktreeStatus.Ongoing && worktree.Status == WorktreeStatus.Finished)
            {
                updated = updated with { FinishedUtc = null };
            }

            catalog.UpdateWorktreeInfo(workbench, updated);
            return Results.Ok(ToDetail(updated));
        });
        app.MapGet("/api/workbenches/{id}/worktrees/{wt}/tasks", (
            string id,
            string wt,
            WorkbenchApiState s,
            WorktreeTaskStore tasks) =>
            tasks.Load(s.WorktreeRoot(id, wt)));
        app.MapGet("/api/workbenches/{id}/worktrees/{wt}/engineering-tasks", (
            string id, string wt, WorkbenchApiState state, WorktreeTaskStore tasks, EngineeringGraphApiFactory graphs) =>
        {
            var workbench = state.Workbench(id);
            state.Worktree(id, wt);
            // Preserve the legacy import boundary, then expose graph task contracts.
            tasks.Load(state.WorktreeRoot(id, wt));
            using var scope = graphs.Open(workbench);
            return Results.Ok(scope.Service.ListTasks(wt)
                .Where(task => task.ScopeKind == Agent.Workbench.EngineeringGraph.GraphTaskScopeKind.Worktree)
                .Select(ToEngineeringTaskResponse));
        });
        app.MapPost("/api/workbenches/{id}/worktrees/{wt}/engineering-tasks", (
            string id, string wt, EngineeringTaskApiRequest request, WorkbenchApiState state, WorktreeTaskStore tasks, EngineeringGraphApiFactory graphs) =>
        {
            var workbench = state.Workbench(id);
            var worktree = state.Worktree(id, wt);
            // A hardware task is the only target that resolves without a registered device (ADR-0007, AC-010);
            // every other worktree task still has to name one and keeps the TASK_DEVICE_REQUIRED rejection (AC-011).
            if (request.TargetKind == GraphTaskTargetKind.Device &&
                (string.IsNullOrWhiteSpace(request.DeviceId) || !worktree.DeviceIds.Contains(request.DeviceId, StringComparer.Ordinal)))
                throw new EngineeringGraphConstraintException("A worktree task must select a registered device.", "TASK_DEVICE_REQUIRED");
            tasks.Load(state.WorktreeRoot(id, wt));
            using var scope = graphs.Open(workbench);
            var task = scope.Service.CreateTask(Guid.NewGuid().ToString("N"), GraphTaskScopeKind.Worktree, wt, request.Title,
                request.Type, request.Status, request.Description, request.Priority, request.Intent, request.ExpectedResult, request.DeviceId, request.TargetKind);
            return Results.Created($"/api/workbenches/{id}/worktrees/{wt}/tasks/{task.TaskId}", ToEngineeringTaskResponse(task));
        });
        app.MapPatch("/api/workbenches/{id}/worktrees/{wt}/engineering-tasks/{taskId}", (
            string id, string wt, string taskId, EngineeringTaskUpdateApiRequest request,
            WorkbenchApiState state, EngineeringGraphApiFactory graphs) =>
        {
            using var scope = graphs.Open(state.Workbench(id));
            var updated = scope.Service.UpdateTask(taskId, task =>
            {
                if (task.WorktreeId != wt) throw new KeyNotFoundException("TASK_NOT_FOUND");
                return task with
                {
                    Title = request.Title?.Trim() is { Length: > 0 } title ? title : task.Title,
                    Type = request.Type ?? task.Type,
                    Status = request.Status ?? task.Status,
                    Priority = request.Priority ?? task.Priority,
                    Intent = request.Intent?.Trim() is { Length: > 0 } intent ? intent : task.Intent,
                    ExpectedResult = request.ExpectedResult?.Trim() is { Length: > 0 } expected ? expected : task.ExpectedResult,
                    Description = request.Description ?? task.Description,
                };
            }) ?? throw new KeyNotFoundException("TASK_NOT_FOUND");
            return Results.Ok(ToEngineeringTaskResponse(updated));
        });
        app.MapGet("/api/workbenches/{id}/worktrees/{wt}/tasks/{taskId}/stages", (
            string id, string wt, string taskId, WorkbenchApiState state, EngineeringGraphApiFactory graphs) =>
        {
            using var scope = graphs.Open(state.Workbench(id));
            var task = scope.Service.FindTask(taskId);
            if (task is null || task.WorktreeId != wt) throw new KeyNotFoundException("TASK_NOT_FOUND");
            return Results.Ok(scope.Service.ListActiveStages(taskId).Select(stage => new TaskSourceStageApiResponse(stage.TaskId, stage.SourceObjectId, stage.DeviceId, stage.BaselineEvidenceJson, stage.StagedUtc)));
        });
        app.MapPost("/api/workbenches/{id}/worktrees/{wt}/tasks/{taskId}/compare-tia", async (
            string id, string wt, string taskId, WorkbenchApiState state, WorkbenchCoordinator coordinator,
            OperationStatusRegistry operations, HttpContext http, CancellationToken ct) =>
        {
            coordinator.RegisterWorkbench(state.Workbench(id));
            return Results.Ok(await RunOperationAsync(http, operations, "compare-task-tia",
                "Comparing staged task sources with TIA...",
                progress => coordinator.CompareTaskWithTiaAsync(id, wt, taskId, ct, progress),
                "Task source comparison completed.").ConfigureAwait(false));
        });
        app.MapGet("/api/workbenches/{id}/worktrees/{wt}/source-stages", (
            string id, string wt, WorkbenchApiState state, EngineeringGraphApiFactory graphs) =>
        {
            using var scope = graphs.Open(state.Workbench(id));
            return Results.Ok(scope.Service.ListWorktreeActiveStages(wt)
                .Select(item => new WorktreeSourceStageApiResponse(
                    item.Stage.TaskId, item.TaskTitle, item.Stage.SourceObjectId, item.Stage.DeviceId,
                    item.Stage.BaselineEvidenceJson, item.Stage.StagedUtc)));
        });
        app.MapPost("/api/workbenches/{id}/worktrees/{wt}/tasks/{taskId}/stages", async (
            string id, string wt, string taskId, TaskSourceStageApiRequest request, WorkbenchApiState state,
            EngineeringGraphApiFactory graphs, WorkbenchCoordinator coordinator, CancellationToken ct) =>
        {
            var workbench = state.Workbench(id);
            using (var scope = graphs.Open(workbench))
            {
                var task = scope.Service.FindTask(taskId);
                if (task is null || task.WorktreeId != wt) throw new KeyNotFoundException("TASK_NOT_FOUND");
                var device = state.Device(id, wt, task.DeviceId!);
                // One transaction for the whole manifest: the per-object autocommit it used to do made
                // a single stage click wait seconds on more than a thousand writes.
                scope.Service.RegisterEntities(DeviceSnapshotReader.ReadManifestSourceObjects(device.Context.SourceRoot)
                    .Select(source => new GraphEntity(GraphEntityKind.SourceObject,
                        $"{task.DeviceId}:{source.Id}", task.WorkbenchId, wt, task.DeviceId, source.RelativePath)));
                // ADR-0012 item 2: staging registers the whole device manifest in the graph, so the
                // device's projection is flagged for a re-projection before the next read serves it.
                scope.Service.InvalidateDeviceProjection(task.DeviceId!);
            }
            // The stage baseline is always derived from the object's committed Git content, never
            // taken from the request: a client-supplied value could only be a live-TIA shortcut,
            // which ADR-0003 rejects as a baseline.
            coordinator.RegisterWorkbench(workbench);
            var stage = await coordinator.StageTaskSourceObjectAsync(id, wt, taskId, request.SourceObjectId, ct)
                .ConfigureAwait(false);
            return Results.Created($"/api/workbenches/{id}/worktrees/{wt}/tasks/{taskId}/stages/{Uri.EscapeDataString(stage.SourceObjectId)}", new TaskSourceStageApiResponse(stage.TaskId, stage.SourceObjectId, stage.DeviceId, stage.BaselineEvidenceJson, stage.StagedUtc));
        });
        app.MapDelete("/api/workbenches/{id}/worktrees/{wt}/tasks/{taskId}/stages/{sourceObjectId}", (
            string id, string wt, string taskId, string sourceObjectId, WorkbenchApiState state, EngineeringGraphApiFactory graphs) =>
        {
            using var scope = graphs.Open(state.Workbench(id));
            var task = scope.Service.FindTask(taskId);
            if (task is null || task.WorktreeId != wt) throw new KeyNotFoundException("TASK_NOT_FOUND");
            return scope.Service.ReleaseSourceStage(taskId, sourceObjectId) ? Results.NoContent() : throw new KeyNotFoundException("TASK_STAGE_NOT_FOUND");
        });
        app.MapGet("/api/workbenches/{id}/worktrees/{wt}/tasks/{taskId}", (
            string id, string wt, string taskId, WorkbenchApiState state,
            WorktreeTaskStore tasks, EngineeringGraphApiFactory graphs) =>
        {
            var workbench = state.Workbench(id);
            state.Worktree(id, wt);
            // Loading through the compatibility boundary performs idempotent legacy
            // tasks.json import before the graph lookup below.
            tasks.Load(state.WorktreeRoot(id, wt));
            using var scope = graphs.Open(workbench);
            var task = scope.Service.FindTask(taskId);
            if (task is null || task.ScopeKind != GraphTaskScopeKind.Worktree || task.WorktreeId != wt)
                throw new KeyNotFoundException("TASK_NOT_FOUND");
            return ToEngineeringTaskDetailResult(scope.Service, taskId);
        });
        app.MapGet("/api/workbenches/{id}/worktrees/{wt}/active-task", (
            string id, string wt, WorkbenchApiState state, WorktreeTaskStore tasks, EngineeringGraphApiFactory graphs,
            ActiveTaskContextService activeTasks) =>
        {
            var workbench = state.Workbench(id);
            state.Worktree(id, wt);
            tasks.Load(state.WorktreeRoot(id, wt));
            using var scope = graphs.Open(workbench);
            var task = activeTasks.Get(scope.Service, wt);
            return Results.Ok(new { activeTask = task is null ? null : ToEngineeringTaskResponse(task) });
        });
        app.MapMethods("/api/workbenches/{id}/worktrees/{wt}/active-task", new[] { "PUT", "POST" }, (
            string id, string wt, ActiveTaskApiRequest request, WorkbenchApiState state, WorktreeTaskStore tasks,
            EngineeringGraphApiFactory graphs, ActiveTaskContextService activeTasks) =>
        {
            var workbench = state.Workbench(id);
            state.Worktree(id, wt);
            tasks.Load(state.WorktreeRoot(id, wt));
            using var scope = graphs.Open(workbench);
            var task = activeTasks.Select(scope.Service, wt, request.TaskId);
            return Results.Ok(new { activeTask = task is null ? null : ToEngineeringTaskResponse(task) });
        });
        app.MapPost("/api/workbenches/{id}/worktrees/{wt}/tasks", (
            string id,
            string wt,
            CreateWorktreeTaskApiRequest r,
            WorkbenchApiState s,
            WorktreeTaskStore tasks) =>
        {
            // This route predates the graph task contract, but it creates a worktree task in the same
            // graph, so it owes the same target: a registered device, or the worktree's hardware (ADR-0007).
            var worktree = s.Worktree(id, wt);
            if (r.TargetKind == GraphTaskTargetKind.Device &&
                (string.IsNullOrWhiteSpace(r.DeviceId) || !worktree.DeviceIds.Contains(r.DeviceId, StringComparer.Ordinal)))
                throw new EngineeringGraphConstraintException("A worktree task must select a registered device.", "TASK_DEVICE_REQUIRED");
            var task = tasks.Add(s.WorktreeRoot(id, wt), r.Title, r.Details, r.ElementRefs, r.DeviceId, r.TargetKind);
            return Results.Created(
                $"/api/workbenches/{id}/worktrees/{wt}/tasks/{task.TaskId}",
                task);
        });
        app.MapPatch("/api/workbenches/{id}/worktrees/{wt}/tasks/{taskId}", (
            string id,
            string wt,
            string taskId,
            JsonElement body,
            WorkbenchApiState s,
            WorktreeTaskStore tasks) =>
        {
            var updated = tasks.Update(s.WorktreeRoot(id, wt), taskId, task =>
            {
                var title = TryGetOptionalString(body, "title", out var changedTitle)
                    ? changedTitle
                    : task.Title;
                ArgumentException.ThrowIfNullOrWhiteSpace(title);
                return task with
                {
                    Title = title,
                    Details = TryGetOptionalString(body, "details", out var details) ? details : task.Details,
                    Status = TryGetOptionalEnum<WorktreeTaskStatus>(body, "status", out var status)
                        ? status
                        : task.Status,
                    ElementRefs = TryGetOptionalStringArray(body, "elementRefs", out var elementRefs)
                        ? elementRefs
                        : task.ElementRefs,
                };
            });
            return updated is null
                ? throw new KeyNotFoundException("TASK_NOT_FOUND")
                : Results.Ok(updated);
        });
        app.MapDelete("/api/workbenches/{id}/worktrees/{wt}/tasks/{taskId}", (
            string id,
            string wt,
            string taskId,
            WorkbenchApiState s,
            WorktreeTaskStore tasks) =>
            tasks.Delete(s.WorktreeRoot(id, wt), taskId)
                ? Results.NoContent()
                : throw new KeyNotFoundException("TASK_NOT_FOUND"));
        app.MapPost("/api/workbenches/{id}/worktrees", async (
            string id,
            CreateWorktreeApiRequest r,
            WorkbenchApiState s,
            WorkbenchCoordinator c,
            OperationStatusRegistry operations,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await RunOperationAsync(
                http,
                operations,
                "create-worktree",
                "Creating linked worktree...",
                progress => c.CreateWorktreeAsync(new(s.Workbench(id), r.Name, r.Branch, r.StartPoint, r.SourceSavepoint), ct, progress),
                "Worktree created.").ConfigureAwait(false);
            s.Refresh(id);
            return result;
        });
        app.MapGet("/api/workbenches/{id}/branch-start-points", async (
            string id, WorkbenchApiState s, WorkbenchCoordinator c, CancellationToken ct) =>
        {
            c.RegisterWorkbench(s.Workbench(id));
            return Results.Ok(await c.ListBranchStartPointsAsync(id, ct));
        });
        app.MapDelete("/api/workbenches/{id}/worktrees/{wt}", async (
            string id,
            string wt,
            WorkbenchApiState s,
            WorkbenchCoordinator c,
            WorkbenchTagService tags,
            OperationStatusRegistry operations,
            HttpContext http,
            CancellationToken ct) =>
        {
            var result = await RunOperationAsync(
                http,
                operations,
                "delete-worktree",
                "Removing linked worktree...",
                async progress =>
                {
                    await c.DeleteWorktreeAsync(s.Workbench(id), wt, ct, progress).ConfigureAwait(false);
                    tags.RemoveWorktreeAssignments(id, wt);
                    return new { deleted = true };
                },
                "Worktree removed.").ConfigureAwait(false);
            s.Refresh(id);
            if (s.Selection?.WorktreeId == wt)
                s.Select(id);
            return result;
        });
        app.MapPost("/api/workbenches/{id}/worktrees/{wt}/select", (string id, string wt, WorkbenchApiState s, WorkbenchCoordinator coordinator, EngineeringGraphApiFactory graphs) =>
        {
            var workbench = s.Workbench(id);
            coordinator.RegisterWorkbench(workbench);
            s.Worktree(id, wt);
            s.Select(id, wt);
            ReconcileWorktreeSelection(s, graphs, workbench, wt);
            return Results.NoContent();
        });
        app.MapGet("/api/workbenches/{id}/worktrees/{wt}/devices", (string id, string wt, WorkbenchApiState s) => s.ListDevices(id, wt));
        app.MapGet("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/hardware", (
            string workbenchId,
            string worktreeId,
            WorkbenchApiState s,
            EngineeringGraphApiFactory graphs) =>
        {
            // The hardware/AML subtree is a different export from the PLC source, and its facts are
            // projected per worktree (ADR-0011 Phase 5): this route reads the graph and parses no AML.
            using var facts = new HardwareGraphScope(s, graphs, workbenchId);
            return Results.Ok(facts.Reader.ReadConfiguration(s.WorktreeRoot(workbenchId, worktreeId), worktreeId));
        });
        app.MapGet("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/hardware/bom", (
            string workbenchId,
            string worktreeId,
            WorkbenchApiState s,
            EngineeringGraphApiFactory graphs) =>
        {
            using var facts = new HardwareGraphScope(s, graphs, workbenchId);
            return Results.Ok(facts.Reader.ReadBom(s.WorktreeRoot(workbenchId, worktreeId), worktreeId));
        });
        app.MapGet("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/hardware/network", (
            string workbenchId,
            string worktreeId,
            WorkbenchApiState s,
            EngineeringGraphApiFactory graphs) =>
        {
            using var facts = new HardwareGraphScope(s, graphs, workbenchId);
            return Results.Ok(facts.Reader.ReadNetwork(s.WorktreeRoot(workbenchId, worktreeId), worktreeId));
        });
        app.MapPost("/api/workbenches/{id}/worktrees/{wt}/devices/{device}/select", (string id, string wt, string device, WorkbenchApiState s, WorkbenchCoordinator coordinator, EngineeringGraphApiFactory graphs) =>
        {
            var workbench = s.Workbench(id);
            coordinator.RegisterWorkbench(workbench);
            s.Select(id, wt);
            s.Device(device);
            s.Select(id, wt, device);
            // The device selection boundary (ADR-0012 item 3, AC-004): the check and the rows it guards
            // share this one graph scope, and a projection failure is reported as a projection failure
            // rather than served as stale facts. The selection itself is already recorded above, so a
            // failing boundary never loses the user's selection.
            var selected = s.Device(device);
            using var facts = new DeviceSnapshotGraphScope(s, graphs, id);
            facts.Reader.EnsureProjectionCurrent(selected.Context, selected.Metadata);
            return Results.NoContent();
        });

        app.MapGet("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/vc/status", async (
            string workbenchId, string worktreeId, WorkbenchApiState s, ApiMcpGateway gateway, CancellationToken ct) =>
            await gateway.For("vc_status").CallAsync<System.Text.Json.JsonElement>(
                "vc_status", new { repoPath = s.WorktreeRoot(workbenchId, worktreeId) }, ct));
        app.MapGet("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/vc/log", async (
            string workbenchId, string worktreeId, int? maxCount, string? filePath,
            WorkbenchApiState s, ApiMcpGateway gateway, CancellationToken ct) =>
            await gateway.For("vc_log").CallAsync<System.Text.Json.JsonElement>(
                "vc_log", new { repoPath = s.WorktreeRoot(workbenchId, worktreeId), maxCount, filePath }, ct));
        app.MapGet("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/vc/timeline", async (
            string workbenchId,
            string worktreeId,
            int? offset,
            int? limit,
            WorkbenchApiState s,
            WorkbenchCoordinator coordinator,
            CancellationToken ct) =>
        {
            coordinator.RegisterWorkbench(s.Workbench(workbenchId));
            return Results.Ok(await coordinator.ListVersionControlTimelineAsync(
                workbenchId,
                worktreeId,
                offset ?? 0,
                limit ?? 10,
                ct));
        });
        app.MapGet("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/vc/diff", async (
            string workbenchId, string worktreeId, string filePath, string? oldSha, string? newSha,
            WorkbenchApiState s, ApiMcpGateway gateway, CancellationToken ct) =>
            await gateway.For("vc_diff").CallAsync<System.Text.Json.JsonElement>(
                "vc_diff", new { repoPath = s.WorktreeRoot(workbenchId, worktreeId), filePath, oldSha, newSha }, ct));
        app.MapPost("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/vc/commit", async (
            string workbenchId, string worktreeId, CommitSourceApiRequest body,
            WorkbenchApiState s, WorkbenchCoordinator coordinator, ApiMcpGateway gateway,
            EngineeringGraphApiFactory graphs, ActiveTaskContextService activeTasks,
            EngineeringGraphEvidenceIndexerProvider evidenceIndexer,
            OperationStatusRegistry operations, HttpContext http, CancellationToken ct) =>
        {
            var root = s.WorktreeRoot(workbenchId, worktreeId);
            string? taskId;
            using (var graphScope = graphs.Open(s.Workbench(workbenchId)))
            {
                var activeTask = activeTasks.Get(graphScope.Service, worktreeId);
                if (activeTask?.ScopeKind == GraphTaskScopeKind.Worktree
                    && !string.IsNullOrWhiteSpace(body.TaskId)
                    && body.TaskId != activeTask.TaskId)
                    throw new EngineeringGraphConstraintException("The requested task differs from the active worktree task.", "TASK_COMMIT_TASK_MISMATCH");
                taskId = string.IsNullOrWhiteSpace(body.TaskId)
                    ? activeTask is { ScopeKind: GraphTaskScopeKind.Worktree }
                        ? activeTask.TaskId : null
                    : body.TaskId;
                if (!string.IsNullOrWhiteSpace(taskId))
                {
                    var task = graphScope.Service.FindTask(taskId);
                    if (task is null || task.WorktreeId != worktreeId)
                        throw new EngineeringGraphConstraintException("The requested task is not in this worktree.", "TASK_NOT_FOUND");
                    var stagedObjects = graphScope.Service.ListActiveStages(task.TaskId)
                        .Select(stage => graphScope.Service.GetEntity(GraphEntityKind.SourceObject, stage.SourceObjectId)?.ExternalRef)
                        .Where(path => !string.IsNullOrWhiteSpace(path))
                        .Select(path => path!)
                        .ToArray();
                    // A stage records its object relative to the device's source root while a commit
                    // selects paths relative to the worktree; comparing one form against the other
                    // rejected every legitimate task commit (015).
                    var sourceRoot = Path.GetRelativePath(
                        root, s.Device(workbenchId, worktreeId, task.DeviceId!).Context.SourceRoot);
                    var requestedPaths = body.Paths.Select(path => path.Replace('\\', '/')).ToArray();
                    // A message-only commit (an untrackable or safety change) carries no paths and still
                    // belongs to its task, so only a requested path outside the task's active stages is a
                    // mismatch. The refusal names those paths and the task that currently owns them, so
                    // the selection can be corrected instead of guessed at.
                    var outsideStages = requestedPaths
                        .Where(path => !stagedObjects.Any(objectPath => SourcePathForms.Matches(sourceRoot, objectPath, path)))
                        .ToArray();
                    if (outsideStages.Length > 0)
                        throw new EngineeringGraphConstraintException(
                            "A task commit may contain only active staged source objects. Not staged on this task: "
                            + DescribeUnstagedPaths(outsideStages, worktreeId, sourceRoot, graphScope.Service),
                            "TASK_COMMIT_STAGE_MISMATCH");
                }
            }
            var hasExistingSource = body.Paths.Any(path =>
            {
                try
                {
                    return File.Exists(WorkbenchPaths.ResolveRelative(root, path));
                }
                catch (WorkbenchPathException)
                {
                    return false;
                }
            });
            if (hasExistingSource || body.UntrackableChange || body.SafetyChange)
            {
                // All registered worktrees commit through the coordinator: master enforces the
                // TIA-authorization gate, and SVN-managed workbenches (master or feature) run
                // the combined SVN+Git transaction. Untrackable-change commits always take this
                // path so the master write gate still applies to message-only commits. The
                // gateway fallback below only remains for empty/legacy worktrees without
                // on-disk source files.
                coordinator.RegisterWorkbench(s.Workbench(workbenchId));
                return Results.Ok(await RunOperationAsync(
                    http,
                    operations,
                    "vc-commit",
                    "Committing selected changes...",
                    progress => coordinator.CommitSourceAsync(
                        workbenchId,
                        worktreeId,
                        body.Paths,
                        body.Message,
                        ct,
                        untrackableChange: body.UntrackableChange,
                        safetyChange: body.SafetyChange,
                        progress: progress,
                        taskEvidenceTaskId: taskId),
                    "Commit completed.").ConfigureAwait(false));
            }

            if (taskId is not null)
                throw new EngineeringGraphConstraintException("A task commit requires existing staged source files.", "TASK_COMMIT_STAGE_MISMATCH");

            // Compatibility for an empty/legacy worktree: the version-control server still
            // validates the selected source paths. Real master XML files use the protected path above.
            var compatibilityCommit = await gateway.For("vc_commit_selected").CallAsync<System.Text.Json.JsonElement>(
                "vc_commit_selected", new { repoPath = root, paths = body.Paths, message = body.Message }, ct);
            // This fallback also bypasses the coordinator's commit flow, so it records the evidence
            // itself (AC-006).
            RawGatewayCommitEvidence.IndexCommit(
                evidenceIndexer, s.Workbench(workbenchId), worktreeId, compatibilityCommit);
            return Results.Ok(compatibilityCommit);
        });
        app.MapGet("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/vc/validation/{sha}", async (
            string workbenchId, string worktreeId, string sha,
            WorkbenchApiState s, ApiMcpGateway gateway, CancellationToken ct) =>
            await gateway.For("vc_validation_get").CallAsync<System.Text.Json.JsonElement?>(
                "vc_validation_get", new { repoPath = s.WorktreeRoot(workbenchId, worktreeId), commitSha = sha }, ct));
        app.MapGet("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/engineering-state", (
            string workbenchId, string worktreeId, WorkbenchApiState s) =>
        {
            var worktree = s.Worktree(workbenchId, worktreeId);
            var root = s.WorktreeRoot(workbenchId, worktreeId);
            System.Text.Json.JsonElement? revision = null;
            var revisionPath = WorkbenchPaths.ResolveRevisionState(root);
            if (File.Exists(revisionPath))
            {
                revision = System.Text.Json.JsonDocument.Parse(File.ReadAllText(revisionPath)).RootElement.Clone();
            }

            return Results.Ok(new
            {
                revision,
                svnUrl = worktree.SvnUrl,
                baseSvnRevision = worktree.BaseSvnRevision,
                managedTiaProjectPath = worktree.ManagedTiaProjectPath,
                tiaStorePath = WorkbenchPaths.ResolveTiaStore(root),
                pendingCommit = File.Exists(Path.Combine(root, ".automation", "pending-commit.json")),
            });
        });
        app.MapPost("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/restore-tia", async (
            string workbenchId, string worktreeId, RestoreTiaProjectApiRequest body,
            WorkbenchApiState s, WorkbenchCoordinator coordinator, CancellationToken ct) =>
        {
            coordinator.RegisterWorkbench(s.Workbench(workbenchId));
            return Results.Ok(await coordinator.RestoreTiaProjectAsync(
                workbenchId, worktreeId, body.GitCommit, ct));
        });
        app.MapGet("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/savepoints", async (
            string workbenchId, string worktreeId, int? maxCount,
            WorkbenchApiState s, WorkbenchCoordinator coordinator, CancellationToken ct) =>
        {
            coordinator.RegisterWorkbench(s.Workbench(workbenchId));
            return Results.Ok(await coordinator.ListSavepointsAsync(workbenchId, worktreeId, maxCount ?? 30, ct));
        });
        app.MapPost("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/svn-savepoint", async (
            string workbenchId, string worktreeId, NativeSavepointApiRequest body,
            WorkbenchApiState s, WorkbenchCoordinator coordinator, OperationStatusRegistry operations,
            HttpContext http, CancellationToken ct) =>
        {
            coordinator.RegisterWorkbench(s.Workbench(workbenchId));
            return Results.Ok(await RunOperationAsync(
                http,
                operations,
                "svn-savepoint",
                "Creating TIA snapshot...",
                progress => coordinator.CreateNativeSavepointAsync(
                    workbenchId,
                    worktreeId,
                    body.Message,
                    ct,
                    progress: progress),
                "TIA snapshot completed.").ConfigureAwait(false));
        });
        app.MapPost("/api/workbenches/{workbenchId}/vc/compare-tia", async (
            string workbenchId,
            WorkbenchApiState state,
            WorkbenchCoordinator coordinator,
            OperationStatusRegistry operations,
            HttpContext http,
            CancellationToken ct,
            bool allowCompile = false,
            bool forceFullExport = false,
            bool includeHardware = true) =>
            await RunOperationAsync(
                http,
                operations,
                "compare-tia",
                "Comparing master with TIA Portal...",
                progress => coordinator.CompareMasterWithTiaAsync(workbenchId, ct, progress, allowCompile, forceFullExport, includeHardware),
                "TIA comparison completed.").ConfigureAwait(false));
        app.MapPost("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/vc/compare-tia", async (
            string workbenchId,
            string worktreeId,
            WorkbenchApiState state,
            WorkbenchCoordinator coordinator,
            OperationStatusRegistry operations,
            HttpContext http,
            CancellationToken ct,
            bool allowCompile = false,
            bool forceFullExport = false,
            bool includeHardware = true) =>
        {
            coordinator.RegisterWorkbench(state.Workbench(workbenchId));
            return await RunOperationAsync(
                http,
                operations,
                "compare-tia",
                "Comparing master with the selected TIA project...",
                progress => coordinator.CompareWorktreeWithTiaAsync(workbenchId, worktreeId, ct, progress, allowCompile, forceFullExport, includeHardware),
                "TIA comparison completed.").ConfigureAwait(false);
        });
        app.MapGet("/api/workbenches/{workbenchId}/vc/comparisons/{comparisonId}", (
            string workbenchId,
            string comparisonId,
            WorkbenchApiState state,
            WorkbenchCoordinator coordinator) =>
            coordinator.GetComparison(workbenchId, comparisonId));
        app.MapPost("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/vc/comparisons/{comparisonId}/accept", async (
            string workbenchId,
            string worktreeId,
            string comparisonId,
            TiaSynchronizationAcceptApiRequest body,
            WorkbenchApiState s,
            WorkbenchCoordinator coordinator,
            EngineeringGraphApiFactory graphs,
            ActiveTaskContextService activeTasks,
            OperationStatusRegistry operations,
            HttpContext http,
            CancellationToken ct) =>
        {
            // The accepted objects become the worktree's active task's new stage baseline, exactly as
            // they would for a /vc/commit (ADR-0003), so the commit has to name that task.
            string? commitTaskId;
            using (var graphScope = graphs.Open(s.Workbench(workbenchId)))
            {
                var activeTask = activeTasks.Get(graphScope.Service, worktreeId);
                commitTaskId = activeTask is { ScopeKind: GraphTaskScopeKind.Worktree } ? activeTask.TaskId : null;
            }

            return await RunOperationAsync(
                http,
                operations,
                "accept-tia-synchronization",
                "Applying selected TIA source to the active worktree...",
                progress => coordinator.ApplyTiaSynchronizationAsync(
                    workbenchId, worktreeId, comparisonId, body.Paths, body.Message, ct, progress, commitTaskId),
                "Selected TIA source committed to the active worktree.").ConfigureAwait(false);
        });
        app.MapPost("/api/workbenches/{workbenchId}/vc/comparisons/{comparisonId}/push-to-tia", async (
            string workbenchId,
            string comparisonId,
            FeaturePathsApiRequest body,
            WorkbenchApiState s,
            WorkbenchCoordinator coordinator,
            OperationStatusRegistry operations,
            HttpContext http,
            CancellationToken ct) =>
        {
            coordinator.RegisterWorkbench(s.Workbench(workbenchId));
            return await RunOperationAsync(
                http,
                operations,
                "push-to-tia",
                "Importing selected local source into TIA...",
                progress => coordinator.PushSourcesToTiaAsync(workbenchId, comparisonId, body.Paths, ct, progress),
                "Selected local source imported into TIA.").ConfigureAwait(false);
        });
        app.MapPost("/api/workbenches/{workbenchId}/vc/validate-sync", async (
            string workbenchId,
            TiaValidationApiRequest body,
            WorkbenchCoordinator coordinator,
            OperationStatusRegistry operations,
            HttpContext http,
            CancellationToken ct) =>
            await RunOperationAsync(
                http,
                operations,
                "validate-tia-sync",
                "Creating exact TIA synchronization evidence...",
                progress => coordinator.ValidateSynchronizedMasterAsync(workbenchId, body.ConfirmedBy, ct, progress),
                "TIA synchronization evidence created.").ConfigureAwait(false));
        app.MapPost("/api/workbenches/{workbenchId}/worktrees/{featureWorktreeId}/vc/import-plan", async (
            string workbenchId,
            string featureWorktreeId,
            WorkbenchCoordinator coordinator,
            OperationStatusRegistry operations,
            HttpContext http,
            CancellationToken ct) =>
            await RunOperationAsync(http, operations, "feature-import-plan", "Planning feature import...",
                _ => coordinator.PlanFeatureImportAsync(workbenchId, featureWorktreeId, ct),
                "Feature import plan created.").ConfigureAwait(false));
        app.MapPost("/api/workbenches/{workbenchId}/vc/import-plans/{planId}/import", async (
            string workbenchId,
            string planId,
            FeaturePathsApiRequest body,
            WorkbenchCoordinator coordinator,
            OperationStatusRegistry operations,
            HttpContext http,
            CancellationToken ct) =>
            await RunOperationAsync(http, operations, "feature-import", "Importing selected feature objects...",
                _ => coordinator.ImportFeatureAsync(workbenchId, planId, body.Paths, ct),
                "Feature objects imported.").ConfigureAwait(false));
        app.MapPost("/api/workbenches/{workbenchId}/vc/import-sessions/{sessionId}/rollback", async (
            string workbenchId,
            string sessionId,
            FeaturePathsApiRequest body,
            WorkbenchCoordinator coordinator,
            OperationStatusRegistry operations,
            HttpContext http,
            CancellationToken ct) =>
            await RunOperationAsync(http, operations, "feature-import-rollback", "Rolling back selected feature objects...",
                _ => coordinator.RollbackFeatureImportAsync(workbenchId, sessionId, body.Paths, ct),
                "Selected feature objects rolled back.").ConfigureAwait(false));
        app.MapPost("/api/workbenches/{workbenchId}/vc/import-sessions/{sessionId}/keep", (
            string workbenchId,
            string sessionId,
            FeaturePathsApiRequest body,
            WorkbenchCoordinator coordinator) =>
            coordinator.KeepFeatureImportAfterCompileFailure(workbenchId, sessionId, body.Paths));
        app.MapPost("/api/workbenches/{workbenchId}/worktrees/{featureWorktreeId}/vc/validate-merge", async (
            string workbenchId,
            string featureWorktreeId,
            ValidateFeatureMergeApiRequest body,
            WorkbenchCoordinator coordinator,
            OperationStatusRegistry operations,
            HttpContext http,
            CancellationToken ct) =>
            await RunOperationAsync(http, operations, "validate-feature-merge", "Compiling and verifying every PLC device...",
                progress => coordinator.ValidateFeatureMergeAsync(new(workbenchId, featureWorktreeId, body.ImportSessionId, body.MachineValidated, body.ConfirmedBy), ct, progress),
                "Feature merge validation completed.").ConfigureAwait(false));
        app.MapPost("/api/workbenches/{workbenchId}/vc/validated-merges/{validationId}/merge", async (
            string workbenchId,
            string validationId,
            WorkbenchCoordinator coordinator,
            OperationStatusRegistry operations,
            HttpContext http,
            CancellationToken ct) =>
            await RunOperationAsync(http, operations, "merge-validated-feature", "Publishing validated feature merge...",
                _ => coordinator.MergeValidatedAsync(workbenchId, validationId, ct),
                "Validated feature merge published.").ConfigureAwait(false));
        app.MapPost("/api/workbenches/{workbenchId}/vc/rollback-features", async (
            string workbenchId,
            RollbackFeatureApiRequest body,
            WorkbenchCoordinator coordinator,
            OperationStatusRegistry operations,
            HttpContext http,
            CancellationToken ct) =>
            await RunOperationAsync(http, operations, "create-rollback-feature", "Creating historical rollback feature...",
                _ => coordinator.CreateRollbackFeatureAsync(workbenchId, body.HistoricalSha, body.Paths, body.FeatureName, ct),
                "Rollback feature created.").ConfigureAwait(false));
        app.MapPost("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/vc/unauthorized/move", async (
            string workbenchId, string worktreeId, UnauthorizedMasterPathsRequest body,
            WorkbenchApiState s, WorkbenchCoordinator coordinator, CancellationToken ct) =>
        {
            var master = s.Worktree(workbenchId, worktreeId);
            if (!string.Equals(master.Branch, "master", StringComparison.OrdinalIgnoreCase))
                throw new WorkbenchLifecycleException("MASTER_WORKTREE_REQUIRED", "Unauthorized-change recovery must target master.");
            return await coordinator.MoveUnauthorizedMasterChangesAsync(
                workbenchId, body.Paths, body.FeatureName ?? "recovered-feature", ct);
        });
        app.MapPost("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/vc/unauthorized/discard", async (
            string workbenchId, string worktreeId, UnauthorizedMasterPathsRequest body,
            WorkbenchApiState s, WorkbenchCoordinator coordinator, CancellationToken ct) =>
        {
            var master = s.Worktree(workbenchId, worktreeId);
            if (!string.Equals(master.Branch, "master", StringComparison.OrdinalIgnoreCase))
                throw new WorkbenchLifecycleException("MASTER_WORKTREE_REQUIRED", "Unauthorized-change recovery must target master.");
            if (!body.Confirm)
                throw new WorkbenchLifecycleException("CONFIRMATION_REQUIRED", "Discarding unauthorized master changes requires confirmation.");
            await coordinator.DiscardUnauthorizedMasterChangesAsync(workbenchId, body.Paths, ct);
            return new { discarded = body.Paths };
        });

        // Device-scoped knowledge-graph browsing. Unlike the compatibility /api/knowledge/* endpoints,
        // these resolve the device from explicit path identity, so they work without a prior /select POST.
        static async Task<IResult> KnowledgeQuery(
            WorkbenchApiState s,
            ApiMcpGateway gateway,
            string id,
            string wt,
            string device,
            string tool,
            IReadOnlyDictionary<string, object?> args,
            CancellationToken ct)
        {
            var context = s.Device(id, wt, device).Context;
            if (!File.Exists(context.KnowledgeDbPath))
            {
                return Results.NotFound(new
                {
                    error = "DB_NOT_FOUND",
                    message = $"Knowledge database '{context.KnowledgeDbPath}' was not found. Run knowledge update or rebuild first.",
                });
            }

            var arguments = new Dictionary<string, object?>(args) { ["dbPath"] = context.KnowledgeDbPath };
            return Results.Ok(await gateway.For(tool).CallAsync<System.Text.Json.JsonElement>(tool, arguments, ct));
        }

        app.MapGet("/api/workbenches/{id}/worktrees/{wt}/devices/{device}/knowledge/node-kinds",
            async (string id, string wt, string device, WorkbenchApiState s, ApiMcpGateway gateway, CancellationToken ct) =>
                await KnowledgeQuery(s, gateway, id, wt, device, "query_node_kinds", new Dictionary<string, object?>(), ct));
        app.MapGet("/api/workbenches/{id}/worktrees/{wt}/devices/{device}/knowledge/nodes",
            async (string id, string wt, string device, string? kind, string? search, int? maxRows, int? offset, WorkbenchApiState s, ApiMcpGateway gateway, CancellationToken ct) =>
                await KnowledgeQuery(s, gateway, id, wt, device, "query_nodes", new Dictionary<string, object?> { ["kind"] = kind, ["search"] = search, ["maxRows"] = maxRows, ["offset"] = offset }, ct));
        app.MapGet("/api/workbenches/{id}/worktrees/{wt}/devices/{device}/knowledge/edge-types",
            async (string id, string wt, string device, WorkbenchApiState s, ApiMcpGateway gateway, CancellationToken ct) =>
                await KnowledgeQuery(s, gateway, id, wt, device, "query_edge_types", new Dictionary<string, object?>(), ct));
        app.MapGet("/api/workbenches/{id}/worktrees/{wt}/devices/{device}/knowledge/edges",
            async (string id, string wt, string device, string? nodeId, string? type, string? search, int? maxRows, int? offset, WorkbenchApiState s, ApiMcpGateway gateway, CancellationToken ct) =>
                await KnowledgeQuery(s, gateway, id, wt, device, "query_edges", new Dictionary<string, object?> { ["nodeId"] = nodeId, ["type"] = type, ["search"] = search, ["maxRows"] = maxRows, ["offset"] = offset }, ct));
        app.MapGet("/api/workbenches/{id}/worktrees/{wt}/devices/{device}/knowledge/node-properties",
            async (string id, string wt, string device, string nodeId, WorkbenchApiState s, ApiMcpGateway gateway, CancellationToken ct) =>
                await KnowledgeQuery(s, gateway, id, wt, device, "query_node_properties", new Dictionary<string, object?> { ["nodeId"] = nodeId }, ct));
        app.MapGet("/api/workbenches/{id}/worktrees/{wt}/devices/{device}/knowledge/edge-properties",
            async (string id, string wt, string device, string edgeId, WorkbenchApiState s, ApiMcpGateway gateway, CancellationToken ct) =>
                await KnowledgeQuery(s, gateway, id, wt, device, "query_edge_properties", new Dictionary<string, object?> { ["edgeId"] = edgeId }, ct));
        app.MapGet("/api/workbenches/{id}/worktrees/{wt}/devices/{device}/source/usage",
            async (string id, string wt, string device, string variable, int? maxRows, WorkbenchApiState s, ApiMcpGateway gateway, CancellationToken ct) =>
                await KnowledgeQuery(s, gateway, id, wt, device, "get_variable_usage", new Dictionary<string, object?> { ["variable"] = variable, ["maxRows"] = maxRows }, ct));
        app.MapPost("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/devices/{device}/tia/open", async (
            string workbenchId,
            string worktreeId,
            string device,
            OpenTiaProjectApiRequest? request,
            WorkbenchApiState s,
            WorkbenchCoordinator c,
            OperationStatusRegistry operations,
            HttpContext http,
            CancellationToken ct) =>
            await RunOperationAsync(
                http,
                operations,
                "open-tia-project",
                "Opening registered project in TIA Portal...",
                async progress =>
                {
                    await c.OpenProjectInTiaAsync(
                            s.Device(workbenchId, worktreeId, device).Context,
                            ct,
                            progress,
                            request?.WithUI ?? true,
                            request?.Upgrade ?? false,
                            request?.AuthenticationMode)
                        .ConfigureAwait(false);
                    return new { opened = true };
                },
                "TIA project opened.").ConfigureAwait(false));
        app.MapPost("/api/workbenches/{workbenchId}/tia/open", async (
            string workbenchId,
            OpenTiaProjectApiRequest? request,
            WorkbenchApiState s,
            WorkbenchCoordinator c,
            OperationStatusRegistry operations,
            HttpContext http,
            CancellationToken ct) =>
            await RunOperationAsync(
                http,
                operations,
                "open-tia-project",
                "Opening workbench project in TIA Portal...",
                async progress =>
                {
                    await c.OpenWorkbenchProjectInTiaAsync(
                            s.Workbench(workbenchId),
                            ct,
                            progress,
                            request?.WithUI ?? true,
                            request?.Upgrade ?? false,
                            request?.AuthenticationMode)
                        .ConfigureAwait(false);
                    return new { opened = true };
                },
                "TIA project opened.").ConfigureAwait(false));
        app.MapPost("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/tia/open", async (
            string workbenchId,
            string worktreeId,
            OpenTiaProjectApiRequest? request,
            WorkbenchApiState s,
            WorkbenchCoordinator c,
            OperationStatusRegistry operations,
            HttpContext http,
            CancellationToken ct) =>
            await RunOperationAsync(
                http,
                operations,
                "open-tia-project",
                "Opening worktree project in TIA Portal...",
                async progress =>
                {
                    await c.OpenWorktreeProjectInTiaAsync(
                            s.Workbench(workbenchId),
                            s.Worktree(workbenchId, worktreeId),
                            ct,
                            progress,
                            request?.WithUI ?? true,
                            request?.Upgrade ?? false,
                            request?.AuthenticationMode)
                        .ConfigureAwait(false);
                    return new { opened = true };
                },
                "TIA project opened.").ConfigureAwait(false));
        app.MapPost("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/devices/{device}/tia/attach", async (
            string workbenchId,
            string worktreeId,
            string device,
            AttachTiaInstanceApiRequest r,
            WorkbenchApiState s,
            WorkbenchCoordinator c,
            OperationStatusRegistry operations,
            HttpContext http,
            CancellationToken ct) =>
            await RunOperationAsync(
                http,
                operations,
                "attach-tia-instance",
                "Attaching to running TIA Portal instance...",
                async progress =>
                {
                    s.Device(workbenchId, worktreeId, device);
                    await c.AttachTiaInstanceAsync(r.SessionId, ct, progress)
                        .ConfigureAwait(false);
                    return new { attached = true };
                },
                "TIA instance attached.").ConfigureAwait(false));
        app.MapPost("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/hardware/reload", async (
            string workbenchId,
            string worktreeId,
            WorkbenchApiState s,
            WorkbenchCoordinator c,
            OperationStatusRegistry operations,
            HttpContext http,
            CancellationToken ct) =>
        {
            var device = s.ListDevices(workbenchId, worktreeId).FirstOrDefault()
                ?? throw new KeyNotFoundException("DEVICE_NOT_FOUND");
            return await RunOperationAsync(
                http,
                operations,
                "reload-hardware",
                "Reloading hardware configuration...",
                progress => c.ReloadHardwareAsync(
                    s.Device(workbenchId, worktreeId, device.DeviceId).Context,
                    ct,
                    progress),
                "Hardware configuration reloaded.").ConfigureAwait(false);
        });
        app.MapPost("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/hardware/compare", async (
            string workbenchId,
            string worktreeId,
            WorkbenchApiState s,
            WorkbenchCoordinator c,
            OperationStatusRegistry operations,
            HttpContext http,
            CancellationToken ct) =>
        {
            var device = s.ListDevices(workbenchId, worktreeId).FirstOrDefault()
                ?? throw new KeyNotFoundException("DEVICE_NOT_FOUND");
            return await RunOperationAsync(
                http,
                operations,
                "compare-hardware",
                "Comparing hardware configuration...",
                progress => c.CompareHardwareAsync(
                    s.Device(workbenchId, worktreeId, device.DeviceId).Context,
                    ct,
                    progress),
                "Hardware comparison complete.").ConfigureAwait(false);
        });
        app.MapPost("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/hardware/overwrite", async (
            string workbenchId,
            string worktreeId,
            HardwareOverwriteApiRequest request,
            WorkbenchApiState s,
            WorkbenchCoordinator c,
            OperationStatusRegistry operations,
            HttpContext http,
            CancellationToken ct) =>
        {
            var device = s.ListDevices(workbenchId, worktreeId).FirstOrDefault()
                ?? throw new KeyNotFoundException("DEVICE_NOT_FOUND");
            return await RunOperationAsync(
                http,
                operations,
                "overwrite-hardware",
                "Applying staged hardware configuration...",
                progress => c.OverwriteHardwareFromStagingAsync(
                    s.Device(workbenchId, worktreeId, device.DeviceId).Context,
                    request.ConfirmOverwrite,
                    ct,
                    progress,
                    request.Message),
                "Saved hardware configuration updated.").ConfigureAwait(false);
        });
        app.MapGet("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/devices/{device}", (
            string workbenchId, string worktreeId, string device, WorkbenchApiState s,
            EngineeringGraphApiFactory graphs) =>
        {
            var selected = s.Device(workbenchId, worktreeId, device);
            // The device page reads the engineering graph; a device without a projection is projected
            // before it is served, so no request walks the exported source tree (ADR-0011).
            using var facts = new DeviceSnapshotGraphScope(s, graphs, workbenchId);
            return Results.Ok(facts.Reader.Read(selected.Context, selected.Metadata));
        });
        app.MapGet("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/devices/{device}/blocks", (
            string workbenchId, string worktreeId, string device, WorkbenchApiState s,
            EngineeringGraphApiFactory graphs) =>
        {
            var selected = s.Device(workbenchId, worktreeId, device);
            using var facts = new DeviceSnapshotGraphScope(s, graphs, workbenchId);
            return Results.Ok(facts.Reader.ReadBlocks(selected.Context, selected.Metadata));
        });
        app.MapGet("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/devices/{device}/source-objects", (
            string workbenchId, string worktreeId, string device, WorkbenchApiState s,
            EngineeringGraphApiFactory graphs) =>
        {
            var selected = s.Device(workbenchId, worktreeId, device);
            // The task page's picker reads this list and nothing else. Instance DBs are dropped: they
            // are outside the evidence domain and can never be compared, so they are not a task basis
            // and must not be offered as one.
            using var facts = new DeviceSnapshotGraphScope(s, graphs, workbenchId);
            return Results.Ok(facts.Reader.ReadSourceObjects(selected.Context, selected.Metadata, comparableOnly: true));
        });
        app.MapPost("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/devices/{device}/refresh/stage", async (
            string workbenchId, string worktreeId, string device, WorkbenchApiState s,
            WorkbenchCoordinator c, OperationStatusRegistry operations, HttpContext http, CancellationToken ct,
            bool allowCompile = false) =>
            await RunOperationAsync(http, operations, "stage-refresh", "Preparing export staging area...",
                progress => c.StageRefreshAsync(
                    s.Device(workbenchId, worktreeId, device).Context,
                    ct,
                    progress,
                    allowCompile),
                "Refresh staged.").ConfigureAwait(false));
        app.MapGet("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/devices/{device}/refresh/preview", (
            string workbenchId, string worktreeId, string device, WorkbenchApiState s, WorkbenchCoordinator c) =>
        {
            var preview = c.PreviewRefresh(s.Device(workbenchId, worktreeId, device).Context);
            s.Remember(preview);
            return preview;
        });
        app.MapPost("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/devices/{device}/refresh/apply", async (
            string workbenchId, string worktreeId, string device, RefreshApplyApiRequest r,
            WorkbenchApiState s, WorkbenchCoordinator c, DeviceReconciler reconciler,
            OperationStatusRegistry operations, HttpContext http, CancellationToken ct) =>
        {
            var selected = s.Device(workbenchId, worktreeId, device);
            var preview = s.Take(r.PreviewId, device, worktreeId);
            var legacyRemovals = reconciler.ValidateLegacyRemovalApprovals(
                selected.Context, preview, r.ApprovedRemovalPaths ?? []);
            var approved = new HashSet<string>(
                r.ApprovedPaths ?? preview.Entries
                    .Where(entry => entry.Kind is ReconciliationChangeKind.Added or ReconciliationChangeKind.Changed)
                    .Select(entry => entry.RelativePath),
                StringComparer.Ordinal);
            approved.UnionWith(legacyRemovals);
            return await RunOperationAsync(http, operations, "apply-refresh", "Applying approved refresh...",
                progress => c.ApplyRefreshAsync(selected.Context, new(preview, approved), ct, progress, r.CommitMessage),
                "Refresh applied.").ConfigureAwait(false);
        });
        app.MapPost("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/devices/{device}/bootstrap", async (
            string workbenchId, string worktreeId, string device, BootstrapApiRequest? body, WorkbenchApiState s,
            WorkbenchCoordinator c, OperationStatusRegistry operations, HttpContext http, CancellationToken ct,
            bool allowCompile = false) =>
            await RunOperationAsync(http, operations, "bootstrap-device", "Generating PLC context...",
                progress => c.BootstrapDeviceAsync(
                    s.Device(workbenchId, worktreeId, device).Context,
                    ct,
                    progress,
                    allowCompile,
                    body?.CommitMessage),
                "PLC context generated.").ConfigureAwait(false));
        app.MapPost("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/devices/{device}/bootstrap-worktree", async (
            string workbenchId, string worktreeId, string device, BootstrapApiRequest? body, WorkbenchApiState s,
            WorkbenchCoordinator c, OperationStatusRegistry operations, HttpContext http, CancellationToken ct,
            bool allowCompile = false) =>
            await RunOperationAsync(http, operations, "bootstrap-worktree", "Generating all PLC contexts...",
                progress => c.BootstrapWorktreeAsync(
                    s.Device(workbenchId, worktreeId, device).Context,
                    ct,
                    progress,
                    allowCompile,
                    body?.CommitMessage),
                "All PLC contexts generated.").ConfigureAwait(false));
        app.MapPost("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/devices/{device}/knowledge/update", async (
            string workbenchId, string worktreeId, string device, WorkbenchApiState s,
            WorkbenchCoordinator c, OperationStatusRegistry operations, HttpContext http, CancellationToken ct) =>
            await RunOperationAsync(http, operations, "update-knowledge", "Updating device knowledge...",
                progress => c.UpdateKnowledgeAsync(s.Device(workbenchId, worktreeId, device).Context, ct, progress),
                "Knowledge updated.").ConfigureAwait(false));
        app.MapPost("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/devices/{device}/knowledge/rebuild", async (
            string workbenchId, string worktreeId, string device, WorkbenchApiState s,
            WorkbenchCoordinator c, OperationStatusRegistry operations, HttpContext http, CancellationToken ct) =>
            await RunOperationAsync(http, operations, "rebuild-knowledge", "Rebuilding device knowledge...",
                progress => c.RebuildKnowledgeAsync(s.Device(workbenchId, worktreeId, device).Context, ct, progress),
                "Knowledge rebuilt.").ConfigureAwait(false));
        app.MapPost("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/devices/{device}/source/prepare-edit", (
            string workbenchId, string worktreeId, string device, SourcePathApiRequest r,
            WorkbenchApiState s, DeviceSourceResolver resolver, WorkbenchWritePolicy writePolicy) =>
        {
            var context = s.Device(workbenchId, worktreeId, device).Context;
            writePolicy.RequireFeatureEdit(context);
            return resolver.PrepareEditable(context, r.RelativePath);
        });
        app.MapGet("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/devices/{device}/source/inspect", (
            string workbenchId, string worktreeId, string device, string relativePath,
            WorkbenchApiState s, EngineeringGraphApiFactory graphs) =>
        {
            // The inspected content is projected at ingest (ADR-0011 Phase 5), so this route reads the
            // graph and never opens the exported XML file: removing or corrupting it after the ingest
            // changes nothing here. The response shape, and the status each inspection error maps to,
            // are the ones the inspector produced.
            try
            {
                var selected = s.Device(workbenchId, worktreeId, device);
                using var facts = new DeviceSnapshotGraphScope(s, graphs, workbenchId);
                return Results.Ok(facts.Reader.ReadInspection(selected.Context, selected.Metadata, relativePath));
            }
            catch (SourceInspectionException exception) { return Results.UnprocessableEntity(new { error = exception.Code, message = exception.Message }); }
            catch (FileNotFoundException exception) { return Results.NotFound(new { error = "SOURCE_FILE_NOT_FOUND", message = exception.Message }); }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = "SOURCE_PATH_INVALID", message = exception.Message }); }
            catch (WorkbenchPathException exception) { return Results.BadRequest(new { error = "SOURCE_PATH_INVALID", message = exception.Message }); }
        });
        app.MapPost("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/devices/{device}/source/import", async (
            string workbenchId, string worktreeId, string device, SourcePathApiRequest r,
            WorkbenchApiState s, WorkbenchCoordinator c, OperationStatusRegistry operations,
            HttpContext http, CancellationToken ct) =>
            await RunOperationAsync(http, operations, "import-source", "Importing modified source...",
                progress => c.ImportModifiedAsync(
                    s.Device(workbenchId, worktreeId, device).Context, r.RelativePath, ct, progress),
                "Source imported.").ConfigureAwait(false));
        app.MapPost("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/devices/{device}/source/open-in-tia", async (
            string workbenchId, string worktreeId, string device, SourcePathApiRequest r,
            WorkbenchApiState s, WorkbenchCoordinator c, OperationStatusRegistry operations,
            HttpContext http, CancellationToken ct) =>
            await RunOperationAsync(http, operations, "open-source-in-tia", "Opening source in TIA Portal...",
                progress => c.OpenSourceObjectInTiaAsync(
                    s.Device(workbenchId, worktreeId, device).Context, r.RelativePath, ct, progress),
                "Source opened in TIA Portal.").ConfigureAwait(false));
        app.MapPost("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/devices/{device}/source/compare-tia", async (
            string workbenchId, string worktreeId, string device, SourcePathApiRequest r,
            WorkbenchApiState s, WorkbenchCoordinator c, OperationStatusRegistry operations,
            HttpContext http, CancellationToken ct) =>
            await RunOperationAsync(http, operations, "compare-source-tia", "Comparing source with TIA...",
                progress => c.CompareSourceObjectWithTiaAsync(
                    s.Device(workbenchId, worktreeId, device).Context, r.RelativePath, ct, progress),
                "Source comparison completed.").ConfigureAwait(false));
        app.MapPost("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/devices/{device}/source/comparisons/{comparisonId}/accept", async (
            string workbenchId, string worktreeId, string device, string comparisonId,
            WorkbenchApiState s, WorkbenchCoordinator c, OperationStatusRegistry operations,
            HttpContext http, CancellationToken ct) =>
            await RunOperationAsync(http, operations, "accept-tia-source", "Applying TIA source to local file...",
                progress => c.AcceptTiaSourceObjectAsync(
                    s.Device(workbenchId, worktreeId, device).Context, comparisonId, ct, progress),
                "TIA source applied locally.").ConfigureAwait(false));
        app.MapPost("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/devices/{device}/source/comparisons/{comparisonId}/push-to-tia", async (
            string workbenchId, string worktreeId, string device, string comparisonId,
            WorkbenchApiState s, WorkbenchCoordinator c, OperationStatusRegistry operations,
            HttpContext http, CancellationToken ct) =>
            await RunOperationAsync(http, operations, "push-source-to-tia", "Importing local source into TIA...",
                progress => c.PushSourceObjectToTiaAsync(
                    s.Device(workbenchId, worktreeId, device).Context, comparisonId, ct, progress),
                "Local source imported into TIA.").ConfigureAwait(false));
        app.MapGet("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/devices/{device}/sessions", (
            string workbenchId, string worktreeId, string device, WorkbenchApiState s) =>
            SessionManager.ListSessions(s.Device(workbenchId, worktreeId, device).Context));
        app.MapPost("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/devices/{device}/sessions", (
            string workbenchId, string worktreeId, string device, SessionCreateApiRequest r, WorkbenchApiState s,
            EngineeringGraphApiFactory graphs, ActiveTaskContextService activeTasks) =>
        {
            var taskId = ResolveSessionTaskId(s, graphs, activeTasks, workbenchId, worktreeId, r.TaskId);
            var session = SessionManager.CreateNewSession(
                s.Device(workbenchId, worktreeId, device).Context, r.Settings, r.RuntimeContext, taskId,
                string.IsNullOrWhiteSpace(taskId) ? null : "default");
            using var graph = graphs.Open(s.Workbench(workbenchId));
            try { SessionGraphOperations.Register(graph.Service, session, GraphProvenance.Default); }
            catch { SessionManager.DeleteSession(s.Device(workbenchId, worktreeId, device).Context, session.Header.SessionId); throw; }
            return session;
        });
        app.MapGet("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/devices/{device}/sessions/{session}", (
            string workbenchId, string worktreeId, string device, string session, WorkbenchApiState s) =>
            SessionManager.LoadSession(s.Device(workbenchId, worktreeId, device).Context, session) is { } value
                ? Results.Ok(value) : Results.NotFound());
        // Deleting a conversation removes it from both stores, so the device it belongs to is named
        // rather than resolved from the current selection (ADR-0010).
        app.MapDelete("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/devices/{device}/sessions/{session}", (
            string workbenchId, string worktreeId, string device, string session, WorkbenchApiState s,
            EngineeringGraphApiFactory graphs, ApiChatService chat) =>
        {
            var context = s.Device(workbenchId, worktreeId, device).Context;
            if (SessionManager.LoadSession(context, session) is null)
                throw new KeyNotFoundException("SESSION_NOT_FOUND");
            using var graph = graphs.Open(s.Workbench(workbenchId));
            SessionGraphOperations.Delete(graph.Service, chat, context, session, ChatScopes.Device);
            return Results.NoContent();
        });
        app.MapPut("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/devices/{device}/sessions/{session}", (
            string workbenchId, string worktreeId, string device, string session,
            SessionSaveApiRequest r, WorkbenchApiState s, EngineeringGraphApiFactory graphs,
            ActiveTaskContextService activeTasks) =>
        {
            if (r.Session.Header.SessionId != session) return Results.BadRequest();
            var context = s.Device(workbenchId, worktreeId, device).Context;
            var current = SessionManager.LoadSession(context, session) ?? throw new KeyNotFoundException("SESSION_NOT_FOUND");
            var candidate = SessionGraphOperations.ValidateCandidate(context, current, r.Session);
            using var graph = graphs.Open(s.Workbench(workbenchId));
            var updatedSession = SessionGraphOperations.ApplyWithPersistence(graph.Service, candidate, candidate.Header.TaskId,
                value => value with { Header = value.Header with { TaskProvenance = string.IsNullOrWhiteSpace(value.Header.TaskId) ? null : "manual" } },
                value => SessionManager.SaveSession(context, value));
            return Results.NoContent();
        });
        app.MapPut("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/devices/{device}/sessions/{session}/task", (
            string workbenchId, string worktreeId, string device, string session, SessionTaskApiRequest request,
            WorkbenchApiState s, EngineeringGraphApiFactory graphs) =>
        {
            var context = s.Device(workbenchId, worktreeId, device).Context;
            var current = SessionManager.LoadSession(context, session) ?? throw new KeyNotFoundException("SESSION_NOT_FOUND");
            using var graph = graphs.Open(s.Workbench(workbenchId));
            var updated = SessionGraphOperations.ApplyWithPersistence(graph.Service, current, request.TaskId,
                value => value with { Header = value.Header with { TaskId = request.TaskId, TaskProvenance = string.IsNullOrWhiteSpace(request.TaskId) ? null : "manual", UpdatedAt = DateTimeOffset.UtcNow.ToString("O") } },
                value => SessionManager.SaveSession(context, value));
            return Results.Ok(updated);
        });
        app.MapGet("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/devices/{device}/vc/status", async (
            string workbenchId, string worktreeId, string device, WorkbenchApiState s,
            ApiMcpGateway gateway, CancellationToken ct) =>
            await gateway.For("vc_status").CallAsync<System.Text.Json.JsonElement>(
                "vc_status", new { repoPath = s.Device(workbenchId, worktreeId, device).Context.WorktreeRoot }, ct));
        app.MapGet("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/devices/{device}/vc/log", async (
            string workbenchId, string worktreeId, string device, int? maxCount, WorkbenchApiState s,
            ApiMcpGateway gateway, CancellationToken ct) =>
            await gateway.For("vc_log").CallAsync<System.Text.Json.JsonElement>(
                "vc_log", new { repoPath = s.Device(workbenchId, worktreeId, device).Context.WorktreeRoot, maxCount }, ct));
        app.MapGet("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/devices/{device}/vc/diff", async (
            string workbenchId, string worktreeId, string device, string filePath, WorkbenchApiState s,
            ApiMcpGateway gateway, CancellationToken ct) =>
            await gateway.For("vc_diff").CallAsync<System.Text.Json.JsonElement>(
                "vc_diff", new { repoPath = s.Device(workbenchId, worktreeId, device).Context.WorktreeRoot, filePath }, ct));
        app.MapPost("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/devices/{device}/vc/add", async (
            string workbenchId, string worktreeId, string device, CompatibilityPathRequest body,
            WorkbenchApiState s, ApiMcpGateway gateway, CancellationToken ct) =>
            await gateway.For("vc_add").CallAsync<System.Text.Json.JsonElement>(
                "vc_add", new { repoPath = s.Device(workbenchId, worktreeId, device).Context.WorktreeRoot, paths = body.Paths ?? [] }, ct));
        app.MapPost("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/devices/{device}/vc/commit", async (
            string workbenchId, string worktreeId, string device, CompatibilityPathRequest body,
            WorkbenchApiState s, ApiMcpGateway gateway, EngineeringGraphEvidenceIndexerProvider evidenceIndexer,
            CancellationToken ct) =>
        {
            var commit = await gateway.For("vc_commit").CallAsync<System.Text.Json.JsonElement>(
                "vc_commit", new { repoPath = s.Device(workbenchId, worktreeId, device).Context.WorktreeRoot, message = body.Message }, ct);
            // A raw gateway commit never reaches the coordinator's evidence indexing (AC-006).
            RawGatewayCommitEvidence.IndexCommit(
                evidenceIndexer, s.Workbench(workbenchId), worktreeId, commit);
            return commit;
        });
        app.MapPost("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/devices/{device}/vc/restore", async (
            string workbenchId, string worktreeId, string device, CompatibilityPathRequest body,
            WorkbenchApiState s, SandboxedToolExecutor executor, CancellationToken ct) =>
            await executor.RequestAsync("vc_restore",
                new Dictionary<string, object?> { ["filePath"] = body.FilePath },
                s.Device(workbenchId, worktreeId, device).Context, "api", ct));
        app.MapPost("/api/devices/{device}/refresh/stage", async (
            string device,
            WorkbenchApiState s,
            WorkbenchCoordinator c,
            OperationStatusRegistry operations,
            HttpContext http,
            CancellationToken ct,
            bool allowCompile = false) =>
            await RunOperationAsync(
                http,
                operations,
                "stage-refresh",
                "Preparing export staging area...",
                progress => c.StageRefreshAsync(
                    s.Device(device).Context,
                    ct,
                    progress,
                    allowCompile),
                "Refresh staged.").ConfigureAwait(false));
        app.MapGet("/api/devices/{device}/refresh/preview", (string device, WorkbenchApiState s, WorkbenchCoordinator c) => { var p = c.PreviewRefresh(s.Device(device).Context); s.Remember(p); return p; });
        app.MapPost("/api/devices/{device}/refresh/apply", async (
            string device,
            RefreshApplyApiRequest r,
            WorkbenchApiState s,
            WorkbenchCoordinator c,
            DeviceReconciler reconciler,
            OperationStatusRegistry operations,
            HttpContext http,
            CancellationToken ct) =>
        {
            var selected = s.Device(device);
            var preview = s.Take(r.PreviewId, device);
            var legacyRemovals = reconciler.ValidateLegacyRemovalApprovals(
                selected.Context,
                preview,
                r.ApprovedRemovalPaths ?? []);
            var approved = new HashSet<string>(
                r.ApprovedPaths ?? preview.Entries
                    .Where(entry => entry.Kind is ReconciliationChangeKind.Added or ReconciliationChangeKind.Changed)
                    .Select(entry => entry.RelativePath),
                StringComparer.Ordinal);
            approved.UnionWith(legacyRemovals);
            return await RunOperationAsync(
                    http,
                    operations,
                    "apply-refresh",
                    "Applying approved refresh...",
                    progress => c.ApplyRefreshAsync(
                        selected.Context,
                        new(preview, approved),
                        ct,
                        progress),
                    "Refresh applied.")
                .ConfigureAwait(false);
        });
        app.MapPost("/api/devices/{device}/knowledge/update", async (
            string device,
            WorkbenchApiState s,
            WorkbenchCoordinator c,
            OperationStatusRegistry operations,
            HttpContext http,
            CancellationToken ct) =>
            await RunOperationAsync(
                http,
                operations,
                "update-knowledge",
                "Updating device knowledge...",
                progress => c.UpdateKnowledgeAsync(s.Device(device).Context, ct, progress),
                "Knowledge updated.").ConfigureAwait(false));
        app.MapPost("/api/devices/{device}/knowledge/rebuild", async (
            string device,
            WorkbenchApiState s,
            WorkbenchCoordinator c,
            OperationStatusRegistry operations,
            HttpContext http,
            CancellationToken ct) =>
            await RunOperationAsync(
                http,
                operations,
                "rebuild-knowledge",
                "Rebuilding device knowledge...",
                progress => c.RebuildKnowledgeAsync(s.Device(device).Context, ct, progress),
                "Knowledge rebuilt.").ConfigureAwait(false));
        app.MapPost("/api/devices/{device}/source/prepare-edit", (string device, SourcePathApiRequest r, WorkbenchApiState s, DeviceSourceResolver resolver, WorkbenchWritePolicy writePolicy) =>
        {
            var context = s.Device(device).Context;
            writePolicy.RequireFeatureEdit(context);
            return resolver.PrepareEditable(context, r.RelativePath);
        });
        app.MapPost("/api/devices/{device}/source/import", async (
            string device,
            SourcePathApiRequest r,
            WorkbenchApiState s,
            WorkbenchCoordinator c,
            OperationStatusRegistry operations,
            HttpContext http,
            CancellationToken ct) =>
            await RunOperationAsync(
                http,
                operations,
                "import-source",
                "Importing modified source...",
                progress => c.ImportModifiedAsync(s.Device(device).Context, r.RelativePath, ct, progress),
                "Source imported.").ConfigureAwait(false));
        app.MapPost("/api/worktrees/{source}/merge", async (
            string source,
            MergeWorktreeApiRequest r,
            WorkbenchApiState s,
            WorkbenchCoordinator c,
            OperationStatusRegistry operations,
            HttpContext http) =>
        {
            var workbenchId = s.Selection?.WorkbenchId ?? throw new InvalidOperationException("WORKBENCH_SELECTION_REQUIRED");
            return await RunOperationAsync(
                http,
                operations,
                "merge-worktree",
                "Merging worktree...",
                progress => c.MergeWorktreeAsync(workbenchId, source, r.TargetWorktreeId, progress: progress),
                "Worktree merged.").ConfigureAwait(false);
        });
        app.MapPost("/api/workbenches/{workbenchId}/worktrees/{source}/merge", async (
            string workbenchId,
            string source,
            MergeWorktreeApiRequest r,
            WorkbenchApiState s,
            WorkbenchCoordinator c,
            OperationStatusRegistry operations,
            HttpContext http) =>
        {
            s.Worktree(workbenchId, source);
            s.Worktree(workbenchId, r.TargetWorktreeId);
            return await RunOperationAsync(
                http,
                operations,
                "merge-worktree",
                "Merging worktree...",
                progress => c.MergeWorktreeAsync(
                    workbenchId, source, r.TargetWorktreeId, progress: progress),
                "Worktree merged.").ConfigureAwait(false);
        });
        app.MapGet("/api/devices/{device}/sessions", (string device, WorkbenchApiState s) => SessionManager.ListSessions(s.Device(device).Context));
        app.MapPost("/api/devices/{device}/sessions", (string device, SessionCreateApiRequest r, WorkbenchApiState s,
            EngineeringGraphApiFactory graphs, ActiveTaskContextService activeTasks) =>
        {
            var selection = s.Selection ?? throw new InvalidOperationException("WORKBENCH_SELECTION_REQUIRED");
            var taskId = ResolveSessionTaskId(s, graphs, activeTasks, selection.WorkbenchId, selection.WorktreeId, r.TaskId);
            var session = SessionManager.CreateNewSession(s.Device(device).Context, r.Settings, r.RuntimeContext, taskId,
                string.IsNullOrWhiteSpace(taskId) ? null : "default");
            using var graph = graphs.Open(s.Workbench(selection.WorkbenchId));
            try { SessionGraphOperations.Register(graph.Service, session, GraphProvenance.Default); }
            catch { SessionManager.DeleteSession(s.Device(device).Context, session.Header.SessionId); throw; }
            return session;
        });
        app.MapGet("/api/devices/{device}/sessions/{session}", (string device, string session, WorkbenchApiState s) => SessionManager.LoadSession(s.Device(device).Context, session) is { } value ? Results.Ok(value) : Results.NotFound());
        app.MapPut("/api/devices/{device}/sessions/{session}", (string device, string session, SessionSaveApiRequest r, WorkbenchApiState s,
            EngineeringGraphApiFactory graphs, ActiveTaskContextService activeTasks) =>
        {
            if (r.Session.Header.SessionId != session) return Results.BadRequest();
            var selection = s.Selection ?? throw new InvalidOperationException("WORKBENCH_SELECTION_REQUIRED");
            var context = s.Device(device).Context;
            var current = SessionManager.LoadSession(context, session) ?? throw new KeyNotFoundException("SESSION_NOT_FOUND");
            var candidate = SessionGraphOperations.ValidateCandidate(context, current, r.Session);
            using var graph = graphs.Open(s.Workbench(selection.WorkbenchId));
            var updatedSession = SessionGraphOperations.ApplyWithPersistence(graph.Service, candidate, candidate.Header.TaskId,
                value => value with { Header = value.Header with { TaskProvenance = string.IsNullOrWhiteSpace(value.Header.TaskId) ? null : "manual" } },
                value => SessionManager.SaveSession(context, value));
            return Results.NoContent();
        });
        return app;
    }

    private static EngineeringTaskApiResponse ToEngineeringTaskResponse(GraphTask task) => new(
        task.TaskId, task.WorkbenchId, JsonNamingPolicy.CamelCase.ConvertName(task.ScopeKind.ToString()), task.WorktreeId,
        task.Title, JsonNamingPolicy.CamelCase.ConvertName(task.Type.ToString()), JsonNamingPolicy.CamelCase.ConvertName(task.Status.ToString()),
        task.Priority, task.Intent, task.ExpectedResult, task.Description,
        task.CreatedUtc!.Value, task.UpdatedUtc!.Value, task.DeviceId,
        JsonNamingPolicy.CamelCase.ConvertName(task.TargetKind.ToString()));

    private static EngineeringTaskRelationshipMutationApiResponse ToRelationshipMutation(GraphEdge edge) => new(
        edge.EdgeId, edge.FromId, JsonNamingPolicy.CamelCase.ConvertName(edge.ToKind.ToString()), edge.ToId,
        JsonNamingPolicy.CamelCase.ConvertName(edge.RelationKind.ToString()),
        JsonNamingPolicy.CamelCase.ConvertName(edge.Provenance.ToString()), edge.IsPrimary);

    /// <summary>
    /// Resolves a source-object id the graph holds no exact entity for, and registers its anchor
    /// (AC-005). Three forms are accepted, all naming the one identity the graph stores
    /// (<c>{deviceId}:{manifestId}</c>):
    /// <list type="bullet">
    /// <item>the exact <c>{deviceId}:{manifestId}</c> id, when nothing has put the object in the graph
    /// yet;</item>
    /// <item>the bare <c>{manifestId}</c> the source panel sends
    /// (<c>PlcSourcePanel.tsx</c>), resolved against the selected device;</item>
    /// <item>the reader's own <c>source:{relativePath}</c> block id
    /// (<see cref="DeviceSnapshotReader.ReadManifestSourceObjects"/> falls back to it for a manifest
    /// component without an id, and the block view's <c>OfflineBlockInfo.Id</c> has always been it),
    /// resolved against the selected device by path.</item>
    /// </list>
    /// A prefix is treated as a device id only when it names a device registered in the current
    /// selection, so a caller cannot name a device outside it; an id that is none of the three forms
    /// is refused with <c>GRAPH_ENTITY_ID_UNRESOLVED</c>, because "this id names something I cannot
    /// attribute" is not the answer "no such entity exists".
    /// </summary>
    private static GraphEntity? ResolveGraphEntityAnchor(
        string workbenchId,
        GraphEntityKind kind,
        string entityId,
        WorkbenchApiState state,
        EngineeringGraphService graph)
    {
        if (kind != GraphEntityKind.SourceObject) return null;
        var selection = state.Selection;
        if (selection?.WorkbenchId != workbenchId || selection.WorktreeId is null) return null;
        var separator = entityId.IndexOf(':');
        if (separator > 0)
        {
            var prefix = entityId[..separator];
            var suffix = DecodeId(entityId[(separator + 1)..]);
            if (TrySelectedDevice(state, prefix, out var prefixed))
                return RegisterListedSourceObject(graph, workbenchId, prefixed, suffix);
            if (!string.Equals(prefix, SourceBlockIdPrefix, StringComparison.OrdinalIgnoreCase)
                || !TrySelectedDevice(state, selection.DeviceId, out var byPath))
            {
                throw new EngineeringGraphConstraintException(
                    $"Source object id '{entityId}' names the device or prefix '{prefix}', which is not a device of the current selection.",
                    "GRAPH_ENTITY_ID_UNRESOLVED");
            }

            return RegisterListedSourceObjectByPath(graph, workbenchId, byPath, suffix);
        }

        return TrySelectedDevice(state, selection.DeviceId, out var selected)
            ? RegisterListedSourceObject(graph, workbenchId, selected, DecodeId(entityId))
            : throw new EngineeringGraphConstraintException(
                $"Source object id '{entityId}' is a bare manifest id, which can only be resolved against a selected device.",
                "GRAPH_ENTITY_ID_UNRESOLVED");
    }

    /// <summary>
    /// The id as the Studio's URL encoding delivers it: a client encodes the whole id
    /// (<c>encodeURIComponent</c> in <c>client.ts</c>), and a slash inside a route value survives as
    /// <c>%2F</c> — route values are decoded, but never into a path separator — so the relative path of
    /// the reader's block id form arrives percent-encoded. Decoding is lenient: a value that is not a
    /// valid escape sequence is returned as it came.
    /// </summary>
    private static string DecodeId(string value)
    {
        if (!value.Contains('%')) return value;
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }

    /// <summary>The prefix of the reader's own block id form, <c>source:{relativePath}</c>.</summary>
    private const string SourceBlockIdPrefix = "source";

    /// <summary>
    /// The worktree selection boundary (ADR-0012 items 3-4, AC-004/AC-007), run in one graph scope:
    /// first the reconciliation pass — a device a write point flagged is re-projected, and the facts of
    /// worktrees the workbench no longer registers are removed — then the digest check that makes sure
    /// every device of the selected worktree is projected from the manifest on disk before it is served.
    /// A projection failure surfaces as a projection failure; the selection itself is already recorded.
    /// </summary>
    private static void ReconcileWorktreeSelection(
        WorkbenchApiState state,
        EngineeringGraphApiFactory graphs,
        WorkbenchMetadata workbench,
        string worktreeId)
    {
        using var scope = graphs.Open(workbench);
        var reconciliation = new EngineeringGraphReconciliation(scope.Service);
        reconciliation.RemoveDeletedWorktreeFacts();
        var reader = new DeviceSnapshotGraphReader(scope.Service);
        foreach (var summary in state.ListDevices(workbench.WorkbenchId, worktreeId))
        {
            var device = state.Device(workbench.WorkbenchId, worktreeId, summary.DeviceId);
            // The repair covers the flagged case without a manifest read; the boundary then compares the
            // stored digest with the manifest on disk for the changes no event reported.
            reconciliation.RepairDevice(device.Context, device.Metadata);
            reader.EnsureProjectionCurrent(device.Context, device.Metadata);
        }
    }

    /// <summary>The device of the current selection, or false when the workbench, worktree or device it
    /// names is not registered there. Every projection read stays inside that boundary.</summary>
    private static bool TrySelectedDevice(
        WorkbenchApiState state,
        string? deviceId,
        out (DeviceContext Context, DeviceMetadata Metadata) device)
    {
        device = default;
        if (string.IsNullOrWhiteSpace(deviceId)) return false;
        try
        {
            device = state.Device(deviceId);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Registers the graph anchor of a device source object the device's projection lists, and returns
    /// it, so a read of a legitimate object does not fail just because nothing has put it in the graph
    /// yet.
    /// </summary>
    /// <remarks>
    /// A source object enters the graph as a side effect of the device projection (and of a commit's
    /// evidence), and a freshly created project has done neither, so every source object in it answered
    /// <c>GRAPH_ENTITY_NOT_FOUND</c> — which the source browser shows as an error on every click. The
    /// object is looked up in the projection — projecting the device first when it has none — so this
    /// fallback reads no manifest and no XML (ADR-0011). The entity is an anchor for edges rather than
    /// evidence of its own, so registering it on first read is safe and costs one idempotent insert.
    /// Only the device of the selection the caller is looking at is consulted: the entity id carries a
    /// device id but no worktree, and a workbench may register the same device in more than one
    /// worktree.
    /// </remarks>
    private static GraphEntity? RegisterListedSourceObject(
        EngineeringGraphService graph,
        string workbenchId,
        (DeviceContext Context, DeviceMetadata Metadata) device,
        string manifestId)
    {
        var entityId = $"{device.Context.DeviceId}:{manifestId}";
        // The projection registers every object it lists, so the identity the caller named is usually
        // already an entity: returning it costs one read instead of a projection-validity check.
        if (graph.GetEntity(GraphEntityKind.SourceObject, entityId) is { } existing) return existing;
        if (!new DeviceSnapshotGraphReader(graph)
                .TryGetListedSourceObject(device.Context, device.Metadata, manifestId, out var relativePath))
            return null;
        return RegisterSourceObjectAnchor(graph, workbenchId, device, entityId, relativePath);
    }

    /// <summary>The same anchor, addressed by the reader's own block id form's relative path.</summary>
    private static GraphEntity? RegisterListedSourceObjectByPath(
        EngineeringGraphService graph,
        string workbenchId,
        (DeviceContext Context, DeviceMetadata Metadata) device,
        string relativePath)
    {
        var listed = new DeviceSnapshotGraphReader(graph)
            .FindListedSourceObjectByPath(device.Context, device.Metadata, relativePath);
        return listed is { } found
            ? RegisterSourceObjectAnchor(graph, workbenchId, device, found.EntityId, found.RelativePath)
            : null;
    }

    private static GraphEntity RegisterSourceObjectAnchor(
        EngineeringGraphService graph,
        string workbenchId,
        (DeviceContext Context, DeviceMetadata Metadata) device,
        string entityId,
        string relativePath)
    {
        var anchor = new GraphEntity(GraphEntityKind.SourceObject, entityId, workbenchId,
            device.Context.WorktreeId, device.Context.DeviceId, relativePath);
        graph.RegisterEntity(anchor);
        return anchor;
    }

    /// <summary>
    /// The entity kinds the graph-entity route serves. <c>task</c>, <c>device</c> and <c>worktree</c>
    /// are deliberately absent (AC-005's "add the task kind or document its absence"): this parser is
    /// also the relationship routes' <c>targetKind</c> parser, so accepting <c>task</c> would let a
    /// relationship target a task node, and the route's response can only express relationships that
    /// point <em>into</em> the entity — a task's associations are outgoing and are already served by
    /// <c>GET /api/workbenches/{id}/tasks/{taskId}</c>, whose response has the same fields. A
    /// <c>device</c> or <c>worktree</c> node has no inbound edge kind at all (a task binds a device
    /// through the task row, not through an edge), so mapping them would answer with an entity whose
    /// three relationship arrays are structurally always empty. No client asks for either kind: the
    /// Studio's two graph-entity call sites send <c>sourceObject</c> and <c>git_commit</c>.
    /// </summary>
    private static GraphEntityKind ParseGraphEntityKind(string value) => value.Trim().ToLowerInvariant() switch
    {
        "session" => GraphEntityKind.Session,
        "commit" or "gitcommit" or "git_commit" => GraphEntityKind.GitCommit,
        "source" or "sourceobject" or "source_object" => GraphEntityKind.SourceObject,
        "svn" or "svnrevision" or "svn_revision" => GraphEntityKind.SvnRevision,
        _ => throw new EngineeringGraphConstraintException($"Unsupported graph entity kind '{value}'."),
    };

    private static string? ResolveSessionTaskId(
        WorkbenchApiState state,
        EngineeringGraphApiFactory graphs,
        ActiveTaskContextService activeTasks,
        string workbenchId,
        string? worktreeId,
        string? requestedTaskId)
    {
        using var scope = graphs.Open(state.Workbench(workbenchId));
        if (string.IsNullOrWhiteSpace(requestedTaskId))
            return activeTasks.Get(scope.Service, worktreeId)?.TaskId;

        var task = scope.Service.FindTask(requestedTaskId)
            ?? throw new EngineeringGraphConstraintException("The selected task was not found in the current Workbench.");
        if (task.ScopeKind == GraphTaskScopeKind.Worktree && (worktreeId is null || task.WorktreeId != worktreeId))
            throw new EngineeringGraphConstraintException(
                "The selected task is not compatible with the current project or Workbench context.");
        return task.TaskId;
    }

    /// <summary>
    /// Names the selected source paths that are not staged on the committing task — and, for each one
    /// another task of this worktree owns, that task — so a refused task commit says what to release
    /// instead of only that something was wrong. Both recorded path forms are resolved (015).
    /// </summary>
    private static string DescribeUnstagedPaths(
        IReadOnlyList<string> paths,
        string worktreeId,
        string sourceRoot,
        EngineeringGraphService graph)
    {
        var owners = graph.ListWorktreeActiveStages(worktreeId)
            .Select(item => new
            {
                item.TaskTitle,
                ObjectPath = graph.GetEntity(GraphEntityKind.SourceObject, item.Stage.SourceObjectId)?.ExternalRef,
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.ObjectPath) && !string.IsNullOrWhiteSpace(item.TaskTitle))
            .ToArray();

        return string.Join(", ", paths.Select(path =>
        {
            var owner = owners.FirstOrDefault(item => SourcePathForms.Matches(sourceRoot, item.ObjectPath!, path));
            return owner is null ? path : $"{path} (staged on '{owner.TaskTitle}')";
        }));
    }

    private static IResult ToEngineeringTaskDetailResult(EngineeringGraphService graph, string taskId)
    {
        var task = graph.FindTask(taskId) ?? throw new KeyNotFoundException("TASK_NOT_FOUND");
        static EngineeringTaskRelationshipApiResponse[] Relationships(
            EngineeringGraphService graph, string taskId, GraphEntityKind kind) =>
            graph.GetEdges(GraphEntityKind.Task, taskId, kind)
                .Select(edge => new EngineeringTaskRelationshipApiResponse(
                    edge.ToId, edge.EdgeId, JsonNamingPolicy.CamelCase.ConvertName(edge.Provenance.ToString()), edge.IsPrimary))
                .ToArray();
        return Results.Ok(new EngineeringTaskDetailApiResponse(
            ToEngineeringTaskResponse(task),
            Relationships(graph, taskId, GraphEntityKind.Session),
            Relationships(graph, taskId, GraphEntityKind.GitCommit),
            Relationships(graph, taskId, GraphEntityKind.SourceObject),
            Relationships(graph, taskId, GraphEntityKind.SvnRevision)));
    }

    private static bool TryGetOptionalInt(JsonElement body, string name, out int value)
    {
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out value)) return true;
        value = default;
        return false;
    }

    private static async Task<WorkbenchLandingCard> BuildLandingCard(
        WorkbenchMetadata workbench,
        WorkbenchTagService tags,
        AtomicJsonStore store,
        WorktreeTaskStore tasks,
        ApiMcpGateway gateway,
        CancellationToken cancellationToken)
    {
        var effectiveTags = Array.Empty<string>();
        try { effectiveTags = tags.GetWorkbenchTags(workbench.WorkbenchId).EffectiveTagIds.ToArray(); }
        catch (WorkbenchTagDomainException) { }
        var activity = ParseActivity(workbench.UpdatedAt ?? workbench.CreatedAt);
        var summaries = new List<WorktreeLandingSummary>();
        foreach (var registration in workbench.Worktrees)
        {
            var root = WorkbenchPaths.ResolveWorktree(workbench.RootPath, registration.RelativePath);
            try
            {
                var metadata = store.Read<WorktreeMetadata>(Path.Combine(root, "worktree.json"));
                var taskList = tasks.Load(root);
                int dirty = 0;
                var statusAvailable = true;
                DateTimeOffset? dirtyActivity = null;
                try
                {
                    var status = await gateway.For("vc_status").CallAsync<JsonElement>(
                        "vc_status", new { repoPath = root }, cancellationToken);
                    var entries = status.TryGetProperty("entries", out var statusEntries)
                        ? statusEntries.EnumerateArray().ToArray() : Array.Empty<JsonElement>();
                    dirty = entries.Length;
                    foreach (var entry in entries)
                    {
                        var relativePath = entry.TryGetProperty("filePath", out var filePath) ? filePath.GetString() ?? string.Empty : string.Empty;
                        var source = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
                        var candidate = source;
                        while (!File.Exists(candidate) && !Directory.Exists(candidate))
                        {
                            candidate = Path.GetDirectoryName(candidate) ?? root;
                        }
                        var stamp = File.GetLastWriteTimeUtc(candidate);
                        if (stamp != DateTime.MinValue)
                        {
                            var value = new DateTimeOffset(DateTime.SpecifyKind(stamp, DateTimeKind.Utc));
                            if (dirtyActivity is null || value > dirtyActivity) dirtyActivity = value;
                        }
                    }
                }
                catch { dirty = 0; statusAvailable = false; }
                if (!statusAvailable)
                {
                    summaries.Add(new WorktreeLandingSummary(registration.WorktreeId, metadata.Name, metadata.Branch,
                        metadata.CreatedAt, metadata.UpdatedAt, null, null, null, null, "unavailable"));
                    continue;
                }
                var sessionDirectory = SessionManager.SessionsDirectory(root);
                var sessions = Directory.Exists(sessionDirectory)
                    ? Directory.EnumerateFiles(sessionDirectory, "*.json").Count()
                    : 0;
                var wtActivity = ParseActivity(metadata.UpdatedAt ?? metadata.CreatedAt);
                if (dirtyActivity is not null && (wtActivity is null || dirtyActivity > wtActivity)) wtActivity = dirtyActivity;
                if (wtActivity is not null && (activity is null || wtActivity > activity)) activity = wtActivity;
                summaries.Add(new WorktreeLandingSummary(registration.WorktreeId, metadata.Name, metadata.Branch,
                    metadata.CreatedAt, metadata.UpdatedAt, taskList.Tasks.Count(t => t.Status == WorktreeTaskStatus.Done),
                    taskList.Tasks.Count, dirty, sessions, "available"));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or MetadataSchemaException)
            {
                summaries.Add(new WorktreeLandingSummary(registration.WorktreeId, registration.Name, registration.Branch,
                    null, null, null, null, null, null, "unavailable"));
            }
        }
        return new WorkbenchLandingCard(workbench.WorkbenchId, workbench.Name, workbench.CreatedAt,
            workbench.UpdatedAt, activity?.ToString("O"), workbench.Purpose, workbench.Owner,
            workbench.CoverAssetId, effectiveTags, summaries);
    }

    private static DateTimeOffset? ParseActivity(string? value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;

    private static (string Extension, bool Valid) DetectCover(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 8 && header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return (".png", true);
        if (header.Length >= 3 && header[..3].SequenceEqual(new byte[] { 255, 216, 255 })) return (".jpg", true);
        if (header.Length >= 6 && (header[..6].SequenceEqual("GIF87a"u8) || header[..6].SequenceEqual("GIF89a"u8))) return (".gif", true);
        if (header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8) && header.Slice(8, 4).SequenceEqual("WEBP"u8)) return (".webp", true);
        return (string.Empty, false);
    }

    private static string DetectCoverContentType(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", _ => "application/octet-stream",
    };

    private static WorktreeDetailResponse ToDetail(WorktreeMetadata worktree) => new(
        worktree.WorktreeId,
        worktree.WorkbenchId,
        worktree.Name,
        worktree.Branch,
        worktree.CreatedAt,
        worktree.BaseCommit,
        worktree.EngineeringProjectId,
        worktree.SourceProjectPath,
        worktree.DeviceIds,
        worktree.LastReconciliationCommit,
        worktree.Purpose,
        worktree.Owner,
        worktree.Status,
        worktree.FinishedUtc);

    private static EntityTagsApiResponse ToEntityTagsResponse(WorkbenchTagProjection tags) => new(
        tags.DirectTagIds,
        tags.InheritedTagIds,
        tags.EffectiveTagIds);

    private static WorkbenchTagSearchResultApiResponse ToSearchResultResponse(WorkbenchTagSearchResult result) => new(
        result.EntityType,
        result.EntityId,
        result.WorkbenchId,
        result.DirectTagIds,
        result.EffectiveTagIds,
        result.Available);

    /// <summary>PATCH semantics: false when the field is omitted (leave unchanged); true when
    /// present — a JSON null clears the value, any string (including empty) sets it.</summary>
    private static bool TryGetOptionalString(JsonElement body, string name, out string? value)
    {
        value = null;
        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty(name, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw new ArgumentException($"Field '{name}' must be a string or null.");
        }

        value = property.GetString();
        return true;
    }

    private static bool TryGetOptionalStringArray(JsonElement body, string name, out string[] value)
    {
        value = [];
        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty(name, out var property))
        {
            return false;
        }

        if (property.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException($"Field '{name}' must be an array of strings or null.");
        }

        value = property.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String
                ? item.GetString()!
                : throw new ArgumentException($"Field '{name}' must be an array of strings or null."))
            .ToArray();
        return true;
    }

    private static bool TryGetOptionalEnum<TEnum>(JsonElement body, string name, out TEnum value)
        where TEnum : struct, Enum
    {
        value = default;
        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty(name, out var property)
            || property.ValueKind == JsonValueKind.Null)
        {
            return false;
        }

        if (property.ValueKind != JsonValueKind.String
            || !Enum.TryParse(property.GetString(), ignoreCase: true, out value)
            || !Enum.IsDefined(value))
        {
            throw new ArgumentException(
                $"Field '{name}' must be one of: {string.Join(", ", Enum.GetNames<TEnum>())}.");
        }

        return true;
    }

    private static async Task<T> RunOperationAsync<T>(
        HttpContext http,
        OperationStatusRegistry operations,
        string operationType,
        string initialMessage,
        Func<IOperationProgress?, Task<T>> action,
        string successMessage)
    {
        var operationId = http.Request.Headers["X-Operation-Id"].FirstOrDefault();
        IOperationProgress? progress = null;
        if (!string.IsNullOrWhiteSpace(operationId))
        {
            operations.Start(operationId, operationType, initialMessage);
            progress = operations.For(operationId);
        }

        try
        {
            var result = await action(progress).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(operationId))
            {
                operations.Succeed(operationId, successMessage);
            }

            return result;
        }
        catch (Exception exception)
        {
            if (!string.IsNullOrWhiteSpace(operationId))
            {
                var lastMessage = operations.TryGet(operationId, out var snapshot)
                    ? snapshot.Message
                    : initialMessage;
                operations.Fail(operationId, lastMessage, exception.Message);
            }

            throw;
        }
    }
}

public sealed class WorkbenchApiExceptionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try { await next(context); }
        catch (AppAssistantGatewayException exception)
        {
            context.Response.StatusCode = exception.StatusCode;
            await context.Response.WriteAsJsonAsync(new { error = exception.Code, message = exception.Message });
        }
        catch (RuntimeStateConflictException exception)
        {
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            await context.Response.WriteAsJsonAsync(new
            {
                error = exception.Code,
                expectedRevision = exception.ExpectedRevision,
                actualRevision = exception.ActualRevision,
            });
        }
        catch (KeyNotFoundException exception)
        {
            context.Response.StatusCode = exception.Message.Contains("PREVIEW", StringComparison.Ordinal) ? 409 : 404;
            await context.Response.WriteAsJsonAsync(new { error = exception.Message });
        }
        catch (WorkbenchTagDomainException exception)
        {
            context.Response.StatusCode = exception.Code switch
            {
                "tag_not_found" or "workbench_not_found" or "worktree_not_found" => StatusCodes.Status404NotFound,
                "sibling_name_conflict" or "tag_has_children" or "tag_assigned" => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status400BadRequest,
            };
            await context.Response.WriteAsJsonAsync(new { error = exception.Code, message = exception.Message });
        }
        catch (Exception exception) when (exception is ArgumentException or WorkbenchPathException)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = exception.Message });
        }
        catch (SandboxException exception)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new
            {
                error = exception.Code,
                message = exception.Message,
                remediation = exception.Remediation,
            });
        }
        catch (ToolCallException exception)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new
            {
                error = exception.Code,
                message = exception.Message,
                remediation = exception.Remediation,
            });
        }
        catch (Exception exception) when (exception is WorkbenchCatalogException or WorkbenchLifecycleException)
        {
            var code = exception is WorkbenchCatalogException catalog ? catalog.Code : ((WorkbenchLifecycleException)exception).Code;
            context.Response.StatusCode = code.Contains("CONFLICT", StringComparison.Ordinal) || code.Contains("STALE", StringComparison.Ordinal) ? 409 : 400;
            await context.Response.WriteAsJsonAsync(new { error = code, message = exception.Message });
        }
        catch (ReconciliationException exception)
        {
            context.Response.StatusCode = exception.Code.Contains("STALE", StringComparison.Ordinal)
                || exception.Code.Contains("APPROVAL", StringComparison.Ordinal) ? 409 : 400;
            await context.Response.WriteAsJsonAsync(new { error = exception.Code, message = exception.Message });
        }
        catch (MetadataSchemaException exception)
        {
            context.Response.StatusCode = 409;
            await context.Response.WriteAsJsonAsync(new { error = "METADATA_SCHEMA_UNSUPPORTED", message = exception.Message });
        }
        catch (EngineeringGraphProjectionException exception)
        {
            // ADR-0012: a projection that runs at a selection boundary and fails is reported as a
            // projection failure, never as a read failure and never as stale facts served successfully.
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new { error = exception.Code, message = exception.Message });
        }
        catch (EngineeringGraphConstraintException exception)
        {
            context.Response.StatusCode = exception.Code.Contains("EXISTS", StringComparison.Ordinal)
                ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = exception.Code, message = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = exception.Message });
        }
    }
}
