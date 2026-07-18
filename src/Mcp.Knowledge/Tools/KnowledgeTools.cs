using System.ComponentModel;
using System.Diagnostics;
using Mcp.Knowledge.Graph;
using Mcp.Knowledge.Import;
using Microsoft.Data.Sqlite;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Mcp.Knowledge.Tools;

/// <summary>
/// MCP tool surface for mcp-knowledge (buildnote/plan/mcp-knowledge.md §6).
/// Failures are normal tool results with isError=true + { code, message, remediation } (§7).
/// </summary>
[McpServerToolType]
public sealed class KnowledgeTools
{
    private const int DefaultMaxRows = 200;
    private const int HardMaxRows = 1000;

    [McpServerTool(Name = "get_schema")]
    [Description("SQLite property-graph schema of the PLC knowledge base: table DDL, node kinds, edge types and example read-only queries (read-only, static content).")]
    public CallToolResult GetSchema() => Invoke(() => new
    {
        ddl = PlcSemanticGraphSqliteSchema.CreateScript,
        nodeKinds = SchemaVocabulary.NodeKinds,
        edgeTypes = SchemaVocabulary.EdgeTypes,
        exampleQueries = SchemaVocabulary.ExampleQueries,
    });

    [McpServerTool(Name = "ingest_source")]
    [Description("Crawl a folder of TIA Openness XML exports, build the PLC property graph and write it as a SQLite knowledge base (write: full rebuild of dbPath; duplicates and unsupported files are skipped with warnings).")]
    public CallToolResult IngestSource(
        [Description("Export folder filled by mcp-engineering export_block / export_all_blocks.")] string exportRoot,
        [Description("SQLite output path. Default: <exportRoot>/plc-knowledge.db.")] string? dbPath = null)
        => Invoke(() => Ingest(exportRoot, dbPath));

    [McpServerTool(Name = "query")]
    [Description("Run a single read-only SQL statement (SELECT / WITH / EXPLAIN) against a PLC knowledge base (read-only).")]
    public CallToolResult Query(
        [Description("Path to the plc-knowledge.db file.")] string dbPath,
        [Description("One read-only SQL statement; must start with SELECT, WITH or EXPLAIN.")] string sql,
        [Description("Maximum rows to return (default 200, hard cap 1000).")] int? maxRows = null)
        => Invoke(() => RunQuery(dbPath, sql, maxRows));

    private static object Ingest(string exportRoot, string? dbPath)
    {
        if (string.IsNullOrWhiteSpace(exportRoot) || !Directory.Exists(exportRoot))
        {
            throw new KnowledgeToolException(
                "EXPORT_ROOT_NOT_FOUND",
                $"Export root '{exportRoot}' was not found.",
                "Pass the folder filled by mcp-engineering export_block / export_all_blocks.");
        }

        var stopwatch = Stopwatch.StartNew();
        var import = ExportFolderCrawler.Import(exportRoot);
        if (import.FilesImported == 0)
        {
            var details = import.Warnings.Count == 0
                ? "No .xml files found."
                : string.Join(" ", import.Warnings);
            throw new KnowledgeToolException(
                "NO_SOURCE_FILES",
                $"Nothing importable under '{exportRoot}': {import.FilesFound} .xml file(s) found, 0 imported. {details}",
                "Point exportRoot at a folder of TIA Openness block exports (SW.Blocks.* content).");
        }

        var targetPath = string.IsNullOrWhiteSpace(dbPath)
            ? Path.Combine(exportRoot, "plc-knowledge.db")
            : dbPath;
        SqliteSemanticGraphStore.Save(targetPath, import.Graph);
        stopwatch.Stop();

        var byKind = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var group in import.Graph.Nodes.GroupBy(node => node.Kind))
        {
            byKind[group.Key] = group.Count();
        }

        return new
        {
            dbPath = targetPath,
            source = import.Source,
            filesFound = import.FilesFound,
            filesImported = import.FilesImported,
            nodes = import.Graph.Nodes.Count,
            edges = import.Graph.Edges.Count,
            byKind,
            warnings = import.Warnings,
            durationMs = stopwatch.ElapsedMilliseconds,
        };
    }

    private static object RunQuery(string dbPath, string sql, int? maxRows)
    {
        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
        {
            throw new KnowledgeToolException(
                "DB_NOT_FOUND",
                $"Database '{dbPath}' was not found.",
                "Run ingest_source first, or check the dbPath.");
        }

        var statement = ValidateReadOnlyStatement(sql);
        var limit = maxRows is null ? DefaultMaxRows : Math.Clamp(maxRows.Value, 1, HardMaxRows);

        SqliteSemanticGraphStore.EnsureSqliteInitialized();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = statement;
        using var reader = command.ExecuteReader();

        var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
        var rows = new List<object?[]>();
        var truncated = false;
        while (reader.Read())
        {
            if (rows.Count == limit)
            {
                truncated = true;
                break;
            }

            var row = new object?[reader.FieldCount];
            for (var index = 0; index < row.Length; index++)
            {
                var value = reader.IsDBNull(index) ? null : reader.GetValue(index);
                row[index] = value is byte[] bytes ? Convert.ToBase64String(bytes) : value;
            }

            rows.Add(row);
        }

        return new { columns, rows, truncated };
    }

    private static string ValidateReadOnlyStatement(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new KnowledgeToolException(
                "QUERY_REJECTED",
                "SQL must not be empty.",
                "Pass a single SELECT, WITH or EXPLAIN statement.");
        }

        var statement = sql.Trim();
        if (statement.EndsWith(';'))
        {
            statement = statement[..^1].TrimEnd();
        }

        if (statement.Length == 0 || statement.Contains(';'))
        {
            throw new KnowledgeToolException(
                "QUERY_REJECTED",
                "Only a single statement is allowed.",
                "Remove the extra statements and run them as separate query calls.");
        }

        if (!statement.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) &&
            !statement.StartsWith("WITH", StringComparison.OrdinalIgnoreCase) &&
            !statement.StartsWith("EXPLAIN", StringComparison.OrdinalIgnoreCase))
        {
            throw new KnowledgeToolException(
                "QUERY_REJECTED",
                "Only read-only SELECT, WITH or EXPLAIN statements are allowed.",
                "Rewrite as a SELECT; the connection is opened read-only as a backstop.");
        }

        return statement;
    }

    private static CallToolResult Invoke(Func<object> action)
    {
        try
        {
            return ToolJson.Ok(action());
        }
        catch (KnowledgeToolException ex)
        {
            return ToolJson.Fail(ex.Code, ex.Message, ex.Remediation);
        }
        catch (ManifestInvalidException ex)
        {
            return ToolJson.Fail(
                "MANIFEST_INVALID",
                ex.Message,
                "Fix the manifest, or delete metadata.json to use the root-element folder crawl instead.");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6) // SQLITE_BUSY / SQLITE_LOCKED
        {
            return ToolJson.Fail(
                "DB_LOCKED",
                ex.Message,
                "Another process holds the database file; close it and retry.");
        }
        catch (Exception ex)
        {
            return ToolJson.Fail("UNEXPECTED_ERROR", ex.Message);
        }
    }
}
