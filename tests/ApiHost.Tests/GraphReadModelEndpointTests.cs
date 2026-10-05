using Agent.Workbench;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

/// <summary>
/// ADR-0011 Phase 5 at the HTTP boundary: the source-inspect route and the three hardware routes serve
/// the engineering graph. The inspect payload is unchanged after the exported XML is corrupted, and the
/// hardware values equal what the readers derive for the same fixture.
/// </summary>
public sealed class GraphReadModelEndpointTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"api-graph-read-{Guid.NewGuid():N}");

    [Fact]
    public async Task SourceInspectServesTheSamePayloadAfterTheXmlIsCorrupted()
    {
        await using var fixture = await GraphReadApiFixture.CreateAsync(_root);
        fixture.WriteSourceManifest();
        fixture.WriteSource("Blocks/Area/Main [OB1].xml", BlockXml);

        var first = await fixture.Client.GetAsync(fixture.InspectRoute("Blocks/Area/Main [OB1].xml"));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var body = await first.Content.ReadAsStringAsync();
        var inspection = JsonNode.Parse(body)!.AsObject();
        Assert.Equal("Blocks", inspection["category"]!.GetValue<string>());
        Assert.Equal("Main", inspection["name"]!.GetValue<string>());
        Assert.Single(inspection["interfaces"]!.AsArray());
        Assert.Single(inspection["networks"]!.AsArray());

        // The payload is what the inspector itself produced for the same file.
        var expected = new SourceObjectInspectorReader().Read(
            fixture.Context, "Blocks/Area/Main [OB1].xml", new DeviceSourceResolver(_ => { }));
        Assert.True(
            JsonNode.DeepEquals(
                JsonNode.Parse(JsonSerializer.Serialize(expected, new JsonSerializerOptions(JsonSerializerDefaults.Web))),
                inspection),
            "The route's payload is not the inspector's payload.");

        // The ingest is done: the XML is replaced by content that could not parse into those values.
        fixture.CorruptSourceXml();

        var second = await fixture.Client.GetAsync(fixture.InspectRoute("Blocks/Area/Main [OB1].xml"));
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(body, await second.Content.ReadAsStringAsync());

        // A path the projection does not list is a named 404, and an escaping path is still a 400.
        var missing = await fixture.Client.GetAsync(fixture.InspectRoute("Blocks/Area/Unknown.xml"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(
            "SOURCE_FILE_NOT_FOUND",
            (await missing.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
        var rejected = await fixture.Client.GetAsync(fixture.InspectRoute("../outside.xml"));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Equal(
            "SOURCE_PATH_INVALID",
            (await rejected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task HardwareRoutesServeTheReadersValuesFromTheGraph()
    {
        await using var fixture = await GraphReadApiFixture.CreateAsync(_root);
        fixture.WriteHardwareExport();

        var configuration = await fixture.Client.GetFromJsonAsync<JsonElement>(fixture.HardwareRoute);
        var bom = await fixture.Client.GetFromJsonAsync<JsonElement>(fixture.BomRoute);
        var network = await fixture.Client.GetFromJsonAsync<JsonElement>(fixture.NetworkRoute);

        AssertJsonEqual(HardwareConfigurationReader.Read(fixture.WorktreeRoot), configuration);
        AssertJsonEqual(HardwareListReader.ReadBom(fixture.WorktreeRoot), bom);
        AssertJsonEqual(HardwareListReader.ReadNetwork(fixture.WorktreeRoot), network);
        Assert.Equal("available", configuration.GetProperty("state").GetString());
        Assert.Equal(2, configuration.GetProperty("devices").GetArrayLength());
        Assert.Equal(4, bom.GetProperty("items").GetArrayLength());
        Assert.Equal(2, network.GetProperty("nodes").GetArrayLength());

        // The AML is replaced by garbage of the same size and timestamp: the digest still agrees, so no
        // re-projection runs and a route that parsed the file would fail instead of answering.
        fixture.CorruptAmlKeepingItsStamp();

        var afterCorruption = await fixture.Client.GetFromJsonAsync<JsonElement>(fixture.HardwareRoute);
        Assert.Equal(
            JsonSerializer.Serialize(configuration),
            JsonSerializer.Serialize(afterCorruption));
        Assert.Equal(2, afterCorruption.GetProperty("devices").GetArrayLength());

        // An out-of-app change is detected at the boundary and re-projected before it is served.
        fixture.WriteHardwareExport(deviceName: "COF-BASE-C_3");

        var reprojected = await fixture.Client.GetFromJsonAsync<JsonElement>(fixture.BomRoute);
        Assert.Contains(
            reprojected.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("name").GetString() == "COF-BASE-C_3");
        AssertJsonEqual(HardwareListReader.ReadBom(fixture.WorktreeRoot), reprojected);
    }

    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    private static void AssertJsonEqual<T>(T expected, JsonElement actual)
    {
        var expectedNode = JsonSerializer.SerializeToNode(expected, WebOptions);
        var actualNode = JsonNode.Parse(actual.GetRawText());
        Assert.True(JsonNode.DeepEquals(expectedNode, actualNode),
            $"expected {expectedNode?.ToJsonString()}, got {actualNode?.ToJsonString()}");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class GraphReadApiFixture : IAsyncDisposable
    {
        private readonly WebApplicationFactory<Program> _factory;

        private GraphReadApiFixture(WebApplicationFactory<Program> factory, HttpClient client, WorkbenchMetadata workbench)
        {
            _factory = factory;
            Client = client;
            WorkbenchRoot = workbench.RootPath;
            WorkbenchId = workbench.WorkbenchId;
        }

        public HttpClient Client { get; }
        public string WorkbenchRoot { get; }
        public string WorkbenchId { get; }
        public string WorktreeRoot => Path.Combine(WorkbenchRoot, "worktrees", "master");
        public string SourceRoot => Path.Combine(WorktreeRoot, "devices", "PLC_1", "source");
        public string HardwareRoot => Path.Combine(WorktreeRoot, "hardware");

        public DeviceContext Context => new(
            WorkbenchId, "wt-1", "dev-1", WorkbenchRoot, WorktreeRoot,
            Path.Combine(WorktreeRoot, "devices", "PLC_1"), SourceRoot,
            Path.Combine(WorktreeRoot, "devices", "PLC_1", "staging"),
            Path.Combine(WorktreeRoot, "devices", "PLC_1", "plc-knowledge.db"));

        public string DeviceRoute => $"/api/workbenches/{WorkbenchId}/worktrees/wt-1/devices/dev-1";
        public string HardwareRoute => $"/api/workbenches/{WorkbenchId}/worktrees/wt-1/hardware";
        public string BomRoute => $"{HardwareRoute}/bom";
        public string NetworkRoute => $"{HardwareRoute}/network";
        public string InspectRoute(string relativePath) =>
            $"{DeviceRoute}/source/inspect?relativePath={Uri.EscapeDataString(relativePath)}";

        public static Task<GraphReadApiFixture> CreateAsync(string fixtureRoot)
        {
            var store = new AtomicJsonStore();
            var catalog = new WorkbenchCatalog(store, fixtureRoot);
            var workbench = catalog.Create("Line", null);
            workbench = catalog.RegisterWorktree(
                workbench,
                new WorkbenchWorktreeRegistration("wt-1", "master", "master", "master"));
            var worktreeRoot = Path.Combine(workbench.RootPath, "worktrees", "master");
            var deviceRoot = Path.Combine(worktreeRoot, "devices", "PLC_1");
            Directory.CreateDirectory(Path.Combine(deviceRoot, "source"));
            store.Write(
                Path.Combine(worktreeRoot, "worktree.json"),
                new WorktreeMetadata(
                    WorkbenchSchema.CurrentVersion, "wt-1", workbench.WorkbenchId, "master", "master",
                    DateTimeOffset.UtcNow.ToString("O"), null, null, null, ["dev-1"], null));
            store.Write(
                Path.Combine(deviceRoot, "device.json"),
                new DeviceMetadata(
                    WorkbenchSchema.CurrentVersion, "dev-1", "wt-1", "PLC_1", "engineering-dev-1", null, null, null,
                    new KnowledgeState(false, new Dictionary<string, string>(), "2026-07-29T08:00:00Z", false),
                    []));

            var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
            {
                host.UseEnvironment("Testing");
                host.ConfigureServices(services =>
                {
                    services.RemoveAll<WorkbenchCatalog>();
                    services.RemoveAll<AtomicJsonStore>();
                    services.RemoveAll<WorkbenchApiState>();
                    services.AddSingleton(store);
                    services.AddSingleton(catalog);
                    services.AddSingleton<WorkbenchApiState>();
                });
            });
            var client = factory.CreateClient();
            return Task.FromResult(new GraphReadApiFixture(factory, client, workbench));
        }

        /// <summary>The manifest the device routes ingest: one block with the exported content hash the
        /// projection's reuse rule keys off.</summary>
        public void WriteSourceManifest() =>
            File.WriteAllText(
                Path.Combine(SourceRoot, "metadata.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = "1.0",
                    device = new { plcName = "PLC_1" },
                    components = new object[]
                    {
                        new
                        {
                            id = "block-1",
                            name = "Main",
                            sourcePath = "Area/Main",
                            category = "OB",
                            status = "Exported",
                            exportedFile = "Blocks/Area/Main [OB1].xml",
                            number = 1,
                            programmingLanguage = "LAD",
                            contentHash = "HASH-A",
                            siemensTypeName = "OB",
                            isKnowHowProtected = false,
                            modifiedDate = "2026-07-20T10:00:00.0000000+00:00",
                            fingerprints = new Dictionary<string, string> { ["Code"] = "AAAA1111" },
                        },
                    },
                }));

        public void WriteSource(string relativePath, string contents)
        {
            var path = Path.Combine(SourceRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
        }

        public void CorruptSourceXml()
        {
            foreach (var path in Directory.EnumerateFiles(SourceRoot, "*.xml", SearchOption.AllDirectories))
                File.WriteAllText(path, "<not-a-siemens-document/>");
        }

        public void WriteHardwareExport(string deviceName = "COF-BASE-A_4")
        {
            Directory.CreateDirectory(HardwareRoot);
            File.WriteAllText(
                Path.Combine(HardwareRoot, "manifest.json"),
                JsonSerializer.Serialize(new { projectAmlFile = "project.aml", exportedAt = "2026-08-04T00:00:00Z" }));
            File.WriteAllText(
                Path.Combine(HardwareRoot, "project.aml"),
                Aml.Replace("COF-BASE-A_4", deviceName));
        }

        public void CorruptAmlKeepingItsStamp()
        {
            var path = Path.Combine(HardwareRoot, "project.aml");
            var stamp = File.GetLastWriteTimeUtc(path);
            var length = new FileInfo(path).Length;
            File.WriteAllText(path, new string('x', (int)length));
            File.SetLastWriteTimeUtc(path, stamp);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _factory.DisposeAsync();
        }
    }

    private const string BlockXml = """
        <Document><SW.Blocks.FC ID="block"><AttributeList><Name>Main</Name></AttributeList>
          <Interface><Sections><Section Name="Input"><Member Name="Enable" Datatype="Bool" DefaultValue="false" /></Section></Sections></Interface>
          <SW.Blocks.CompileUnit ID="network-1"><AttributeList><ProgrammingLanguage>LAD</ProgrammingLanguage></AttributeList>
            <FlgNet><Parts><Access UId="1"><Symbol><Component Name="Enable" /></Symbol></Access><Part UId="3" Name="Contact" /></Parts>
            <Wires><Wire><Powerrail /><NameCon UId="3" Name="in" /></Wire><Wire><IdentCon UId="1" /><NameCon UId="3" Name="operand" /></Wire></Wires></FlgNet>
          </SW.Blocks.CompileUnit>
        </SW.Blocks.FC></Document>
        """;

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
              <InternalLink Name="Link To Subnet_1" RefPartnerSideA="node-1:LogicalEndPoint_Node" RefPartnerSideB="subnet-1:LogicalEndPoint_Subnet" />
            </InternalElement>
          </InstanceHierarchy>
        </CAEXFile>
        """;
}
