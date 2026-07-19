using System;
using System.IO;
using System.Linq;
using Mcp.Knowledge.Graph;
using Mcp.Knowledge.Tools;
using Xunit;

namespace Mcp.Knowledge.Tests;

public sealed class QueryToolTests : IDisposable
{
    private readonly string _dbPath;

    public QueryToolTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "Mcp.Knowledge.Tests", Guid.NewGuid().ToString("N"), "query.db");
        SqliteSemanticGraphStore.Save(_dbPath, FixtureGraph.Build());
    }

    [Fact]
    public void SelectReturnsColumnsAndRows()
    {
        var result = ToolResults.OkJson(new KnowledgeTools().Query(
            _dbPath,
            "SELECT kind, COUNT(*) AS count FROM graph_nodes GROUP BY kind ORDER BY kind;",
            null));

        Assert.Equal(new[] { "kind", "count" }, result.GetProperty("columns").EnumerateArray().Select(item => item.GetString()).ToArray());
        var rows = result.GetProperty("rows").EnumerateArray().ToArray();
        Assert.NotEmpty(rows);
        Assert.Contains(rows, row => row[0].GetString() == "OB" && row[1].GetInt64() == 1);
        Assert.False(result.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void RespectsMaxRowsAndSetsTruncated()
    {
        var tools = new KnowledgeTools();

        var limited = ToolResults.OkJson(tools.Query(_dbPath, "SELECT id FROM graph_nodes ORDER BY id;", 5));
        Assert.Equal(5, limited.GetProperty("rows").GetArrayLength());
        Assert.True(limited.GetProperty("truncated").GetBoolean());

        var all = ToolResults.OkJson(tools.Query(_dbPath, "SELECT id FROM graph_nodes ORDER BY id;", null));
        Assert.Equal(FixtureGraph.Build().Nodes.Count, all.GetProperty("rows").GetArrayLength());
        Assert.False(all.GetProperty("truncated").GetBoolean());
    }

    [Theory]
    [InlineData("DROP TABLE graph_nodes;")]
    [InlineData("DELETE FROM graph_nodes;")]
    [InlineData("SELECT 1; SELECT 2;")]
    [InlineData("SELECT 1; DROP TABLE graph_nodes;")]
    [InlineData("   ")]
    public void RejectsNonReadOnlyOrMultiStatements(string sql)
    {
        var error = ToolResults.ErrorJson(new KnowledgeTools().Query(_dbPath, sql, null));

        Assert.Equal("QUERY_REJECTED", error.GetProperty("code").GetString());
    }

    [Fact]
    public void AcceptsWithAndExplainStatements()
    {
        var tools = new KnowledgeTools();

        var cte = ToolResults.OkJson(tools.Query(_dbPath, "WITH blocks AS (SELECT id FROM graph_nodes WHERE kind = 'OB') SELECT * FROM blocks;", null));
        Assert.Equal(1, cte.GetProperty("rows").GetArrayLength());

        ToolResults.OkJson(tools.Query(_dbPath, "EXPLAIN SELECT id FROM graph_nodes;", null));
    }

    [Fact]
    public void MissingTableReturnsSchemaHint()
    {
        var error = ToolResults.ErrorJson(new KnowledgeTools().Query(
            _dbPath,
            "SELECT * FROM networks WHERE kind = 'Network';",
            null));

        Assert.Equal("QUERY_INVALID_SQL", error.GetProperty("code").GetString());
        Assert.Contains("no such table", error.GetProperty("message").GetString());
        var remediation = error.GetProperty("remediation").GetString();
        Assert.Contains("get_schema", remediation);
        Assert.Contains("graph_nodes", remediation);
    }

    [Fact]
    public void RejectsMissingDatabase()
    {
        var missing = Path.Combine(Path.GetTempPath(), "Mcp.Knowledge.Tests", "missing", "nope.db");

        var error = ToolResults.ErrorJson(new KnowledgeTools().Query(missing, "SELECT 1;", null));

        Assert.Equal("DB_NOT_FOUND", error.GetProperty("code").GetString());
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_dbPath)!, recursive: true);
        }
        catch
        {
            // best effort
        }
    }
}
