using Nebo.Client.Core;

namespace Nebo.Client.Core.Tests;

public class RadioSpotTests
{
    // 100×100 cells of 500 m, flat at 0 m with a 2000 m ridge across x = 24.5–25.5 km.
    private static TerrainGrid Ridge()
    {
        const int n = 100;
        var heights = new short[n * n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                heights[i * n + j] = i is 49 or 50 or 51 ? (short)2000 : (short)0;
        return new TerrainGrid("Test", 0, 0, 500, n, n, heights, new byte[n * n]);
    }

    [Fact]
    public void The_nearest_spot_behind_a_ridge_loses_to_a_farther_one_with_line_of_sight()
    {
        // The aircraft low at x = 30 km. The near airfield (x = 20 km) is behind the ridge; the
        // far one (x = 45 km) is on the aircraft's side.
        var near = new RadioSpot("airfield", "Near", 1, null, 20_000, 10_000, 10);
        var far = new RadioSpot("airfield", "Far", 2, null, 45_000, 10_000, 10);
        var best = RadioSpots.Best([near, far], 30_000, 10_000, 300, Ridge());
        Assert.Equal((far, true), best);
    }

    [Fact]
    public void An_awacs_high_above_the_ridge_sees_over_it()
    {
        var near = new RadioSpot("airfield", "Near", 1, null, 20_000, 10_000, 10);
        var awacs = new RadioSpot("awacs", "Overlord1-1", null, "AWACS-1", 15_000, 10_000, 9000);
        Assert.Equal((awacs, true), RadioSpots.Best([near, awacs], 30_000, 10_000, 300, Ridge()));
    }

    [Fact]
    public void With_none_clear_the_nearest_and_without_terrain_the_nearest()
    {
        var a = new RadioSpot("airfield", "A", 1, null, 20_000, 10_000, 10);
        var b = new RadioSpot("airfield", "B", 2, null, 5_000, 10_000, 10);
        Assert.Equal((a, false), RadioSpots.Best([a, b], 30_000, 10_000, 300, Ridge()));
        Assert.Equal((a, true), RadioSpots.Best([a, b], 30_000, 10_000, 300, null));
        Assert.Null(RadioSpots.Best([], 0, 0, 0, null));
    }
}
