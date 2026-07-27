using System.Text.Json;
using System.Text.Json.Serialization;
using Agent.Workbench;

namespace Agent.Chat;

/// <summary>
/// Per-worktree persistent chat session storage.
/// Session files live at {worktreeRoot}\.automation\sessions\{sessionId}.json.
/// Stateless - all methods are static with no shared state.
/// </summary>
public static class SessionManager
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Directory for a device worktree's ignored session files.</summary>
    public static string SessionsDirectory(DeviceContext device) =>
        SessionsDirectory(device?.WorktreeRoot
            ?? throw new ArgumentNullException(nameof(device)));

    /// <summary>Directory for an explicit worktree's ignored session files.</summary>
    public static string SessionsDirectory(string worktreeRoot)
    {
        if (string.IsNullOrWhiteSpace(worktreeRoot))
            throw new ArgumentException("Worktree root cannot be blank.", nameof(worktreeRoot));
        if (!Path.IsPathFullyQualified(worktreeRoot))
            throw new ArgumentException("Worktree root must be an absolute path.", nameof(worktreeRoot));

        return Path.Combine(Path.GetFullPath(worktreeRoot), ".automation", "sessions");
    }

    /// <summary>Generate a new unique session ID.</summary>
    public static string NewSessionId() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// List all sessions for a device worktree, ordered by creation time descending.
    /// </summary>
    public static List<ChatSessionInfo> ListSessions(DeviceContext device) =>
        ListSessions(device?.WorktreeRoot
            ?? throw new ArgumentNullException(nameof(device)));

    /// <summary>List all sessions beneath an explicit worktree root.</summary>
    public static List<ChatSessionInfo> ListSessions(string worktreeRoot)
    {
        var directory = SessionsDirectory(worktreeRoot);
        if (!Directory.Exists(directory))
            return new List<ChatSessionInfo>();

        var result = new List<ChatSessionInfo>();
        foreach (var filePath in Directory.EnumerateFiles(directory, "*.json"))
        {
            var info = ReadSessionInfo(filePath);
            if (info is not null)
                result.Add(info);
        }

        result.Sort((left, right) =>
            string.CompareOrdinal(right.CreatedAt, left.CreatedAt));
        return result;
    }

    /// <summary>
    /// Load a full session from a device worktree. Returns null for missing,
    /// corrupted, or unsafe session IDs.
    /// </summary>
    public static ChatSessionData? LoadSession(DeviceContext device, string sessionId) =>
        LoadSession(
            device?.WorktreeRoot ?? throw new ArgumentNullException(nameof(device)),
            sessionId);

    /// <summary>Load a full session from an explicit worktree root.</summary>
    public static ChatSessionData? LoadSession(string worktreeRoot, string sessionId)
    {
        if (!TrySessionFilePath(SessionsDirectory(worktreeRoot), sessionId, out var filePath))
            return null;
        if (!File.Exists(filePath))
            return null;

        try
        {
            var json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<ChatSessionData>(json, Json);
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return null;
        }
    }

    /// <summary>Create a new empty session for the selected device.</summary>
    public static ChatSessionData CreateNewSession(
        DeviceContext device,
        ChatRequestSettings settings,
        string? runtimeContext) =>
        CreateNewSession(
            device?.WorkbenchId ?? throw new ArgumentNullException(nameof(device)),
            device.WorktreeId,
            device.DeviceId,
            device.WorktreeRoot,
            device.KnowledgeDbPath,
            settings,
            runtimeContext);

    /// <summary>Create a new empty session using explicit stable identities and paths.</summary>
    public static ChatSessionData CreateNewSession(
        string workbenchId,
        string worktreeId,
        string deviceId,
        string worktreeRoot,
        string knowledgeDbPath,
        ChatRequestSettings settings,
        string? runtimeContext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workbenchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(knowledgeDbPath);
        ArgumentNullException.ThrowIfNull(settings);

        _ = SessionsDirectory(worktreeRoot);
        var normalizedWorktreeRoot = Path.GetFullPath(worktreeRoot);
        var now = DateTimeOffset.UtcNow.ToString("O");
        var header = new ChatSessionHeader(
            NewSessionId(),
            workbenchId,
            worktreeId,
            deviceId,
            normalizedWorktreeRoot,
            Path.GetFullPath(knowledgeDbPath),
            now,
            now,
            settings,
            runtimeContext);
        var data = new ChatSessionData(
            header,
            new List<ChatMessage>(),
            new List<UsageInfo?>());

        SaveSession(data);
        return data;
    }

    /// <summary>
    /// Write session data beneath the WorktreeRoot recorded in its header.
    /// </summary>
    public static void SaveSession(ChatSessionData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        ValidateNewSessionHeader(data.Header);

        var directory = SessionsDirectory(data.Header.WorktreeRoot);
        Directory.CreateDirectory(directory);
        if (!TrySessionFilePath(directory, data.Header.SessionId, out var filePath))
            throw new ArgumentException("Session ID contains unsafe path characters.", nameof(data));

        File.WriteAllText(filePath, JsonSerializer.Serialize(data, Json));
    }

    /// <summary>Delete a session file. Idempotent if the file is absent.</summary>
    public static void DeleteSession(DeviceContext device, string sessionId) =>
        DeleteSession(
            device?.WorktreeRoot ?? throw new ArgumentNullException(nameof(device)),
            sessionId);

    /// <summary>Delete a session beneath an explicit worktree root.</summary>
    public static void DeleteSession(string worktreeRoot, string sessionId)
    {
        if (!TrySessionFilePath(SessionsDirectory(worktreeRoot), sessionId, out var filePath))
            return;

        try
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
        catch (IOException)
        {
            // Non-fatal: file may be in use or already removed.
        }
    }

    /// <summary>Resolve an existing session file for a device worktree.</summary>
    public static string? ResolveSessionPath(DeviceContext device, string sessionId) =>
        ResolveSessionPath(
            device?.WorktreeRoot ?? throw new ArgumentNullException(nameof(device)),
            sessionId);

    /// <summary>Resolve an existing session file beneath an explicit worktree root.</summary>
    public static string? ResolveSessionPath(string worktreeRoot, string sessionId)
    {
        if (!TrySessionFilePath(SessionsDirectory(worktreeRoot), sessionId, out var filePath))
            return null;

        return File.Exists(filePath) ? filePath : null;
    }

    /// <summary>Build the runtime context shown to the model for a selected device.</summary>
    public static string BuildRuntimeContext(
        DeviceContext device,
        string workbenchName,
        string worktreeName,
        string branch,
        string plcName,
        bool knowledgeStale)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrWhiteSpace(workbenchName);
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        ArgumentException.ThrowIfNullOrWhiteSpace(plcName);

        var knowledgeState = knowledgeStale
            ? "stale; run update_components before reuse"
            : "current";
        return string.Join(
            Environment.NewLine,
            $"Workbench: {workbenchName} ({device.WorkbenchId})",
            $"Worktree: {worktreeName} [{branch}]",
            $"Device: {plcName} ({device.DeviceId})",
            $"Exported source: {device.ExportedSourceRoot}",
            $"Modified source: {device.ModifiedSourceRoot}",
            $"Knowledge DB: {device.KnowledgeDbPath}",
            $"Knowledge state: {knowledgeState}");
    }

    private static ChatSessionInfo? ReadSessionInfo(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (!root.TryGetProperty("header", out var header) ||
                header.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var sessionId = GetString(header, "sessionId");
            var createdAt = GetString(header, "createdAt");
            var updatedAt = GetString(header, "updatedAt");
            if (sessionId is null || createdAt is null || updatedAt is null)
                return null;

            var messageCount = 0;
            var turnCount = 0;
            string? firstUserMessage = null;
            if (root.TryGetProperty("messages", out var messages) &&
                messages.ValueKind == JsonValueKind.Array)
            {
                messageCount = messages.GetArrayLength();
                foreach (var message in messages.EnumerateArray())
                {
                    if (GetString(message, "role") != "user")
                        continue;

                    turnCount++;
                    firstUserMessage ??= Truncate(GetString(message, "content"), 120);
                }
            }

            return new ChatSessionInfo(
                sessionId,
                GetString(header, "workbenchId"),
                GetString(header, "worktreeId"),
                GetString(header, "deviceId"),
                createdAt,
                updatedAt,
                messageCount,
                turnCount,
                firstUserMessage);
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            return null;
        }
    }

    private static bool TrySessionFilePath(
        string sessionsDirectory,
        string? sessionId,
        out string filePath)
    {
        filePath = string.Empty;
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 128)
            return false;
        if (sessionId.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            return false;
        }

        var directory = Path.GetFullPath(sessionsDirectory);
        var candidate = Path.GetFullPath(Path.Combine(directory, $"{sessionId}.json"));
        var directoryPrefix = directory.EndsWith(Path.DirectorySeparatorChar)
            ? directory
            : directory + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        filePath = candidate;
        return true;
    }

    private static void ValidateNewSessionHeader(ChatSessionHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentException.ThrowIfNullOrWhiteSpace(header.WorkbenchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(header.WorktreeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(header.DeviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(header.WorktreeRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(header.KnowledgeDbPath);
        ArgumentNullException.ThrowIfNull(header.Settings);
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? Truncate(string? text, int maxChars)
    {
        if (text is null)
            return null;
        return text.Length <= maxChars ? text : text[..maxChars] + "...";
    }
}
