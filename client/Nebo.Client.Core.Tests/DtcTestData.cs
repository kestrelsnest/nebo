using System.Runtime.CompilerServices;
using Nebo.Client.Core.Dtc;

namespace Nebo.Client.Core.Tests;

/// <summary>The data cartridge tests run on tables made from this PC's DCS install, as the client
/// does (DtcData): DCS_PATH when set, else the usual install folders. The tables are Eagle
/// Dynamics' data and are never kept in the repository; without DCS those tests fail with "no
/// cartridge format".</summary>
internal static class DtcTestData
{
    [ModuleInitializer]
    internal static void Init()
    {
        var cache = Path.Combine(Path.GetTempPath(), "nebo-tests");
        DtcData.Configure(FindDcs, cache);
    }

    private static string? FindDcs()
    {
        if (Environment.GetEnvironmentVariable("DCS_PATH") is { Length: > 0 } env && DcsLocator.IsDcsFolder(env)) return env;
        var guesses = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .SelectMany(d => DcsLocator.UsualFolders(d.RootDirectory.FullName)).Select(p => (p, "test"));
        return DcsLocator.Verify(guesses).FirstOrDefault()?.Path;
    }

    /// <summary>What the server's unit_types message gives for a few threat groups known only by
    /// wsType: each named unit type gets its group's wsType from the generated threat base.</summary>
    internal static void UnitTypes(params (string UnitType, string Group)[] items)
    {
        var groups = DtcDefs.For(Cartridge.HornetType)["threats_base"]!.AsArray();
        int[] WsType(string group) => groups.First(g => (string?)g!["group_name"] == group)!["threats"]!.AsArray()
            .Select(t => t!["wstype"]!.AsArray()).First(w => w.Count == 4).Select(v => (int)v!).ToArray();
        HornetEw.SetUnitTypes(items.Select(i => (i.UnitType, WsType(i.Group))));
    }
}
