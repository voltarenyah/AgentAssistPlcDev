namespace Contracts.Engineering;

/// <summary>The TIA Portal session this server is currently attached to, if any.</summary>
public sealed class CurrentSessionInfo
{
    public bool Attached { get; set; }
    /// <summary>OS process id of the attached portal (see <see cref="SessionInfo.Id"/>).</summary>
    public int? SessionId { get; set; }
    public string? ProjectName { get; set; }
    public string? ProjectPath { get; set; }
}
