namespace Contracts.Engineering;

/// <summary>get_project_info result.</summary>
public sealed class ProjectInfo
{
    public string? Name { get; set; }
    public string? Path { get; set; }
    public string[] PlcDevices { get; set; } = Array.Empty<string>();

    /// <summary>-1 until block enumeration is implemented.</summary>
    public int BlockCount { get; set; } = -1;

    public DateTime? LastModified { get; set; }
}
