using System.Collections.Concurrent;
using Agent.Chat;
using Agent.Workbench;
using Contracts.Sandbox;

public sealed record WorkbenchSelection(string WorkbenchId, string? WorktreeId, string? DeviceId);
public sealed record CreateWorkbenchApiRequest(
    string Name,
    string? RootPath,
    int? EngineeringSessionId,
    string? EngineeringProjectPath);
public sealed record AttachTiaInstanceApiRequest(int SessionId);
public sealed record OpenTiaProjectApiRequest(bool WithUI = true);
public sealed record OpenWorkbenchApiRequest(string RootPath);
public sealed record CreateWorktreeApiRequest(string Name, string Branch, string? StartPoint);
public sealed record RefreshApplyApiRequest(
    string PreviewId,
    string[]? ApprovedPaths,
    string[]? ApprovedRemovalPaths = null);
public sealed record SourcePathApiRequest(string RelativePath);
public sealed record CommitSourceApiRequest(string[] Paths, string Message);
public sealed record TiaSynchronizationAcceptApiRequest(string[] Paths);
public sealed record TiaValidationApiRequest(string ConfirmedBy);
public sealed record UnauthorizedMasterPathsRequest(string[] Paths, string? FeatureName = null, bool Confirm = false);
public sealed record FeaturePathsApiRequest(string[] Paths);
public sealed record ValidateFeatureMergeApiRequest(string ImportSessionId, bool MachineValidated, string ConfirmedBy);
public sealed record RollbackFeatureApiRequest(string HistoricalSha, string[] Paths, string FeatureName);

/// <summary>Optional bootstrap body retained for request compatibility; CommitMessage is ignored because bootstrap never commits automatically.</summary>
public sealed record BootstrapApiRequest(string? CommitMessage);

/// <summary>Device list entry: opaque object id plus the human-readable PLC name from device.json.</summary>
public sealed record DeviceSummary(string DeviceId, string PlcName);
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
    public string WorktreeRoot(string workbenchId, string worktreeId)
    {
        var wb = Workbench(workbenchId);
        var registration = wb.Worktrees.SingleOrDefault(x => x.WorktreeId == worktreeId)
            ?? throw new KeyNotFoundException("WORKTREE_NOT_FOUND");
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
    public void Select(string wb, string? wt = null, string? device = null) => Selection = new(wb, wt, device);

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
        app.MapGet("/api/workbenches", (WorkbenchApiState s) => s.List());
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
        app.MapPost("/api/workbenches/{id}/worktrees/{wt}/select", (string id, string wt, WorkbenchApiState s) => { s.Worktree(id, wt); s.Select(id, wt); return Results.NoContent(); });
        app.MapGet("/api/workbenches/{id}/worktrees/{wt}/devices", (string id, string wt, WorkbenchApiState s) => s.ListDevices(id, wt));
        app.MapPost("/api/workbenches/{id}/worktrees/{wt}/devices/{device}/select", (string id, string wt, string device, WorkbenchApiState s) => { s.Select(id, wt); s.Device(device); s.Select(id, wt, device); return Results.NoContent(); });

        app.MapGet("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/vc/status", async (
            string workbenchId, string worktreeId, WorkbenchApiState s, ApiMcpGateway gateway, CancellationToken ct) =>
            await gateway.For("vc_status").CallAsync<System.Text.Json.JsonElement>(
                "vc_status", new { repoPath = s.WorktreeRoot(workbenchId, worktreeId) }, ct));
        app.MapGet("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/vc/log", async (
            string workbenchId, string worktreeId, int? maxCount, string? filePath,
            WorkbenchApiState s, ApiMcpGateway gateway, CancellationToken ct) =>
            await gateway.For("vc_log").CallAsync<System.Text.Json.JsonElement>(
                "vc_log", new { repoPath = s.WorktreeRoot(workbenchId, worktreeId), maxCount, filePath }, ct));
        app.MapGet("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/vc/diff", async (
            string workbenchId, string worktreeId, string filePath, string? oldSha, string? newSha,
            WorkbenchApiState s, ApiMcpGateway gateway, CancellationToken ct) =>
            await gateway.For("vc_diff").CallAsync<System.Text.Json.JsonElement>(
                "vc_diff", new { repoPath = s.WorktreeRoot(workbenchId, worktreeId), filePath, oldSha, newSha }, ct));
        app.MapPost("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/vc/commit", async (
            string workbenchId, string worktreeId, CommitSourceApiRequest body,
            WorkbenchApiState s, WorkbenchCoordinator coordinator, ApiMcpGateway gateway, CancellationToken ct) =>
        {
            var worktree = s.Worktree(workbenchId, worktreeId);
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
            if (string.Equals(worktree.Branch, "master", StringComparison.OrdinalIgnoreCase) && hasExistingSource)
            {
                return await coordinator.CommitSourceAsync(workbenchId, worktreeId, body.Paths, body.Message, ct);
            }

            // Compatibility for an empty/legacy worktree: the version-control server still
            // validates the selected source paths. Real master XML files use the protected path above.
            return await gateway.For("vc_commit_selected").CallAsync<System.Text.Json.JsonElement>(
                "vc_commit_selected", new { repoPath = root, paths = body.Paths, message = body.Message }, ct);
        });
        app.MapGet("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/vc/validation/{sha}", async (
            string workbenchId, string worktreeId, string sha,
            WorkbenchApiState s, ApiMcpGateway gateway, CancellationToken ct) =>
            await gateway.For("vc_validation_get").CallAsync<System.Text.Json.JsonElement>(
                "vc_validation_get", new { repoPath = s.WorktreeRoot(workbenchId, worktreeId), commitSha = sha }, ct));
        app.MapPost("/api/workbenches/{workbenchId}/vc/compare-tia", async (
            string workbenchId,
            WorkbenchApiState state,
            WorkbenchCoordinator coordinator,
            OperationStatusRegistry operations,
            HttpContext http,
            CancellationToken ct) =>
            await RunOperationAsync(
                http,
                operations,
                "compare-tia",
                "Comparing master with TIA Portal...",
                progress => coordinator.CompareMasterWithTiaAsync(workbenchId, ct, progress),
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
                progress => coordinator.ApplyTiaSynchronizationAsync(workbenchId, comparisonId, body.Paths, ct, progress),
                "Selected TIA source accepted.").ConfigureAwait(false));
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
            async (string id, string wt, string device, string? kind, WorkbenchApiState s, ApiMcpGateway gateway, CancellationToken ct) =>
                await KnowledgeQuery(s, gateway, id, wt, device, "query_nodes", new Dictionary<string, object?> { ["kind"] = kind }, ct));
        app.MapGet("/api/workbenches/{id}/worktrees/{wt}/devices/{device}/knowledge/edge-types",
            async (string id, string wt, string device, WorkbenchApiState s, ApiMcpGateway gateway, CancellationToken ct) =>
                await KnowledgeQuery(s, gateway, id, wt, device, "query_edge_types", new Dictionary<string, object?>(), ct));
        app.MapGet("/api/workbenches/{id}/worktrees/{wt}/devices/{device}/knowledge/edges",
            async (string id, string wt, string device, string? nodeId, string? type, WorkbenchApiState s, ApiMcpGateway gateway, CancellationToken ct) =>
                await KnowledgeQuery(s, gateway, id, wt, device, "query_edges", new Dictionary<string, object?> { ["nodeId"] = nodeId, ["type"] = type }, ct));
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
                            request?.WithUI ?? true)
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
                progress => c.ApplyRefreshAsync(selected.Context, new(preview, approved), ct, progress),
                "Refresh applied.").ConfigureAwait(false);
        });
        app.MapPost("/api/workbenches/{workbenchId}/worktrees/{worktreeId}/devices/{device}/bootstrap", async (
            string workbenchId, string worktreeId, string device, BootstrapApiRequest? _, WorkbenchApiState s,
            WorkbenchCoordinator c, OperationStatusRegistry operations, HttpContext http, CancellationToken ct,
            bool allowCompile = false) =>
            await RunOperationAsync(http, operations, "bootstrap-device", "Generating PLC context...",
                progress => c.BootstrapDeviceAsync(
                    s.Device(workbenchId, worktreeId, device).Context,
                    ct,
                    progress,
                    allowCompile),
                "PLC context generated.").ConfigureAwait(false));
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
