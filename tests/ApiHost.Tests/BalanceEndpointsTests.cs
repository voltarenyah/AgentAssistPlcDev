using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

public sealed class BalanceEndpointsTests
{
    [Fact]
    public async Task BalanceReturnsSafeDtoAndUsesRuntimeApiKey()
    {
        var handler = new BalanceHandler(HttpStatusCode.OK, """
            {
              "is_available": true,
              "balance_infos": [
                {
                  "currency": "CNY",
                  "total_balance": "10.00",
                  "granted_balance": "2.00",
                  "topped_up_balance": "8.00"
                }
              ]
            }
            """);
        await using var factory = CreateFactory(
            handler,
            new CompatibilityRuntimeState { ApiKey = "runtime-secret" },
            new Dictionary<string, string?> { ["DeepSeek:BaseUrl"] = "https://balance.test/v1/" });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/config/balance");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal("https://balance.test/v1/user/balance", handler.RequestUri);
        Assert.Equal("Bearer runtime-secret", handler.Authorization);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("runtime-secret", body);
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.True(root.GetProperty("isAvailable").GetBoolean());
        var balance = Assert.Single(root.GetProperty("balances").EnumerateArray());
        Assert.Equal("CNY", balance.GetProperty("currency").GetString());
        Assert.Equal("10.00", balance.GetProperty("totalBalance").GetString());
        Assert.Equal("2.00", balance.GetProperty("grantedBalance").GetString());
        Assert.Equal("8.00", balance.GetProperty("toppedUpBalance").GetString());
        Assert.True(DateTimeOffset.TryParse(root.GetProperty("fetchedAt").GetString(), out _));
    }

    [Fact]
    public async Task BalanceFallsBackToConfiguredApiKey()
    {
        var handler = new BalanceHandler(HttpStatusCode.OK, "{\"is_available\":false,\"balance_infos\":[]}");
        await using var factory = CreateFactory(
            handler,
            new CompatibilityRuntimeState(),
            new Dictionary<string, string?> { ["DeepSeek:ApiKey"] = "configured-secret" });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/config/balance");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Bearer configured-secret", handler.Authorization);
    }

    [Fact]
    public async Task BalanceFallsBackToLegacyConfiguredApiKey()
    {
        var handler = new BalanceHandler(HttpStatusCode.OK, "{\"is_available\":false,\"balance_infos\":[]}");
        await using var factory = CreateFactory(
            handler,
            new CompatibilityRuntimeState(),
            new Dictionary<string, string?> { ["deepSeekApiKey"] = "legacy-secret" });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/config/balance");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Bearer legacy-secret", handler.Authorization);
    }

    [Fact]
    public async Task BalanceWithoutApiKeyReturnsProblemDetailsWithoutCallingUpstream()
    {
        var handler = new BalanceHandler(HttpStatusCode.OK, "{}");
        await using var factory = CreateFactory(
            handler,
            new CompatibilityRuntimeState { ApiKey = "" },
            new Dictionary<string, string?>
            {
                ["DeepSeek:ApiKey"] = "",
                ["deepSeekApiKey"] = "",
            });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/config/balance");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("DEEPSEEK_API_KEY_REQUIRED", body);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task BalancePreservesUpstreamStatusAndSanitizesError()
    {
        const string apiKey = "upstream-secret";
        var handler = new BalanceHandler(
            HttpStatusCode.TooManyRequests,
            "{\"error\":{\"message\":\"quota rejected for " + apiKey + "\"}}");
        await using var factory = CreateFactory(
            handler,
            new CompatibilityRuntimeState { ApiKey = apiKey },
            new Dictionary<string, string?>());
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/config/balance");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("quota rejected", body);
        Assert.DoesNotContain(apiKey, body);
    }

    private static WebApplicationFactory<Program> CreateFactory(
        BalanceHandler handler,
        CompatibilityRuntimeState state,
        IReadOnlyDictionary<string, string?> settings)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            foreach (var setting in settings)
                builder.UseSetting(setting.Key, setting.Value);

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<CompatibilityRuntimeState>();
                services.AddSingleton(state);
                services.RemoveAll<HttpClient>();
                services.AddSingleton(new HttpClient(handler));
            });
        });
    }

    private sealed class BalanceHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? RequestUri { get; private set; }
        public string? Authorization { get; private set; }
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri?.ToString();
            Authorization = request.Headers.Authorization?.ToString();
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
