using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Agent.Workbench.EngineeringGraph;

/// <summary>
/// Everything one hardware projection reads from the worktree, resolved before any graph write so the
/// write is one short transaction. The three views are the hardware readers' own output — the readers
/// are the ingest now (ADR-0011 Phase 5) — so the routes' values cannot drift from the readers'.
/// </summary>
public sealed record HardwareProjectionInput(
    HardwareConfigurationView Configuration,
    HardwareBomView Bom,
    HardwareNetworkView Network,
    string Digest);

/// <summary>
/// The hardware/AML subtree's ingest (ADR-0011 Phase 5). The subtree is a different export from the PLC
/// source (`hardware/manifest.json` and the `project.aml` it names), so it is projected per worktree:
/// one <see cref="GraphEntityKind.Worktree"/> node whose properties hold the configuration, the bill of
/// materials and the network view, plus the digest a boundary compares. The hardware routes serve those
/// rows, so no request opens or parses the AML.
/// </summary>
public sealed class HardwareProjectionService
{
    /// <summary>The shape version of the stored views; raising it re-projects on the next route call.</summary>
    public const string CurrentFormat = "1";

    /// <summary>Same options on both sides of the round trip, so the body the route serializes is the
    /// body the readers produced.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly EngineeringGraphService _graph;

    public HardwareProjectionService(EngineeringGraphService graph) =>
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));

    /// <summary>Reads the subtree and writes the worktree's facts. A projection that fails is reported
    /// as a projection failure (ADR-0012), never left to look like the read that followed it.</summary>
    public int ProjectHardware(string worktreeRoot, string worktreeId, HardwareProjectionInput? input = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreeRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreeId);
        try
        {
            return _graph.ReplaceHardwareFacts(worktreeId, Facts(input ?? ReadInput(worktreeRoot)));
        }
        catch (Exception exception) when (exception is not OperationCanceledException
            and not ArgumentException
            and not WorkbenchPathException
            and not EngineeringGraphConstraintException
            and not EngineeringGraphProjectionException)
        {
            throw new EngineeringGraphProjectionException(
                $"The projection of worktree '{worktreeId}' hardware facts failed: {exception.Message}",
                exception);
        }
    }

    /// <summary>The three views exactly as the routes used to build them, plus the digest of the files
    /// they came from. No graph write happens here.</summary>
    public static HardwareProjectionInput ReadInput(string worktreeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreeRoot);
        return new HardwareProjectionInput(
            HardwareConfigurationReader.Read(worktreeRoot),
            HardwareListReader.ReadBom(worktreeRoot),
            HardwareListReader.ReadNetwork(worktreeRoot),
            HardwareDigest.Compute(worktreeRoot));
    }

    private static IReadOnlyList<GraphProperty> Facts(HardwareProjectionInput input)
    {
        const string source = GraphPropertySource.HardwareProjection;
        return
        [
            GraphProperty.TextValue(HardwarePropertyNames.Format, source, CurrentFormat),
            GraphProperty.TextValue(HardwarePropertyNames.State, source, input.Configuration.State),
            GraphProperty.TextValue(HardwarePropertyNames.ExportedAt, source, input.Configuration.ExportedAt),
            GraphProperty.TextValue(HardwarePropertyNames.ProjectAmlPath, source, input.Configuration.ProjectAmlPath),
            GraphProperty.TextValue(HardwarePropertyNames.Digest, source, input.Digest),
            GraphProperty.FlagValue(HardwarePropertyNames.Invalidated, source, false),
            GraphProperty.JsonValue(HardwarePropertyNames.Configuration, source,
                JsonSerializer.Serialize(input.Configuration, Json)),
            GraphProperty.JsonValue(HardwarePropertyNames.Bom, source,
                JsonSerializer.Serialize(input.Bom, Json)),
            GraphProperty.JsonValue(HardwarePropertyNames.Network, source,
                JsonSerializer.Serialize(input.Network, Json)),
        ];
    }
}

/// <summary>
/// The hardware subtree's change detection (ADR-0012). The digest covers the layout the readers
/// resolved — which root, which manifest, which AML file — and the size and timestamp of the two files
/// it was derived from, so the boundary that compares it reads no XML and does not parse the AML.
/// </summary>
public static class HardwareDigest
{
    public static string Compute(string worktreeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worktreeRoot);
        var layout = HardwareAml.ResolveLayout(worktreeRoot);
        var payload = string.Join('\n',
            layout.State,
            layout.ManifestRoot,
            layout.ProjectAmlPath ?? string.Empty,
            layout.ExportedAt ?? string.Empty,
            Stamp(layout.ManifestPath),
            Stamp(layout.ProjectAmlPath));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }

    private static string Stamp(string? path)
    {
        if (path is null || !File.Exists(path)) return "absent";
        var file = new FileInfo(path);
        return $"{file.Length}:{file.LastWriteTimeUtc.Ticks}";
    }
}
