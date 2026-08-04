using AutomationWorkbench.Desktop;
using Microsoft.Web.WebView2.WinForms;
using Xunit;

namespace AutomationWorkbench.Desktop.Tests;

public sealed class MainWindowTests
{
    [Fact]
    public void StartsMaximizedToFitTheWorkingArea()
    {
        using var window = CreateWindow();

        Assert.Equal(FormWindowState.Maximized, window.WindowState);
    }

    [Fact]
    public void ShowsStartupSurfaceBeforeWebViewIsReady()
    {
        using var window = CreateWindow();

        var browser = Assert.Single(window.Controls.OfType<WebView2>());
        Assert.False(browser.Visible);
        var message = Assert.Single(window.Controls.Find("startupMessage", searchAllChildren: true));
        Assert.Contains("Starting Automation Workbench", message.Text, StringComparison.Ordinal);
    }

    private static MainWindow CreateWindow()
    {
        var paths = RuntimePaths.Create(Path.GetTempPath());
        return new MainWindow(new BackendProcessHost(paths), paths);
    }
}
