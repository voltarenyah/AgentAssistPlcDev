using Agent.Workbench;
using Contracts.Engineering;
using Xunit;

namespace Agent.Tests;

/// <summary>Pure reads of one object's committed fingerprint evidence out of an export manifest.</summary>
public sealed class CommittedSourceManifestTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), $"committed-manifest-{Guid.NewGuid():N}");

    [Fact]
    public void ReadsTheFingerprintEvidenceOfOneStandardBlock()
    {
        var evidence = CommittedSourceManifest.TryReadObjectEvidence(Manifest, "block-main");

        Assert.NotNull(evidence);
        Assert.Equal("block-main", evidence!.Id);
        Assert.Equal("Main", evidence.Name);
        Assert.Equal("Program blocks/Main", evidence.SourcePath);
        Assert.Equal("OB", evidence.Category);
        Assert.Equal(ManagedSourceEvidenceKind.StandardBlock, evidence.Kind);
        Assert.Equal(ManagedSourceEvidenceReadState.Readable, evidence.ReadState);
        Assert.Equal("AAAA1111", evidence.Fingerprints!["Code"]);
        Assert.Equal("CCCC3333", evidence.Fingerprints["Comments"]);
        Assert.Null(evidence.ModifiedTimeStamp);
    }

    [Fact]
    public void ReadsATagTableTimestampAndTheLegacyCanonicalFingerprintString()
    {
        var evidence = CommittedSourceManifest.TryReadObjectEvidence(
            """
            {
              "components": [
                {
                  "id": "tags-plant",
                  "name": "Plant",
                  "category": "Tags",
                  "exportedFile": "Tags/Plant.xml",
                  "modifiedDate": "2026-08-01T10:00:00Z",
                  "fingerprints": "Code=DDDD;Comments=EEEE"
                }
              ]
            }
            """,
            "tags-plant");

        Assert.NotNull(evidence);
        Assert.Equal(ManagedSourceEvidenceKind.TagTable, evidence!.Kind);
        Assert.Equal(DateTimeOffset.Parse("2026-08-01T10:00:00Z"), evidence.ModifiedTimeStamp);
        Assert.Equal("DDDD", evidence.Fingerprints!["Code"]);
    }

    [Fact]
    public void AnUnreadableObjectIsStillReportedSoComparisonCanAttributeIt()
    {
        // A standard block with no fingerprint components is unreadable, not absent: the compare
        // must still see the baseline and report evidence-unreadable rather than "new".
        var evidence = CommittedSourceManifest.TryReadObjectEvidence(
            """{ "components": [ { "id": "block-bare", "name": "Bare", "category": "FC", "exportedFile": "Blocks/Bare.xml" } ] }""",
            "block-bare");

        Assert.NotNull(evidence);
        Assert.Equal(ManagedSourceEvidenceReadState.Unreadable, evidence!.ReadState);
        Assert.Null(evidence.Fingerprints);
    }

    [Fact]
    public void InstanceDataBlocksAreExcludedFromEvidence()
    {
        var evidence = CommittedSourceManifest.TryReadObjectEvidence(
            """
            {
              "components": [
                {
                  "id": "idb-main",
                  "name": "Main",
                  "category": "DB",
                  "siemensTypeName": "InstanceDB",
                  "exportedFile": "Blocks/Main.xml"
                }
              ]
            }
            """,
            "idb-main");

        Assert.Null(evidence);
    }

    [Fact]
    public void ArgsAgreeWithTheSnapshotReaderOnTheStableObjectId()
    {
        // The stage route registers entities from DeviceSnapshotReader.ReadManifestSourceObjects,
        // so the evidence lookup must resolve the same stable ID from the same manifest content.
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, DeviceManifestSafety.ManifestFileName), Manifest);

        var registered = Assert.Single(DeviceSnapshotReader.ReadManifestSourceObjects(root));
        var evidence = CommittedSourceManifest.TryReadObjectEvidence(Manifest, registered.Id);

        Assert.Equal("block-main", registered.Id);
        Assert.NotNull(evidence);
        Assert.Equal(registered.Id, evidence!.Id);
    }

    [Fact]
    public void UnparseableOrMissingContentHasNoEvidence()
    {
        Assert.Null(CommittedSourceManifest.TryReadObjectEvidence(null, "block-main"));
        Assert.Null(CommittedSourceManifest.TryReadObjectEvidence("not json", "block-main"));
        Assert.Null(CommittedSourceManifest.TryReadObjectEvidence("{ \"components\": [] }", "block-main"));
        Assert.Null(CommittedSourceManifest.TryReadObjectEvidence(Manifest, "unknown"));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private const string Manifest = """
        {
          "schemaVersion": "1.0",
          "components": [
            {
              "id": "block-main",
              "name": "Main",
              "category": "OB",
              "sourcePath": "Program blocks/Main",
              "exportedFile": "Blocks/Main.xml",
              "fingerprints": { "Code": "AAAA1111", "Interface": "BBBB2222", "Comments": "CCCC3333" }
            }
          ]
        }
        """;
}
