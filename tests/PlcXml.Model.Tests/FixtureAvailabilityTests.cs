using System.IO;
using Xunit;

namespace PlcXml.Model.Tests;

public sealed class FixtureAvailabilityTests
{
    public static TheoryData<string> SelectedFixtures => new()
    {
        FixtureFiles.MainObPath,
        FixtureFiles.SimulateCylinderFcPath,
        FixtureFiles.SclAssignFcPath,
    };

    [Theory]
    [MemberData(nameof(SelectedFixtures))]
    public void Selected_repository_fixture_is_copied_to_test_output(string fixturePath)
    {
        Assert.True(File.Exists(fixturePath), $"Fixture was not copied to test output: {fixturePath}");

        var bytes = FixtureFiles.ReadAllBytes(fixturePath);

        Assert.NotEmpty(bytes);
    }
}
