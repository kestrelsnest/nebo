namespace Nebo.Client.Core.Tests;

public class FlightBoardTests
{
    private static FlightInfo F(long unit, string state, string ev, long? owner, double since = 10) =>
        new(unit, unit + 16_000_000, 1, "Akrotiri_F/A-18C", "Viper" + unit, "FA-18C_hornet", state, 22, "Akrotiri", since, ev, owner);

    private static FlightsMessage M(params FlightInfo[] f) => new("blue", f, []);

    [Fact]
    public void First_list_raises_nothing_then_a_new_unowned_spawn_does()
    {
        var b = new FlightBoard { MyId = 5 };
        var spawned = new List<long>();
        b.Spawned += f => spawned.Add(f.Unit);
        b.ApplyFlights(M(F(1, "ground", "birth", null)));
        Assert.Empty(spawned);
        b.ApplyFlights(M(F(1, "ground", "birth", null), F(2, "ground", "birth", null), F(3, "ground", "birth", 7)));
        Assert.Equal([2], spawned);
        Assert.True(b.Available);
    }

    [Fact]
    public void Take_off_is_raised_only_for_my_pilots_and_once()
    {
        var b = new FlightBoard { MyId = 5 };
        var off = new List<long>();
        b.TookOff += f => off.Add(f.Unit);
        b.ApplyFlights(M(F(1, "ground", "birth", 5), F(2, "ground", "birth", 7)));
        b.ApplyFlights(M(F(1, "air", "takeoff", 5), F(2, "air", "takeoff", 7)));
        b.ApplyFlights(M(F(1, "air", "takeoff", 5), F(2, "air", "takeoff", 7)));
        Assert.Equal([1], off);
    }

    [Fact]
    public void Handoff_offered_to_me_and_my_handoff_answered()
    {
        var b = new FlightBoard { MyId = 5 };
        var offered = new List<long>();
        var answered = new List<(long, bool)>();
        b.Offered += h => offered.Add(h.Id);
        b.Answered += (h, ok) => answered.Add((h.Id, ok));
        b.ApplyFlights(M(F(1, "air", "takeoff", 5), F(2, "air", "takeoff", 7)));
        b.ApplyFlights(new("blue", [F(1, "air", "takeoff", 5), F(2, "air", "takeoff", 7)],
            [new(10, 5, 7, [1], "", 0, 1), new(11, 7, 5, [2], "Pkg", 251000, 1)]));
        Assert.Equal([11], offered);
        // 7 takes unit 1 (accepted); I decline 11.
        b.ApplyFlights(M(F(1, "air", "takeoff", 7), F(2, "air", "takeoff", 7)));
        Assert.Equal([(10L, true)], answered);
    }

    [Fact]
    public void Controller_names_and_initials()
    {
        var c = new ControllerInfo(3, "Overlord", "kestrel", "gci", 251000);
        Assert.Equal("Overlord (GCI) 251.000", c.Describe);
        Assert.Equal("OV", c.Initials);
        Assert.Equal("kestrel", (c with { Callsign = "" }).Display);
        Assert.Equal("#3", (c with { Callsign = "", User = "" }).Display);
    }

    [Fact]
    public void Area_flags_follow_the_areas()
    {
        Assert.Equal("01", Areas.SetFlag("", 1, true, 3));
        Assert.Equal("", Areas.SetFlag("01", 1, false, 3));
        Assert.Equal("11", Areas.RemoveFlag("011", 0));
        Assert.Equal("", Areas.RemoveFlag("10", 0));
        Assert.True(Areas.Flag("01", 1));
        Assert.False(Areas.Flag("01", 5));
        var c = new ControllerInfo(1, "Magic", "u", "gci", 0, F10: "001");
        Assert.True(c.OnF10(2));
        Assert.False(c.OnF10(0));
    }

    [Fact]
    public void Area_round_trips_and_contains()
    {
        var square = new List<(double, double)> { (34.0, 32.0), (34.0, 33.0), (35.0, 33.0), (35.0, 32.0) };
        var text = Areas.Format(square);
        Assert.Equal("34,32;34,33;35,33;35,32", text);
        var c = new ControllerInfo(3, "Overlord", "", "gci", 0, "#FF7A1A", text + "|40,40;40,41;41,41");
        Assert.Equal(2, c.AreaPolygons.Count);
        Assert.Equal(4, c.AreaPolygons[0].Count);
        Assert.True(Areas.Contains(c.AreaPolygons, 34.5, 32.5));
        Assert.True(Areas.Contains(c.AreaPolygons, 40.2, 40.8));
        Assert.False(Areas.Contains(c.AreaPolygons, 35.5, 32.5));
        Assert.Equal(text + "|40,40;40,41;41,41", Areas.FormatAll(c.AreaPolygons));
        Assert.Equal(((byte)0xFF, (byte)0x7A, (byte)0x1A), c.Rgb);
        Assert.Empty(Areas.Parse("34,32;35,33"));  // two points are no area
    }

    [Theory]
    [InlineData("Overlord", true)]
    [InlineData("Akrotiri Tower", true)]
    [InlineData("Magic 1-1 / O'Neil:", true)]
    [InlineData("Небо", true)]
    [InlineData("Over<lord>", false)]
    [InlineData("A callsign far too long here", false)]
    public void Callsign_rule_matches_the_server(string callsign, bool ok) => Assert.Equal(ok, Callsigns.IsValid(callsign));

    [Fact]
    public void Atc_channel_reuses_the_tuned_one_then_an_empty_one_then_the_oldest()
    {
        var tuned = new Dictionary<int, double?> { [4] = 251.0, [5] = 260.0, [6] = null };
        Assert.Equal(5, ChannelPlan.ChooseAtc(c => tuned[c], 260.0, new Dictionary<int, DateTime>()));
        Assert.Equal(6, ChannelPlan.ChooseAtc(c => tuned[c], 122.1, new Dictionary<int, DateTime>()));
        tuned[6] = 118.0;
        var used = new Dictionary<int, DateTime> { [4] = new(2026, 1, 1, 10, 0, 0), [5] = new(2026, 1, 1, 9, 0, 0), [6] = new(2026, 1, 1, 11, 0, 0) };
        Assert.Equal(5, ChannelPlan.ChooseAtc(c => tuned[c], 122.1, used));
    }
}
