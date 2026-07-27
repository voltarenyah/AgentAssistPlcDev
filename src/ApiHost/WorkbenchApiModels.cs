using System.Collections.Concurrent;
using Agent.Chat;
using Agent.Workbench;

public sealed record WorkbenchSelection(string WorkbenchId, string? WorktreeId, string? DeviceId);
public sealed record CreateWorkbenchApiRequest(string Name, string? RootPath, string EngineeringProjectPath);
public sealed record OpenWorkbenchApiRequest(string RootPath);
public sealed record CreateWorktreeApiRequest(string Name, string Branch, string? StartPoint);
public sealed record RefreshApplyApiRequest(string PreviewId, string[]? ApprovedRemovalPaths);
public sealed record SourcePathApiRequest(string RelativePath);
public sealed record MergeWorktreeApiRequest(string TargetWorktreeId);
public sealed record SessionCreateApiRequest(Agent.Chat.ChatRequestSettings Settings, string? RuntimeContext);
public sealed record SessionSaveApiRequest(ChatSessionData Session);

public sealed class WorkbenchApiState
{
    private readonly WorkbenchCatalog catalog;
    private readonly AtomicJsonStore store;
    private readonly ConcurrentDictionary<string, WorkbenchMetadata> workbenches = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ReconciliationPreview> previews = new(StringComparer.Ordinal);

    public WorkbenchApiState(WorkbenchCatalog catalog, AtomicJsonStore store)
    {
        this.catalog = catalog;
        this.store = store;
        foreach (var item in catalog.ListDefaultRoot()) workbenches[item.WorkbenchId] = item;
    }

    public WorkbenchSelection? Selection { get; private set; }
    public IReadOnlyList<WorkbenchMetadata> List() => workbenches.Values.OrderBy(x => x.Name).ToArray();
    public WorkbenchMetadata Add(WorkbenchMetadata value) { workbenches[value.WorkbenchId] = value; return value; }
    public WorkbenchMetadata Open(string root) => Add(catalog.Load(root));
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
        var wb = Workbench(selection.WorkbenchId);
        var wt = Worktree(wb.WorkbenchId, selection.WorktreeId);
        if (!wt.DeviceIds.Contains(deviceId, StringComparer.Ordinal)) throw new KeyNotFoundException("DEVICE_NOT_FOUND");
        var reg = wb.Worktrees.Single(x => x.WorktreeId == wt.WorktreeId);
        var wtRoot = WorkbenchPaths.ResolveWorktree(wb.RootPath, reg.RelativePath);
        var metadataPath = Directory.EnumerateFiles(Path.Combine(wtRoot, "devices"), "device.json", SearchOption.AllDirectories)
            .Select(path => (path, device: store.Read<DeviceMetadata>(path))).Single(x => x.device.DeviceId == deviceId);
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
        app.MapGet("/api/workbenches", (WorkbenchApiState s) => s.List());
        app.MapPost("/api/workbenches/open", (OpenWorkbenchApiRequest r, WorkbenchApiState s) => s.Open(r.RootPath));
        app.MapPost("/api/workbenches", async (CreateWorkbenchApiRequest r, WorkbenchCoordinator c, WorkbenchApiState s, CancellationToken ct) =>
            s.Add((await c.CreateWorkbenchAsync(new(r.Name, r.RootPath, r.EngineeringProjectPath), ct)).Workbench));
        app.MapGet("/api/workbenches/{id}", (string id, WorkbenchApiState s) => s.Workbench(id));
        app.MapPost("/api/workbenches/{id}/select", (string id, WorkbenchApiState s) => { s.Workbench(id); s.Select(id); return Results.NoContent(); });
        app.MapGet("/api/workbenches/{id}/worktrees", (string id, WorkbenchApiState s) => s.Workbench(id).Worktrees);
        app.MapPost("/api/workbenches/{id}/worktrees", async (string id, CreateWorktreeApiRequest r, WorkbenchApiState s, WorkbenchCoordinator c, CancellationToken ct) =>
            await c.CreateWorktreeAsync(new(s.Workbench(id), r.Name, r.Branch, r.StartPoint), ct));
        app.MapPost("/api/workbenches/{id}/worktrees/{wt}/select", (string id, string wt, WorkbenchApiState s) => { s.Worktree(id, wt); s.Select(id, wt); return Results.NoContent(); });
        app.MapGet("/api/workbenches/{id}/worktrees/{wt}/devices", (string id, string wt, WorkbenchApiState s) => s.Worktree(id, wt).DeviceIds);
        app.MapPost("/api/workbenches/{id}/worktrees/{wt}/devices/{device}/select", (string id, string wt, string device, WorkbenchApiState s) => { s.Select(id, wt); s.Device(device); s.Select(id, wt, device); return Results.NoContent(); });
        app.MapPost("/api/devices/{device}/refresh/stage", async (string device, WorkbenchApiState s, WorkbenchCoordinator c, CancellationToken ct) => await c.StageRefreshAsync(s.Device(device).Context, ct));
        app.MapGet("/api/devices/{device}/refresh/preview", (string device, WorkbenchApiState s, WorkbenchCoordinator c) => { var p = c.PreviewRefresh(s.Device(device).Context); s.Remember(p); return p; });
        app.MapPost("/api/devices/{device}/refresh/apply", async (string device, RefreshApplyApiRequest r, WorkbenchApiState s, WorkbenchCoordinator c, CancellationToken ct) =>
            await c.ApplyRefreshAsync(s.Device(device).Context, new(s.Take(r.PreviewId, device), new HashSet<string>(r.ApprovedRemovalPaths ?? [], StringComparer.Ordinal)), ct));
        app.MapPost("/api/devices/{device}/knowledge/update", async (string device, WorkbenchApiState s, WorkbenchCoordinator c, CancellationToken ct) => await c.UpdateKnowledgeAsync(s.Device(device).Context, ct));
        app.MapPost("/api/devices/{device}/knowledge/rebuild", async (string device, WorkbenchApiState s, WorkbenchCoordinator c, CancellationToken ct) => await c.UpdateKnowledgeAsync(s.Device(device).Context, ct));
        app.MapPost("/api/devices/{device}/source/prepare-edit", (string device, SourcePathApiRequest r, WorkbenchApiState s, DeviceSourceResolver resolver) => resolver.PrepareEditable(s.Device(device).Context, r.RelativePath));
        app.MapPost("/api/devices/{device}/source/import", async (string device, SourcePathApiRequest r, WorkbenchApiState s, WorkbenchCoordinator c, CancellationToken ct) => await c.ImportModifiedAsync(s.Device(device).Context, r.RelativePath, ct));
        app.MapPost("/api/worktrees/{source}/merge", async (string source, MergeWorktreeApiRequest r, WorkbenchApiState s, WorkbenchCoordinator c) =>
        {
            var workbenchId = s.Selection?.WorkbenchId ?? throw new InvalidOperationException("WORKBENCH_SELECTION_REQUIRED");
            return await c.MergeWorktreeAsync(workbenchId, source, r.TargetWorktreeId);
        });
        app.MapGet("/api/devices/{device}/sessions", (string device, WorkbenchApiState s) => SessionManager.ListSessions(s.Device(device).Context));
        app.MapPost("/api/devices/{device}/sessions", (string device, SessionCreateApiRequest r, WorkbenchApiState s) => SessionManager.CreateNewSession(s.Device(device).Context, r.Settings, r.RuntimeContext));
        app.MapGet("/api/devices/{device}/sessions/{session}", (string device, string session, WorkbenchApiState s) => SessionManager.LoadSession(s.Device(device).Context, session) is { } value ? Results.Ok(value) : Results.NotFound());
        app.MapPut("/api/devices/{device}/sessions/{session}", (string device, string session, SessionSaveApiRequest r, WorkbenchApiState s) => { if (r.Session.Header.SessionId != session) return Results.BadRequest(); SessionManager.SaveSession(s.Device(device).Context, r.Session); return Results.NoContent(); });
        return app;
    }
}
