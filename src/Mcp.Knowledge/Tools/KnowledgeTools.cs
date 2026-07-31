using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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
    private const int DefaultBrowseMaxRows = 1000;
    private const int HardBrowseMaxRows = 10000;
    private const int SearchSnippetMaxLength = 300;

    private readonly ILogger<KnowledgeTools>? _logger;

    public KnowledgeTools(ILogger<KnowledgeTools>? logger = null)
    {
        _logger = logger;
    }

    [McpServerTool(Name = "get_schema")]
    [Description("SQLite property-graph schema of the PLC knowledge base: table DDL, node kinds, edge types and example read-only queries (read-only, static content). The response carries a version hash; pass it as knownVersion on repeat calls to get back only {version, unchanged:true} instead of the full payload.")]
    public CallToolResult GetSchema(
        [Description("Version hash returned by an earlier get_schema call; when it still matches, the full payload is skipped.")] string? knownVersion = null)
        => Invoke(() => SchemaPayload(knownVersion));

    [McpServerTool(Name = "ingest_source")]
    [Description("Build one device SQLite knowledge database from exported-source plus an optional sparse modified-source overlay (write: full rebuild of dbPath). The legacy exportRoot argument remains accepted.")]
    public CallToolResult IngestSource(
        [Description("Authoritative exported-source folder.")] string? exportedSourceRoot = null,
        [Description("Device SQLite output path. Default: <exportedSourceRoot>/plc-knowledge.db.")] string? dbPath = null,
        [Description("Optional sparse modified-source overlay folder.")] string? modifiedSourceRoot = null,
        [Description("Deprecated alias for exportedSourceRoot retained for existing callers.")] string? exportRoot = null)
        => Invoke(() => Ingest(
            ResolveExportedSourceRoot(exportedSourceRoot, exportRoot),
            dbPath,
            modifiedSourceRoot));

    [McpServerTool(Name = "update_components")]
    [Description("Transactionally replace selected components in one device SQLite knowledge database from modified-source overlays (write).")]
    public CallToolResult UpdateComponents(
        [Description("Authoritative exported-source folder containing metadata.json.")] string exportedSourceRoot,
        [Description("Sparse modified-source overlay folder.")] string modifiedSourceRoot,
        [Description("Existing device SQLite knowledge database path.")] string dbPath,
        [Description("One or more component paths relative to the source roots.")] string[] relativePaths)
        => Invoke(() => Update(
            exportedSourceRoot,
            modifiedSourceRoot,
            dbPath,
            relativePaths));

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

    // Retained as a source-compatible wrapper for callers compiled against the old API.
    // It is intentionally not advertised as an MCP tool; agents should choose between
    // get_single_network and get_all_networks explicitly.
    [Description("Deprecated compatibility name for get_single_network. Use get_single_network when the block and network index are known.")]
    public CallToolResult GetNetwork(
        [Description("Path to the plc-knowledge.db file.")] string dbPath,
        [Description("Block name, e.g. 'Main'.")] string block,
        [Description("1-based network index.")] int networkIndex,
        [Description("When true, the block wrapper carries only id + name.")] bool compact = false)
        => Invoke(() => SingleNetworkDetail(dbPath, block, networkIndex, compact, null));

    [McpServerTool(Name = "get_single_network")]
    [Description("Inspect exactly one PLC network. Use this when the block name and 1-based network index are already known. The response contains only that network plus its reads, writes and calls.")]
    public CallToolResult GetSingleNetwork(
        [Description("Path to the plc-knowledge.db file.")] string dbPath,
        [Description("Program block name, e.g. 'Main'.")] string block,
        [Description("1-based network index.")] int networkIndex,
        [Description("Optional fields: logic, access, calls. Defaults to all fields.")] string[]? include = null)
        => Invoke(() => SingleNetworkDetail(dbPath, block, networkIndex, false, include));

    [McpServerTool(Name = "get_all_networks")]
    [Description("List networks in one PLC block. Defaults to compact summaries; request include=['logic'] only when full translated logic is needed. Use get_single_network for one known network.")]
    public CallToolResult GetAllNetworks(
        [Description("Path to the plc-knowledge.db file.")] string dbPath,
        [Description("Program block name, e.g. 'Main'.")] string block,
        [Description("Optional fields: logic, access, calls. Defaults to summary only.")] string[]? include = null,
        [Description("Maximum logic characters per network; 1200 by default when logic is requested.")] int? maxLogicCharsPerNetwork = null)
        => Invoke(() => AllNetworksDetail(dbPath, block, include, maxLogicCharsPerNetwork));

    [McpServerTool(Name = "get_variable_usage")]
    [Description("Find every usage site of a PLC variable in one call: networks whose translated logic text mentions it (text is authoritative) plus READS/WRITES graph edges, including the linked DB-member chain. Each row is labeled read/write/mention with block and network ids. Prefer this for 'where is X read/written / how is X processed' questions instead of chaining search + query + get_single_network.")]
    public CallToolResult GetVariableUsage(
        [Description("Path to the plc-knowledge.db file.")] string dbPath,
        [Description("Full dotted variable path (e.g. 'Cav_A.Cavity.CAB.PLS_Green_Cup.CAB') or a leaf name.")] string variable,
        [Description("Maximum rows to return (default 200, hard cap 1000).")] int? maxRows = null)
        => Invoke(() => VariableUsage(dbPath, variable, maxRows));

    [McpServerTool(Name = "search")]
    [Description("Case-insensitive substring search over node names and network title / logicStatements text (read-only).")]
    public CallToolResult Search(
        [Description("Path to the plc-knowledge.db file.")] string dbPath,
        [Description("Substring to find in node names, network titles or logicStatements.")] string text,
        [Description("Optional node-kind filter, e.g. 'Network', 'OB', 'Variable'.")] string? kind = null,
        [Description("Maximum matches to return (default 50, hard cap 200).")] int? maxRows = null)
        => Invoke(() => SearchGraph(dbPath, text, kind, maxRows));

    [McpServerTool(Name = "query_node_kinds")]
    [Description("List the distinct node kinds present in a PLC knowledge base (read-only).")]
    public CallToolResult QueryNodeKinds(
        [Description("Path to the plc-knowledge.db file.")] string dbPath)
        => Invoke(() => NodeKinds(dbPath));

    [McpServerTool(Name = "query_nodes")]
    [Description("List graph nodes (id, kind, name), optionally filtered by one kind (read-only).")]
    public CallToolResult QueryNodes(
        [Description("Path to the plc-knowledge.db file.")] string dbPath,
        [Description("Optional node-kind filter, e.g. 'Network', 'OB', 'Variable'.")] string? kind = null,
        [Description("Maximum rows to return (default 1000, hard cap 10000).")] int? maxRows = null)
        => Invoke(() => BrowseNodes(dbPath, kind, maxRows));

    [McpServerTool(Name = "query_edge_types")]
    [Description("List the distinct edge types present in a PLC knowledge base (read-only).")]
    public CallToolResult QueryEdgeTypes(
        [Description("Path to the plc-knowledge.db file.")] string dbPath)
        => Invoke(() => EdgeTypes(dbPath));

    [McpServerTool(Name = "query_edges")]
    [Description("List graph edges (id, from_node_id, to_node_id, type). The optional nodeId matches edges where the node is either endpoint (from OR to); the optional type filters by edge type (read-only).")]
    public CallToolResult QueryEdges(
        [Description("Path to the plc-knowledge.db file.")] string dbPath,
        [Description("Optional node id; matches edges where the node is the from- or to-endpoint.")] string? nodeId = null,
        [Description("Optional edge-type filter, e.g. 'CONTAINS', 'CALLS'.")] string? type = null,
        [Description("Maximum rows to return (default 1000, hard cap 10000).")] int? maxRows = null)
        => Invoke(() => BrowseEdges(dbPath, nodeId, type, maxRows));

    [McpServerTool(Name = "query_node_properties")]
    [Description("List the name/value properties of one graph node (read-only).")]
    public CallToolResult QueryNodeProperties(
        [Description("Path to the plc-knowledge.db file.")] string dbPath,
        [Description("Graph node id.")] string nodeId)
        => Invoke(() => BrowseProperties(dbPath, "graph_node_properties", "node_id", nodeId, "nodeId"));

    [McpServerTool(Name = "query_edge_properties")]
    [Description("List the name/value properties of one graph edge (read-only).")]
    public CallToolResult QueryEdgeProperties(
        [Description("Path to the plc-knowledge.db file.")] string dbPath,
        [Description("Graph edge id.")] string edgeId)
        => Invoke(() => BrowseProperties(dbPath, "graph_edge_properties", "edge_id", edgeId, "edgeId"));

    private object Ingest(
        string exportRoot,
        string? dbPath,
        string? modifiedSourceRoot)
    {
        if (string.IsNullOrWhiteSpace(exportRoot) || !Directory.Exists(exportRoot))
        {
            throw new KnowledgeToolException(
                "EXPORT_ROOT_NOT_FOUND",
                $"Export root '{exportRoot}' was not found.",
                "Pass the folder filled by mcp-engineering export_block / export_all_blocks.");
        }

        var stopwatch = Stopwatch.StartNew();
        var import = string.IsNullOrWhiteSpace(modifiedSourceRoot)
            ? ExportFolderCrawler.Import(
                exportRoot,
                progress: message => _logger?.LogInformation("{IngestProgress}", message))
            : ToExportFolderResult(
                EffectiveSourceImporter.Import(
                    exportRoot,
                    modifiedSourceRoot,
                    progress: message => _logger?.LogInformation(
                        "{IngestProgress}",
                        message)));
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

    private static object Update(
        string exportedSourceRoot,
        string modifiedSourceRoot,
        string dbPath,
        string[] relativePaths)
    {
        if (relativePaths == null || relativePaths.Length == 0)
        {
            throw new KnowledgeToolException(
                "COMPONENT_PATHS_REQUIRED",
                "At least one component path is required.",
                "Pass the modified-source relative paths that should be applied.");
        }

        if (string.IsNullOrWhiteSpace(exportedSourceRoot) ||
            !Directory.Exists(exportedSourceRoot))
        {
            throw new KnowledgeToolException(
                "EXPORT_ROOT_NOT_FOUND",
                $"Exported source root '{exportedSourceRoot}' was not found.",
                "Pass the device exported-source folder containing metadata.json.");
        }

        if (string.IsNullOrWhiteSpace(modifiedSourceRoot) ||
            !Directory.Exists(modifiedSourceRoot))
        {
            throw new KnowledgeToolException(
                "MODIFIED_ROOT_NOT_FOUND",
                $"Modified source root '{modifiedSourceRoot}' was not found.",
                "Pass the device modified-source folder.");
        }

        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
        {
            throw new KnowledgeToolException(
                "DB_NOT_FOUND",
                $"Database '{dbPath}' was not found.",
                "Run ingest_source first, or check the device dbPath.");
        }

        var normalizedPaths = new List<string>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in relativePaths)
        {
            string normalizedPath;
            try
            {
                normalizedPath = EffectiveSourceImporter.NormalizeRelativePath(path);
            }
            catch (ArgumentException ex)
            {
                throw new KnowledgeToolException(
                    "COMPONENT_PATH_INVALID",
                    ex.Message,
                    "Pass paths relative to modifiedSourceRoot without '.' or '..' segments.");
            }

            if (seenPaths.Add(normalizedPath))
            {
                normalizedPaths.Add(normalizedPath);
            }
        }

        var replacements = new List<(SemanticPlcGraph Graph, ComponentImport Component)>();
        foreach (var relativePath in normalizedPaths)
        {
            var overlayPath = EffectiveSourceImporter.ResolvePath(
                modifiedSourceRoot,
                relativePath);
            if (!File.Exists(overlayPath))
            {
                throw new KnowledgeToolException(
                    "OVERLAY_COMPONENT_NOT_FOUND",
                    $"Overlay component '{relativePath}' was not found under '{modifiedSourceRoot}'.",
                    "Create the component under modified-source, or remove it from relativePaths.");
            }

            var imported = EffectiveSourceImporter.ImportComponent(
                exportedSourceRoot,
                modifiedSourceRoot,
                relativePath);
            replacements.Add((imported.Graph, imported.Components.Single()));
        }

        var stored = SqliteSemanticGraphStore.Load(dbPath).ComponentImports;
        if (stored.Count == 0)
        {
            throw new ComponentProvenanceUnavailableException(
                "The knowledge database has no component provenance. Rebuild it before updating selected components.");
        }

        foreach (var replacement in replacements)
        {
            var exactIdentity = stored.Where(component =>
                string.Equals(
                    component.ComponentKey,
                    replacement.Component.ComponentKey,
                    StringComparison.Ordinal) ||
                string.Equals(
                    component.RelativePath,
                    replacement.Component.RelativePath,
                    StringComparison.Ordinal)).ToArray();
            if (exactIdentity.Length == 0)
            {
                throw new KnowledgeToolException(
                    "COMPONENT_NOT_IN_DATABASE",
                    $"Component '{replacement.Component.ComponentKey}' at '{replacement.Component.RelativePath}' is not present in the device database.",
                    "Run ingest_source with exportedSourceRoot, modifiedSourceRoot, and dbPath to rebuild before applying partial updates.");
            }

            if (exactIdentity.Length != 1 ||
                !string.Equals(
                    exactIdentity[0].ComponentKey,
                    replacement.Component.ComponentKey,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    exactIdentity[0].RelativePath,
                    replacement.Component.RelativePath,
                    StringComparison.Ordinal))
            {
                throw new ComponentIdentityMismatchException(
                    $"Overlay component '{replacement.Component.ComponentKey}' at '{replacement.Component.RelativePath}' " +
                    "does not match the component identity stored in the device database.");
            }
        }

        foreach (var replacement in replacements)
        {
            ComponentProvenanceStore.Replace(
                dbPath,
                replacement.Graph,
                replacement.Component);
        }

        var hashes = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var replacement in replacements)
        {
            hashes[replacement.Component.ComponentKey] =
                replacement.Component.ContentHash;
        }

        return new KnowledgeUpdateResult(
            dbPath,
            replacements
                .Select(replacement => replacement.Component.ComponentKey)
                .ToArray(),
            hashes,
            Array.Empty<string>());
    }

    private static string ResolveExportedSourceRoot(
        string? exportedSourceRoot,
        string? legacyExportRoot)
    {
        if (!string.IsNullOrWhiteSpace(exportedSourceRoot) &&
            !string.IsNullOrWhiteSpace(legacyExportRoot) &&
            !string.Equals(
                Path.GetFullPath(exportedSourceRoot),
                Path.GetFullPath(legacyExportRoot),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new KnowledgeToolException(
                "EXPORT_ROOT_CONFLICT",
                "exportedSourceRoot and legacy exportRoot identify different folders.",
                "Pass only exportedSourceRoot, or pass the same folder to both arguments.");
        }

        return !string.IsNullOrWhiteSpace(exportedSourceRoot)
            ? exportedSourceRoot
            : legacyExportRoot ?? string.Empty;
    }

    private static ExportFolderImportResult ToExportFolderResult(
        EffectiveSourceImportResult import)
    {
        return new ExportFolderImportResult(
            import.Graph,
            import.FilesFound,
            import.FilesImported,
            import.Warnings,
            import.Source);
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

    private static object SingleNetworkDetail(
        string dbPath,
        string block,
        int networkIndex,
        bool compact,
        IReadOnlyCollection<string>? include)
    {
        using var connection = OpenReadOnly(dbPath);
        var blockNode = FindBlockNode(connection, block);
        var network = ReadNetwork(connection, blockNode.Id, networkIndex);
        if (network == null)
        {
            var networks = ReadNetworks(connection, blockNode.Id);
            var available = string.Join(", ", networks.Select(item => item.Index?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"));
            throw new KnowledgeToolException(
                "NETWORK_NOT_FOUND",
                $"Block '{blockNode.Name}' has no network with index {networkIndex} (available: {available}).",
                "Pass a 1-based network index as listed by get_block.");
        }

        // I10: compact mode drops the repeated per-call block metadata down to id + name.
        object blockInfo = compact
            ? new { id = blockNode.Id, name = blockNode.Name }
            : blockNode;
        var fields = NormalizeInclude(include, allByDefault: true);
        return new
        {
            block = blockInfo,
            network = ProjectNetwork(connection, network, fields, logicLimit: null),
            reads = fields.Contains("access") ? ReadAccessNames(connection, network.Id, "READS") : Array.Empty<string>(),
            writes = fields.Contains("access") ? ReadAccessNames(connection, network.Id, "WRITES") : Array.Empty<string>(),
            calls = fields.Contains("calls") ? ReadCalls(connection, network.Id) : Array.Empty<CallInfo>(),
        };
    }

    private static object AllNetworksDetail(
        string dbPath,
        string block,
        IReadOnlyCollection<string>? include,
        int? maxLogicCharsPerNetwork)
    {
        using var connection = OpenReadOnly(dbPath);
        var blockNode = FindBlockNode(connection, block);
        var fields = NormalizeInclude(include, allByDefault: false);
        var logicLimit = fields.Contains("logic")
            ? Math.Clamp(maxLogicCharsPerNetwork ?? 1200, 1, 8000)
            : 0;
        var networks = ReadNetworks(connection, blockNode.Id)
            .Select(network => ProjectNetwork(connection, network, fields, logicLimit))
            .ToArray();

        return new
        {
            block = new { id = blockNode.Id, kind = blockNode.Kind, name = blockNode.Name },
            networks,
            meta = new
            {
                returned = networks.Length,
                include = fields.OrderBy(item => item, StringComparer.Ordinal).ToArray(),
                logicLimit,
            },
        };
    }

    private static HashSet<string> NormalizeInclude(
        IReadOnlyCollection<string>? include,
        bool allByDefault)
    {
        var fields = include is null
            ? new HashSet<string>(allByDefault ? new[] { "logic", "access", "calls" } : Array.Empty<string>(), StringComparer.OrdinalIgnoreCase)
            : include
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unsupported = fields.Where(item => item is not ("logic" or "access" or "calls")).ToArray();
        if (unsupported.Length > 0)
        {
            throw new KnowledgeToolException(
                "NETWORK_INCLUDE_INVALID",
                $"Unsupported network fields: {string.Join(", ", unsupported)}.",
                "Use only 'logic', 'access' or 'calls'.");
        }

        return fields;
    }

    private static object ProjectNetwork(
        SqliteConnection connection,
        NetworkInfo network,
        IReadOnlySet<string> fields,
        int? logicLimit)
    {
        var logic = fields.Contains("logic") ? network.LogicStatements : null;
        var logicTruncated = false;
        if (logicLimit is > 0 && logic is { Length: > 0 } && logic.Length > logicLimit.Value)
        {
            logic = logic[..logicLimit.Value];
            logicTruncated = true;
        }

        return new
        {
            id = network.Id,
            index = network.Index,
            compileUnitId = network.CompileUnitId,
            title = network.Title,
            language = network.Language,
            logicStatements = logic,
            logicTruncated,
            reads = fields.Contains("access") ? ReadAccessNames(connection, network.Id, "READS") : null,
            writes = fields.Contains("access") ? ReadAccessNames(connection, network.Id, "WRITES") : null,
            calls = fields.Contains("calls") ? ReadCalls(connection, network.Id) : null,
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
        var useFts = text.Trim().Length >= 3 && TableExists(connection, "knowledge_search");
        command.CommandText = useFts
            ? $"""
                SELECT s.node_id, n.kind, n.name, s.field AS matchedIn, s.content AS snippet
                FROM knowledge_search s
                JOIN graph_nodes n ON n.id = s.node_id
                WHERE knowledge_search MATCH @ftsQuery
                  AND s.field IN ('name', 'title', 'logicStatements')
                  {(hasKind ? "AND s.kind = @kind" : string.Empty)}
                ORDER BY n.kind, n.id
                LIMIT @limit;
                """
            : $"""
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
        command.Parameters.AddWithValue(
            useFts ? "@ftsQuery" : "@pattern",
            useFts ? FtsLiteral(text.Trim()) : pattern);
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

    private static object NodeKinds(string dbPath)
    {
        using var connection = OpenReadOnly(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT kind FROM graph_nodes ORDER BY kind;";
        var kinds = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            kinds.Add(reader.GetString(0));
        }

        return new { kinds };
    }

    private static object BrowseNodes(string dbPath, string? kind, int? maxRows)
    {
        var limit = maxRows is null ? DefaultBrowseMaxRows : Math.Clamp(maxRows.Value, 1, HardBrowseMaxRows);
        var hasKind = !string.IsNullOrWhiteSpace(kind);
        using var connection = OpenReadOnly(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = hasKind
            ? "SELECT id, kind, name FROM graph_nodes WHERE kind = @kind ORDER BY name COLLATE NOCASE, id LIMIT @limit;"
            : "SELECT id, kind, name FROM graph_nodes ORDER BY kind, name COLLATE NOCASE, id LIMIT @limit;";
        if (hasKind)
        {
            command.Parameters.AddWithValue("@kind", kind);
        }

        command.Parameters.AddWithValue("@limit", limit + 1);

        var nodes = new List<object>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            nodes.Add(new { id = reader.GetString(0), kind = reader.GetString(1), name = reader.GetString(2) });
        }

        var truncated = nodes.Count > limit;
        if (truncated)
        {
            nodes.RemoveAt(nodes.Count - 1);
        }

        return new { nodes, truncated };
    }

    private static object EdgeTypes(string dbPath)
    {
        using var connection = OpenReadOnly(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT type FROM graph_edges ORDER BY type;";
        var types = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            types.Add(reader.GetString(0));
        }

        return new { types };
    }

    private static object BrowseEdges(string dbPath, string? nodeId, string? type, int? maxRows)
    {
        var limit = maxRows is null ? DefaultBrowseMaxRows : Math.Clamp(maxRows.Value, 1, HardBrowseMaxRows);
        var hasNode = !string.IsNullOrWhiteSpace(nodeId);
        var hasType = !string.IsNullOrWhiteSpace(type);
        using var connection = OpenReadOnly(dbPath);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, from_node_id, to_node_id, type
            FROM graph_edges
            WHERE 1 = 1
              {(hasNode ? "AND (from_node_id = @nodeId OR to_node_id = @nodeId)" : string.Empty)}
              {(hasType ? "AND type = @type" : string.Empty)}
            ORDER BY id
            LIMIT @limit;
            """;
        if (hasNode)
        {
            command.Parameters.AddWithValue("@nodeId", nodeId);
        }

        if (hasType)
        {
            command.Parameters.AddWithValue("@type", type);
        }

        command.Parameters.AddWithValue("@limit", limit + 1);

        var edges = new List<object>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            edges.Add(new
            {
                id = reader.GetString(0),
                from_node_id = reader.GetString(1),
                to_node_id = reader.GetString(2),
                type = reader.GetString(3),
            });
        }

        var truncated = edges.Count > limit;
        if (truncated)
        {
            edges.RemoveAt(edges.Count - 1);
        }

        return new { edges, truncated };
    }

    private static object BrowseProperties(string dbPath, string table, string keyColumn, string key, string keyArgumentName)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new KnowledgeToolException(
                "PROPERTIES_KEY_REQUIRED",
                $"The {keyArgumentName} argument must not be empty.",
                $"Pass the {keyArgumentName} of a node or edge listed by query_nodes / query_edges.");
        }

        using var connection = OpenReadOnly(dbPath);
        using var command = connection.CreateCommand();
        // table / keyColumn are fixed internal constants, never caller-supplied.
        command.CommandText = $"SELECT name, value FROM {table} WHERE {keyColumn} = @key ORDER BY name;";
        command.Parameters.AddWithValue("@key", key);
        var properties = new List<object>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            properties.Add(new { name = reader.GetString(0), value = reader.GetString(1) });
        }

        return new { properties };
    }

    private static string? _schemaVersion;

    // I9: stable hash over the whole static schema payload; agents pass it back as knownVersion
    // on repeat get_schema calls to skip the (~2.5k token) full payload.
    private static object SchemaPayload(string? knownVersion)
    {
        var version = _schemaVersion ??= ComputeSchemaVersion();
        if (!string.IsNullOrWhiteSpace(knownVersion) &&
            string.Equals(knownVersion, version, StringComparison.OrdinalIgnoreCase))
        {
            return new { version, unchanged = true };
        }

        return new
        {
            version,
            ddl = PlcSemanticGraphSqliteSchema.CreateScript,
            nodeKinds = SchemaVocabulary.NodeKinds,
            edgeTypes = SchemaVocabulary.EdgeTypes,
            exampleQueries = SchemaVocabulary.ExampleQueries,
        };
    }

    private static string ComputeSchemaVersion()
    {
        var builder = new StringBuilder();
        builder.Append(PlcSemanticGraphSqliteSchema.CreateScript);
        foreach (var kind in SchemaVocabulary.NodeKinds)
        {
            builder.Append('|').Append(kind);
        }

        foreach (var edgeType in SchemaVocabulary.EdgeTypes)
        {
            builder.Append('|').Append(edgeType);
        }

        foreach (var query in SchemaVocabulary.ExampleQueries)
        {
            builder.Append('|').Append(query.Name).Append(':').Append(query.Sql);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    // I4: one-call "where is X read/written". Text hits (logicStatements LIKE) are authoritative
    // and labeled "mention" unless a READS/WRITES edge gives a direction for the same network;
    // edge targets include the REFERS_TO-linked DB-member chain (I2) plus db-member ids derived
    // from the dotted path, so the id space the agent guessed cannot strand the query.
    private static object VariableUsage(string dbPath, string variable, int? maxRows)
    {
        if (string.IsNullOrWhiteSpace(variable))
        {
            throw new KnowledgeToolException(
                "VARIABLE_REQUIRED",
                "Variable path must not be empty.",
                "Pass a full dotted path (e.g. 'Cav_A.Cavity.CAB.PLS_Green_Cup.CAB') or a leaf name.");
        }

        var trimmed = variable.Trim();
        var limit = maxRows is null ? DefaultMaxRows : Math.Clamp(maxRows.Value, 1, HardMaxRows);
        using var connection = OpenReadOnly(dbPath);

        var matchedNodeIds = ResolveVariableNodeIds(connection, trimmed);
        var directionsByNetwork = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        if (matchedNodeIds.Count > 0)
        {
            using var command = connection.CreateCommand();
            var useAccessProjection = TableExists(connection, "knowledge_network_accesses");
            command.CommandText = useAccessProjection
                ? $"""
                    SELECT access, network_id
                    FROM knowledge_network_accesses
                    WHERE {InClause(command, "target_node_id", "t", matchedNodeIds)};
                    """
                : $"""
                    SELECT e.type, e.from_node_id, src.kind, nid.value
                    FROM graph_edges e
                    JOIN graph_nodes src ON src.id = e.from_node_id
                    LEFT JOIN graph_edge_properties nid ON nid.edge_id = e.id AND nid.name = 'networkId'
                    WHERE e.type IN ('READS', 'WRITES') AND {InClause(command, "e.to_node_id", "t", matchedNodeIds)};
                    """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var networkId = useAccessProjection
                    ? reader.GetString(1)
                    : string.Equals(reader.GetString(2), SemanticNodeKind.Network, StringComparison.Ordinal)
                        ? reader.GetString(1)
                        : reader.IsDBNull(3) ? null : reader.GetString(3);
                if (string.IsNullOrWhiteSpace(networkId))
                {
                    continue;
                }

                if (!directionsByNetwork.TryGetValue(networkId, out var directions))
                {
                    directions = new SortedSet<string>(StringComparer.Ordinal);
                    directionsByNetwork.Add(networkId, directions);
                }

                directions.Add(string.Equals(reader.GetString(0), "WRITES", StringComparison.Ordinal) ||
                              string.Equals(reader.GetString(0), "WRITES", StringComparison.OrdinalIgnoreCase)
                    ? "write"
                    : "read");
            }
        }

        var pattern = "%" + EscapeLike(trimmed) + "%";
        using (var command = connection.CreateCommand())
        {
            var useFts = trimmed.Length >= 3 && TableExists(connection, "knowledge_search");
            command.CommandText = useFts
                ? """
                    SELECT s.node_id
                    FROM knowledge_search s
                    WHERE knowledge_search MATCH @ftsQuery
                      AND s.kind = 'Network'
                      AND s.field = 'logicStatements';
                    """
                : """
                    SELECT n.id
                    FROM graph_nodes n
                    JOIN graph_node_properties p ON p.node_id = n.id AND p.name = 'logicStatements'
                    WHERE n.kind = 'Network' AND p.value LIKE @pattern ESCAPE '\';
                    """;
            command.Parameters.AddWithValue(
                useFts ? "@ftsQuery" : "@pattern",
                useFts ? FtsLiteral(trimmed) : pattern);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                directionsByNetwork.TryAdd(reader.GetString(0), new SortedSet<string>(StringComparer.Ordinal));
            }
        }

        var networkDetails = ReadNetworkDetails(connection, directionsByNetwork.Keys);
        var rows = new List<VariableUsageRow>();
        foreach (var entry in directionsByNetwork)
        {
            networkDetails.TryGetValue(entry.Key, out var detail);
            var directions = entry.Value.Count == 0
                ? (IEnumerable<string>)new[] { "mention" }
                : entry.Value;
            foreach (var direction in directions)
            {
                rows.Add(new VariableUsageRow(
                    detail?.BlockName,
                    detail?.BlockKind,
                    detail?.Index,
                    detail?.Title,
                    direction,
                    entry.Key));
            }
        }

        var ordered = rows
            .OrderBy(row => row.Block, StringComparer.OrdinalIgnoreCase)
            .ThenBy(row => row.NetworkIndex ?? int.MaxValue)
            .ThenBy(row => row.Access, StringComparer.Ordinal)
            .ToList();
        var truncated = ordered.Count > limit;
        if (truncated)
        {
            ordered.RemoveRange(limit, ordered.Count - limit);
        }

        return new
        {
            variable = trimmed,
            matchedNodes = matchedNodeIds.OrderBy(id => id, StringComparer.Ordinal).ToArray(),
            usages = ordered,
            truncated,
        };
    }

    private static List<string> ResolveVariableNodeIds(SqliteConnection connection, string variable)
    {
        var symbols = new List<(string Id, string Name)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id, name
                FROM graph_nodes
                WHERE kind = 'Variable' AND (name = @variable COLLATE NOCASE OR name LIKE @suffix ESCAPE '\')
                ORDER BY id;
                """;
            command.Parameters.AddWithValue("@variable", variable);
            command.Parameters.AddWithValue("@suffix", "%." + EscapeLike(variable));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                symbols.Add((reader.GetString(0), reader.GetString(1)));
            }
        }

        var resolved = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in symbols)
        {
            resolved.Add(symbol.Id);
        }

        if (symbols.Count > 0)
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"""
                    SELECT to_node_id FROM graph_edges
                    WHERE type = 'REFERS_TO' AND {InClause(command, "from_node_id", "s", symbols.Select(symbol => symbol.Id).ToList())};
                    """;
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    resolved.Add(reader.GetString(0));
                }
            }

            // Fallback for databases built before the REFERS_TO pass existed: derive the
            // db-member id candidates from the dotted symbol names (deepest chain included).
            var candidates = new List<string>();
            foreach (var (id, name) in symbols)
            {
                var markerIndex = id.IndexOf("symbol:", StringComparison.Ordinal);
                var prefix = markerIndex < 0 ? string.Empty : id.Substring(0, markerIndex);
                var segments = name.Split('.');
                for (var depth = 2; depth <= segments.Length; depth++)
                {
                    candidates.Add($"{prefix}db-member:{segments[0]}:{string.Join(".", segments.Skip(1).Take(depth - 1))}");
                }
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = $"""
                    SELECT id FROM graph_nodes
                    WHERE kind = 'DB Member' AND {InClause(command, "id", "m", candidates)};
                    """;
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    resolved.Add(reader.GetString(0));
                }
            }
        }

        return resolved.ToList();
    }

    private static Dictionary<string, VariableUsageNetwork> ReadNetworkDetails(
        SqliteConnection connection,
        IEnumerable<string> networkIds)
    {
        var ids = networkIds.ToList();
        var details = new Dictionary<string, VariableUsageNetwork>(StringComparer.Ordinal);
        if (ids.Count == 0)
        {
            return details;
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT n.id, idx.value, title.value, b.name, b.kind
            FROM graph_nodes n
            LEFT JOIN graph_node_properties idx ON idx.node_id = n.id AND idx.name = 'networkIndex'
            LEFT JOIN graph_node_properties title ON title.node_id = n.id AND title.name = 'title'
            LEFT JOIN graph_edges c ON c.to_node_id = n.id AND c.type = 'CONTAINS'
            LEFT JOIN graph_nodes b ON b.id = c.from_node_id AND b.kind IN ('OB', 'FB', 'FC')
            WHERE {InClause(command, "n.id", "n", ids)};
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var indexText = reader.IsDBNull(1) ? null : reader.GetString(1);
            int? index = int.TryParse(indexText, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
            details[reader.GetString(0)] = new VariableUsageNetwork(
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                index,
                reader.IsDBNull(2) ? null : reader.GetString(2));
        }

        return details;
    }

    private static string InClause(SqliteCommand command, string column, string parameterPrefix, IReadOnlyList<string> values)
    {
        var parameterNames = new List<string>();
        for (var index = 0; index < values.Count; index++)
        {
            var parameterName = $"@{parameterPrefix}{index}";
            command.Parameters.AddWithValue(parameterName, values[index]);
            parameterNames.Add(parameterName);
        }

        return $"{column} IN ({string.Join(", ", parameterNames)})";
    }

    private static string EscapeLike(string text)
    {
        return text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    }

    private static string FtsLiteral(string text) =>
        "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private sealed record VariableUsageNetwork(string? BlockName, string? BlockKind, int? Index, string? Title);

    private sealed record VariableUsageRow(string? Block, string? BlockKind, int? NetworkIndex, string? NetworkTitle, string Access, string NetworkId);

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

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE name = @name;";
        command.Parameters.AddWithValue("@name", tableName);
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
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

    private static NetworkInfo? ReadNetwork(
        SqliteConnection connection,
        string blockId,
        int networkIndex)
    {
        if (TableExists(connection, "knowledge_networks"))
        {
            using var projected = connection.CreateCommand();
            projected.CommandText = """
                SELECT network_id, network_index, compile_unit_id,
                       title, language, logic_statements
                FROM knowledge_networks
                WHERE block_id = @blockId AND network_index = @networkIndex
                LIMIT 1;
                """;
            projected.Parameters.AddWithValue("@blockId", blockId);
            projected.Parameters.AddWithValue("@networkIndex", networkIndex);
            using var projectedReader = projected.ExecuteReader();
            return projectedReader.Read() ? ReadProjectedNetworkInfo(projectedReader) : null;
        }

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
            JOIN graph_node_properties idx
              ON idx.node_id = network.id
             AND idx.name = 'networkIndex'
             AND idx.value = @networkIndex
            LEFT JOIN graph_node_properties cu ON cu.node_id = network.id AND cu.name = 'compileUnitId'
            LEFT JOIN graph_node_properties title ON title.node_id = network.id AND title.name = 'title'
            LEFT JOIN graph_node_properties lang ON lang.node_id = network.id AND lang.name = 'language'
            LEFT JOIN graph_node_properties logic ON logic.node_id = network.id AND logic.name = 'logicStatements'
            WHERE e.type = 'CONTAINS' AND e.from_node_id = @blockId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@blockId", blockId);
        command.Parameters.AddWithValue(
            "@networkIndex",
            networkIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadNetworkInfo(reader) : null;
    }

    private static NetworkInfo ReadProjectedNetworkInfo(SqliteDataReader reader)
    {
        return new NetworkInfo(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    private static NetworkInfo ReadNetworkInfo(SqliteDataReader reader)
    {
        var indexText = reader.IsDBNull(1) ? null : reader.GetString(1);
        int? index = int.TryParse(
            indexText,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : null;
        return new NetworkInfo(
            reader.GetString(0),
            index,
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));
    }

    private static List<NetworkInfo> ReadNetworks(SqliteConnection connection, string blockId)
    {
        if (TableExists(connection, "knowledge_networks"))
        {
            using var projected = connection.CreateCommand();
            projected.CommandText = """
                SELECT network_id, network_index, compile_unit_id,
                       title, language, logic_statements
                FROM knowledge_networks
                WHERE block_id = @blockId
                ORDER BY network_index;
                """;
            projected.Parameters.AddWithValue("@blockId", blockId);
            var projectedNetworks = new List<NetworkInfo>();
            using var projectedReader = projected.ExecuteReader();
            while (projectedReader.Read())
            {
                projectedNetworks.Add(ReadProjectedNetworkInfo(projectedReader));
            }

            return projectedNetworks;
        }

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
        catch (ComponentIdentityMismatchException ex)
        {
            return ToolJson.Fail(
                ex.Code,
                ex.Message,
                "Keep the overlay XML name/type at the baseline path, or run a full ingest for a new component.");
        }
        catch (ComponentProvenanceUnavailableException ex)
        {
            return ToolJson.Fail(
                ex.Code,
                ex.Message,
                "Run ingest_source to rebuild the device database before applying partial updates.");
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
