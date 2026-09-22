using System.Text.Json;
using Agent.Mcp;
using Agent.Workbench;

namespace ApiHost.AppAssistant;

/// <summary>
/// Reads the workbench facts the Workbench Assistant panel shows. It exists for the in-process
/// <see cref="WorkbenchAssistantService"/>; the internal HTTP routes and the LangGraph worktree and
/// workbench mutation endpoints it used to serve the Python sidecar are gone (ADR-0005), and the
/// panel's destructive actions now go through the shared MCP tools and the AgentSandbox approval
/// card instead.
/// </summary>
public sealed class AppAssistantGateway(
    WorkbenchApiState state,
    WorkbenchRuntimeStateCoordinator runtime,
    WorktreeTaskStore tasks,
    ApiMcpGateway mcp)
{
    public Task<AppAssistantWorkbenchContext> GetContextAsync(string workbenchId)
    {
        var workbench = state.RefreshRuntimeIfChanged(workbenchId);
        var snapshot = runtime.GetSnapshot(workbenchId);

        var focus = state.Selection?.WorkbenchId == workbenchId ? state.Selection : null;
        var actions = snapshot.AvailableActions;
        return Task.FromResult(new AppAssistantWorkbenchContext(
            workbench.WorkbenchId,
            workbench.Name,
            snapshot,
            focus,
            actions,
            snapshot.ObservedAt,
            null));
    }

    public Task<WorktreeTodosResponse> GetTodosAsync(
        string workbenchId,
        string worktreeId,
        int? limit = null)
    {
        var count = ValidateLimit(limit, 20, 100);
        var worktreeRoot = state.WorktreeRoot(workbenchId, worktreeId);
        var items = tasks.Load(worktreeRoot).Tasks.Take(count).ToArray();
        runtime.ObserveTodos(workbenchId, worktreeId, items.Length);
        var snapshot = runtime.GetSnapshot(workbenchId);
        return Task.FromResult(new WorktreeTodosResponse(
            workbenchId,
            worktreeId,
            snapshot.WorkbenchRevision,
            snapshot.ObservedAt,
            items));
    }

    public async Task<WorktreeHistoryResponse> GetHistoryAsync(
        string workbenchId,
        string worktreeId,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        return await GetHistoryAsync(
            workbenchId,
            worktreeId,
            new HistoryRequest(limit ?? 30, false),
            cancellationToken).ConfigureAwait(false);
    }

    public Task<WorktreeHistoryResponse> GetHistoryByDepthAsync(
        string workbenchId,
        string worktreeId,
        string? depth,
        CancellationToken cancellationToken = default) =>
        GetHistoryAsync(workbenchId, worktreeId, ParseHistoryRequest(depth, 30), cancellationToken);

    private async Task<WorktreeHistoryResponse> GetHistoryAsync(
        string workbenchId,
        string worktreeId,
        HistoryRequest request,
        CancellationToken cancellationToken)
    {
        var worktreeRoot = state.WorktreeRoot(workbenchId, worktreeId);
        JsonElement result;
        try
        {
            result = await mcp.For("vc_log").CallAsync<JsonElement>(
                "vc_log",
                new { repoPath = worktreeRoot, maxCount = request.Limit, allHistory = request.AllHistory },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ToolCallException or InvalidOperationException)
        {
            throw new AppAssistantGatewayException(
                "HISTORY_UNAVAILABLE",
                "Version-control history is currently unavailable.",
                StatusCodes.Status503ServiceUnavailable);
        }

        var commits = ParseCommits(result, request.Limit);
        var snapshot = runtime.GetSnapshot(workbenchId);
        return new WorktreeHistoryResponse(
            workbenchId,
            worktreeId,
            snapshot.WorkbenchRevision,
            snapshot.ObservedAt,
            commits,
            request.AllHistory);
    }

    public Task<WorktreeSvnHistoryResponse> GetSvnHistoryByDepthAsync(
        string workbenchId,
        string worktreeId,
        string? depth,
        CancellationToken cancellationToken = default) =>
        GetSvnHistoryAsync(
            workbenchId,
            worktreeId,
            ParseHistoryRequest(depth, 30),
            cancellationToken);

    private async Task<WorktreeSvnHistoryResponse> GetSvnHistoryAsync(
        string workbenchId,
        string worktreeId,
        HistoryRequest request,
        CancellationToken cancellationToken)
    {
        var workbench = state.Workbench(workbenchId);
        var worktree = state.Worktree(workbenchId, worktreeId);
        var snapshot = runtime.GetSnapshot(workbenchId);
        if (string.IsNullOrWhiteSpace(worktree.SvnUrl)
            && string.IsNullOrWhiteSpace(workbench.SvnRepositoryPath))
        {
            return new WorktreeSvnHistoryResponse(
                workbench.WorkbenchId,
                worktree.WorktreeId,
                snapshot.WorkbenchRevision,
                snapshot.ObservedAt,
                worktree.SvnUrl,
                Array.Empty<WorktreeSvnHistoryEntry>(),
                false,
                "SVN_NOT_CONFIGURED");
        }

        JsonElement result;
        try
        {
            result = await mcp.For("svn_log").CallAsync<JsonElement>(
                "svn_log",
                new
                {
                    path = ResolveSvnHistoryTarget(worktree, state.WorktreeRoot(workbenchId, worktreeId)),
                    limit = request.Limit,
                    allHistory = request.AllHistory,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new AppAssistantGatewayException(
                "SVN_HISTORY_UNAVAILABLE",
                "SVN history is currently unavailable.",
                StatusCodes.Status503ServiceUnavailable);
        }

        var entries = ParseSvnHistory(result, request.Limit);
        return new WorktreeSvnHistoryResponse(
            workbench.WorkbenchId,
            worktree.WorktreeId,
            snapshot.WorkbenchRevision,
            snapshot.ObservedAt,
            worktree.SvnUrl,
            entries,
            request.AllHistory);
    }

    private static string ResolveSvnHistoryTarget(WorktreeMetadata worktree, string worktreeRoot)
    {
        if (!string.IsNullOrWhiteSpace(worktree.ManagedTiaProjectPath))
        {
            var directory = Path.GetDirectoryName(worktree.ManagedTiaProjectPath);
            if (!string.IsNullOrWhiteSpace(directory))
                return directory;
        }

        return string.IsNullOrWhiteSpace(worktree.SvnUrl)
            ? worktreeRoot
            : worktree.SvnUrl;
    }

    public Task<WorktreeSvnResponse> GetSvnAsync(string workbenchId, string worktreeId)
    {
        var workbench = state.Workbench(workbenchId);
        var worktree = state.Worktree(workbenchId, worktreeId);
        var worktreeRoot = state.WorktreeRoot(workbenchId, worktreeId);
        EngineeringRevisionState? revision = null;
        var revisionPath = WorkbenchPaths.ResolveRevisionState(worktreeRoot);
        if (File.Exists(revisionPath))
        {
            try
            {
                revision = EngineeringStateWriter.Read(revisionPath);
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                // A partially written or legacy revision file is reported as unavailable.
            }
        }

        runtime.ObserveSvnState(workbenchId, worktreeId, worktree.BaseSvnRevision, revision?.Svn.Revision);
        var snapshot = runtime.GetSnapshot(workbenchId);
        return Task.FromResult(new WorktreeSvnResponse(
            workbench.WorkbenchId,
            worktree.WorktreeId,
            snapshot.WorkbenchRevision,
            snapshot.ObservedAt,
            worktree.SvnUrl,
            worktree.BaseSvnRevision,
            revision?.Svn.Revision,
            revision?.Validation.CompileStatus));
    }

    private static int ValidateLimit(int? requested, int defaultValue, int maximum)
    {
        var value = requested ?? defaultValue;
        if (value < 1 || value > maximum)
            throw new AppAssistantGatewayException(
                "INVALID_LIMIT",
                $"The limit must be between 1 and {maximum}.");
        return value;
    }

    private sealed record HistoryRequest(int? Limit, bool AllHistory);

    private static HistoryRequest ParseHistoryRequest(string? depth, int defaultLimit)
    {
        if (string.IsNullOrWhiteSpace(depth) || string.Equals(depth, "recent", StringComparison.OrdinalIgnoreCase))
            return new HistoryRequest(defaultLimit, false);
        if (string.Equals(depth, "all", StringComparison.OrdinalIgnoreCase))
            return new HistoryRequest(null, true);
        if (int.TryParse(depth, out var requested))
            return new HistoryRequest(ValidateLimit(requested, defaultLimit, 100), false);
        throw new AppAssistantGatewayException(
            "INVALID_HISTORY_DEPTH",
            "History depth must be recent, all, or a number between 1 and 100.");
    }

    private static IReadOnlyList<WorktreeHistoryEntry> ParseCommits(JsonElement result, int? limit)
    {
        var commits = result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("commits", out var property)
            && property.ValueKind == JsonValueKind.Array
            ? property
            : result.ValueKind == JsonValueKind.Array ? result : default;
        if (commits.ValueKind != JsonValueKind.Array)
            return Array.Empty<WorktreeHistoryEntry>();

        var items = limit is int count ? commits.EnumerateArray().Take(count) : commits.EnumerateArray();
        return items.Select(commit => new WorktreeHistoryEntry(
            ReadString(commit, "sha") ?? string.Empty,
            ReadString(commit, "message") ?? string.Empty,
            ReadString(commit, "author"),
            ReadString(commit, "timestamp"),
            ReadString(commit, "validationState"))).ToArray();
    }

    private static IReadOnlyList<WorktreeSvnHistoryEntry> ParseSvnHistory(JsonElement result, int? limit)
    {
        var entries = result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("entries", out var property)
            && property.ValueKind == JsonValueKind.Array
            ? property
            : result.ValueKind == JsonValueKind.Array ? result : default;
        if (entries.ValueKind != JsonValueKind.Array)
            return Array.Empty<WorktreeSvnHistoryEntry>();

        var items = limit is int count ? entries.EnumerateArray().Take(count) : entries.EnumerateArray();
        return items.Select(entry => new WorktreeSvnHistoryEntry(
            ReadInt64(entry, "revision"),
            ReadString(entry, "message") ?? string.Empty,
            ReadString(entry, "author") ?? string.Empty,
            ReadString(entry, "time") ?? ReadString(entry, "timestamp"))).ToArray();
    }

    private static long ReadInt64(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.Number
        && property.TryGetInt64(out var result)
            ? result
            : 0;

    private static string? ReadString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
