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

/// <summary>Owns taxonomy mutations while the store owns durable document concurrency.</summary>
public sealed class WorkbenchTagService
{
    private readonly WorkbenchTagStore _store;

    public WorkbenchTagService(WorkbenchTagStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
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
