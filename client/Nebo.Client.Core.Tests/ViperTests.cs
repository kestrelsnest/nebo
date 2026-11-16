using System.Text.Json.Nodes;
using Nebo.Client.Core.Dtc;

namespace Nebo.Client.Core.Tests;

[Trait("Needs", "DCS")]  // tables made from a DCS install (DtcTestData); GitHub's build has none
public class ViperTests
{
    private static Cartridge New() => Cartridge.NewDefault(Cartridge.ViperType, "Caucasus");

    [Fact]
    public void Default_cartridge_is_dcs_s_with_the_terrain_set()
    {
        var c = New();
        Assert.Equal("F-16C_50", c.Type);
        Assert.Equal("Caucasus", (string?)c.Data["MPD"]!["terrain"]);
        Assert.True(new ViperNav(c).Mirror);              // a fresh dialog mirrors the mission
        Assert.Equal(48, new ViperEw(c).RoeList.Count);
        Assert.Equal(20, ((JsonObject)c.Data["COMM"]!["COMM1"]!).Count);
    }

    [Fact]
    public void Compile_gives_the_tables_dcs_compiled()
    {
        var c = New();
        var rwr = c.Data["ELINT"]!["RWR"]!["RWR_Avionics_Threat_Table"]!.DeepClone();
        var cmds = c.Data["MPD"]!["CMDS"]!["CMDSPrograms"]!["CMDS_Avionics_Threat_Table"]!.DeepClone();
        new ViperEw(c).Compile();
        Assert.True(JsonNode.DeepEquals(rwr, c.Data["ELINT"]!["RWR"]!["RWR_Avionics_Threat_Table"]));
        Assert.True(JsonNode.DeepEquals(cmds, c.Data["MPD"]!["CMDS"]!["CMDSPrograms"]!["CMDS_Avionics_Threat_Table"]));
    }

    [Fact]
    public void Compile_follows_a_changed_priority_and_program()
    {
        var c = New();
        var ew = new ViperEw(c);
        ew.MoveRwr("HIGH", "A-50", 1);
        ew.SetCmdsThreat("Air", "A-50", 3, 1);
        ew.Compile();
        Assert.Equal("A-50", (string?)c.Data["ELINT"]!["RWR"]!["RWR_Avionics_Threat_Table"]!["HIGH"]![0]!["group_name"]);
        var row = c.Data["MPD"]!["CMDS"]!["CMDSPrograms"]!["CMDS_Avionics_Threat_Table"]!.AsArray().First(r => (string?)r!["group_name"] == "A-50")!;
        Assert.Equal(3, HornetNav.Number(row["program"]));
        // The ranking stays 1…n.
        var pri = ew.Rwr("HIGH").Select(r => (int)HornetNav.Number(r.Row["PRI"])!).ToList();
        Assert.Equal(Enumerable.Range(1, pri.Count), pri);
    }

    [Fact]
    public void Steerpoints_renumber_and_routes_follow_their_order()
    {
        var c = New();
        var nav = new ViperNav(c);
        for (var i = 0; i < 4; i++) nav.Add(i * 1000, 0, 10);
        Assert.False(nav.Mirror);
        nav.SetInRoute(1, 2, true);
        nav.SetInRoute(1, 4, true);
        Assert.Equal([2, 4], nav.Route(1));
        nav.Remove(1);
        Assert.Equal([1, 3], nav.Route(1));
        Assert.Equal("STPT3", (string?)nav.Point(3)!["id"]);
        Assert.Equal(3, HornetNav.Number(nav.Point(3)!["number"]));
        Assert.Equal("OA13Line", (string?)nav.Point(3)!["idOA1_Line"]);
        for (var i = 0; i < 30; i++) nav.Add(0, 0, 0);
        Assert.Equal(ViperNav.MaxPoints, nav.Points.Count);
    }

    [Fact]
    public void Offset_aimpoint_like_the_dialog_and_moves_with_its_steerpoint()
    {
        var nav = new ViperNav(New());
        nav.Add(0, 0, 0);
        nav.SetOap(1, 1, 0, 1000, 50);        // 1 km east
        var p = nav.Point(1)!;
        Assert.Equal(1000, HornetNav.Number(p["OAP_1_Range"])!.Value, 6);
        Assert.Equal(Math.PI / 2, HornetNav.Number(p["OAP_1_Bearing"])!.Value, 6);
        nav.Move(1, 500, 0);
        Assert.Equal(500, HornetNav.Number(p["OAP_1_X"])!.Value, 6);
        Assert.Equal(1000, HornetNav.Number(p["OAP_1_Y"])!.Value, 6);
        nav.SetOap(1, 2, 500 + 100_000, 0, 0);  // beyond 99,999 ft: at the limit
        Assert.Equal(ViperNav.MaxOapRangeM, HornetNav.Number(p["OAP_2_Range"])!.Value, 6);
    }

    [Theory]
    [InlineData(20, 30, ViperComm.Fm)]
    [InlineData(87.975, 87.975, ViperComm.Fm)]
    [InlineData(95, 87.975, ViperComm.Fm)]
    [InlineData(110, 116, ViperComm.Am)]
    [InlineData(127.5, 127.5, ViperComm.Am)]
    [InlineData(200, 155.975, ViperComm.Am)]
    public void Vhf_band_and_modulation_like_comm2(double mhz, double got, int modulation) =>
        Assert.Equal((got, modulation), ViperComm.Vhf(mhz));

    [Fact]
    public void Dest_points_like_dcs_numbered_81_up_and_renumbered()
    {
        var dest = new ViperDest(New());
        for (var i = 0; i < 3; i++) dest.Add(i, 0, 0);
        Assert.False(dest.Mirror);
        Assert.Equal("D83", (string?)dest.Point(3)!["text"]);
        dest.Remove(1);
        Assert.Equal("DEST82", (string?)dest.Point(2)!["id"]);
        dest.Set(1, "ab-12x", new string('n', 40), 5);
        Assert.Equal("AB1", (string?)dest.Point(1)!["text"]);
        Assert.Equal(30, ((string?)dest.Point(1)!["note"])!.Length);
        for (var i = 0; i < 30; i++) dest.Add(0, 0, 0);
        Assert.Equal(19, dest.Points.Count);
    }

    [Fact]
    public void Geo_points_are_on_one_line_each_in_list_order()
    {
        var geo = new ViperGeo(New());
        geo.Insert(int.MaxValue, 2, 0, 0, 0);
        geo.Insert(int.MaxValue, 1, 1, 0, 0);
        geo.Insert(int.MaxValue, 2, 2, 0, 0);
        geo.Insert(2, 2, 9, 0, 0);                 // between the first two
        Assert.Equal([1, 2, 4], geo.Line(2));
        Assert.Equal(9, HornetNav.Number(geo.Point(2)!["x"]));
        geo.SetLine(1, 3);
        Assert.Equal([1], geo.Line(3));
        Assert.Equal(0, ViperGeo.LineOf(geo.Point(1)!) == 3 ? 0 : 1);
        Assert.Equal("GEO_LINES34", (string?)geo.Point(4)!["id"]);
    }

    [Fact]
    public void Threats_take_their_type_and_only_custom_takes_its_own_radius()
    {
        var threats = new ViperThreats(New());
        var hawk = ViperThreats.Types.Select((t, i) => (t, i)).First(x => (string?)x.t["name"] == "SAM Hawk").i + 1;
        threats.Add(0, 0, 0, hawk);
        var t = threats.Point(1)!;
        Assert.Equal(45000, HornetNav.Number(t["radius"]));
        Assert.Equal("HK", (string?)t["text"]);
        threats.Set(1, "hwk1", false, 1, 1);
        Assert.Equal(45000, HornetNav.Number(t["radius"]));   // not Custom: the type's
        Assert.Equal("HWK", (string?)t["text"]);
        threats.SetType(1, 1);
        threats.Set(1, "C", true, 30000, 3000);
        Assert.Equal(30000, HornetNav.Number(t["radius"]));
    }

    [Fact]
    public void Vip_legs_follow_their_steerpoint_and_stop_at_80_nm()
    {
        var c = New();
        var nav = new ViperNav(c);
        nav.Add(0, 0, 0);
        var vip = new ViperVipVrp(c);
        vip.SetSteerpoint(true, 1);
        vip.Place("VIPTOTGT", 0, 0, 200 * 1852, 0, 10);
        Assert.True(vip.Enabled("VIPTOTGT"));
        Assert.Equal(ViperVipVrp.MaxRangeM, HornetNav.Number(vip.Data["VIPTOTGT_Range"])!.Value, 6);
        nav.Move(1, 1000, 0);
        Assert.Equal(1000 + ViperVipVrp.MaxRangeM, ViperOffset.At(vip.Data, "VIPTOTGT").X, 6);
        vip.Clear("VIPTOTGT", 1000, 0);
        Assert.False(vip.IsSet("VIPTOTGT"));
    }

    [Fact]
    public void Colours_by_page_with_dcs_names()
    {
        var colr = new ViperColr(New());
        Assert.Equal(8, ViperColr.Colors.Count);
        Assert.Equal("White", ViperColr.Colors[0]);
        Assert.Equal(24, colr.Page("MFDFCR").Count);
        Assert.Equal(24, colr.Page("HSDHAD").Count);
        Assert.Equal(17, colr.Page("MISC").Count);
        colr.Set("MFDFCR_SOIBox", 5);
        Assert.Equal(5, colr.Page("MFDFCR").First(p => p.Key == "MFDFCR_SOIBox").Color);
    }

    [Fact]
    public void Roe_marks_own_friendly_seen_hostile_rest_unknown()
    {
        var ew = new ViperEw(New());
        var (f, h) = ew.MarkFriendly(new HashSet<string> { "F-16C_50", "FA-18C_hornet" }, new HashSet<string> { "MiG-29S" });
        string S(string g) => ew.RoeList.First(r => (string?)r["group_name"] == g)["sovereignty"]!.ToString();
        Assert.Equal("1", S("F-16"));
        Assert.Equal("1", S("F/A-18"));
        Assert.Equal("3", S("A-10"));
        Assert.True(f >= 2 && h >= 1);
    }
}
