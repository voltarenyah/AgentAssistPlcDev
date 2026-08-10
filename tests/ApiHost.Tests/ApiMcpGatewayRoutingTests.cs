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

    private sealed class RecordingCaller : IMcpToolCaller
    {
        public Task<T> CallAsync<T>(string tool, object args, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
