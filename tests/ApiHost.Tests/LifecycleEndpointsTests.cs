using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

public sealed class LifecycleEndpointsTests
{
    [Fact]
    public async Task ShutdownWithoutTokenIsUnauthorized()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/lifecycle/shutdown", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ShutdownWithWrongTokenIsUnauthorized()
    {
        await using var factory = CreateFactory();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/lifecycle/shutdown");
        request.Headers.Add("X-AutomationWorkbench-Shutdown-Token", "wrong-token");

        var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ShutdownWithCorrectTokenIsAccepted()
    {
        await using var factory = CreateFactory();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/lifecycle/shutdown");
        request.Headers.Add("X-AutomationWorkbench-Shutdown-Token", "test-shutdown-token");

        var response = await factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Fact]
    public async Task ShutdownIsNotAvailableAsGet()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/lifecycle/shutdown");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    private static WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Application:ShutdownToken"] = "test-shutdown-token",
                }));
        });
}
