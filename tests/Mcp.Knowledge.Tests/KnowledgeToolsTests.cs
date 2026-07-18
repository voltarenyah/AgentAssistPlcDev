using System.Linq;
using System.Text.Json;
using Mcp.Knowledge.Tools;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Mcp.Knowledge.Tests;

public sealed class KnowledgeToolsTests
{
    [Fact]
    public void GetSchemaReturnsDdlWithAllFourTables()
    {
        var result = new KnowledgeTools().GetSchema();

        Assert.NotEqual(true, result.IsError);
        var text = Assert.IsType<TextContentBlock>(result.Content.Single()).Text;
        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;

        var ddl = root.GetProperty("ddl").GetString()!;
        Assert.Contains("CREATE TABLE IF NOT EXISTS graph_nodes", ddl);
        Assert.Contains("CREATE TABLE IF NOT EXISTS graph_node_properties", ddl);
        Assert.Contains("CREATE TABLE IF NOT EXISTS graph_edges", ddl);
        Assert.Contains("CREATE TABLE IF NOT EXISTS graph_edge_properties", ddl);

        var nodeKinds = root.GetProperty("nodeKinds").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains("OB", nodeKinds);
        Assert.Contains("Network", nodeKinds);
        Assert.Contains("Global DB", nodeKinds);
        Assert.Contains("Instance DB", nodeKinds);

        var edgeTypes = root.GetProperty("edgeTypes").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Contains("CALLS", edgeTypes);
        Assert.Contains("HAS_TYPE", edgeTypes);
        Assert.Contains("INSTANCE_OF", edgeTypes);

        Assert.NotEmpty(root.GetProperty("exampleQueries").EnumerateArray());
    }
}
