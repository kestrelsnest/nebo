using Nebo.Client.Core;

namespace Nebo.Client.Core.Tests;

public class EngagementTests
{
    private static readonly DateTime T0 = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    // An aircraft at a point east/north of 42 N 42 E, heading and speed given.
    private static Unit Air(long id, double eastNm, double northNm, double hdg, double spdMps, string coalition = "blue", bool detected = false,
        string? player = null, string type = "F-16C_50")
    {
        var e = eastNm * Nav.MetersPerNm;
        var n = northNm * Nav.MetersPerNm;
        var (lat, lon) = Nav.Destination(42, 42, Nav.Normalize(Math.Atan2(e, n) * 180 / Math.PI), Math.Sqrt(e * e + n * n));
        return SymbologyTests.Make(id: id, coalition: coalition, detected: detected, player: player, type: type) with { Lat = lat, Lon = lon, Hdg = hdg, Spd = spdMps };
    }

    private static PictureStore Store(params Unit[] units)
    {
        var store = new PictureStore();
        store.Apply("blue", new Picture(1, "blue", 0, "Caucasus", units), T0);
        return store;
    }

    private static LineEnd On(Unit u) => new(u.Id, u.Lat!.Value, u.Lon!.Value);

    [Fact]
    public void A_head_on_line_closes_at_both_speeds_and_has_an_intercept()
    {
        var ftr = Air(1, 0, 0, 0, 250, player: "Bullat95");
        var tgt = Air(9, 0, 30, 180, 250, "red", detected: true, type: "MiG-29 Fulcrum");
        var book = new EngagementBook();
        book.Add(On(ftr), On(tgt));
        var row = Assert.Single(book.Rows(Store(ftr, tgt)));
        Assert.Equal(("Bullat95 F16C", "MiG29"), (row.From, row.To));
        Assert.Equal(30, row.RangeNm, 1);
        Assert.Equal(500 * Unit.KnotsPerMps, row.ClosureKt!.Value, 0);
        Assert.NotNull(row.Intercept);
    }

    [Fact]
    public void Lines_sort_by_range_or_by_the_range_left_to_their_alert()
    {
        var ftr = Air(1, 0, 0, 0, 250);
        var near = Air(9, 0, 20, 180, 250, "red", detected: true);
        var far = Air(8, 0, 40, 180, 250, "red", detected: true);
        var book = new EngagementBook();
        var a = book.Add(On(ftr), On(near));
        var b = book.Add(On(ftr), On(far));
        var store = Store(ftr, near, far);
        Assert.Equal([a.Id, b.Id], book.Rows(store).Select(r => r.Line!.Id));
        // The far one has an alert at 35 NM: 5 NM to go, ahead of the near one's 20.
        book.SetAlert(b.Id, 35);
        Assert.Equal([b.Id, a.Id], book.Rows(store).Select(r => r.Line!.Id));
    }

    [Fact]
    public void The_alert_sounds_once_when_the_range_closes_and_rearms_when_it_opens()
    {
        var ftr = Air(1, 0, 0, 0, 250);
        var book = new EngagementBook();
        var line = book.Add(On(ftr), new LineEnd(9, 0, 0));
        book.SetAlert(line.Id, 20);
        int Fires(double tgtNorthNm) => book.CheckAlerts(book.Rows(Store(ftr, Air(9, 0, tgtNorthNm, 180, 250, "red", detected: true)))).Count;
        Assert.Equal(0, Fires(25));
        Assert.Equal(1, Fires(19.5));
        Assert.Equal(0, Fires(15));     // still inside: once only
        Assert.Equal(0, Fires(21));     // out, but not 2 NM past: not re-armed
        Assert.Equal(0, Fires(22.5));   // re-armed here
        Assert.Equal(1, Fires(19));
    }

    [Theory]
    [InlineData(19.9, 20.0, "inside")]
    [InlineData(25, 20.0, "near")]
    [InlineData(30.5, 20.0, "far")]
    [InlineData(15, null, "inside")]
    [InlineData(40, null, "near")]
    [InlineData(41, null, "far")]
    public void Stream_deck_level_follows_the_alert_or_the_range(double rangeNm, double? alertNm, string level) =>
        Assert.Equal(level, EngagementBook.Level(rangeNm, alertNm));

    [Fact]
    public void A_marker_is_on_one_line_at_a_time()
    {
        var book = new EngagementBook();
        var a = book.Add(new LineEnd(null, 42, 42), new LineEnd(null, 42.1, 42));
        var b = book.Add(new LineEnd(null, 42, 42), new LineEnd(null, 42.2, 42));
        book.SetMarker(a.Id, 3);
        book.SetMarker(b.Id, 3);
        Assert.Null(a.Marker);
        Assert.Equal(b, book.WithMarker(3));
        book.SetMarker(b.Id, 9);  // out of range: ignored
        Assert.Equal(3, b.Marker);
        book.SetMarker(b.Id, null);
        Assert.Null(book.WithMarker(3));
    }

    [Fact]
    public void Launches_at_our_players_come_first()
    {
        var player = Air(1, 0, 0, 0, 250, player: "HART");
        var tgt = Air(9, 0, 5, 180, 250, "red", detected: true);
        var store = new PictureStore();
        store.Apply("blue", new Picture(1, "blue", 0, "Caucasus", [player, tgt]), T0);
        var (mlat, mlon) = Nav.Destination(player.Lat!.Value, player.Lon!.Value, 0, 10 * Nav.MetersPerNm);
        store.Apply("blue", new Picture(1, "blue", 1, "Caucasus", [player, tgt],
            Weapons: [new Weapon(77, "R-77", "red", 9, 1, mlat, mlon, 8000, 180, 900, 0)]), T0.AddSeconds(1));
        var book = new EngagementBook();
        book.Add(On(player), On(tgt));
        var rows = book.Rows(store);
        Assert.Equal(2, rows.Count);
        Assert.True(rows[0].IsLaunch);
        Assert.Equal(("R-77", "HART"), (rows[0].From, rows[0].To));
        Assert.Equal(10, rows[0].RangeNm, 1);
    }

    [Fact]
    public void A_line_whose_contact_is_lost_is_dropped_and_a_line_to_a_map_point_stays()
    {
        var ftr = Air(1, 0, 0, 0, 250);
        var tgt = Air(9, 0, 20, 180, 250, "red", detected: true);
        var store = new PictureStore();
        store.Apply("blue", new Picture(1, "blue", 0, "Caucasus", [ftr, tgt]), T0);
        var book = new EngagementBook();
        book.Add(On(ftr), On(tgt));
        var toPoint = book.Add(On(ftr), new LineEnd(null, 42.5, 42.5));
        Assert.Equal(0, book.DropLost(store));
        // The contact drops out of the picture: lost (fading) — its line goes, the other stays.
        store.Apply("blue", new Picture(1, "blue", 1, "Caucasus", [ftr]), T0.AddSeconds(1));
        Assert.Equal(1, book.DropLost(store));
        Assert.Equal([toPoint], book.Lines);
    }
}
