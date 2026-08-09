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

    public Task<AppAssistantWorkbenchContext> GetContextAsync(string workbenchId)
    {
        var workbench = state.Workbench(workbenchId);
        var snapshot = runtime.GetSnapshot(workbenchId);
        if (snapshot.Worktrees.Count != workbench.Worktrees.Count)
        {
            state.Refresh(workbenchId);
            snapshot = runtime.GetSnapshot(workbenchId);
        }

        var focus = state.Selection?.WorkbenchId == workbenchId ? state.Selection : null;
        var actions = snapshot.AvailableActions;
        return Task.FromResult(new AppAssistantWorkbenchContext(
            workbench.WorkbenchId,
            workbench.Name,
            snapshot,
            focus,
            actions,
            snapshot.ObservedAt));
    }

    public Task<WorktreeTodosResponse> GetTodosAsync(
        string workbenchId,
        string worktreeId,
        int? limit = null)
    {
        var count = ValidateLimit(limit, 20, 100);
        var worktreeRoot = state.WorktreeRoot(workbenchId, worktreeId);
        var snapshot = runtime.GetSnapshot(workbenchId);
        var items = tasks.Load(worktreeRoot).Tasks.Take(count).ToArray();
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
        var count = ValidateLimit(limit, 30, 100);
        var worktreeRoot = state.WorktreeRoot(workbenchId, worktreeId);
        JsonElement result;
        try
        {
            result = await mcp.For("vc_log").CallAsync<JsonElement>(
                "vc_log",
                new { repoPath = worktreeRoot, maxCount = count },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ToolCallException or InvalidOperationException)
        {
            throw new AppAssistantGatewayException(
                "HISTORY_UNAVAILABLE",
                "Version-control history is currently unavailable.",
                StatusCodes.Status503ServiceUnavailable);
        }

        var commits = ParseCommits(result, count);
        var snapshot = runtime.GetSnapshot(workbenchId);
        return new WorktreeHistoryResponse(
            workbenchId,
            worktreeId,
            snapshot.WorkbenchRevision,
            snapshot.ObservedAt,
            commits);
    }

    public Task<WorktreeSvnResponse> GetSvnAsync(string workbenchId, string worktreeId)
    {
        var workbench = state.Workbench(workbenchId);
        var worktree = state.Worktree(workbenchId, worktreeId);
        var snapshot = runtime.GetSnapshot(workbenchId);
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

    private static IReadOnlyList<WorktreeHistoryEntry> ParseCommits(JsonElement result, int limit)
    {
        var commits = result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("commits", out var property)
            && property.ValueKind == JsonValueKind.Array
            ? property
            : result.ValueKind == JsonValueKind.Array ? result : default;
        if (commits.ValueKind != JsonValueKind.Array)
            return Array.Empty<WorktreeHistoryEntry>();

        return commits.EnumerateArray().Take(limit).Select(commit => new WorktreeHistoryEntry(
            ReadString(commit, "sha") ?? string.Empty,
            ReadString(commit, "message") ?? string.Empty,
            ReadString(commit, "author"),
            ReadString(commit, "timestamp"),
            ReadString(commit, "validationState"))).ToArray();
    }

    private static string? ReadString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
