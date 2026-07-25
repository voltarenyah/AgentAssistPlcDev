using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agent.Chat;

/// <summary>
/// Per-project persistent chat session storage.
/// Session files live at {exportRoot}\sessions\{sessionId}.json.
/// Stateless — all methods are static with no shared state.
/// </summary>
public static class SessionManager
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Directory for a project's session files: {exportRoot}\sessions\</summary>
    public static string SessionsDirectory(string projectName) =>
        Path.Combine(AssistantPaths.ResolveExportRoot(projectName), "sessions");

    private static string SessionFilePath(string sessionsDir, string sessionId) =>
        Path.Combine(sessionsDir, $"{sessionId}.json");

    /// <summary>Generate a new unique session ID.</summary>
    public static string NewSessionId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// List all sessions for a project, ordered by creation time descending (newest first).
    /// Returns an empty list if the sessions directory does not exist.
    /// </summary>
    public static List<ChatSessionInfo> ListSessions(string projectName)
    {
        var dir = SessionsDirectory(projectName);
        if (!Directory.Exists(dir))
            return new List<ChatSessionInfo>();

        var result = new List<ChatSessionInfo>();
        foreach (var filePath in Directory.EnumerateFiles(dir, "*.json"))
        {
            var info = ReadSessionInfo(filePath);
            if (info != null)
                result.Add(info);
        }

        result.Sort((a, b) => string.CompareOrdinal(b.CreatedAt, a.CreatedAt));
        return result;
    }

    /// <summary>
    /// Load a full session from disk. Returns null if not found or if the file is corrupted.
    /// </summary>
    public static ChatSessionData? LoadSession(string projectName, string sessionId)
    {
        var filePath = SessionFilePath(SessionsDirectory(projectName), sessionId);
        if (!File.Exists(filePath)) return null;

        try
        {
            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<ChatSessionData>(json, Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Create a new session file for the given project and settings.
    /// The returned ChatSessionData has an empty message list and zero round usages.
    /// </summary>
    public static ChatSessionData CreateNewSession(
        string projectName,
        ChatRequestSettings settings,
        string? runtimeContext)
    {
        var now = DateTimeOffset.Now.ToString("O");
        var sessionId = NewSessionId();
        var exportRoot = AssistantPaths.ResolveExportRoot(projectName);
        var knowledgeDbPath = AssistantPaths.ResolveKnowledgeDbPath(projectName);

        var header = new ChatSessionHeader(
            sessionId,
            projectName,
            now,
            now,
            settings,
            runtimeContext,
            exportRoot,
            knowledgeDbPath);

        var data = new ChatSessionData(header, new List<ChatMessage>(), new List<UsageInfo?>());

        SaveSession(data);
        return data;
    }

    /// <summary>
    /// Write session data to its file. Creates the sessions directory if needed.
    /// SessionId and ProjectName come from <paramref name="data"/>'s header.
    /// </summary>
    public static void SaveSession(ChatSessionData data)
    {
        var dir = SessionsDirectory(data.Header.ProjectName);
        Directory.CreateDirectory(dir);

        var filePath = SessionFilePath(dir, data.Header.SessionId);
        var json = JsonSerializer.Serialize(data, Json);
        File.WriteAllText(filePath, json);
    }

    /// <summary>Delete a session file. Idempotent — does nothing if the file is absent.</summary>
    public static void DeleteSession(string projectName, string sessionId)
    {
        var filePath = SessionFilePath(SessionsDirectory(projectName), sessionId);
        try
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
        catch (IOException)
        {
            // Non-fatal: file may be in use or already removed
        }
    }

    /// <summary>
    /// Resolve a session-relative path to an absolute path. Returns null if the session
    /// does not exist on disk.
    /// </summary>
    public static string? ResolveSessionPath(string projectName, string sessionId)
    {
        var filePath = SessionFilePath(SessionsDirectory(projectName), sessionId);
        return File.Exists(filePath) ? filePath : null;
    }

    // Parse session metadata without loading the full message array.
    // Uses JsonDocument for lightweight parsing — reads header fields from the
    // "header" sub-object + counts messages/turns from the top-level "messages" array.
    private static ChatSessionInfo? ReadSessionInfo(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            if (!root.TryGetProperty("header", out var header) || header.ValueKind != JsonValueKind.Object)
                return null;

            var sessionId = GetString(header, "sessionId");
            var projectName = GetString(header, "projectName");
            var createdAt = GetString(header, "createdAt");
            var updatedAt = GetString(header, "updatedAt");

            if (sessionId == null || projectName == null || createdAt == null || updatedAt == null)
                return null;

            var messageCount = 0;
            var turnCount = 0;
            string? firstUserMessage = null;

            if (root.TryGetProperty("messages", out var messages) && messages.ValueKind == JsonValueKind.Array)
            {
                messageCount = messages.GetArrayLength();
                foreach (var msg in messages.EnumerateArray())
                {
                    var role = GetString(msg, "role");
                    if (role == "user")
                    {
                        turnCount++;
                        firstUserMessage = Truncate(GetString(msg, "content"), 120);
                        break;
                    }
                }
            }

            return new ChatSessionInfo(
                sessionId,
                projectName,
                createdAt,
                updatedAt,
                messageCount,
                turnCount,
                firstUserMessage);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? Truncate(string? text, int maxChars)
    {
        if (text == null) return null;
        return text.Length <= maxChars ? text : text[..maxChars] + "…";
    }
}
