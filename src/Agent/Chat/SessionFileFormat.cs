using System.Text.Json;

namespace Agent.Chat;

/// <summary>Persistent session metadata, stored alongside messages in the session JSON file.</summary>
public sealed record ChatSessionHeader(
    string SessionId,
    string ProjectName,
    string CreatedAt,
    string UpdatedAt,
    ChatRequestSettings Settings,
    string? RuntimeContext,
    string? ExportRoot,
    string? KnowledgeDbPath);

/// <summary>Full session payload: header + conversation state, serialized as JSON.</summary>
public sealed record ChatSessionData(
    ChatSessionHeader Header,
    List<ChatMessage> Messages,
    List<UsageInfo?> RoundUsages);

/// <summary>Lightweight metadata for session listing — no messages included.</summary>
public sealed record ChatSessionInfo(
    string SessionId,
    string ProjectName,
    string CreatedAt,
    string UpdatedAt,
    int MessageCount,
    int TurnCount,
    string? FirstUserMessage);
