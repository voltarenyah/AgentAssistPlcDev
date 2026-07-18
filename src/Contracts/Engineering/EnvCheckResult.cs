namespace Contracts.Engineering;

/// <summary>check_environment result (mcp-engineering.md §8.2).</summary>
public sealed class EnvCheckResult
{
    public bool OpennessInstalled { get; set; }
    public string? OpennessVersion { get; set; }
    public string? TiaPortalVersion { get; set; }

    /// <summary>Effective membership in the current process token (what Openness actually checks).</summary>
    public bool UserInOpennessGroup { get; set; }

    /// <summary>Persistent membership in the local group. If true while UserInOpennessGroup is false: re-login required.</summary>
    public bool UserInOpennessGroupPersistent { get; set; }

    public string OpennessGroupName { get; set; } = "Siemens TIA Openness";
    public Dictionary<string, string> OpennessDllPaths { get; set; } = new();
    public string? CurrentUser { get; set; }
    public string? MachineName { get; set; }
    public string? OsVersion { get; set; }
}
