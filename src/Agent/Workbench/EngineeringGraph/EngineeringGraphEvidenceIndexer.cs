namespace Agent.Workbench.EngineeringGraph;

using Agent.Workbench;

/// <summary>Raw file evidence that could not be resolved to a source object. The path is kept
/// exactly as reported by Git; no source identity is manufactured.</summary>
public sealed record UnresolvedGitFileEvidence(string CommitSha, string RelativePath);

public sealed record EngineeringGraphEvidenceIndexResult(
    IReadOnlyList<GraphEdge> SourceEdges,
    IReadOnlyList<GraphEdge> SvnEdges,
    IReadOnlyList<UnresolvedGitFileEvidence> UnresolvedFiles);

/// <summary>Indexes externally-owned Git timeline evidence into the Workbench-local graph.</summary>
public sealed class EngineeringGraphEvidenceIndexer
{
    private readonly EngineeringGraphService graph;
    private readonly WorkbenchMetadata workbench;

    public EngineeringGraphEvidenceIndexer(EngineeringGraphService graph, WorkbenchMetadata workbench)
    {
        this.graph = graph ?? throw new ArgumentNullException(nameof(graph));
        this.workbench = workbench ?? throw new ArgumentNullException(nameof(workbench));
        if (!string.Equals(graph.WorkbenchId(), workbench.WorkbenchId, StringComparison.Ordinal))
            throw new EngineeringGraphConstraintException("Evidence belongs to another Workbench.");
    }

    /// <summary>Indexes one timeline commit. Source edges are created only when the path resolves
    /// through a registered device's exported metadata manifest.</summary>
    public EngineeringGraphEvidenceIndexResult IndexCommit(
        string worktreeId,
        VersionControlTimelineGitCommit commit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreeId);
        ArgumentNullException.ThrowIfNull(commit);
        var registration = workbench.Worktrees.SingleOrDefault(item => item.WorktreeId == worktreeId)
            ?? throw new EngineeringGraphConstraintException($"Worktree '{worktreeId}' was not found.");

        graph.RegisterEntity(new GraphEntity(GraphEntityKind.GitCommit, commit.Sha,
            workbench.WorkbenchId, worktreeId));
        // One lookup for the whole commit. A baseline commit lists every exported file — over a
        // thousand for a real project — and resolving each path against a freshly parsed manifest
        // would read that manifest once per file.
        var sourceIndex = BuildSourceIndex(registration);
        var edges = new List<GraphEdge>();
        var unresolved = new List<UnresolvedGitFileEvidence>();
        foreach (var path in commit.Files ?? Array.Empty<string>())
        {
            var source = ResolveSource(sourceIndex, path);
            if (source is null)
            {
                if (!string.Equals(path, EngineeringStateWriter.RelativePath, StringComparison.OrdinalIgnoreCase))
                {
                    unresolved.Add(new UnresolvedGitFileEvidence(commit.Sha, path));
                    graph.RecordFileEvidence(commit.Sha, path);
                }
                continue;
            }

            var entityId = $"{source.Value.DeviceId}:{source.Value.Info.Id}";
            graph.RegisterEntity(new GraphEntity(GraphEntityKind.SourceObject, entityId,
                workbench.WorkbenchId, worktreeId, source.Value.DeviceId,
                source.Value.Info.RelativePath));
            if (!graph.GetEdges(GraphEntityKind.GitCommit, commit.Sha, GraphEntityKind.SourceObject)
                    .Any(edge => edge.ToId == entityId))
            {
                edges.Add(graph.AddEdge(GraphEntityKind.GitCommit, commit.Sha,
                    GraphEntityKind.SourceObject, entityId, GraphProvenance.Evidence));
            }
        }

        var svnEdges = new List<GraphEdge>();
        if (commit.SvnRevision is { } revision)
        {
            var svnId = $"{worktreeId}:{revision}";
            graph.RegisterEntity(new GraphEntity(GraphEntityKind.SvnRevision, svnId,
                workbench.WorkbenchId, worktreeId, ExternalRef: svnId));
            if (!graph.GetEdges(GraphEntityKind.GitCommit, commit.Sha, GraphEntityKind.SvnRevision)
                    .Any(edge => edge.ToId == svnId))
            {
                svnEdges.Add(graph.AddEdge(GraphEntityKind.GitCommit, commit.Sha,
                    GraphEntityKind.SvnRevision, svnId, GraphProvenance.Evidence));
            }
            foreach (var taskEdge in graph.GetIncomingEdges(GraphEntityKind.GitCommit, commit.Sha)
                         .Where(edge => edge.FromKind == GraphEntityKind.Task))
            {
                if (!graph.GetEdges(GraphEntityKind.Task, taskEdge.FromId, GraphEntityKind.SvnRevision)
                        .Any(edge => edge.ToId == svnId))
                    graph.AddEdge(GraphEntityKind.Task, taskEdge.FromId,
                        GraphEntityKind.SvnRevision, svnId, GraphProvenance.Evidence);
            }
        }

        return new EngineeringGraphEvidenceIndexResult(edges, svnEdges, unresolved);
    }

    /// <summary>Every source object the worktree's registered devices have exported, keyed by the Git
    /// path that carries it and built once per indexed commit.</summary>
    private sealed class WorktreeSourceIndex
    {
        public Dictionary<string, (string DeviceId, SourceObjectInfo Info)> ByPath { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private WorktreeSourceIndex BuildSourceIndex(WorkbenchWorktreeRegistration registration)
    {
        var index = new WorktreeSourceIndex();
        var contextRoot = WorkbenchPaths.ResolveWorktree(workbench.RootPath, registration.RelativePath);
        var metadataPath = Path.Combine(contextRoot, "worktree.json");
        if (!File.Exists(metadataPath)) return index;
        var metadata = new AtomicJsonStore().Read<WorktreeMetadata>(metadataPath);
        var devicesRoot = Path.Combine(contextRoot, "devices");
        if (!Directory.Exists(devicesRoot)) return index;
        foreach (var deviceMetadataPath in Directory.EnumerateFiles(devicesRoot, "device.json", SearchOption.AllDirectories))
        {
            DeviceMetadata device;
            try
            {
                device = new AtomicJsonStore().Read<DeviceMetadata>(deviceMetadataPath);
            }
            catch (Exception)
            {
                continue;
            }

            if (!metadata.DeviceIds.Contains(device.DeviceId, StringComparer.Ordinal)) continue;
            var deviceDirectory = Path.GetFileName(Path.GetDirectoryName(deviceMetadataPath)!);
            var marker = $"devices/{deviceDirectory}/source/";
            foreach (var info in DeviceSnapshotReader.ReadManifestSourceObjects(
                         Path.Combine(Path.GetDirectoryName(deviceMetadataPath)!, "source")))
            {
                // First device listed for the worktree wins, as the per-path walk did.
                index.ByPath.TryAdd($"{marker}{info.RelativePath}", (device.DeviceId, info));
            }
        }

        return index;
    }

    private static (string DeviceId, SourceObjectInfo Info)? ResolveSource(WorktreeSourceIndex index, string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        var markerIndex = normalized.IndexOf("devices/", StringComparison.OrdinalIgnoreCase);
        return markerIndex >= 0 && index.ByPath.TryGetValue(normalized[markerIndex..], out var found)
            ? found
            : null;
    }
}

public sealed class EngineeringGraphEvidenceIndexerProvider
{
    public EngineeringGraphEvidenceIndexResult Index(WorkbenchMetadata workbench, string worktreeId,
        VersionControlTimelineGitCommit commit)
    {
        using var store = new EngineeringGraphStore(workbench.RootPath);
        var graph = new EngineeringGraphService(store, workbench.WorkbenchId,
            id => workbench.Worktrees.Any(item => item.WorktreeId == id));
        return new EngineeringGraphEvidenceIndexer(graph, workbench).IndexCommit(worktreeId, commit);
    }

    /// <summary>
    /// Indexes one commit whose file list the caller already holds, for a path that is not the
    /// coordinator's commit flow — a merge, or a raw gateway commit route (AC-006). Best effort by
    /// design: the Git operation has already succeeded by the time this runs, so a graph that cannot
    /// be written returns a warning rather than failing the commit. Returns null when the evidence was
    /// recorded.
    /// </summary>
    public string? TryIndexCommit(
        WorkbenchMetadata workbench,
        string worktreeId,
        string sha,
        IReadOnlyList<string> files,
        string message = "app-mediated commit",
        long? svnRevision = null)
    {
        ArgumentNullException.ThrowIfNull(workbench);
        if (string.IsNullOrWhiteSpace(sha))
            throw new ArgumentException("A Git commit id is required.", nameof(sha));
        try
        {
            Index(workbench, worktreeId,
                new VersionControlTimelineGitCommit(sha, "Automation Workbench", message,
                    DateTimeOffset.UtcNow.ToString("O"), files, null, svnRevision, false));
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return $"Commit '{sha}' succeeded, but evidence indexing was not recorded: {exception.Message}";
        }
    }
}
