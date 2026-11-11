using System.Text.Json.Nodes;
using Nebo.Client.Core.Dtc;

namespace Nebo.Client.Core.Tests;

[Trait("Needs", "DCS")]  // tables made from a DCS install (DtcTestData); GitHub's build has none
public class HornetSaTests
{
    private static HornetSa New() => new(Cartridge.NewDefault(Cartridge.HornetType, "Caucasus"));
    private static IReadOnlyList<JsonObject> Types =>
        DtcDefs.For(Cartridge.HornetType)["MEZ_THRTS_defs/MEZ_THRTS_defs"]!.AsArray().OfType<JsonObject>().ToList();

    [Fact]
    public void Threat_rings_take_dcs_type_defaults_and_renumber()
    {
        var sa = New();
        var sa11 = HornetSa.RingTypeFor(Types, "SA-11")!;
        var t = sa.AddThreat(10, 20, sa11)!;
        Assert.Equal("MEZ_THRTS_1", (string?)t["id"]);
        Assert.Equal((HornetNav.Number(sa11["detectionRange"]) ?? 0) / 1852, HornetNav.Number(t["threat_ring_radius"]));
        sa.AddThreat(0, 0, sa11);
        sa.RemoveThreat(0);
        Assert.Equal("MEZ_THRTS_1", (string?)sa.Threats[0]["id"]);
        Assert.Equal(1, HornetNav.Number(sa.Threats[0]["num"]));
    }

    [Fact]
    public void At_most_40_rings_and_9_caps()
    {
        var sa = New();
        var type = Types[0];
        for (var i = 0; i < HornetSa.MaxThreats; i++) Assert.NotNull(sa.AddThreat(i, i, type));
        Assert.Null(sa.AddThreat(0, 0, type));
        for (var i = 0; i < HornetSa.MaxCaps; i++) Assert.NotNull(sa.AddCap(i, i));
        Assert.Null(sa.AddCap(0, 0));
    }

    [Fact]
    public void Cap_defaults_and_settings_like_dcs()
    {
        var sa = New();
        var c = sa.AddCap(100, 200)!;
        Assert.Equal("CAP_PTS_1", (string?)c["id"]);
        Assert.Equal(10 * 1852, HornetNav.Number(c["length"]));
        Assert.Equal(5 * 1852, HornetNav.Number(c["diameter"]));
        Assert.Equal("Left", (string?)c["turn_direction"]);
        sa.SetCap(0, 450, 20, 8, false, "north");
        Assert.Equal(90, HornetNav.Number(c["course"]));
        Assert.Equal(20 * 1852, HornetNav.Number(c["length"]));
        Assert.Equal("Right", (string?)c["turn_direction"]);
        sa.AddCap(0, 0);
        sa.RemoveCap(0);
        Assert.Equal("CAP_PTS_2", (string?)sa.Caps[0]["id"]);   // ids stay, as DCS's
        Assert.Equal(1, HornetNav.Number(sa.Caps[0]["num"]));
    }

    [Fact]
    public void Lines_hold_their_points_and_renumber_on_delete()
    {
        var sa = New();
        sa.AddLine(HornetSa.Corridor, 0, 0);
        sa.AddLine(HornetSa.Corridor, 5, 5);
        for (var i = 1; i < 14; i++) Assert.True(sa.AddPoint(HornetSa.Corridor, 1, i, i));
        Assert.False(sa.AddPoint(HornetSa.Corridor, 1, 99, 99));        // 14 points at most
        sa.RemoveLine(HornetSa.Corridor, 0);
        var line = sa.LinesOf(HornetSa.Corridor)[0];
        Assert.Equal("CORR_1", (string?)line["id"]);
        Assert.Equal("CORR_1_PT_14", (string?)HornetSa.PointsOf(line)[13]["id"]);
        for (var i = 0; i < 3; i++) Assert.NotNull(sa.AddLine(HornetSa.Flot, i, i));
        Assert.Null(sa.AddLine(HornetSa.Flot, 9, 9));                     // 3 FLOT lines
        sa.RemovePoint(HornetSa.Flot, 0, 0);                              // its only point: the line goes
        Assert.Equal(2, sa.LinesOf(HornetSa.Flot).Count);
    }

    [Fact]
    public void A_point_inserted_inside_a_line_keeps_the_ids_in_order()
    {
        var sa = New();
        sa.AddLine(HornetSa.Faor, 0, 0);
        sa.AddPoint(HornetSa.Faor, 0, 20, 20);
        Assert.True(sa.InsertPoint(HornetSa.Faor, 0, 1, 10, 10));         // between the two
        var pts = HornetSa.PointsOf(sa.LinesOf(HornetSa.Faor)[0]);
        Assert.Equal([0.0, 10.0, 20.0], pts.Select(p => HornetNav.Number(p["x"]) ?? -1));
        Assert.Equal(["FAOR_1_PT_1", "FAOR_1_PT_2", "FAOR_1_PT_3"], pts.Select(p => (string?)p["id"]));
        for (var i = 0; i < 4; i++) sa.AddPoint(HornetSa.Faor, 0, 30 + i, 30 + i);
        Assert.False(sa.InsertPoint(HornetSa.Faor, 0, 0, 1, 1));           // 7 points at most
    }

    [Fact]
    public void A_dcs_line_list_with_a_hole_is_read_in_order()
    {
        var c = Cartridge.Parse("""
            {"type":"FA-18C_hornet","name":"","data":{"SA":{"CORRIDORS":{"3":{"id":"CORR_3","num":3,"points":[]},"1":{"id":"CORR_1","num":1,"points":[]}}}}}
            """);
        var sa = new HornetSa(c);
        Assert.Equal(["CORR_1", "CORR_3"], sa.LinesOf(HornetSa.Corridor).Select(l => (string?)l["id"]));
    }

    [Theory]
    [InlineData("SA-11", "SAM SA-11")]
    [InlineData("11", "SAM SA-11")]
    [InlineData("SAM SA-6 Kub 1S91", "SAM SA-6")]
    [InlineData("Hawk sr", "SAM Hawk")]
    [InlineData("CVN-74 John C. Stennis", "Nimitz aircraft carrier")]
    public void Known_threats_find_their_ring_type(string name, string startsWith) =>
        Assert.StartsWith(startsWith, (string?)HornetSa.RingTypeFor(Types, name)!["name"]);

    [Fact]
    public void Unknown_threat_has_no_ring_type() => Assert.Null(HornetSa.RingTypeFor(Types, "Ural truck"));
}
