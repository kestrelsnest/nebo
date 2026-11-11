using Nebo.Client.Core;

namespace Nebo.Client.Core.Tests;

public class RadioCoverageTests
{
    // A grid of flat ground, 1 km step, centred on (0, 0), with an optional ridge.
    private static TerrainGrid Grid(Func<double, double, short> height, int n = 801)
    {
        var heights = new short[n * n];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
                heights[i * n + j] = height((i - n / 2) * 1000.0, (j - n / 2) * 1000.0);
        return new TerrainGrid("t", -(n / 2) * 1000.0, -(n / 2) * 1000.0, 1000, n, n, heights, new byte[n * n]);
    }

    [Fact]
    public void Flat_ground_reaches_the_radio_horizon_all_round()
    {
        var c = RadioCoverage.Compute(Grid((_, _) => 0), 0, 0, 10, 3048);
        Assert.Equal(RadioCoverage.HorizonMeters(10, 3048), c.HorizonM, 1);
        Assert.InRange(c.HorizonM / 1000, 230, 245);  // ~240 km for 10 m and 10 000 ft
        Assert.Equal(180, c.Rays.Count);
        Assert.All(c.Rays, r => Assert.True(Assert.Single(r).To > c.HorizonM - 1));
    }

    [Fact]
    public void A_ridge_hides_level_aircraft_beyond_the_sight_line_over_it()
    {
        // A 2 000 m ridge 19–21 km north of the radio, across the whole grid. The sight line over
        // its near edge climbs ~0.105 m/m: an aircraft at 2 500 m drops below it ~24 km out and
        // stays hidden; one at 9 000 m stays above it until ~86 km.
        var grid = Grid((x, _) => (short)(x is >= 19000 and <= 21000 ? 2000 : 0));
        var low = RadioCoverage.Compute(grid, 0, 0, 10, 2500);
        Assert.InRange(Assert.Single(low.Rays[0]).To, 22000, 26000);          // ray 0 points north
        Assert.True(Assert.Single(low.Rays[90]).To > low.HorizonM - 1);       // nothing to the south
        var high = RadioCoverage.Compute(grid, 0, 0, 10, 9000);
        Assert.InRange(Assert.Single(high.Rays[0]).To, 80000, 92000);
    }
}
