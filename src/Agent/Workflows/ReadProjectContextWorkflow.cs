using System.Diagnostics;
using Agent.Mcp;
using Contracts.Engineering;
using Contracts.Knowledge;

namespace Agent.Workflows;

/// <summary>
/// The confirmed incremental context sync (buildnote/plan/export-sync.md §UI): one sync_export
/// call (checksum gate + fingerprint/hash diff, full export when no baseline exists), then
/// ingest_source only when the sync actually changed files or the knowledge db is missing.
/// Requires an already-connected engineering session (connect/disconnect is UI session state);
/// the connection is validated via get_project_info.
/// </summary>
public sealed class ReadProjectContextWorkflow
{
    private readonly IMcpToolCaller engineering;
    private readonly IMcpToolCaller knowledge;
    private readonly IProgress<string>? progress;
    private readonly Func<string, bool> fileExists;

    public ReadProjectContextWorkflow(
        IMcpToolCaller engineering,
        IMcpToolCaller knowledge,
        IProgress<string>? progress = null,
        Func<string, bool>? fileExists = null)
    {
        this.engineering = engineering;
        this.knowledge = knowledge;
        this.progress = progress;
        this.fileExists = fileExists ?? File.Exists;
    }

    public async Task<ReadProjectContextResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var info = await Timed("Reading project info", () =>
            engineering.CallAsync<ProjectInfo>("get_project_info", new { }, cancellationToken));
        var projectName = string.IsNullOrWhiteSpace(info.Name) ? "unknown" : info.Name!;
        var exportRoot = AssistantPaths.ResolveExportRoot(projectName);
        Log($"Export root: {exportRoot}");

        cancellationToken.ThrowIfCancellationRequested();
        var sync = await Timed("Syncing exports", () =>
            engineering.CallAsync<SyncResult[]>("sync_export", new { outputDir = exportRoot }, cancellationToken));

        // Fail fast with the real cause: a first sync (no baseline) that produced nothing means no
        // PLC software was found or every export failed — running ingest would only surface a
        // confusing EXPORT_ROOT_NOT_FOUND later.
        foreach (var plc in sync)
        {
            if (!plc.BaselineExisted && plc.Added.Length == 0)
            {
                var detail = plc.Failed.Length > 0
                    ? $"every export failed (first: {plc.Failed[0].Reason})"
                    : "no PLC software was found";
                throw new InvalidOperationException(
                    $"sync_export produced 0 components for PLC '{plc.PlcName}' in project '{projectName}' — {detail}. " +
                    "Check the project has a PLC with blocks (list_blocks) before syncing context.");
            }
        }

        var added = sync.Sum(plc => plc.Added.Length);
        var changed = sync.Sum(plc => plc.Changed.Length);
        var touched = sync.Sum(plc => plc.Touched.Length);
        var removed = sync.Sum(plc => plc.Removed.Length);
        var failed = sync.Sum(plc => plc.Failed.Length);
        Log($"Sync: {added} added, {changed} changed, {touched} touched, {removed} removed, {failed} failed");

        var dbPath = AssistantPaths.ResolveKnowledgeDbPath(projectName);
        var contentChanged = added + changed + removed > 0;
        var dbMissing = !fileExists(dbPath);

        IngestResult? ingest = null;
        if (contentChanged || dbMissing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ingest = await Timed("Building knowledge base", () =>
                knowledge.CallAsync<IngestResult>("ingest_source", new { exportRoot }, cancellationToken));
            Log($"Knowledge base: {ingest.Nodes} nodes, {ingest.Edges} edges → {ingest.DbPath}");
        }
        else
        {
            Log("Knowledge base already up to date — ingest skipped.");
        }

        return new ReadProjectContextResult
        {
            ProjectName = projectName,
            ExportRoot = exportRoot,
            DbPath = ingest?.DbPath ?? dbPath,
            Sync = sync,
            Ingest = ingest,
            UpToDate = !contentChanged && !dbMissing,
        };
    }

    private async Task<T> Timed<T>(string step, Func<Task<T>> action)
    {
        Log($"{step}…");
        var stopwatch = Stopwatch.StartNew();
        var result = await action();
        Log($"{step} — done in {stopwatch.ElapsedMilliseconds / 1000.0:0.0}s");
        return result;
    }

    private void Log(string message) => progress?.Report(message);
}
