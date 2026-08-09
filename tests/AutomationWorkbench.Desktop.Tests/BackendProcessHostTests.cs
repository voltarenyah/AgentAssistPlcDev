using System.Diagnostics;
using System.Net;
using System.Net.Http;
using AutomationWorkbench.Desktop;
using Xunit;

public sealed class BackendProcessHostTests
{
    [Fact]
    public void RuntimePathsUseLoopbackApiHostAndUserDataLocations()
    {
        var paths = RuntimePaths.Create("C:\\Automation Workbench", 5255);

        Assert.Equal("C:\\Automation Workbench", paths.InstallRoot);
        Assert.Equal("C:\\Automation Workbench\\ApiHost.exe", paths.ApiHostPath);
        Assert.Equal("http://127.0.0.1:5255", paths.BaseUrl);
        Assert.EndsWith("AutomationWorkbench\\logs", paths.LogDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("AutomationWorkbench\\WebView2", paths.WebViewUserDataPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StartInfoStartsBackendWithoutShellOrConsole()
    {
        var paths = RuntimePaths.Create("C:\\Automation Workbench", 5239);
        var info = BackendProcessHost.CreateStartInfo(paths, "test-shutdown-token");

        Assert.Equal(paths.ApiHostPath, info.FileName);
        Assert.Equal(paths.InstallRoot, info.WorkingDirectory);
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.Equal(ProcessWindowStyle.Hidden, info.WindowStyle);
        Assert.True(info.RedirectStandardOutput);
        Assert.True(info.RedirectStandardError);
        Assert.Equal(
            new[]
            {
                "--Application:Port", "5239",
                "--Application:OpenBrowserOnStart", "false",
                "--Application:ShutdownToken", "test-shutdown-token",
            },
            info.ArgumentList);
    }

    [Fact]
    public void AppAssistantRuntimePathsAreOptInAndUseTheServicePort()
    {
        var paths = RuntimePaths.Create(
            "C:\\Automation Workbench",
            5239,
            appAssistantEnabled: true,
            appAssistantPort: 8791,
            appAssistantCommand: "python.exe",
            appAssistantWorkingDirectory: "C:\\Automation Workbench\\agent-service");

        Assert.True(paths.AppAssistantEnabled);
        Assert.Equal("http://127.0.0.1:8791", paths.AppAssistantBaseUrl);
        Assert.Equal("python.exe", paths.AppAssistantCommand);
        Assert.Equal("C:\\Automation Workbench\\agent-service", paths.AppAssistantWorkingDirectory);
    }

    [Fact]
    public void AssistantStartInfoUsesAHiddenLoopbackUvicornProcess()
    {
        var paths = RuntimePaths.Create("C:\\Automation Workbench", appAssistantEnabled: true, appAssistantPort: 8791);

        var info = BackendProcessHost.CreateAppAssistantStartInfo(paths);

        Assert.Equal("py", info.FileName);
        Assert.Equal(paths.EffectiveAppAssistantWorkingDirectory, info.WorkingDirectory);
        Assert.Equal(paths.BaseUrl, info.Environment["APP_ASSISTANT_APIHOST_URL"]);
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.Equal(new[]
        {
            "-3.13", "-m", "uvicorn", "app_assistant.server:app",
            "--host", "127.0.0.1", "--port", "8791",
        }, info.ArgumentList);
        Assert.Equal(paths.AppAssistantDataDirectory, info.Environment["APP_ASSISTANT_DATA_DIR"]);
    }

    [Fact]
    public async Task StartsAndStopsTheLivePythonSidecarThroughTheDesktopHost()
    {
        var root = Path.Combine(Path.GetTempPath(), "assistant-desktop-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var apiHostPath = Path.Combine(root, "ApiHost.exe");
        File.WriteAllText(apiHostPath, "test placeholder");
        var serviceDirectory = FindServiceDirectory();
        var paths = new RuntimePaths(
            root,
            apiHostPath,
            "http://127.0.0.1:5239",
            Path.Combine(root, "logs"),
            AppAssistantEnabled: true,
            AppAssistantCommand: "py",
            AppAssistantWorkingDirectory: serviceDirectory,
            AppAssistantPort: GetFreePort());
        using var http = new HttpClient(new ApiHostHealthHandler());
        await using var host = new BackendProcessHost(paths, http, startInfo =>
        {
            var process = startInfo.ArgumentList.Contains("uvicorn")
                ? Process.Start(startInfo)
                : Process.Start(new ProcessStartInfo
                {
                    FileName = "py",
                    WorkingDirectory = root,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    ArgumentList = { "-3.13", "-c", "import time; time.sleep(120)" },
                });
            return process!;
        });

        try
        {
            await host.StartAsync();
            Assert.True(host.IsRunning);
            Assert.True(host.IsAppAssistantRunning);
        }
        finally
        {
            await host.StopAsync();
            Directory.Delete(root, recursive: true);
        }

        Assert.False(host.IsAppAssistantRunning);
    }

    private static string FindServiceDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "agent-service", "pyproject.toml");
            if (File.Exists(candidate))
                return Path.GetDirectoryName(candidate)!;
        }
        throw new DirectoryNotFoundException("Could not locate the agent-service directory.");
    }

    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class ApiHostHealthHandler : HttpMessageHandler
    {
        private readonly HttpClient fallback = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/api/status")
                return new HttpResponseMessage(HttpStatusCode.OK);
            return await fallback.GetAsync(request.RequestUri!, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                fallback.Dispose();
            base.Dispose(disposing);
        }
    }
}
