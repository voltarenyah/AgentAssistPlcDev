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
    : JsonStringEnumConverter<TagEntityType>(System.Text.Json.JsonNamingPolicy.CamelCase);

/// <summary>A direct assignment. Worktree assignments carry their owning WorkbenchId.</summary>
public sealed record TagAssignment(
    string TagId,
    TagEntityType EntityType,
    string EntityId,
    string? WorkbenchId);
