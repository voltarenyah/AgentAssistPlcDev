namespace Mcp.Engineering.Export;

/// <summary>
/// Which live TIA objects belong to the managed-source domain that gets exported, recorded in the
/// device manifest, committed and compared.
/// </summary>
/// <remarks>
/// Instance DBs do not belong to it. TIA generates them from their FB, and the FB is where the
/// information worth tracking lives, so ADR-0001 calls them "deliberately unmanaged native elements"
/// and the fingerprint-first compare design states their XML policy as "never export or diff".
/// Excluding them here is what makes that true end to end: they are not exported, not listed in the
/// manifest, not reported as added or removed by a sync, and therefore never reach Git. A device that
/// already exported them once drops the stale records and files on its next sync rather than reporting
/// them as a change.
/// </remarks>
internal static class ManagedSourceScope
{
    /// <summary>Openness type name of an instance DB: the value <see cref="ExportManifest.CategoryOf"/>
    /// reads from the block type, and the value the manifest stores as <c>siemensTypeName</c>.</summary>
    public const string InstanceDbTypeName = "InstanceDB";

    public static bool IsExcluded(string? typeName) =>
        string.Equals(typeName, InstanceDbTypeName, StringComparison.Ordinal);
}
