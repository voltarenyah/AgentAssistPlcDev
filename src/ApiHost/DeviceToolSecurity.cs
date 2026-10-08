using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Agent.Chat;
using Agent.Mcp;
using Agent.Workbench;
using Contracts.Sandbox;

[assembly: InternalsVisibleTo("ApiHost.Tests")]

public static class DeviceContextIdentity
{
    public static string Key(DeviceContext device) =>
        $"{device.WorkbenchId}\n{device.WorktreeId}\n{device.DeviceId}";
}

public sealed class DeviceToolArgumentBinder(
    DeviceSourceResolver resolver,
    WorkbenchWritePolicy? writePolicy = null)
{
    /// <summary>
    /// Engineering tools whose <c>plcName</c> names <b>the one device</b> they read or write. The
    /// conversation's selected device is the authority for that name, so the host supplies it: the
    /// model is never told the TIA PLC name — the runtime context carries the conversation's device id,
    /// not TIA's own name for the device — and <c>PlcSoftwareResolver.Resolve(project, null)</c> fails a
    /// multi-PLC project with <c>AMBIGUOUS_PLC</c> and no argument the model can change.
    ///
    /// Tools that read <c>plcName == null</c> as <b>"every PLC"</b> are deliberately absent
    /// (<c>get_plc_checksums</c>, <c>export_tag_tables</c>, <c>export_udts</c>, <c>sync_export</c>,
    /// <c>rebuild_export</c>, <c>get_context_status</c>, <c>compare_context</c>): binding them would
    /// silently narrow a project-wide read or write to one device. The two tag/UDT exports are
    /// additionally refused by the staged-refresh guard below, so they never reach this binding.
    /// </summary>
    private static readonly HashSet<string> SingleDeviceTools = new(StringComparer.Ordinal)
    {
        "list_blocks",
        "capture_source_evidence",
        "compare_source_evidence",
        "export_source_object",
        "import_block",
        "import_source_object",
        "create_block",
        "delete_block",
        "compile_block",
        "compile_plc",
        "open_block_in_editor",
        "open_source_object_in_editor",
    };

    public Dictionary<string, object?> Bind(string tool, IDictionary<string, object?> supplied, DeviceContext device)
    {
        if (tool is "export_block" or "export_all_blocks" or "export_tag_tables" or "export_udts")
            throw new WorkbenchLifecycleException(
                "STAGED_REFRESH_REQUIRED",
                $"'{tool}' is unavailable through generic tools; use the device refresh/stage lifecycle.");
        var args = new Dictionary<string, object?>(supplied, StringComparer.Ordinal);
        if (SingleDeviceTools.Contains(tool))
            BindPlcName(args, device);
        if (tool is "sync_export" or "rebuild_export")
            Force(args, "outputDir", device.StagingRoot);
        if (tool is "get_context_status" or "compare_context")
            Force(args, "outputDir", device.SourceRoot);
        if (tool is "ingest_source" or "update_components" or "get_schema" or "query"
            or "get_block" or "get_network" or "get_single_network" or "get_network_logic" or "get_all_networks"
            or "get_variable_usage" or "search"
            || tool.StartsWith("query_", StringComparison.Ordinal))
        {
            if (tool is "ingest_source" or "update_components")
            {
                args.Remove("exportedSourceRoot");
                args.Remove("modifiedSourceRoot");
                Force(args, "sourceRoot", device.SourceRoot);
            }
            Force(args, "dbPath", device.KnowledgeDbPath);
        }
        if (tool.StartsWith("vc_", StringComparison.Ordinal)) Force(args, "repoPath", device.WorktreeRoot);
        if (tool == "src_apply_edits")
        {
            writePolicy?.RequireFeatureEdit(device);
            Force(args, "sourceRoot", device.SourceRoot);
            ForceFlag(args, "inPlace");
            ForceFlag(args, "confirmInPlace");
            args.Remove("overwriteOutput");
            var relative = StringValue(args, "relativePath")
                ?? RelativeFromTrustedInput(args, "xmlFilePath", device)
                ?? throw new ArgumentException("relativePath or a trusted xmlFilePath is required.");
            var effective = resolver.ResolveEffective(device, relative);
            var editable = resolver.PrepareEditable(device, relative);
            // The relative form fully determines both paths; drop the caller's originals
            // so Force never conflicts with a pre-resolution value.
            args.Remove("xmlFilePath");
            args.Remove("relativePath");
            Force(args, "xmlFilePath", effective);
            Force(args, "outputFilePath", editable);
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
                ?? RelativeUnder(device.SourceRoot, StringValue(args, "xmlFilePath"))
                ?? throw new ArgumentException("An existing source path is required.");
            var modified = WorkbenchPaths.ResolveRelative(device.SourceRoot, relative);
            if (!File.Exists(modified)) throw new FileNotFoundException("Source was not found.", modified);
            args.Remove("xmlFilePath");
            args.Remove("relativePath");
            Force(args, "xmlFilePath", modified);
        }
        if (tool == "import_source_object")
        {
            var relative = StringValue(args, "relativePath")
                ?? RelativeUnder(device.SourceRoot, StringValue(args, "xmlFilePath"))
                ?? throw new ArgumentException("An existing source path is required.");
            var modified = WorkbenchPaths.ResolveRelative(device.SourceRoot, relative);
            if (!File.Exists(modified)) throw new FileNotFoundException("Source was not found.", modified);
            args.Remove("xmlFilePath");
            args.Remove("relativePath");
            Force(args, "relativePath", relative.Replace('\\', '/'));
            Force(args, "xmlFilePath", modified);
        }
        return args;
    }
    /// <summary>
    /// Fills <c>plcName</c> with the selected device's PLC name. An argument that names another device
    /// is a conflict, not an override: the conversation is bound to one device, so a call that aims
    /// elsewhere is a model-fixable argument error rather than a silent cross-device operation.
    /// When the device's metadata is unreadable, the argument is left as supplied — degrading to the
    /// caller's behaviour instead of inventing a name that could target the wrong device.
    /// </summary>
    private static void BindPlcName(IDictionary<string, object?> args, DeviceContext device)
    {
        var trusted = ReadPlcName(device);
        if (trusted is null)
            return;

        if (StringValue(args, "plcName") is { } supplied
            && !string.Equals(supplied, trusted, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"plcName '{supplied}' conflicts with the selected device '{trusted}'.");
        }

        args["plcName"] = trusted;
    }

    /// <summary>The TIA PLC name recorded for a device context; null when it cannot be read.</summary>
    private static string? ReadPlcName(DeviceContext device)
    {
        try
        {
            var metadata = new AtomicJsonStore().TryRead<DeviceMetadata>(
                Path.Combine(device.DeviceRoot, "device.json"));
            return string.IsNullOrWhiteSpace(metadata?.PlcName) ? null : metadata!.PlcName;
        }
        catch (Exception exception) when (
            exception is IOException or JsonException or MetadataSchemaException)
        {
            return null;
        }
    }

    private static void BindReadable(
        IDictionary<string, object?> args,
        string key,
        DeviceContext device)
    {
        var path = StringValue(args, key)
            ?? throw new ArgumentException($"{key} is required.");
        if (!Path.IsPathRooted(path))
        {
            // Bare relative sourceFile returned by the knowledge DB: resolve it against the
            // device root directly (never against the host process working directory).
            string? fallback = null;
            foreach (var root in new[] { device.SourceRoot })
            {
                var candidate = WorkbenchPaths.ResolveRelative(root, path);
                fallback ??= candidate;
                if (File.Exists(candidate))
                {
                    args[key] = candidate;
                    return;
                }
            }
            args[key] = fallback!;
            return;
        }
        foreach (var root in new[] { device.SourceRoot })
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
        if (!Path.IsPathRooted(path))
        {
            var normalized = path.Replace('\\', '/');
            _ = WorkbenchPaths.ResolveRelative(root, normalized);
            return normalized;
        }
        var relative = Path.GetRelativePath(root, Path.GetFullPath(path));
        _ = WorkbenchPaths.ResolveRelative(root, relative);
        return relative.StartsWith("..", StringComparison.Ordinal) ? null : relative.Replace('\\', '/');
    }

    private static string? RelativeFromTrustedInput(IDictionary<string, object?> args, string key, DeviceContext device)
    {
        var value = StringValue(args, key);
        if (value is null) return null;
        if (!Path.IsPathRooted(value))
        {
            // Bare relative sourceFile returned by the knowledge DB: validate the form
            // (rejects traversal) and keep it relative for ResolveEffective/PrepareEditable.
            var normalized = value.Replace('\\', '/');
            _ = WorkbenchPaths.ResolveRelative(device.SourceRoot, normalized);
            return normalized;
        }
        foreach (var root in new[] { device.SourceRoot })
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
    private static void ForceFlag(IDictionary<string, object?> args, string key)
    {
        if (args.TryGetValue(key, out var supplied) && supplied is not null)
        {
            var isTrue = supplied switch
            {
                bool b => b,
                JsonElement { ValueKind: JsonValueKind.True } => true,
                JsonElement { ValueKind: JsonValueKind.False } => false,
                _ => true,
            };
            if (!isTrue)
                throw new ArgumentException($"{key}=false conflicts with the selected device context.");
        }
        args[key] = true;
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
            // Expired, not Deny: the agent and the user must be able to tell "you did not answer in
            // time" from "you rejected this", because the two produce the same blocked call otherwise.
            if (pending.TryRemove(new(id, entry)))
                _ = entry.Action(ToolConfirmation.Expired, CancellationToken.None);
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
                // Fail closed on every outcome that is not an approval, including an expired card.
                if (decision == ToolConfirmation.Deny) return new { status = "denied" };
                if (decision == ToolConfirmation.Expired) return new { status = "expired" };
                return await gateway.For(tool).CallAsync<JsonElement>(tool, args, executionToken);
            });
            return new { _requiresConfirmation = true, _confirmationId = id, _toolName = tool };
        }
        return await gateway.For(tool).CallAsync<JsonElement>(tool, args, token);
    }
}
