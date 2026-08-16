using Contracts.Engineering;
using Mcp.Engineering.Export;
using Xunit;

namespace Mcp.Engineering.Tests;

public sealed class FingerprintComponentsTests
{
    [Fact]
    public void ParsesCanonicalFingerprintDataIntoNamedComponents()
    {
        var fingerprints = FingerprintSet.Parse(
            "Code=code-hash;Comments=comments-hash;Events=events-hash;Interface=interface-hash;Properties=properties-hash");

        Assert.NotNull(fingerprints);
        Assert.Equal("code-hash", fingerprints!["Code"]);
        Assert.Equal("properties-hash", fingerprints["Properties"]);
        Assert.Equal(
            "Code=code-hash;Comments=comments-hash;Events=events-hash;Interface=interface-hash;Properties=properties-hash",
            fingerprints.ToCanonicalString());
    }

    [Fact]
    public void ComparesEachFingerprintComponentIndependently()
    {
        var stored = FingerprintSet.Parse("Code=old-code;Comments=same;Interface=same")!;
        var live = FingerprintSet.Parse("Code=new-code;Comments=same;Properties=new-property")!;

        var comparison = FingerprintComparison.Compare(stored, live)!;

        Assert.False(comparison["Code"].Matches);
        Assert.Equal("old-code", comparison["Code"].Stored);
        Assert.Equal("new-code", comparison["Code"].Live);
        Assert.True(comparison["Comments"].Matches);
        Assert.Null(comparison["Interface"].Live);
        Assert.Null(comparison["Properties"].Stored);
    }

    [Fact]
    public void WritesStructuredFingerprintsAndReadsLegacyStrings()
    {
        var document = new ExportMetadataDocument
        {
            Components =
            {
                new ExportMetadataRecord
                {
                    Id = "id-1",
                    Name = "Main",
                    SourcePath = "Main",
                    Category = "OB",
                    Status = "Exported",
                    Fingerprints = "Code=code-hash;Comments=comments-hash",
                    FingerprintComponents = FingerprintSet.Parse("Code=code-hash;Comments=comments-hash"),
                },
            },
        };

        var json = ExportMetadataJsonSerializer.Serialize(document);

        Assert.Contains("\"fingerprints\": {", json);
        Assert.Contains("\"Code\": \"code-hash\"", json);
        var roundTripped = ExportMetadataJsonSerializer.Deserialize(json);
        Assert.Equal("comments-hash", roundTripped.Components[0].FingerprintComponents!["Comments"]);

        var legacy = ExportMetadataJsonSerializer.Deserialize("""
            {
              "components": [{
                "id": "id-1",
                "fingerprints": "Code=old-code;Comments=old-comments"
              }]
            }
            """);

        Assert.Equal("old-code", legacy.Components[0].FingerprintComponents!["Code"]);
        Assert.Equal("Code=old-code;Comments=old-comments", legacy.Components[0].Fingerprints);
    }
}
