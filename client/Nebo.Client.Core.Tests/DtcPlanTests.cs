using Nebo.Client.Core.Dtc;

namespace Nebo.Client.Core.Tests;

public class DtcPlanTests
{
    private static (double, double) LatLon(double x, double y) => (x / 1e5, y / 1e5);
    private static double Ground(double x, double y) => 100;

    /// <summary>A Hornet cartridge with 3 waypoints (1 and 3 in route 1), a GPS point, a FAOR and a threat ring.</summary>
    private static Cartridge Hornet()
    {
        var c = Cartridge.NewDefault(Cartridge.HornetType, "Caucasus");
        var nav = new HornetNav(c);
        for (var i = 0; i < 3; i++) nav.SetText(HornetNav.Num(nav.Add(i * 1000, 0, 0)!), $"WP{i + 1}", "");
        nav.AddToRoute(1, 1);
        nav.AddToRoute(1, 3);
        new HornetGps(c).Add(5000, 5000, 0);
        var sa = new HornetSa(c);
        sa.AddLine(HornetSa.Faor, 0, 0);
        sa.AddPoint(HornetSa.Faor, 0, 0, 1000);
        sa.AddPoint(HornetSa.Faor, 0, 1000, 1000);
        var types = DtcDefs.For(Cartridge.HornetType)["MEZ_THRTS_defs/MEZ_THRTS_defs"]!.AsArray().OfType<System.Text.Json.Nodes.JsonObject>().ToList();
        sa.AddThreat(20000, 0, HornetSa.RingTypeFor(types, "SA-6")!);
        return c;
    }

    [Fact]
    public void Hornet_plan_goes_into_the_viper()
    {
        var plan = DtcPlan.From(Hornet());
        var viper = Cartridge.NewDefault(Cartridge.ViperType, "Caucasus");
        Assert.Empty(plan.ApplyTo(viper, LatLon, Ground));
        var stpt = new ViperNav(viper);
        Assert.Equal(3, stpt.Points.Count);
        Assert.Equal([1, 3], stpt.Route(1));
        Assert.Equal("WP2", (string?)stpt.Point(2)!["note"]);
        Assert.Single(new ViperDest(viper).Points);
        var geo = new ViperGeo(viper);
        Assert.Equal(4, geo.Line(1).Count);                     // the FAOR closed: its first point again
        var threat = new ViperThreats(viper).Point(1)!;
        Assert.Contains("SA-6", (string?)threat["threatName"]);  // the same system's type
    }

    [Fact]
    public void Viper_details_survive_a_moved_plan_point()
    {
        var plan = DtcPlan.From(Hornet());
        var viper = Cartridge.NewDefault(Cartridge.ViperType, "Caucasus");
        plan.ApplyTo(viper, LatLon, Ground);
        var stpt = new ViperNav(viper);
        stpt.SetOap(2, 1, 1000, 2000, 0);
        stpt.Set(2, 100, 3000, ViperNav.Msl, 800, 3, "TGT", "WP2");
        // The Hornet's waypoint 2 moves 500 m: the Viper's steerpoint follows, with its OA and type.
        var hornet = Hornet();
        new HornetNav(hornet).Move(2, 1500, 0);
        DtcPlan.From(hornet).ApplyTo(viper, LatLon, Ground);
        var p = stpt.Point(2)!;
        Assert.Equal(1500, HornetNav.Number(p["x"]));
        Assert.Equal("TGT", (string?)p["type"]);
        Assert.Equal(1500, ViperOffset.At(p, "OAP_1").X);
    }

    [Fact]
    public void Plan_into_the_tomcat_route_1_with_lat_lon()
    {
        var tomcat = Cartridge.NewDefault(Cartridge.TomcatType, "Caucasus");
        Assert.Empty(DtcPlan.From(Hornet()).ApplyTo(tomcat, LatLon, Ground));
        var nav = new TomcatNav(tomcat);
        var wps = nav.Waypoints(DtcPlan.TomcatPlan);
        Assert.Equal(2, wps.Count);                              // route 1: waypoints 1 and 3
        Assert.Equal("WP3", (string?)wps[1]["name"]);
        Assert.Equal(0.02, HornetNav.Number(wps[1]["lat"]));
        Assert.Equal(328, HornetNav.Number(wps[1]["elev"]));     // 100 m in feet
        Assert.True(TomcatNav.IsClosed(nav.Lines(DtcPlan.TomcatPlan)[0]));
        Assert.Single(nav.Extra(DtcPlan.TomcatPlan));
    }

    [Fact]
    public void What_does_not_fit_is_cut_and_said()
    {
        var hornet = Cartridge.NewDefault(Cartridge.HornetType, "Caucasus");
        var nav = new HornetNav(hornet);
        for (var i = 0; i < 30; i++) nav.Add(i * 100, 0, 0);
        var cut = DtcPlan.From(hornet).ApplyTo(Cartridge.NewDefault(Cartridge.ViperType, ""), LatLon, Ground);
        Assert.Contains("F-16C: 25 of 30 steerpoints", cut);
    }

    [Fact]
    public void Applying_again_changes_nothing()
    {
        var plan = DtcPlan.From(Hornet());
        foreach (var type in new[] { Cartridge.ViperType, Cartridge.TomcatType, Cartridge.HornetType })
        {
            var c = Cartridge.NewDefault(type, "Caucasus");
            plan.ApplyTo(c, LatLon, Ground);
            var once = c.ToJson();
            plan.ApplyTo(c, LatLon, Ground);
            Assert.Equal(once, c.ToJson());
        }
    }

    [Fact]
    public void A_viper_plan_goes_back_into_a_hornet()
    {
        var viper = Cartridge.NewDefault(Cartridge.ViperType, "Caucasus");
        DtcPlan.From(Hornet()).ApplyTo(viper, LatLon, Ground);
        var hornet = Cartridge.NewDefault(Cartridge.HornetType, "Caucasus");
        DtcPlan.From(viper).ApplyTo(hornet, LatLon, Ground);
        var nav = new HornetNav(hornet);
        Assert.Equal(3, nav.Waypoints.Count);
        Assert.Equal([1, 3], nav.Route(1));
        Assert.Equal("WP2", (string?)nav.Waypoint(2)!["text_note"]);
        Assert.Single(new HornetSa(hornet).LinesOf(HornetSa.Faor));   // the closed GEO line: an area again
        Assert.Empty(new HornetSa(hornet).LinesOf(HornetSa.Flot));
    }

    [Theory]
    [InlineData(Cartridge.ViperType)]
    [InlineData(Cartridge.TomcatType)]
    public void Hornet_line_kinds_survive_a_trip_through_another_jet(string type)
    {
        // A corridor, a FAOR and a FLOT: through a jet without line kinds and back, each keeps its kind.
        var hornet = Hornet();
        var sa = new HornetSa(hornet);
        sa.AddLine(HornetSa.Corridor, 30000, 0);
        sa.AddPoint(HornetSa.Corridor, 0, 30000, 9000);
        sa.AddLine(HornetSa.Flot, 40000, 0);
        sa.AddPoint(HornetSa.Flot, 0, 40000, 9000);
        var other = Cartridge.NewDefault(type, "Caucasus");
        DtcPlan.From(hornet).ApplyTo(other, LatLon, Ground);
        // The other jet's line moves: the plan comes back from it.
        var back = DtcPlan.From(other);
        var moved = back with { Lines = back.Lines.Select((l, i) => i == 0 ? l with { Points = l.Points.Select(p => p with { X = p.X + 500 }).ToList() } : l).ToList() };
        moved.ApplyTo(hornet, LatLon, Ground);
        sa = new HornetSa(hornet);
        Assert.Single(sa.LinesOf(HornetSa.Corridor));
        Assert.Equal(30500, HornetNav.Number(HornetSa.PointsOf(sa.LinesOf(HornetSa.Corridor)[0])[0]["x"]));
        Assert.Single(sa.LinesOf(HornetSa.Faor));
        Assert.Single(sa.LinesOf(HornetSa.Flot));
        Assert.Equal(3, HornetSa.PointsOf(sa.LinesOf(HornetSa.Faor)[0]).Count);   // no repeated closing point
    }
}
