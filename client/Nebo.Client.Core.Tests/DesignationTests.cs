using System.Text.Json;
using Nebo.Client.Core;
using Xunit;

namespace Nebo.Client.Core.Tests;

public class DesignationTests
{
    // As the server hook publishes it (the DLL rebuilt the pilot's part), from a real F-16 flight.
    private const string Message = """
        {"type":"designations","coalition":"blue","designations":[
         {"player":2,"name":"Kestrel","slot":"6209","aircraft":"F-16C_50","age":4,"lat":42.567367,"lon":41.628833,"alt_ft":51,
          "range_nm":20.0,"had_major_m":6482,"had_minor_m":1228,"had_pgm":2,"track":"INR POINT"},
         {"player":3,"name":"Viper 2","slot":"","aircraft":"","age":30,"lat":42.5,"lon":41.7,"track":"AREA"}]}
        """;

    private static DesignationsMessage Parse() => JsonSerializer.Deserialize<DesignationsMessage>(Message)!;

    [Fact]
    public void Message_parses_with_and_without_the_hts_ellipse()
    {
        var m = Parse();
        Assert.Equal(2, m.Designations.Count);
        var d = m.Designations[0];
        Assert.Equal(("Kestrel", "6209", 51.0, 6482.0, 2), (d.Name, d.Slot, d.AltFt!.Value, d.HadMajorM!.Value, d.HadPgm!.Value));
        Assert.Null(m.Designations[1].HadMajorM);
    }

    [Theory]
    // North-east theatre (Caucasus): the pilot nearby keeps both signs positive.
    [InlineData(42.4, 41.5, 42.567367, 41.628833)]
    // South-west theatre (South Atlantic): the pod shows 51 42.000 / 57 50.000, the pilot is at -51.6, -57.9.
    [InlineData(-51.6, -57.9, -51.7, -57.833333)]
    // Western hemisphere, northern (Nevada-like).
    [InlineData(36.2, -115.0, 36.3, -115.1)]
    public void Signs_come_from_the_pilots_own_position(double pilotLat, double pilotLon, double wantLat, double wantLon)
    {
        var (lat, lon) = Designations.Resolve(Math.Abs(wantLat), Math.Abs(wantLon), pilotLat, pilotLon);
        Assert.Equal(wantLat, lat, 6);
        Assert.Equal(wantLon, lon, 6);
    }

    [Fact]
    public void Without_the_pilots_position_north_and_east()
    {
        Assert.Equal((51.7, 57.8), Designations.Resolve(51.7, 57.8, null, null));
    }

    [Fact]
    public void Views_age_since_the_message_and_drop_old_and_dismissed_ones()
    {
        var m = Parse();
        var received = new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
        var views = Designations.Views(m.Designations, received, received.AddSeconds(10),
            slot => slot == "6209" ? (42.4, 41.5) : null, _ => false);
        Assert.Equal(2, views.Count);
        Assert.Equal(14, views[0].AgeSeconds, 3);
        Assert.Equal((42.4, 41.5), (views[0].FromLat!.Value, views[0].FromLon!.Value));
        Assert.True(views[0].Hts);
        Assert.Equal("Kestrel · HTS PGM2 3.5 NM · 0:14", views[0].Label);
        Assert.Equal("Viper 2 · TGP AREA · 0:40", views[1].Label);

        // 10 minutes on: both too old.
        Assert.Empty(Designations.Views(m.Designations, received, received.AddSeconds(600), _ => null, _ => false));
        // Dismissed here: only the other one stays.
        Assert.Single(Designations.Views(m.Designations, received, received, _ => null, d => d.Player == 2));
    }

    [Fact]
    public void Store_hides_a_dismissed_designation_until_the_pilot_designates_elsewhere()
    {
        var store = new PictureStore();
        var m = Parse();
        var now = DateTime.UtcNow;
        store.ApplyDesignations(m, now);
        store.DismissDesignation(2);
        Assert.DoesNotContain(store.DesignationViews(now), v => v.Player == 2);
        // Same point again: still hidden; a new point: back.
        store.ApplyDesignations(m, now);
        Assert.DoesNotContain(store.DesignationViews(now), v => v.Player == 2);
        var moved = m with { Designations = [m.Designations[0] with { Lat = 42.6 }, m.Designations[1]] };
        store.ApplyDesignations(moved, now);
        Assert.Contains(store.DesignationViews(now), v => v.Player == 2);
    }

    // A pilot who stepped through two emitters on the HAD, the pod now on the second.
    private const string Emitters = """
        {"type":"designations","coalition":"blue","designations":[
         {"player":2,"name":"Kestrel","slot":"6209","aircraft":"F-16C_50","age":40,"emitter":1,"current":false,
          "lat":42.567367,"lon":41.628833,"alt_ft":51,"had_major_m":6482,"had_minor_m":1228,"had_pgm":3,"track":"INR POINT"},
         {"player":2,"name":"Kestrel","slot":"6209","aircraft":"F-16C_50","age":2,"emitter":2,"current":true,
          "lat":42.61,"lon":41.71,"alt_ft":90,"had_major_m":3241,"had_minor_m":800,"had_pgm":2,"track":"INR POINT"}]}
        """;

    [Fact]
    public void Kept_emitters_are_numbered_and_only_the_current_one_is_current()
    {
        var m = JsonSerializer.Deserialize<DesignationsMessage>(Emitters)!;
        var received = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        var views = Designations.Views(m.Designations, received, received, _ => null, _ => false);
        Assert.Equal(["Kestrel · HTS 1 PGM3 3.5 NM · 0:40", "Kestrel · HTS 2 PGM2 1.8 NM · 0:02"], views.Select(v => v.Label).ToList());
        Assert.Equal([false, true], views.Select(v => v.Current).ToList());
        // A server before kept emitters sends neither field: the one designation is current.
        Assert.True(Designations.Views(Parse().Designations, received, received, _ => null, _ => false)[0].Current);
    }

    [Fact]
    public void A_hornets_atflir_point_is_labelled_flir()
    {
        var m = Parse();
        var hornet = m with { Designations = [m.Designations[1] with { Name = "Hornet", Aircraft = "FA-18C_hornet", Track = "FLIR SCENE" }] };
        var received = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal("Hornet · FLIR SCENE · 0:30", Designations.Views(hornet.Designations, received, received, _ => null, _ => false)[0].Label);
    }

    [Fact]
    public void Clearing_one_emitter_keeps_the_pilots_others()
    {
        var store = new PictureStore();
        var now = DateTime.UtcNow;
        store.ApplyDesignations(JsonSerializer.Deserialize<DesignationsMessage>(Emitters)!, now);
        store.DismissDesignation(2, 1);
        Assert.Equal([2], store.DesignationViews(now).Select(v => v.Emitter).ToList());
    }
}
