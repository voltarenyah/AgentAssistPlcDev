using System.Text.Json;
using System.Text.Json.Nodes;
using Agent.Mcp;

namespace Agent.Chat;

/// <summary>One MCP tool exposed to the model: OpenAI-shaped schema + the caller that executes it.</summary>
public sealed record AgentToolSpec(string Name, string? Description, JsonElement InputSchema, IMcpToolCaller Caller);

/// <summary>
/// All tools of both MCP servers as OpenAI function definitions, with name → caller routing.
/// Discovered live via tools/list so the agent always matches what the servers were tested with.
/// </summary>
public sealed class McpToolCatalog
{
    /// <summary>
    /// Never exposed to the model: import_block writes into TIA, and agent.md rule 6 forbids
    /// importing without a vc_snapshot (the version-control MCP does not exist yet).
    /// </summary>
    public static readonly IReadOnlySet<string> ExcludedToolNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "import_block",
    };

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
            if (!ExcludedToolNames.Contains(spec.Name))
            {
                byName[spec.Name] = spec;
            }
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

    /// <summary>Lists tools on both servers of the host and builds the catalog (excluded names filtered).</summary>
    public static async Task<McpToolCatalog> BuildAsync(McpHost host, CancellationToken cancellationToken = default)
    {
        var specs = new List<AgentToolSpec>();
        foreach (var connection in new[] { host.Engineering, host.Knowledge })
        {
            foreach (var (name, description, inputSchema) in await connection.ListToolsAsync(cancellationToken))
            {
                specs.Add(new AgentToolSpec(name, description, inputSchema, connection));
            }
        }

        return new McpToolCatalog(specs);
    }
}
