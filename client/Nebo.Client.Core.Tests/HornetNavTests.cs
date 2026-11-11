using Nebo.Client.Core.Dtc;

namespace Nebo.Client.Core.Tests;

[Trait("Needs", "DCS")]  // tables made from a DCS install (DtcTestData); GitHub's build has none
public class HornetNavTests
{
    private static (Cartridge C, HornetNav Nav) New()
    {
        var c = Cartridge.NewDefault(Cartridge.HornetType, "Caucasus");
        return (c, new HornetNav(c));
    }

    [Fact]
    public void New_waypoints_take_the_first_free_number_with_dcs_defaults()
    {
        var (_, nav) = New();
        var a = nav.Add(-1000, 240000, 30)!;
        var b = nav.Add(-2000, 241000, 40)!;
        Assert.Equal("STPT1", a["id"]!.GetValue<string>());
        Assert.Equal(2, HornetNav.Num(b));
        Assert.Equal(1, a["altitudeType"]!.GetValue<int>());
        Assert.Equal("OA1Line", a["idOA_Line"]!.GetValue<string>());
        nav.Remove(1);
        Assert.Equal(1, HornetNav.Num(nav.Add(0, 0, 0)!));  // the freed number is reused
    }

    [Fact]
    public void At_most_59_waypoints()
    {
        var (_, nav) = New();
        for (var i = 0; i < HornetNav.MaxWaypoints; i++) Assert.NotNull(nav.Add(i, i, 0));
        Assert.Null(nav.Add(0, 0, 0));
    }

    [Fact]
    public void Route_keeps_order_and_legs_like_dcs()
    {
        var (_, nav) = New();
        for (var i = 0; i < 3; i++) nav.Add(i * 1000, 0, 300);
        Assert.True(nav.AddToRoute(1, 3));
        Assert.True(nav.AddToRoute(1, 1));
        Assert.False(nav.AddToRoute(1, 1));                 // already in it
        Assert.Equal([3, 1], nav.Route(1));
        var leg = nav.Leg(1, 3)!;
        Assert.Equal(463.0, leg["speed"]!.GetValue<double>());
        Assert.Equal(300, leg["alt"]!.GetValue<double>());
        nav.Reorder(1, [1, 3]);
        Assert.Equal([1, 3], nav.Route(1));
        nav.RemoveFromRoute(1, 3);
        Assert.Equal([1], nav.Route(1));
        Assert.Null(nav.Leg(1, 3));
        Assert.Null(nav.Waypoint(3)!["R1_order"]);
    }

    [Fact]
    public void A_route_holds_15_points()
    {
        var (_, nav) = New();
        for (var i = 1; i <= 16; i++) nav.Add(i, i, 0);
        for (var i = 1; i <= 15; i++) Assert.True(nav.AddToRoute(2, i));
        Assert.False(nav.AddToRoute(2, 16));
    }

    [Fact]
    public void Removing_a_waypoint_takes_it_out_of_its_routes()
    {
        var (_, nav) = New();
        nav.Add(0, 0, 0);
        nav.Add(1, 1, 0);
        nav.AddToRoute(1, 1);
        nav.AddToRoute(3, 1);
        nav.Remove(1);
        Assert.Empty(nav.Route(1));
        Assert.Empty(nav.Route(3));
        Assert.Null(nav.Leg(3, 1));
    }

    [Fact]
    public void Altitude_change_reaches_the_route_legs_and_speed_is_clamped()
    {
        var (_, nav) = New();
        nav.Add(0, 0, 0);
        nav.AddToRoute(1, 1);
        nav.SetAltitude(1, 6096, HornetNav.Radar);
        Assert.Equal(6096, nav.Leg(1, 1)!["alt"]!.GetValue<double>());
        Assert.Equal(2, nav.Leg(1, 1)!["altitudeType"]!.GetValue<int>());
        nav.SetSpeed(1, 1, 5000);
        Assert.Equal(HornetNav.MaxSpeedKmh, nav.Leg(1, 1)!["speed"]!.GetValue<double>());
    }

    [Fact]
    public void Build_route_takes_the_points_in_order_up_to_15()
    {
        var (_, nav) = New();
        for (var i = 1; i <= 17; i++) nav.Add(i, i, 0);
        nav.AddToRoute(1, 17);
        Assert.Equal(15, nav.BuildRoute(1, Enumerable.Range(1, 17).Reverse().ToList()));
        Assert.Equal(Enumerable.Range(3, 15).Reverse(), nav.Route(1));   // 17 was replaced, 2 and 1 did not fit
        Assert.Null(nav.Leg(1, 1));
    }

    [Fact]
    public void Renumber_follows_the_list_and_keeps_routes()
    {
        var (_, nav) = New();
        nav.Add(100, 0, 10);   // 1
        nav.Add(200, 0, 20);   // 2
        nav.Add(300, 0, 30);   // 3
        nav.AddToRoute(1, 1);
        nav.AddToRoute(1, 3);
        nav.Renumber([3, 1, 2]);   // the old 3 is now first
        Assert.Equal(300, HornetNav.Number(nav.Waypoint(1)!["x"]));
        Assert.Equal("STPT1", nav.Waypoint(1)!["id"]!.GetValue<string>());
        Assert.Equal("OA2Line", nav.Waypoint(2)!["idOA_Line"]!.GetValue<string>());
        // Route 1 was old 1 then old 3: now waypoints 2 then 1, legs renamed with them.
        Assert.Equal([2, 1], nav.Route(1));
        Assert.Equal(2, HornetNav.Number(nav.Leg(1, 2)!["wypt_num"]));
        Assert.Equal(30, HornetNav.Number(nav.Leg(1, 1)!["alt"]));
        Assert.Null(nav.Leg(1, 3));
    }

    [Fact]
    public void Leg_lengths_in_metres()
    {
        var (_, nav) = New();
        nav.Add(0, 0, 0);
        nav.Add(3000, 4000, 0);
        nav.Add(3000, 4000 + 1852, 0);
        nav.BuildRoute(2, [1, 2, 3]);
        Assert.Equal([5000, 1852], nav.LegLengths(2).Select(m => Math.Round(m)));
    }

    [Fact]
    public void Tacan_and_icls_settings()
    {
        var (_, nav) = New();
        nav.SetTacan(74, 'X', 1, true);
        nav.SetIcls(11, true);
        Assert.Equal(74, nav.Settings["TACAN"]!["Channel"]!.GetValue<int>());
        Assert.Equal(1, nav.Settings["TACAN"]!["ChannelMode"]!.GetValue<int>());
        Assert.True(nav.Settings["ICLS"]!["OnOff"]!.GetValue<bool>());
    }

    [Fact]
    public void Reads_a_real_dcs_route_as_dcs_orders_it()
    {
        // As in a cartridge DCS saved: R1_order on the points, legs keyed STPT<n>.
        var c = Cartridge.Parse("""
            {"type":"FA-18C_hornet","name":"","data":{"WYPT":{"NAV_PTS":[
              {"id":"STPT1","wypt_num":1,"x":1,"y":1,"alt":914.4,"altitudeType":1,"R1":true,"R1_order":2,"R2":false,"R3":false},
              {"id":"STPT2","wypt_num":2,"x":2,"y":2,"alt":1524,"altitudeType":1,"R1":true,"R1_order":1,"R2":false,"R3":false}],
              "NAV_ROUTE":[{"STPT1":{"speed":463}},[],[]]}}}
            """);
        var nav = new HornetNav(c);
        Assert.Equal([2, 1], nav.Route(1));
        Assert.Empty(nav.Route(2));
        Assert.True(nav.AddToRoute(2, 1));   // an empty [] route becomes an object
        Assert.NotNull(nav.Leg(2, 1));
    }
}
