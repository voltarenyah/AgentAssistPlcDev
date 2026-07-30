using Agent.Chat;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Agent.Tests;

public sealed class BlockInterfaceReaderTests : IDisposable
{
    private readonly string dbPath = Path.Combine(
        Path.GetTempPath(),
        "block-interface-" + Guid.NewGuid().ToString("N") + ".db");

    [Fact]
    public void ReadsCompactFbInterfaceSummary()
    {
        Seed();

        var summary = BlockInterfaceReader.Read(dbPath, "FB_LAD_SimulateCylinder");

        Assert.Equal("block:FB_LAD_SimulateCylinder", summary.BlockId);
        Assert.Equal("FB", summary.Kind);
        Assert.Equal("FB_LAD_SimulateCylinder_DB", summary.InstanceDb);
        Assert.Contains(summary.Members, member => member.Name == "btn_forward");
        Assert.Contains(summary.CallSites, site => site.CallerBlock == "Main" && site.NetworkId == "network:Main:2");
        Assert.Contains(summary.Networks, network => network.NetworkId == "network:FB_LAD_SimulateCylinder:1");
    }

    private void Seed()
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE graph_nodes (id TEXT PRIMARY KEY, kind TEXT NOT NULL, name TEXT NOT NULL);
            CREATE TABLE graph_node_properties (node_id TEXT NOT NULL, name TEXT NOT NULL, value TEXT NOT NULL);
            CREATE TABLE graph_edges (id TEXT PRIMARY KEY, from_node_id TEXT NOT NULL, to_node_id TEXT NOT NULL, type TEXT NOT NULL);
            CREATE TABLE graph_edge_properties (edge_id TEXT NOT NULL, name TEXT NOT NULL, value TEXT NOT NULL);
            INSERT INTO graph_nodes VALUES
              ('block:FB_LAD_SimulateCylinder','FB','FB_LAD_SimulateCylinder'),
              ('block:Main','OB','Main'),
              ('db:FB_LAD_SimulateCylinder_DB','Instance DB','FB_LAD_SimulateCylinder_DB'),
              ('db-member:FB_LAD_SimulateCylinder_DB:btn_forward','DB Member','btn_forward'),
              ('network:FB_LAD_SimulateCylinder:1','Network','Network 1'),
              ('network:Main:2','Network','Network 2');
            INSERT INTO graph_node_properties VALUES
              ('block:FB_LAD_SimulateCylinder','sourceFile','Blocks\FB_LAD_SimulateCylinder [FB1].xml'),
              ('network:FB_LAD_SimulateCylinder:1','logicStatements','outputGoForwardPos := TRUE;'),
              ('network:FB_LAD_SimulateCylinder:1','language','LAD'),
              ('network:FB_LAD_SimulateCylinder:1','index','1'),
              ('network:Main:2','logicStatements','FB_LAD_SimulateCylinder(btn_forward := Btn_ForwardCommand);');
            INSERT INTO graph_edges VALUES
              ('edge:instance','db:FB_LAD_SimulateCylinder_DB','block:FB_LAD_SimulateCylinder','INSTANCE_OF'),
              ('edge:member','db:FB_LAD_SimulateCylinder_DB','db-member:FB_LAD_SimulateCylinder_DB:btn_forward','CONTAINS'),
              ('edge:contains-network','block:FB_LAD_SimulateCylinder','network:FB_LAD_SimulateCylinder:1','CONTAINS'),
              ('edge:call','block:Main','block:FB_LAD_SimulateCylinder','CALLS');
            INSERT INTO graph_node_properties VALUES
              ('db-member:FB_LAD_SimulateCylinder_DB:btn_forward','path','btn_forward');
            INSERT INTO graph_edge_properties VALUES
              ('edge:call','networkId','network:Main:2'),
              ('edge:call','networkIndex','2'),
              ('edge:call','sourceFile','Blocks\Main [OB1].xml');
            """;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(dbPath))
            File.Delete(dbPath);
    }
}
