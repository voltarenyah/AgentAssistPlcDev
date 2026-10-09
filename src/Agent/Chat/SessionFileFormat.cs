using System.Text.Json.Serialization;

namespace Agent.Chat;

/// <summary>
/// Persistent session metadata, stored alongside messages in the session JSON file.
/// <para><see cref="TaskId"/> and <see cref="TaskProvenance"/> are <b>not state</b> (ADR-0014): no
/// writer sets them any more, the graph owns the relation, and the only reader is
/// <c>SessionGraphOperations.ImportLegacy</c>, which imports a pre-change value once. They are kept
/// so a session file written before the change still loads. A response header carries the projected
/// primary relation, not this field.</para>
/// </summary>
public sealed record ChatSessionHeader(
    string SessionId,
    string WorkbenchId,
    string WorktreeId,
    string DeviceId,
    string WorktreeRoot,
    string KnowledgeDbPath,
    string CreatedAt,
    string UpdatedAt,
    ChatRequestSettings Settings,
    string? RuntimeContext,
    string? Title = null,
    string? TaskId = null,
    string? TaskProvenance = null)
{
    /// <summary>Legacy JSON field retained only so project-name session files can be read.</summary>
    [JsonInclude]
    public string? ProjectName { get; private init; }
}

/// <summary>Full session payload: header + conversation state, serialized as JSON.</summary>
public sealed record ChatSessionData(
    ChatSessionHeader Header,
    List<ChatMessage> Messages,
    List<UsageInfo?> RoundUsages);

/// <summary>
/// One relation of a conversation to a task as it crosses the wire. The provenance is a plain string
/// and not a <c>GraphProvenance</c>: the response carries the enum's lowercased name (the shape every
/// existing relation response already uses), and <c>Agent.Chat</c> stays free of the engineering
/// graph's types. ApiHost fills this from the graph; nothing here is persisted (ADR-0014).
/// </summary>
public sealed record ChatSessionRelation(
    string TaskId,
    string EdgeId,
    string Provenance,
    bool IsPrimary);

/// <summary>
/// Lightweight metadata for session listing — no messages included. The relation fields are not read
/// from the session file: <see cref="TaskId"/> and <see cref="TaskProvenance"/> are the projected
/// primary relation and <see cref="TaskRelations"/> is the projected set, both filled by ApiHost from
/// the engineering graph, which is the relation's only authority (ADR-0014).
/// </summary>
public sealed record ChatSessionInfo(
    string SessionId,
    string Title,
    string? WorkbenchId,
    string? WorktreeId,
    string? DeviceId,
    string CreatedAt,
    string UpdatedAt,
    int MessageCount,
    int TurnCount,
    string? FirstUserMessage,
    string? TaskId = null,
    string? TaskProvenance = null,
    IReadOnlyList<ChatSessionRelation>? TaskRelations = null);
