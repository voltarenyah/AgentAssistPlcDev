using System.Text.Json;
using System.Collections.Concurrent;
using Agent.Mcp;
using Agent.Workbench;

namespace ApiHost.AppAssistant;

public sealed class AppAssistantGateway(
    WorkbenchApiState state,
    WorkbenchRuntimeStateCoordinator runtime,
    WorktreeTaskStore tasks,
    ApiMcpGateway mcp,
    WorkbenchCoordinator coordinator,
    OperationStatusRegistry operations)
{
    private readonly ConcurrentDictionary<string, Lazy<Task<CreateWorktreeAssistantResult>>> mutationRequests = new(StringComparer.Ordinal);

    public async Task<AppAssistantWorkbenchContext> GetContextAsync(string workbenchId)
    {
        var workbench = state.RefreshRuntimeIfChanged(workbenchId);
        var snapshot = runtime.GetSnapshot(workbenchId);

        var focus = state.Selection?.WorkbenchId == workbenchId ? state.Selection : null;
        var actions = snapshot.AvailableActions;
        var history = await GetFocusedHistoryAsync(workbenchId, focus).ConfigureAwait(false);
        return new AppAssistantWorkbenchContext(
            workbench.WorkbenchId,
            workbench.Name,
            snapshot,
            focus,
            actions,
            snapshot.ObservedAt,
            history);
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
                new { path = state.WorktreeRoot(workbenchId, worktreeId), limit = request.Limit, allHistory = request.AllHistory },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ToolCallException or InvalidOperationException)
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

    private async Task<AppAssistantHistoryContext> GetFocusedHistoryAsync(
        string workbenchId,
        WorkbenchSelection? focus)
    {
        if (string.IsNullOrWhiteSpace(focus?.WorktreeId))
            return new AppAssistantHistoryContext(null, null, null, "NO_FOCUSED_WORKTREE");

        var worktreeId = focus.WorktreeId!;
        WorktreeHistoryResponse? git = null;
        WorktreeSvnHistoryResponse? svn = null;
        try
        {
            git = await GetHistoryAsync(
                workbenchId,
                worktreeId,
                new HistoryRequest(10, false),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (AppAssistantGatewayException exception)
        {
            var snapshot = runtime.GetSnapshot(workbenchId);
            git = new WorktreeHistoryResponse(
                workbenchId,
                worktreeId,
                snapshot.WorkbenchRevision,
                snapshot.ObservedAt,
                Array.Empty<WorktreeHistoryEntry>(),
                false,
                exception.Code);
        }

        try
        {
            svn = await GetSvnHistoryAsync(
                workbenchId,
                worktreeId,
                new HistoryRequest(10, false),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (AppAssistantGatewayException exception)
        {
            var snapshot = runtime.GetSnapshot(workbenchId);
            svn = new WorktreeSvnHistoryResponse(
                workbenchId,
                worktreeId,
                snapshot.WorkbenchRevision,
                snapshot.ObservedAt,
                null,
                Array.Empty<WorktreeSvnHistoryEntry>(),
                false,
                exception.Code);
        }

        return new AppAssistantHistoryContext(worktreeId, git, svn, null);
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

    public async Task<IReadOnlyList<ActionCapability>> GetActionsAsync(string workbenchId)
    {
        var context = await GetContextAsync(workbenchId).ConfigureAwait(false);
        return context.AvailableActions.Select(action => action.Id == "create_worktree"
            ? action with
            {
                Enabled = false,
                BlockedBy = action.BlockedBy.Append("Assistant worktree mutations require the approved mutation flow.").ToArray(),
            }
            : action).ToArray();
    }

    public Task<CreateWorktreeAssistantResult> CreateWorktreeAsync(
        string workbenchId,
        CreateWorktreeAssistantRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(workbenchId, request.WorkbenchId, StringComparison.Ordinal))
            throw new AppAssistantGatewayException("WORKBENCH_SCOPE_MISMATCH", "The mutation workbench does not match the route scope.");
        ValidateMutationRequest(request);
        var key = $"{workbenchId}:{request.RequestId}";
        var operation = mutationRequests.GetOrAdd(
            key,
            _ => new Lazy<Task<CreateWorktreeAssistantResult>>(
                () => ExecuteCreateWorktreeAsync(request, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));
        return operation.Value;
    }

    private async Task<CreateWorktreeAssistantResult> ExecuteCreateWorktreeAsync(
        CreateWorktreeAssistantRequest request,
        CancellationToken cancellationToken)
    {
        var workbench = state.Workbench(request.WorkbenchId);
        var current = runtime.GetSnapshot(request.WorkbenchId);
        if (current.WorkbenchRevision != request.ExpectedWorkbenchRevision)
            throw new RuntimeStateConflictException(request.ExpectedWorkbenchRevision, current.WorkbenchRevision);
        if (current.Operation.Status is RuntimeOperationStatus.Running or RuntimeOperationStatus.AwaitingApproval)
            throw new WorkbenchLifecycleException("WORKBENCH_OPERATION_BUSY", "Another workbench operation is running.");
        if (workbench.Worktrees.Any(worktree =>
                string.Equals(worktree.Name, request.Name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(worktree.Branch, request.Branch, StringComparison.OrdinalIgnoreCase)))
            throw new WorkbenchLifecycleException("WORKTREE_CONFLICT", "A worktree with the same name or branch already exists.");

        runtime.StartOperation(request.WorkbenchId, request.RequestId, "create-worktree");
        operations.Start(request.RequestId, "assistant-create-worktree", "Creating linked worktree...");
        try
        {
            var created = await coordinator.CreateWorktreeAsync(
                new CreateWorktreeRequest(workbench, request.Name, request.Branch, request.StartPoint),
                cancellationToken,
                operations.For(request.RequestId)).ConfigureAwait(false);
            state.Refresh(request.WorkbenchId);
            runtime.CompleteOperation(request.WorkbenchId, request.RequestId, "Worktree created.");
            var refreshed = runtime.GetSnapshot(request.WorkbenchId);
            operations.Succeed(request.RequestId, "Worktree created.");
            return new CreateWorktreeAssistantResult(
                request.WorkbenchId,
                created.WorktreeId,
                created.Name,
                created.Branch,
                refreshed.WorkbenchRevision,
                false);
        }
        catch (Exception exception)
        {
            runtime.FailOperation(request.WorkbenchId, request.RequestId, exception.Message);
            operations.Fail(request.RequestId, "Worktree creation failed.", exception.Message);
            throw;
        }
    }

    private static void ValidateMutationRequest(CreateWorktreeAssistantRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId))
            throw new AppAssistantGatewayException("REQUEST_ID_REQUIRED", "A deterministic request ID is required.");
        if (string.IsNullOrWhiteSpace(request.Name)
            || request.Name.Contains('/')
            || request.Name.Contains('\\')
            || request.Name.Contains("..", StringComparison.Ordinal))
            throw new AppAssistantGatewayException("INVALID_WORKTREE_NAME", "The worktree name must be a single relative name.");
        if (string.IsNullOrWhiteSpace(request.Branch)
            || Path.IsPathRooted(request.Branch)
            || request.Branch.Contains("..", StringComparison.Ordinal))
            throw new AppAssistantGatewayException("INVALID_BRANCH", "The branch must be a relative branch name.");
        if (!string.IsNullOrWhiteSpace(request.StartPoint) && Path.IsPathRooted(request.StartPoint))
            throw new AppAssistantGatewayException("INVALID_START_POINT", "The start point cannot be a filesystem path.");
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
