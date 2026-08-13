using Agent.Workbench;

namespace ApiHost.AppAssistant;

public sealed record AppAssistantWorkbenchContext(
    string WorkbenchId,
    string Name,
    WorkbenchRuntimeSnapshot Runtime,
    WorkbenchSelection? UiFocus,
    IReadOnlyList<ActionCapability> AvailableActions,
    DateTimeOffset ObservedAt,
    AppAssistantHistoryContext? History = null);

public sealed record AppAssistantHistoryContext(
    string? WorktreeId,
    WorktreeHistoryResponse? Git,
    WorktreeSvnHistoryResponse? Svn,
    string? UnavailableReason);

public sealed record WorktreeTodosResponse(
    string WorkbenchId,
    string WorktreeId,
    long SourceRevision,
    DateTimeOffset ObservedAt,
    IReadOnlyList<WorktreeTask> Tasks);

public sealed record WorktreeHistoryEntry(
    string Sha,
    string Message,
    string? Author,
    string? Timestamp,
    string? ValidationState);

public sealed record WorktreeHistoryResponse(
    string WorkbenchId,
    string WorktreeId,
    long SourceRevision,
    DateTimeOffset ObservedAt,
    IReadOnlyList<WorktreeHistoryEntry> Commits,
    bool Complete = false,
    string? UnavailableReason = null);

public sealed record WorktreeSvnResponse(
    string WorkbenchId,
    string WorktreeId,
    long SourceRevision,
    DateTimeOffset ObservedAt,
    string? BranchUrl,
    long? BaseRevision,
    long? CurrentRevision,
    string? ValidationState);

public sealed record WorktreeSvnHistoryEntry(
    long Revision,
    string Message,
    string Author,
    string? Timestamp);

public sealed record WorktreeSvnHistoryResponse(
    string WorkbenchId,
    string WorktreeId,
    long SourceRevision,
    DateTimeOffset ObservedAt,
    string? BranchUrl,
    IReadOnlyList<WorktreeSvnHistoryEntry> Entries,
    bool Complete = false,
    string? UnavailableReason = null);

public sealed record CreateWorktreeAssistantRequest(
    string WorkbenchId,
    string Name,
    string Branch,
    string? StartPoint,
    long ExpectedWorkbenchRevision,
    string RequestId);

public sealed record CreateWorkbenchAssistantRequest(
    string WorkbenchId,
    string Name,
    string? RootPath,
    string EngineeringProjectPath,
    long ExpectedWorkbenchRevision,
    string RequestId);

public sealed record CreateWorktreeAssistantResult(
    string WorkbenchId,
    string WorktreeId,
    string Name,
    string Branch,
    long WorkbenchRevision,
    bool Selected);

public sealed record CreateWorkbenchAssistantResult(
    WorkbenchMetadata Workbench,
    WorktreeMetadata Worktree,
    IReadOnlyList<DeviceMetadata> Devices);

public sealed class AppAssistantGatewayException(
    string code,
    string message,
    int statusCode = StatusCodes.Status400BadRequest)
    : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}
