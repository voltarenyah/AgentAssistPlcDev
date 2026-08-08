using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Contracts.Engineering;

[assembly: InternalsVisibleTo("Agent.Tests")]

namespace Agent.Workbench;

internal interface IReconciliationFileOperations
{
    bool FileExists(string path);

    void CopyFile(string sourcePath, string destinationPath, bool overwrite);

    void MoveFile(string sourcePath, string destinationPath, bool overwrite);

    void DeleteFile(string path);
}

internal sealed class ReconciliationFileOperations : IReconciliationFileOperations
{
    public bool FileExists(string path) => File.Exists(path);

    public void CopyFile(string sourcePath, string destinationPath, bool overwrite) =>
        File.Copy(sourcePath, destinationPath, overwrite);

    public void MoveFile(string sourcePath, string destinationPath, bool overwrite) =>
        File.Move(sourcePath, destinationPath, overwrite);

    public void DeleteFile(string path) => File.Delete(path);
}

public sealed class DeviceReconciler
{
    public const string ApprovalRequiredCode = "RECONCILIATION_APPROVAL_REQUIRED";
    public const string ApprovalInvalidCode = "RECONCILIATION_APPROVAL_INVALID";
    public const string PreviewStaleCode = "RECONCILIATION_PREVIEW_STALE";
    public const string ManifestInvalidCode = "RECONCILIATION_MANIFEST_INVALID";

    private const string MetadataFileName = "metadata.json";
    private readonly IReconciliationFileOperations _fileOperations;

    public DeviceReconciler()
        : this(new ReconciliationFileOperations())
    {
    }

    internal DeviceReconciler(IReconciliationFileOperations fileOperations)
    {
        _fileOperations = fileOperations
            ?? throw new ArgumentNullException(nameof(fileOperations));
    }

    public ReconciliationPreview Preview(DeviceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var stagingManifest = ReadManifest(
            context.StagingRoot,
            required: true,
            requireReferencedFiles: true);
        var baselineManifest = ReadManifest(
            context.SourceRoot,
            required: false,
            requireReferencedFiles: false);

        var paths = baselineManifest.Components.Keys
            .Concat(stagingManifest.Components.Keys)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        var entries = new ReconciliationEntry[paths.Length];
        var baselineTree = new List<TreeItem>(baselineManifest.Components.Count);
        var stagingTree = new List<TreeItem>(stagingManifest.Components.Count);

        for (var index = 0; index < paths.Length; index++)
        {
            var relativePath = paths[index];
            baselineManifest.Components.TryGetValue(relativePath, out var baselineComponent);
            stagingManifest.Components.TryGetValue(relativePath, out var stagingComponent);

            var baselineHash = baselineComponent is null
                ? null
                : HashFileIfPresent(context.SourceRoot, relativePath);
            var stagingHash = stagingComponent is null
                ? null
                : HashRequiredFile(context.StagingRoot, relativePath);

            if (baselineComponent is not null)
            {
                baselineTree.Add(new TreeItem(relativePath, baselineHash));
            }

            if (stagingComponent is not null)
            {
                stagingTree.Add(new TreeItem(relativePath, stagingHash));
            }

            entries[index] = new ReconciliationEntry(
                relativePath,
                Classify(baselineComponent, stagingComponent, baselineHash, stagingHash),
                baselineHash,
                stagingHash,
                stagingComponent?.Identity ?? baselineComponent?.Identity,
                baselineComponent?.Fingerprints,
                stagingComponent?.Fingerprints,
                MatchFingerprints(
                    baselineComponent?.Fingerprints,
                    stagingComponent?.Fingerprints));
        }

        var baselineTreeHash = ComputeTreeHash(baselineTree);
        var stagingTreeHash = ComputeTreeHash(stagingTree);
        var previewId = ComputePreviewId(
            context.WorktreeId,
            context.DeviceId,
            baselineTreeHash,
            stagingTreeHash,
            entries);

        return new ReconciliationPreview(
            previewId,
            context.WorktreeId,
            context.DeviceId,
            baselineTreeHash,
            stagingTreeHash,
            Array.AsReadOnly(entries));
    }

    public ReconciliationOutcome Apply(
        DeviceContext context,
        ReconciliationPreview approvedPreview,
        IReadOnlySet<string> approvedPaths)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (approvedPreview is null)
        {
            throw new ReconciliationException(
                ApprovalRequiredCode,
                "A reconciliation preview must be explicitly approved before applying it.");
        }

        ArgumentNullException.ThrowIfNull(approvedPaths);

        var current = Preview(context);
        if (!string.Equals(approvedPreview.WorktreeId, context.WorktreeId, StringComparison.Ordinal)
            || !string.Equals(approvedPreview.DeviceId, context.DeviceId, StringComparison.Ordinal)
            || !string.Equals(
                approvedPreview.BaselineTreeHash,
                current.BaselineTreeHash,
                StringComparison.Ordinal)
            || !string.Equals(
                approvedPreview.StagingTreeHash,
                current.StagingTreeHash,
                StringComparison.Ordinal)
            || !string.Equals(approvedPreview.PreviewId, current.PreviewId, StringComparison.Ordinal))
        {
            throw new ReconciliationException(
                PreviewStaleCode,
                "The baseline or staged export changed after this preview was created.");
        }

        var normalizedApprovals = NormalizeApprovedPaths(
            context.SourceRoot,
            approvedPaths);
        var actionablePaths = current.Entries
            .Where(static entry => entry.Kind != ReconciliationChangeKind.Unchanged)
            .Select(static entry => entry.RelativePath)
            .ToHashSet(StringComparer.Ordinal);
        var invalidApproval = normalizedApprovals.FirstOrDefault(path =>
            !actionablePaths.Contains(path));
        if (invalidApproval is not null)
        {
            throw new ReconciliationException(
                ApprovalInvalidCode,
                $"'{invalidApproval}' is not an actionable entry in this comparison.");
        }
        var mutations = new List<PendingMutation>();
        var artifacts = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            foreach (var entry in current.Entries)
            {
                if (entry.Kind is not (
                        ReconciliationChangeKind.Added or
                        ReconciliationChangeKind.Changed)
                    || !normalizedApprovals.Contains(entry.RelativePath))
                {
                    continue;
                }

                var sourcePath = ResolveControlledPath(context.StagingRoot, entry.RelativePath);
                var destinationPath =
                    ResolveControlledPath(context.SourceRoot, entry.RelativePath);
                var destinationDirectory = Path.GetDirectoryName(destinationPath)!;
                Directory.CreateDirectory(destinationDirectory);
                var temporaryPath = Path.Combine(
                    destinationDirectory,
                    $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
                artifacts.Add(temporaryPath);
                _fileOperations.CopyFile(sourcePath, temporaryPath, overwrite: false);
                mutations.Add(PendingMutation.Replace(destinationPath, temporaryPath));
            }

            foreach (var entry in current.Entries)
            {
                if (entry.Kind != ReconciliationChangeKind.Removed
                    || !normalizedApprovals.Contains(entry.RelativePath))
                {
                    continue;
                }

                var destinationPath =
                    ResolveControlledPath(context.SourceRoot, entry.RelativePath);
                if (_fileOperations.FileExists(destinationPath))
                {
                    mutations.Add(PendingMutation.Delete(destinationPath));
                }
            }

            var manifestMutation = PrepareManifestMutation(
                context,
                current,
                normalizedApprovals,
                artifacts);
            if (manifestMutation is not null)
            {
                mutations.Add(manifestMutation);
            }

            CaptureOriginalState(mutations, artifacts);

            try
            {
                foreach (var mutation in mutations)
                {
                    ApplyMutation(mutation);
                }
            }
            catch (Exception applyException)
            {
                var rollbackExceptions = RollBack(mutations);
                if (rollbackExceptions.Count > 0)
                {
                    throw new AggregateException(
                        "Reconciliation failed and one or more baseline files could not be restored.",
                        new[] { applyException }.Concat(rollbackExceptions));
                }

                throw;
            }

            var exactChangedPaths = mutations
                .Where(static mutation => mutation.Applied)
                .Select(mutation => ToGitPath(context, mutation.DestinationPath))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static path => path, StringComparer.Ordinal)
                .ToArray();
            return new ReconciliationOutcome(
                current.PreviewId,
                Array.AsReadOnly(exactChangedPaths));
        }
        finally
        {
            foreach (var artifact in artifacts)
            {
                try
                {
                    if (_fileOperations.FileExists(artifact))
                    {
                        _fileOperations.DeleteFile(artifact);
                    }
                }
                catch (IOException)
                {
                    // Preserve the primary apply/rollback result. Artifacts use unique names and
                    // are retried by neither reconciliation nor Git staging.
                }
                catch (UnauthorizedAccessException)
                {
                    // Preserve the primary apply/rollback result.
                }
            }
        }
    }

    private PendingMutation? PrepareManifestMutation(
        DeviceContext context,
        ReconciliationPreview preview,
        IReadOnlySet<string> approvedPaths,
        ISet<string> artifacts)
    {
        if (approvedPaths.Count == 0)
        {
            return null;
        }

        var sourcePath = ResolveControlledPath(context.StagingRoot, MetadataFileName);
        var destinationPath =
            ResolveControlledPath(context.SourceRoot, MetadataFileName);
        Directory.CreateDirectory(context.SourceRoot);
        var temporaryPath = Path.Combine(
            context.SourceRoot,
            $".{MetadataFileName}.{Guid.NewGuid():N}.tmp");
        artifacts.Add(temporaryPath);

        try
        {
            var mergedManifest = MergeSelectedComponents(
                context,
                preview,
                approvedPaths);
            File.WriteAllText(temporaryPath, mergedManifest);
        }
        catch
        {
            if (_fileOperations.FileExists(temporaryPath))
            {
                _fileOperations.DeleteFile(temporaryPath);
            }

            throw;
        }

        if (_fileOperations.FileExists(destinationPath)
            && FilesHaveEqualContent(temporaryPath, destinationPath))
        {
            _fileOperations.DeleteFile(temporaryPath);
            return null;
        }

        return PendingMutation.Replace(destinationPath, temporaryPath);
    }

    private static string MergeSelectedComponents(
        DeviceContext context,
        ReconciliationPreview preview,
        IReadOnlySet<string> approvedPaths)
    {
        var stagingManifestPath =
            ResolveControlledPath(context.StagingRoot, MetadataFileName);
        var baselineManifestPath =
            ResolveControlledPath(context.SourceRoot, MetadataFileName);
        var stagingRoot = JsonNode.Parse(File.ReadAllText(stagingManifestPath))
            ?.AsObject()
            ?? throw new ReconciliationException(
                ManifestInvalidCode,
                $"The manifest '{stagingManifestPath}' is empty.");
        var stagingComponents = stagingRoot["components"]?.AsArray()
            ?? throw new ReconciliationException(
                ManifestInvalidCode,
                $"The manifest '{stagingManifestPath}' has no component array.");
        var baselineRoot = File.Exists(baselineManifestPath)
            ? JsonNode.Parse(File.ReadAllText(baselineManifestPath))?.AsObject()
            : null;
        var outputRoot = baselineRoot?.DeepClone().AsObject()
            ?? stagingRoot.DeepClone().AsObject();
        var outputComponents = new JsonArray();
        outputRoot["components"] = outputComponents;

        // The document-level "device" section is not a component: it describes the live TIA
        // project/device captured at staging time, so it always follows the staged manifest —
        // regardless of which component paths were approved (found 2026-07-31: a full rebuild
        // staged the section but the applied baseline never received it).
        if (stagingRoot["device"] is JsonNode stagedDevice)
        {
            outputRoot["device"] = stagedDevice.DeepClone();
        }

        var baselineByPath = IndexManifestNodes(
            baselineRoot?["components"]?.AsArray(),
            context.SourceRoot);
        var stagingByPath = IndexManifestNodes(
            stagingComponents,
            context.StagingRoot);

        foreach (var entry in preview.Entries.OrderBy(
                     static entry => entry.RelativePath,
                     StringComparer.Ordinal))
        {
            JsonNode? selected = entry.Kind switch
            {
                ReconciliationChangeKind.Added when approvedPaths.Contains(entry.RelativePath) =>
                    stagingByPath.GetValueOrDefault(entry.RelativePath),
                ReconciliationChangeKind.Added => null,
                ReconciliationChangeKind.Changed when approvedPaths.Contains(entry.RelativePath) =>
                    stagingByPath.GetValueOrDefault(entry.RelativePath),
                ReconciliationChangeKind.Changed =>
                    baselineByPath.GetValueOrDefault(entry.RelativePath),
                ReconciliationChangeKind.Removed when approvedPaths.Contains(entry.RelativePath) =>
                    null,
                ReconciliationChangeKind.Removed =>
                    baselineByPath.GetValueOrDefault(entry.RelativePath),
                _ => baselineByPath.GetValueOrDefault(entry.RelativePath)
                    ?? stagingByPath.GetValueOrDefault(entry.RelativePath),
            };

            if (selected is not null)
            {
                outputComponents.Add(selected.DeepClone());
            }
        }

        return outputRoot.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public IReadOnlySet<string> ValidateLegacyRemovalApprovals(
        DeviceContext context,
        ReconciliationPreview preview,
        IEnumerable<string> approvedRemovalPaths)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(approvedRemovalPaths);

        var normalized = NormalizeApprovedPaths(
            context.SourceRoot,
            approvedRemovalPaths);
        var removable = preview.Entries
            .Where(static entry => entry.Kind == ReconciliationChangeKind.Removed)
            .Select(static entry => entry.RelativePath)
            .ToHashSet(StringComparer.Ordinal);
        var invalid = normalized.FirstOrDefault(path => !removable.Contains(path));
        if (invalid is not null)
        {
            throw new ReconciliationException(
                ApprovalInvalidCode,
                $"Deprecated removal approval '{invalid}' is not a removed entry in this comparison.");
        }

        return normalized;
    }

    private static Dictionary<string, JsonNode> IndexManifestNodes(
        JsonArray? components,
        string root)
    {
        var indexed = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        if (components is null)
        {
            return indexed;
        }

        foreach (var node in components)
        {
            if (node is not JsonObject component
                || component["exportedFile"] is not JsonValue exportedFile
                || !exportedFile.TryGetValue<string>(out var value)
                || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            indexed[NormalizeRelativePath(root, value)] = component;
        }

        return indexed;
    }

    private void CaptureOriginalState(
        IEnumerable<PendingMutation> mutations,
        ISet<string> artifacts)
    {
        foreach (var mutation in mutations)
        {
            mutation.OriginalExisted =
                _fileOperations.FileExists(mutation.DestinationPath);
            if (!mutation.OriginalExisted)
            {
                continue;
            }

            var directory = Path.GetDirectoryName(mutation.DestinationPath)!;
            var backupPath = Path.Combine(
                directory,
                $".{Path.GetFileName(mutation.DestinationPath)}.{Guid.NewGuid():N}.bak");
            artifacts.Add(backupPath);
            _fileOperations.CopyFile(
                mutation.DestinationPath,
                backupPath,
                overwrite: false);
            mutation.BackupPath = backupPath;
        }
    }

    private void ApplyMutation(PendingMutation mutation)
    {
        if (mutation.ReplacementPath is not null)
        {
            _fileOperations.MoveFile(
                mutation.ReplacementPath,
                mutation.DestinationPath,
                overwrite: true);
        }
        else
        {
            _fileOperations.DeleteFile(mutation.DestinationPath);
        }

        mutation.Applied = true;
    }

    private IReadOnlyList<Exception> RollBack(IReadOnlyList<PendingMutation> mutations)
    {
        var failures = new List<Exception>();
        for (var index = mutations.Count - 1; index >= 0; index--)
        {
            var mutation = mutations[index];
            if (!mutation.Applied)
            {
                continue;
            }

            try
            {
                if (mutation.OriginalExisted)
                {
                    _fileOperations.MoveFile(
                        mutation.BackupPath!,
                        mutation.DestinationPath,
                        overwrite: true);
                }
                else if (_fileOperations.FileExists(mutation.DestinationPath))
                {
                    _fileOperations.DeleteFile(mutation.DestinationPath);
                }
            }
            catch (Exception exception) when (
                exception is IOException
                or UnauthorizedAccessException)
            {
                failures.Add(exception);
            }
        }

        return failures;
    }

    private static Manifest ReadManifest(
        string root,
        bool required,
        bool requireReferencedFiles)
    {
        var manifestPath = Path.Combine(root, MetadataFileName);
        if (!File.Exists(manifestPath))
        {
            if (!required)
            {
                return Manifest.Empty;
            }

            throw new ReconciliationException(
                ManifestInvalidCode,
                $"The staged export is missing '{MetadataFileName}'.");
        }

        JsonDocument document;
        try
        {
            using var stream = File.OpenRead(manifestPath);
            document = JsonDocument.Parse(stream);
        }
        catch (Exception exception) when (
            exception is JsonException
            or IOException
            or UnauthorizedAccessException)
        {
            throw new ReconciliationException(
                ManifestInvalidCode,
                $"The manifest '{manifestPath}' could not be read.",
                exception);
        }

        using (document)
        {
            var documentRoot = document.RootElement;
            if (documentRoot.ValueKind != JsonValueKind.Object
                || !documentRoot.TryGetProperty("schemaVersion", out var schemaVersion)
                || schemaVersion.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(schemaVersion.GetString())
                || !documentRoot.TryGetProperty("components", out var components)
                || components.ValueKind != JsonValueKind.Array)
            {
                throw new ReconciliationException(
                    ManifestInvalidCode,
                    $"The manifest '{manifestPath}' does not have the required shape.");
            }

            var controlled = new Dictionary<string, ManifestComponent>(StringComparer.Ordinal);
            foreach (var component in components.EnumerateArray())
            {
                if (component.ValueKind != JsonValueKind.Object)
                {
                    throw new ReconciliationException(
                        ManifestInvalidCode,
                        $"The manifest '{manifestPath}' contains a malformed component.");
                }

                if (!component.TryGetProperty("exportedFile", out var exportedFile)
                    || exportedFile.ValueKind is JsonValueKind.Null)
                {
                    continue;
                }

                if (exportedFile.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(exportedFile.GetString()))
                {
                    throw new ReconciliationException(
                        ManifestInvalidCode,
                        $"The manifest '{manifestPath}' contains an invalid exported file path.");
                }

                var relativePath = NormalizeRelativePath(root, exportedFile.GetString()!);
                if (string.Equals(relativePath, MetadataFileName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ReconciliationException(
                        ManifestInvalidCode,
                        $"The manifest '{manifestPath}' cannot list itself as a component.");
                }

                var identity = ReadOptionalString(component, "id")
                    ?? ReadOptionalString(component, "sourcePath");
                var fingerprints = ReadOptionalString(component, "fingerprints");
                if (!controlled.TryAdd(
                        relativePath,
                        new ManifestComponent(relativePath, identity, fingerprints)))
                {
                    throw new ReconciliationException(
                        ManifestInvalidCode,
                        $"The manifest '{manifestPath}' lists '{relativePath}' more than once.");
                }

                if (requireReferencedFiles
                    && !File.Exists(ResolveControlledPath(root, relativePath)))
                {
                    throw new ReconciliationException(
                        ManifestInvalidCode,
                        $"The staged manifest references missing file '{relativePath}'.");
                }
            }

            return new Manifest(controlled);
        }
    }

    private static string? ReadOptionalString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)
            || value.ValueKind is JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ReconciliationException(
                ManifestInvalidCode,
                $"Manifest property '{propertyName}' must be a string.");
        }

        return value.GetString();
    }

    private static HashSet<string> NormalizeApprovedPaths(
        string baselineRoot,
        IEnumerable<string> approvedPaths)
    {
        var normalized = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in approvedPaths)
        {
            normalized.Add(NormalizeRelativePath(baselineRoot, path));
        }

        return normalized;
    }

    private static string NormalizeRelativePath(string root, string relativePath)
    {
        try
        {
            var platformPath = relativePath
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);
            var resolved = WorkbenchPaths.ResolveRelative(root, platformPath);
            return Path.GetRelativePath(Path.GetFullPath(root), resolved)
                .Replace('\\', '/');
        }
        catch (WorkbenchPathException exception)
        {
            throw new ReconciliationException(
                ManifestInvalidCode,
                $"The manifest path '{relativePath}' is unsafe.",
                exception);
        }
    }

    private static string ResolveControlledPath(string root, string normalizedRelativePath)
    {
        try
        {
            return WorkbenchPaths.ResolveRelative(
                root,
                normalizedRelativePath.Replace('/', Path.DirectorySeparatorChar));
        }
        catch (WorkbenchPathException exception)
        {
            throw new ReconciliationException(
                ManifestInvalidCode,
                $"The controlled path '{normalizedRelativePath}' is unsafe.",
                exception);
        }
    }

    private static ReconciliationChangeKind Classify(
        ManifestComponent? baseline,
        ManifestComponent? staging,
        string? baselineHash,
        string? stagingHash)
    {
        if (baseline is null)
        {
            return ReconciliationChangeKind.Added;
        }

        if (staging is null)
        {
            return ReconciliationChangeKind.Removed;
        }

        return string.Equals(baselineHash, stagingHash, StringComparison.Ordinal)
            ? ReconciliationChangeKind.Unchanged
            : ReconciliationChangeKind.Changed;
    }

    private static bool? MatchFingerprints(string? stored, string? live) =>
        stored is null || live is null
            ? null
            : string.Equals(stored, live, StringComparison.Ordinal);

    private static string? HashFileIfPresent(string root, string relativePath)
    {
        var path = ResolveControlledPath(root, relativePath);
        return File.Exists(path) ? HashFile(path) : null;
    }

    private static string HashRequiredFile(string root, string relativePath)
    {
        var path = ResolveControlledPath(root, relativePath);
        if (!File.Exists(path))
        {
            throw new ReconciliationException(
                ManifestInvalidCode,
                $"The staged manifest references missing file '{relativePath}'.");
        }

        return HashFile(path);
    }

    /// <summary>Normalized content hash (<see cref="XmlContentHash"/>) — never a raw byte hash.
    /// Every staged refresh is a full rebuild_export, and each TIA export stamps a fresh
    /// &lt;Created&gt; timestamp, so raw bytes always differ; comparing raw hashes reported every
    /// component as changed even when nothing was edited (found 2026-07-31).</summary>
    private static string HashFile(string path)
    {
        try
        {
            return XmlContentHash.Compute(File.ReadAllText(path));
        }
        catch (Exception exception) when (
            exception is IOException
            or UnauthorizedAccessException)
        {
            throw new ReconciliationException(
                ManifestInvalidCode,
                $"The controlled file '{path}' could not be hashed.",
                exception);
        }
    }

    private static string ComputeTreeHash(IEnumerable<TreeItem> items)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var item in items.OrderBy(static item => item.RelativePath, StringComparer.Ordinal))
        {
            AppendUtf8(hash, item.RelativePath);
            hash.AppendData(new byte[] { 0 });
            AppendUtf8(hash, item.ContentHash ?? "<missing>");
            hash.AppendData(new byte[] { (byte)'\n' });
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string ComputePreviewId(
        string worktreeId,
        string deviceId,
        string baselineTreeHash,
        string stagingTreeHash,
        IEnumerable<ReconciliationEntry> entries)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var value in new[]
                 {
                     worktreeId,
                     deviceId,
                     baselineTreeHash,
                     stagingTreeHash,
                 })
        {
            AppendUtf8(hash, value);
            hash.AppendData(new byte[] { 0 });
        }

        foreach (var entry in entries)
        {
            AppendUtf8(hash, entry.RelativePath);
            hash.AppendData(new byte[] { 0 });
            AppendUtf8(hash, entry.Kind.ToString());
            hash.AppendData(new byte[] { 0 });
            AppendUtf8(hash, entry.ComponentIdentity ?? string.Empty);
            hash.AppendData(new byte[] { 0 });
            AppendUtf8(hash, entry.StoredFingerprints ?? "<missing>");
            hash.AppendData(new byte[] { 0 });
            AppendUtf8(hash, entry.LiveFingerprints ?? "<missing>");
            hash.AppendData(new byte[] { 0 });
            AppendUtf8(hash, entry.FingerprintsMatch?.ToString() ?? "<unknown>");
            hash.AppendData(new byte[] { (byte)'\n' });
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendUtf8(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
    }

    private static bool FilesHaveEqualContent(string firstPath, string secondPath) =>
        string.Equals(HashFile(firstPath), HashFile(secondPath), StringComparison.Ordinal);

    private static string ToGitPath(DeviceContext context, string path) =>
        Path.GetRelativePath(context.WorktreeRoot, path).Replace('\\', '/');

    private sealed record ManifestComponent(
        string RelativePath,
        string? Identity,
        string? Fingerprints);

    private sealed record Manifest(
        IReadOnlyDictionary<string, ManifestComponent> Components)
    {
        public static Manifest Empty { get; } =
            new(new Dictionary<string, ManifestComponent>(StringComparer.Ordinal));
    }

    private sealed record TreeItem(string RelativePath, string? ContentHash);

    private sealed class PendingMutation
    {
        private PendingMutation(string destinationPath, string? replacementPath)
        {
            DestinationPath = destinationPath;
            ReplacementPath = replacementPath;
        }

        public string DestinationPath { get; }

        public string? ReplacementPath { get; }

        public bool OriginalExisted { get; set; }

        public string? BackupPath { get; set; }

        public bool Applied { get; set; }

        public static PendingMutation Replace(
            string destinationPath,
            string replacementPath) =>
            new(destinationPath, replacementPath);

        public static PendingMutation Delete(string destinationPath) =>
            new(destinationPath, replacementPath: null);
    }
}
