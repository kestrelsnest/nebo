using Nebo.Client.Core.Dtc;

namespace Nebo.Client.Core.Tests;

public class HornetEwTests
{
    private static HornetEw New() => new(Cartridge.NewDefault(Cartridge.HornetType, "Caucasus"));

    [Fact]
    public void Default_cartridge_has_dcs_tables()
    {
        var ew = New();
        Assert.Equal(61, ew.TooClasses.Count);
        Assert.True(ew.CmdsThreats.Count > 90);
        Assert.Equal(95, ew.Rwr("NORM").Count);
        Assert.NotNull(ew.Program("AUTO_1")["Chaff"]);
    }

    [Fact]
    public void Own_sides_systems_become_friendly_and_the_rest_hostile()
    {
        // These radars are known to the threat base only by wsType: the server's unit_types message names them.
        DtcTestData.UnitTypes(("Patriot str", "SAM Patriot - MPQ-53"), ("Hawk tr", "SAM Hawk - MPQ-46"), ("Kub 1S91 str", "SAM SA-6 'Gainful'"));
        var ew = New();
        // Blue with a Patriot site, Hawk, Hornets and an Arleigh Burke.
        var (rwr, harm) = ew.MarkFriendly(new HashSet<string> { "Patriot str", "Hawk tr", "FA-18C_hornet", "USS_Arleigh_Burke_IIa" });
        int Too(string radar) => ew.TooClasses.First(t => t.Radar == radar).Class;
        bool Friend(string table, string group) => ew.Rwr(table).First(r => r.Group == group).Row["friend"]!.GetValue<bool>();
        Assert.Contains(Too("SAM Patriot - MPQ-53"), new[] { 5, 6 });                  // H1/H2 → F1/F2
        Assert.Contains(Too("SAM Hawk - MPQ-46"), new[] { 5, 6 });
        Assert.Contains(Too("SAM SA-6 'Gainful'"), new[] { 7, 8 });                    // red's: hostile
        Assert.Equal(3, Too("Arleigh Burke guided missile destroyer"));                 // HN → FN
        Assert.True(Friend("NORM", "F/A-18"));
        Assert.True(Friend("FRND", "F/A-18"));
        Assert.False(Friend("NORM", "SAM SA-6 'Gainful'"));
        Assert.True(rwr >= 3 && harm >= 3);

        // The other side: the same cartridge turned around.
        ew.MarkFriendly(new HashSet<string> { "Kub 1S91 str" });
        Assert.Contains(Too("SAM SA-6 'Gainful'"), new[] { 5, 6 });
        Assert.Contains(Too("SAM Patriot - MPQ-53"), new[] { 7, 8 });
        Assert.Equal(4, Too("Arleigh Burke guided missile destroyer"));
        Assert.False(Friend("NORM", "F/A-18"));
    }

    [Theory]
    [InlineData(0.1, 0.25)]
    [InlineData(1.13, 1.25)]
    [InlineData(9, 5)]
    public void Chaff_interval_like_dcs(double s, double got) => Assert.Equal(got, HornetEw.Interval(s));

    [Fact]
    public void Program_values_are_clamped()
    {
        var ew = New();
        ew.SetProgram("MAN_1", 150, -3, 0.6, 4, 0, 99);
        var p = ew.Program("MAN_1");
        Assert.Equal(99, HornetNav.Number(p["Chaff"]!["Quantity"]));
        Assert.Equal(0, HornetNav.Number(p["Chaff"]!["Repeat"]));
        Assert.Equal(0.5, HornetNav.Number(p["Chaff"]!["Interval"]));
        ew.Delay = 15;
        Assert.Equal(10, ew.Delay);
    }

    [Fact]
    public void Cmds_threat_takes_an_offered_threshold_only()
    {
        var ew = New();
        var (cat, group, row) = ew.CmdsThreats.First(t => t.Group == "A-50");
        ew.SetCmdsThreat(cat, group, 8, 1);
        Assert.Equal(8, HornetNav.Number(row["program"]));
        Assert.Equal("SEARCH", (string?)row["default_threshold"]!["label"]);
        ew.SetCmdsThreat(cat, group, 5, 3);   // 5 is no AUTO program; LAUNCH not offered for an A-50
        Assert.Equal(0, HornetNav.Number(row["program"]));
        Assert.Equal("SEARCH", (string?)row["default_threshold"]!["label"]);
    }

    [Fact]
    public void Rwr_rows_change_and_sort_by_priority()
    {
        var ew = New();
        ew.SetRwr("AI", "A-50", 1, false, true, false);
        var (group, row) = ew.Rwr("AI")[0];
        Assert.Equal("A-50", group);
        Assert.False(row["display"]!.GetValue<bool>());
        Assert.True(row["friend"]!.GetValue<bool>());
    }
}
