namespace Contracts.Engineering;

/// <summary>Conflict handling used by the TIA Openness CaxProvider during AML import.</summary>
public enum HardwareImportConflictPolicy
{
    MoveToParkingLot,
    RetainTiaDevice,
    OverwriteTiaDevice,
}
