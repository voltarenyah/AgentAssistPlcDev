using System.Text.Json;

namespace Agent.Workbench;

/// <summary>schemaVersion 1 of engineering-state/revision.json.</summary>
public sealed record EngineeringRevisionState(
    int SchemaVersion,
    EngineeringSvnLink Svn,
    EngineeringTiaState Tia,
    EngineeringSafetyState Safety,
    EngineeringValidationState Validation);

/// <summary>Link to the SVN native store: url may be repository-relative ("^/native/main").</summary>
public sealed record EngineeringSvnLink(string? Url, long? Revision);

public sealed record EngineeringTiaState(string? ProjectChecksum);

/// <summary>Safety evidence: the aggregated offline collective F-signature plus the aggregate
/// read state (null on legacy files, "ok" when every safety device's signature was read or no
/// safety device exists, "read-failed" when a required read failed — see
/// Contracts.Engineering.FSignatureReadState).</summary>
public sealed record EngineeringSafetyState(string? FSignature, string? ReadState = null);

public sealed record EngineeringValidationState(string CompileStatus);

/// <summary>Change classification recorded per engineering savepoint (Phase 3 commit flow).</summary>
public sealed record EngineeringChangeClassification(
    bool SemanticChanged,
    bool SafetyChanged,
    bool NativeChanged);

/// <summary>Compile status values recorded in revision.json.</summary>
public static class EngineeringCompileStatus
{
    public const string Success = "SUCCESS";
    public const string Failed = "FAILED";
    public const string NotRun = "NOT_RUN";
}

/// <summary>
/// Deterministic writer/reader for the single Git-tracked engineering revision metadata file
/// (&lt;worktree&gt;/engineering-state/revision.json, schemaVersion 1). The file links one Git
/// commit to the SVN native revision holding the same TIA state: Git = what happened,
/// SVN = the exact project, revision.json = the link between them. Property order is fixed by
/// the record declarations and nulls are written explicitly, so identical state serializes to
/// identical bytes.
/// </summary>
public static class EngineeringStateWriter
{
    public const int SchemaVersion = 1;

    /// <summary>Repository-relative Git path; must stay in sync with SourcePathPolicy.</summary>
    public const string RelativePath = "engineering-state/revision.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static EngineeringRevisionState Create(
        string? svnUrl,
        long? svnRevision,
        string? projectChecksum,
        string? fSignature,
        string compileStatus,
        string? fSignatureReadState = null)
    {
        if (string.IsNullOrWhiteSpace(compileStatus))
        {
            throw new ArgumentException("A compile status is required.", nameof(compileStatus));
        }

        return new EngineeringRevisionState(
            SchemaVersion,
            new EngineeringSvnLink(svnUrl, svnRevision),
            new EngineeringTiaState(projectChecksum),
            new EngineeringSafetyState(fSignature, fSignatureReadState),
            new EngineeringValidationState(compileStatus));
    }

    /// <summary>Writes revision.json into the worktree's engineering-state directory.</summary>
    public static void Write(string worktreeRoot, EngineeringRevisionState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.SchemaVersion != SchemaVersion)
        {
            throw new ArgumentException(
                $"Unsupported revision state schemaVersion '{state.SchemaVersion}'.", nameof(state));
        }

        var path = WorkbenchPaths.ResolveRevisionState(worktreeRoot);
        WriteFile(path, state);
    }

    public static EngineeringRevisionState Read(string path)
    {
        var text = File.ReadAllText(path);
        EngineeringRevisionState? state;
        try
        {
            state = JsonSerializer.Deserialize<EngineeringRevisionState>(text, Json);
        }
        catch (JsonException exception)
        {
            throw new WorkbenchLifecycleException(
                "REVISION_STATE_INVALID",
                $"The engineering revision state '{path}' is not valid JSON: {exception.Message}");
        }

        if (state is null || state.SchemaVersion != SchemaVersion)
        {
            throw new WorkbenchLifecycleException(
                "REVISION_STATE_UNSUPPORTED",
                $"The engineering revision state '{path}' does not declare schemaVersion {SchemaVersion}.");
        }

        return state with { Validation = state.Validation ?? new EngineeringValidationState(EngineeringCompileStatus.NotRun) };
    }

    /// <summary>Parse a revision state from raw JSON (e.g. read via vc_show_file). Returns null
    /// when the text is missing or not a valid/supported revision state — unlike Read, which
    /// throws, because history entries may legitimately predate revision.json.</summary>
    public static EngineeringRevisionState? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            var state = JsonSerializer.Deserialize<EngineeringRevisionState>(text, Json);
            return state is null || state.SchemaVersion != SchemaVersion
                ? null
                : state with { Validation = state.Validation ?? new EngineeringValidationState(EngineeringCompileStatus.NotRun) };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Directory-name-safe rendering of a project checksum for restore targets, e.g.
    /// "Sino_PEI:7F FB BF" → "Sino_PEI-7FFBBF". Deterministic; null/empty → null.</summary>
    public static string? ChecksumDirectoryName(string? projectChecksum)
    {
        if (string.IsNullOrWhiteSpace(projectChecksum))
        {
            return null;
        }

        var builder = new System.Text.StringBuilder(projectChecksum.Length);
        foreach (var character in projectChecksum.Trim())
        {
            switch (character)
            {
                case >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-' or '.':
                    builder.Append(character);
                    break;
                case ':' or ';':
                    builder.Append('-');
                    break;
                case ' ':
                    break;
                default:
                    builder.Append('_');
                    break;
            }
        }

        var name = builder.ToString().Trim('-', '.');
        return name.Length == 0 ? null : name;
    }

    /// <summary>
    /// Classifies a savepoint against its base revision.json: semanticChanged comes from the
    /// exported-XML diff, safetyChanged from the F-signature (null-safe: a signature appearing
    /// or disappearing counts as a change; a failed required read also counts as a safety
    /// change because it cannot prove the safety program unchanged), nativeChanged from a
    /// dirty SVN working copy or a changed project checksum.
    /// </summary>
    public static EngineeringChangeClassification Classify(
        EngineeringRevisionState baseline,
        string? currentProjectChecksum,
        string? currentFSignature,
        bool svnWorkingCopyDirty,
        bool semanticDiffChanged,
        bool fSignatureReadFailed = false)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        return new EngineeringChangeClassification(
            semanticDiffChanged,
            fSignatureReadFailed || !StateValueEquals(baseline.Safety?.FSignature, currentFSignature),
            svnWorkingCopyDirty || !StateValueEquals(baseline.Tia?.ProjectChecksum, currentProjectChecksum));
    }

    private static bool StateValueEquals(string? left, string? right) =>
        string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.Ordinal);

    private static void WriteFile(string path, EngineeringRevisionState state)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new ArgumentException("The revision state path must include a parent directory.", nameof(path));
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".revision.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(state, Json));
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
}
