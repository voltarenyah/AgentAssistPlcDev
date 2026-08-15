using System;
using System.IO;
using System.Linq;
using Mcp.Knowledge.Graph;
using Mcp.Knowledge.Tools;
using Xunit;

namespace Mcp.Knowledge.Tests;

public sealed class GraphBrowseToolTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SemanticPlcGraph _graph;

    public GraphBrowseToolTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "Mcp.Knowledge.Tests", Guid.NewGuid().ToString("N"), "browse.db");
        _graph = FixtureGraph.Build();
        SqliteSemanticGraphStore.Save(_dbPath, _graph);
    }

    [Fact]
    public void NodeKindsReturnsDistinctKinds()
    {
        var result = ToolResults.OkJson(new KnowledgeTools().QueryNodeKinds(_dbPath));

        var kinds = result.GetProperty("kinds").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Equal(_graph.Nodes.Select(node => node.Kind).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToArray(), kinds);
    }

    [Fact]
    public void QueryNodesReturnsAllNodesWithoutFilter()
    {
        var result = ToolResults.OkJson(new KnowledgeTools().QueryNodes(_dbPath, null, null));

        var nodes = result.GetProperty("nodes").EnumerateArray().ToArray();
        Assert.Equal(_graph.Nodes.Count, nodes.Length);
        Assert.False(result.GetProperty("truncated").GetBoolean());
        Assert.All(nodes, node =>
        {
            Assert.False(string.IsNullOrEmpty(node.GetProperty("id").GetString()));
            Assert.False(string.IsNullOrEmpty(node.GetProperty("kind").GetString()));
        });
    }

    [Fact]
    public void QueryNodesFiltersByKind()
    {
        var expected = _graph.Nodes.Where(node => node.Kind == "OB").ToArray();
        Assert.NotEmpty(expected);

        var result = ToolResults.OkJson(new KnowledgeTools().QueryNodes(_dbPath, "OB", null));

        var nodes = result.GetProperty("nodes").EnumerateArray().ToArray();
        Assert.Equal(expected.Length, nodes.Length);
        Assert.All(nodes, node => Assert.Equal("OB", node.GetProperty("kind").GetString()));
    }

    [Fact]
    public void QueryNodesRespectsMaxRowsAndSetsTruncated()
    {
        var result = ToolResults.OkJson(new KnowledgeTools().QueryNodes(_dbPath, null, 2));

        Assert.Equal(2, result.GetProperty("nodes").GetArrayLength());
        Assert.True(result.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void EdgeTypesReturnsDistinctTypes()
    {
        var result = ToolResults.OkJson(new KnowledgeTools().QueryEdgeTypes(_dbPath));

        var types = result.GetProperty("types").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert.Equal(_graph.Edges.Select(edge => edge.Type).Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToArray(), types);
    }

    [Fact]
    public void QueryEdgesReturnsAllEdgesWithoutFilter()
    {
        var result = ToolResults.OkJson(new KnowledgeTools().QueryEdges(_dbPath, null, null, null));

        var edges = result.GetProperty("edges").EnumerateArray().ToArray();
        Assert.Equal(_graph.Edges.Count, edges.Length);
        Assert.False(result.GetProperty("truncated").GetBoolean());
        Assert.All(edges, edge =>
        {
            Assert.False(string.IsNullOrEmpty(edge.GetProperty("from_node_id").GetString()));
            Assert.False(string.IsNullOrEmpty(edge.GetProperty("to_node_id").GetString()));
        });
    }

    [Fact]
    public void QueryEdgesMatchesNodeOnEitherEndpoint()
    {
        // Find a node that is the to-endpoint of at least one edge and the from-endpoint of none,
        // so the OR-semantics are actually exercised.
        var toOnly = _graph.Edges
            .Select(edge => edge.ToNodeId)
            .First(id => _graph.Edges.All(edge => edge.FromNodeId != id));
        var expected = _graph.Edges.Where(edge => edge.FromNodeId == toOnly || edge.ToNodeId == toOnly).ToArray();
        Assert.NotEmpty(expected);

        var result = ToolResults.OkJson(new KnowledgeTools().QueryEdges(_dbPath, toOnly, null, null));

        var edges = result.GetProperty("edges").EnumerateArray().ToArray();
        Assert.Equal(expected.Length, edges.Length);
        Assert.All(edges, edge =>
            Assert.True(
                edge.GetProperty("from_node_id").GetString() == toOnly ||
                edge.GetProperty("to_node_id").GetString() == toOnly));
    }

    [Fact]
    public void QueryEdgesFiltersByType()
    {
        var type = _graph.Edges.First().Type;
        var expected = _graph.Edges.Count(edge => edge.Type == type);

        var result = ToolResults.OkJson(new KnowledgeTools().QueryEdges(_dbPath, null, type, null));

        Assert.Equal(expected, result.GetProperty("edges").GetArrayLength());
    }

    [Fact]
    public void QueryNodesFiltersByCaseInsensitiveSearch()
    {
        var search = _graph.Nodes.First().Name[..3];
        var expected = _graph.Nodes
            .Where(node =>
                node.Id.Contains(search, StringComparison.OrdinalIgnoreCase)
                || node.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                || node.Kind.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.NotEmpty(expected);

        var result = ToolResults.OkJson(new KnowledgeTools().QueryNodes(_dbPath, null, null, search.ToUpperInvariant()));

        var ids = result.GetProperty("nodes").EnumerateArray()
            .Select(node => node.GetProperty("id").GetString()).ToArray();
        Assert.Equal(
            expected.Select(node => node.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            ids.OrderBy(id => id, StringComparer.Ordinal).ToArray());
        Assert.Equal(expected.Length, result.GetProperty("totalCount").GetInt64());
    }

    [Fact]
    public void QueryNodesCombinesSearchWithKindFilter()
    {
        var ob = _graph.Nodes.First(node => node.Kind == "OB");
        var search = ob.Name[..3];

        var result = ToolResults.OkJson(new KnowledgeTools().QueryNodes(_dbPath, "OB", null, search));

        var nodes = result.GetProperty("nodes").EnumerateArray().ToArray();
        Assert.NotEmpty(nodes);
        Assert.All(nodes, node => Assert.Equal("OB", node.GetProperty("kind").GetString()));
        Assert.All(nodes, node =>
        {
            var text = $"{node.GetProperty("id").GetString()}\n{node.GetProperty("name").GetString()}\n{node.GetProperty("kind").GetString()}";
            Assert.Contains(search, text, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal(nodes.Length, result.GetProperty("totalCount").GetInt64());
    }

    [Fact]
    public void QueryNodesOffsetReturnsNextPage()
    {
        var all = ToolResults.OkJson(new KnowledgeTools().QueryNodes(_dbPath, null, null))
            .GetProperty("nodes").EnumerateArray()
            .Select(node => node.GetProperty("id").GetString()).ToArray();

        var firstPage = ToolResults.OkJson(new KnowledgeTools().QueryNodes(_dbPath, null, 2, null, 0));
        var secondPage = ToolResults.OkJson(new KnowledgeTools().QueryNodes(_dbPath, null, 2, null, 2));

        Assert.True(firstPage.GetProperty("truncated").GetBoolean());
        Assert.Equal(
            all.Skip(2).Take(2).ToArray(),
            secondPage.GetProperty("nodes").EnumerateArray().Select(node => node.GetProperty("id").GetString()).ToArray());
        Assert.Equal(all.Length, secondPage.GetProperty("totalCount").GetInt64());
    }

    [Fact]
    public void QueryEdgesFiltersByCaseInsensitiveSearch()
    {
        var search = _graph.Edges.First().Type[..3];
        var expected = _graph.Edges
            .Where(edge =>
                edge.Id.Contains(search, StringComparison.OrdinalIgnoreCase)
                || edge.Type.Contains(search, StringComparison.OrdinalIgnoreCase)
                || edge.FromNodeId.Contains(search, StringComparison.OrdinalIgnoreCase)
                || edge.ToNodeId.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.NotEmpty(expected);

        var result = ToolResults.OkJson(new KnowledgeTools().QueryEdges(_dbPath, null, null, null, search.ToUpperInvariant()));

        var ids = result.GetProperty("edges").EnumerateArray()
            .Select(edge => edge.GetProperty("id").GetString()).ToArray();
        Assert.Equal(
            expected.Select(edge => edge.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            ids.OrderBy(id => id, StringComparer.Ordinal).ToArray());
        Assert.Equal(expected.Length, result.GetProperty("totalCount").GetInt64());
    }

    [Fact]
    public void QueryEdgesCombinesSearchWithTypeFilter()
    {
        var edge = _graph.Edges.First();
        var search = edge.FromNodeId[..Math.Min(5, edge.FromNodeId.Length)];
        var expected = _graph.Edges
            .Where(item => item.Type == edge.Type)
            .Where(item =>
                item.Id.Contains(search, StringComparison.OrdinalIgnoreCase)
                || item.Type.Contains(search, StringComparison.OrdinalIgnoreCase)
                || item.FromNodeId.Contains(search, StringComparison.OrdinalIgnoreCase)
                || item.ToNodeId.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.NotEmpty(expected);

        var result = ToolResults.OkJson(new KnowledgeTools().QueryEdges(_dbPath, null, edge.Type, null, search));

        var edges = result.GetProperty("edges").EnumerateArray().ToArray();
        Assert.Equal(expected.Length, edges.Length);
        Assert.All(edges, item => Assert.Equal(edge.Type, item.GetProperty("type").GetString()));
        Assert.Equal(expected.Length, result.GetProperty("totalCount").GetInt64());
    }

    [Fact]
    public void QueryEdgesOffsetReturnsNextPage()
    {
        var all = ToolResults.OkJson(new KnowledgeTools().QueryEdges(_dbPath, null, null, null))
            .GetProperty("edges").EnumerateArray()
            .Select(edge => edge.GetProperty("id").GetString()).ToArray();

        var firstPage = ToolResults.OkJson(new KnowledgeTools().QueryEdges(_dbPath, null, null, 2, null, 0));
        var secondPage = ToolResults.OkJson(new KnowledgeTools().QueryEdges(_dbPath, null, null, 2, null, 2));

        Assert.True(firstPage.GetProperty("truncated").GetBoolean());
        Assert.Equal(
            all.Skip(2).Take(2).ToArray(),
            secondPage.GetProperty("edges").EnumerateArray().Select(edge => edge.GetProperty("id").GetString()).ToArray());
        Assert.Equal(all.Length, secondPage.GetProperty("totalCount").GetInt64());
    }

    [Fact]
    public void QueryNodesReportsTotalCountWithoutFilters()
    {
        var result = ToolResults.OkJson(new KnowledgeTools().QueryNodes(_dbPath, null, null));

        Assert.Equal(_graph.Nodes.Count, result.GetProperty("totalCount").GetInt64());
    }

    [Fact]
    public void NodePropertiesReturnsFixtureProperties()
    {
        var node = _graph.Nodes.First(item => item.Properties.Count > 0);

        var result = ToolResults.OkJson(new KnowledgeTools().QueryNodeProperties(_dbPath, node.Id));

        var properties = result.GetProperty("properties").EnumerateArray()
            .ToDictionary(item => item.GetProperty("name").GetString()!, item => item.GetProperty("value").GetString());
        Assert.Equal(node.Properties.Count, properties.Count);
        foreach (var (key, value) in node.Properties)
        {
            Assert.Equal(value, properties[key]);
        }
    }

    [Fact]
    public void EdgePropertiesReturnsFixtureProperties()
    {
        var edge = _graph.Edges.FirstOrDefault(item => item.Properties.Count > 0);
        if (edge is null)
        {
            return; // fixture has no edge properties; nothing to assert
        }

        var result = ToolResults.OkJson(new KnowledgeTools().QueryEdgeProperties(_dbPath, edge.Id));

        var properties = result.GetProperty("properties").EnumerateArray()
            .ToDictionary(item => item.GetProperty("name").GetString()!, item => item.GetProperty("value").GetString());
        Assert.Equal(edge.Properties.Count, properties.Count);
        foreach (var (key, value) in edge.Properties)
        {
            Assert.Equal(value, properties[key]);
        }
    }

    [Fact]
    public void NodePropertiesRejectsEmptyNodeId()
    {
        var error = ToolResults.ErrorJson(new KnowledgeTools().QueryNodeProperties(_dbPath, "  "));

        Assert.Equal("PROPERTIES_KEY_REQUIRED", error.GetProperty("code").GetString());
    }

    [Fact]
    public void MissingDatabaseReturnsDbNotFound()
    {
        var error = ToolResults.ErrorJson(new KnowledgeTools().QueryNodeKinds(
            Path.Combine(Path.GetTempPath(), "Mcp.Knowledge.Tests", Guid.NewGuid().ToString("N"), "missing.db")));

        Assert.Equal("DB_NOT_FOUND", error.GetProperty("code").GetString());
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
        catch (IOException)
        {
            // best effort; temp files are cleaned by the OS
        }
    }
}
