using System.Text.Json;

namespace Agent.Workbench.EngineeringGraph;

/// <summary>
/// The source inspector's ingest (ADR-0011 Phase 5). <see cref="SourceObjectInspectorReader"/> used to
/// run inside the inspect request; it now runs once per object during the device projection, and its
/// payload becomes properties of that object's source-object node. The read path
/// (<see cref="DeviceSnapshotGraphReader.ReadInspection"/>) returns the stored payload, so no request
/// opens or parses an exported XML file.
/// </summary>
/// <remarks>
/// The ingest is total by design: one object whose XML is missing, malformed, unsupported or
/// unreadable must not fail the whole device projection, so the outcome — payload or error — is
/// recorded as facts and re-raised by the read exactly as the inspector raised it. Only a cancelled
/// projection propagates.
/// </remarks>
public static class SourceInspectionProjection
{
    /// <summary>The shape version of the stored payload. Raising it re-ingests every object's parsed
    /// content on the next projection, because the reuse rule compares it.</summary>
    public const string CurrentFormat = "1";

    private const string Available = "available";
    private const string Error = "error";

    /// <summary>Same options on both sides of the round trip, so the payload the route serializes is
    /// the payload the inspector produced.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The parsed content of one exported XML file as the inspector derives it, or the error the
    /// inspector raised, as a property set for that object's node.
    /// </summary>
    public static IReadOnlyList<GraphProperty> ReadFacts(
        DeviceContext context,
        string relativePath,
        DeviceSourceResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentNullException.ThrowIfNull(resolver);
        try
        {
            var inspection = new SourceObjectInspectorReader().Read(context, relativePath, resolver);
            return Facts(Available, null, null, JsonSerializer.Serialize(inspection, Json));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SourceInspectionException exception)
        {
            return Facts(Error, exception.Code, exception.Message, null);
        }
        catch (FileNotFoundException exception)
        {
            return Facts(Error, "SOURCE_FILE_NOT_FOUND", exception.Message, null);
        }
        catch (WorkbenchPathException exception)
        {
            return Facts(Error, "SOURCE_PATH_INVALID", exception.Message, null);
        }
        catch (ArgumentException exception)
        {
            return Facts(Error, "SOURCE_PATH_INVALID", exception.Message, null);
        }
        catch (Exception exception)
        {
            // The read path re-raises this as an unhandled read failure, which is what an unreadable
            // file already was; the projection itself must not fail because one object could not be
            // inspected.
            return Facts(Error, "SOURCE_INSPECTION_UNAVAILABLE", exception.Message, null);
        }
    }

    /// <summary>True when an object's stored facts already describe its current exported content, so a
    /// re-projection reuses them instead of opening the XML file again. The decision is the manifest's
    /// own <c>contentHash</c> plus the payload's format — the same input the projection's digest is
    /// built from — so a projection that re-runs for an unrelated reason costs no XML parse at all.</summary>
    public static bool CanReuse(
        string? storedContentHash,
        string? contentHash,
        IReadOnlyList<GraphProperty>? storedInspection)
    {
        // A legacy manifest carries no content hash, so nothing proves the file did not move: such an
        // object is re-ingested whenever its device is projected, exactly as the crawl fallback that
        // produced it re-reads every file.
        if (string.IsNullOrEmpty(contentHash)) return false;
        if (storedInspection is null) return false;
        if (!string.Equals(
                Property(storedInspection, SourceObjectInspectionPropertyNames.Format)?.Text,
                CurrentFormat,
                StringComparison.Ordinal))
            return false;
        if (Property(storedInspection, SourceObjectInspectionPropertyNames.State) is null) return false;
        return string.Equals(storedContentHash, contentHash, StringComparison.Ordinal);
    }

    private static GraphProperty? Property(IReadOnlyList<GraphProperty> properties, string name) =>
        properties.FirstOrDefault(property => string.Equals(property.Name, name, StringComparison.Ordinal));

    private static IReadOnlyList<GraphProperty> Facts(string state, string? errorCode, string? errorMessage, string? payload)
    {
        const string source = GraphPropertySource.SourceInspection;
        return
        [
            GraphProperty.TextValue(SourceObjectInspectionPropertyNames.Format, source, CurrentFormat),
            GraphProperty.TextValue(SourceObjectInspectionPropertyNames.State, source, state),
            GraphProperty.TextValue(SourceObjectInspectionPropertyNames.ErrorCode, source, errorCode),
            GraphProperty.TextValue(SourceObjectInspectionPropertyNames.ErrorMessage, source, errorMessage),
            GraphProperty.JsonValue(SourceObjectInspectionPropertyNames.Payload, source, payload),
        ];
    }
}
