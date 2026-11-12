using System.Text.Json;
using Nebo.Client.Core;

namespace Nebo.Client.Core.Tests;

public class TheatreTests
{
    // 3×3 grid over x 0..2000, z 0..2000: lat rises with x, lon with z.
    private static Theatre Make() => new("Test", 0, 0, 2000, 2000,
        new GeoGrid(3, [40, 40, 40, 41, 41, 41, 42, 42, 42], [30, 31, 32, 30, 31, 32, 30, 31, 32]),
        [[JsonDocument.Parse("\"Town\"").RootElement, JsonDocument.Parse("41.5").RootElement, JsonDocument.Parse("31.5").RootElement]]);

    [Fact]
    public void Grid_nodes_map_exactly()
    {
        var (lat, lon) = Make().ToLatLon(1000, 2000);
        Assert.Equal(41, lat, 9);
        Assert.Equal(32, lon, 9);
    }

    [Fact]
    public void Between_nodes_interpolates()
    {
        var (lat, lon) = Make().ToLatLon(500, 1500);
        Assert.Equal(40.5, lat, 9);
        Assert.Equal(31.5, lon, 9);
    }

    [Fact]
    public void Outside_bounds_clamps_to_edge()
    {
        var (lat, _) = Make().ToLatLon(-5000, 0);
        Assert.Equal(40, lat, 9);
    }

    [Theory]
    [InlineData(500, 1500)]
    [InlineData(1234, 77)]
    [InlineData(1999, 1)]
    public void FromLatLon_inverts_ToLatLon(double x, double z)
    {
        var t = Make();
        var (lat, lon) = t.ToLatLon(x, z);
        var (bx, bz) = t.FromLatLon(lat, lon);
        Assert.Equal(x, bx, 3);
        Assert.Equal(z, bz, 3);
    }

    [Fact]
    public void Grid_north_is_where_x_points()
    {
        Assert.Equal(0, Make().GridNorthAt(41, 31), 6);
        // Lon also rises with x: grid north leans east of true north.
        var skewed = new Theatre("Test", 0, 0, 2000, 2000,
            new GeoGrid(3, [40, 40, 40, 41, 41, 41, 42, 42, 42], [30, 31, 32, 30.1, 31.1, 32.1, 30.2, 31.2, 32.2]), []);
        var expected = Math.Atan2(0.1 * Math.Cos(41 * Math.PI / 180), 1) * 180 / Math.PI;
        Assert.Equal(expected, skewed.GridNorthAt(41, 31.1), 1);
    }

    [Fact]
    public void Border_walks_four_edges()
    {
        var border = Make().Border(pointsPerEdge: 4);
        Assert.Equal(16, border.Count);
        Assert.Equal((40.0, 30.0), border[0]);
    }

    [Fact]
    public void Towns_parse_from_arrays() =>
        Assert.Equal(new Town("Town", 41.5, 31.5), Make().Towns.Single());

    [Fact]
    public void Terrain_decodes_little_endian_heights()
    {
        var heights = new byte[] { 0x10, 0x00, 0xFF, 0xFF };  // 16, -1
        var msg = new TerrainMessage("Test", 0, 0, 1000, 1, 2, Convert.ToBase64String(heights), Convert.ToBase64String(new byte[] { 1, 3 }));
        var grid = msg.Decode();
        Assert.Equal(16, grid.HeightAt(0, 0));
        Assert.Equal(-1, grid.HeightAt(0, 1));
        Assert.Equal(Surface.Water, grid.SurfaceAt(0, 1));
    }

    /// <summary>A 3x4 grid compressed the way DLL 0.17.3 sends it: heights then surfaces.</summary>
    private static TerrainMessage Compressed(string hash = "0123456789abcdef")
    {
        short[] heights = [0, 10, 20, 30, -5, 5, 15, 25, 100, 200, 300, 400];
        byte[] surfaces = [1, 1, 3, 3, 1, 4, 5, 1, 2, 2, 1, 1];
        var raw = new byte[heights.Length * 2 + surfaces.Length];
        Buffer.BlockCopy(heights, 0, raw, 0, heights.Length * 2);
        surfaces.CopyTo(raw, heights.Length * 2);
        return new TerrainMessage("Test", 0, 0, 500, 3, 4, null, null, "xpress_huff", hash, raw.Length, Convert.ToBase64String(Xpress.Compress(raw)));
    }

    [Fact]
    public void Compressed_terrain_decodes()
    {
        var grid = Compressed().Decode();
        Assert.Equal(-5, grid.HeightAt(1, 0));
        Assert.Equal(400, grid.HeightAt(2, 3));
        Assert.Equal(Surface.Runway, grid.SurfaceAt(1, 2));
        Assert.Equal(Surface.ShallowWater, grid.SurfaceAt(2, 0));
    }

    [Fact]
    public void Unknown_terrain_encoding_is_refused() =>
        Assert.Throws<InvalidDataException>(() => (Compressed() with { Enc = "zstd" }).Decode());

    [Fact]
    public void Terrain_cache_keeps_compressed_grids_by_hash()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nebo-terrain-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new TerrainCache(dir);
            Assert.Empty(cache.Have());
            cache.Save(Compressed());
            Assert.Equal(["0123456789abcdef"], cache.Have());
            Assert.Equal(400, cache.Load("0123456789abcdef")!.Decode().HeightAt(2, 3));
            Assert.Null(cache.Load("fedcba9876543210"));     // not kept
            Assert.Null(cache.Load("..\\..\\x"));             // not a hash: never a path
            cache.Save(new TerrainMessage("Plain", 0, 0, 1000, 1, 1, "AAA=", "AQ=="));  // plain: not kept
            Assert.Single(cache.Have());
            for (var i = 0; i < TerrainCache.Keep + 2; i++) cache.Save(Compressed($"{i:x16}"));
            Assert.Equal(TerrainCache.Keep, cache.Have().Count);
        }
        finally { Directory.Delete(dir, true); }
    }
}
