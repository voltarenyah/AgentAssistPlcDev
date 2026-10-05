using Agent.Workbench;
using Agent.Workbench.EngineeringGraph;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace Agent.Tests;

/// <summary>
/// ADR-0011 Phase 5 (AC-001, AC-002): the hardware/AML subtree's configuration, bill of materials and
/// network views are projected facts, and the hardware routes serve them. The three values equal what
/// the readers derive from the same fixture, the AML is not parsed on the read path, and an
/// out-of-app change is detected at the boundary before it is served.
/// </summary>
public sealed class HardwareGraphProjectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"hardware-graph-{Guid.NewGuid():N}");

    [Fact]
    public void ServesTheReadersConfigurationBomAndNetworkFromTheGraphWithoutParsingTheAml()
    {
        var fixture = new HardwareFixture(_root);
        fixture.WriteExport(Aml);
        var worktreeRoot = fixture.WorktreeRoot;

        // The values the routes produced before the change, for the same files.
        var expectedConfiguration = HardwareConfigurationReader.Read(worktreeRoot);
        var expectedBom = HardwareListReader.ReadBom(worktreeRoot);
        var expectedNetwork = HardwareListReader.ReadNetwork(worktreeRoot);
        Assert.Equal("available", expectedConfiguration.State);

        using var store = new EngineeringGraphStore(_root);
        var graph = new EngineeringGraphService(store, "wb-1", id => id == "wt-1");
        var reader = new HardwareGraphReader(graph);

        var configuration = reader.ReadConfiguration(worktreeRoot, "wt-1");
        var bom = reader.ReadBom(worktreeRoot, "wt-1");
        var network = reader.ReadNetwork(worktreeRoot, "wt-1");

        AssertJsonEqual(expectedConfiguration, configuration);
        AssertJsonEqual(expectedBom, bom);
        AssertJsonEqual(expectedNetwork, network);
        // The values AC-002 names explicitly, so a passing comparison can never be an empty coincidence.
        Assert.Equal(2, configuration.Devices.Count);
        Assert.Equal(2, configuration.Tags.Count);
        Assert.Equal(4, bom.Items.Count);
        Assert.Equal(2, network.Nodes.Count);
        Assert.Equal("192.168.1.11", Assert.Single(network.Nodes, node => node.Address == "192.168.1.11").Address);

        // The ingest is done: the AML is replaced by garbage of exactly the same size and timestamp, so
        // the digest still agrees and the read must not notice. A read that parsed the file would fail.
        fixture.CorruptAmlKeepingItsStamp();

        AssertJsonEqual(expectedConfiguration, reader.ReadConfiguration(worktreeRoot, "wt-1"));
        AssertJsonEqual(expectedBom, reader.ReadBom(worktreeRoot, "wt-1"));
        AssertJsonEqual(expectedNetwork, reader.ReadNetwork(worktreeRoot, "wt-1"));
        Assert.Equal(HardwareBoundaryReason.None, reader.EnsureCurrent(worktreeRoot, "wt-1"));
    }

    [Fact]
    public void ServesTheMissingStateWhenThereIsNoHardwareExport()
    {
        var fixture = new HardwareFixture(_root);
        var worktreeRoot = fixture.WorktreeRoot;
        var expected = HardwareConfigurationReader.Read(worktreeRoot);

        using var store = new EngineeringGraphStore(_root);
        var graph = new EngineeringGraphService(store, "wb-1", id => id == "wt-1");
        var reader = new HardwareGraphReader(graph);

        var configuration = reader.ReadConfiguration(worktreeRoot, "wt-1");

        AssertJsonEqual(expected, configuration);
        Assert.Equal("missing", configuration.State);
        Assert.NotNull(configuration.Message);
        Assert.Equal(HardwareBoundaryReason.None, reader.EnsureCurrent(worktreeRoot, "wt-1"));
        // The worktree node is the hardware projection's own node, and it exists even for a worktree
        // whose export is missing: the "missing" answer is a fact too.
        Assert.NotNull(graph.GetEntity(GraphEntityKind.Worktree, "wt-1"));
    }

    [Fact]
    public void ReprojectsBeforeServingWhenTheExportAppearsOrAWritePointFlaggedIt()
    {
        var fixture = new HardwareFixture(_root);
        var worktreeRoot = fixture.WorktreeRoot;
        using var store = new EngineeringGraphStore(_root);
        var graph = new EngineeringGraphService(store, "wb-1", id => id == "wt-1");
        var reader = new HardwareGraphReader(graph);

        Assert.Equal("missing", reader.ReadConfiguration(worktreeRoot, "wt-1").State);

        // An out-of-app change: the export appears without any write point running.
        fixture.WriteExport(Aml);

        Assert.Equal(HardwareBoundaryReason.Digest, reader.EnsureCurrent(worktreeRoot, "wt-1"));
        var configuration = reader.ReadConfiguration(worktreeRoot, "wt-1");
        AssertJsonEqual(HardwareConfigurationReader.Read(worktreeRoot), configuration);
        Assert.Equal("available", configuration.State);

        // A write point changed the subtree and said so: the flag alone drives the re-projection.
        fixture.WriteExport(Aml.Replace("COF-BASE-A_4", "COF-BASE-C_3"));
        graph.InvalidateWorktreeProjections("wt-1");
        Assert.True(Property(graph, "wt-1", HardwarePropertyNames.Invalidated).Flag);

        Assert.Equal(HardwareBoundaryReason.Invalidated, reader.EnsureCurrent(worktreeRoot, "wt-1"));
        Assert.False(Property(graph, "wt-1", HardwarePropertyNames.Invalidated).Flag);
        Assert.Equal("COF-BASE-C_3", Assert.Single(reader.ReadBom(worktreeRoot, "wt-1").Items, item => item.Name == "COF-BASE-C_3").Name);
    }

    [Fact]
    public void WorktreeDeletionTakesTheHardwareFactsWithIt()
    {
        var fixture = new HardwareFixture(_root);
        using var store = new EngineeringGraphStore(_root);
        var registered = new HashSet<string>(StringComparer.Ordinal) { "wt-1" };
        var graph = new EngineeringGraphService(store, "wb-1", registered.Contains);
        var reader = new HardwareGraphReader(graph);
        reader.ReadConfiguration(fixture.WorktreeRoot, "wt-1");
        Assert.NotNull(graph.GetEntity(GraphEntityKind.Worktree, "wt-1"));

        // The catalog no longer registers the worktree.
        registered.Remove("wt-1");
        var cleanup = graph.RemoveUnregisteredWorktreeFacts();

        Assert.Equal(new[] { "wt-1" }, cleanup.RemovedWorktrees);
        Assert.Null(graph.GetEntity(GraphEntityKind.Worktree, "wt-1"));
        Assert.Empty(graph.GetProperties(GraphEntityKind.Worktree, "wt-1"));
    }

    private static GraphProperty Property(EngineeringGraphService graph, string worktreeId, string name) =>
        graph.GetProperties(GraphEntityKind.Worktree, worktreeId)
            .Single(property => property.Name == name);

    private static void AssertJsonEqual<T>(T expected, T actual) =>
        Assert.True(
            JsonNode.DeepEquals(
                JsonSerializer.SerializeToNode(expected, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                JsonSerializer.SerializeToNode(actual, new JsonSerializerOptions(JsonSerializerDefaults.Web))),
            $"expected {JsonSerializer.Serialize(expected)}, got {JsonSerializer.Serialize(actual)}");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>One worktree root with a hardware subtree, and the graph's view of it.</summary>
    private sealed class HardwareFixture
    {
        public HardwareFixture(string root)
        {
            WorktreeRoot = Path.Combine(root, "worktrees", "master");
            HardwareRoot = Path.Combine(WorktreeRoot, "hardware");
            Directory.CreateDirectory(HardwareRoot);
        }

        public string WorktreeRoot { get; }
        public string HardwareRoot { get; }

        public void WriteExport(string aml)
        {
            Directory.CreateDirectory(HardwareRoot);
            File.WriteAllText(
                Path.Combine(HardwareRoot, "manifest.json"),
                JsonSerializer.Serialize(new { projectAmlFile = "project.aml", exportedAt = "2026-08-04T00:00:00Z" }));
            File.WriteAllText(Path.Combine(HardwareRoot, "project.aml"), aml);
        }

        /// <summary>Replaces the AML with content that could never parse, keeping the file's size and
        /// last-write time so the projection's digest still agrees: a read that parsed the file would
        /// fail, while a read that serves the graph's facts cannot notice.</summary>
        public void CorruptAmlKeepingItsStamp()
        {
            var path = Path.Combine(HardwareRoot, "project.aml");
            var stamp = File.GetLastWriteTimeUtc(path);
            var length = new FileInfo(path).Length;
            File.WriteAllText(path, new string('x', (int)length));
            File.SetLastWriteTimeUtc(path, stamp);
        }
    }

    /// <summary>A small project AML with two typed devices, an addressed node on a subnet and two
    /// external-interface tags.</summary>
    private const string Aml = """
        <CAEXFile xmlns="http://www.automationml.org/StandardVersion2.1">
          <InstanceHierarchy Name="Project hierarchy">
            <InternalElement Name="Project" ID="project-1">
              <InternalElement Name="PN/IE_1" ID="subnet-1">
                <Attribute Name="Type"><Value>Ethernet</Value></Attribute>
                <ExternalInterface ID="ei-subnet-1" Name="LogicalEndPoint_Subnet" RefBaseClassPath="CommunicationInterfaceClassLib/LogicalEndPoint" />
                <SupportedRoleClass RefRoleClassPath="AutomationProjectConfigurationRoleClassLib/Subnet" />
              </InternalElement>
              <InternalElement Name="COF-BASE-A_4" ID="device-1">
                <Attribute Name="TypeIdentifier"><Value>System:Device.ET200SP</Value></Attribute>
                <InternalElement Name="Châssis_0" ID="rack-1">
                  <Attribute Name="TypeName"><Value>Rack</Value></Attribute>
                  <Attribute Name="TypeIdentifier"><Value>System:Rack.ET200SP</Value></Attribute>
                  <InternalElement Name="COF-LID-A" ID="head-1">
                    <Attribute Name="TypeName"><Value>IM 155-6 PN ST</Value></Attribute>
                    <Attribute Name="TypeIdentifier"><Value>OrderNumber:6ES7 155-6AU01-0BN0</Value></Attribute>
                    <Attribute Name="FirmwareVersion"><Value>V4.2</Value></Attribute>
                    <InternalElement Name="Interface PROFINET" ID="iface-1">
                      <Attribute Name="Label"><Value>X1</Value></Attribute>
                      <InternalElement Name="IE1" ID="node-1">
                        <Attribute Name="SubnetMask"><Value>255.255.255.0</Value></Attribute>
                        <Attribute Name="ProfinetDeviceName"><Value>cof-lid-a</Value></Attribute>
                        <Attribute Name="NetworkAddress"><Value>192.168.1.11</Value></Attribute>
                        <ExternalInterface ID="ei-node-1" Name="LogicalEndPoint_Node" RefBaseClassPath="CommunicationInterfaceClassLib/LogicalEndPoint" />
                        <SupportedRoleClass RefRoleClassPath="AutomationProjectConfigurationRoleClassLib/Node" />
                      </InternalElement>
                    </InternalElement>
                  </InternalElement>
                </InternalElement>
              </InternalElement>
              <InternalElement Name="COF-BASE-B_2" ID="device-2">
                <Attribute Name="TypeIdentifier"><Value>System:Device.ET200SP</Value></Attribute>
                <InternalElement Name="Interface PROFINET" ID="iface-2">
                  <InternalElement Name="IE1" ID="node-2">
                    <Attribute Name="NetworkAddress"><Value>10.0.0.5</Value></Attribute>
                    <ExternalInterface ID="ei-node-2" Name="LogicalEndPoint_Node" RefBaseClassPath="CommunicationInterfaceClassLib/LogicalEndPoint" />
                  </InternalElement>
                </InternalElement>
              </InternalElement>
              <ExternalInterface ID="tag-1" Name="MotorRun" RefBaseClassPath="AutomationProjectConfigurationRoleClassLib/Tag">
                <Attribute Name="DataType"><Value>Bool</Value></Attribute>
                <Attribute Name="IoType"><Value>Input</Value></Attribute>
                <Attribute Name="LogicalAddress"><Value>%I0.0</Value></Attribute>
              </ExternalInterface>
              <ExternalInterface ID="tag-2" Name="MotorStop" RefBaseClassPath="AutomationProjectConfigurationRoleClassLib/Tag">
                <Attribute Name="DataType"><Value>Bool</Value></Attribute>
                <Attribute Name="IoType"><Value>Output</Value></Attribute>
                <Attribute Name="LogicalAddress"><Value>%Q0.0</Value></Attribute>
              </ExternalInterface>
              <InternalLink Name="Link To Subnet_1" RefPartnerSideA="node-1:LogicalEndPoint_Node" RefPartnerSideB="subnet-1:LogicalEndPoint_Subnet" />
            </InternalElement>
          </InstanceHierarchy>
        </CAEXFile>
        """;
}
