using System.Diagnostics;
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
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.Equal(new[]
        {
            "-3.13", "-m", "uvicorn", "app_assistant.server:app",
            "--host", "127.0.0.1", "--port", "8791",
        }, info.ArgumentList);
    }
}
