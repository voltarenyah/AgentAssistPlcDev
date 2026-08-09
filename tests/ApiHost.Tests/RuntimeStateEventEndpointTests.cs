using System.Net;
using Agent.Workbench;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public sealed class RuntimeStateEventEndpointTests
{
    [Fact]
    public async Task RuntimeEventStreamSendsInitialSnapshotImmediately()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();
        var catalog = factory.Services.GetRequiredService<WorkbenchCatalog>();
        var state = factory.Services.GetRequiredService<WorkbenchApiState>();
        var root = Path.Combine(Path.GetTempPath(), "runtime-events-" + Guid.NewGuid().ToString("N"));
        var workbench = catalog.Create("Runtime Events", root);
        state.Open(root);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        using var response = await client.GetAsync(
            $"/api/workbenches/{workbench.WorkbenchId}/runtime-events",
            HttpCompletionOption.ResponseHeadersRead,
            cancellation.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellation.Token));
        Assert.Equal("event: runtime-state", await reader.ReadLineAsync(cancellation.Token));
        var data = await reader.ReadLineAsync(cancellation.Token);
        Assert.NotNull(data);
        Assert.Contains("\"revision\":0", data);
    }
}
