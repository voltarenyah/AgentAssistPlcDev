using Agent.Workbench;
using Agent.Workbench.EngineeringGraph;
using System.Text.Json.Serialization;

public sealed class EngineeringGraphApiScope : IDisposable
{
    private readonly EngineeringGraphStore store;
    public EngineeringGraphService Service { get; }

    internal EngineeringGraphApiScope(EngineeringGraphStore store, EngineeringGraphService service)
    {
        this.store = store;
        Service = service;
    }

    public void Dispose() => store.Dispose();
}

/// <summary>Creates a graph service bound to a server-owned Workbench root.</summary>
public sealed class EngineeringGraphApiFactory
{
    public EngineeringGraphApiScope Open(WorkbenchMetadata workbench)
    {
        ArgumentNullException.ThrowIfNull(workbench);
        var graphStore = new EngineeringGraphStore(workbench.RootPath);
        var graph = new EngineeringGraphService(
            graphStore,
            workbench.WorkbenchId,
            worktreeId => workbench.Worktrees.Any(item => item.WorktreeId == worktreeId));
        return new EngineeringGraphApiScope(graphStore, graph);
    }
}

public sealed record EngineeringTaskApiRequest(
    string Title,
    [property: JsonConverter(typeof(JsonStringEnumConverter<GraphTaskType>))] GraphTaskType Type = GraphTaskType.Feature,
    [property: JsonConverter(typeof(JsonStringEnumConverter<GraphTaskStatus>))] GraphTaskStatus Status = GraphTaskStatus.Todo,
    int Priority = 0,
    string Intent = "",
    string ExpectedResult = "",
    string? Description = null);

public sealed record EngineeringTaskApiResponse(
    string TaskId,
    string WorkbenchId,
    string Scope,
    string? WorktreeId,
    string Title,
    string Type,
    string Status,
    int Priority,
    string Intent,
    string ExpectedResult,
    string? Description,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

public sealed record EngineeringTaskRelationshipApiResponse(
    string Id,
    string Provenance,
    bool IsPrimary);

public sealed record EngineeringTaskDetailApiResponse(
    EngineeringTaskApiResponse Task,
    IReadOnlyList<EngineeringTaskRelationshipApiResponse> Sessions,
    IReadOnlyList<EngineeringTaskRelationshipApiResponse> Commits,
    IReadOnlyList<EngineeringTaskRelationshipApiResponse> SourceObjects,
    IReadOnlyList<EngineeringTaskRelationshipApiResponse> SvnRevisions);

public sealed record ActiveTaskApiRequest(string? TaskId);
