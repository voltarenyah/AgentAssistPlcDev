using System;
using System.IO;

namespace PlcXml.Model.Tests;

internal static class FixtureFiles
{
    public static string DirectoryPath { get; } = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static string MainObPath => Path.Combine(DirectoryPath, "Main [OB1].xml");

    public static string SimulateCylinderFcPath => Path.Combine(DirectoryPath, "FC_LAD_SimulateCylinder_Call [FC1].xml");

    public static string SclAssignFcPath => Path.Combine(DirectoryPath, "SclAssign [FC10].xml");
    public static string MutationSourcePath => Path.Combine(DirectoryPath, "MutationSource.xml");
    public static string MutationTitleExpectedPath => Path.Combine(DirectoryPath, "MutationSource.title.expected.xml");
    public static string MutationCommentExpectedPath => Path.Combine(DirectoryPath, "MutationSource.comment.expected.xml");

    public static byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);
}
