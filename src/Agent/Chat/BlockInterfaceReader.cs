using Microsoft.Data.Sqlite;

namespace Agent.Chat;

public static class BlockInterfaceReader
{
    public static BlockInterfaceSummary Read(string dbPath, string blockName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(blockName);

        using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        connection.Open();

        var block = Single(connection, """
            SELECT id, kind, name
            FROM graph_nodes
            WHERE kind IN ('FB','FC','OB') AND name = $name
            LIMIT 1;
            """, ("$name", blockName));

        var blockId = block["id"] ?? throw new KeyNotFoundException("BLOCK_NOT_FOUND");
        var sourceFile = Scalar(connection, """
            SELECT value
            FROM graph_node_properties
            WHERE node_id = $id AND name = 'sourceFile'
            LIMIT 1;
            """, ("$id", blockId));

        var instanceDb = Scalar(connection, """
            SELECT db.name
            FROM graph_edges e
            JOIN graph_nodes db ON db.id = e.from_node_id
            WHERE e.type = 'INSTANCE_OF' AND e.to_node_id = $id
            LIMIT 1;
            """, ("$id", blockId));

        var members = Rows(connection, """
            SELECT member.name AS name,
                   path.value AS path,
                   dtype.name AS dataType
            FROM graph_edges contains
            JOIN graph_nodes db ON db.id = contains.from_node_id
            JOIN graph_nodes member ON member.id = contains.to_node_id
            LEFT JOIN graph_node_properties path
              ON path.node_id = member.id AND path.name = 'path'
            LEFT JOIN graph_edges typed
              ON typed.from_node_id = member.id AND typed.type = 'HAS_TYPE'
            LEFT JOIN graph_nodes dtype ON dtype.id = typed.to_node_id
            WHERE contains.type = 'CONTAINS'
              AND db.kind = 'Instance DB'
              AND db.name = $dbName
              AND member.kind = 'DB Member'
            ORDER BY COALESCE(path.value, member.name), member.name;
            """, ("$dbName", instanceDb ?? string.Empty))
            .Select(row => new BlockInterfaceMember(row["name"]!, row["path"], row["dataType"]))
            .ToList();

        var networks = Rows(connection, """
            SELECT network.id AS networkId,
                   idx.value AS networkIndex,
                   lang.value AS language,
                   logic.value AS logicStatements
            FROM graph_edges contains
            JOIN graph_nodes network ON network.id = contains.to_node_id
            LEFT JOIN graph_node_properties idx
              ON idx.node_id = network.id AND idx.name = 'index'
            LEFT JOIN graph_node_properties lang
              ON lang.node_id = network.id AND lang.name = 'language'
            LEFT JOIN graph_node_properties logic
              ON logic.node_id = network.id AND logic.name = 'logicStatements'
            WHERE contains.type = 'CONTAINS'
              AND contains.from_node_id = $id
              AND network.kind = 'Network'
            ORDER BY CAST(COALESCE(idx.value, '0') AS INTEGER), network.id;
            """, ("$id", blockId))
            .Select(row => new BlockNetworkSummary(
                row["networkId"]!,
                int.TryParse(row["networkIndex"], out var index) ? index : null,
                row["language"],
                row["logicStatements"]))
            .ToList();

        var callSites = Rows(connection, """
            SELECT caller.name AS callerBlock,
                   networkId.value AS networkId,
                   networkIndex.value AS networkIndex,
                   sourceFile.value AS sourceFile,
                   logic.value AS logicStatements
            FROM graph_edges call
            JOIN graph_nodes caller ON caller.id = call.from_node_id
            LEFT JOIN graph_edge_properties networkId
              ON networkId.edge_id = call.id AND networkId.name = 'networkId'
            LEFT JOIN graph_edge_properties networkIndex
              ON networkIndex.edge_id = call.id AND networkIndex.name = 'networkIndex'
            LEFT JOIN graph_edge_properties sourceFile
              ON sourceFile.edge_id = call.id AND sourceFile.name = 'sourceFile'
            LEFT JOIN graph_node_properties logic
              ON logic.node_id = networkId.value AND logic.name = 'logicStatements'
            WHERE call.type = 'CALLS'
              AND call.to_node_id = $id
              AND caller.kind IN ('OB','FB','FC')
            ORDER BY caller.name, CAST(COALESCE(networkIndex.value, '0') AS INTEGER);
            """, ("$id", blockId))
            .Select(row => new BlockCallSite(
                row["callerBlock"]!,
                row["networkId"] ?? string.Empty,
                int.TryParse(row["networkIndex"], out var index) ? index : null,
                row["sourceFile"],
                row["logicStatements"] ?? string.Empty))
            .ToList();

        return new BlockInterfaceSummary(
            blockId,
            block["kind"]!,
            block["name"]!,
            sourceFile,
            instanceDb,
            members,
            callSites,
            networks);
    }

    private static Dictionary<string, string?> Single(
        SqliteConnection connection,
        string sql,
        params (string Name, string Value)[] parameters)
    {
        var rows = Rows(connection, sql, parameters);
        return rows.Count == 0
            ? throw new KeyNotFoundException("BLOCK_NOT_FOUND")
            : rows[0];
    }

    private static string? Scalar(
        SqliteConnection connection,
        string sql,
        params (string Name, string Value)[] parameters) =>
        Rows(connection, sql, parameters).FirstOrDefault()?.Values.FirstOrDefault();

    private static List<Dictionary<string, string?>> Rows(
        SqliteConnection connection,
        string sql,
        params (string Name, string Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);

        using var reader = command.ExecuteReader();
        var rows = new List<Dictionary<string, string?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, string?>(StringComparer.Ordinal);
            for (var i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetString(i);
            rows.Add(row);
        }

        return rows;
    }
}
