using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Agent.Workbench;

public sealed class DeviceReconciler
{
    public const string ApprovalRequiredCode = "RECONCILIATION_APPROVAL_REQUIRED";
    public const string PreviewStaleCode = "RECONCILIATION_PREVIEW_STALE";
    public const string ManifestInvalidCode = "RECONCILIATION_MANIFEST_INVALID";

    private const string MetadataFileName = "metadata.json";

    public ReconciliationPreview Preview(DeviceContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var stagingManifest = ReadManifest(
            context.StagingRoot,
            required: true,
            requireReferencedFiles: true);
        var baselineManifest = ReadManifest(
            context.ExportedSourceRoot,
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
                : HashFileIfPresent(context.ExportedSourceRoot, relativePath);
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
                stagingComponent?.Identity ?? baselineComponent?.Identity);
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
        IReadOnlySet<string> approvedRemovalPaths)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (approvedPreview is null)
        {
            throw new ReconciliationException(
                ApprovalRequiredCode,
                "A reconciliation preview must be explicitly approved before applying it.");
        }

        ArgumentNullException.ThrowIfNull(approvedRemovalPaths);

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

        var normalizedRemovals = NormalizeApprovedRemovals(
            context.ExportedSourceRoot,
            approvedRemovalPaths);
        var temporaryFiles = new List<PendingReplacement>();
        var changedRelativePaths = new List<string>();

        try
        {
            foreach (var entry in current.Entries)
            {
                if (entry.Kind is not (
                        ReconciliationChangeKind.Added or
                        ReconciliationChangeKind.Changed))
                {
                    continue;
                }

                var sourcePath = ResolveControlledPath(context.StagingRoot, entry.RelativePath);
                var destinationPath =
                    ResolveControlledPath(context.ExportedSourceRoot, entry.RelativePath);
                var destinationDirectory = Path.GetDirectoryName(destinationPath)!;
                Directory.CreateDirectory(destinationDirectory);
                var temporaryPath = Path.Combine(
                    destinationDirectory,
                    $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");
                File.Copy(sourcePath, temporaryPath, overwrite: false);
                temporaryFiles.Add(new PendingReplacement(temporaryPath, destinationPath));
            }

            foreach (var replacement in temporaryFiles)
            {
                File.Move(replacement.TemporaryPath, replacement.DestinationPath, overwrite: true);
                changedRelativePaths.Add(ToGitPath(context, replacement.DestinationPath));
            }

            foreach (var entry in current.Entries)
            {
                if (entry.Kind != ReconciliationChangeKind.Removed
                    || !normalizedRemovals.Contains(entry.RelativePath))
                {
                    continue;
                }

                var destinationPath =
                    ResolveControlledPath(context.ExportedSourceRoot, entry.RelativePath);
                if (File.Exists(destinationPath))
                {
                    File.Delete(destinationPath);
                    changedRelativePaths.Add(ToGitPath(context, destinationPath));
                }
            }

            ApplyManifestLast(context, changedRelativePaths);
        }
        finally
        {
            foreach (var replacement in temporaryFiles)
            {
                if (File.Exists(replacement.TemporaryPath))
                {
                    File.Delete(replacement.TemporaryPath);
                }
            }
        }

        var exactChangedPaths = changedRelativePaths
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        return new ReconciliationOutcome(
            current.PreviewId,
            Array.AsReadOnly(exactChangedPaths));
    }

    private static void ApplyManifestLast(
        DeviceContext context,
        ICollection<string> changedRelativePaths)
    {
        var sourcePath = ResolveControlledPath(context.StagingRoot, MetadataFileName);
        var destinationPath =
            ResolveControlledPath(context.ExportedSourceRoot, MetadataFileName);
        if (File.Exists(destinationPath)
            && FilesHaveEqualContent(sourcePath, destinationPath))
        {
            return;
        }

        Directory.CreateDirectory(context.ExportedSourceRoot);
        var temporaryPath = Path.Combine(
            context.ExportedSourceRoot,
            $".{MetadataFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(sourcePath, temporaryPath, overwrite: false);
            File.Move(temporaryPath, destinationPath, overwrite: true);
            changedRelativePaths.Add(ToGitPath(context, destinationPath));
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
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
                if (!controlled.TryAdd(
                        relativePath,
                        new ManifestComponent(relativePath, identity)))
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

    private static HashSet<string> NormalizeApprovedRemovals(
        string baselineRoot,
        IEnumerable<string> approvedRemovalPaths)
    {
        var normalized = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in approvedRemovalPaths)
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

    private static string HashFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
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

    private sealed record ManifestComponent(string RelativePath, string? Identity);

    private sealed record Manifest(
        IReadOnlyDictionary<string, ManifestComponent> Components)
    {
        public static Manifest Empty { get; } =
            new(new Dictionary<string, ManifestComponent>(StringComparer.Ordinal));
    }

    private sealed record TreeItem(string RelativePath, string? ContentHash);

    private sealed record PendingReplacement(string TemporaryPath, string DestinationPath);
}
