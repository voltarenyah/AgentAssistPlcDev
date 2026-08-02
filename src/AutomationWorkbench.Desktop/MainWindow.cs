using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AutomationWorkbench.Desktop;

public sealed class MainWindow : Form
{
    private readonly BackendProcessHost backend;
    private readonly RuntimePaths paths;
    private readonly WebView2 browser = new() { Dock = DockStyle.Fill };
    private bool startupStarted;
    private bool shutdownStarted;

    public MainWindow(BackendProcessHost backend, RuntimePaths paths)
    {
        this.backend = backend;
        this.paths = paths;
        Text = "Automation Workbench";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1440, 900);
        MinimumSize = new Size(960, 640);
        ShowInTaskbar = true;
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "AutomationWorkbench.ico")))
            Icon = new Icon(Path.Combine(AppContext.BaseDirectory, "AutomationWorkbench.ico"));

        Controls.Add(browser);
        browser.NavigationStarting += OnNavigationStarting;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (startupStarted)
            return;
        startupStarted = true;
        _ = StartApplicationAsync();
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (shutdownStarted)
        {
            base.OnFormClosing(e);
            return;
        }

        e.Cancel = true;
        shutdownStarted = true;
        try
        {
            await backend.StopAsync();
        }
        finally
        {
            Close();
        }
    }

    private async Task StartApplicationAsync()
    {
        try
        {
            await backend.StartAsync();
            Directory.CreateDirectory(paths.WebViewUserDataPath);
            var runtimeVersion = CoreWebView2Environment.GetAvailableBrowserVersionString();
            if (string.IsNullOrWhiteSpace(runtimeVersion))
                throw new InvalidOperationException("Microsoft Edge WebView2 Runtime was not found.");

            var environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: paths.WebViewUserDataPath);
            await browser.EnsureCoreWebView2Async(environment);
            browser.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
            browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            browser.CoreWebView2.Settings.AreDevToolsEnabled = false;
            browser.CoreWebView2.Navigate(backend.BaseUrl + "/");
        }
        catch (BackendStartupException exception)
        {
            ShowStartupError(
                "Automation Workbench could not start its backend.",
                exception.Message);
        }
        catch (COMException exception)
        {
            ShowStartupError(
                "Microsoft Edge WebView2 Runtime is required.",
                $"Install the Evergreen WebView2 Runtime, then start Automation Workbench again.\n\n{exception.Message}");
        }
        catch (Exception exception)
        {
            ShowStartupError(
                "Automation Workbench could not open its application window.",
                exception.Message);
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (IsAllowedApplicationUrl(e.Uri))
            return;

        e.Cancel = true;
        OpenExternalUrl(e.Uri);
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        OpenExternalUrl(e.Uri);
    }

    private bool IsAllowedApplicationUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        var baseUri = new Uri(backend.BaseUrl + "/");
        return string.Equals(uri.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(uri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase)
            && uri.Port == baseUri.Port;
    }

    private static void OpenExternalUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = uri.ToString(),
            UseShellExecute = true,
        });
    }

    private void ShowStartupError(string title, string detail)
    {
        MessageBox.Show(
            this,
            $"{detail}\n\nBackend log: {paths.BackendLogPath}",
            title,
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
        Close();
    }
}
