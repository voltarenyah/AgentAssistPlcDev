// New code (not ported): get_schema payload vocabulary for mcp-knowledge.
// Node kinds / edge types are the subset produced by this step (buildnote/plan/mcp-knowledge.md §5);
// example queries are lifted from SemanticPlcGraphAgentGuide.Content.
namespace Mcp.Knowledge.Graph;

public sealed record SchemaExampleQuery(string Name, string Sql);

public static class SchemaVocabulary
{
    public static readonly IReadOnlyList<string> NodeKinds = new[]
    {
        SemanticNodeKind.Project,
        SemanticNodeKind.OrganizationBlock,
        SemanticNodeKind.FunctionBlock,
        SemanticNodeKind.Function,
        SemanticNodeKind.Network,
        SemanticNodeKind.Instruction,
        SemanticNodeKind.Variable,
        SemanticNodeKind.GlobalDataBlock,
        SemanticNodeKind.InstanceDataBlock,
        SemanticNodeKind.DataBlockMember,
        SemanticNodeKind.DataType,
        SemanticNodeKind.UserDataType,
        SemanticNodeKind.UserDataTypeMember,
        SemanticNodeKind.PlcTag,
        SemanticNodeKind.IoAddress,
    };

    public static readonly IReadOnlyList<string> EdgeTypes = new[]
    {
        SemanticRelationshipType.Contains,
        SemanticRelationshipType.Calls,
        SemanticRelationshipType.Reads,
        SemanticRelationshipType.Writes,
        SemanticRelationshipType.HasType,
        SemanticRelationshipType.InstanceOf,
        SemanticRelationshipType.ConnectedTo,
        SemanticRelationshipType.ExecutesBefore,
        SemanticRelationshipType.ExecutesAfter,
    };

    public static readonly IReadOnlyList<SchemaExampleQuery> ExampleQueries = new[]
    {
        new SchemaExampleQuery(
            "Count node kinds",
            "SELECT kind, COUNT(*) AS count\nFROM graph_nodes\nGROUP BY kind\nORDER BY kind;"),
        new SchemaExampleQuery(
            "Count relationship types",
            "SELECT type, COUNT(*) AS count\nFROM graph_edges\nGROUP BY type\nORDER BY type;"),
        new SchemaExampleQuery(
            "List blocks",
            "SELECT id, kind, name\nFROM graph_nodes\nWHERE kind IN ('OB', 'FB', 'FC')\nORDER BY kind, name;"),
        new SchemaExampleQuery(
            "Which blocks call which blocks",
            """
            SELECT
              caller.kind AS caller_kind,
              caller.name AS caller,
              callee.kind AS callee_kind,
              callee.name AS callee,
              sf.value AS source_file,
              ni.value AS network_index
            FROM graph_edges e
            JOIN graph_nodes caller ON caller.id = e.from_node_id
            JOIN graph_nodes callee ON callee.id = e.to_node_id
            LEFT JOIN graph_edge_properties sf
              ON sf.edge_id = e.id AND sf.name = 'sourceFile'
            LEFT JOIN graph_edge_properties ni
              ON ni.edge_id = e.id AND ni.name = 'networkIndex'
            WHERE e.type = 'CALLS'
              AND caller.kind IN ('OB', 'FB', 'FC')
              AND callee.kind IN ('OB', 'FB', 'FC')
            ORDER BY caller.name, CAST(ni.value AS INTEGER), callee.name;
            """),
        new SchemaExampleQuery(
            "Which blocks read or write variables",
            """
            SELECT
              e.type AS access,
              block.kind AS block_kind,
              block.name AS block_name,
              variable.kind AS variable_kind,
              variable.name AS variable_name,
              sf.value AS source_file,
              ni.value AS network_index
            FROM graph_edges e
            JOIN graph_nodes block ON block.id = e.from_node_id
            JOIN graph_nodes variable ON variable.id = e.to_node_id
            LEFT JOIN graph_edge_properties sf
              ON sf.edge_id = e.id AND sf.name = 'sourceFile'
            LEFT JOIN graph_edge_properties ni
              ON ni.edge_id = e.id AND ni.name = 'networkIndex'
            WHERE e.type IN ('READS', 'WRITES')
              AND block.kind IN ('OB', 'FB', 'FC', 'Network')
            ORDER BY variable.name, access, block.name, CAST(ni.value AS INTEGER);
            """),
        new SchemaExampleQuery(
            "DB members and data types",
            """
            SELECT
              db.kind AS db_kind,
              db.name AS db_name,
              member.name AS member_name,
              dtype.name AS data_type
            FROM graph_edges contains
            JOIN graph_nodes db ON db.id = contains.from_node_id
            JOIN graph_nodes member ON member.id = contains.to_node_id
            LEFT JOIN graph_edges typed
              ON typed.from_node_id = member.id AND typed.type = 'HAS_TYPE'
            LEFT JOIN graph_nodes dtype ON dtype.id = typed.to_node_id
            WHERE contains.type = 'CONTAINS'
              AND db.kind IN ('Global DB', 'Instance DB')
              AND member.kind = 'DB Member'
            ORDER BY db.name, member.name;
            """),
        new SchemaExampleQuery(
            "Instance DB to FB relationship",
            "SELECT db.name AS instance_db, fb.name AS function_block\nFROM graph_edges e\nJOIN graph_nodes db ON db.id = e.from_node_id\nJOIN graph_nodes fb ON fb.id = e.to_node_id\nWHERE e.type = 'INSTANCE_OF'\nORDER BY db.name;"),
        new SchemaExampleQuery(
            "PLC tags connected to IO addresses",
            """
            SELECT
              tag.name AS tag_name,
              address.name AS io_address,
              dtype.name AS data_type
            FROM graph_edges connected
            JOIN graph_nodes tag ON tag.id = connected.from_node_id
            JOIN graph_nodes address ON address.id = connected.to_node_id
            LEFT JOIN graph_edges typed
              ON typed.from_node_id = tag.id AND typed.type = 'HAS_TYPE'
            LEFT JOIN graph_nodes dtype ON dtype.id = typed.to_node_id
            WHERE connected.type = 'CONNECTED_TO'
              AND tag.kind = 'PLC Tag'
              AND address.kind = 'IO Address'
            ORDER BY address.name, tag.name;
            """),
        new SchemaExampleQuery(
            "UDT members and data types",
            """
            SELECT
              udt.name AS udt_name,
              member.name AS member_name,
              dtype.name AS data_type
            FROM graph_edges contains
            JOIN graph_nodes udt ON udt.id = contains.from_node_id
            JOIN graph_nodes member ON member.id = contains.to_node_id
            LEFT JOIN graph_edges typed
              ON typed.from_node_id = member.id AND typed.type = 'HAS_TYPE'
            LEFT JOIN graph_nodes dtype ON dtype.id = typed.to_node_id
            WHERE contains.type = 'CONTAINS'
              AND udt.kind = 'UDT'
              AND member.kind = 'UDT Member'
            ORDER BY udt.name, member.name;
            """),
    };
}
