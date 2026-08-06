using System.Collections.ObjectModel;
using Mcp.VersionControl.Git;
using SharpSvn;

namespace Mcp.VersionControl.Svn;

/// <summary>
/// Instance-based wrapper around SharpSvn for the native TIA project store
/// (repository.svn with a native/{main,branches} layout). Each operation opens its
/// own SvnClient and disposes it on return, so the class is safe for stateless MCP calls.
/// </summary>
internal sealed class SvnRepositoryService
{
    /// <summary>
    /// Create a local SVN repository at &lt;workbenchRoot&gt;/repository.svn and the
    /// initial native/main and native/branches directories. Returns the file:// URI.
    /// </summary>
    public SvnSharedInitResult CreateShared(string workbenchRoot)
    {
        if (string.IsNullOrWhiteSpace(workbenchRoot))
            throw new VcInternalException("PATH_REQUIRED", "workbenchRoot must not be empty.");

        var root = Path.GetFullPath(workbenchRoot);
        var repositoryPath = Path.Combine(root, "repository.svn");
        if (Directory.Exists(repositoryPath) && Directory.EnumerateFileSystemEntries(repositoryPath).Any())
        {
            throw new VcInternalException(
                "SVN_REPOSITORY_EXISTS",
                $"'{repositoryPath}' already exists and is not empty.",
                "Remove the existing repository.svn directory or choose an empty workbench root.");
        }

        var repositoryUri = PathToUri(repositoryPath);
        Run("SVN_INIT_FAILED", "Failed to create the shared SVN repository.", () =>
        {
            Directory.CreateDirectory(root);
            using (var repositoryClient = new SvnRepositoryClient())
            {
                repositoryClient.CreateRepository(repositoryPath, new SvnCreateRepositoryArgs());
            }

            using var client = CreateClient();
            var repositoryBase = new Uri(repositoryUri.ToString().TrimEnd('/') + "/");
            client.RemoteCreateDirectories(
                new Collection<Uri>
                {
                    new(repositoryBase, "native/main"),
                    new(repositoryBase, "native/branches"),
                },
                new SvnCreateDirectoryArgs
                {
                    CreateParents = true,
                    LogMessage = "Create native store layout",
                });
        });

        return new SvnSharedInitResult
        {
            RepositoryPath = repositoryPath,
            RepositoryUri = repositoryUri.ToString(),
            Initialized = true,
        };
    }

    /// <summary>
    /// Checkout a repository or branch URL into a local working copy. With
    /// <paramref name="allowObstructions"/>, the target may be non-empty (used to bring a
    /// freshly SaveAs'd TIA project under SVN control; only safe while the URL is empty).
    /// </summary>
    public SvnCheckoutResult Checkout(string url, string localPath, bool allowObstructions = false)
    {
        var uri = RequireUri(url, nameof(url));
        var path = RequirePath(localPath, nameof(localPath));

        SvnUpdateResult? update = null;
        Run("SVN_CHECKOUT_FAILED", $"Failed to check out '{url}'.", () =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var client = CreateClient();
            client.CheckOut(
                new SvnUriTarget(uri),
                path,
                new SvnCheckOutArgs { AllowObstructions = allowObstructions },
                out update);
        });

        return new SvnCheckoutResult
        {
            Path = path,
            Uri = uri.ToString(),
            Revision = update?.Revision ?? -1,
        };
    }

    /// <summary>Recursively add all unversioned items below a working copy path.</summary>
    public SvnAddResult AddRecursive(string localPath)
    {
        var path = RequirePath(localPath, nameof(localPath));
        Run("SVN_ADD_FAILED", $"Failed to add '{localPath}' to version control.", () =>
        {
            using var client = CreateClient();
            client.Add(path, new SvnAddArgs
            {
                Depth = SvnDepth.Infinity,
                Force = true,
            });
        });

        return new SvnAddResult { Path = path };
    }

    /// <summary>
    /// Commit a working copy. Returns the committed revision; when there is nothing to
    /// commit, returns the repository HEAD revision of the working copy's URL with
    /// Committed=false (SharpSvn reports success with a null result object then).
    /// </summary>
    public SvnCommitResult Commit(string localPath, string message)
    {
        var path = RequirePath(localPath, nameof(localPath));
        if (string.IsNullOrWhiteSpace(message))
            throw new VcInternalException("MESSAGE_REQUIRED", "message must not be empty.");

        SharpSvn.SvnCommitResult? result = null;
        Run("SVN_COMMIT_FAILED", $"Failed to commit '{localPath}'.", () =>
        {
            using var client = CreateClient();
            client.Commit(path, new SvnCommitArgs { LogMessage = message }, out result);
        });

        if (result is not null)
        {
            return new SvnCommitResult
            {
                Path = path,
                Committed = true,
                Revision = result.Revision,
            };
        }

        // No changes were committed: revision.json needs the revision the working copy
        // content corresponds to, i.e. the repository HEAD of its URL — not the working-copy
        // root's own (stale, mixed-revision) number from Info().
        var headRevision = 0L;
        var workingCopyUri = Info(path).Uri;
        Run("SVN_COMMIT_FAILED", $"Failed to read the repository head of '{localPath}'.", () =>
        {
            using var client = CreateClient();
            client.GetInfo(new SvnUriTarget(new Uri(workingCopyUri)), out var headInfo);
            headRevision = headInfo!.Revision;
        });

        return new SvnCommitResult
        {
            Path = path,
            Committed = false,
            Revision = headRevision,
        };
    }

    /// <summary>Working-copy status: clean/dirty plus the changed entries.</summary>
    public SvnStatusResult Status(string localPath)
    {
        var path = RequirePath(localPath, nameof(localPath));
        Collection<SvnStatusEventArgs>? entries = null;
        Run("SVN_STATUS_FAILED", $"Failed to read status of '{localPath}'.", () =>
        {
            using var client = CreateClient();
            client.GetStatus(path, new SvnStatusArgs { RetrieveAllEntries = false }, out entries);
        });

        var mapped = (entries ?? new Collection<SvnStatusEventArgs>())
            .Where(e => e.LocalContentStatus != SvnStatus.Ignored)
            .Select(e => new SvnStatusEntry
            {
                Path = RelativeTo(path, e.FullPath),
                NodeStatus = e.LocalContentStatus.ToString(),
                PropertyStatus = e.LocalPropertyStatus.ToString(),
            })
            .ToArray();

        return new SvnStatusResult
        {
            Path = path,
            IsClean = mapped.Length == 0,
            Entries = mapped,
        };
    }

    /// <summary>Log of a working-copy path or repository URL, newest first.</summary>
    public SvnLogResult Log(string pathOrUrl, int limit = 20)
    {
        if (string.IsNullOrWhiteSpace(pathOrUrl))
            throw new VcInternalException("PATH_REQUIRED", "pathOrUrl must not be empty.");
        if (limit <= 0)
            throw new VcInternalException("LIMIT_INVALID", "limit must be a positive number.");

        Collection<SvnLogEventArgs>? entries = null;
        Run("SVN_LOG_FAILED", $"Failed to read log of '{pathOrUrl}'.", () =>
        {
            using var client = CreateClient();
            var args = new SvnLogArgs { Limit = limit };
            if (IsUri(pathOrUrl))
                client.GetLog(new Uri(pathOrUrl), args, out entries);
            else
                client.GetLog(RequirePath(pathOrUrl, nameof(pathOrUrl)), args, out entries);
        });

        return new SvnLogResult
        {
            Entries = (entries ?? new Collection<SvnLogEventArgs>())
                .Select(e => new SvnLogEntry
                {
                    Revision = e.Revision,
                    Message = e.LogMessage ?? string.Empty,
                    Author = e.Author ?? string.Empty,
                    Time = e.Time,
                })
                .ToArray(),
        };
    }

    /// <summary>Repository URL and base revision of a working copy.</summary>
    public SvnInfoResult Info(string localPath)
    {
        var path = RequirePath(localPath, nameof(localPath));
        SvnInfoEventArgs? info = null;
        Run("SVN_INFO_FAILED", $"Failed to read info of '{localPath}'.", () =>
        {
            using var client = CreateClient();
            client.GetInfo(SvnTarget.FromString(path), out info);
        });

        return new SvnInfoResult
        {
            Path = path,
            Uri = info!.Uri?.ToString().TrimEnd('/') ?? string.Empty,
            Revision = info.Revision,
        };
    }

    /// <summary>
    /// Server-side copy of a branch URL at a peg revision into
    /// native/branches/&lt;newBranchName&gt; of the same repository.
    /// </summary>
    public SvnCopyBranchResult CopyBranch(string sourceBranchUrl, long pegRevision, string newBranchName, string message)
    {
        var sourceUri = RequireUri(sourceBranchUrl, nameof(sourceBranchUrl));
        if (pegRevision < 0)
            throw new VcInternalException("REVISION_INVALID", "pegRevision must be zero or greater.");
        if (string.IsNullOrWhiteSpace(newBranchName) ||
            newBranchName.Contains('/') || newBranchName.Contains('\\'))
            throw new VcInternalException("BRANCH_NAME_INVALID", "newBranchName must be a single path segment.");
        if (string.IsNullOrWhiteSpace(message))
            throw new VcInternalException("MESSAGE_REQUIRED", "message must not be empty.");

        var root = RepositoryRoot(sourceUri);
        var branchUri = new Uri(root, $"native/branches/{newBranchName}");

        SharpSvn.SvnCommitResult? result = null;
        Run("SVN_COPY_BRANCH_FAILED", $"Failed to copy '{sourceBranchUrl}' to branch '{newBranchName}'.", () =>
        {
            using var client = CreateClient();
            client.RemoteCopy(
                new SvnUriTarget(sourceUri, pegRevision),
                branchUri,
                new SvnCopyArgs { CreateParents = true, LogMessage = message },
                out result);
        });

        return new SvnCopyBranchResult
        {
            BranchUrl = branchUri.ToString(),
            Revision = result?.Revision ?? -1,
        };
    }

    /// <summary>Update a working copy to an exact revision.</summary>
    public SvnUpdateResultInfo UpdateToRevision(string localPath, long revision)
    {
        var path = RequirePath(localPath, nameof(localPath));
        if (revision < 0)
            throw new VcInternalException("REVISION_INVALID", "revision must be zero or greater.");

        SvnUpdateResult? update = null;
        Run("SVN_UPDATE_FAILED", $"Failed to update '{localPath}' to revision {revision}.", () =>
        {
            using var client = CreateClient();
            client.Update(path, new SvnUpdateArgs { Revision = new SvnRevision(revision) }, out update);
        });

        return new SvnUpdateResultInfo
        {
            Path = path,
            Revision = update?.Revision ?? -1,
        };
    }

    /* ── helpers ────────────────────────────────────────── */

    private static SvnClient CreateClient() => new();

    private static string RequirePath(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new VcInternalException("PATH_REQUIRED", $"{parameterName} must not be empty.");
        return Path.GetFullPath(path);
    }

    private static Uri RequireUri(string url, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(url) || !IsUri(url))
            throw new VcInternalException("URL_REQUIRED", $"{parameterName} must be a file:/// or http(s):// URL.");
        return new Uri(url);
    }

    private static bool IsUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeFile || uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps ||
         string.Equals(uri.Scheme, "svn", StringComparison.OrdinalIgnoreCase));

    /// <summary>Convert an absolute Windows path to a file:/// URI (forward slashes, escaped).</summary>
    private static Uri PathToUri(string path) => new(Path.GetFullPath(path));

    /// <summary>Repository root URI of a URL inside the native/ tree.</summary>
    private static Uri RepositoryRoot(Uri uri)
    {
        var text = uri.ToString();
        var marker = text.IndexOf("/native/", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
            throw new VcInternalException(
                "SVN_URL_OUTSIDE_NATIVE",
                $"'{uri}' is not inside a native/ tree.",
                "Use a URL below <repository>/native/, e.g. .../repository.svn/native/main.");
        return new Uri(text[..(marker + 1)]);
    }

    private static string RelativeTo(string root, string fullPath)
    {
        var relative = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        return relative == "." ? string.Empty : relative;
    }

    private static void Run(string code, string message, Action action)
    {
        try
        {
            action();
        }
        catch (VcInternalException)
        {
            throw;
        }
        catch (SvnException ex)
        {
            throw new VcInternalException(
                code,
                $"{message} {ex.Message}",
                RemediationFor(ex.SvnErrorCode));
        }
    }

    private static string RemediationFor(SvnErrorCode subErrorCode) => subErrorCode switch
    {
        SvnErrorCode.SVN_ERR_WC_NOT_DIRECTORY =>
            "The path is not an SVN working copy. Check out a working copy first.",
        SvnErrorCode.SVN_ERR_FS_ALREADY_EXISTS or SvnErrorCode.SVN_ERR_FS_NOT_FOUND =>
            "Check that the repository URL and branch names are correct.",
        SvnErrorCode.SVN_ERR_RA_LOCAL_REPOS_OPEN_FAILED or SvnErrorCode.SVN_ERR_RA_LOCAL_REPOS_NOT_FOUND =>
            "The local SVN repository is missing or locked. Verify the repository.svn path.",
        _ => string.Empty,
    };
}

internal sealed class SvnSharedInitResult
{
    public string RepositoryPath { get; set; } = string.Empty;
    public string RepositoryUri { get; set; } = string.Empty;
    public bool Initialized { get; set; }
}

internal sealed class SvnCheckoutResult
{
    public string Path { get; set; } = string.Empty;
    public string Uri { get; set; } = string.Empty;
    public long Revision { get; set; }
}

internal sealed class SvnAddResult
{
    public string Path { get; set; } = string.Empty;
}

internal sealed class SvnCommitResult
{
    public string Path { get; set; } = string.Empty;
    public bool Committed { get; set; }
    public long Revision { get; set; }
}

internal sealed class SvnStatusResult
{
    public string Path { get; set; } = string.Empty;
    public bool IsClean { get; set; }
    public SvnStatusEntry[] Entries { get; set; } = Array.Empty<SvnStatusEntry>();
}

internal sealed class SvnStatusEntry
{
    public string Path { get; set; } = string.Empty;
    public string NodeStatus { get; set; } = string.Empty;
    public string PropertyStatus { get; set; } = string.Empty;
}

internal sealed class SvnLogResult
{
    public SvnLogEntry[] Entries { get; set; } = Array.Empty<SvnLogEntry>();
}

internal sealed class SvnLogEntry
{
    public long Revision { get; set; }
    public string Message { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public DateTime Time { get; set; }
}

internal sealed class SvnInfoResult
{
    public string Path { get; set; } = string.Empty;
    public string Uri { get; set; } = string.Empty;
    public long Revision { get; set; }
}

internal sealed class SvnCopyBranchResult
{
    public string BranchUrl { get; set; } = string.Empty;
    public long Revision { get; set; }
}

internal sealed class SvnUpdateResultInfo
{
    public string Path { get; set; } = string.Empty;
    public long Revision { get; set; }
}
