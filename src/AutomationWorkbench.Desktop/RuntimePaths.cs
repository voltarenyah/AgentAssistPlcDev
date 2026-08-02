namespace AutomationWorkbench.Desktop;

public sealed record RuntimePaths(
    string InstallRoot,
    string ApiHostPath,
    string BaseUrl,
    string LogDirectory)
{
    public string BackendLogPath => Path.Combine(LogDirectory, "backend.log");

    public string WebViewUserDataPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AutomationWorkbench", "WebView2");

    public static RuntimePaths Create(string? installRoot = null, int port = 5239)
    {
        var root = Path.GetFullPath(installRoot ?? AppContext.BaseDirectory);
        var logs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AutomationWorkbench", "logs");
        return new(
            root,
            Path.Combine(root, "ApiHost.exe"),
            $"http://127.0.0.1:{port}",
            logs);
    }
}
