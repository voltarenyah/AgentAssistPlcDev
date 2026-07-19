using System.ComponentModel;
using System.Diagnostics;
using Contracts.Knowledge;
using Mcp.Knowledge.Graph;
using Mcp.Knowledge.Import;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
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
    private const int DefaultSearchMaxRows = 50;
    private const int HardSearchMaxRows = 200;
    private const int SearchSnippetMaxLength = 300;

    private readonly ILogger<KnowledgeTools>? _logger;

    public KnowledgeTools(ILogger<KnowledgeTools>? logger = null)
    {
        _logger = logger;
    }

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

    [McpServerTool(Name = "get_block")]
    [Description("Get a program block (OB/FB/FC) with its networks: index, title, language and translated SCL-like logicStatements (read-only).")]
    public CallToolResult GetBlock(
        [Description("Path to the plc-knowledge.db file.")] string dbPath,
        [Description("Block name, e.g. 'Main'.")] string block)
        => Invoke(() => BlockDetail(dbPath, block));

    [McpServerTool(Name = "get_network")]
    [Description("Get one network of a program block: title, language, logicStatements, symbols read/written and blocks called (read-only).")]
    public CallToolResult GetNetwork(
        [Description("Path to the plc-knowledge.db file.")] string dbPath,
        [Description("Block name, e.g. 'Main'.")] string block,
        [Description("1-based network index.")] int networkIndex)
        => Invoke(() => NetworkDetail(dbPath, block, networkIndex));

    [McpServerTool(Name = "search")]
    [Description("Case-insensitive substring search over node names and network title / logicStatements text (read-only).")]
    public CallToolResult Search(
        [Description("Path to the plc-knowledge.db file.")] string dbPath,
        [Description("Substring to find in node names, network titles or logicStatements.")] string text,
        [Description("Optional node-kind filter, e.g. 'Network', 'OB', 'Variable'.")] string? kind = null,
        [Description("Maximum matches to return (default 50, hard cap 200).")] int? maxRows = null)
        => Invoke(() => SearchGraph(dbPath, text, kind, maxRows));

    private object Ingest(string exportRoot, string? dbPath)
    {
        if (string.IsNullOrWhiteSpace(exportRoot) || !Directory.Exists(exportRoot))
        {
            throw new KnowledgeToolException(
                "EXPORT_ROOT_NOT_FOUND",
                $"Export root '{exportRoot}' was not found.",
                "Pass the folder filled by mcp-engineering export_block / export_all_blocks.");
        }

        var stopwatch = Stopwatch.StartNew();
        var import = ExportFolderCrawler.Import(
            exportRoot,
            progress: message => _logger?.LogInformation("{IngestProgress}", message));
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

        return new IngestResult
        {
            DbPath = targetPath,
            Source = import.Source,
            FilesFound = import.FilesFound,
            FilesImported = import.FilesImported,
            Nodes = import.Graph.Nodes.Count,
            Edges = import.Graph.Edges.Count,
            ByKind = byKind,
            Warnings = import.Warnings.ToList(),
            DurationMs = stopwatch.ElapsedMilliseconds,
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
        using var reader = ExecuteWithSchemaHint(command, connection);

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

    /// <summary>
    /// Executes the reader; on SQLite errors (e.g. "no such table" — the model often guesses table
    /// names) returns a structured error whose remediation lists the tables actually present, so an
    /// agent can correct the statement in its next round instead of giving up.
    /// </summary>
    private static SqliteDataReader ExecuteWithSchemaHint(SqliteCommand command, SqliteConnection connection)
    {
        try
        {
            return command.ExecuteReader();
        }
        catch (SqliteException ex)
        {
            throw new KnowledgeToolException(
                "QUERY_INVALID_SQL",
                ex.Message,
                $"Check the statement against get_schema (ddl + exampleQueries). Tables in this db: {ReadTableNames(connection)}.");
        }
    }

    private static string ReadTableNames(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return string.Join(", ", names);
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

    private static object BlockDetail(string dbPath, string block)
    {
        using var connection = OpenReadOnly(dbPath);
        var blockNode = FindBlockNode(connection, block);
        var networks = ReadNetworks(connection, blockNode.Id);
        return new
        {
            block = blockNode,
            networks,
        };
    }

    private static object NetworkDetail(string dbPath, string block, int networkIndex)
    {
        using var connection = OpenReadOnly(dbPath);
        var blockNode = FindBlockNode(connection, block);
        var networks = ReadNetworks(connection, blockNode.Id);
        var network = networks.FirstOrDefault(item => item.Index == networkIndex);
        if (network == null)
        {
            var available = string.Join(", ", networks.Select(item => item.Index?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"));
            throw new KnowledgeToolException(
                "NETWORK_NOT_FOUND",
                $"Block '{blockNode.Name}' has no network with index {networkIndex} (available: {available}).",
                "Pass a 1-based network index as listed by get_block.");
        }

        return new
        {
            block = blockNode,
            network,
            reads = ReadAccessNames(connection, network.Id, "READS"),
            writes = ReadAccessNames(connection, network.Id, "WRITES"),
            calls = ReadCalls(connection, network.Id),
        };
    }

    private static object SearchGraph(string dbPath, string text, string? kind, int? maxRows)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new KnowledgeToolException(
                "SEARCH_TEXT_REQUIRED",
                "Search text must not be empty.",
                "Pass a substring to find in node names, network titles or logicStatements.");
        }

        var limit = maxRows is null ? DefaultSearchMaxRows : Math.Clamp(maxRows.Value, 1, HardSearchMaxRows);
        var pattern = "%" + text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        var hasKind = !string.IsNullOrWhiteSpace(kind);
        using var connection = OpenReadOnly(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, kind, name, 'name' AS matchedIn, NULL AS snippet
            FROM graph_nodes
            WHERE name LIKE @pattern ESCAPE '\' {(hasKind ? "AND kind = @kind" : string.Empty)}
            UNION ALL
            SELECT n.id, n.kind, n.name, p.name AS matchedIn, p.value AS snippet
            FROM graph_nodes n
            JOIN graph_node_properties p ON p.node_id = n.id AND p.name IN ('title', 'logicStatements')
            WHERE p.value LIKE @pattern ESCAPE '\' {(hasKind ? "AND n.kind = @kind" : string.Empty)}
            ORDER BY kind, id
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@pattern", pattern);
        if (hasKind)
        {
            command.Parameters.AddWithValue("@kind", kind);
        }

        command.Parameters.AddWithValue("@limit", limit + 1);

        var matches = new List<SearchMatch>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var snippet = reader.IsDBNull(4) ? null : reader.GetString(4);
            if (snippet is { Length: > SearchSnippetMaxLength })
            {
                snippet = snippet[..SearchSnippetMaxLength] + "…";
            }

            matches.Add(new SearchMatch(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                snippet));
        }

        var truncated = matches.Count > limit;
        if (truncated)
        {
            matches.RemoveAt(matches.Count - 1);
        }

        return new { text, kind, matches, truncated };
    }

    private static SqliteConnection OpenReadOnly(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
        {
            throw new KnowledgeToolException(
                "DB_NOT_FOUND",
                $"Database '{dbPath}' was not found.",
                "Run ingest_source first, or check the dbPath.");
        }

        SqliteSemanticGraphStore.EnsureSqliteInitialized();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    private static BlockNodeInfo FindBlockNode(SqliteConnection connection, string block)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, kind, name
            FROM graph_nodes
            WHERE name = @name COLLATE NOCASE
              AND kind IN ('OB', 'FB', 'FC')
            ORDER BY id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@name", block);
        string? id = null;
        string? kind = null;
        string? name = null;
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                id = reader.GetString(0);
                kind = reader.GetString(1);
                name = reader.GetString(2);
            }
        }

        if (id == null || kind == null || name == null)
        {
            throw new KnowledgeToolException(
                "BLOCK_NOT_FOUND",
                $"Program block '{block}' was not found.",
                "Check the name; list blocks via query: SELECT id, kind, name FROM graph_nodes WHERE kind IN ('OB','FB','FC') ORDER BY kind, name;");
        }

        using var properties = connection.CreateCommand();
        properties.CommandText = "SELECT name, value FROM graph_node_properties WHERE node_id = @id AND name IN ('sourceFile', 'folderPath');";
        properties.Parameters.AddWithValue("@id", id);
        string? sourceFile = null;
        string? folderPath = null;
        using (var reader = properties.ExecuteReader())
        {
            while (reader.Read())
            {
                if (reader.GetString(0) == "sourceFile")
                {
                    sourceFile = reader.GetString(1);
                }
                else
                {
                    folderPath = reader.GetString(1);
                }
            }
        }

        return new BlockNodeInfo(id, kind, name, sourceFile, folderPath);
    }

    private static List<NetworkInfo> ReadNetworks(SqliteConnection connection, string blockId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
              network.id,
              idx.value,
              cu.value,
              title.value,
              lang.value,
              logic.value
            FROM graph_edges e
            JOIN graph_nodes network ON network.id = e.to_node_id AND network.kind = 'Network'
            LEFT JOIN graph_node_properties idx ON idx.node_id = network.id AND idx.name = 'networkIndex'
            LEFT JOIN graph_node_properties cu ON cu.node_id = network.id AND cu.name = 'compileUnitId'
            LEFT JOIN graph_node_properties title ON title.node_id = network.id AND title.name = 'title'
            LEFT JOIN graph_node_properties lang ON lang.node_id = network.id AND lang.name = 'language'
            LEFT JOIN graph_node_properties logic ON logic.node_id = network.id AND logic.name = 'logicStatements'
            WHERE e.type = 'CONTAINS' AND e.from_node_id = @blockId
            ORDER BY CAST(idx.value AS INTEGER);
            """;
        command.Parameters.AddWithValue("@blockId", blockId);
        var networks = new List<NetworkInfo>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var indexText = reader.IsDBNull(1) ? null : reader.GetString(1);
            int? index = int.TryParse(indexText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
            networks.Add(new NetworkInfo(
                reader.GetString(0),
                index,
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return networks;
    }

    private static string[] ReadAccessNames(SqliteConnection connection, string networkId, string relationship)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT symbol.name
            FROM graph_edges e
            JOIN graph_nodes symbol ON symbol.id = e.to_node_id
            WHERE e.type = @relationship AND e.from_node_id = @networkId
            ORDER BY symbol.name;
            """;
        command.Parameters.AddWithValue("@relationship", relationship);
        command.Parameters.AddWithValue("@networkId", networkId);
        var names = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names.ToArray();
    }

    private static CallInfo[] ReadCalls(SqliteConnection connection, string networkId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT callee.name, callee.kind
            FROM graph_edges contains
            JOIN graph_nodes instruction ON instruction.id = contains.to_node_id AND instruction.kind = 'Instruction'
            JOIN graph_edges calls ON calls.from_node_id = instruction.id AND calls.type = 'CALLS'
            JOIN graph_nodes callee ON callee.id = calls.to_node_id
            WHERE contains.type = 'CONTAINS' AND contains.from_node_id = @networkId
            ORDER BY callee.name;
            """;
        command.Parameters.AddWithValue("@networkId", networkId);
        var calls = new List<CallInfo>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            calls.Add(new CallInfo(reader.GetString(0), reader.GetString(1)));
        }

        return calls.ToArray();
    }

    private sealed record BlockNodeInfo(string Id, string Kind, string Name, string? SourceFile, string? FolderPath);

    private sealed record NetworkInfo(string Id, int? Index, string? CompileUnitId, string? Title, string? Language, string? LogicStatements);

    private sealed record CallInfo(string Name, string Kind);

    private sealed record SearchMatch(string Id, string Kind, string Name, string MatchedIn, string? Snippet);

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
