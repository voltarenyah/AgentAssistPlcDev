using System.Text.Json;
using System.Text.Json.Nodes;
using Agent.Mcp;

namespace Agent.Chat;

/// <summary>One MCP tool exposed to the model: OpenAI-shaped schema + the caller that executes it.</summary>
public sealed record AgentToolSpec(string Name, string? Description, JsonElement InputSchema, IMcpToolCaller Caller, string ServerName);

/// <summary>
/// All tools of the connected MCP servers (engineering, knowledge, versioncontrol, sourceeditor)
/// as OpenAI function definitions, with name → caller routing. Discovered live via tools/list so
/// the agent always matches what the servers were tested with. Safety is enforced per call by the
/// sandbox tiers (AgentSandbox + EngineeringGuard), not by hiding tools: import_block is exposed
/// and gated as destructive — the user confirms each import (agent.md rules 6-7).
/// </summary>
public sealed class McpToolCatalog
{
    private static readonly JsonObject EmptySchema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject(),
    };

    private readonly Dictionary<string, AgentToolSpec> byName = new(StringComparer.Ordinal);

    public McpToolCatalog(IEnumerable<AgentToolSpec> specs)
    {
        foreach (var spec in specs)
        {
            if (byName.TryGetValue(spec.Name, out var existing))
                throw new InvalidOperationException(
                    $"Duplicate MCP tool name '{spec.Name}' from '{existing.ServerName}' and '{spec.ServerName}'.");
            byName.Add(spec.Name, spec);
        }
    }

    public IReadOnlyCollection<AgentToolSpec> Tools => byName.Values;

    public AgentToolSpec Resolve(string name) =>
        byName.TryGetValue(name, out var spec)
            ? spec
            : throw new KeyNotFoundException($"Tool '{name}' is not exposed to the agent.");

    /// <summary>OpenAI tools array: [{ type: "function", function: { name, description, parameters } }].</summary>
    public JsonArray ToOpenAiToolsJson()
    {
        var tools = new JsonArray();
        foreach (var spec in byName.Values.OrderBy(spec => spec.Name, StringComparer.Ordinal))
        {
            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = spec.Name,
                    ["description"] = spec.Description ?? string.Empty,
                    ["parameters"] = spec.InputSchema.ValueKind is JsonValueKind.Object
                        ? JsonNode.Parse(spec.InputSchema.GetRawText())
                        : JsonNode.Parse(EmptySchema.ToJsonString()),
                },
            });
        }

        return tools;
    }

    /// <summary>Lists tools on every server of the host and builds the catalog.</summary>
    public static async Task<McpToolCatalog> BuildAsync(McpHost host, CancellationToken cancellationToken = default)
    {
        var specs = new List<AgentToolSpec>();
        foreach (var (server, conn) in new (string, McpServerConnection)[]
                 { ("engineering", host.Engineering), ("knowledge", host.Knowledge) })
        {
            foreach (var (name, description, inputSchema) in await conn.ListToolsAsync(cancellationToken))
                specs.Add(new AgentToolSpec(name, description, inputSchema, conn, server));
        }

        if (host.VersionControl != null)
        {
            foreach (var (name, description, inputSchema) in await host.VersionControl.ListToolsAsync(cancellationToken))
                specs.Add(new AgentToolSpec(name, description, inputSchema, host.VersionControl, "versioncontrol"));
        }
        if (host.SourceEditor != null)
        {
            foreach (var (name, description, inputSchema) in await host.SourceEditor.ListToolsAsync(cancellationToken))
                specs.Add(new AgentToolSpec(name, description, inputSchema, host.SourceEditor, "sourceeditor"));
        }

        return new McpToolCatalog(specs);
    }
}
