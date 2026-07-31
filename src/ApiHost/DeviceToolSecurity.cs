using System.Collections.Concurrent;
using System.Text.Json;
using Agent.Chat;
using Agent.Mcp;
using Agent.Workbench;
using Contracts.Sandbox;

public static class DeviceContextIdentity
{
    public static string Key(DeviceContext device) =>
        $"{device.WorkbenchId}\n{device.WorktreeId}\n{device.DeviceId}";
}

public sealed class DeviceToolArgumentBinder(DeviceSourceResolver resolver)
{
    public Dictionary<string, object?> Bind(string tool, IDictionary<string, object?> supplied, DeviceContext device)
    {
        if (tool is "export_block" or "export_all_blocks" or "export_tag_tables" or "export_udts")
            throw new WorkbenchLifecycleException(
                "STAGED_REFRESH_REQUIRED",
                $"'{tool}' is unavailable through generic tools; use the device refresh/stage lifecycle.");
        var args = new Dictionary<string, object?>(supplied, StringComparer.Ordinal);
        if (tool is "sync_export" or "rebuild_export")
            Force(args, "outputDir", device.StagingRoot);
        if (tool is "get_context_status" or "compare_context")
            Force(args, "outputDir", device.ExportedSourceRoot);
        if (tool is "ingest_source" or "update_components" or "get_schema" or "query"
            or "get_block" or "get_network" or "get_single_network" or "get_all_networks"
            or "get_variable_usage" or "search"
            || tool.StartsWith("query_", StringComparison.Ordinal))
        {
            if (tool is "ingest_source" or "update_components")
            {
                Force(args, "exportedSourceRoot", device.ExportedSourceRoot);
                Force(args, "modifiedSourceRoot", device.ModifiedSourceRoot);
            }
            Force(args, "dbPath", device.KnowledgeDbPath);
        }
        if (tool.StartsWith("vc_", StringComparison.Ordinal)) Force(args, "repoPath", device.WorktreeRoot);
        if (tool is "src_preview_edits" or "src_apply_edits")
        {
            var relative = StringValue(args, "relativePath")
                ?? RelativeFromTrustedInput(args, "xmlFilePath", device)
                ?? throw new ArgumentException("relativePath or a trusted xmlFilePath is required.");
            var effective = resolver.ResolveEffective(device, relative);
            var output = resolver.PrepareEditable(device, relative);
            Force(args, "xmlFilePath", effective);
            Force(args, "outputFilePath", output);
            args.Remove("relativePath");
        }
        if (tool == "src_parse_block")
            BindReadable(args, "xmlFilePath", device);
        if (tool == "src_diff")
        {
            BindReadable(args, "originalFilePath", device);
            BindReadable(args, "modifiedFilePath", device);
        }
        if (tool == "src_validate")
        {
            BindReadable(args, "xmlFilePath", device);
            if (StringValue(args, "baselineFilePath") is not null)
                BindReadable(args, "baselineFilePath", device);
        }
        if (tool == "import_block")
        {
            var relative = StringValue(args, "relativePath")
                ?? RelativeUnder(device.ModifiedSourceRoot, StringValue(args, "xmlFilePath"))
                ?? throw new ArgumentException("An existing modified-source path is required.");
            var modified = WorkbenchPaths.ResolveRelative(device.ModifiedSourceRoot, relative);
            if (!File.Exists(modified)) throw new FileNotFoundException("Modified source was not found.", modified);
            Force(args, "xmlFilePath", modified);
            args.Remove("relativePath");
        }
        return args;
    }
    private static void BindReadable(
        IDictionary<string, object?> args,
        string key,
        DeviceContext device)
    {
        var path = StringValue(args, key)
            ?? throw new ArgumentException($"{key} is required.");
        foreach (var root in new[] { device.ModifiedSourceRoot, device.ExportedSourceRoot })
        {
            try
            {
                var relative = Path.GetRelativePath(root, Path.GetFullPath(path));
                var safe = WorkbenchPaths.ResolveRelative(root, relative);
                if (!relative.StartsWith("..", StringComparison.Ordinal))
                {
                    args[key] = safe;
                    return;
                }
            }
            catch (WorkbenchPathException) { }
        }
        throw new ArgumentException($"{key} must be inside the selected device source roots.");
    }
    private static string? RelativeUnder(string root, string? path)
    {
        if (path is null) return null;
        var relative = Path.GetRelativePath(root, Path.GetFullPath(path));
        _ = WorkbenchPaths.ResolveRelative(root, relative);
        return relative.StartsWith("..", StringComparison.Ordinal) ? null : relative.Replace('\\', '/');
    }

    private static string? RelativeFromTrustedInput(IDictionary<string, object?> args, string key, DeviceContext device)
    {
        var value = StringValue(args, key);
        if (value is null) return null;
        foreach (var root in new[] { device.ModifiedSourceRoot, device.ExportedSourceRoot })
        {
            try
            {
                var relative = Path.GetRelativePath(root, Path.GetFullPath(value));
                _ = WorkbenchPaths.ResolveRelative(root, relative);
                if (!relative.StartsWith("..", StringComparison.Ordinal)) return relative.Replace('\\', '/');
            }
            catch (WorkbenchPathException) { }
        }
        throw new ArgumentException($"{key} must identify source in the selected device.");
    }

    private static void Force(IDictionary<string, object?> args, string key, string trusted)
    {
        if (StringValue(args, key) is { } supplied && !PathsEqual(supplied, trusted))
            throw new ArgumentException($"{key} conflicts with the selected device context.");
        args[key] = trusted;
    }
    private static string? StringValue(IDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null) return null;
        return value is JsonElement element && element.ValueKind == JsonValueKind.String
            ? element.GetString() : value as string;
    }
    private static bool PathsEqual(string left, string right)
    {
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal); }
        catch { return false; }
    }
}

public sealed class PendingToolActions
{
    private sealed class Entry(
        string contextKey,
        string requester,
        DateTimeOffset expiresAt,
        Func<ToolConfirmation, CancellationToken, Task<object?>> action,
        CancellationTokenSource expiry)
    {
        public string ContextKey { get; } = contextKey;
        public string Requester { get; } = requester;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public Func<ToolConfirmation, CancellationToken, Task<object?>> Action { get; } = action;
        public CancellationTokenSource Expiry { get; } = expiry;
    }
    private readonly ConcurrentDictionary<string, Entry> pending = new(StringComparer.Ordinal);
    private readonly TimeProvider timeProvider;
    private readonly TimeSpan lifetime;
    public PendingToolActions() : this(TimeProvider.System, TimeSpan.FromMinutes(3)) { }
    public PendingToolActions(TimeProvider timeProvider, TimeSpan lifetime)
    {
        this.timeProvider = timeProvider;
        this.lifetime = lifetime;
    }
    public string Add(
        string contextKey,
        string requester,
        Func<ToolConfirmation, CancellationToken, Task<object?>> action)
    {
        var id = Guid.NewGuid().ToString("N");
        var expiry = new CancellationTokenSource(lifetime, timeProvider);
        var entry = new Entry(contextKey, requester, timeProvider.GetUtcNow() + lifetime, action, expiry);
        pending[id] = entry;
        expiry.Token.Register(() =>
        {
            if (pending.TryRemove(new(id, entry)))
                _ = entry.Action(ToolConfirmation.Deny, CancellationToken.None);
        });
        return id;
    }
    public string? Requester(string id) =>
        pending.TryGetValue(id, out var entry) ? entry.Requester : null;
    public async Task<object?> ResolveAsync(
        string id,
        ToolConfirmation decision,
        string contextKey,
        string requester)
    {
        if (!pending.TryGetValue(id, out var entry)) throw new KeyNotFoundException("CONFIRMATION_NOT_FOUND");
        if (entry.ExpiresAt <= timeProvider.GetUtcNow())
        {
            pending.TryRemove(new(id, entry));
            entry.Expiry.Dispose();
            throw new KeyNotFoundException("CONFIRMATION_EXPIRED");
        }
        if (!string.Equals(entry.ContextKey, contextKey, StringComparison.Ordinal)
            || !string.Equals(entry.Requester, requester, StringComparison.Ordinal))
            throw new InvalidOperationException("CONFIRMATION_CONTEXT_MISMATCH");
        if (!pending.TryRemove(new(id, entry))) throw new KeyNotFoundException("CONFIRMATION_NOT_FOUND");
        entry.Expiry.CancelAfter(Timeout.InfiniteTimeSpan);
        entry.Expiry.Dispose();
        using var execution = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return await entry.Action(decision, execution.Token);
    }
}

public sealed class SandboxedToolExecutor(
    SandboxPolicy policy,
    DeviceToolArgumentBinder binder,
    ApiMcpGateway gateway,
    PendingToolActions pending)
{
    public async Task<object?> RequestAsync(
        string tool,
        IDictionary<string, object?> supplied,
        DeviceContext device,
        string requester,
        CancellationToken token)
    {
        var args = binder.Bind(tool, supplied, device);
        var tier = policy.Classify(tool) ?? throw new InvalidOperationException("SANDBOX_TOOL_UNKNOWN");
        if (tier == SandboxTier.Denied) throw new InvalidOperationException("SANDBOX_TOOL_DENIED");
        if (tier == SandboxTier.Destructive)
        {
            var id = pending.Add(DeviceContextIdentity.Key(device), requester, async (decision, executionToken) =>
            {
                if (decision == ToolConfirmation.Deny) return new { status = "denied" };
                return await gateway.For(tool).CallAsync<JsonElement>(tool, args, executionToken);
            });
            return new { _requiresConfirmation = true, _confirmationId = id, _toolName = tool };
        }
        return await gateway.For(tool).CallAsync<JsonElement>(tool, args, token);
    }
}
