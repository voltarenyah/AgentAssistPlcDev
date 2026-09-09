using System.Text.Json.Serialization;

namespace Agent.Workbench;

/// <summary>Independent host-owned tag catalog persisted in tags.json.</summary>
public sealed record WorkbenchTagDocument(
    string SchemaVersion,
    List<TagNode> Nodes,
    List<TagAssignment> Assignments);

/// <summary>A stable taxonomy node. Parent links, rather than paths, preserve identity.</summary>
public sealed record TagNode(
    string TagId,
    string? ParentTagId,
    string Name,
    string NormalizedName);

[JsonConverter(typeof(TagEntityTypeJsonConverter))]
public enum TagEntityType
{
    Workbench,
    Worktree,
}

public sealed class TagEntityTypeJsonConverter()
    : JsonStringEnumConverter<TagEntityType>(
        System.Text.Json.JsonNamingPolicy.CamelCase,
        allowIntegerValues: false);

/// <summary>A direct assignment. Worktree assignments carry their owning WorkbenchId.</summary>
public sealed record TagAssignment(
    string TagId,
    TagEntityType EntityType,
    string EntityId,
    string? WorkbenchId);

/// <summary>Structured tag-filter input. Matching is an AND across the selected tag IDs.</summary>
public sealed record WorkbenchTagSearchQuery(IReadOnlyList<string> TagIds);

/// <summary>A tag-filtered Workbench or Worktree projection.</summary>
public sealed record WorkbenchTagSearchResult(
    TagEntityType EntityType,
    string EntityId,
    string? WorkbenchId,
    IReadOnlyList<string> DirectTagIds,
    IReadOnlyList<string> EffectiveTagIds,
    bool Available);

/// <summary>Server-owned results for the structured Workbench/worktree tag filter.</summary>
public sealed record WorkbenchTagSearchResults(
    IReadOnlyList<WorkbenchTagSearchResult> Workbenches,
    IReadOnlyList<WorkbenchTagSearchResult> Worktrees);
