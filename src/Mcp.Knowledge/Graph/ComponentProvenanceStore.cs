using Mcp.Knowledge.Import;
using Microsoft.Data.Sqlite;

namespace Mcp.Knowledge.Graph;

public static class ComponentProvenanceStore
{
    public static void Replace(
        string dbPath,
        string exportRoot,
        string relativePath,
        string deviceName = "")
    {
        var replacement = ManifestImporter.ImportComponent(
            exportRoot,
            relativePath,
            deviceName);
        Replace(
            dbPath,
            replacement.Graph,
            replacement.Components.Single());
    }

    public static void Replace(
        string dbPath,
        SemanticPlcGraph replacementGraph,
        ComponentImport replacement)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
        {
            throw new ArgumentException("SQLite path is required.", nameof(dbPath));
        }

        ArgumentNullException.ThrowIfNull(replacementGraph);
        ArgumentNullException.ThrowIfNull(replacement);
        if (!File.Exists(dbPath))
        {
            throw new FileNotFoundException(
                $"Knowledge database '{dbPath}' was not found.",
                dbPath);
        }

        SqliteSemanticGraphStore.EnsureSqliteInitialized();
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        Execute(connection, null, "PRAGMA foreign_keys = ON;");
        EnsureProvenanceAvailable(connection);
        SqliteSemanticGraphStore.EnsureSchema(connection);

        using var transaction = connection.BeginTransaction();
        ValidateStoredIdentity(connection, transaction, replacement);
        (replacementGraph, replacement) = AlignProjectContainment(
            connection,
            transaction,
            replacementGraph,
            replacement);
        var oldEdgeIds = ReadIds(
            connection,
            transaction,
            "SELECT edge_id FROM source_component_edges WHERE component_key = $component_key;",
            replacement.ComponentKey);
        var oldNodeIds = ReadIds(
            connection,
            transaction,
            "SELECT node_id FROM source_component_nodes WHERE component_key = $component_key;",
            replacement.ComponentKey);

        Execute(
            connection,
            transaction,
            "DELETE FROM source_component_edges WHERE component_key = $component_key;",
            ("$component_key", replacement.ComponentKey));
        foreach (var edgeId in oldEdgeIds)
        {
            Execute(
                connection,
                transaction,
                """
                DELETE FROM graph_edges
                WHERE id = $edge_id
                  AND NOT EXISTS (
                    SELECT 1
                    FROM source_component_edges
                    WHERE edge_id = $edge_id
                  );
                """,
                ("$edge_id", edgeId));
        }

        Execute(
            connection,
            transaction,
            "DELETE FROM source_component_nodes WHERE component_key = $component_key;",
            ("$component_key", replacement.ComponentKey));
        foreach (var nodeId in oldNodeIds)
        {
            Execute(
                connection,
                transaction,
                """
                DELETE FROM graph_nodes
                WHERE id = $node_id
                  AND NOT EXISTS (
                    SELECT 1
                    FROM source_component_nodes
                    WHERE node_id = $node_id
                  )
                  AND NOT EXISTS (
                    SELECT 1
                    FROM graph_edges
                    WHERE from_node_id = $node_id OR to_node_id = $node_id
                  );
                """,
                ("$node_id", nodeId));
        }

        Execute(
            connection,
            transaction,
            "DELETE FROM source_components WHERE component_key = $component_key;",
            ("$component_key", replacement.ComponentKey));

        foreach (var node in replacementGraph.Nodes)
        {
            UpsertNode(connection, transaction, node);
        }

        foreach (var edge in replacementGraph.Edges)
        {
            UpsertEdge(connection, transaction, edge);
        }

        Save(connection, transaction, new[] { replacement });
        SqliteSemanticGraphStore.RebuildDerivedData(connection, transaction);
        transaction.Commit();
    }

    internal static void Load(
        SqliteConnection connection,
        SemanticPlcGraph graph)
    {
        if (!TableExists(connection, "source_components") ||
            !TableExists(connection, "source_component_nodes") ||
            !TableExists(connection, "source_component_edges"))
        {
            return;
        }

        var nodeIds = ReadOwnership(
            connection,
            "SELECT component_key, node_id FROM source_component_nodes ORDER BY component_key, node_id;");
        var edgeIds = ReadOwnership(
            connection,
            "SELECT component_key, edge_id FROM source_component_edges ORDER BY component_key, edge_id;");
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT component_key, relative_path, content_hash
            FROM source_components
            ORDER BY component_key;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var componentKey = reader.GetString(0);
            graph.RegisterComponentImport(new ComponentImport(
                componentKey,
                reader.GetString(1),
                reader.GetString(2),
                nodeIds.TryGetValue(componentKey, out var componentNodeIds)
                    ? componentNodeIds
                    : new HashSet<string>(StringComparer.Ordinal),
                edgeIds.TryGetValue(componentKey, out var componentEdgeIds)
                    ? componentEdgeIds
                    : new HashSet<string>(StringComparer.Ordinal)));
        }
    }

    internal static void Save(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<ComponentImport> components)
    {
        foreach (var component in components.OrderBy(
                     item => item.ComponentKey,
                     StringComparer.Ordinal))
        {
            Execute(
                connection,
                transaction,
                """
                INSERT INTO source_components (component_key, relative_path, content_hash)
                VALUES ($component_key, $relative_path, $content_hash);
                """,
                ("$component_key", component.ComponentKey),
                ("$relative_path", component.RelativePath),
                ("$content_hash", component.ContentHash));
            foreach (var nodeId in component.NodeIds.OrderBy(id => id, StringComparer.Ordinal))
            {
                Execute(
                    connection,
                    transaction,
                    """
                    INSERT INTO source_component_nodes (component_key, node_id)
                    VALUES ($component_key, $node_id);
                    """,
                    ("$component_key", component.ComponentKey),
                    ("$node_id", nodeId));
            }

            foreach (var edgeId in component.EdgeIds.OrderBy(id => id, StringComparer.Ordinal))
            {
                Execute(
                    connection,
                    transaction,
                    """
                    INSERT INTO source_component_edges (component_key, edge_id)
                    VALUES ($component_key, $edge_id);
                    """,
                    ("$component_key", component.ComponentKey),
                    ("$edge_id", edgeId));
            }
        }
    }

    private static void ValidateStoredIdentity(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ComponentImport replacement)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT component_key, relative_path
            FROM source_components
            WHERE component_key = $component_key OR relative_path = $relative_path
            ORDER BY component_key;
            """;
        command.Parameters.AddWithValue("$component_key", replacement.ComponentKey);
        command.Parameters.AddWithValue("$relative_path", replacement.RelativePath);
        using var reader = command.ExecuteReader();
        var identities = new List<(string ComponentKey, string RelativePath)>();
        while (reader.Read())
        {
            identities.Add((reader.GetString(0), reader.GetString(1)));
        }

        if (identities.Count != 1 ||
            !string.Equals(
                identities[0].ComponentKey,
                replacement.ComponentKey,
                StringComparison.Ordinal) ||
            !string.Equals(
                identities[0].RelativePath,
                replacement.RelativePath,
                StringComparison.Ordinal))
        {
            throw new ComponentIdentityMismatchException(
                $"Replacement component '{replacement.ComponentKey}' at '{replacement.RelativePath}' " +
                "does not match the component identity already stored in the knowledge database.");
        }
    }

    private static (SemanticPlcGraph Graph, ComponentImport Component)
        AlignProjectContainment(
            SqliteConnection connection,
            SqliteTransaction transaction,
            SemanticPlcGraph replacementGraph,
            ComponentImport replacement)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT DISTINCT edge.from_node_id
            FROM source_component_edges ownership
            JOIN graph_edges edge ON edge.id = ownership.edge_id
            JOIN graph_nodes source ON source.id = edge.from_node_id
            WHERE ownership.component_key = $component_key
              AND edge.type = $contains
              AND source.kind = $project_kind;
            """;
        command.Parameters.AddWithValue("$component_key", replacement.ComponentKey);
        command.Parameters.AddWithValue("$contains", SemanticRelationshipType.Contains);
        command.Parameters.AddWithValue("$project_kind", SemanticNodeKind.Project);
        var storedProjectIds = new List<string>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                storedProjectIds.Add(reader.GetString(0));
            }
        }

        var replacementProjectIds = replacementGraph
            .FindNodesByKind(SemanticNodeKind.Project)
            .Select(node => node.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (replacementProjectIds.Count != 1)
        {
            return (replacementGraph, replacement);
        }

        var storedProjectId = storedProjectIds.Count == 1
            ? storedProjectIds[0]
            : null;
        var edgeIdMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var alignedGraph = new SemanticPlcGraph();
        foreach (var node in replacementGraph.Nodes)
        {
            if (!replacementProjectIds.Contains(node.Id))
            {
                alignedGraph.UpsertNode(node);
            }
        }

        foreach (var edge in replacementGraph.Edges)
        {
            if (replacementProjectIds.Contains(edge.ToNodeId))
            {
                continue;
            }

            if (replacementProjectIds.Contains(edge.FromNodeId))
            {
                if (storedProjectId == null)
                {
                    continue;
                }

                var alignedEdge = new SemanticGraphEdge(
                    TiaXmlSemanticGraphImporter.EdgeId(
                        storedProjectId,
                        edge.ToNodeId,
                        edge.Type,
                        edge.Properties),
                    storedProjectId,
                    edge.ToNodeId,
                    edge.Type,
                    edge.Properties);
                alignedGraph.UpsertEdge(alignedEdge);
                edgeIdMap[edge.Id] = alignedEdge.Id;
                continue;
            }

            alignedGraph.UpsertEdge(edge);
        }

        return (
            alignedGraph,
            replacement with
            {
                NodeIds = replacement.NodeIds
                    .Where(nodeId => !replacementProjectIds.Contains(nodeId))
                    .ToHashSet(StringComparer.Ordinal),
                EdgeIds = replacement.EdgeIds
                    .Select(edgeId =>
                        edgeIdMap.TryGetValue(edgeId, out var alignedId)
                            ? alignedId
                            : edgeId)
                    .Where(edgeId => alignedGraph.Edges.Any(edge => edge.Id == edgeId))
                    .ToHashSet(StringComparer.Ordinal),
            });
    }

    private static IReadOnlyList<string> ReadIds(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        string componentKey)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddWithValue("$component_key", componentKey);
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }

    private static Dictionary<string, HashSet<string>> ReadOwnership(
        SqliteConnection connection,
        string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var ownership = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var componentKey = reader.GetString(0);
            if (!ownership.TryGetValue(componentKey, out var ids))
            {
                ids = new HashSet<string>(StringComparer.Ordinal);
                ownership.Add(componentKey, ids);
            }

            ids.Add(reader.GetString(1));
        }

        return ownership;
    }

    private static void EnsureProvenanceAvailable(SqliteConnection connection)
    {
        if (!TableExists(connection, "source_components") ||
            !TableExists(connection, "source_component_nodes") ||
            !TableExists(connection, "source_component_edges"))
        {
            throw new ComponentProvenanceUnavailableException(
                "The knowledge database predates component provenance. Rebuild it before updating selected components.");
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM source_components;";
        if (Convert.ToInt64(command.ExecuteScalar()) == 0)
        {
            throw new ComponentProvenanceUnavailableException(
                "The knowledge database has no component provenance. Rebuild it before updating selected components.");
        }
    }

    private static bool TableExists(
        SqliteConnection connection,
        string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table' AND name = $table_name;
            """;
        command.Parameters.AddWithValue("$table_name", tableName);
        return Convert.ToInt64(command.ExecuteScalar()) == 1;
    }

    private static void UpsertNode(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SemanticGraphNode node)
    {
        if (IsReferencePlaceholder(node) &&
            StoredNodeIsConcrete(connection, transaction, node.Id))
        {
            return;
        }

        Execute(
            connection,
            transaction,
            """
            INSERT INTO graph_nodes (id, kind, name)
            VALUES ($id, $kind, $name)
            ON CONFLICT(id) DO UPDATE SET kind = excluded.kind, name = excluded.name;
            DELETE FROM graph_node_properties WHERE node_id = $id;
            """,
            ("$id", node.Id),
            ("$kind", node.Kind),
            ("$name", node.Name));
        foreach (var property in node.Properties.OrderBy(
                     item => item.Key,
                     StringComparer.Ordinal))
        {
            Execute(
                connection,
                transaction,
                """
                INSERT INTO graph_node_properties (node_id, name, value)
                VALUES ($node_id, $name, $value);
                """,
                ("$node_id", node.Id),
                ("$name", property.Key),
                ("$value", property.Value));
        }
    }

    private static void UpsertEdge(
        SqliteConnection connection,
        SqliteTransaction transaction,
        SemanticGraphEdge edge)
    {
        Execute(
            connection,
            transaction,
            """
            INSERT INTO graph_edges (id, from_node_id, to_node_id, type)
            VALUES ($id, $from_node_id, $to_node_id, $type)
            ON CONFLICT(id) DO UPDATE SET
              from_node_id = excluded.from_node_id,
              to_node_id = excluded.to_node_id,
              type = excluded.type;
            DELETE FROM graph_edge_properties WHERE edge_id = $id;
            """,
            ("$id", edge.Id),
            ("$from_node_id", edge.FromNodeId),
            ("$to_node_id", edge.ToNodeId),
            ("$type", edge.Type));
        foreach (var property in edge.Properties.OrderBy(
                     item => item.Key,
                     StringComparer.Ordinal))
        {
            Execute(
                connection,
                transaction,
                """
                INSERT INTO graph_edge_properties (edge_id, name, value)
                VALUES ($edge_id, $name, $value);
                """,
                ("$edge_id", edge.Id),
                ("$name", property.Key),
                ("$value", property.Value));
        }
    }

    private static bool IsReferencePlaceholder(SemanticGraphNode node)
    {
        return node.Properties.TryGetValue("declaredByReference", out var value) &&
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool StoredNodeIsConcrete(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string nodeId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM graph_nodes node
            WHERE node.id = $node_id
              AND NOT EXISTS (
                SELECT 1
                FROM graph_node_properties property
                WHERE property.node_id = node.id
                  AND property.name = 'declaredByReference'
                  AND property.value = 'true'
              );
            """;
        command.Parameters.AddWithValue("$node_id", nodeId);
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        params (string Name, string Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        command.ExecuteNonQuery();
    }
}
