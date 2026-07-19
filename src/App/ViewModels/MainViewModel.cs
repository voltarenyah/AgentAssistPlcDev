using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Agent;
using Agent.Chat;
using Agent.Mcp;
using Agent.Workflows;
using App.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Contracts.Engineering;

namespace App.ViewModels;

/// <summary>
/// UI-0/1 view model (buildnote/plan/app.md §3): server startup + env check in the status strip,
/// TIA session connect, and the Read Project Context button wired to <see cref="ReadProjectContextWorkflow"/>.
/// </summary>
public partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private McpHost? host;
    private CancellationTokenSource? runCancellation;
    private string? connectedProjectName;
    private string? knowledgeDbPath;

    public MainViewModel()
    {
        Chat = new ChatViewModel(BuildRuntimeContext);
    }

    [ObservableProperty]
    private string environmentStatus = "—";

    [ObservableProperty]
    private string serverStatus = "Starting servers…";

    [ObservableProperty]
    private string connectionStatus = "Not connected";

    [ObservableProperty]
    private string ingestSummary = "—";

    [ObservableProperty]
    private string dbPath = "—";

    [ObservableProperty]
    private string projectPathInput = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private SessionInfo? selectedSession;

    public ObservableCollection<SessionInfo> Sessions { get; } = new();

    public ObservableCollection<string> LogLines { get; } = new();

    public ObservableCollection<KeyValuePair<string, int>> NodeKinds { get; } = new();

    public ObservableCollection<string> Warnings { get; } = new();

    /// <summary>DeepSeek chat panel (configured once the servers are up and the tool catalog is built).</summary>
    public ChatViewModel Chat { get; }

    /// <summary>Called once from MainWindow.Loaded: resolve settings, start servers, run the env check.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            ReportLog($"Log file: {AppLog.Initialize()}");
            var settings = AppSettings.Load();
            ReportLog($"Engineering server: {settings.EngineeringServerPath}");
            ReportLog($"Knowledge server: {settings.KnowledgeServerPath}");
            host = new McpHost(settings.EngineeringServerPath, settings.KnowledgeServerPath);
            host.ServerLog += ReportLog;
            await host.StartAsync();
            ServerStatus = "Servers running";

            try
            {
                var catalog = await McpToolCatalog.BuildAsync(host);
                ReportLog($"agent: {catalog.Tools.Count} MCP tools exposed to DeepSeek (import_block excluded)");
                Chat.Configure(settings, catalog);
            }
            catch (Exception ex)
            {
                ReportLog($"ERROR building agent tool catalog (chat disabled): {ex.Message}");
            }

            var env = await host.Engineering.CallAsync<EnvCheckResult>("check_environment", new { });
            EnvironmentStatus = env.UserInOpennessGroup
                ? $"TIA {env.TiaPortalVersion ?? "?"} · Openness group OK"
                : "User NOT in 'Siemens TIA Openness' group (re-login if persistent)";

            await RefreshSessionsAsync();
        }
        catch (Exception ex)
        {
            ServerStatus = "Startup failed";
            ReportLog($"ERROR: {ex}");
        }
    }

    [RelayCommand]
    private async Task RefreshSessionsAsync()
    {
        if (host == null)
        {
            return;
        }

        try
        {
            var sessions = await host.Engineering.CallAsync<SessionInfo[]>("list_sessions", new { });
            Sessions.Clear();
            foreach (var session in sessions)
            {
                Sessions.Add(session);
            }

            ReportLog($"list_sessions: {sessions.Length} TIA process(es)");
            foreach (var session in sessions)
            {
                ReportLog($"  session {session.Id}: mode={session.Mode}, project={session.ProjectPath ?? "<none>"}");
            }
        }
        catch (Exception ex)
        {
            ReportLog($"ERROR: {ex}");
        }
    }

    [RelayCommand]
    private async Task ConnectSelectedAsync()
    {
        if (host == null || SelectedSession == null)
        {
            return;
        }

        ReportLog($"Attaching to TIA process {SelectedSession.Id} (mode: {SelectedSession.Mode}, project: {SelectedSession.ProjectPath ?? "<none reported>"})");
        if (string.IsNullOrWhiteSpace(SelectedSession.ProjectPath))
        {
            ReportLog("WARNING: this TIA process reports no open project in the session list. " +
                "If your project is open in another TIA version/process, select that row instead — the attach will fail with PROJECT_NOT_FOUND otherwise.");
        }

        await ConnectAsync(new { sessionId = (int?)SelectedSession.Id });
    }

    [RelayCommand]
    private async Task ConnectHeadlessAsync()
    {
        if (host == null || string.IsNullOrWhiteSpace(ProjectPathInput))
        {
            return;
        }

        await ConnectAsync(new { projectPath = (string?)ProjectPathInput });
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        if (host == null)
        {
            return;
        }

        try
        {
            await host.Engineering.CallAsync<object>("disconnect", new { });
            connectedProjectName = null;
            knowledgeDbPath = null;
            ConnectionStatus = "Not connected";
            ReportLog("Disconnected.");
        }
        catch (Exception ex)
        {
            ReportLog($"ERROR: {ex}");
        }
    }

    [RelayCommand]
    private async Task ReadProjectContextAsync()
    {
        if (host == null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        runCancellation = new CancellationTokenSource();
        try
        {
            var progress = new Progress<string>(ReportLog);
            var workflow = new ReadProjectContextWorkflow(host.Engineering, host.Knowledge, progress);
            var result = await workflow.RunAsync(runCancellation.Token);

            DbPath = result.DbPath;
            connectedProjectName = result.ProjectName;
            knowledgeDbPath = result.DbPath;
            IngestSummary =
                $"{result.ProjectName}: {result.BlocksExported} blocks, {result.TagTablesExported} tag tables, {result.UdtsExported} UDTs → " +
                $"{result.Ingest.Nodes} nodes / {result.Ingest.Edges} edges in {result.Ingest.DurationMs} ms";
            NodeKinds.Clear();
            foreach (var pair in result.Ingest.ByKind)
            {
                NodeKinds.Add(pair);
            }

            Warnings.Clear();
            foreach (var warning in result.Ingest.Warnings)
            {
                Warnings.Add(warning);
            }
        }
        catch (OperationCanceledException)
        {
            ReportLog("Cancelled.");
        }
        catch (ToolCallException ex)
        {
            ReportLog($"ERROR [{ex.Code}] {ex.Message}" + (ex.Remediation != null ? $" — {ex.Remediation}" : string.Empty));
        }
        catch (Exception ex)
        {
            ReportLog($"ERROR: {ex}");
        }
        finally
        {
            IsBusy = false;
            runCancellation.Dispose();
            runCancellation = null;
        }
    }

    [RelayCommand]
    private void CancelRun()
    {
        runCancellation?.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        if (host != null)
        {
            await host.DisposeAsync();
        }
    }

    private async Task ConnectAsync(object args)
    {
        try
        {
            var info = await host!.Engineering.CallAsync<ConnectionInfo>("connect", args);
            connectedProjectName = info.ProjectName;
            RefreshKnowledgeDbPath();
            ConnectionStatus = info.ProjectName != null
                ? $"Connected: {info.ProjectName} ({(info.Attached ? "attached" : "headless")})"
                : $"Connected ({(info.Attached ? "attached" : "headless")}, no project open?)";
            ReportLog($"connect: {ConnectionStatus}");
        }
        catch (ToolCallException ex)
        {
            ReportLog($"ERROR [{ex.Code}] {ex.Message}" + (ex.Remediation != null ? $" — {ex.Remediation}" : string.Empty));
        }
        catch (Exception ex)
        {
            ReportLog($"ERROR: {ex}");
        }
    }

    /// <summary>Points the chat context at the connected project's knowledge db when it already exists on disk.</summary>
    private void RefreshKnowledgeDbPath()
    {
        if (connectedProjectName == null)
        {
            knowledgeDbPath = null;
            return;
        }

        var candidate = AssistantPaths.ResolveKnowledgeDbPath(connectedProjectName);
        knowledgeDbPath = File.Exists(candidate) ? candidate : null;
        if (knowledgeDbPath != null)
        {
            ReportLog($"Knowledge db found for '{connectedProjectName}': {knowledgeDbPath}");
        }
    }

    /// <summary>Runtime context block for the agent's system prompt, rebuilt before every turn.</summary>
    private string BuildRuntimeContext()
    {
        var lines = new List<string> { $"TIA connection: {ConnectionStatus}" };
        if (connectedProjectName != null)
        {
            lines.Add($"Project: {connectedProjectName}");
            lines.Add($"Export root: {AssistantPaths.ResolveExportRoot(connectedProjectName)}");
        }

        lines.Add(knowledgeDbPath != null
            ? $"Knowledge db (use this dbPath for the knowledge tools): {knowledgeDbPath}"
            : "Knowledge db: none known for this session — the user must press \"Read Project Context\" first, or attach to a project whose knowledge base already exists on disk.");
        return string.Join('\n', lines);
    }

    private void ReportLog(string line)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => ReportLog(line));
            return;
        }

        LogLines.Add($"{DateTime.Now:HH:mm:ss} {line}");
        AppLog.Write($"{DateTime.Now:HH:mm:ss} {line}");
    }
}
