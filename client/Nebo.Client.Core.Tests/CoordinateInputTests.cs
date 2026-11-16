using Nebo.Client.Core;
using Xunit;

namespace Nebo.Client.Core.Tests;

public class CoordinateInputTests
{
    // Tbilisi, Batumi, Damascus, Guam, and a southern / western point.
    public static TheoryData<double, double> Places => new()
    {
        { 41.669, 44.955 }, { 41.610, 41.600 }, { 33.411, 36.516 }, { 13.584, 144.930 }, { -33.946, -18.421 },
    };

    [Theory]
    [MemberData(nameof(Places))]
    public void Ddm_as_nebo_prints_it_parses_back(double lat, double lon)
    {
        Assert.True(CoordinateInput.TryParse(Coordinates.Ddm(lat, lon), out var la, out var lo));
        Assert.Equal(lat, la, 4);
        Assert.Equal(lon, lo, 4);
    }

    [Theory]
    [MemberData(nameof(Places))]
    public void Dms_as_nebo_prints_it_parses_back(double lat, double lon)
    {
        Assert.True(CoordinateInput.TryParse(Coordinates.Dms(lat, lon), out var la, out var lo));
        Assert.Equal(lat, la, 4);
        Assert.Equal(lon, lo, 4);
    }

    [Theory]
    [MemberData(nameof(Places))]
    public void Mgrs_as_nebo_prints_it_parses_back_within_a_metre(double lat, double lon)
    {
        Assert.True(CoordinateInput.TryParse(Coordinates.Mgrs(lat, lon), out var la, out var lo));
        Assert.True(Nav.BearingRange(lat, lon, la, lo).Meters < 2);
        // Run together, and to 100 m.
        Assert.True(CoordinateInput.TryParse(Coordinates.Mgrs(lat, lon).Replace(" ", ""), out la, out lo));
        Assert.True(Nav.BearingRange(lat, lon, la, lo).Meters < 2);
        Assert.True(CoordinateInput.TryParse(Coordinates.Mgrs(lat, lon, 3), out la, out lo));
        Assert.True(Nav.BearingRange(lat, lon, la, lo).Meters < 200);
    }

    [Theory]
    [InlineData("41.669, 44.955", 41.669, 44.955)]
    [InlineData("41.669 44.955", 41.669, 44.955)]
    [InlineData("-33.946 -18.421", -33.946, -18.421)]
    [InlineData("N41 40.140 E44 57.300", 41.669, 44.955)]
    [InlineData("41 40.140N 44 57.300E", 41.669, 44.955)]
    [InlineData("E44 57.300 N41 40.140", 41.669, 44.955)]
    [InlineData("S33 56.76 W18 25.26", -33.946, -18.421)]
    [InlineData("41,669 44,955", 41.669, 44.955)]
    public void Typed_forms(string text, double lat, double lon)
    {
        Assert.True(CoordinateInput.TryParse(text, out var la, out var lo));
        Assert.Equal(lat, la, 3);
        Assert.Equal(lon, lo, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("41.5")]
    [InlineData("91 10")]
    [InlineData("41 70 44 10")]
    [InlineData("N41 40 N44 50")]
    [InlineData("41.5 30 44 10")]
    public void Nonsense_is_refused(string text) => Assert.False(CoordinateInput.TryParse(text, out _, out _));
}
