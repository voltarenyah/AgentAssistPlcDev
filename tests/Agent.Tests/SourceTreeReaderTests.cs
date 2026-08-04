using Agent.Workbench;
using Contracts.Engineering;
using Xunit;

namespace Agent.Tests;

public sealed class SourceTreeReaderTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "source-tree-reader-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ReadReturnsOnlyXmlWithStableIdentityAndSortedPaths()
    {
        Write("Tags/Z.xml", "<Document><SW.Tags.PlcTagTable ID=\"1\" /></Document>");
        Write("Blocks/A.xml", "<Document><SW.Blocks.OB ID=\"2\" /></Document>");
        Write("metadata.json", "{}");

        var objects = new SourceTreeReader().Read(root);

        Assert.Equal(new[] { "Blocks/A.xml", "Tags/Z.xml" }, objects.Select(x => x.RelativePath));
        Assert.All(objects, item => Assert.Equal(64, item.Sha256.Length));
    }

    [Fact]
    public void ReadFingerprintIgnoresExportTimestampAndLineEnding()
    {
        Write("Blocks/A.xml", "<Document>\r\n  <Created>one</Created>\r\n  <SW.Blocks.OB ID=\"2\" />\r\n</Document>");
        var first = new SourceTreeReader().Read(root).Single().Sha256;

        Write("Blocks/A.xml", "<Document>\n  <Created>two</Created>\n  <SW.Blocks.OB ID=\"2\" />\n</Document>");
        var second = new SourceTreeReader().Read(root).Single().Sha256;

        Assert.Equal(first, second);
    }

    private void Write(string relativePath, string content)
    {
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}
