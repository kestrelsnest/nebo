using System.Text.Json.Nodes;
using Nebo.Client.Core.Dtc;

namespace Nebo.Client.Core.Tests;

[Trait("Needs", "DCS")]  // tables made from a DCS install (DtcTestData); GitHub's build has none
public class TomcatTests
{
    private static Cartridge New() => Cartridge.NewDefault(Cartridge.TomcatType, "Caucasus");
    private static TomcatNav.At At(double x, double y) => new(x, y, 42, 41, 100);

    [Fact]
    public void Default_cartridge_is_dcs_s()
    {
        var c = New();
        Assert.Equal("F-14BU", c.Type);
        Assert.Equal(12, ((JsonArray)c.Data["NAV"]!).Count);
        Assert.Equal(8, TomcatCmds.Programs.Count);
        Assert.Equal(5, TomcatCmds.Categories.Count);
        Assert.Equal("NONE", TomcatCmds.AutoNames[0]);
        Assert.Equal((5, 3), new TomcatCmds(c).Auto("SAM"));
    }

    [Fact]
    public void Launch_zone_like_dcs_s_lookup()
    {
        // DCS's own init() stored these for a new target (450 kt, 20,000 ft).
        var t = new TomcatJdam(New()).Target(1, 1)!;
        var (rmin, rmax, half) = TomcatJdam.Lar(450, 20);
        Assert.Equal(HornetNav.Number(t["lar_rmin_nmi"])!.Value, rmin, 12);
        Assert.Equal(HornetNav.Number(t["lar_rmax_nmi"])!.Value, rmax, 12);
        Assert.Equal(HornetNav.Number(t["lar_half_angle_deg"])!.Value, half, 12);
    }

    [Fact]
    public void Plan_1_takes_no_waypoints_and_a_plan_holds_50_points()
    {
        var nav = new TomcatNav(New());
        Assert.Null(nav.AddWaypoint(1, At(0, 0)));
        var line = nav.AddLine(2)!.Value;
        for (var i = 0; i < 9; i++) Assert.True(nav.InsertLinePoint(2, line, i, At(i, 0)));
        Assert.False(nav.InsertLinePoint(2, line, 9, At(9, 0)));     // 9 points a line
        Assert.False(nav.SetClosed(2, line, true));                   // closed: 8 at most
        for (var i = 0; i < 50; i++) nav.AddWaypoint(2, At(0, i));
        Assert.Equal(50, nav.TotalPoints(2));
        Assert.Null(nav.AddExtra(2, At(0, 0)));
    }

    [Fact]
    public void Waypoint_fields_like_the_dialog()
    {
        var nav = new TomcatNav(New());
        nav.AddWaypoint(2, At(0, 0));
        var wp = nav.Waypoints(2)[0];
        Assert.Equal(328, HornetNav.Number(wp["elev"]));              // 100 m in whole feet
        Assert.Equal(42, HornetNav.Number(wp["lat"]));
        nav.SetWaypoint(2, 1, "TOOLONGNAME", 500, 420, "12:30:00");
        Assert.Equal("TOOLONGN", (string?)wp["name"]);
        Assert.Equal(0, HornetNav.Number(wp["spd"]));                 // a time over target: no speed
        nav.SetRouteAsLine(2, true);
        Assert.Equal(3, nav.LineLimit(2));
    }

    [Fact]
    public void Jdam_launch_point_stays_in_the_zone_and_turns_with_the_heading()
    {
        var jdam = new TomcatJdam(New());
        jdam.Place(2, 3, At(10000, 20000));
        var t = jdam.Target(2, 3)!;
        Assert.True(TomcatJdam.IsActive(t));
        var (mid, _) = TomcatJdam.Axis(t);
        Assert.Equal(mid.X, HornetNav.Number(t["launch_x"])!.Value, 6);   // a new one: the middle
        jdam.Set(2, 3, "BRIDGE", 90, 30000, 500, null, 60);
        var (mid2, _) = TomcatJdam.Axis(t);
        // Turned with the heading and kept inside the new zone (the middle here).
        Assert.True(Math.Abs(HornetNav.Number(t["launch_y"])!.Value - 20000) > 1000);
        Assert.Equal(60, HornetNav.Number(t["impact_angle"]));
        jdam.SetLaunchPoint(2, 3, 10000 + 500_000, 20000);              // far outside: onto the edge
        var lx = HornetNav.Number(t["launch_x"])!.Value;
        Assert.True(lx < 10000 + 20 * 1852);
        Assert.Equal(270, TomcatJdam.HeadingFrom(t, 10000, 20000 + 5000), 1);   // handle east of it: attack heading west
        jdam.Clear(2, 3);
        Assert.False(TomcatJdam.IsActive(jdam.Target(2, 3)!));
        _ = mid2;
    }

    [Fact]
    public void Tis_callsigns_and_cmds_overrides_like_dcs()
    {
        var c = New();
        var tis = new TomcatTis(c);
        Assert.Equal("ENFIEL", TomcatTis.Callsign("enfield-1x"));   // A–Z 0–9 blank, the first 6
        Assert.True(tis.AddSendTo("dodge 2"));     // 6 characters: "DODGE "
        Assert.False(tis.AddSendTo("Dodge"));      // the same once padded
        Assert.Equal(["DODGE"], tis.SendTo);
        var cmds = new TomcatCmds(c);
        cmds.SetOverride("F-15C", 2, 3);
        cmds.SetOverride("Not a type", 2, 3);
        Assert.Single(cmds.Overrides);
        cmds.SetPriority("PROG_1", 42);
        Assert.Equal(9, HornetNav.Number(cmds.Program("PROG_1")["Priority"]));
    }
}
