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
        var args = new Dictionary<string, object?>(supplied, StringComparer.Ordinal);
        if (tool is "sync_export" or "rebuild_export" or "export_block" or "export_all_blocks" or "export_tag_tables" or "export_udts")
            Force(args, "outputDir", device.StagingRoot);
        if (tool is "get_context_status" or "compare_context")
            Force(args, "outputDir", device.ExportedSourceRoot);
        if (tool is "ingest_source" or "update_components")
        {
            Force(args, "exportedSourceRoot", device.ExportedSourceRoot);
            Force(args, "modifiedSourceRoot", device.ModifiedSourceRoot);
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
    private readonly ConcurrentDictionary<string, Func<ToolConfirmation, Task<object?>>> pending = new(StringComparer.Ordinal);
    public string Add(Func<ToolConfirmation, Task<object?>> action)
    {
        var id = Guid.NewGuid().ToString("N");
        pending[id] = action;
        return id;
    }
    public async Task<object?> ResolveAsync(string id, ToolConfirmation decision)
    {
        if (!pending.TryRemove(id, out var action)) throw new KeyNotFoundException("CONFIRMATION_NOT_FOUND");
        return await action(decision);
    }
}

public sealed class SandboxedToolExecutor(
    SandboxPolicy policy,
    DeviceToolArgumentBinder binder,
    ApiMcpGateway gateway,
    PendingToolActions pending)
{
    public async Task<object?> RequestAsync(string tool, IDictionary<string, object?> supplied, DeviceContext device, CancellationToken token)
    {
        var args = binder.Bind(tool, supplied, device);
        var tier = policy.Classify(tool) ?? throw new InvalidOperationException("SANDBOX_TOOL_UNKNOWN");
        if (tier == SandboxTier.Denied) throw new InvalidOperationException("SANDBOX_TOOL_DENIED");
        if (tier == SandboxTier.Destructive)
        {
            var id = pending.Add(async decision =>
            {
                if (decision == ToolConfirmation.Deny) return new { status = "denied" };
                return await gateway.For(tool).CallAsync<JsonElement>(tool, args, CancellationToken.None);
            });
            return new { _requiresConfirmation = true, _confirmationId = id, _toolName = tool };
        }
        return await gateway.For(tool).CallAsync<JsonElement>(tool, args, token);
    }
}
