namespace AutomationWorkbench.Desktop;

public sealed record RuntimePaths(
    string InstallRoot,
    string ApiHostPath,
    string BaseUrl,
    string LogDirectory,
    bool AppAssistantEnabled = false,
    string AppAssistantCommand = "py",
    string? AppAssistantWorkingDirectory = null,
    int AppAssistantPort = 8787,
    string? AppAssistantDataDirectoryOverride = null)
{
    public string BackendLogPath => Path.Combine(LogDirectory, "backend.log");

    public string WebViewUserDataPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AutomationWorkbench", "WebView2");

    public string AppAssistantBaseUrl => $"http://127.0.0.1:{AppAssistantPort}";

    public string EffectiveAppAssistantWorkingDirectory =>
        AppAssistantWorkingDirectory ?? Path.Combine(InstallRoot, "agent-service");

    public string AppAssistantDataDirectory =>
        AppAssistantDataDirectoryOverride
        ?? Environment.GetEnvironmentVariable("AUTOMATION_WORKBENCH_APP_ASSISTANT_DATA_DIR")
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AutomationWorkbench", "AppAssistant");

    public static RuntimePaths Create(
        string? installRoot = null,
        int port = 5239,
        bool? appAssistantEnabled = null,
        int appAssistantPort = 8787,
        string? appAssistantCommand = null,
        string? appAssistantWorkingDirectory = null,
        string? appAssistantDataDirectory = null)
    {
        var root = Path.GetFullPath(installRoot ?? AppContext.BaseDirectory);
        var logs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AutomationWorkbench", "logs");
        return new(
            root,
            Path.Combine(root, "ApiHost.exe"),
            $"http://127.0.0.1:{port}",
            logs,
            appAssistantEnabled ?? IsEnabled(Environment.GetEnvironmentVariable("AUTOMATION_WORKBENCH_APP_ASSISTANT_ENABLED")),
            appAssistantCommand ?? Environment.GetEnvironmentVariable("AUTOMATION_WORKBENCH_APP_ASSISTANT_COMMAND") ?? "py",
            appAssistantWorkingDirectory
                ?? Environment.GetEnvironmentVariable("AUTOMATION_WORKBENCH_APP_ASSISTANT_WORKDIR"),
            appAssistantPort,
            appAssistantDataDirectory
                ?? Environment.GetEnvironmentVariable("AUTOMATION_WORKBENCH_APP_ASSISTANT_DATA_DIR"));
    }

    private static bool IsEnabled(string? value) =>
        string.Equals(value, "1", StringComparison.Ordinal)
        || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}
