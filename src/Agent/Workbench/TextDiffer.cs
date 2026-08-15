namespace Agent.Workbench;

/// <summary>One line of a line-based text diff. Kind is "same", "added" (only in the new/TIA
/// side), or "removed" (only in the old/local side).</summary>
public sealed record DiffLine(string Kind, string Text)
{
    public const string Same = "same";
    public const string Added = "added";
    public const string Removed = "removed";
}

/// <summary>
/// Line-based diff for PLC source XML comparison (device-scoped Compare with TIA). Callers
/// normalize the inputs first (<see cref="Contracts.Engineering.XmlCompare.Normalize"/> strips
/// &lt;Created&gt; timestamp lines and CR) so only real content changes surface.
/// Common prefixes/suffixes are trimmed before the LCS pass; when the differing middle is too
/// large for a full matrix the whole middle is reported as removed+added instead of allocating
/// quadratic memory.
/// </summary>
public static class TextDiffer
{
    /// <summary>Max LCS matrix cells before falling back to a whole-middle replacement.</summary>
    private const long MaxMatrixCells = 4_000_000;

    public static IReadOnlyList<DiffLine> Diff(string oldText, string newText)
    {
        var oldLines = SplitLines(oldText);
        var newLines = SplitLines(newText);

        var prefix = 0;
        while (prefix < oldLines.Length && prefix < newLines.Length
            && string.Equals(oldLines[prefix], newLines[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < oldLines.Length - prefix && suffix < newLines.Length - prefix
            && string.Equals(
                oldLines[oldLines.Length - 1 - suffix],
                newLines[newLines.Length - 1 - suffix],
                StringComparison.Ordinal))
        {
            suffix++;
        }

        var oldMiddle = oldLines[prefix..(oldLines.Length - suffix)];
        var newMiddle = newLines[prefix..(newLines.Length - suffix)];

        var result = new List<DiffLine>(prefix + suffix + Math.Max(oldMiddle.Length, newMiddle.Length));
        foreach (var line in oldLines[..prefix])
        {
            result.Add(new DiffLine(DiffLine.Same, line));
        }

        if ((long)oldMiddle.Length * newMiddle.Length <= MaxMatrixCells)
        {
            DiffMiddle(oldMiddle, newMiddle, result);
        }
        else
        {
            foreach (var line in oldMiddle)
            {
                result.Add(new DiffLine(DiffLine.Removed, line));
            }

            foreach (var line in newMiddle)
            {
                result.Add(new DiffLine(DiffLine.Added, line));
            }
        }

        foreach (var line in oldLines[(oldLines.Length - suffix)..])
        {
            result.Add(new DiffLine(DiffLine.Same, line));
        }

        return result;
    }

    private static void DiffMiddle(string[] oldMiddle, string[] newMiddle, List<DiffLine> result)
    {
        var lengths = new int[oldMiddle.Length + 1, newMiddle.Length + 1];
        for (var i = oldMiddle.Length - 1; i >= 0; i--)
        {
            for (var j = newMiddle.Length - 1; j >= 0; j--)
            {
                lengths[i, j] = string.Equals(oldMiddle[i], newMiddle[j], StringComparison.Ordinal)
                    ? lengths[i + 1, j + 1] + 1
                    : Math.Max(lengths[i + 1, j], lengths[i, j + 1]);
            }
        }

        var x = 0;
        var y = 0;
        while (x < oldMiddle.Length && y < newMiddle.Length)
        {
            if (string.Equals(oldMiddle[x], newMiddle[y], StringComparison.Ordinal))
            {
                result.Add(new DiffLine(DiffLine.Same, oldMiddle[x]));
                x++;
                y++;
            }
            else if (lengths[x + 1, y] >= lengths[x, y + 1])
            {
                result.Add(new DiffLine(DiffLine.Removed, oldMiddle[x]));
                x++;
            }
            else
            {
                result.Add(new DiffLine(DiffLine.Added, newMiddle[y]));
                y++;
            }
        }

        while (x < oldMiddle.Length)
        {
            result.Add(new DiffLine(DiffLine.Removed, oldMiddle[x]));
            x++;
        }

        while (y < newMiddle.Length)
        {
            result.Add(new DiffLine(DiffLine.Added, newMiddle[y]));
            y++;
        }
    }

    private static string[] SplitLines(string text) =>
        text.Replace("\r", "").Split('\n');
}
