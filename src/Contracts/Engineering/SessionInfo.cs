namespace Contracts.Engineering;

/// <summary>A running TIA Portal process that can be attached to (see TiaPortal.GetProcesses()).</summary>
public sealed class SessionInfo
{
    public int Id { get; set; }
    public string Mode { get; set; } = string.Empty;
    public string? PortalPath { get; set; }
    public string? ProjectPath { get; set; }
    public DateTime AcquisitionTime { get; set; }
}
