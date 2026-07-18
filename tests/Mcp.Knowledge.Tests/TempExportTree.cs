using System;
using System.IO;

namespace Mcp.Knowledge.Tests;

/// <summary>Temp-folder export tree mimicking an mcp-engineering output folder (incl. nested duplicates).</summary>
internal sealed class TempExportTree : IDisposable
{
    public TempExportTree()
    {
        Root = Path.Combine(Path.GetTempPath(), "Mcp.Knowledge.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string AddFixture(string fixturePath, string relativePath)
    {
        var target = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(fixturePath, target);
        return target;
    }

    public string AddText(string relativePath, string content)
    {
        var target = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, content);
        return target;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // best effort; temp files are harmless
        }
    }
}
