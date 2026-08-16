namespace Contracts.Engineering;

/// <summary>Named TIA fingerprint values. The metadata serializer writes this as a JSON object
/// while still accepting the legacy canonical string representation.</summary>
public sealed class FingerprintSet : Dictionary<string, string>
{
    public FingerprintSet()
        : base(StringComparer.Ordinal)
    {
    }

    public FingerprintSet(IEnumerable<KeyValuePair<string, string>> values)
        : this()
    {
        foreach (var pair in values)
        {
            Add(pair.Key, pair.Value);
        }
    }

    public static FingerprintSet? Parse(string? canonical)
    {
        if (string.IsNullOrWhiteSpace(canonical))
        {
            return null;
        }

        var result = new FingerprintSet();
        foreach (var part in canonical!.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0 || separator == part.Length - 1)
            {
                return null;
            }

            var key = part.Substring(0, separator);
            var value = part.Substring(separator + 1);
            if (result.ContainsKey(key))
            {
                return null;
            }

            result[key] = value;
        }

        return result.Count == 0 ? null : result;
    }

    public string ToCanonicalString() => string.Join(
        ";",
        this.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={pair.Value}"));
}

public sealed class FingerprintComponentComparison
{
    public string? Stored { get; set; }
    public string? Live { get; set; }
    public bool? Matches { get; set; }
}

public static class FingerprintComparison
{
    public static Dictionary<string, FingerprintComponentComparison>? Compare(
        FingerprintSet? stored,
        FingerprintSet? live)
    {
        if (stored is null && live is null)
        {
            return null;
        }

        var keys = (stored?.Keys ?? Enumerable.Empty<string>())
            .Concat(live?.Keys ?? Enumerable.Empty<string>())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal);
        var result = new Dictionary<string, FingerprintComponentComparison>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            string? storedValue = null;
            string? liveValue = null;
            stored?.TryGetValue(key, out storedValue);
            live?.TryGetValue(key, out liveValue);
            result[key] = new FingerprintComponentComparison
            {
                Stored = storedValue,
                Live = liveValue,
                Matches = storedValue is null || liveValue is null
                    ? null
                    : string.Equals(storedValue, liveValue, StringComparison.Ordinal),
            };
        }

        return result;
    }
}
