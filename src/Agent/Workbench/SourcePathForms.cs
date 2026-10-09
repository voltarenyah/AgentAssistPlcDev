namespace Agent.Workbench;

/// <summary>
/// One source object has two recorded path forms. A device's source objects are registered relative to
/// that device's source root ("Blocks/Group/Block [FB1].xml"), while a commit selects paths relative to
/// the worktree ("devices/PLC_1/source/Blocks/Group/Block [FB1].xml"). Comparing one form with the other
/// matches nothing and used to fail silently, so both sides are resolved through here (item 015).
/// </summary>
public static class SourcePathForms
{
    /// <summary>The worktree-relative path a commit contains for one registered source object.</summary>
    public static string CommitPath(string sourceRootRelativeToWorktree, string objectPath)
    {
        var prefix = Normalize(sourceRootRelativeToWorktree).Trim('/');
        var relative = Normalize(objectPath).TrimStart('/');
        return prefix.Length == 0 ? relative : prefix + "/" + relative;
    }

    /// <summary>
    /// True when a worktree-relative commit path names this registered object, in either recorded form:
    /// resolved against the device source root, or already recorded worktree-relative by an older
    /// registration.
    /// </summary>
    public static bool Matches(string sourceRootRelativeToWorktree, string objectPath, string commitPath)
    {
        var candidate = Normalize(commitPath);
        return string.Equals(candidate, CommitPath(sourceRootRelativeToWorktree, objectPath), StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate, Normalize(objectPath).TrimStart('/'), StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
}
