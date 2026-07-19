using System.Collections.ObjectModel;
using System.Windows;
using App.Logging;
using App.Mcp;
using App.Workflows;
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

    /// <summary>Called once from MainWindow.Loaded: resolve settings, start servers, run the env check.</summary>
    public async Task InitializeAsync()
    {
        try
        {
            ReportLog($"Log file: {AppLog.Initialize()}");
            var settings = AppSettings.Load();
            ReportLog($"Engineering server: {settings.EngineeringServerPath}");
            ReportLog($"Knowledge server: {settings.KnowledgeServerPath}");
            host = new McpHost(settings);
            host.ServerLog += ReportLog;
            await host.StartAsync();
            ServerStatus = "Servers running";

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
