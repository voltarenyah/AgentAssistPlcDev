using Agent.Workbench;
using Agent.Mcp;
using Agent.Chat;
using Contracts.Sandbox;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.Sqlite;
using System.Net.Http.Json;
using System.Text.Json;
using System.Net;
using Contracts.Engineering;
using Xunit;

public sealed class WorkbenchEndpointsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "api-workbench-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task TestingHostStartsWithoutExternalProcessesAndMapsUnknownIdToNotFound()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();

        var status = await client.GetAsync("/api/status");
        var missing = await client.GetAsync("/api/workbenches/missing");

        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task TestingHostServesSpaStaticAssetsAndKeepsApiRoutesOutOfFallback()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();

        var home = await client.GetAsync("/");
        var asset = await client.GetAsync("/assets/test.css");
        var spaRoute = await client.GetAsync("/workbenches/example");
        var apiRoute = await client.GetAsync("/api/does-not-exist");
        var status = await client.GetAsync("/api/status");

        var index = await home.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        Assert.Equal("text/html", home.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Automation Workbench Test Studio", index);
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
        Assert.Equal("text/css", asset.Content.Headers.ContentType?.MediaType);
        Assert.Contains("#123456", await asset.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, spaRoute.StatusCode);
        Assert.Equal(index, await spaRoute.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, apiRoute.StatusCode);
        Assert.NotEqual("text/html", apiRoute.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, status.StatusCode);
        Assert.Equal("application/json", status.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task TestingCorsAllowsOnlyTheDeterministicTestingOrigin()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();

        using var allowedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        allowedRequest.Headers.TryAddWithoutValidation("Origin", "http://testing.local");
        using var deniedRequest = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        deniedRequest.Headers.TryAddWithoutValidation("Origin", "http://unexpected.local");

        var allowed = await client.SendAsync(allowedRequest);
        var denied = await client.SendAsync(deniedRequest);

        Assert.True(allowed.Headers.TryGetValues("Access-Control-Allow-Origin", out var origins));
        Assert.Equal("http://testing.local", Assert.Single(origins));
        Assert.False(denied.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task DevelopmentCorsAllowsConfiguredViteOrigin()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.UseSetting("Mcp:StartExternal", "false");
                builder.UseSetting("Cors:ViteOrigin", "http://localhost:5173");
            });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        request.Headers.TryAddWithoutValidation("Origin", "http://localhost:5173");

        var response = await client.SendAsync(request);

        Assert.True(response.Headers.TryGetValues("Access-Control-Allow-Origin", out var origins));
        Assert.Equal("http://localhost:5173", Assert.Single(origins));
    }

    [Fact]
    public async Task ProductionDoesNotEmitCrossOriginAccessHeaders()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.UseSetting("Mcp:StartExternal", "false");
            });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        request.Headers.TryAddWithoutValidation("Origin", "http://localhost:5173");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public void ProductionStartupDefaultsToLoopbackPort5239AndBrowserLaunch()
    {
        var configuration = new ConfigurationBuilder().Build();

        var options = ApplicationStartupOptions.From(configuration, "Production");

        Assert.Equal("127.0.0.1", options.Host);
        Assert.Equal(5239, options.Port);
        Assert.Equal("http://127.0.0.1:5239", options.Url);
        Assert.True(options.OpenBrowserOnStart);
    }

    [Fact]
    public void StartupHonorsCustomPortButTestingNeverLaunchesBrowser()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Application:Host"] = "127.0.0.1",
                ["Application:Port"] = "5299",
                ["Application:OpenBrowserOnStart"] = "true",
            })
            .Build();

        var options = ApplicationStartupOptions.From(configuration, "Testing");

        Assert.Equal(5299, options.Port);
        Assert.Equal("http://127.0.0.1:5299", options.Url);
        Assert.False(options.OpenBrowserOnStart);
    }

    [Fact]
    public void StartupReportsActionablePortCollisionMessage()
    {
        var options = new ApplicationStartupOptions("127.0.0.1", 5239, true);

        var message = ApplicationStartupOptions.PortInUseMessage(options);

        Assert.Equal(
            string.Join(
                Environment.NewLine,
                "Automation Workbench could not start because port 5239 on 127.0.0.1 is already in use.",
                string.Empty,
                "Close the other application or configure Application:Port to another loopback port."),
            message);
    }

    [Fact]
    public async Task SessionsListsTiaProcessesBeforeAWorkbenchIsSelected()
    {
        var engineering = new RecordingToolCaller("[{\"sessionId\":17,\"projectName\":\"Demo\"}]");
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ApiMcpGateway>();
                services.AddSingleton(new ApiMcpGateway(
                    engineering,
                    new RecordingToolCaller(),
                    new RecordingToolCaller(),
                    new RecordingToolCaller()));
            });
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/sessions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"sessionId\":17", await response.Content.ReadAsStringAsync());
        Assert.Equal(["list_sessions"], engineering.Calls);
    }

    [Fact]
    public async Task CurrentSessionReportsAttachedPortal()
    {
        var engineering = new RecordingToolCaller("{\"attached\":true,\"sessionId\":17,\"projectName\":\"Demo\",\"projectPath\":\"C:\\\\Demo\\\\demo.ap17\"}");
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ApiMcpGateway>();
                services.AddSingleton(new ApiMcpGateway(
                    engineering,
                    new RecordingToolCaller(),
                    new RecordingToolCaller(),
                    new RecordingToolCaller()));
            });
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/sessions/current");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"sessionId\":17", await response.Content.ReadAsStringAsync());
        Assert.Equal(["get_current_session"], engineering.Calls);
    }

    [Fact]
    public async Task CloseSessionForwardsProcessId()
    {
        var engineering = new RecordingToolCaller("true");
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ApiMcpGateway>();
                services.AddSingleton(new ApiMcpGateway(
                    engineering,
                    new RecordingToolCaller(),
                    new RecordingToolCaller(),
                    new RecordingToolCaller()));
            });
        });
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/sessions/close", new { sessionId = 17 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["close_session"], engineering.Calls);
        Assert.Equal(17, engineering.Arguments.Single().GetProperty("sessionId").GetInt32());
    }

    [Fact]
    public async Task SaveProjectForwardsToTheAttachedEngineeringSession()
    {
        var engineering = new RecordingToolCaller("{}");
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ApiMcpGateway>();
                services.AddSingleton(new ApiMcpGateway(
                    engineering,
                    new RecordingToolCaller(),
                    new RecordingToolCaller(),
                    new RecordingToolCaller()));
            });
        });
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/tia/project/save", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["save_project"], engineering.Calls);
    }

    [Fact]
    public async Task OpenProjectInTiaSwitchesToVisibleProjectPathAndCompletesOperation()
    {
        var engineering = new RecordingToolCaller();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ApiMcpGateway>();
                services.AddSingleton(new ApiMcpGateway(
                    engineering,
                    new RecordingToolCaller(),
                    new RecordingToolCaller(),
                    new RecordingToolCaller()));
            });
        });
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/connections/switch")
        {
            Content = JsonContent.Create(new
            {
                projectPath = @"C:\Projects\Line.ap17",
                withUI = true,
            }),
        };
        request.Headers.Add("X-Operation-Id", "open-tia-1");

        var response = await client.SendAsync(request);
        var operation = await client.GetFromJsonAsync<JsonElement>("/api/operations/open-tia-1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["connect"], engineering.Calls);
        Assert.Equal(@"C:\Projects\Line.ap17", engineering.Arguments.Single().GetProperty("projectPath").GetString());
        Assert.True(engineering.Arguments.Single().GetProperty("withUI").GetBoolean());
        Assert.Equal("succeeded", operation.GetProperty("state").GetString());
    }

    [Fact]
    public void ProductionResolverUsesRepositoryDefaultsWithoutMcpConfiguration()
    {
        var configuration = new ConfigurationBuilder().Build();
        var paths = McpExecutableResolver.Resolve(configuration, AppContext.BaseDirectory);

        Assert.EndsWith(Path.Combine("Mcp.Engineering", "bin", BuildConfiguration, "net48", "Mcp.Engineering.exe"), paths.Engineering);
        Assert.EndsWith(Path.Combine("Mcp.Knowledge", "bin", BuildConfiguration, "net8.0", "Mcp.Knowledge.exe"), paths.Knowledge);
    }

    [Fact]
    public void ResolverUsesExplicitConfigurationOverridesBeforeInstalledDefaults()
    {
        var installedRoot = Path.Combine(root, "installed layout with spaces");
        var configuredEngineering = Path.Combine(root, "configured", "Mcp.Engineering.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(configuredEngineering)!);
        File.WriteAllText(configuredEngineering, string.Empty);
        CreateInstalledMcpExecutables(installedRoot);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Mcp:Engineering"] = configuredEngineering,
            })
            .Build();

        var paths = McpExecutableResolver.Resolve(configuration, installedRoot);

        Assert.Equal(configuredEngineering, paths.Engineering);
        Assert.Equal(
            Path.Combine(installedRoot, "mcp", "knowledge", "Mcp.Knowledge.exe"),
            paths.Knowledge);
    }

    [Fact]
    public void ResolverUsesExecutableRelativeInstalledLayoutWithoutSolutionFile()
    {
        var installedRoot = Path.Combine(root, "installed layout with spaces");
        CreateInstalledMcpExecutables(installedRoot);

        var paths = McpExecutableResolver.Resolve(
            new ConfigurationBuilder().Build(),
            installedRoot);

        Assert.Equal(
            Path.Combine(installedRoot, "mcp", "engineering", "Mcp.Engineering.exe"),
            paths.Engineering);
        Assert.Equal(
            Path.Combine(installedRoot, "mcp", "source-editor", "Mcp.SourceEditor.exe"),
            paths.SourceEditor);
    }

    [Fact]
    public void ResolverUsesDevelopmentRepositoryFallbackWhenInstalledFilesAreAbsent()
    {
        var repositoryRoot = Path.Combine(root, "development repository");
        Directory.CreateDirectory(repositoryRoot);
        File.WriteAllText(Path.Combine(repositoryRoot, "AgentAssistPlcDev.sln"), string.Empty);
        CreateDevelopmentMcpExecutables(repositoryRoot);

        var paths = McpExecutableResolver.Resolve(
            new ConfigurationBuilder().Build(),
            Path.Combine(repositoryRoot, "src", "ApiHost", "bin", BuildConfiguration, "net8.0"));

        Assert.Equal(
            Path.Combine(repositoryRoot, "src", "Mcp.Engineering", "bin", BuildConfiguration, "net48", "Mcp.Engineering.exe"),
            paths.Engineering);
        Assert.Equal(
            Path.Combine(repositoryRoot, "src", "Mcp.VersionControl", "bin", BuildConfiguration, "net8.0", "Mcp.VersionControl.exe"),
            paths.VersionControl);
    }

    [Fact]
    public void ResolverValidationReportsOneMissingExecutable()
    {
        Directory.CreateDirectory(root);
        var paths = new McpExecutablePaths(
            Path.Combine(root, "engineering.exe"),
            Path.Combine(root, "knowledge.exe"),
            Path.Combine(root, "version-control.exe"),
            Path.Combine(root, "source-editor.exe"));
        File.WriteAllText(paths.Engineering, string.Empty);
        File.WriteAllText(paths.Knowledge, string.Empty);
        File.WriteAllText(paths.SourceEditor, string.Empty);

        var exception = Assert.Throws<InvalidOperationException>(
            () => McpExecutableResolver.Validate(paths));

        Assert.Contains($"VersionControl: {paths.VersionControl}", exception.Message);
        Assert.DoesNotContain("Knowledge:", exception.Message);
    }

    [Fact]
    public void ResolverValidationReportsAllMissingExecutablesTogether()
    {
        var paths = new McpExecutablePaths(
            Path.Combine(root, "engineering.exe"),
            Path.Combine(root, "knowledge.exe"),
            Path.Combine(root, "version-control.exe"),
            Path.Combine(root, "source-editor.exe"));

        var exception = Assert.Throws<InvalidOperationException>(
            () => McpExecutableResolver.Validate(paths));

        Assert.Contains($"Engineering: {paths.Engineering}", exception.Message);
        Assert.Contains($"Knowledge: {paths.Knowledge}", exception.Message);
        Assert.Contains($"SourceEditor: {paths.SourceEditor}", exception.Message);
        Assert.Contains($"VersionControl: {paths.VersionControl}", exception.Message);
        Assert.Contains("Repair the installation or configure the corresponding Mcp path.", exception.Message);
    }

    private static void CreateInstalledMcpExecutables(string root)
    {
        foreach (var relativePath in new[]
        {
            Path.Combine("mcp", "engineering", "Mcp.Engineering.exe"),
            Path.Combine("mcp", "knowledge", "Mcp.Knowledge.exe"),
            Path.Combine("mcp", "source-editor", "Mcp.SourceEditor.exe"),
            Path.Combine("mcp", "version-control", "Mcp.VersionControl.exe"),
        })
        {
            var path = Path.Combine(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Empty);
        }
    }

    private static void CreateDevelopmentMcpExecutables(string root)
    {
        foreach (var relativePath in new[]
        {
            Path.Combine("src", "Mcp.Engineering", "bin", BuildConfiguration, "net48", "Mcp.Engineering.exe"),
            Path.Combine("src", "Mcp.Knowledge", "bin", BuildConfiguration, "net8.0", "Mcp.Knowledge.exe"),
            Path.Combine("src", "Mcp.SourceEditor", "bin", BuildConfiguration, "net8.0", "Mcp.SourceEditor.exe"),
            Path.Combine("src", "Mcp.VersionControl", "bin", BuildConfiguration, "net8.0", "Mcp.VersionControl.exe"),
        })
        {
            var path = Path.Combine(root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, string.Empty);
        }
    }

#if DEBUG
    private const string BuildConfiguration = "Debug";
#else
    private const string BuildConfiguration = "Release";
#endif

    [Fact]
    public async Task ProductionHostCanReachListeningPipelineWithExternalStartupDisabled()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("Mcp:StartExternal", "false");
        });
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/status")).StatusCode);
    }

    [Fact]
    public void OperationRegistryKeepsOnlyLatestStatusAndDismissesTerminalSnapshots()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-28T00:00:00Z"));
        var registry = new OperationStatusRegistry(clock);

        registry.Start("op-1", "create-workbench", "Preparing workbench storage...");
        registry.Report("op-1", "Initializing Git repository...");

        Assert.True(registry.TryGet("op-1", out var running));
        Assert.Equal("op-1", running.OperationId);
        Assert.Equal("create-workbench", running.OperationType);
        Assert.Equal("running", running.State);
        Assert.Equal("Initializing Git repository...", running.Message);
        Assert.Null(running.ErrorMessage);

        registry.Succeed("op-1", "Workbench created.");
        registry.Dismiss("op-1");

        Assert.False(registry.TryGet("op-1", out _));
        Assert.False(registry.TryGet("missing", out _));
    }

    [Fact]
    public void OperationRegistryRetainsFailureUntilDismissedOrExpired()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-07-28T00:00:00Z"));
        var registry = new OperationStatusRegistry(clock);

        registry.Start("op-1", "refresh", "Exporting block Main_OB1...");
        registry.Fail("op-1", "Exporting block Main_OB1...", "TIA export failed.");

        Assert.True(registry.TryGet("op-1", out var failed));
        Assert.Equal("failed", failed.State);
        Assert.Equal("Exporting block Main_OB1...", failed.Message);
        Assert.Equal("TIA export failed.", failed.ErrorMessage);

        clock.Advance(TimeSpan.FromMinutes(61));

        Assert.False(registry.TryGet("op-1", out _));
    }

    [Theory]
    [InlineData("/api/project/info")]
    [InlineData("/api/blocks")]
    [InlineData("/api/knowledge/node-kinds")]
    [InlineData("/api/vc/status")]
    [InlineData("/api/chat")]
    public async Task RestoredDeviceScopedEndpointsRejectNoSelection(string endpoint)
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();

        var response = endpoint == "/api/chat"
            ? await client.PostAsJsonAsync(endpoint, new { message = "hello" })
            : await client.GetAsync(endpoint);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ChatEndpointStreamsSseErrorsAndDone()
    {
        await using var fixture = await SelectedApiFixture.CreateAsync(root, databaseExists: true);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = JsonContent.Create(new { message = "what is the function of FB block and interface" }),
        };

        using var response = await fixture.Client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.TryGetValues("X-Accel-Buffering", out var buffering));
        Assert.Contains("no", buffering);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"kind\":\"progress\"", body);
        Assert.Contains("Preparing chat context", body);
        Assert.Contains("\"kind\":\"error\"", body);
        Assert.Contains("\"delta\":", body);
        Assert.Contains("data: [DONE]", body);
    }

    [Fact]
    public async Task BlockInterfaceEndpointReturnsCompactSummaryForSelectedDevice()
    {
        await using var fixture = await SelectedApiFixture.CreateAsync(root, databaseExists: true);
        SeedBlockInterfaceGraph(fixture.Context.KnowledgeDbPath);

        var response = await fixture.Client.GetAsync(
            "/api/knowledge/block-interface?blockName=FB_LAD_SimulateCylinder");

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("FB_LAD_SimulateCylinder", body.GetProperty("name").GetString());
        Assert.Equal("FB_LAD_SimulateCylinder_DB", body.GetProperty("instanceDb").GetString());
        Assert.Equal("Main", body.GetProperty("callSites")[0].GetProperty("callerBlock").GetString());
    }

    [Fact]
    public async Task TiaComparisonStageAndPreviewAreNonDestructiveAndExposeFingerprintEvidence()
    {
        await using var fixture = await SelectedApiFixture.CreateAsync(
            root,
            databaseExists: true,
            stageExport: (outputDir, plcName) =>
            {
                Assert.Equal("PLC_1", plcName);
                Directory.CreateDirectory(Path.Combine(outputDir, "Blocks"));
                File.WriteAllText(Path.Combine(outputDir, "Blocks", "Main.xml"), "<live/>");
                File.WriteAllText(Path.Combine(outputDir, "Blocks", "New.xml"), "<new/>");
                WriteComparisonManifest(outputDir, "live-fingerprint", includeNew: true);
            });
        fixture.WriteComparisonBaseline("stored-fingerprint");
        var before = fixture.PersistentArtifactHashes();

        var stage = await fixture.Client.PostAsync(
            $"/api/devices/{fixture.DeviceId}/refresh/stage",
            null);
        stage.EnsureSuccessStatusCode();
        var preview = await fixture.Client.GetFromJsonAsync<JsonElement>(
            $"/api/devices/{fixture.DeviceId}/refresh/preview");

        Assert.Equal(before, fixture.PersistentArtifactHashes());
        var entries = preview.GetProperty("entries").EnumerateArray().ToArray();
        var entry = Assert.Single(entries, value =>
            value.GetProperty("relativePath").GetString() == "Blocks/Main.xml");
        Assert.Equal("stored-fingerprint", entry.GetProperty("storedFingerprints").GetString());
        Assert.Equal("live-fingerprint", entry.GetProperty("liveFingerprints").GetString());
        Assert.False(entry.GetProperty("fingerprintsMatch").GetBoolean());
        var added = Assert.Single(entries, value =>
            value.GetProperty("relativePath").GetString() == "Blocks/New.xml");
        Assert.Equal(JsonValueKind.Null, added.GetProperty("storedFingerprints").ValueKind);
        Assert.Equal("new-live-fingerprint", added.GetProperty("liveFingerprints").GetString());
        Assert.Equal(JsonValueKind.Null, added.GetProperty("fingerprintsMatch").ValueKind);
        Assert.Empty(fixture.VersionControl.Calls);
    }

    [Fact]
    public async Task TiaComparisonApplyChangesOnlyExplicitlySelectedPaths()
    {
        await using var fixture = await SelectedApiFixture.CreateAsync(
            root,
            databaseExists: true,
            stageExport: (outputDir, _) =>
            {
                Directory.CreateDirectory(Path.Combine(outputDir, "Blocks"));
                File.WriteAllText(Path.Combine(outputDir, "Blocks", "Main.xml"), "<live/>");
                File.WriteAllText(Path.Combine(outputDir, "Blocks", "New.xml"), "<new/>");
                WriteComparisonManifest(outputDir, "live-fingerprint", includeNew: true);
            });
        fixture.WriteComparisonBaseline("stored-fingerprint");
        (await fixture.Client.PostAsync(
            $"/api/devices/{fixture.DeviceId}/refresh/stage",
            null)).EnsureSuccessStatusCode();
        var preview = await fixture.Client.GetFromJsonAsync<JsonElement>(
            $"/api/devices/{fixture.DeviceId}/refresh/preview");

        var response = await fixture.Client.PostAsJsonAsync(
            $"/api/devices/{fixture.DeviceId}/refresh/apply",
            new
            {
                previewId = preview.GetProperty("previewId").GetString(),
                approvedPaths = new[] { "Blocks/Main.xml" },
            });

        response.EnsureSuccessStatusCode();
        var apply = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            "FilesUpdated",
            Enum.GetName(
                typeof(RefreshApplyState),
                apply.GetProperty("state").GetInt32()));
        Assert.Equal(JsonValueKind.Null, apply.GetProperty("commitSha").ValueKind);
        Assert.Equal(JsonValueKind.Null, apply.GetProperty("error").ValueKind);
        Assert.Equal("<live/>", fixture.ReadBaseline("Blocks/Main.xml"));
        Assert.False(fixture.BaselineExists("Blocks/New.xml"));
        Assert.DoesNotContain(fixture.BaselineComponentIds(), id => id == "fb-new");
        Assert.Empty(fixture.VersionControl.Calls);
    }

    [Fact]
    public async Task LegacyRefreshPayloadAppliesAllAddedAndChangedPaths()
    {
        await using var fixture = await SelectedApiFixture.CreateAsync(
            root,
            databaseExists: true,
            stageExport: (outputDir, _) =>
            {
                Directory.CreateDirectory(Path.Combine(outputDir, "Blocks"));
                File.WriteAllText(Path.Combine(outputDir, "Blocks", "Main.xml"), "<live/>");
                File.WriteAllText(Path.Combine(outputDir, "Blocks", "New.xml"), "<new/>");
                WriteComparisonManifest(outputDir, "live-fingerprint", includeNew: true);
            });
        fixture.WriteComparisonBaseline("stored-fingerprint");
        (await fixture.Client.PostAsync($"/api/devices/{fixture.DeviceId}/refresh/stage", null))
            .EnsureSuccessStatusCode();
        var preview = await fixture.Client.GetFromJsonAsync<JsonElement>(
            $"/api/devices/{fixture.DeviceId}/refresh/preview");

        var response = await fixture.Client.PostAsJsonAsync(
            $"/api/devices/{fixture.DeviceId}/refresh/apply",
            new
            {
                previewId = preview.GetProperty("previewId").GetString(),
                approvedRemovalPaths = Array.Empty<string>(),
            });

        response.EnsureSuccessStatusCode();
        Assert.Equal("<live/>", fixture.ReadBaseline("Blocks/Main.xml"));
        Assert.Equal("<new/>", fixture.ReadBaseline("Blocks/New.xml"));
    }

    [Fact]
    public async Task NewRefreshPayloadWithEmptyApprovedPathsAppliesNothing()
    {
        await using var fixture = await SelectedApiFixture.CreateAsync(
            root,
            databaseExists: true,
            stageExport: (outputDir, _) =>
            {
                Directory.CreateDirectory(Path.Combine(outputDir, "Blocks"));
                File.WriteAllText(Path.Combine(outputDir, "Blocks", "Main.xml"), "<live/>");
                File.WriteAllText(Path.Combine(outputDir, "Blocks", "New.xml"), "<new/>");
                WriteComparisonManifest(outputDir, "live-fingerprint", includeNew: true);
            });
        fixture.WriteComparisonBaseline("stored-fingerprint");
        (await fixture.Client.PostAsync($"/api/devices/{fixture.DeviceId}/refresh/stage", null))
            .EnsureSuccessStatusCode();
        var preview = await fixture.Client.GetFromJsonAsync<JsonElement>(
            $"/api/devices/{fixture.DeviceId}/refresh/preview");

        var response = await fixture.Client.PostAsJsonAsync(
            $"/api/devices/{fixture.DeviceId}/refresh/apply",
            new
            {
                previewId = preview.GetProperty("previewId").GetString(),
                approvedPaths = Array.Empty<string>(),
            });

        response.EnsureSuccessStatusCode();
        Assert.Equal("<stored/>", fixture.ReadBaseline("Blocks/Main.xml"));
        Assert.False(fixture.BaselineExists("Blocks/New.xml"));
    }

    [Fact]
    public async Task LegacyRemovalApprovalRejectsAddedOrChangedEntries()
    {
        await using var fixture = await SelectedApiFixture.CreateAsync(
            root,
            databaseExists: true,
            stageExport: (outputDir, _) =>
            {
                Directory.CreateDirectory(Path.Combine(outputDir, "Blocks"));
                File.WriteAllText(Path.Combine(outputDir, "Blocks", "Main.xml"), "<live/>");
                File.WriteAllText(Path.Combine(outputDir, "Blocks", "New.xml"), "<new/>");
                WriteComparisonManifest(outputDir, "live-fingerprint", includeNew: true);
            });
        fixture.WriteComparisonBaseline("stored-fingerprint");
        (await fixture.Client.PostAsync(
            $"/api/devices/{fixture.DeviceId}/refresh/stage",
            null)).EnsureSuccessStatusCode();
        foreach (var path in new[] { "Blocks/Main.xml", "Blocks/New.xml", "Blocks/Unknown.xml" })
        {
            var preview = await fixture.Client.GetFromJsonAsync<JsonElement>(
                $"/api/devices/{fixture.DeviceId}/refresh/preview");
            var response = await fixture.Client.PostAsJsonAsync(
                $"/api/devices/{fixture.DeviceId}/refresh/apply",
                new
                {
                    previewId = preview.GetProperty("previewId").GetString(),
                    approvedRemovalPaths = new[] { path },
                });

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var error = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(
                DeviceReconciler.ApprovalInvalidCode,
                error.GetProperty("error").GetString());
        }
    }

    [Fact]
    public async Task LegacyRemovalApprovalRejectsUnchangedEntry()
    {
        await using var fixture = await SelectedApiFixture.CreateAsync(
            root,
            databaseExists: true,
            stageExport: (outputDir, _) =>
            {
                Directory.CreateDirectory(Path.Combine(outputDir, "Blocks"));
                File.WriteAllText(Path.Combine(outputDir, "Blocks", "Main.xml"), "<stored/>");
                WriteComparisonManifest(outputDir, "stored-fingerprint");
            });
        fixture.WriteComparisonBaseline("stored-fingerprint");
        (await fixture.Client.PostAsync(
            $"/api/devices/{fixture.DeviceId}/refresh/stage",
            null)).EnsureSuccessStatusCode();
        var preview = await fixture.Client.GetFromJsonAsync<JsonElement>(
            $"/api/devices/{fixture.DeviceId}/refresh/preview");

        var response = await fixture.Client.PostAsJsonAsync(
            $"/api/devices/{fixture.DeviceId}/refresh/apply",
            new
            {
                previewId = preview.GetProperty("previewId").GetString(),
                approvedRemovalPaths = new[] { "Blocks/Main.xml" },
            });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            DeviceReconciler.ApprovalInvalidCode,
            error.GetProperty("error").GetString());
    }

    [Fact]
    public async Task LegacyRemovalApprovalStillAppliesActualRemoval()
    {
        await using var fixture = await SelectedApiFixture.CreateAsync(
            root,
            databaseExists: true,
            stageExport: (outputDir, _) =>
            {
                Directory.CreateDirectory(outputDir);
                File.WriteAllText(
                    Path.Combine(outputDir, "metadata.json"),
                    """{"schemaVersion":"1.0","components":[]}""");
            });
        fixture.WriteComparisonBaseline("stored-fingerprint");
        (await fixture.Client.PostAsync(
            $"/api/devices/{fixture.DeviceId}/refresh/stage",
            null)).EnsureSuccessStatusCode();
        var preview = await fixture.Client.GetFromJsonAsync<JsonElement>(
            $"/api/devices/{fixture.DeviceId}/refresh/preview");

        var response = await fixture.Client.PostAsJsonAsync(
            $"/api/devices/{fixture.DeviceId}/refresh/apply",
            new
            {
                previewId = preview.GetProperty("previewId").GetString(),
                approvedRemovalPaths = new[] { "Blocks/Main.xml" },
            });

        response.EnsureSuccessStatusCode();
        Assert.False(fixture.BaselineExists("Blocks/Main.xml"));
    }

    private static void WriteComparisonManifest(
        string rootPath,
        string? fingerprints,
        bool includeNew = false)
    {
        var components = new List<object>
        {
            new
            {
                id = "ob-main",
                sourcePath = "Program/Main",
                exportedFile = "Blocks/Main.xml",
                fingerprints,
            },
        };
        if (includeNew)
        {
            components.Add(new
            {
                id = "fb-new",
                sourcePath = "Program/New",
                exportedFile = "Blocks/New.xml",
                fingerprints = "new-live-fingerprint",
            });
        }

        File.WriteAllText(
            Path.Combine(rootPath, "metadata.json"),
            JsonSerializer.Serialize(new
            {
                schemaVersion = "1.0",
                components,
            }));
    }

    [Theory]
    [InlineData(false, false, false, "missing")]
    [InlineData(true, true, false, "stale")]
    [InlineData(true, false, true, "stale")]
    [InlineData(true, false, false, "current")]
    public async Task SelectedDeviceSnapshotSerializesPersistedKnowledgeState(
        bool databaseExists,
        bool stale,
        bool baselineStale,
        string expected)
    {
        await using var fixture = await SelectedApiFixture.CreateAsync(
            Path.Combine(root, Guid.NewGuid().ToString("N")),
            databaseExists,
            stale,
            baselineStale);
        fixture.WriteManifest();

        var snapshot = await fixture.Client.GetFromJsonAsync<JsonElement>("/api/project/info");

        Assert.Equal(expected, snapshot.GetProperty("knowledge").GetProperty("state").GetString());
        Assert.Single(snapshot.GetProperty("blocks").EnumerateArray());
        Assert.Empty(fixture.Engineering.Calls);
    }

    [Fact]
    public async Task SelectedDeviceSnapshotAllowsEmptySourceWithoutEngineering()
    {
        await using var fixture = await SelectedApiFixture.CreateAsync(
            Path.Combine(root, Guid.NewGuid().ToString("N")),
            databaseExists: true);

        var snapshot = await fixture.Client.GetFromJsonAsync<JsonElement>("/api/project/info");

        Assert.Empty(snapshot.GetProperty("blocks").EnumerateArray());
        Assert.Empty(snapshot.GetProperty("diagnostics").EnumerateArray());
        Assert.Empty(fixture.Engineering.Calls);
    }

    [Fact]
    public async Task OpenProjectInTiaEndpointUsesExplicitOperationAndPreservesOfflineSnapshot()
    {
        await using var fixture = await SelectedApiFixture.CreateAsync(
            Path.Combine(root, Guid.NewGuid().ToString("N")),
            databaseExists: true,
            engineeringOffline: false,
            sourceProjectPath: @"C:\Projects\Line.ap17");
        fixture.WriteManifest();
        var before = await fixture.Client.GetStringAsync("/api/project/info");
        var artifactsBefore = fixture.PersistentArtifactHashes();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{fixture.DeviceRoute}/tia/open");
        request.Headers.Add("X-Operation-Id", "open-1");

        var response = await fixture.Client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        Assert.Equal(["connect"], fixture.Engineering.Calls);
        Assert.Equal(@"C:\Projects\Line.ap17",
            fixture.Engineering.Arguments["connect"].GetProperty("projectPath").GetString());
        Assert.True(fixture.Engineering.Arguments["connect"].GetProperty("withUI").GetBoolean());
        Assert.Equal(before, await fixture.Client.GetStringAsync("/api/project/info"));
        Assert.Equal(artifactsBefore, fixture.PersistentArtifactHashes());
        var operation = await fixture.Client.GetFromJsonAsync<JsonElement>("/api/operations/open-1");
        Assert.Equal("open-tia-project", operation.GetProperty("operationType").GetString());
        Assert.Equal("succeeded", operation.GetProperty("state").GetString());
    }

    [Fact]
    public async Task OpenProjectInTiaEndpointCanUseHeadlessMode()
    {
        await using var fixture = await SelectedApiFixture.CreateAsync(
            Path.Combine(root, Guid.NewGuid().ToString("N")),
            databaseExists: true,
            engineeringOffline: false,
            sourceProjectPath: @"C:\Projects\Line.ap17");

        var response = await fixture.Client.PostAsJsonAsync(
            $"{fixture.DeviceRoute}/tia/open",
            new { withUI = false });

        response.EnsureSuccessStatusCode();
        Assert.False(fixture.Engineering.Arguments["connect"].GetProperty("withUI").GetBoolean());
    }

    [Fact]
    public async Task FailedOpenProjectInTiaPreservesOfflineSnapshot()
    {
        await using var fixture = await SelectedApiFixture.CreateAsync(
            Path.Combine(root, Guid.NewGuid().ToString("N")),
            databaseExists: true,
            sourceProjectPath: @"C:\Projects\Line.ap17");
        fixture.WriteManifest();
        var before = await fixture.Client.GetStringAsync("/api/project/info");
        var artifactsBefore = fixture.PersistentArtifactHashes();

        var response = await fixture.Client.PostAsync($"{fixture.DeviceRoute}/tia/open", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await fixture.Client.GetStringAsync("/api/project/info"));
        Assert.Equal(artifactsBefore, fixture.PersistentArtifactHashes());
    }

    [Fact]
    public async Task ExplicitTiaOpenIgnoresMutableSelectionAndUsesRequestedWorktree()
    {
        await using var fixture = await SelectedApiFixture.CreateAsync(
            Path.Combine(root, Guid.NewGuid().ToString("N")),
            databaseExists: true,
            engineeringOffline: false,
            sourceProjectPath: @"C:\Projects\Master.ap17");
        var other = fixture.AddWorktree("wt-2", "other", @"C:\Projects\Other.ap17");
        await fixture.Client.GetAsync("/api/workbenches");
        (await fixture.Client.PostAsync(other.SelectRoute, null)).EnsureSuccessStatusCode();

        var response = await fixture.Client.PostAsync($"{fixture.DeviceRoute}/tia/open", null);

        response.EnsureSuccessStatusCode();
        Assert.Equal(@"C:\Projects\Master.ap17",
            fixture.Engineering.Arguments["connect"].GetProperty("projectPath").GetString());
    }

    [Fact]
    public async Task ExplicitDeviceSnapshotIgnoresSelectionFlipWithSharedDeviceId()
    {
        await using var fixture = await SelectedApiFixture.CreateAsync(
            Path.Combine(root, Guid.NewGuid().ToString("N")),
            databaseExists: true,
            sourceProjectPath: @"C:\Projects\Master.ap17");
        var other = fixture.AddWorktree("wt-2", "other", @"C:\Projects\Other.ap17");
        await fixture.Client.GetAsync("/api/workbenches");
        (await fixture.Client.PostAsync(other.SelectRoute, null)).EnsureSuccessStatusCode();

        var master = await fixture.Client.GetFromJsonAsync<JsonElement>(fixture.DeviceRoute);
        var selectedOther = await fixture.Client.GetFromJsonAsync<JsonElement>(other.DeviceRoute);

        Assert.Equal("wt-1", master.GetProperty("worktreeId").GetString());
        Assert.Equal("wt-2", selectedOther.GetProperty("worktreeId").GetString());
        Assert.Equal(fixture.DeviceId, master.GetProperty("deviceId").GetString());
        Assert.Equal(fixture.DeviceId, selectedOther.GetProperty("deviceId").GetString());
    }

    [Fact]
    public async Task MissingProjectPathFailsOperationBeforeEngineeringCall()
    {
        await using var fixture = await SelectedApiFixture.CreateAsync(
            Path.Combine(root, Guid.NewGuid().ToString("N")),
            databaseExists: true,
            engineeringOffline: false);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{fixture.DeviceRoute}/tia/open");
        request.Headers.Add("X-Operation-Id", "open-missing");

        var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(fixture.Engineering.Calls);
        var operation = await fixture.Client.GetFromJsonAsync<JsonElement>(
            "/api/operations/open-missing");
        Assert.Equal("failed", operation.GetProperty("state").GetString());
        Assert.Contains("No engineering project path is registered",
            operation.GetProperty("errorMessage").GetString());
    }

    [Fact]
    public async Task AttachTiaInstanceEndpointConnectsBySessionIdOnly()
    {
        await using var fixture = await SelectedApiFixture.CreateAsync(
            Path.Combine(root, Guid.NewGuid().ToString("N")),
            databaseExists: true,
            engineeringOffline: false,
            sourceProjectPath: @"C:\Projects\Line.ap17");
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{fixture.DeviceRoute}/tia/attach")
        {
            Content = JsonContent.Create(new { sessionId = 4242 }),
        };
        request.Headers.Add("X-Operation-Id", "attach-1");

        var response = await fixture.Client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        Assert.Equal(["connect"], fixture.Engineering.Calls);
        Assert.Equal(4242,
            fixture.Engineering.Arguments["connect"].GetProperty("sessionId").GetInt32());
        Assert.False(fixture.Engineering.Arguments["connect"].TryGetProperty("projectPath", out _));
        var operation = await fixture.Client.GetFromJsonAsync<JsonElement>("/api/operations/attach-1");
        Assert.Equal("attach-tia-instance", operation.GetProperty("operationType").GetString());
        Assert.Equal("succeeded", operation.GetProperty("state").GetString());
    }

    [Fact]
    public async Task AttachTiaInstanceRejectsUnknownDevice()
    {
        await using var fixture = await SelectedApiFixture.CreateAsync(
            Path.Combine(root, Guid.NewGuid().ToString("N")),
            databaseExists: true,
            engineeringOffline: false,
            sourceProjectPath: @"C:\Projects\Line.ap17");

        var response = await fixture.Client.PostAsJsonAsync(
            $"{fixture.DeviceRoute}x/tia/attach",
            new { sessionId = 4242 });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(fixture.Engineering.Calls);
    }

    [Fact]
    public async Task SandboxRootsEndpointListsAllowedRoots()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();

        var body = await client.GetFromJsonAsync<JsonElement>("/api/sandbox/roots");

        var roots = body.GetProperty("roots").EnumerateArray()
            .Select(value => value.GetString()!)
            .ToArray();
        Assert.Contains(roots, value => value.Contains("AutomationWorkbench", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CreateWorkbenchWithProjectPathLaunchesNewTiaInstance()
    {
        var projectPath = Path.Combine(root, "Line.ap17");
        Directory.CreateDirectory(root);
        File.WriteAllText(projectPath, "origin project placeholder");
        var engineering = new CreateFlowEngineeringCaller(projectPath);
        var versionControl = new CreateFlowVersionControlCaller();
        var sandboxFile = Path.Combine(root, "sandbox.json");
        File.WriteAllText(sandboxFile, JsonSerializer.Serialize(new { allowedRoots = new[] { root } }));
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<SandboxConfig>();
                services.AddSingleton(SandboxConfig.Load(sandboxFile));
                services.RemoveAll<WorkbenchCoordinator>();
                services.AddSingleton(sp => new WorkbenchCoordinator(
                    engineering,
                    new RecordingToolCaller(),
                    versionControl,
                    sp.GetRequiredService<WorkbenchCatalog>(),
                    sp.GetRequiredService<AtomicJsonStore>(),
                    sp.GetRequiredService<DeviceReconciler>(),
                    sp.GetRequiredService<DeviceSourceResolver>(),
                    sp.GetRequiredService<DeviceOperationLock>(),
                    sp.GetRequiredService<SandboxConfig>().PathJail));
            });
        });
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/workbenches", new
        {
            name = "Line",
            rootPath = Path.Combine(root, "wb-from-path"),
            engineeringProjectPath = projectPath,
        });

        response.EnsureSuccessStatusCode();
        Assert.Equal(
            new[]
            {
                "connect", "get_project_info", "save_project_as", "get_project_info",
                "compile_plc", "get_plc_checksums", "rebuild_export", "disconnect",
            },
            engineering.Calls);
        Assert.Equal(projectPath, engineering.Arguments[0].GetProperty("projectPath").GetString());
        Assert.False(engineering.Arguments[0].GetProperty("withUI").GetBoolean());
        Assert.Equal(
            new[]
            {
                "vc_init_shared", "svn_init_shared", "svn_checkout", "svn_commit", "vc_commit_selected",
            },
            versionControl.Calls);
        var workbenchRoot = Path.Combine(root, "wb-from-path");
        var store = new AtomicJsonStore();
        var workbench = store.Read<WorkbenchMetadata>(Path.Combine(workbenchRoot, "workbench.json"));
        var registration = Assert.Single(workbench.Worktrees);
        var worktreeRoot = Path.Combine(workbenchRoot, "worktrees", registration.RelativePath);
        var deviceRoot = Path.Combine(worktreeRoot, "devices", "PLC_1");

        Assert.Equal("1.2", workbench.SchemaVersion);
        Assert.Equal(Path.Combine(workbenchRoot, "repository.svn"), workbench.SvnRepositoryPath);
        Assert.Equal(projectPath, workbench.OriginProjectPath);
        Assert.NotNull(workbench.OriginImportedAt);
        Assert.NotNull(workbench.ManagedTiaProjectPath);
        Assert.True(File.Exists(Path.Combine(worktreeRoot, "worktree.json")));
        Assert.True(File.Exists(Path.Combine(worktreeRoot, "engineering-state", "revision.json")));
        Assert.True(File.Exists(Path.Combine(deviceRoot, "device.json")));
        Assert.True(Directory.Exists(Path.Combine(deviceRoot, "source")));
        Assert.True(Directory.Exists(Path.Combine(deviceRoot, "staging")));
        Assert.False(Directory.Exists(Path.Combine(deviceRoot, "exported-source")));
        Assert.False(Directory.Exists(Path.Combine(deviceRoot, "modified-source")));
    }

    [Fact]
    public async Task CreateWorkbenchRejectsAmbiguousEngineeringConnection()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/workbenches", new
        {
            name = "Line",
            engineeringSessionId = 42,
            engineeringProjectPath = @"C:\Projects\Line.ap17",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ENGINEERING_CONNECTION_INVALID", error.GetProperty("error").GetString());
    }

    [Fact]
    public async Task CreateWorkbenchRejectsProjectPathOutsideSandboxWithAllowedRoots()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/workbenches", new
        {
            name = "Line",
            engineeringProjectPath = Path.Combine(root, "outside", "Line.ap17"),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("SANDBOX_PATH_DENIED", error.GetProperty("error").GetString());
        Assert.Contains("AutomationWorkbench",
            error.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SelectedDeviceBlocksComeFromPersistedSnapshotWithoutEngineering()
    {
        await using var fixture = await SelectedApiFixture.CreateAsync(
            Path.Combine(root, Guid.NewGuid().ToString("N")),
            databaseExists: true);
        fixture.WriteManifest();

        var blocks = await fixture.Client.GetFromJsonAsync<JsonElement>("/api/blocks");

        var block = Assert.Single(blocks.EnumerateArray());
        Assert.Equal("Main", block.GetProperty("name").GetString());
        Assert.Equal("OB", block.GetProperty("blockType").GetString());
        Assert.Empty(fixture.Engineering.Calls);
    }

    [Fact]
    public void BinderRejectsConflictingStoragePath()
    {
        var device = Context();
        var binder = new DeviceToolArgumentBinder(new DeviceSourceResolver(_ => { }));

        Assert.Throws<ArgumentException>(() => binder.Bind(
            "vc_status",
            new Dictionary<string, object?> { ["repoPath"] = Path.Combine(root, "other") },
            device));
    }

    [Fact]
    public async Task DestructiveConfirmationExecutesOnceAndRejectionDoesNotExecute()
    {
        var caller = new RecordingToolCaller();
        var gateway = new ApiMcpGateway(caller, caller, caller, caller);
        var pending = new PendingToolActions();
        var executor = new SandboxedToolExecutor(
            new SandboxPolicy(),
            new DeviceToolArgumentBinder(new DeviceSourceResolver(_ => { })),
            gateway,
            pending);
        var requested = await executor.RequestAsync(
            "vc_restore",
            new Dictionary<string, object?> { ["filePath"] = "Blocks/A.xml" },
            Context(),
            "requester",
            CancellationToken.None);
        var id = requested!.GetType().GetProperty("_confirmationId")!.GetValue(requested)!.ToString()!;

        await pending.ResolveAsync(id, ToolConfirmation.AllowOnce, DeviceContextIdentity.Key(Context()), "requester");
        Assert.Single(caller.Calls);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => pending.ResolveAsync(id, ToolConfirmation.AllowOnce, DeviceContextIdentity.Key(Context()), "requester"));

        var rejected = await executor.RequestAsync("vc_restore", new Dictionary<string, object?>(), Context(), "requester", CancellationToken.None);
        var rejectedId = rejected!.GetType().GetProperty("_confirmationId")!.GetValue(rejected)!.ToString()!;
        await pending.ResolveAsync(rejectedId, ToolConfirmation.Deny, DeviceContextIdentity.Key(Context()), "requester");
        Assert.Single(caller.Calls);
    }

    [Fact]
    public void ChatIdentitySeparatesSameDeviceIdAcrossWorktrees()
    {
        var first = Context();
        var second = first with { WorktreeId = "other-worktree" };
        Assert.NotEqual(DeviceContextIdentity.Key(first), DeviceContextIdentity.Key(second));
    }

    [Theory]
    [InlineData("src_validate", "source")]
    [InlineData("get_schema", "knowledge")]
    [InlineData("query", "knowledge")]
    [InlineData("get_block", "knowledge")]
    [InlineData("get_network_logic", "knowledge")]
    [InlineData("search", "knowledge")]
    [InlineData("vc_status", "vc")]
    [InlineData("list_blocks", "engineering")]
    public void GatewayRoutesEveryToolFamilyToItsOwner(string tool, string owner)
    {
        var engineering = new RecordingToolCaller();
        var knowledge = new RecordingToolCaller();
        var vc = new RecordingToolCaller();
        var source = new RecordingToolCaller();
        var gateway = new ApiMcpGateway(engineering, knowledge, vc, source);

        Assert.Same(owner switch
        {
            "knowledge" => knowledge,
            "vc" => vc,
            "source" => source,
            _ => engineering,
        }, gateway.For(tool));
    }

    [Fact]
    public void PartialExportIsRejectedWithoutTouchingStagedSnapshot()
    {
        var context = Context();
        Directory.CreateDirectory(context.StagingRoot);
        var snapshot = Path.Combine(context.StagingRoot, "metadata.json");
        File.WriteAllText(snapshot, "keep");
        var binder = new DeviceToolArgumentBinder(new DeviceSourceResolver(_ => { }));

        Assert.Throws<WorkbenchLifecycleException>(() =>
            binder.Bind("export_block", new Dictionary<string, object?>(), context));
        Assert.Equal("keep", File.ReadAllText(snapshot));
    }

    [Fact]
    public async Task PendingConfirmationRejectsWrongContextAndExpires()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var pending = new PendingToolActions(clock, TimeSpan.FromSeconds(1));
        var id = pending.Add("right", "requester", (_, _) => Task.FromResult<object?>(true));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pending.ResolveAsync(id, ToolConfirmation.AllowOnce, "wrong", "requester"));
        Assert.True((bool)(await pending.ResolveAsync(id, ToolConfirmation.AllowOnce, "right", "requester"))!);

        var expired = pending.Add("right", "requester", (_, _) => Task.FromResult<object?>(true));
        clock.Advance(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            pending.ResolveAsync(expired, ToolConfirmation.AllowOnce, "right", "requester"));
    }

    [Fact]
    public void KnowledgeAndSourceReadsAreBoundToSelectedDevice()
    {
        var context = Context();
        Directory.CreateDirectory(context.SourceRoot);
        var source = Path.Combine(context.SourceRoot, "A.xml");
        File.WriteAllText(source, "<a/>");
        var binder = new DeviceToolArgumentBinder(new DeviceSourceResolver(_ => { }));

        var knowledge = binder.Bind("get_schema", new Dictionary<string, object?>(), context);
        Assert.Equal(context.KnowledgeDbPath, knowledge["dbPath"]);
        var ingest = binder.Bind("ingest_source", new Dictionary<string, object?>(), context);
        Assert.Equal(context.SourceRoot, ingest["sourceRoot"]);
        Assert.False(ingest.ContainsKey("exportedSourceRoot"));
        Assert.False(ingest.ContainsKey("modifiedSourceRoot"));
        var update = binder.Bind("update_components", new Dictionary<string, object?>(), context);
        Assert.Equal(context.SourceRoot, update["sourceRoot"]);
        Assert.False(update.ContainsKey("exportedSourceRoot"));
        Assert.False(update.ContainsKey("modifiedSourceRoot"));
        Assert.Throws<ArgumentException>(() => binder.Bind(
            "search", new Dictionary<string, object?> { ["dbPath"] = Path.Combine(root, "other.db") }, context));
        var parsed = binder.Bind("src_parse_block", new Dictionary<string, object?> { ["xmlFilePath"] = source }, context);
        Assert.Equal(source, parsed["xmlFilePath"]);
        Assert.Throws<ArgumentException>(() => binder.Bind(
            "src_validate", new Dictionary<string, object?> { ["xmlFilePath"] = Path.Combine(root, "foreign.xml") }, context));
    }

    [Fact]
    public void ImportBlockBindsToExistingModifiedSourceOnly()
    {
        var context = Context();
        var modified = Path.Combine(context.SourceRoot, "Blocks", "A.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(modified)!);
        File.WriteAllText(modified, "<a/>");
        var binder = new DeviceToolArgumentBinder(new DeviceSourceResolver(_ => { }));

        var bound = binder.Bind(
            "import_block",
            new Dictionary<string, object?> { ["relativePath"] = "Blocks/A.xml" },
            context);
        Assert.Equal(modified, bound["xmlFilePath"]);
        Assert.False(bound.ContainsKey("relativePath"));
        Assert.Throws<FileNotFoundException>(() => binder.Bind(
            "import_block", new Dictionary<string, object?> { ["relativePath"] = "Blocks/Missing.xml" }, context));
        Assert.Throws<Agent.Workbench.WorkbenchPathException>(() => binder.Bind(
            "import_block", new Dictionary<string, object?> { ["xmlFilePath"] = Path.Combine(root, "foreign.xml") }, context));
    }

    [Fact]
    public void ApplyEditsBindsTrackedSourceAsConfirmedInPlace()
    {
        var context = Context();
        var baseline = Path.Combine(context.SourceRoot, "Blocks", "A.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(baseline)!);
        File.WriteAllText(baseline, "<a/>");
        var binder = new DeviceToolArgumentBinder(new DeviceSourceResolver(_ => { }));
        var bound = binder.Bind(
            "src_apply_edits",
            new Dictionary<string, object?> { ["relativePath"] = "Blocks/A.xml" },
            context);
        Assert.Equal(baseline, bound["xmlFilePath"]);
        Assert.Equal(baseline, bound["outputFilePath"]);
        Assert.Equal(context.SourceRoot, bound["sourceRoot"]);
        Assert.Equal(true, bound["inPlace"]);
        Assert.Equal(true, bound["confirmInPlace"]);
        Assert.False(bound.ContainsKey("overwriteOutput"));
    }

    [Fact]
    public void ApplyEditsRejectsCallerSuppliedSourceRootFromAnotherDevice()
    {
        var context = Context();
        var source = Path.Combine(context.SourceRoot, "Blocks", "A.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "<a/>");
        var binder = new DeviceToolArgumentBinder(new DeviceSourceResolver(_ => { }));

        Assert.Throws<ArgumentException>(() => binder.Bind(
            "src_apply_edits",
            new Dictionary<string, object?>
            {
                ["relativePath"] = "Blocks/A.xml",
                ["sourceRoot"] = Path.Combine(root, "other", "devices", "PLC", "source"),
            },
            context));
    }

    [Fact]
    public void ApplyEditsDiscardsCopyOutputFlagAndForcesInPlaceFlags()
    {
        var context = Context();
        var source = Path.Combine(context.SourceRoot, "Blocks", "A.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "<a/>");
        var binder = new DeviceToolArgumentBinder(new DeviceSourceResolver(_ => { }));

        var bound = binder.Bind(
            "src_apply_edits",
            new Dictionary<string, object?>
            {
                ["relativePath"] = "Blocks/A.xml",
                ["overwriteOutput"] = true,
            },
            context);

        Assert.False(bound.ContainsKey("overwriteOutput"));
        Assert.Equal(true, bound["inPlace"]);
        Assert.Equal(true, bound["confirmInPlace"]);
    }

    [Fact]
    public async Task BoundCallerConvertsBinderFailuresIntoToolCallException()
    {
        var bound = new ApiChatService.BoundMcpCaller(
            new RecordingToolCaller(),
            new DeviceToolArgumentBinder(new DeviceSourceResolver(_ => { })),
            Context());

        var exception = await Assert.ThrowsAsync<ToolCallException>(() =>
            bound.CallAsync<JsonElement>(
                "import_block",
                new Dictionary<string, object?> { ["relativePath"] = "Blocks/Missing.xml" },
                CancellationToken.None));

        Assert.Equal("TOOL_ARGUMENT_BINDING_FAILED", exception.Code);
        Assert.NotNull(exception.Remediation);
    }

    [Fact]
    public void SourceToolsAcceptListedRelativeXmlFilePath()
    {
        var context = Context();
        var baseline = Path.Combine(context.SourceRoot, "Blocks", "A.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(baseline)!);
        File.WriteAllText(baseline, "<a/>");
        var binder = new DeviceToolArgumentBinder(new DeviceSourceResolver(_ => { }));

        // src_parse_block with the relative sourceFile returned by get_block resolves into the device roots.
        var parsed = binder.Bind(
            "src_parse_block",
            new Dictionary<string, object?> { ["xmlFilePath"] = "Blocks/A.xml" },
            context);
        Assert.Equal(baseline, parsed["xmlFilePath"]);

        // An existing overlay wins over the baseline for reads.
        var overlay = Path.Combine(context.SourceRoot, "Blocks", "A.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(overlay)!);
        File.WriteAllText(overlay, "<b/>");
        var parsedOverlay = binder.Bind(
            "src_parse_block",
            new Dictionary<string, object?> { ["xmlFilePath"] = "Blocks/A.xml" },
            context);
        Assert.Equal(overlay, parsedOverlay["xmlFilePath"]);

        // src_apply_edits with the relative sourceFile behaves like relativePath.
        var apply = binder.Bind(
            "src_apply_edits",
            new Dictionary<string, object?> { ["xmlFilePath"] = "Blocks/A.xml" },
            context);
        Assert.Equal(overlay, apply["xmlFilePath"]);
        Assert.Equal(overlay, apply["outputFilePath"]);

        // import_block with the relative sourceFile resolves into modified-source.
        var import = binder.Bind(
            "import_block",
            new Dictionary<string, object?> { ["xmlFilePath"] = "Blocks/A.xml" },
            context);
        Assert.Equal(overlay, import["xmlFilePath"]);

        // Traversal is still rejected.
        Assert.Throws<Agent.Workbench.WorkbenchPathException>(() => binder.Bind(
            "src_parse_block",
            new Dictionary<string, object?> { ["xmlFilePath"] = "../outside.xml" },
            context));
    }

    [Fact]
    public async Task ExpiryActivelyDeniesWaitingConfirmation()
    {
        var pending = new PendingToolActions(TimeProvider.System, TimeSpan.FromMilliseconds(30));
        var released = new TaskCompletionSource<ToolConfirmation>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending.Add("context", "requester", (decision, _) =>
        {
            released.TrySetResult(decision);
            return Task.FromResult<object?>(null);
        });

        Assert.Equal(ToolConfirmation.Deny, await released.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task SelectionResolvesRegisteredDeviceAndUnknownApprovalIsConflict()
    {
        var store = new AtomicJsonStore();
        var catalog = new WorkbenchCatalog(store, root);
        var wb = catalog.Create("Line", null);
        var wtId = "wt-1";
        wb = catalog.RegisterWorktree(wb, new(wtId, "master", "master", "master"));
        var wtRoot = Path.Combine(wb.RootPath, "worktrees", "master");
        Directory.CreateDirectory(wtRoot);
        var wt = new WorktreeMetadata("1.0", wtId, wb.WorkbenchId, "master", "master",
            DateTimeOffset.UtcNow.ToString("O"), null, null, null, ["dev-1"], null);
        store.Write(Path.Combine(wtRoot, "worktree.json"), wt);
        var deviceRoot = Path.Combine(wtRoot, "devices", "PLC_1");
        Directory.CreateDirectory(deviceRoot);
        store.Write(Path.Combine(deviceRoot, "device.json"),
            new DeviceMetadata("1.0", "dev-1", wtId, "PLC:1", "PLC:1", null, null, null,
                new KnowledgeState(true, new Dictionary<string, string>(), null), []));
        var runtimeState = new CompatibilityRuntimeState();

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
        {
            host.UseEnvironment("Testing");
            host.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["chatSettings:reasoningEffort"] = ChatRequestSettings.DefaultReasoningEffort,
                }));
            host.ConfigureServices(services =>
            {
                services.RemoveAll<WorkbenchCatalog>();
                services.RemoveAll<AtomicJsonStore>();
                services.RemoveAll<WorkbenchApiState>();
                services.RemoveAll<CompatibilityRuntimeState>();
                services.RemoveAll<CompatibilityConfigStore>();
                services.AddSingleton(store);
                services.AddSingleton(catalog);
                services.AddSingleton<WorkbenchApiState>();
                services.AddSingleton(runtimeState);
                services.AddSingleton(new CompatibilityConfigStore(Path.Combine(root, "config.json")));
            });
        });
        using var client = factory.CreateClient();

        var defaultSettings = await client.GetFromJsonAsync<JsonElement>("/api/config/settings");
        Assert.Equal("deepseek-v4-flash", defaultSettings.GetProperty("model").GetString());
        Assert.Equal("high", defaultSettings.GetProperty("reasoningEffort").GetString());

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.PostAsync($"/api/workbenches/{wb.WorkbenchId}/worktrees/{wtId}/devices/dev-1/select", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.GetAsync("/api/devices/dev-1/sessions")).StatusCode);
        var createdSessionResponse = await client.PostAsync("/api/chat/session/new", null);
        createdSessionResponse.EnsureSuccessStatusCode();
        var createdSession = await createdSessionResponse.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = createdSession.GetProperty("header").GetProperty("sessionId").GetString()!;
        var renameResponse = await client.PostAsJsonAsync(
            "/api/chat/session/rename",
            new { sessionId, title = "  Startup checks  " });
        renameResponse.EnsureSuccessStatusCode();
        var renamedSession = await renameResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            "Startup checks",
            renamedSession.GetProperty("header").GetProperty("title").GetString());
        var sessionList = await client.GetFromJsonAsync<JsonElement[]>("/api/chat/sessions");
        Assert.Equal("Startup checks", Assert.Single(sessionList!).GetProperty("title").GetString());
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync(
                "/api/chat/session/rename",
                new { sessionId = SessionManager.NewSessionId(), title = "Missing" })).StatusCode);
        var secondSessionResponse = await client.PostAsync("/api/chat/session/new", null);
        var secondSession = await secondSessionResponse.Content.ReadFromJsonAsync<JsonElement>();
        var secondId = secondSession.GetProperty("header").GetProperty("sessionId").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/chat/history")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/chat/clear", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync(
            "/api/chat/session/delete", new { sessionId = secondId })).StatusCode);
        var deletedInfo = await client.GetFromJsonAsync<JsonElement>("/api/chat/session/info");
        Assert.True(deletedInfo.GetProperty("requiresExplicitSession").GetBoolean());
        var thirdSession = await (await client.PostAsync("/api/chat/session/new", null))
            .Content.ReadFromJsonAsync<JsonElement>();
        var thirdId = thirdSession.GetProperty("header").GetProperty("sessionId").GetString()!;
        var newInfo = await client.GetFromJsonAsync<JsonElement>("/api/chat/session/info");
        Assert.False(newInfo.GetProperty("requiresExplicitSession").GetBoolean());
        await client.PostAsJsonAsync("/api/chat/session/delete", new { sessionId = thirdId });
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync(
            "/api/chat/session/load", new { sessionId })).StatusCode);
        var loadedInfo = await client.GetFromJsonAsync<JsonElement>("/api/chat/session/info");
        Assert.False(loadedInfo.GetProperty("requiresExplicitSession").GetBoolean());
        var generation = runtimeState.ChatGeneration;
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync(
            "/api/config/settings",
            new { model = "new-model", thinkingEnabled = false, reasoningEffort = "low", temperature = 0.2, topP = 0.8, historyTokenThreshold = 12345 })).StatusCode);
        Assert.Equal(generation + 1, runtimeState.ChatGeneration);
        Assert.Equal("new-model", runtimeState.ChatSettings!.Value.GetProperty("model").GetString());
        Assert.Equal(12_345, runtimeState.ChatSettings!.Value.GetProperty("historyTokenThreshold").GetInt32());
        var resolvedSettings = await client.GetFromJsonAsync<JsonElement>("/api/config/settings");
        Assert.Equal("new-model", resolvedSettings.GetProperty("model").GetString());
        Assert.False(resolvedSettings.GetProperty("thinkingEnabled").GetBoolean());
        Assert.Equal("low", resolvedSettings.GetProperty("reasoningEffort").GetString());
        Assert.Equal(0.2, resolvedSettings.GetProperty("temperature").GetDouble());
        Assert.Equal(0.8, resolvedSettings.GetProperty("topP").GetDouble());
        Assert.Equal(12_345, resolvedSettings.GetProperty("historyTokenThreshold").GetInt32());
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync(
            "/api/config/key", new { apiKey = "replacement" })).StatusCode);
        Assert.Equal(generation + 2, runtimeState.ChatGeneration);
        var conflict = await client.PostAsJsonAsync("/api/devices/dev-1/refresh/apply",
            new RefreshApplyApiRequest("unknown", []));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }

    [Fact]
    public void OpenAndSelectUseImmutableIdsAndIgnoreLegacyExports()
    {
        var store = new AtomicJsonStore();
        var catalog = new WorkbenchCatalog(store, root);
        var created = catalog.Create("Line 1", null);
        Directory.CreateDirectory(Path.Combine(root, "PlcAiAssistant", "exports", "legacy"));

        var state = new WorkbenchApiState(catalog, store);

        Assert.Single(state.List());
        Assert.Equal(created.WorkbenchId, state.List()[0].WorkbenchId);
        state.Select(created.WorkbenchId);
        Assert.Equal(created.WorkbenchId, state.Selection!.WorkbenchId);
    }

    [Fact]
    public void UnknownApprovalCannotBeReusedOrAppliedToAnotherDevice()
    {
        var store = new AtomicJsonStore();
        var state = new WorkbenchApiState(new WorkbenchCatalog(store, root), store);
        var preview = new ReconciliationPreview("approval", "wt", "device-a", "base", "stage", []);
        state.Remember(preview);

        Assert.Throws<KeyNotFoundException>(() => state.Take("approval", "device-b"));
        Assert.Throws<KeyNotFoundException>(() => state.Take("missing", "device-a"));
    }

    [Fact]
    public void OpeningPersistedWorkbenchRegistersItsCustomRootForMcpSandboxes()
    {
        var store = new AtomicJsonStore();
        var catalog = new WorkbenchCatalog(store, Path.Combine(root, "defaults"));
        var customRoot = Path.Combine(root, "chosen", "Line");
        var created = catalog.Create("Line", customRoot);
        var registry = new TrustedWorkbenchRootRegistry(Path.Combine(root, "trusted-roots.json"));
        var state = new WorkbenchApiState(catalog, store, registry);

        state.Open(created.RootPath);

        Assert.Contains(
            registry.Read(),
            registered => string.Equals(registered, created.RootPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void OpeningWorkbenchRejectsMetadataThatRedirectsTrustToAnotherRoot()
    {
        var store = new AtomicJsonStore();
        var catalog = new WorkbenchCatalog(store, Path.Combine(root, "defaults"));
        var customRoot = Path.Combine(root, "chosen", "Line");
        var created = catalog.Create("Line", customRoot);
        var redirectedRoot = Path.Combine(root, "unregistered");
        store.Write(
            Path.Combine(customRoot, "workbench.json"),
            created with { RootPath = redirectedRoot });
        Directory.CreateDirectory(redirectedRoot);
        store.Write(
            Path.Combine(redirectedRoot, "workbench.json"),
            created with { RootPath = redirectedRoot });
        var registry = new TrustedWorkbenchRootRegistry(Path.Combine(root, "trusted-roots.json"));
        var state = new WorkbenchApiState(catalog, store, registry);

        Assert.Throws<WorkbenchCatalogException>(() => state.Open(customRoot));
        Assert.DoesNotContain(
            registry.Read(),
            registered => string.Equals(registered, redirectedRoot, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CatalogReloadRemovesDeletedWorkbenchFromTrustedRegistry()
    {
        var store = new AtomicJsonStore();
        var catalog = new WorkbenchCatalog(store, Path.Combine(root, "defaults"));
        var created = catalog.Create("Line", Path.Combine(root, "custom", "Line"));
        var registry = new TrustedWorkbenchRootRegistry(Path.Combine(root, "trusted-roots.json"));
        var state = new WorkbenchApiState(catalog, store, registry);
        state.Open(created.RootPath);
        Assert.Contains(created.RootPath, registry.Read(), StringComparer.OrdinalIgnoreCase);

        File.Delete(Path.Combine(created.RootPath, "workbench.json"));
        Assert.Empty(state.List());
        Assert.Empty(registry.Read());
    }

    [Fact]
    public void McpHostPassesTrustedRegistryLocationToAllServers()
    {
        var registryPath = Path.Combine(root, "trusted-roots.json");
        var environment = new Dictionary<string, string?>
        {
            [TrustedWorkbenchRootRegistry.EnvironmentVariableName] = registryPath,
        };

        var host = new McpHost("engineering.exe", "knowledge.exe", "vc.exe", "source.exe", environment);

        Assert.Equal(registryPath,
            host.Engineering.EnvironmentVariables[TrustedWorkbenchRootRegistry.EnvironmentVariableName]);
        Assert.Equal(registryPath,
            host.SourceEditor!.EnvironmentVariables[TrustedWorkbenchRootRegistry.EnvironmentVariableName]);
        Assert.Equal(registryPath,
            host.Knowledge.EnvironmentVariables[TrustedWorkbenchRootRegistry.EnvironmentVariableName]);
        Assert.Equal(registryPath,
            host.VersionControl!.EnvironmentVariables[TrustedWorkbenchRootRegistry.EnvironmentVariableName]);
    }

    [Fact]
    public async Task BootstrapEndpointCreatesInitialBaselineCommitAndCompletesOperation()
    {
        await using var fixture = await SelectedApiFixture.CreateAsync(
            root,
            databaseExists: false,
            stageExport: (outputDir, plcName) =>
            {
                Assert.Equal("PLC_1", plcName);
                Directory.CreateDirectory(Path.Combine(outputDir, "Blocks"));
                File.WriteAllText(Path.Combine(outputDir, "Blocks", "Main.xml"), "<live/>");
                WriteComparisonManifest(outputDir, "live-fingerprint");
            });
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{fixture.DeviceRoute}/bootstrap");
        request.Headers.Add("X-Operation-Id", "bootstrap-1");

        var response = await fixture.Client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            "FilesUpdated",
            Enum.GetName(
                typeof(RefreshApplyState),
                body.GetProperty("baseline").GetProperty("state").GetInt32()));
        Assert.Equal(
            "baseline-1",
            body.GetProperty("baseline").GetProperty("commitSha").GetString());
        Assert.Equal(
            JsonValueKind.Null,
            body.GetProperty("baseline").GetProperty("error").ValueKind);
        Assert.True(body.TryGetProperty("knowledge", out _));
        Assert.Equal("<live/>", fixture.ReadBaseline("Blocks/Main.xml"));
        Assert.Equal(["vc_log", "vc_commit_selected"], fixture.VersionControl.Calls);
        var operation = await fixture.Client.GetFromJsonAsync<JsonElement>("/api/operations/bootstrap-1");
        Assert.Equal("bootstrap-device", operation.GetProperty("operationType").GetString());
        Assert.Equal("succeeded", operation.GetProperty("state").GetString());
    }

    [Fact]
    public async Task BootstrapEndpointSurfacesCompileRequiredWithoutCommitting()
    {
        await using var fixture = await SelectedApiFixture.CreateAsync(
            root,
            databaseExists: false,
            stageExport: (_, _) =>
                throw new WorkbenchLifecycleException("PLC_COMPILE_REQUIRED", "Compile PLC_1 first."));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{fixture.DeviceRoute}/bootstrap");
        request.Headers.Add("X-Operation-Id", "bootstrap-2");

        var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("PLC_COMPILE_REQUIRED", error.GetProperty("error").GetString());
        Assert.Empty(fixture.VersionControl.Calls);
        Assert.False(fixture.BaselineExists("Blocks/Main.xml"));
        var operation = await fixture.Client.GetFromJsonAsync<JsonElement>("/api/operations/bootstrap-2");
        Assert.Equal("failed", operation.GetProperty("state").GetString());
    }

    [Fact]
    public async Task SessionExportWritesMarkdownUnderWorktreeSessionExportFolder()
    {
        await using var fixture = await SelectedApiFixture.CreateAsync(root, databaseExists: true);
        var created = await (await fixture.Client.PostAsync("/api/chat/session/new", null))
            .Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = created.GetProperty("header").GetProperty("sessionId").GetString()!;

        var response = await fixture.Client.PostAsJsonAsync(
            "/api/chat/session/export",
            new { sessionId });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var path = body.GetProperty("path").GetString()!;
        Assert.StartsWith(
            Path.Combine(fixture.Context.WorktreeRoot, "sessionexport") + Path.DirectorySeparatorChar,
            path);
        Assert.True(File.Exists(path));
        Assert.Contains("# Chat session export", File.ReadAllText(path));
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await fixture.Client.PostAsJsonAsync(
                "/api/chat/session/export",
                new { sessionId = SessionManager.NewSessionId() })).StatusCode);
    }

    [Fact]
    public async Task DeleteWorkbenchRemovesRootPrunesStateAndCompletesOperation()
    {
        await using var fixture = await SelectedApiFixture.CreateAsync(root, databaseExists: true);
        var workbenchRoot = fixture.Context.WorkbenchRoot;
        Assert.True(Directory.Exists(workbenchRoot));
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/api/workbenches/{fixture.Context.WorkbenchId}");
        request.Headers.Add("X-Operation-Id", "delete-1");

        var response = await fixture.Client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        Assert.Equal(["vc_remove_worktree"], fixture.VersionControl.Calls);
        Assert.False(Directory.Exists(workbenchRoot));
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await fixture.Client.GetAsync($"/api/workbenches/{fixture.Context.WorkbenchId}")).StatusCode);
        Assert.Empty(await fixture.Client.GetFromJsonAsync<JsonElement[]>("/api/workbenches") ?? []);
        var operation = await fixture.Client.GetFromJsonAsync<JsonElement>("/api/operations/delete-1");
        Assert.Equal("delete-workbench", operation.GetProperty("operationType").GetString());
        Assert.Equal("succeeded", operation.GetProperty("state").GetString());
    }

    [Fact]
    public async Task DeleteMasterWorktreeIsRejectedWithoutChangingTheWorkbench()
    {
        await using var fixture = await SelectedApiFixture.CreateAsync(root, databaseExists: true);
        using var request = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/api/workbenches/{fixture.Context.WorkbenchId}/worktrees/{fixture.Context.WorktreeId}");

        var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("MASTER_WORKTREE_PROTECTED", error.GetProperty("error").GetString());
        Assert.True(Directory.Exists(fixture.Context.WorktreeRoot));
    }

    [Fact]
    public async Task DeleteLinkedWorktreeRemovesCheckoutAndRegistration()
    {
        await using var fixture = await SelectedApiFixture.CreateAsync(root, databaseExists: true);
        fixture.AddWorktree("wt-2", "feature", @"C:\Projects\Feature.ap17");
        _ = await fixture.Client.GetFromJsonAsync<JsonElement[]>("/api/workbenches");

        var response = await fixture.Client.DeleteAsync(
            $"/api/workbenches/{fixture.Context.WorkbenchId}/worktrees/wt-2");

        response.EnsureSuccessStatusCode();
        Assert.Contains("vc_remove_worktree", fixture.VersionControl.Calls);
        Assert.False(Directory.Exists(Path.Combine(fixture.Context.WorkbenchRoot, "worktrees", "feature")));
        var workbench = await fixture.Client.GetFromJsonAsync<JsonElement>(
            $"/api/workbenches/{fixture.Context.WorkbenchId}");
        Assert.Single(workbench.GetProperty("worktrees").EnumerateArray());
        Assert.DoesNotContain(
            workbench.GetProperty("worktrees").EnumerateArray(),
            worktree => worktree.GetProperty("worktreeId").GetString() == "wt-2");
    }

    [Fact]
    public async Task DeleteUnknownWorkbenchMapsToNotFound()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseEnvironment("Testing"));
        using var client = factory.CreateClient();

        var response = await client.DeleteAsync("/api/workbenches/missing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task WorktreeVersionControlEndpointsForwardRootAndSelectedDiffRefs()
    {
        await using var fixture = await SelectedApiFixture.CreateAsync(
            Path.Combine(root, Guid.NewGuid().ToString("N")),
            databaseExists: true,
            versionControlJson: """{"Commits":[{"Sha":"head-1"}]}""");
        var prefix = $"/api/workbenches/{fixture.Context.WorkbenchId}/worktrees/{fixture.Context.WorktreeId}/vc";

        (await fixture.Client.GetAsync($"{prefix}/status")).EnsureSuccessStatusCode();
        (await fixture.Client.GetAsync($"{prefix}/log?maxCount=12&filePath=devices/PLC_1/source/A.xml")).EnsureSuccessStatusCode();
        (await fixture.Client.GetAsync($"{prefix}/diff?filePath=devices/PLC_1/source/A.xml&oldSha=old-1&newSha=new-2")).EnsureSuccessStatusCode();
        (await fixture.Client.PostAsJsonAsync($"{prefix}/commit", new
        {
            paths = new[] { "devices/PLC_1/source/A.xml" },
            message = "selected source",
        })).EnsureSuccessStatusCode();
        (await fixture.Client.GetAsync($"{prefix}/validation/head-1")).EnsureSuccessStatusCode();

        Assert.Equal(
            ["vc_status", "vc_log", "vc_diff", "vc_commit_selected", "vc_validation_get"],
            fixture.VersionControl.Calls);
        Assert.Equal(fixture.Context.WorktreeRoot, fixture.VersionControl.Arguments[0].GetProperty("repoPath").GetString());
        Assert.Equal("old-1", fixture.VersionControl.Arguments[2].GetProperty("oldSha").GetString());
        Assert.Equal("new-2", fixture.VersionControl.Arguments[2].GetProperty("newSha").GetString());
        Assert.Equal(
            "selected source",
            fixture.VersionControl.Arguments[3].GetProperty("message").GetString());
        Assert.Equal(
            "devices/PLC_1/source/A.xml",
            fixture.VersionControl.Arguments[3].GetProperty("paths")[0].GetString());
    }

    [Fact]
    public async Task ValidationEndpointReturnsNullForAnUnlabeledCommit()
    {
        await using var fixture = await SelectedApiFixture.CreateAsync(
            Path.Combine(root, Guid.NewGuid().ToString("N")),
            databaseExists: true,
            versionControlJson: "null");
        var prefix = $"/api/workbenches/{fixture.Context.WorkbenchId}/worktrees/{fixture.Context.WorktreeId}/vc";

        var response = await fixture.Client.GetAsync($"{prefix}/validation/head-1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("null", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SelectingAWorkbenchRegistersItForCoordinatorOperations()
    {
        await using var fixture = await SelectedApiFixture.CreateAsync(
            Path.Combine(root, Guid.NewGuid().ToString("N")), databaseExists: true);

        var select = await fixture.Client.PostAsync(
            $"/api/workbenches/{fixture.Context.WorkbenchId}/select", null);
        select.EnsureSuccessStatusCode();

        var response = await fixture.Client.PostAsJsonAsync(
            $"/api/workbenches/{fixture.Context.WorkbenchId}/worktrees/{fixture.Context.WorktreeId}/vc/unauthorized/discard",
            new { paths = new[] { "devices/PLC_1/source/Main.xml" }, confirm = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("vc_restore", fixture.VersionControl.Calls);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private DeviceContext Context() => new(
        "wb", "wt", "device", root, Path.Combine(root, "worktree"),
        Path.Combine(root, "worktree", "devices", "PLC"),
        Path.Combine(root, "worktree", "devices", "PLC", "source"),
        Path.Combine(root, "worktree", "devices", "PLC", "staging"),
        Path.Combine(root, "worktree", "devices", "PLC", "plc-knowledge.db"));

    private static void SeedBlockInterfaceGraph(string dbPath)
    {
        if (File.Exists(dbPath))
            File.Delete(dbPath);

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
              ('db-member:FB_LAD_SimulateCylinder_DB:btn_forward','path','btn_forward'),
              ('network:FB_LAD_SimulateCylinder:1','logicStatements','outputGoForwardPos := TRUE;'),
              ('network:FB_LAD_SimulateCylinder:1','language','LAD'),
              ('network:FB_LAD_SimulateCylinder:1','index','1'),
              ('network:Main:2','logicStatements','FB_LAD_SimulateCylinder(btn_forward := Btn_ForwardCommand);');
            INSERT INTO graph_edges VALUES
              ('edge:instance','db:FB_LAD_SimulateCylinder_DB','block:FB_LAD_SimulateCylinder','INSTANCE_OF'),
              ('edge:member','db:FB_LAD_SimulateCylinder_DB','db-member:FB_LAD_SimulateCylinder_DB:btn_forward','CONTAINS'),
              ('edge:contains-network','block:FB_LAD_SimulateCylinder','network:FB_LAD_SimulateCylinder:1','CONTAINS'),
              ('edge:call','block:Main','block:FB_LAD_SimulateCylinder','CALLS');
            INSERT INTO graph_edge_properties VALUES
              ('edge:call','networkId','network:Main:2'),
              ('edge:call','networkIndex','2'),
              ('edge:call','sourceFile','Blocks\Main [OB1].xml');
            """;
        command.ExecuteNonQuery();
        SqliteConnection.ClearAllPools();
    }

    private sealed class RecordingToolCaller(string json = "{}") : IMcpToolCaller
    {
        public List<string> Calls { get; } = [];
        public List<JsonElement> Arguments { get; } = [];
        public Task<T> CallAsync<T>(string tool, object args, CancellationToken cancellationToken = default)
        {
            Calls.Add(tool);
            Arguments.Add(JsonSerializer.SerializeToElement(args));
            if (typeof(T) == typeof(JsonElement))
            {
                if (string.Equals(json.Trim(), "null", StringComparison.Ordinal))
                    return Task.FromResult((T)(object)default(JsonElement));

                return Task.FromResult(
                    (T)(object)JsonDocument.Parse(json).RootElement.Clone());
            }

            return Task.FromResult(JsonSerializer.Deserialize<T>(json)!);
        }
    }

    /// <summary>Fakes the engineering side of the 1.2 create flow: headless origin connect,
    /// Save As into the tia/ store, managed verify, compile + checksums, export, disconnect.</summary>
    private sealed class CreateFlowEngineeringCaller(string originPath) : IMcpToolCaller
    {
        private string? managedPath;
        private int projectInfoCalls;

        public List<string> Calls { get; } = [];
        public List<JsonElement> Arguments { get; } = [];

        public Task<T> CallAsync<T>(string tool, object args, CancellationToken cancellationToken = default)
        {
            Calls.Add(tool);
            Arguments.Add(JsonSerializer.SerializeToElement(args));
            object result = tool switch
            {
                "connect" => new object(),
                "disconnect" => new object(),
                "get_project_info" => NextProjectInfo(),
                "save_project_as" => SaveProjectAs(args),
                "compile_plc" => new CompileResult { State = "success" },
                "get_plc_checksums" => new[]
                {
                    new PlcChecksumInfo { PlcName = "PLC_1", SoftwareChecksum = "checksum-PLC_1" },
                },
                "rebuild_export" => Export(args),
                _ => throw new InvalidOperationException(tool),
            };
            return Task.FromResult((T)result);
        }

        private ProjectInfo NextProjectInfo()
        {
            projectInfoCalls++;
            return new ProjectInfo
            {
                Name = "Line",
                Path = projectInfoCalls == 1 ? originPath : managedPath,
                PlcDevices = ["PLC_1"],
            };
        }

        private CoordinatorSaveProjectAsResult SaveProjectAs(object args)
        {
            var target = args.GetType().GetProperty("targetDirectory")!.GetValue(args) as string;
            Directory.CreateDirectory(target!);
            managedPath = Path.Combine(target!, "Line.ap17");
            File.WriteAllText(managedPath, "managed project placeholder");
            return new CoordinatorSaveProjectAsResult { ManagedProjectPath = managedPath };
        }

        private static SyncResult[] Export(object args)
        {
            var output = args.GetType().GetProperty("outputDir")!.GetValue(args) as string;
            Directory.CreateDirectory(Path.Combine(output!, "Blocks"));
            File.WriteAllText(Path.Combine(output!, "Blocks", "Main.xml"), "<block />");
            File.WriteAllText(
                Path.Combine(output!, "metadata.json"),
                JsonSerializer.Serialize(new
                {
                    schemaVersion = "1.0",
                    components = new[]
                    {
                        new
                        {
                            name = "Main",
                            sourcePath = "Program blocks/Main",
                            category = "OB",
                            status = "Exported",
                            exportedFile = "Blocks/Main.xml",
                        },
                    },
                }));
            return [new SyncResult { PlcName = "PLC_1", ExportRoot = output! }];
        }
    }

    /// <summary>Fakes the version-control side of the 1.2 create flow (git + svn init, baseline commits).</summary>
    private sealed class CreateFlowVersionControlCaller : IMcpToolCaller
    {
        public List<string> Calls { get; } = [];

        public Task<T> CallAsync<T>(string tool, object args, CancellationToken cancellationToken = default)
        {
            Calls.Add(tool);
            object result = tool switch
            {
                "svn_init_shared" => new CoordinatorSvnInitResult
                {
                    RepositoryPath = "repository.svn",
                    RepositoryUri = "file:///repository.svn/",
                },
                "svn_commit" => new CoordinatorSvnCommitResult { Committed = true, Revision = 1 },
                "vc_commit_selected" => new WorkbenchCommitResult(
                    "baseline-sha",
                    "Initial PLC source baseline",
                    new[] { "engineering-state/revision.json" }),
                _ => new object(),
            };
            return Task.FromResult((T)result);
        }
    }

    private sealed class SelectedApiFixture : IAsyncDisposable
    {
        private readonly WebApplicationFactory<Program> factory;
        private readonly DeviceContext context;
        private readonly WorkbenchCatalog catalog;

        private SelectedApiFixture(
            WebApplicationFactory<Program> factory,
            HttpClient client,
            ThrowingToolCaller engineering,
            RecordingToolCaller versionControl,
            DeviceContext context,
            WorkbenchCatalog catalog)
        {
            this.factory = factory;
            Client = client;
            Engineering = engineering;
            VersionControl = versionControl;
            this.context = context;
            this.catalog = catalog;
        }

        public HttpClient Client { get; }
        public ThrowingToolCaller Engineering { get; }
        public RecordingToolCaller VersionControl { get; }
        public DeviceContext Context => context;
        public string DeviceId => context.DeviceId;
        public string DeviceRoute =>
            $"/api/workbenches/{context.WorkbenchId}/worktrees/{context.WorktreeId}/devices/{context.DeviceId}";

        public static async Task<SelectedApiFixture> CreateAsync(
            string fixtureRoot,
            bool databaseExists,
            bool stale = false,
            bool baselineStale = false,
            bool engineeringOffline = true,
            string? sourceProjectPath = null,
            Action<string, string>? stageExport = null,
            string? versionControlJson = null)
        {
            var store = new AtomicJsonStore();
            var catalog = new WorkbenchCatalog(store, fixtureRoot);
            var workbench = catalog.Create("Line", null);
            const string worktreeId = "wt-1";
            const string deviceId = "dev-1";
            workbench = catalog.RegisterWorktree(
                workbench,
                new WorkbenchWorktreeRegistration(worktreeId, "master", "master", "master"));
            var worktreeRoot = Path.Combine(workbench.RootPath, "worktrees", "master");
            var deviceRoot = Path.Combine(worktreeRoot, "devices", "PLC_1");
            var context = new DeviceContext(
                workbench.WorkbenchId,
                worktreeId,
                deviceId,
                workbench.RootPath,
                worktreeRoot,
                deviceRoot,
                Path.Combine(deviceRoot, "source"),
                Path.Combine(deviceRoot, "staging"),
                Path.Combine(deviceRoot, "plc-knowledge.db"));
            Directory.CreateDirectory(context.SourceRoot);
            Directory.CreateDirectory(context.StagingRoot);
            store.Write(
                Path.Combine(worktreeRoot, "worktree.json"),
                new WorktreeMetadata(
                    "1.0", worktreeId, workbench.WorkbenchId, "master", "master",
                    DateTimeOffset.UtcNow.ToString("O"), null, null, sourceProjectPath, [deviceId], null));
            store.Write(
                Path.Combine(deviceRoot, "device.json"),
                new DeviceMetadata(
                    "1.0", deviceId, worktreeId, "PLC_1", "PLC:1", null, null, null,
                    new KnowledgeState(stale, new Dictionary<string, string>(), "2026-07-29T08:00:00Z", baselineStale),
                    []));
            if (databaseExists)
                await File.WriteAllBytesAsync(context.KnowledgeDbPath, [1]);

            var engineering = new ThrowingToolCaller(engineeringOffline, stageExport);
            var versionControl = new RecordingToolCaller(versionControlJson ?? """
                {"Sha":"baseline-1","Message":"Initial PLC source baseline","Files":["devices/PLC_1/source/Blocks/Main.xml"]}
                """);
            var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host =>
            {
                host.UseEnvironment("Testing");
                host.ConfigureServices(services =>
                {
                    services.RemoveAll<WorkbenchCatalog>();
                    services.RemoveAll<AtomicJsonStore>();
                    services.RemoveAll<WorkbenchApiState>();
                    services.RemoveAll<ApiMcpGateway>();
                    services.RemoveAll<WorkbenchCoordinator>();
                    services.AddSingleton(store);
                    services.AddSingleton(catalog);
                    services.AddSingleton<WorkbenchApiState>();
                    services.AddSingleton(new ApiMcpGateway(
                        engineering,
                        new RecordingToolCaller(),
                        versionControl,
                        new RecordingToolCaller()));
                    services.AddSingleton(sp => new WorkbenchCoordinator(
                        engineering,
                        new RecordingToolCaller(),
                        versionControl,
                        catalog,
                        store,
                        sp.GetRequiredService<DeviceReconciler>(),
                        sp.GetRequiredService<DeviceSourceResolver>(),
                        sp.GetRequiredService<DeviceOperationLock>()));
                });
            });
            var client = factory.CreateClient();
            var select = await client.PostAsync(
                $"/api/workbenches/{workbench.WorkbenchId}/worktrees/{worktreeId}/devices/{deviceId}/select",
                null);
            select.EnsureSuccessStatusCode();
            return new SelectedApiFixture(factory, client, engineering, versionControl, context, catalog);
        }

        public void WriteComparisonBaseline(string? fingerprints)
        {
            Directory.CreateDirectory(Path.Combine(context.SourceRoot, "Blocks"));
            File.WriteAllText(Path.Combine(context.SourceRoot, "Blocks", "Main.xml"), "<stored/>");
            WriteComparisonManifest(context.SourceRoot, fingerprints);
        }

        public string ReadBaseline(string relativePath) =>
            File.ReadAllText(Path.Combine(
                context.SourceRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));

        public bool BaselineExists(string relativePath) =>
            File.Exists(Path.Combine(
                context.SourceRoot,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));

        public string?[] BaselineComponentIds()
        {
            using var manifest = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(context.SourceRoot, "metadata.json")));
            return manifest.RootElement.GetProperty("components")
                .EnumerateArray()
                .Select(component => component.GetProperty("id").GetString())
                .ToArray();
        }

        public void WriteManifest()
        {
            var blockPath = Path.Combine(context.SourceRoot, "Blocks", "Main [OB1].xml");
            Directory.CreateDirectory(Path.GetDirectoryName(blockPath)!);
            File.WriteAllText(
                blockPath,
                """
                <Document>
                  <SW.Blocks.OB>
                    <AttributeList>
                      <Name>Main</Name>
                      <Number>1</Number>
                      <ProgrammingLanguage>LAD</ProgrammingLanguage>
                    </AttributeList>
                  </SW.Blocks.OB>
                </Document>
                """);
            File.WriteAllText(
                Path.Combine(context.SourceRoot, "metadata.json"),
                """
                {
                  "schemaVersion": "1.0",
                  "components": [
                    {
                      "id": "ob-main",
                      "name": "Main",
                      "category": "OB",
                      "status": "Exported",
                      "exportedFile": "Blocks/Main [OB1].xml",
                      "sourcePath": "Area/Main",
                      "number": 1,
                      "programmingLanguage": "LAD"
                    }
                  ]
                }
                """);
        }

        public string[] PersistentArtifactHashes() =>
            Directory.EnumerateFiles(context.DeviceRoot, "*", SearchOption.AllDirectories)
                .Where(path => !path.StartsWith(
                    context.StagingRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(path =>
                    $"{Path.GetRelativePath(context.DeviceRoot, path).Replace('\\', '/')}:" +
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                        File.ReadAllBytes(path))))
                .ToArray();

        public (string SelectRoute, string DeviceRoute) AddWorktree(
            string worktreeId,
            string relativePath,
            string sourceProjectPath)
        {
            var workbench = catalog.Load(context.WorkbenchRoot);
            workbench = catalog.RegisterWorktree(
                workbench,
                new WorkbenchWorktreeRegistration(worktreeId, relativePath, relativePath, relativePath));
            var worktreeRoot = Path.Combine(workbench.RootPath, "worktrees", relativePath);
            var deviceRoot = Path.Combine(worktreeRoot, "devices", "PLC_1");
            Directory.CreateDirectory(deviceRoot);
            var store = new AtomicJsonStore();
            store.Write(
                Path.Combine(worktreeRoot, "worktree.json"),
                new WorktreeMetadata(
                    "1.0", worktreeId, workbench.WorkbenchId, relativePath, relativePath,
                    DateTimeOffset.UtcNow.ToString("O"), null, null, sourceProjectPath,
                    [context.DeviceId], null));
            store.Write(
                Path.Combine(deviceRoot, "device.json"),
                new DeviceMetadata(
                    "1.0", context.DeviceId, worktreeId, "PLC_1", "PLC:1", null, null, null,
                    new KnowledgeState(true, new Dictionary<string, string>(), null), []));
            var baseRoute =
                $"/api/workbenches/{workbench.WorkbenchId}/worktrees/{worktreeId}/devices/{context.DeviceId}";
            return ($"{baseRoute}/select", baseRoute);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await factory.DisposeAsync();
        }
    }

    private sealed class ThrowingToolCaller(
        bool throwOnCall = true,
        Action<string, string>? stageExport = null) : IMcpToolCaller
    {
        public List<string> Calls { get; } = [];
        public Dictionary<string, JsonElement> Arguments { get; } = [];

        public Task<T> CallAsync<T>(
            string tool,
            object args,
            CancellationToken cancellationToken = default)
        {
            Calls.Add(tool);
            Arguments[tool] = JsonSerializer.SerializeToElement(args);
            if (tool == "rebuild_export" && stageExport is not null)
            {
                var arguments = Arguments[tool];
                var outputDir = arguments.GetProperty("outputDir").GetString()!;
                var plcName = arguments.GetProperty("plcName").GetString()!;
                stageExport(outputDir, plcName);
                return Task.FromResult((T)(object)new[]
                {
                    new SyncResult { PlcName = plcName, ExportRoot = outputDir, Status = "updated" },
                });
            }
            if (throwOnCall)
                throw new InvalidOperationException("Engineering is offline.");
            return Task.FromResult((T)(object)new object());
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan value) => now += value;
    }
}
