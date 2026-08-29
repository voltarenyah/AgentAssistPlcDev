using Agent.Workbench;
using Xunit;

namespace Agent.Tests;

public sealed class EngineeringStateWriterTests : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), $"engineering-state-tests-{Guid.NewGuid():N}");

    [Fact]
    public void WriteReadRoundTripsAllFields()
    {
        var state = EngineeringStateWriter.Create(
            "^/native/main", 25, "PLC_1:abc123", "F-SIG-4711", EngineeringCompileStatus.Success, "ok");

        EngineeringStateWriter.Write(root, state);
        var loaded = EngineeringStateWriter.Read(
            Path.Combine(root, "engineering-state", "revision.json"));

        Assert.Equal(state, loaded);
    }

    [Fact]
    public void ReadToleratesLegacyStateWithoutSafetyReadState()
    {
        var path = Path.Combine(root, "revision.json");
        Directory.CreateDirectory(root);
        File.WriteAllText(path, """
            {
              "schemaVersion": 1,
              "svn": { "url": "^/native/main", "revision": 25 },
              "tia": { "projectChecksum": "PLC_1:abc123" },
              "safety": { "fSignature": "F-SIG-4711" },
              "validation": { "compileStatus": "SUCCESS" }
            }
            """);

        var loaded = EngineeringStateWriter.Read(path);

        Assert.Equal("F-SIG-4711", loaded.Safety.FSignature);
        Assert.Null(loaded.Safety.ReadState);
    }

    [Fact]
    public void ClassifyFlagsFailedRequiredSignatureReadAsSafetyChange()
    {
        var baseline = EngineeringStateWriter.Create(
            "^/native/main", 25, "c1", "s1", EngineeringCompileStatus.Success);

        var classification = EngineeringStateWriter.Classify(
            baseline, "c1", "s1", svnWorkingCopyDirty: false, semanticDiffChanged: false,
            fSignatureReadFailed: true);

        Assert.True(classification.SafetyChanged);
        Assert.False(classification.SemanticChanged);
        Assert.False(classification.NativeChanged);
    }

    [Fact]
    public void WriteIsDeterministicForIdenticalState()
    {
        var state = EngineeringStateWriter.Create(
            "^/native/main", 25, "checksum", null, EngineeringCompileStatus.NotRun);

        EngineeringStateWriter.Write(root, state);
        var first = File.ReadAllText(Path.Combine(root, "engineering-state", "revision.json"));
        EngineeringStateWriter.Write(root, state);
        var second = File.ReadAllText(Path.Combine(root, "engineering-state", "revision.json"));

        Assert.Equal(first, second);
    }

    [Fact]
    public void WriteUsesStablePropertyOrderAndWritesNullsExplicitly()
    {
        var state = EngineeringStateWriter.Create("^/native/main", 25, null, null, EngineeringCompileStatus.NotRun);

        EngineeringStateWriter.Write(root, state);
        var json = File.ReadAllText(Path.Combine(root, "engineering-state", "revision.json"));

        var expectedOrder = new[]
        {
            "\"schemaVersion\"", "\"svn\"", "\"url\"", "\"revision\"",
            "\"tia\"", "\"projectChecksum\"", "\"safety\"", "\"fSignature\"", "\"readState\"",
            "\"validation\"", "\"compileStatus\"",
        };
        var positions = expectedOrder
            .Select(token => json.IndexOf(token, StringComparison.Ordinal))
            .ToArray();
        Assert.All(positions, position => Assert.True(position >= 0));
        Assert.Equal(positions.OrderBy(position => position).ToArray(), positions);
        Assert.Contains("\"projectChecksum\": null", json);
        Assert.Contains("\"fSignature\": null", json);
        Assert.Contains("\"schemaVersion\": 1", json);
    }

    [Fact]
    public void ReadRejectsForeignSchemaVersion()
    {
        var path = Path.Combine(root, "revision.json");
        Directory.CreateDirectory(root);
        File.WriteAllText(path, """{"schemaVersion": 99}""");

        var error = Assert.Throws<WorkbenchLifecycleException>(() => EngineeringStateWriter.Read(path));

        Assert.Equal("REVISION_STATE_UNSUPPORTED", error.Code);
    }

    [Fact]
    public void CreateRejectsBlankCompileStatus()
    {
        Assert.Throws<ArgumentException>(() =>
            EngineeringStateWriter.Create("^/native/main", 1, null, null, "  "));
    }

    [Theory]
    // semantic diff, svn dirty, base checksum, current checksum, base sig, current sig
    // => semantic, safety, native
    [InlineData(false, false, "c1", "c1", "s1", "s1", false, false, false)]
    [InlineData(true, false, "c1", "c1", "s1", "s1", true, false, false)]
    [InlineData(false, true, "c1", "c1", "s1", "s1", false, false, true)]
    [InlineData(false, false, "c1", "c2", "s1", "s1", false, false, true)]
    [InlineData(false, false, "c1", "c1", "s1", "s2", false, true, false)]
    [InlineData(false, false, null, null, null, null, false, false, false)]
    [InlineData(false, false, "c1", null, null, null, false, false, true)]
    [InlineData(false, false, null, "c1", null, null, false, false, true)]
    [InlineData(false, false, null, null, "s1", null, false, true, false)]
    [InlineData(false, false, null, null, null, "s1", false, true, false)]
    [InlineData(true, true, "c1", "c2", "s1", "s2", true, true, true)]
    public void ClassifyMapsInputsToChangeKinds(
        bool semanticDiff,
        bool svnDirty,
        string? baseChecksum,
        string? currentChecksum,
        string? baseSignature,
        string? currentSignature,
        bool expectedSemantic,
        bool expectedSafety,
        bool expectedNative)
    {
        var baseline = EngineeringStateWriter.Create(
            "^/native/main", 25, baseChecksum, baseSignature, EngineeringCompileStatus.Success);

        var classification = EngineeringStateWriter.Classify(
            baseline, currentChecksum, currentSignature, svnDirty, semanticDiff);

        Assert.Equal(expectedSemantic, classification.SemanticChanged);
        Assert.Equal(expectedSafety, classification.SafetyChanged);
        Assert.Equal(expectedNative, classification.NativeChanged);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
