using System;
using System.IO;

namespace Mcp.Knowledge.Tests;

internal static class FixtureFiles
{
    public static string DirectoryPath { get; } = Path.Combine(AppContext.BaseDirectory, "Fixtures");

    public static string MainObPath => Path.Combine(DirectoryPath, "Main [OB1].xml");

    public static string SimulateCylinderFcPath => Path.Combine(DirectoryPath, "FC_LAD_SimulateCylinder_Call [FC1].xml");

    public static string GlobalDataDbPath => Path.Combine(DirectoryPath, "GlobalData [DB1].xml");

    public static string MotorFbInstanceDbPath => Path.Combine(DirectoryPath, "MotorFbInstance [DB2].xml");

    public static string IoCcTagTablePath => Path.Combine(DirectoryPath, "IO_CC_Cav_A.xml");

    public static string CabUdtPath => Path.Combine(DirectoryPath, "CAB.xml");

    public static string ReadAllText(string path) => File.ReadAllText(path);
}
