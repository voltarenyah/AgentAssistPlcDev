using System.Text.Json.Serialization;

namespace Agent.Chat;

/// <summary>Persistent session metadata, stored alongside messages in the session JSON file.</summary>
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
    string? Title = null)
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

/// <summary>Lightweight metadata for session listing — no messages included.</summary>
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
    string? FirstUserMessage);
