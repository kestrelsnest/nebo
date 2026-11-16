using Nebo.Client.Core;

namespace Nebo.Client.Core.Tests;

public class FormationTests
{
    // Aircraft at 42 N 42 E heading 090 at 7620 m / 450 kt, moved east/north by metres.
    private static Unit At(long id, double eastM = 0, double northM = 0, double altM = 7620, double spdMps = 231.5, double hdg = 90,
        string? player = null, string coalition = "blue", bool detected = false, string? type = "F-16C_50")
    {
        var (lat, lon) = Nav.Destination(42, 42, Nav.Normalize(Math.Atan2(eastM, northM) * 180 / Math.PI), Math.Sqrt(eastM * eastM + northM * northM));
        return SymbologyTests.Make(id: id, player: player, coalition: coalition, detected: detected, type: type, altM: altM, spdMps: spdMps)
            with { Lat = lat, Lon = lon, Hdg = hdg };
    }

    [Fact]
    public void A_pair_in_formation_is_one_flight_with_both_names_and_one_altitude_line()
    {
        var flights = Formations.Group([At(2, player: "HART"), At(1, eastM: 1500, player: "Bullat95")], "blue");
        var f = Assert.Single(flights);
        Assert.Equal(1, f.Key);
        Assert.Equal(["Bullat95 F16C", "HART F16C", Symbology.AltLine(f.Lead)], Formations.Block(f, LabelLevel.Full));
    }

    [Theory]
    [InlineData(3000, 0, 0, 90)]      // 1.6 NM apart
    [InlineData(1000, 400, 0, 90)]    // 1300 ft higher
    [InlineData(1000, 0, 30, 90)]     // 58 kt faster
    [InlineData(1000, 0, 0, 135)]     // 45° apart
    public void Separating_breaks_the_flight(double eastM, double altUpM, double fasterMps, double hdg)
    {
        var flights = Formations.Group([At(1), At(2, eastM: eastM, altM: 7620 + altUpM, spdMps: 231.5 + fasterMps, hdg: hdg)], "blue");
        Assert.Equal(2, flights.Count);
    }

    [Fact]
    public void A_trail_chains_and_sides_never_mix()
    {
        // Four in trail 1 NM apart: one flight. A hostile jet in between stays apart.
        var nm = Nav.MetersPerNm;
        var flights = Formations.Group([At(1), At(2, eastM: -1 * nm), At(3, eastM: -2 * nm), At(4, eastM: -3 * nm),
            At(9, eastM: -1 * nm, coalition: "red", detected: true, type: "MiG-29 Fulcrum")], "blue");
        Assert.Equal([4, 1], flights.Select(f => f.Members.Count).OrderByDescending(n => n).ToList());
    }

    [Fact]
    public void A_package_member_never_shares_a_block_with_others()
    {
        // Four in trail; 2 and 3 in package 1: they make one block, 1 and 4 their own (1 and 4
        // are 3 NM apart, so they don't chain past the package).
        var nm = Nav.MetersPerNm;
        var four = new[] { At(1), At(2, eastM: -1 * nm), At(3, eastM: -2 * nm), At(4, eastM: -3 * nm) };
        int? Package(Unit u) => u.Id is 2 or 3 ? 1 : null;
        var flights = Formations.Group(four, "blue", Package);
        Assert.Equal(new long[][] { [1], [2, 3], [4] }, flights.Select(f => f.Members.Select(m => m.Id).ToArray()).ToArray());
        // Without packages they are one flight, as before.
        Assert.Single(Formations.Group(four, "blue"));
    }

    [Fact]
    public void Contacts_without_names_are_counted_by_type()
    {
        var red = (long id, string type, double e) => At(id, eastM: e, coalition: "red", detected: true, type: type);
        var f = Assert.Single(Formations.Group([red(5, "MiG-29 Fulcrum", 0), red(6, "MiG-29 Fulcrum", 900), red(7, "Su-27", 1800)], "blue"));
        var block = Formations.Block(f, LabelLevel.Full);
        Assert.Equal(["2× MiG29", "1× Su27"], block.Take(2));
        Assert.Equal(["2× MiG29 " + Symbology.AltitudeHundreds(f.Lead) + " "], Formations.Block(f with { Members = f.Members.Take(2).ToList() }, LabelLevel.Short));
        Assert.Equal(["3× MiG29+Su27 " + Symbology.AltitudeHundreds(f.Lead) + " "], Formations.Block(f, LabelLevel.Short));
    }

    [Fact]
    public void Blocks_avoid_each_other_and_the_symbols_and_dragged_ones_stay_put()
    {
        // Two symbols 20 px apart: the second block must not sit on the first.
        var tags = new List<TagLayout.Tag> { new(1, 100, 100, 80, 26, null), new(2, 120, 100, 80, 26, null) };
        var symbols = new List<Box> { new(92, 92, 16, 16), new(112, 92, 16, 16) };
        var boxes = TagLayout.Place(tags, symbols);
        Assert.False(boxes[0].Overlaps(boxes[1]));
        Assert.All(boxes, b => Assert.DoesNotContain(symbols, s => s.Overlaps(b)));
        // Up-right is the first choice for the first block.
        Assert.Equal(TagLayout.At(tags[0], (1, -1)), boxes[0]);
        // A dragged block keeps its offset, and the other one moves out of its way.
        tags[1] = tags[1] with { Dragged = (-100, -60) };
        boxes = TagLayout.Place(tags, symbols);
        Assert.Equal(new Box(20, 40, 80, 26), boxes[1]);
        Assert.False(boxes[0].Overlaps(boxes[1]));
    }

    [Fact]
    public void Above_puts_the_block_over_the_aircraft_behind_it_and_keeps_it_steady_through_turns()
    {
        var memory = new TagLayout.Memory();
        Box At(double hdgDeg) => TagLayout.Place([new TagLayout.Tag(1, 100, 100, 80, 26, null, hdgDeg * Math.PI / 180)], [],
            TagPlacement.Above, memory)[0];
        // Heading east: above and to the left (behind).
        var east = At(90);
        Assert.True(east.Bottom < 100 && east.Right < 100, east.ToString());
        // Turning to 060 and 120: the same place, not a pixel up or down.
        Assert.Equal(east, At(60));
        Assert.Equal(east, At(120));
        // Through north (010, 350): still the same side; only well past it (330) does it swap.
        Assert.Equal(east, At(10));
        Assert.Equal(east, At(350));
        var west = At(330);
        Assert.True(west.X > 100 && west.Bottom < 100, west.ToString());
        Assert.Equal(east.Y, west.Y);
    }

    [Fact]
    public void A_block_keeps_its_spot_while_it_is_clear()
    {
        var memory = new TagLayout.Memory();
        var a = new TagLayout.Tag(1, 100, 100, 80, 26, null, Math.PI / 2);
        // Something sits where its first choice would be: it takes another spot.
        var blocker = new Box(5, 55, 90, 40);
        var first = TagLayout.Place([a], [blocker], TagPlacement.Above, memory)[0];
        Assert.False(first.Overlaps(blocker));
        // The blocker goes: the block stays where it is rather than jumping back.
        Assert.Equal(first, TagLayout.Place([a], [], TagPlacement.Above, memory)[0]);
    }

    [Fact]
    public void Moving_aircraft_get_their_blocks_beside_their_vectors_on_different_sides()
    {
        // Heading east (90°): the right of the vector is south (down). Alone: below it.
        var east = Math.PI / 2;
        var alone = TagLayout.Place([new TagLayout.Tag(1, 100, 100, 80, 26, null, east)], []);
        Assert.True(alone[0].Y > 100 && alone[0].X < 100 && alone[0].Right > 100, alone[0].ToString());

        // Two heading east 20 px apart (B south of A), each vector 60 px long: obstacles.
        Box Sym(double x, double y) => new(x - 8, y - 8, 16, 16);
        var obstacles = new List<Box> { Sym(100, 100), Sym(100, 120) };
        foreach (var y in new[] { 100.0, 120.0 })
            for (double s = 12; s <= 60; s += 6) obstacles.Add(new Box(100 + s - 3, y - 3, 6, 6));
        var tags = new List<TagLayout.Tag> { new(1, 100, 100, 80, 26, null, east), new(2, 100, 120, 80, 26, null, east) };
        var boxes = TagLayout.Place(tags, obstacles);
        Assert.False(boxes[0].Overlaps(boxes[1]));
        Assert.All(boxes, b => Assert.DoesNotContain(obstacles, o => o.Overlaps(b)));
        // On different sides: one above both aircraft, one below both.
        Assert.True(boxes.Any(b => b.Bottom < 100 - 8) && boxes.Any(b => b.Y > 120 + 8), string.Join(" ", boxes));
    }

    [Fact]
    public void Intercept_of_a_head_on_target_meets_in_the_middle()
    {
        // Target 20 NM due north heading south, both 250 m/s: meet 10 NM north, heading 000.
        var (tLat, tLon) = Nav.Destination(42, 42, 0, 20 * Nav.MetersPerNm);
        var s = Intercept.Solve(42, 42, 250, tLat, tLon, 180, 250)!;
        Assert.InRange(s.Seconds, 20 * Nav.MetersPerNm / 500 * 0.995, 20 * Nav.MetersPerNm / 500 * 1.005);  // flat-earth solver: within 0.5 %
        Assert.Equal(0, Math.Abs(Nav.Normalize(s.HeadingTrue + 180) - 180), 1);
        Assert.InRange(Nav.BearingRange(42, 42, s.Lat, s.Lon).Meters, 10 * Nav.MetersPerNm * 0.995, 10 * Nav.MetersPerNm * 1.005);
    }

    [Fact]
    public void Intercept_of_a_crossing_target_leads_it_and_a_faster_one_running_away_has_none()
    {
        // Target 20 NM north crossing east at 200 m/s, interceptor 300 m/s: lead to the east of north.
        var (tLat, tLon) = Nav.Destination(42, 42, 0, 20 * Nav.MetersPerNm);
        var s = Intercept.Solve(42, 42, 300, tLat, tLon, 90, 200)!;
        Assert.InRange(s.HeadingTrue, 30, 50);
        // Both at the meeting point at the same time: the target's run equals its speed × time.
        var (tgtBrg, tgtRun) = Nav.BearingRange(tLat, tLon, s.Lat, s.Lon);
        Assert.InRange(tgtRun, 200 * s.Seconds * 0.995, 200 * s.Seconds * 1.005);
        Assert.InRange(Nav.BearingRange(42, 42, s.Lat, s.Lon).Meters, 300 * s.Seconds * 0.995, 300 * s.Seconds * 1.005);
        // Running away faster than the interceptor: none.
        Assert.Null(Intercept.Solve(42, 42, 200, tLat, tLon, 0, 300));
    }
}
