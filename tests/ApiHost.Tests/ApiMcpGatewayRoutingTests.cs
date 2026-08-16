using Agent.Mcp;
using Xunit;

public sealed class ApiMcpGatewayRoutingTests
{
    [Fact]
    public void RoutesSvnToolsToTheVersionControlServer()
    {
        var engineering = new RecordingCaller();
        var knowledge = new RecordingCaller();
        var versionControl = new RecordingCaller();
        var sourceEditor = new RecordingCaller();
        var gateway = new ApiMcpGateway(engineering, knowledge, versionControl, sourceEditor);

        Assert.Same(versionControl, gateway.For("svn_log"));
    }

    [Fact]
    public void RoutesNetworkLogicChunksToTheKnowledgeServer()
    {
        var knowledge = new RecordingCaller();
        var gateway = new ApiMcpGateway(
            new RecordingCaller(),
            knowledge,
            new RecordingCaller(),
            new RecordingCaller());

        Assert.Same(knowledge, gateway.For("get_network_logic"));
    }

    private sealed class RecordingCaller : IMcpToolCaller
    {
        public Task<T> CallAsync<T>(string tool, object args, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
