namespace Agent.Workbench;

public sealed class WorkbenchCatalogException : Exception
{
    public WorkbenchCatalogException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

public sealed class WorkbenchCatalog
{
    private readonly AtomicJsonStore _store;
    private readonly string _defaultRoot;

    public WorkbenchCatalog()
        : this(new AtomicJsonStore(), defaultRoot: null)
    {
    }

    public WorkbenchCatalog(AtomicJsonStore store, string? defaultRoot = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        _defaultRoot = Path.GetFullPath(
            defaultRoot
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AutomationWorkbench",
                "Project"));
    }

    public WorkbenchMetadata Create(string name, string? requestedRoot)
    {
        var root = requestedRoot is null
            ? ResolveDefaultWorkbenchRoot(name)
            : WorkbenchPaths.ResolveWorkbench(name, requestedRoot);

        if (File.Exists(root)
            || (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()))
        {
            throw new WorkbenchCatalogException(
                "WORKBENCH_CONFLICT",
                $"Workbench root '{root}' already exists and is not empty.");
        }

        var createdDirectories = new List<string>();
        try
        {
            CreateDirectory(root, createdDirectories);
            CreateDirectory(Path.Combine(root, "worktrees"), createdDirectories);

            var metadata = new WorkbenchMetadata(
                WorkbenchSchema.CurrentVersion,
                Guid.NewGuid().ToString("N"),
                name,
                DateTimeOffset.UtcNow.ToString("O"),
                root,
                Path.Combine(root, "repository.git"),
                null,
                null,
                Array.Empty<WorkbenchWorktreeRegistration>(),
                SvnRepositoryPath: Path.Combine(root, "repository.svn"));

            _store.Write(MetadataPath(root), metadata);
            return metadata;
        }
        catch
        {
            RemoveEmptyCreatedDirectories(createdDirectories);
            throw;
        }
    }

    public WorkbenchMetadata Load(string workbenchRoot)
    {
        var root = WorkbenchPaths.ResolveWorkbench("workbench", workbenchRoot);
        var metadataPath = MetadataPath(root);
        if (!File.Exists(metadataPath))
        {
            throw new WorkbenchCatalogException(
                "WORKBENCH_NOT_FOUND",
                $"Workbench metadata was not found at '{metadataPath}'.");
        }

        var metadata = _store.Read<WorkbenchMetadata>(metadataPath);
        if (!string.Equals(
                Path.GetFullPath(metadata.RootPath).TrimEnd(Path.DirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkbenchCatalogException(
                "WORKBENCH_RELATIONSHIP_MISMATCH",
                $"Workbench metadata root '{metadata.RootPath}' does not match its directory '{root}'.");
        }

        return metadata;
    }

    public void RollbackCreate(WorkbenchMetadata workbench)
    {
        ArgumentNullException.ThrowIfNull(workbench);

        var root = WorkbenchPaths.ResolveWorkbench("workbench", workbench.RootPath);
        DeleteDirectoryIfPresent(Path.Combine(root, "repository.git"));
        // SVN pristine files and the tia/ working copy may be read-only; clear attributes
        // before the recursive delete (the tia/ store lives under worktrees/).
        var svnRepository = Path.Combine(root, "repository.svn");
        ClearReadOnlyAttributes(svnRepository);
        DeleteDirectoryIfPresent(svnRepository);
        var worktrees = Path.Combine(root, "worktrees");
        ClearReadOnlyAttributes(worktrees);
        DeleteDirectoryIfPresent(worktrees);

        var metadataPath = MetadataPath(root);
        if (File.Exists(metadataPath))
        {
            File.Delete(metadataPath);
        }
    }

    /// <summary>
    /// Permanently deletes a registered workbench root and everything beneath it
    /// (workbench.json, worktrees/, repository.git). The persisted metadata is
    /// re-loaded and identity-checked first, so only the catalog-registered path
    /// can be removed.
    /// </summary>
    public void Delete(WorkbenchMetadata workbench)
    {
        ArgumentNullException.ThrowIfNull(workbench);

        var root = WorkbenchPaths.ResolveWorkbench("workbench", workbench.RootPath);
        var persisted = Load(root);
        if (!string.Equals(persisted.WorkbenchId, workbench.WorkbenchId, StringComparison.Ordinal))
        {
            throw new WorkbenchCatalogException(
                "WORKBENCH_RELATIONSHIP_MISMATCH",
                "Workbench metadata does not match the persisted catalog entry.");
        }

        ClearReadOnlyAttributes(root);
        DeleteDirectoryIfPresent(root);
    }

    public IReadOnlyList<WorkbenchMetadata> ListDefaultRoot()
    {
        if (!Directory.Exists(_defaultRoot))
        {
            return Array.Empty<WorkbenchMetadata>();
        }

        return Directory.EnumerateDirectories(_defaultRoot)
            .Where(directory => File.Exists(MetadataPath(directory)))
            .Select(Load)
            .OrderBy(workbench => workbench.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(workbench => workbench.WorkbenchId, StringComparer.Ordinal)
            .ToArray();
    }

    public WorkbenchMetadata RegisterWorktree(
        WorkbenchMetadata workbench,
        WorkbenchWorktreeRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(workbench);
        ArgumentNullException.ThrowIfNull(registration);

        if (workbench.Worktrees.Any(existing =>
                string.Equals(
                    existing.WorktreeId,
                    registration.WorktreeId,
                    StringComparison.Ordinal)))
        {
            throw new WorkbenchCatalogException(
                "WORKTREE_CONFLICT",
                $"Workbench '{workbench.WorkbenchId}' already contains worktree '{registration.WorktreeId}'.");
        }

        var updated = workbench with
        {
            Worktrees = workbench.Worktrees.Append(registration).ToArray(),
        };

        _store.Write(MetadataPath(workbench.RootPath), updated);
        return updated;
    }

    public WorkbenchMetadata RemoveWorktree(WorkbenchMetadata workbench, string worktreeId)
    {
        ArgumentNullException.ThrowIfNull(workbench);

        var root = WorkbenchPaths.ResolveWorkbench("workbench", workbench.RootPath);
        var persisted = Load(root);
        if (!string.Equals(persisted.WorkbenchId, workbench.WorkbenchId, StringComparison.Ordinal))
        {
            throw new WorkbenchCatalogException(
                "WORKBENCH_RELATIONSHIP_MISMATCH",
                "Workbench metadata does not match the persisted catalog entry.");
        }

        var registration = persisted.Worktrees.SingleOrDefault(candidate =>
            string.Equals(candidate.WorktreeId, worktreeId, StringComparison.Ordinal));
        if (registration is null)
        {
            throw new WorkbenchCatalogException(
                "WORKTREE_NOT_FOUND",
                $"Workbench '{persisted.WorkbenchId}' does not contain worktree '{worktreeId}'.");
        }

        if (string.Equals(registration.Branch, "master", StringComparison.OrdinalIgnoreCase))
        {
            throw new WorkbenchCatalogException(
                "MASTER_WORKTREE_PROTECTED",
                "The master worktree is the workbench baseline and cannot be removed.");
        }

        var worktreeRoot = WorkbenchPaths.ResolveWorktree(persisted.RootPath, registration.RelativePath);
        ClearReadOnlyAttributes(worktreeRoot);
        DeleteDirectoryIfPresent(worktreeRoot);

        var updated = persisted with
        {
            Worktrees = persisted.Worktrees
                .Where(candidate => !string.Equals(candidate.WorktreeId, worktreeId, StringComparison.Ordinal))
                .ToArray(),
        };
        _store.Write(MetadataPath(persisted.RootPath), updated);
        return updated;
    }

    /// <summary>Persists the landing-page info fields (purpose/owner) of a workbench.</summary>
    public WorkbenchMetadata UpdateWorkbenchInfo(
        WorkbenchMetadata workbench,
        string? purpose,
        string? owner)
    {
        ArgumentNullException.ThrowIfNull(workbench);

        var updated = workbench with { Purpose = purpose, Owner = owner };
        _store.Write(MetadataPath(workbench.RootPath), updated);
        return updated;
    }

    /// <summary>Persists a worktree.json update (landing-page info fields, status). The
    /// caller supplies the fully-updated metadata; identity is checked against the catalog.</summary>
    public WorktreeMetadata UpdateWorktreeInfo(
        WorkbenchMetadata workbench,
        WorktreeMetadata worktree)
    {
        ArgumentNullException.ThrowIfNull(workbench);
        ArgumentNullException.ThrowIfNull(worktree);

        var registration = workbench.Worktrees.SingleOrDefault(candidate =>
            string.Equals(
                candidate.WorktreeId,
                worktree.WorktreeId,
                StringComparison.Ordinal));
        if (registration is null)
        {
            throw new WorkbenchCatalogException(
                "WORKTREE_NOT_FOUND",
                $"Workbench '{workbench.WorkbenchId}' does not contain worktree '{worktree.WorktreeId}'.");
        }

        var worktreeRoot = WorkbenchPaths.ResolveWorktree(
            workbench.RootPath,
            registration.RelativePath);
        _store.Write(Path.Combine(worktreeRoot, "worktree.json"), worktree);
        return worktree;
    }

    public DeviceContext ResolveDevice(
        WorkbenchMetadata workbench,
        WorktreeMetadata worktree,
        DeviceMetadata device)
    {
        ArgumentNullException.ThrowIfNull(workbench);
        ArgumentNullException.ThrowIfNull(worktree);
        ArgumentNullException.ThrowIfNull(device);

        if (!string.Equals(
                workbench.WorkbenchId,
                worktree.WorkbenchId,
                StringComparison.Ordinal)
            || !string.Equals(
                worktree.WorktreeId,
                device.WorktreeId,
                StringComparison.Ordinal)
            || !worktree.DeviceIds.Contains(device.DeviceId, StringComparer.Ordinal))
        {
            throw new WorkbenchCatalogException(
                "WORKBENCH_RELATIONSHIP_MISMATCH",
                "Workbench, worktree, and device metadata do not describe the same registered context.");
        }

        var registration = workbench.Worktrees.SingleOrDefault(candidate =>
            string.Equals(
                candidate.WorktreeId,
                worktree.WorktreeId,
                StringComparison.Ordinal));
        if (registration is null)
        {
            throw new WorkbenchCatalogException(
                "WORKTREE_NOT_FOUND",
                $"Workbench '{workbench.WorkbenchId}' does not contain worktree '{worktree.WorktreeId}'.");
        }

        return WorkbenchPaths.ResolveDevice(
            workbench.WorkbenchId,
            workbench.RootPath,
            worktree.WorktreeId,
            registration.RelativePath,
            device.DeviceId,
            device.PlcName);
    }

    private string ResolveDefaultWorkbenchRoot(string name)
    {
        var sanitizedName = Path.GetFileName(WorkbenchPaths.DefaultRoot(name));
        return WorkbenchPaths.ResolveWorkbench(
            name,
            Path.Combine(_defaultRoot, sanitizedName));
    }

    private static string MetadataPath(string root) =>
        Path.Combine(root, "workbench.json");

    private static void CreateDirectory(string path, ICollection<string> createdDirectories)
    {
        var missing = new Stack<string>();
        var current = Path.GetFullPath(path);

        while (!Directory.Exists(current))
        {
            missing.Push(current);
            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || string.Equals(parent, current, StringComparison.Ordinal))
            {
                break;
            }

            current = parent;
        }

        Directory.CreateDirectory(path);
        while (missing.TryPop(out var created))
        {
            createdDirectories.Add(created);
        }
    }

    private static void RemoveEmptyCreatedDirectories(
        IEnumerable<string> createdDirectories)
    {
        foreach (var directory in createdDirectories.Reverse())
        {
            if (Directory.Exists(directory)
                && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
    }

    /// <summary>Git object and pack files are read-only on Windows; reset attributes so the recursive delete succeeds.</summary>
    internal static void ClearReadOnlyAttributes(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        {
            if (!File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint))
            {
                File.SetAttributes(entry, FileAttributes.Normal);
            }
        }
    }

    private static void DeleteDirectoryIfPresent(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        var attributes = File.GetAttributes(path);
        Directory.Delete(path, recursive: !attributes.HasFlag(FileAttributes.ReparsePoint));
    }
}
