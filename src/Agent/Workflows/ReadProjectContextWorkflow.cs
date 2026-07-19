using System.Diagnostics;
using Agent.Mcp;
using Contracts.Engineering;
using Contracts.Knowledge;

namespace Agent.Workflows;

/// <summary>
/// The Read Project Context orchestration (buildnote/plan/app.md §4): one workflow, two triggers —
/// the UI button now, the AI agent in step 6. Requires an already-connected engineering session
/// (connect/disconnect is UI session state); the connection is validated via get_project_info.
/// </summary>
public sealed class ReadProjectContextWorkflow
{
    private readonly IMcpToolCaller engineering;
    private readonly IMcpToolCaller knowledge;
    private readonly IProgress<string>? progress;

    public ReadProjectContextWorkflow(IMcpToolCaller engineering, IMcpToolCaller knowledge, IProgress<string>? progress = null)
    {
        this.engineering = engineering;
        this.knowledge = knowledge;
        this.progress = progress;
    }

    public async Task<ReadProjectContextResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var info = await Timed("Reading project info", () =>
            engineering.CallAsync<ProjectInfo>("get_project_info", new { }, cancellationToken));
        var projectName = string.IsNullOrWhiteSpace(info.Name) ? "unknown" : info.Name!;
        var exportRoot = AssistantPaths.ResolveExportRoot(projectName);
        Log($"Export root: {exportRoot}");

        cancellationToken.ThrowIfCancellationRequested();
        var blocks = await Timed("Exporting blocks", () =>
            engineering.CallAsync<ExportResult[]>("export_all_blocks", new { outputDir = exportRoot }, cancellationToken));

        cancellationToken.ThrowIfCancellationRequested();
        var tagTables = await Timed("Exporting tag tables", () =>
            engineering.CallAsync<ExportResult[]>("export_tag_tables", new { outputDir = exportRoot }, cancellationToken));

        cancellationToken.ThrowIfCancellationRequested();
        var udts = await Timed("Exporting UDTs", () =>
            engineering.CallAsync<ExportResult[]>("export_udts", new { outputDir = exportRoot }, cancellationToken));

        // Fail fast with the real cause: a 0-block export means no PLC software was found (or every
        // block failed) — running ingest would only surface a confusing EXPORT_ROOT_NOT_FOUND later.
        var blocksExported = CountSuccessful(blocks);
        if (blocksExported == 0)
        {
            throw new InvalidOperationException(
                $"export_all_blocks produced 0 blocks for project '{projectName}' — no PLC software was found or every block failed to export. " +
                "Check the project has a PLC with blocks (list_blocks) before reading project context.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var ingest = await Timed("Building knowledge base", () =>
            knowledge.CallAsync<IngestResult>("ingest_source", new { exportRoot }, cancellationToken));

        Log($"Knowledge base: {ingest.Nodes} nodes, {ingest.Edges} edges → {ingest.DbPath}");
        return new ReadProjectContextResult
        {
            ProjectName = projectName,
            ExportRoot = exportRoot,
            DbPath = ingest.DbPath,
            BlocksExported = blocksExported,
            TagTablesExported = CountSuccessful(tagTables),
            UdtsExported = CountSuccessful(udts),
            Ingest = ingest,
        };
    }

    private static int CountSuccessful(ExportResult[] results)
    {
        var failed = results.Count(result => !result.Success);
        return results.Length - failed;
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
