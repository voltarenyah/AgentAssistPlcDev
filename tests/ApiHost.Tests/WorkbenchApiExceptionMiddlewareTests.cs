using System.Text;
using Agent.Mcp;
using Microsoft.AspNetCore.Http;
using Xunit;

public sealed class WorkbenchApiExceptionMiddlewareTests
{
    [Fact]
    public async Task MapsToolCallFailureToAnActionableBadRequest()
    {
        var context = new DefaultHttpContext();
        await using var body = new MemoryStream();
        context.Response.Body = body;
        var middleware = new WorkbenchApiExceptionMiddleware(_ => throw new ToolCallException(
            "TIA_NOT_CONNECTED",
            "No project connected. Call connect first.",
            "Attach the running TIA project and retry."));

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        var response = Encoding.UTF8.GetString(body.ToArray());
        Assert.Contains("TIA_NOT_CONNECTED", response);
        Assert.Contains("No project connected", response);
        Assert.Contains("Attach the running TIA project", response);
    }
}
