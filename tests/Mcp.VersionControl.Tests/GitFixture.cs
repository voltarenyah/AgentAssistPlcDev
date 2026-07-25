using System;
using System.Collections.Concurrent;
using LibGit2Sharp;

namespace Mcp.VersionControl.Tests;

/// <summary>Creates and disposes temporary directory git repos for testing.</summary>
internal sealed class GitFixture : IDisposable
{
    private static readonly ConcurrentBag<string> CleanupPaths = new();

    public GitFixture()
    {
        RootPath = Path.Combine(Path.GetTempPath(), "McpVcTest", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);
        CleanupPaths.Add(RootPath);
    }

    public string RootPath { get; }

    /// <summary>Init a git repo in the fixture root and return the Repository.</summary>
    public Repository InitRepo()
    {
        Repository.Init(RootPath);
        return new Repository(RootPath);
    }

    /// <summary>Write a file to the repo root.</summary>
    public string WriteFile(string relativePath, string content = "hello")
    {
        var fullPath = Path.Combine(RootPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
        return fullPath;
    }

    /// <summary>Write and commit a file, returning the commit SHA. Inits the repo if needed.</summary>
    public string CommitFile(string relativePath, string content = "hello", string message = "initial commit")
    {
        WriteFile(relativePath, content);
        if (!Repository.IsValid(RootPath))
            Repository.Init(RootPath);
        using var repo = new Repository(RootPath);
        Commands.Stage(repo, relativePath);
        repo.Index.Write();
        var author = new Signature("Test", "test@test.local", DateTimeOffset.UtcNow);
        var commit = repo.Commit(message, author, author);
        return commit.Sha;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
