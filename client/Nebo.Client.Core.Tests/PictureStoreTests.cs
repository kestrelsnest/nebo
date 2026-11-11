using Nebo.Client.Core;

namespace Nebo.Client.Core.Tests;

public class PictureStoreTests
{
    private static readonly DateTime T0 = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

    private static Picture Pic(params Unit[] units) => new(1, "blue", 0, "Caucasus", units);

    private static Unit Own(long id) => SymbologyTests.Make(id: id);
    private static Unit Contact(long id) => SymbologyTests.Make(id: id, coalition: "red", detected: true, type: "MiG-29 Fulcrum");

    private static Weapon Missile(long id, double lat) => new(id, "AIM_120C", "blue", 1, 7, lat, 36, 8000, 90, 900, 0);

    [Fact]
    public void Missile_keeps_a_trail_and_goes_when_gone()
    {
        var store = new PictureStore();
        for (int i = 0; i < MissileTrack.TrailLength + 3; i++)
            store.Apply("blue", Pic() with { Weapons = [Missile(5, 35 + i * 0.01)] }, T0.AddSeconds(i));
        var m = Assert.Single(store.Missiles);
        Assert.Equal(MissileTrack.TrailLength, m.Trail.Count);
        Assert.Equal(m.Weapon.Lat, m.Trail[^1].Lat);

        store.Apply("blue", Pic(), T0.AddSeconds(20));
        Assert.Empty(store.Missiles);
    }

    private static Weapon Incoming(long id, long target) => new(id, null, "red", null, target, 35, 36, 8000, 270, 900, 0);

    [Fact]
    public void Launch_at_a_player_is_raised_once_and_goes_with_its_missile()
    {
        var store = new PictureStore();
        var raised = new List<Launch>();
        store.Launched += raised.Add;
        var player = Own(1) with { Player = "Kestrel", Type = "F-16C_50" };
        var ai = Own(2);
        store.Apply("blue", Pic(player, ai), T0);
        store.Apply("blue", Pic(player, ai) with { Weapons = [Incoming(10, 1), Incoming(11, 2), Missile(12, 35)] }, T0.AddSeconds(1));
        var l = Assert.Single(raised);                      // not the AI, not our own missile
        Assert.Equal(("Kestrel", 1L, 10L), (l.Player, l.Target, l.Missile));
        store.Apply("blue", Pic(player, ai) with { Weapons = [Incoming(10, 1), Incoming(13, 1)] }, T0.AddSeconds(2));
        Assert.Equal(2, raised.Count);                      // the second missile only
        Assert.Equal([13L, 10L], store.Launches.Select(x => x.Missile));   // newest first
        store.Apply("blue", Pic(player, ai) with { Weapons = [Incoming(13, 1)] }, T0.AddSeconds(3));
        Assert.Equal([13L], store.Launches.Select(x => x.Missile));
    }

    [Fact]
    public void Jump_goes_to_the_newest_then_cycles_older()
    {
        Launch L(long m, int s) => new(m, m, "p", null, T0.AddSeconds(s));
        var cycle = new LaunchCycle();
        var list = new List<Launch> { L(3, 3), L(2, 2), L(1, 1) };
        Assert.Equal(3, cycle.Next(list, T0.AddSeconds(5))!.Missile);
        Assert.Equal(2, cycle.Next(list, T0.AddSeconds(6))!.Missile);
        Assert.Equal(1, cycle.Next(list, T0.AddSeconds(7))!.Missile);
        Assert.Equal(3, cycle.Next(list, T0.AddSeconds(8))!.Missile);   // round again
        list.Insert(0, L(4, 9));                                         // a new launch: straight to it
        Assert.Equal(4, cycle.Next(list, T0.AddSeconds(10))!.Missile);
        Assert.Null(cycle.Next([], T0.AddSeconds(11)));
    }

    [Theory]
    [InlineData(1000, 0, 2.94)]      // sea level
    [InlineData(1000, 12000, 3.39)]  // above the tropopause
    public void Missile_mach(double spd, double alt, double mach) => Assert.Equal(mach, Symbology.MachOf(spd, alt), 2);

    [Fact]
    public void Picture_with_weapons_parses()
    {
        var json = """{"type":"picture","version":1,"coalition":"blue","time":1,"theatre":"Syria","bullseye":null,"units":[],"weapons":[{"id":3,"type":null,"coalition":"red","shooter":null,"target":4940,"lat":35.1,"lon":36.2,"alt":5000,"hdg":270,"spd":800,"vs":-20}]}""";
        var pic = System.Text.Json.JsonSerializer.Deserialize<Picture>(json)!;
        var w = Assert.Single(pic.Weapons!);
        Assert.Null(w.Type);
        Assert.Equal(4940, w.Target);
    }

    [Fact]
    public void Destroyed_own_unit_disappears_at_once()
    {
        var store = new PictureStore();
        store.Apply("blue", Pic(Own(1), Own(2)), T0);
        store.Apply("blue", Pic(Own(1)), T0.AddSeconds(1));
        Assert.Single(store.Tracks);
    }

    [Fact]
    public void Lost_contact_fades_then_expires()
    {
        var store = new PictureStore();
        store.Apply("blue", Pic(Contact(7)), T0);
        store.Apply("blue", Pic(), T0.AddSeconds(1));

        var track = store.Find(7)!;
        Assert.True(track.Lost);
        Assert.Equal(0.5, track.Freshness(T0.AddSeconds(15)), 3);

        store.Expire(T0.AddSeconds(31));
        Assert.Null(store.Find(7));
    }

    [Fact]
    public void Reacquired_contact_is_tracked_again()
    {
        var store = new PictureStore();
        store.Apply("blue", Pic(Contact(7)), T0);
        store.Apply("blue", Pic(), T0.AddSeconds(1));
        store.Apply("blue", Pic(Contact(7)), T0.AddSeconds(5));
        Assert.False(store.Find(7)!.Lost);
    }

    [Fact]
    public void Expiring_the_selected_track_clears_selection()
    {
        var store = new PictureStore();
        store.Apply("blue", Pic(Contact(7)), T0);
        store.Select(7);
        store.Apply("blue", Pic(), T0.AddSeconds(1));
        store.Expire(T0.AddSeconds(40));
        Assert.Null(store.SelectedId);
    }
}
