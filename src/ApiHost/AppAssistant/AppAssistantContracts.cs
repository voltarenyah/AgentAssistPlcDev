using Agent.Workbench;

namespace ApiHost.AppAssistant;

public sealed record AppAssistantWorkbenchContext(
    string WorkbenchId,
    string Name,
    WorkbenchRuntimeSnapshot Runtime,
    WorkbenchSelection? UiFocus,
    IReadOnlyList<ActionCapability> AvailableActions,
    DateTimeOffset ObservedAt);

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
    IReadOnlyList<WorktreeHistoryEntry> Commits);

public sealed record WorktreeSvnResponse(
    string WorkbenchId,
    string WorktreeId,
    long SourceRevision,
    DateTimeOffset ObservedAt,
    string? BranchUrl,
    long? BaseRevision,
    long? CurrentRevision,
    string? ValidationState);

public sealed class AppAssistantGatewayException(
    string code,
    string message,
    int statusCode = StatusCodes.Status400BadRequest)
    : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}
