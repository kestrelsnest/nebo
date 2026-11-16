using Nebo.Client.Core;

namespace Nebo.Client.Core.Tests;

public class RunwayNamesTests
{
    // DCS course: radians, the opposite sign of the true heading.
    private static Runway Rw(string name, double headingTrue, double x, double z) =>
        new(name, -headingTrue * Math.PI / 180, 3000, 45, x, z);

    private static Airfield Field(params Runway[] runways) => new(1, "Test", 0, 0, 0, 2, runways, []);

    [Fact]
    public void Parallel_runways_get_left_and_right_and_point_the_way_their_number_says()
    {
        // Two runways heading 170 true, 1 km apart east-west; DCS gave the western one the 350 course.
        var west = Rw("17", 350, 0, -500);
        var east = Rw("17", 170, 0, 500);
        var f = RunwayNames.Normalize(Field(west, east));
        var w = f.Runways[0];
        var e = f.Runways[1];
        Assert.Equal(170, w.HeadingTrue, 3);  // turned to match "17"
        // Landing on 17 (southbound) the east runway is on the left.
        Assert.Equal("17R", w.Name);
        Assert.Equal("17L", e.Name);
        Assert.Equal(("17R", "35L"), w.Designators);
        Assert.Equal(("17L", "35R"), e.Designators);
    }

    [Fact]
    public void Single_and_already_named_runways_are_left_alone()
    {
        var single = RunwayNames.Normalize(Field(Rw("09", 92, 0, 0))).Runways[0];
        Assert.Equal("09", single.Name);
        Assert.Equal(92, single.HeadingTrue, 3);
        var named = RunwayNames.Normalize(Field(Rw("05L", 50, 0, 0), Rw("05R", 50, 300, 300))).Runways;
        Assert.Equal(["05L", "05R"], named.Select(r => r.Name));
    }

    [Fact]
    public void Three_parallels_get_left_centre_right()
    {
        var f = RunwayNames.Normalize(Field(Rw("36", 0, 0, 1000), Rw("36", 0, 0, -1000), Rw("36", 0, 0, 0)));
        // Heading north, east (z+) is on the right.
        Assert.Equal(["36R", "36L", "36C"], f.Runways.Select(r => r.Name));
    }
}
