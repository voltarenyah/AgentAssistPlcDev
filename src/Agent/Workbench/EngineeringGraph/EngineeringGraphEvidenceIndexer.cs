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
        var edges = new List<GraphEdge>();
        var unresolved = new List<UnresolvedGitFileEvidence>();
        foreach (var path in commit.Files ?? Array.Empty<string>())
        {
            var source = ResolveSource(registration, path);
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
        }

        return new EngineeringGraphEvidenceIndexResult(edges, svnEdges, unresolved);
    }

    private (string DeviceId, SourceObjectInfo Info)? ResolveSource(
        WorkbenchWorktreeRegistration registration, string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        var contextRoot = WorkbenchPaths.ResolveWorktree(workbench.RootPath, registration.RelativePath);
        var metadataPath = Path.Combine(contextRoot, "worktree.json");
        if (!File.Exists(metadataPath)) return null;
        var metadata = new AtomicJsonStore().Read<WorktreeMetadata>(metadataPath);
        foreach (var deviceId in metadata.DeviceIds)
        {
            var deviceMetadataPath = Directory.EnumerateFiles(
                    Path.Combine(contextRoot, "devices"), "device.json", SearchOption.AllDirectories)
                .FirstOrDefault(candidate =>
                {
                    try { return new AtomicJsonStore().Read<DeviceMetadata>(candidate).DeviceId == deviceId; }
                    catch (Exception) { return false; }
                });
            if (deviceMetadataPath is null) continue;
            var deviceMetadata = new AtomicJsonStore().Read<DeviceMetadata>(deviceMetadataPath);
            var deviceDirectory = Path.GetFileName(Path.GetDirectoryName(deviceMetadataPath)!);
            if (deviceMetadata.DeviceId != deviceId) continue;
            var marker = $"devices/{deviceDirectory}/source/";
            var markerIndex = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0) continue;
            var sourcePath = normalized[(markerIndex + marker.Length)..];
            var deviceRoot = Path.GetDirectoryName(deviceMetadataPath)!;
            var info = DeviceSnapshotReader.ReadManifestSourceObjects(Path.Combine(deviceRoot, "source"))
                .FirstOrDefault(item =>
                    string.Equals(item.RelativePath, sourcePath, StringComparison.OrdinalIgnoreCase));
            if (info is not null) return (deviceMetadata.DeviceId, info);
        }
        return null;
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
}
