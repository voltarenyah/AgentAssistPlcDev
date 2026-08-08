using System.Text.Json;

namespace Agent.Workbench;

/// <summary>
/// Minimal Phase 3 recovery record, written to the git-ignored
/// &lt;worktree&gt;/.automation/pending-commit.json when the SVN native commit of a savepoint
/// succeeded but the git commit failed. The next commit on that worktree retries the git side
/// only, reusing the recorded SVN revision — never a second SVN snapshot.
/// </summary>
public sealed record PendingSvnCommit(string SvnUrl, long SvnRevision, string Status)
{
    public const string PendingGitCommit = "PENDING_GIT_COMMIT";
}

public static class PendingCommitStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string PathFor(string worktreeRoot) =>
        Path.Combine(worktreeRoot, ".automation", "pending-commit.json");

    public static PendingSvnCommit? Read(string worktreeRoot)
    {
        var path = PathFor(worktreeRoot);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var pending = JsonSerializer.Deserialize<PendingSvnCommit>(File.ReadAllText(path), Json);
            return pending is null || pending.Status != PendingSvnCommit.PendingGitCommit
                ? null
                : pending;
        }
        catch (JsonException exception)
        {
            throw new WorkbenchLifecycleException(
                "PENDING_COMMIT_INVALID",
                $"The pending commit record '{path}' is not valid JSON: {exception.Message}");
        }
    }

    public static void Write(string worktreeRoot, PendingSvnCommit pending)
    {
        ArgumentNullException.ThrowIfNull(pending);
        var path = PathFor(worktreeRoot);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".pending-commit.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(pending, Json));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public static void Clear(string worktreeRoot)
    {
        var path = PathFor(worktreeRoot);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
