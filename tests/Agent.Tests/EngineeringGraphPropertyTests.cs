using Agent.Workbench;
using Agent.Workbench.EngineeringGraph;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Agent.Tests;

public sealed class EngineeringGraphPropertyTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"engineering-graph-properties-{Guid.NewGuid():N}");

    [Fact]
    public void ReplacesOneNodesPropertySetSoAnOmittedPropertyDisappears()
    {
        using var store = new EngineeringGraphStore(_root);
        var service = new EngineeringGraphService(store, "wb-1", id => id == "wt-1");
        service.RegisterEntity(new GraphEntity(GraphEntityKind.Session, "session-1", "wb-1", "wt-1"));

        Assert.Equal(5, service.ReplaceProperties(GraphEntityKind.Session, "session-1", [
            GraphProperty.TextValue("title", "test", "Session one"),
            GraphProperty.NumberValue("priority", "test", 3),
            GraphProperty.FlagValue("pinned", "test", true),
            GraphProperty.TimestampValue("updatedAt", "test", new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero)),
            GraphProperty.JsonValue("fingerprints", "test", """{"Code":"AAAA1111"}"""),
        ]));

        var properties = Properties(service.GetProperties(GraphEntityKind.Session, "session-1"));
        Assert.Equal(["fingerprints", "pinned", "priority", "title", "updatedAt"], properties.Keys);
        Assert.Equal("Session one", properties["title"].Text);
        Assert.Equal(GraphPropertyValueKind.Text, properties["title"].Kind);
        Assert.Equal(3d, properties["priority"].Number);
        Assert.Equal(GraphPropertyValueKind.Number, properties["priority"].Kind);
        Assert.True(properties["pinned"].Flag);
        Assert.Equal(GraphPropertyValueKind.Flag, properties["pinned"].Kind);
        Assert.Equal(new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero), properties["updatedAt"].Timestamp);
        Assert.Equal(GraphPropertyValueKind.Timestamp, properties["updatedAt"].Kind);
        Assert.Equal("""{"Code":"AAAA1111"}""", properties["fingerprints"].Json);
        Assert.Equal(GraphPropertyValueKind.Json, properties["fingerprints"].Kind);
        Assert.Equal("test", properties["title"].Source);

        // The new set replaces the old one: the omitted properties are gone, the changed one is
        // rewritten, and the untouched ones are not counted.
        Assert.Equal(3, service.ReplaceProperties(GraphEntityKind.Session, "session-1", [
            GraphProperty.TextValue("title", "test", "Session two"),
            GraphProperty.NumberValue("priority", "test", 3),
            GraphProperty.TimestampValue("updatedAt", "test", new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero)),
        ]));

        properties = Properties(service.GetProperties(GraphEntityKind.Session, "session-1"));
        Assert.Equal(["priority", "title", "updatedAt"], properties.Keys);
        Assert.Equal("Session two", properties["title"].Text);
    }

    [Fact]
    public void ReadsEveryNodePropertyOfOneDeviceAndNothingOfAnother()
    {
        using var store = new EngineeringGraphStore(_root);
        var service = new EngineeringGraphService(store, "wb-1", id => id == "wt-1");
        service.ReplaceDeviceProperties("plc-1", [
            DeviceNode("plc-1", [GraphProperty.TextValue(DevicePropertyNames.PlcName, "test", "PLC_1")]),
            SourceNode("plc-1", "ob-1", "Blocks/Main [OB1].xml", [GraphProperty.TextValue(SourceObjectPropertyNames.Name, "test", "Main")]),
            SourceNode("plc-1", "tags-1", "Tags/Inputs.xml", [GraphProperty.TextValue(SourceObjectPropertyNames.Name, "test", "Inputs")]),
        ]);
        service.ReplaceDeviceProperties("plc-2", [
            DeviceNode("plc-2", [GraphProperty.TextValue(DevicePropertyNames.PlcName, "test", "PLC_2")]),
        ]);

        // One device-scoped read: the device node plus every source object it owns.
        var byId = service.GetDeviceProperties("plc-1");

        Assert.Equal(["plc-1", "plc-1:ob-1", "plc-1:tags-1"], byId.Keys.OrderBy(key => key, StringComparer.Ordinal));
        Assert.Equal("PLC_1", Properties(byId["plc-1"])[DevicePropertyNames.PlcName].Text);
        Assert.Equal("Main", Properties(byId["plc-1:ob-1"])[SourceObjectPropertyNames.Name].Text);
        Assert.DoesNotContain("plc-2", byId.Keys);
    }

    [Fact]
    public void WritesTheSameDevicePropertySetAgainWithoutWritingARow()
    {
        using var store = new EngineeringGraphStore(_root);
        var service = new EngineeringGraphService(store, "wb-1", id => id == "wt-1");
        var nodes = new List<GraphNodePropertySet>
        {
            DeviceNode("plc-1", [
                GraphProperty.TextValue(DevicePropertyNames.PlcName, "test", "PLC_1"),
                GraphProperty.FlagValue(DevicePropertyNames.ExportIsSafetyDevice, "test", null),
            ]),
            SourceNode("plc-1", "ob-1", "Blocks/Main [OB1].xml", [
                GraphProperty.TextValue(SourceObjectPropertyNames.ContentHash, "test", "HASH-A"),
                GraphProperty.TimestampValue(SourceObjectPropertyNames.ModifiedDate, "test", null),
            ]),
        };

        var first = service.ReplaceDeviceProperties("plc-1", nodes);

        Assert.Equal(4, first.PropertyRowsWritten);
        Assert.Equal(4, first.Inserted);

        var second = service.ReplaceDeviceProperties("plc-1", nodes);

        // Nothing changed: an unchanged null-valued typed property is not rewritten either.
        Assert.Equal(0, second.PropertyRowsWritten);
        Assert.Equal(0, second.Inserted);
        Assert.Equal(0, second.Updated);
        Assert.Equal(0, second.Deleted);
        Assert.Equal(0, second.NodesRemoved);
    }

    [Fact]
    public void WritesOneDevicesWholePropertySetInASingleTransaction()
    {
        using var store = new EngineeringGraphStore(_root);
        var service = new EngineeringGraphService(store, "wb-1", id => id == "wt-1");

        // The second node's set names one property twice, so the write is rejected after the first
        // node's rows were already written. One transaction means none of them survive.
        var rejected = Assert.Throws<EngineeringGraphConstraintException>(() => service.ReplaceDeviceProperties("plc-1", [
            DeviceNode("plc-1", [GraphProperty.TextValue(DevicePropertyNames.PlcName, "test", "PLC_1")]),
            SourceNode("plc-1", "ob-1", "Blocks/Main [OB1].xml", [
                GraphProperty.TextValue(SourceObjectPropertyNames.Name, "test", "Main"),
                GraphProperty.TextValue(SourceObjectPropertyNames.Name, "test", "Other"),
            ]),
        ]));

        Assert.Contains("set twice", rejected.Message, StringComparison.Ordinal);
        Assert.Equal(0, Scalar(store.Connection, "SELECT COUNT(*) FROM graph_entity_properties;"));
        Assert.Equal(0, Scalar(store.Connection, "SELECT COUNT(*) FROM graph_entities;"));
    }

    [Fact]
    public void PropertyRowsSurviveNodeRegistrationBecauseNothingCascades()
    {
        using var store = new EngineeringGraphStore(_root);
        var service = new EngineeringGraphService(store, "wb-1", id => id == "wt-1");
        var entity = new GraphEntity(GraphEntityKind.Device, "plc-1", "wb-1", "wt-1", "plc-1");
        service.ReplaceDeviceProperties("plc-1", [
            new GraphNodePropertySet(entity, [GraphProperty.TextValue(DevicePropertyNames.PlcName, "test", "PLC_1")]),
        ]);

        // Every stage click and anchor read registers the whole manifest again, and registration uses
        // INSERT OR REPLACE — which deletes the replaced entity row. A cascading foreign key would take
        // the device's facts with it (ADR-0011, Negative Consequences).
        service.RegisterEntities([entity]);
        service.RegisterEntity(entity);

        Assert.Equal("PLC_1", Assert.Single(service.GetProperties(GraphEntityKind.Device, "plc-1")).Text);
        Assert.Equal(0, Scalar(store.Connection, "SELECT COUNT(*) FROM pragma_foreign_key_list('graph_entity_properties');"));
    }

    private static GraphNodePropertySet DeviceNode(string deviceId, IReadOnlyList<GraphProperty> properties) =>
        new(new GraphEntity(GraphEntityKind.Device, deviceId, "wb-1", "wt-1", deviceId), properties);

    private static GraphNodePropertySet SourceNode(string deviceId, string manifestId, string relativePath, IReadOnlyList<GraphProperty> properties) =>
        new(new GraphEntity(GraphEntityKind.SourceObject, $"{deviceId}:{manifestId}", "wb-1", "wt-1", deviceId, relativePath), properties);

    private static Dictionary<string, GraphProperty> Properties(IReadOnlyList<GraphProperty> properties) =>
        properties.ToDictionary(property => property.Name, StringComparer.Ordinal);

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
