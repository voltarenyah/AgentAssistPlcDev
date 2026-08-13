namespace Contracts.Engineering;

/// <summary>get_project_info result.</summary>
public sealed class ProjectInfo
{
    public string? Name { get; set; }
    public string? Path { get; set; }
    public string[] PlcDevices { get; set; } = Array.Empty<string>();

    public string? Author { get; set; }
    public string? Comment { get; set; }
    public string? Copyright { get; set; }
    public string? Family { get; set; }
    public string? Version { get; set; }
    public string? LastModifiedBy { get; set; }
    public DateTime? CreationTime { get; set; }
    public long? Size { get; set; }
    public bool IsModified { get; set; }
    public bool IsReadOnly { get; set; }
    public bool IsPrimary { get; set; }
    public string[] Languages { get; set; } = Array.Empty<string>();

    /// <summary>-1 until block enumeration is implemented.</summary>
    public int BlockCount { get; set; } = -1;

    public DateTime? LastModified { get; set; }
}

/// <summary>Read-only access and authentication capabilities for the connected TIA project.</summary>
public sealed class ProjectCapabilities
{
    public string? ProjectName { get; set; }
    public string? ProjectPath { get; set; }
    public bool IsReadOnly { get; set; }
    public bool IsPrimary { get; set; }
    public bool IsModified { get; set; }
    public bool CanRead { get; set; }
    public bool CanAttemptWrite { get; set; }
    public string[] AuthenticationModes { get; set; } = Array.Empty<string>();
    public string[] Notes { get; set; } = Array.Empty<string>();
}

/// <summary>Result of creating a project through the connected TIA Portal.</summary>
public sealed class ProjectCreateResult
{
    public string? Name { get; set; }
    public string? Path { get; set; }
    public string? ProjectFilePath { get; set; }
}

/// <summary>Result of archiving the currently open project.</summary>
public sealed class ProjectArchiveResult
{
    public string? ProjectName { get; set; }
    public string? ArchivePath { get; set; }
    public string ArchivationMode { get; set; } = "compressed";
}

/// <summary>Result of retrieving a TIA project archive.</summary>
public sealed class ProjectRetrieveResult
{
    public string? Name { get; set; }
    public string? Path { get; set; }
    public string? ProjectFilePath { get; set; }
    public bool IsPrimary { get; set; }
    public bool IsReadOnly { get; set; }
    public bool Upgraded { get; set; }
}
