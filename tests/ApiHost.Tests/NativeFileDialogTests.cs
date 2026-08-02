using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;

namespace ApiHost.Tests;

public sealed class NativeFileDialogTests
{
    [Fact]
    public void OpenFileNameInteropStructureCanBeSized()
    {
        var structure = typeof(NativeFileDialog).GetNestedType("OpenFileName", BindingFlags.NonPublic);

        Assert.NotNull(structure);
        Assert.True(Marshal.SizeOf(structure!) > 0);
    }
}
