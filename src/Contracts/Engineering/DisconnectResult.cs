namespace Contracts.Engineering;

/// <summary>
/// disconnect result. Per locked decision §1.1 the server never saves implicitly, so an
/// unsaved-changes warning is the caller's signal to call save_project first next time.
/// </summary>
public sealed class DisconnectResult
{
    public bool WasConnected { get; set; }

    /// <summary>True when the project had unsaved changes (ProjectBase.IsModified) at disconnect.</summary>
    public bool HadUnsavedChanges { get; set; }
}
