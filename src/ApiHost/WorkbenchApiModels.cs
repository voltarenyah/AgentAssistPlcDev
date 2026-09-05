using System.Collections.Concurrent;
using System.Text.Json;
using Agent.Chat;
using Agent.Mcp;
using Agent.Workbench;
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
public sealed record CreateWorktreeApiRequest(string Name, string Branch, string? StartPoint);
public sealed record RefreshApplyApiRequest(
    string PreviewId,
    string[]? ApprovedPaths,
    string[]? ApprovedRemovalPaths = null,
    string? CommitMessage = null);
public sealed record SourcePathApiRequest(string RelativePath);
public sealed record CommitSourceApiRequest(string[] Paths, string Message, bool UntrackableChange, bool SafetyChange = false);
public sealed record RestoreTiaProjectApiRequest(string? GitCommit = null);
public sealed record NativeSavepointApiRequest(string Message);
public sealed record TiaSynchronizationAcceptApiRequest(string[] Paths, string Message);
public sealed record TiaValidationApiRequest(string ConfirmedBy);
public sealed record UnauthorizedMasterPathsRequest(string[] Paths, string? FeatureName = null, bool Confirm = false);
public sealed record FeaturePathsApiRequest(string[] Paths);
public sealed record ValidateFeatureMergeApiRequest(string ImportSessionId, bool MachineValidated, string ConfirmedBy);
public sealed record RollbackFeatureApiRequest(string HistoricalSha, string[] Paths, string FeatureName);

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
    string[]? ElementRefs);

/// <summary>Device list entry: opaque object id plus the human-readable PLC name from device.json.</summary>
public sealed record DeviceSummary(string DeviceId, string PlcName);
public sealed record MergeWorktreeApiRequest(string TargetWorktreeId);
public sealed record SessionCreateApiRequest(Agent.Chat.ChatRequestSettings Settings, string? RuntimeContext);
public sealed record SessionSaveApiRequest(ChatSessionData Session);

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
        app.MapGet("/api/workbenches", (WorkbenchApiState s, WorkbenchCoordinator coordinator) =>
        {
            var workbenches = s.List();
            foreach (var workbench in workbenches)
                coordinator.RegisterWorkbench(workbench);
            return workbenches;
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
        app.MapDelete("/api/workbenches/{id}", async (
            string id,
            WorkbenchApiState s,
            WorkbenchCoordinator c,
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
                    return new { deleted = true };
                },
                "Workbench deleted.").ConfigureAwait(false);
            s.Remove(id);
            return result;
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
        app.MapPost("/api/workbenches/{id}/worktrees/{wt}/tasks", (
            string id,
            string wt,
            CreateWorktreeTaskApiRequest r,
            WorkbenchApiState s,
            WorktreeTaskStore tasks) =>
        {
            var task = tasks.Add(s.WorktreeRoot(id, wt), r.Title, r.Details, r.ElementRefs);
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
                progress => c.CreateWorktreeAsync(new(s.Workbench(id), r.Name, r.Branch, r.StartPoint), ct, progress),
                "Worktree created.").ConfigureAwait(false);
            s.Refresh(id);
            return result;
        });
        app.MapDelete("/api/workbenches/{id}/worktrees/{wt}", async (
            string id,
            string wt,
            WorkbenchApiState s,
            WorkbenchCoordinator c,
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
                    return new { deleted = true };
                },
                "Worktree removed.").ConfigureAwait(false);
            s.Refresh(id);
            if (s.Selection?.WorktreeId == wt)
                s.Select(id);
            return result;
        });
        app.MapPost("/api/workbenches/{id}/worktrees/{wt}/select", (string id, string wt, WorkbenchApiState s, WorkbenchCoordinator coordinator) =>
        {
            var workbench = s.Workbench(id);
            coordinator.RegisterWorkbench(workbench);
            s.Worktree(id, wt);
            s.Select(id, wt);
            return Results.NoContent();
        });
        app.MapGet("/api/workbenches/{id}/worktrees/{wt}/devices", (string id, string wt, WorkbenchApiState s) => s.ListDevices(id, wt));
        app.MapGet("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/hardware", (
            string workbenchId,
            string worktreeId,
            WorkbenchApiState s) =>
            Results.Ok(HardwareConfigurationReader.Read(s.WorktreeRoot(workbenchId, worktreeId))));
        app.MapGet("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/hardware/bom", (
            string workbenchId,
            string worktreeId,
            WorkbenchApiState s) =>
            Results.Ok(HardwareListReader.ReadBom(s.WorktreeRoot(workbenchId, worktreeId))));
        app.MapGet("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/hardware/network", (
            string workbenchId,
            string worktreeId,
            WorkbenchApiState s) =>
            Results.Ok(HardwareListReader.ReadNetwork(s.WorktreeRoot(workbenchId, worktreeId))));
        app.MapPost("/api/workbenches/{id}/worktrees/{wt}/devices/{device}/select", (string id, string wt, string device, WorkbenchApiState s, WorkbenchCoordinator coordinator) =>
        {
            var workbench = s.Workbench(id);
            coordinator.RegisterWorkbench(workbench);
            s.Select(id, wt);
            s.Device(device);
            s.Select(id, wt, device);
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
            WorkbenchApiState s, WorkbenchCoordinator coordinator, ApiMcpGateway gateway, CancellationToken ct) =>
        {
            var root = s.WorktreeRoot(workbenchId, worktreeId);
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
                return Results.Ok(await coordinator.CommitSourceAsync(
                    workbenchId, worktreeId, body.Paths, body.Message, ct,
                    untrackableChange: body.UntrackableChange,
                    safetyChange: body.SafetyChange));
            }

            // Compatibility for an empty/legacy worktree: the version-control server still
            // validates the selected source paths. Real master XML files use the protected path above.
            return Results.Ok(await gateway.For("vc_commit_selected").CallAsync<System.Text.Json.JsonElement>(
                "vc_commit_selected", new { repoPath = root, paths = body.Paths, message = body.Message }, ct));
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
            WorkbenchApiState s, WorkbenchCoordinator coordinator, CancellationToken ct) =>
        {
            coordinator.RegisterWorkbench(s.Workbench(workbenchId));
            return Results.Ok(await coordinator.CreateNativeSavepointAsync(
                workbenchId, worktreeId, body.Message, ct));
        });
        app.MapPost("/api/workbenches/{workbenchId}/vc/compare-tia", async (
            string workbenchId,
            WorkbenchApiState state,
            WorkbenchCoordinator coordinator,
            OperationStatusRegistry operations,
            HttpContext http,
            CancellationToken ct,
            bool allowCompile = false,
            bool forceFullExport = false) =>
            await RunOperationAsync(
                http,
                operations,
                "compare-tia",
                "Comparing master with TIA Portal...",
                progress => coordinator.CompareMasterWithTiaAsync(workbenchId, ct, progress, allowCompile, forceFullExport),
                "TIA comparison completed.").ConfigureAwait(false));
        app.MapGet("/api/workbenches/{workbenchId}/vc/comparisons/{comparisonId}", (
            string workbenchId,
            string comparisonId,
            WorkbenchApiState state,
            WorkbenchCoordinator coordinator) =>
            coordinator.GetComparison(workbenchId, comparisonId));
        app.MapPost("/api/workbenches/{workbenchId}/vc/comparisons/{comparisonId}/accept", async (
            string workbenchId,
            string comparisonId,
            TiaSynchronizationAcceptApiRequest body,
            WorkbenchCoordinator coordinator,
            OperationStatusRegistry operations,
            HttpContext http,
            CancellationToken ct) =>
            await RunOperationAsync(
                http,
                operations,
                "accept-tia-synchronization",
                "Applying selected TIA source to master...",
                progress => coordinator.ApplyTiaSynchronizationAsync(workbenchId, comparisonId, body.Paths, body.Message, ct, progress),
                "Selected TIA source accepted.").ConfigureAwait(false));
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
            string workbenchId, string worktreeId, string device, WorkbenchApiState s, DeviceSnapshotReader snapshots) =>
        {
            var selected = s.Device(workbenchId, worktreeId, device);
            return Results.Ok(snapshots.Read(selected.Context, selected.Metadata));
        });
        app.MapGet("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/devices/{device}/blocks", (
            string workbenchId, string worktreeId, string device, WorkbenchApiState s, DeviceSnapshotReader snapshots) =>
        {
            var selected = s.Device(workbenchId, worktreeId, device);
            return Results.Ok(snapshots.Read(selected.Context, selected.Metadata).Blocks);
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
            string workbenchId, string worktreeId, string device, SessionCreateApiRequest r, WorkbenchApiState s) =>
            SessionManager.CreateNewSession(
                s.Device(workbenchId, worktreeId, device).Context, r.Settings, r.RuntimeContext));
        app.MapGet("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/devices/{device}/sessions/{session}", (
            string workbenchId, string worktreeId, string device, string session, WorkbenchApiState s) =>
            SessionManager.LoadSession(s.Device(workbenchId, worktreeId, device).Context, session) is { } value
                ? Results.Ok(value) : Results.NotFound());
        app.MapPut("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/devices/{device}/sessions/{session}", (
            string workbenchId, string worktreeId, string device, string session,
            SessionSaveApiRequest r, WorkbenchApiState s) =>
        {
            if (r.Session.Header.SessionId != session) return Results.BadRequest();
            SessionManager.SaveSession(s.Device(workbenchId, worktreeId, device).Context, r.Session);
            return Results.NoContent();
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
            WorkbenchApiState s, ApiMcpGateway gateway, CancellationToken ct) =>
            await gateway.For("vc_commit").CallAsync<System.Text.Json.JsonElement>(
                "vc_commit", new { repoPath = s.Device(workbenchId, worktreeId, device).Context.WorktreeRoot, message = body.Message }, ct));
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
        app.MapPost("/api/devices/{device}/sessions", (string device, SessionCreateApiRequest r, WorkbenchApiState s) => SessionManager.CreateNewSession(s.Device(device).Context, r.Settings, r.RuntimeContext));
        app.MapGet("/api/devices/{device}/sessions/{session}", (string device, string session, WorkbenchApiState s) => SessionManager.LoadSession(s.Device(device).Context, session) is { } value ? Results.Ok(value) : Results.NotFound());
        app.MapPut("/api/devices/{device}/sessions/{session}", (string device, string session, SessionSaveApiRequest r, WorkbenchApiState s) => { if (r.Session.Header.SessionId != session) return Results.BadRequest(); SessionManager.SaveSession(s.Device(device).Context, r.Session); return Results.NoContent(); });
        return app;
    }

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
        catch (InvalidOperationException exception)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { error = exception.Message });
        }
    }
}
