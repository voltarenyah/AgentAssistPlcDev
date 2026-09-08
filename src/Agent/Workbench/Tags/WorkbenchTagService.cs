namespace Agent.Workbench;

public sealed class WorkbenchTagDomainException : Exception
{
    public WorkbenchTagDomainException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}

/// <summary>Authoritative entity membership used by the tag service.</summary>
public interface IWorkbenchTagEntityLookup
{
    bool WorkbenchExists(string workbenchId);

    bool WorktreeBelongsToWorkbench(string workbenchId, string worktreeId);
}

/// <summary>Catalog-backed lookup adapter. The catalog remains the owner of entity lifecycle.</summary>
public sealed class WorkbenchCatalogTagEntityLookup : IWorkbenchTagEntityLookup
{
    private readonly IReadOnlyList<WorkbenchMetadata> _workbenches;

    public WorkbenchCatalogTagEntityLookup(WorkbenchCatalog catalog, IEnumerable<string> workbenchRoots)
        : this((workbenchRoots ?? throw new ArgumentNullException(nameof(workbenchRoots))).Select(catalog.Load))
    {
        ArgumentNullException.ThrowIfNull(catalog);
    }

    public WorkbenchCatalogTagEntityLookup(IEnumerable<WorkbenchMetadata> workbenches)
    {
        ArgumentNullException.ThrowIfNull(workbenches);
        _workbenches = workbenches.ToArray();
    }

    public bool WorkbenchExists(string workbenchId) => _workbenches.Any(workbench =>
        string.Equals(workbench.WorkbenchId, workbenchId, StringComparison.Ordinal));

    public bool WorktreeBelongsToWorkbench(string workbenchId, string worktreeId) => _workbenches.Any(workbench =>
        string.Equals(workbench.WorkbenchId, workbenchId, StringComparison.Ordinal)
        && workbench.Worktrees.Any(worktree =>
            string.Equals(worktree.WorktreeId, worktreeId, StringComparison.Ordinal)));
}

public sealed record WorkbenchTagProjection(
    IReadOnlyList<string> DirectTagIds,
    IReadOnlyList<string> EffectiveTagIds);

/// <summary>Owns taxonomy mutations while the store owns durable document concurrency.</summary>
public sealed class WorkbenchTagService
{
    private readonly WorkbenchTagStore _store;
    private readonly IWorkbenchTagEntityLookup _entityLookup;

    public WorkbenchTagService(WorkbenchTagStore store)
        : this(store, new MissingEntityLookup())
    {
    }

    public WorkbenchTagService(WorkbenchTagStore store, IWorkbenchTagEntityLookup entityLookup)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _entityLookup = entityLookup ?? throw new ArgumentNullException(nameof(entityLookup));
    }

    public void AssignWorkbenchTag(string tagId, string workbenchId)
    {
        ValidateId(workbenchId, "workbenchId");
        if (!_entityLookup.WorkbenchExists(workbenchId))
        {
            throw new WorkbenchTagDomainException("workbench_not_found", $"Workbench '{workbenchId}' was not found.");
        }

        Assign(tagId, new TagAssignment(tagId, TagEntityType.Workbench, workbenchId, null));
    }

    public void UnassignWorkbenchTag(string tagId, string workbenchId)
    {
        ValidateId(workbenchId, "workbenchId");
        if (!_entityLookup.WorkbenchExists(workbenchId))
        {
            throw new WorkbenchTagDomainException("workbench_not_found", $"Workbench '{workbenchId}' was not found.");
        }

        Unassign(tagId, TagEntityType.Workbench, workbenchId, null);
    }

    public void AssignWorktreeTag(string tagId, string workbenchId, string worktreeId)
    {
        ValidateId(workbenchId, "workbenchId");
        ValidateId(worktreeId, "worktreeId");
        if (!_entityLookup.WorktreeBelongsToWorkbench(workbenchId, worktreeId))
        {
            throw new WorkbenchTagDomainException(
                "worktree_not_found",
                $"Worktree '{worktreeId}' was not found in workbench '{workbenchId}'.");
        }

        Assign(tagId, new TagAssignment(tagId, TagEntityType.Worktree, worktreeId, workbenchId));
    }

    public void UnassignWorktreeTag(string tagId, string workbenchId, string worktreeId)
    {
        ValidateId(workbenchId, "workbenchId");
        ValidateId(worktreeId, "worktreeId");
        if (!_entityLookup.WorktreeBelongsToWorkbench(workbenchId, worktreeId))
        {
            throw new WorkbenchTagDomainException(
                "worktree_not_found",
                $"Worktree '{worktreeId}' was not found in workbench '{workbenchId}'.");
        }

        Unassign(tagId, TagEntityType.Worktree, worktreeId, workbenchId);
    }

    public WorkbenchTagProjection GetWorkbenchTags(string workbenchId)
    {
        ValidateId(workbenchId, "workbenchId");
        EnsureWorkbench(workbenchId);
        var direct = DirectTagIds(TagEntityType.Workbench, workbenchId, null);
        return new(direct, direct);
    }

    public WorkbenchTagProjection GetWorktreeTags(string workbenchId, string worktreeId)
    {
        ValidateId(workbenchId, "workbenchId");
        ValidateId(worktreeId, "worktreeId");
        if (!_entityLookup.WorktreeBelongsToWorkbench(workbenchId, worktreeId))
        {
            throw new WorkbenchTagDomainException(
                "worktree_not_found",
                $"Worktree '{worktreeId}' was not found in workbench '{workbenchId}'.");
        }

        var document = _store.Load();
        var direct = document.Assignments
            .Where(assignment => assignment.EntityType == TagEntityType.Worktree
                && string.Equals(assignment.EntityId, worktreeId, StringComparison.Ordinal)
                && string.Equals(assignment.WorkbenchId, workbenchId, StringComparison.Ordinal))
            .Select(assignment => assignment.TagId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var inherited = document.Assignments
            .Where(assignment => assignment.EntityType == TagEntityType.Workbench
                && string.Equals(assignment.EntityId, workbenchId, StringComparison.Ordinal))
            .Select(assignment => assignment.TagId);
        return new(direct, inherited.Concat(direct).Distinct(StringComparer.Ordinal).ToArray());
    }

    private void Assign(string tagId, TagAssignment assignment)
    {
        ValidateId(tagId, "tagId");
        _store.Mutate(document =>
        {
            _ = FindNode(document, tagId);
            if (document.Assignments.Any(existing => existing == assignment))
            {
                return document;
            }

            return document with { Assignments = document.Assignments.Append(assignment).ToList() };
        });
    }

    private void Unassign(string tagId, TagEntityType entityType, string entityId, string? workbenchId)
    {
        ValidateId(tagId, "tagId");
        _store.Mutate(document => document with
        {
            Assignments = document.Assignments.Where(assignment =>
                !(assignment.TagId == tagId
                && assignment.EntityType == entityType
                && assignment.EntityId == entityId
                && assignment.WorkbenchId == workbenchId)).ToList(),
        });
    }

    private IReadOnlyList<string> DirectTagIds(TagEntityType entityType, string entityId, string? workbenchId) =>
        _store.Load().Assignments
            .Where(assignment => assignment.EntityType == entityType
                && assignment.EntityId == entityId
                && assignment.WorkbenchId == workbenchId)
            .Select(assignment => assignment.TagId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private void EnsureWorkbench(string workbenchId)
    {
        if (!_entityLookup.WorkbenchExists(workbenchId))
        {
            throw new WorkbenchTagDomainException("workbench_not_found", $"Workbench '{workbenchId}' was not found.");
        }
    }

    private static void ValidateId(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new WorkbenchTagDomainException("invalid_entity_id", $"{name} is required.");
        }
    }

    private sealed class MissingEntityLookup : IWorkbenchTagEntityLookup
    {
        public bool WorkbenchExists(string workbenchId) => false;
        public bool WorktreeBelongsToWorkbench(string workbenchId, string worktreeId) => false;
    }

    public TagNode CreatePath(string path)
    {
        var segments = ParsePath(path);
        TagNode? result = null;
        _store.Mutate(document =>
        {
            var nodes = document.Nodes.ToList();
            string? parentId = null;
            foreach (var segment in segments)
            {
                var normalized = NormalizeName(segment);
                var existing = nodes.SingleOrDefault(node =>
                    string.Equals(node.ParentTagId, parentId, StringComparison.Ordinal)
                    && string.Equals(node.NormalizedName, normalized, StringComparison.Ordinal));
                if (existing is null)
                {
                    existing = new TagNode(Guid.NewGuid().ToString("N"), parentId, segment, normalized);
                    nodes.Add(existing);
                }

                parentId = existing.TagId;
                result = existing;
            }

            return document with { Nodes = nodes };
        });

        return result!;
    }

    public TagNode Rename(string tagId, string name)
    {
        var displayName = NormalizeDisplayName(name);
        var normalized = NormalizeName(displayName);
        TagNode? result = null;
        _store.Mutate(document =>
        {
            var node = FindNode(document, tagId);
            if (document.Nodes.Any(candidate =>
                    candidate.TagId != node.TagId
                    && string.Equals(candidate.ParentTagId, node.ParentTagId, StringComparison.Ordinal)
                    && string.Equals(candidate.NormalizedName, normalized, StringComparison.Ordinal)))
            {
                throw new WorkbenchTagDomainException(
                    "sibling_name_conflict",
                    $"A sibling tag named '{displayName}' already exists.");
            }

            result = node with { Name = displayName, NormalizedName = normalized };
            return document with
            {
                Nodes = document.Nodes.Select(candidate => candidate.TagId == tagId ? result : candidate).ToList(),
            };
        });

        return result!;
    }

    public void Delete(string tagId)
    {
        _store.Mutate(document =>
        {
            _ = FindNode(document, tagId);
            if (document.Nodes.Any(node => string.Equals(node.ParentTagId, tagId, StringComparison.Ordinal)))
            {
                throw new WorkbenchTagDomainException(
                    "tag_has_children",
                    $"Tag '{tagId}' cannot be deleted while it has children.");
            }

            if (document.Assignments.Any(assignment => string.Equals(assignment.TagId, tagId, StringComparison.Ordinal)))
            {
                throw new WorkbenchTagDomainException(
                    "tag_assigned",
                    $"Tag '{tagId}' cannot be deleted while it has assignments.");
            }

            return document with { Nodes = document.Nodes.Where(node => node.TagId != tagId).ToList() };
        });
    }

    public string GetPath(string tagId)
    {
        var document = _store.Load();
        var nodes = document.Nodes.ToDictionary(node => node.TagId, StringComparer.Ordinal);
        var node = FindNode(document, tagId);
        var segments = new List<string>();
        var current = node;
        while (true)
        {
            segments.Add(current.Name);
            if (current.ParentTagId is null)
            {
                break;
            }

            var parentId = current.ParentTagId;
            if (!nodes.TryGetValue(parentId, out current!))
            {
                throw new WorkbenchTagDomainException(
                    "invalid_document",
                    $"Tag '{node.TagId}' references missing parent '{parentId}'.");
            }
        }

        segments.Reverse();
        return string.Join('/', segments);
    }

    private static TagNode FindNode(WorkbenchTagDocument document, string tagId) =>
        document.Nodes.SingleOrDefault(node => string.Equals(node.TagId, tagId, StringComparison.Ordinal))
        ?? throw new WorkbenchTagDomainException("tag_not_found", $"Tag '{tagId}' was not found.");

    private static IReadOnlyList<string> ParsePath(string path)
    {
        if (path is null)
        {
            throw new WorkbenchTagDomainException("invalid_path", "Tag path is required.");
        }

        var parts = path.Split('/');
        var first = 0;
        var last = parts.Length - 1;
        while (first <= last && string.IsNullOrWhiteSpace(parts[first]))
        {
            first++;
        }

        while (last >= first && string.IsNullOrWhiteSpace(parts[last]))
        {
            last--;
        }

        if (first > last)
        {
            throw new WorkbenchTagDomainException("invalid_path", "Tag path must contain a non-empty segment.");
        }

        var segments = new List<string>(last - first + 1);
        for (var index = first; index <= last; index++)
        {
            var segment = parts[index].Trim();
            if (segment.Length == 0)
            {
                throw new WorkbenchTagDomainException("invalid_path", "Tag paths cannot contain empty interior segments.");
            }

            segments.Add(segment);
        }

        return segments;
    }

    private static string NormalizeDisplayName(string name)
    {
        if (name is null)
        {
            throw new WorkbenchTagDomainException("invalid_name", "Tag name is required.");
        }

        var displayName = name.Trim();
        if (displayName.Length == 0 || displayName.Contains('/'))
        {
            throw new WorkbenchTagDomainException("invalid_name", "Tag name must be non-empty and cannot contain '/'.");
        }

        return displayName;
    }

    private static string NormalizeName(string name) => name.ToLowerInvariant();
}
