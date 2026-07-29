using System.Collections.Concurrent;
using Agent.Chat;
using Agent.Workbench;
using Contracts.Sandbox;

public sealed record WorkbenchSelection(string WorkbenchId, string? WorktreeId, string? DeviceId);
public sealed record CreateWorkbenchApiRequest(
    string Name,
    string? RootPath,
    int EngineeringSessionId,
    string EngineeringProjectPath);
public sealed record OpenWorkbenchApiRequest(string RootPath);
public sealed record CreateWorktreeApiRequest(string Name, string Branch, string? StartPoint);
public sealed record RefreshApplyApiRequest(
    string PreviewId,
    string[]? ApprovedPaths,
    string[]? ApprovedRemovalPaths = null);
public sealed record SourcePathApiRequest(string RelativePath);
public sealed record MergeWorktreeApiRequest(string TargetWorktreeId);
public sealed record SessionCreateApiRequest(Agent.Chat.ChatRequestSettings Settings, string? RuntimeContext);
public sealed record SessionSaveApiRequest(ChatSessionData Session);

public sealed class WorkbenchApiState
{
    private readonly WorkbenchCatalog catalog;
    private readonly AtomicJsonStore store;
    private readonly TrustedWorkbenchRootRegistry? trustedRoots;
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
        this.trustedRoots = trustedRoots;
        foreach (var item in catalog.ListDefaultRoot()) Add(item);
    }

    public WorkbenchSelection? Selection { get; private set; }
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
    public WorkbenchMetadata Refresh(string id) => Add(catalog.Load(Workbench(id).RootPath));
    public WorkbenchMetadata Open(string root) => Add(catalog.Load(root));

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
    public void Select(string wb, string? wt = null, string? device = null) => Selection = new(wb, wt, device);
    public void Remember(ReconciliationPreview preview) => previews[preview.PreviewId] = preview;
    public ReconciliationPreview Take(string id, string deviceId)
    {
        if (!previews.TryGetValue(id, out var preview) || preview.DeviceId != deviceId
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
        app.MapGet("/api/workbenches", (WorkbenchApiState s) => s.List());
        app.MapPost("/api/workbenches/open", (OpenWorkbenchApiRequest r, WorkbenchApiState s) => s.Open(r.RootPath));
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
        app.MapPost("/api/workbenches/{id}/select", (string id, WorkbenchApiState s) => { s.Workbench(id); s.Select(id); return Results.NoContent(); });
        app.MapGet("/api/workbenches/{id}/worktrees", (string id, WorkbenchApiState s) => s.Workbench(id).Worktrees);
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
        app.MapPost("/api/workbenches/{id}/worktrees/{wt}/select", (string id, string wt, WorkbenchApiState s) => { s.Worktree(id, wt); s.Select(id, wt); return Results.NoContent(); });
        app.MapGet("/api/workbenches/{id}/worktrees/{wt}/devices", (string id, string wt, WorkbenchApiState s) => s.Worktree(id, wt).DeviceIds);
        app.MapPost("/api/workbenches/{id}/worktrees/{wt}/devices/{device}/select", (string id, string wt, string device, WorkbenchApiState s) => { s.Select(id, wt); s.Device(device); s.Select(id, wt, device); return Results.NoContent(); });
        app.MapPost("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/devices/{device}/tia/open", async (
            string workbenchId,
            string worktreeId,
            string device,
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
                            progress)
                        .ConfigureAwait(false);
                    return new { opened = true };
                },
                "TIA project opened.").ConfigureAwait(false));
        app.MapPost("/api/devices/{device}/refresh/stage", async (
            string device,
            WorkbenchApiState s,
            WorkbenchCoordinator c,
            OperationStatusRegistry operations,
            HttpContext http,
            CancellationToken ct) =>
            await RunOperationAsync(
                http,
                operations,
                "stage-refresh",
                "Preparing export staging area...",
                progress => c.StageRefreshAsync(s.Device(device).Context, ct, progress),
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
                r.ApprovedPaths ?? [],
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
        app.MapPost("/api/devices/{device}/source/prepare-edit", (string device, SourcePathApiRequest r, WorkbenchApiState s, DeviceSourceResolver resolver) => resolver.PrepareEditable(s.Device(device).Context, r.RelativePath));
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
        app.MapGet("/api/devices/{device}/sessions", (string device, WorkbenchApiState s) => SessionManager.ListSessions(s.Device(device).Context));
        app.MapPost("/api/devices/{device}/sessions", (string device, SessionCreateApiRequest r, WorkbenchApiState s) => SessionManager.CreateNewSession(s.Device(device).Context, r.Settings, r.RuntimeContext));
        app.MapGet("/api/devices/{device}/sessions/{session}", (string device, string session, WorkbenchApiState s) => SessionManager.LoadSession(s.Device(device).Context, session) is { } value ? Results.Ok(value) : Results.NotFound());
        app.MapPut("/api/devices/{device}/sessions/{session}", (string device, string session, SessionSaveApiRequest r, WorkbenchApiState s) => { if (r.Session.Header.SessionId != session) return Results.BadRequest(); SessionManager.SaveSession(s.Device(device).Context, r.Session); return Results.NoContent(); });
        return app;
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
