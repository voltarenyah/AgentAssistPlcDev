using System.Security.Cryptography;
using System.Text;
using Contracts.Engineering;
using LibGit2Sharp;

namespace Mcp.VersionControl.Git;

/// <summary>
/// Builds a prospective merge tree without checking out, merging, or moving any ref.
/// Only tracked PLC source XML blobs are exposed as candidate objects.
/// </summary>
internal static class MergePreviewService
{
    public static VcMergePreviewResult Preview(string repoPath, string sourceBranch)
    {
        if (string.IsNullOrWhiteSpace(sourceBranch))
            throw new VcInternalException("BRANCH_REQUIRED", "sourceBranch must not be empty.");

        using var repo = new Repository(repoPath);
        var target = repo.Head?.Tip
            ?? throw new VcInternalException("HEAD_REQUIRED", "Merge preview requires a repository with a current HEAD.");
        var source = repo.Branches[sourceBranch]?.Tip;
        if (source == null)
        {
            throw new VcInternalException(
                "BRANCH_NOT_FOUND",
                $"Branch '{sourceBranch}' was not found.");
        }

        var mergeBase = repo.ObjectDatabase.FindMergeBase(target, source);
        if (mergeBase == null)
        {
            throw new VcInternalException(
                "MERGE_BASE_NOT_FOUND",
                $"Branches '{repo.Head.FriendlyName}' and '{sourceBranch}' have no merge base.");
        }

        var featurePaths = GetFeaturePaths(repo, mergeBase, source);
        var merge = repo.ObjectDatabase.MergeCommits(target, source, new MergeTreeOptions());
        var conflicts = merge.Conflicts
            .Select(GetConflictPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!.Replace('\\', '/'))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        if (merge.Status == MergeTreeStatus.Conflicts || conflicts.Length > 0)
        {
            return new VcMergePreviewResult(
                repo.Head.FriendlyName,
                sourceBranch,
                mergeBase.Sha,
                target.Sha,
                source.Sha,
                null,
                true,
                conflicts,
                featurePaths,
                Array.Empty<VcTreeObject>());
        }

        var objects = EnumerateSourceObjects(merge.Tree).ToArray();
        return new VcMergePreviewResult(
            repo.Head.FriendlyName,
            sourceBranch,
            mergeBase.Sha,
            target.Sha,
            source.Sha,
            merge.Tree.Sha,
            false,
            Array.Empty<string>(),
            featurePaths,
            objects);
    }

    private static string[] GetFeaturePaths(Repository repo, Commit mergeBase, Commit source)
    {
        using var changes = repo.Diff.Compare<TreeChanges>(mergeBase.Tree, source.Tree);
        return changes
            .SelectMany(change => new[] { change.Path, change.OldPath })
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!.Replace('\\', '/'))
            .Where(SourcePathPolicy.IsAllowed)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<VcTreeObject> EnumerateSourceObjects(Tree tree, string prefix = "")
    {
        foreach (var entry in tree)
        {
            var path = string.IsNullOrEmpty(prefix)
                ? entry.Name
                : $"{prefix}/{entry.Name}";
            if (entry.Target is Tree child)
            {
                foreach (var item in EnumerateSourceObjects(child, path))
                    yield return item;
                continue;
            }

            if (entry.Target is not Blob blob || !SourcePathPolicy.IsAllowed(path))
                continue;

            var bytes = ReadBlob(blob);
            var xml = DecodeXml(bytes, path);
            var normalized = XmlCompare.Normalize(xml);
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
                .ToLowerInvariant();
            yield return new VcTreeObject(path, hash, bytes.LongLength);
        }
    }

    private static byte[] ReadBlob(Blob blob)
    {
        using var input = blob.GetContentStream();
        using var output = new MemoryStream();
        input.CopyTo(output);
        return output.ToArray();
    }

    private static string DecodeXml(byte[] bytes, string path)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (DecoderFallbackException exception)
        {
            throw new VcInternalException(
                "SOURCE_XML_UNREADABLE",
                $"Tracked source XML '{path}' is not valid UTF text.",
                exception.Message);
        }
    }

    private static string? GetConflictPath(Conflict conflict) =>
        conflict.Ours?.Path ?? conflict.Theirs?.Path ?? conflict.Ancestor?.Path;
}
