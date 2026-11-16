using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nebo.Client.Core;

public sealed record GeoGrid(
    [property: JsonPropertyName("n")] int N,
    [property: JsonPropertyName("lat")] double[] Lat,
    [property: JsonPropertyName("lon")] double[] Lon);

/// <summary>The DCS theatre (terrain) the server runs: bounds in DCS map metres (x north,
/// z east), a lat/lon control grid over them, and town names.</summary>
public sealed record Theatre(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("x0")] double X0,
    [property: JsonPropertyName("z0")] double Z0,
    [property: JsonPropertyName("x1")] double X1,
    [property: JsonPropertyName("z1")] double Z1,
    [property: JsonPropertyName("grid")] GeoGrid Grid,
    [property: JsonPropertyName("towns")] JsonElement[][] TownsRaw,
    [property: JsonPropertyName("magvar")] MagVarGrid? MagVar = null)
{
    private IReadOnlyList<Town>? _towns;

    public IReadOnlyList<Town> Towns => _towns ??= TownsRaw
        .Where(t => t.Length >= 3)
        .Select(t => new Town(t[0].GetString() ?? "", t[1].GetDouble(), t[2].GetDouble()))
        .ToList();

    /// <summary>DCS map x/z → lat/lon by bilinear interpolation in the control grid. DCS maps
    /// are transverse Mercator: the server's 81×81 grid keeps the error under ~8 m even on
    /// Caucasus (the 21×21 grid older servers send is off by up to ~140 m there).</summary>
    public (double Lat, double Lon) ToLatLon(double x, double z)
    {
        var n = Grid.N;
        var fi = Math.Clamp((x - X0) / (X1 - X0) * (n - 1), 0, n - 1);
        var fj = Math.Clamp((z - Z0) / (Z1 - Z0) * (n - 1), 0, n - 1);
        var i = Math.Min((int)fi, n - 2);
        var j = Math.Min((int)fj, n - 2);
        double ti = fi - i, tj = fj - j;
        double Lerp(double[] v) =>
            (v[i * n + j] * (1 - tj) + v[i * n + j + 1] * tj) * (1 - ti) +
            (v[(i + 1) * n + j] * (1 - tj) + v[(i + 1) * n + j + 1] * tj) * ti;
        return (Lerp(Grid.Lat), Lerp(Grid.Lon));
    }

    /// <summary>Lat/lon → DCS map x/z: Newton steps on <see cref="ToLatLon"/> (smooth), from the
    /// theatre centre. Within the grid it converges to well under a metre in a few steps.</summary>
    public (double X, double Z) FromLatLon(double lat, double lon)
    {
        double x = (X0 + X1) / 2, z = (Z0 + Z1) / 2;
        const double h = 1000;  // metres for the numerical Jacobian
        for (int k = 0; k < 8; k++)
        {
            var (la, lo) = ToLatLon(x, z);
            var (lax, lox) = ToLatLon(x + h, z);
            var (laz, loz) = ToLatLon(x, z + h);
            double a = (lax - la) / h, b = (laz - la) / h, c = (lox - lo) / h, d = (loz - lo) / h;
            var det = a * d - b * c;
            if (Math.Abs(det) < 1e-18) break;
            double eLat = lat - la, eLon = lon - lo;
            double dx = (d * eLat - b * eLon) / det, dz = (-c * eLat + a * eLon) / det;
            x += dx;
            z += dz;
            if (Math.Abs(dx) < 0.01 && Math.Abs(dz) < 0.01) break;
        }
        return (x, z);
    }

    /// <summary>Bearing of DCS grid north (+x) from true north at a point, degrees (meridian
    /// convergence): add it to a grid bearing (headings, RWR bearings) to get a true one.</summary>
    public double GridNorthAt(double lat, double lon)
    {
        var (x, z) = FromLatLon(lat, lon);
        var (la0, lo0) = ToLatLon(x, z);
        var (la1, lo1) = ToLatLon(x + 500, z);
        return Math.Atan2((lo1 - lo0) * Math.Cos(la0 * Math.PI / 180), la1 - la0) * 180 / Math.PI;
    }

    /// <summary>Theatre border as lat/lon points, walking the x/z rectangle's edges.</summary>
    public IReadOnlyList<(double Lat, double Lon)> Border(int pointsPerEdge = 40)
    {
        var pts = new List<(double, double)>();
        void Edge(double xa, double za, double xb, double zb)
        {
            for (int k = 0; k < pointsPerEdge; k++)
            {
                var t = (double)k / pointsPerEdge;
                pts.Add(ToLatLon(xa + (xb - xa) * t, za + (zb - za) * t));
            }
        }
        Edge(X0, Z0, X0, Z1);
        Edge(X0, Z1, X1, Z1);
        Edge(X1, Z1, X1, Z0);
        Edge(X1, Z0, X0, Z0);
        return pts;
    }
}

public sealed record Town(string Name, double Lat, double Lon);

/// <summary>Magnetic variation in degrees (east positive) from DCS's own model for the
/// mission date, sampled by the server on a lat/lon grid: row i at Lat0 + i·Step, column j at
/// Lon0 + j·Step.</summary>
public sealed record MagVarGrid(
    [property: JsonPropertyName("lat0")] double Lat0,
    [property: JsonPropertyName("lon0")] double Lon0,
    [property: JsonPropertyName("step")] double Step,
    [property: JsonPropertyName("nlat")] int NLat,
    [property: JsonPropertyName("nlon")] int NLon,
    [property: JsonPropertyName("deg")] double[] Deg)
{
    /// <summary>Variation at a point, bilinear between grid nodes, clamped to the grid.</summary>
    public double At(double lat, double lon)
    {
        if (NLat < 2 || NLon < 2 || Deg.Length < NLat * NLon) return Deg.Length > 0 ? Deg[0] : 0;
        var fi = Math.Clamp((lat - Lat0) / Step, 0, NLat - 1);
        var fj = Math.Clamp((lon - Lon0) / Step, 0, NLon - 1);
        var i = Math.Min((int)fi, NLat - 2);
        var j = Math.Min((int)fj, NLon - 2);
        double ti = fi - i, tj = fj - j;
        return (Deg[i * NLon + j] * (1 - tj) + Deg[i * NLon + j + 1] * tj) * (1 - ti) +
               (Deg[(i + 1) * NLon + j] * (1 - tj) + Deg[(i + 1) * NLon + j + 1] * tj) * ti;
    }
}

/// <summary>DCS surface types (land.SurfaceType).</summary>
public enum Surface : byte { Unknown = 0, Land = 1, ShallowWater = 2, Water = 3, Road = 4, Runway = 5 }

/// <summary>Terrain heights and surface types sampled by the server on a regular x/z grid:
/// row i at x = X0 + i·Step, column j at z = Z0 + j·Step. Plain (heights, surfaces in base64),
/// or from DLL 0.17.3 compressed: <see cref="Enc"/> "xpress_huff", heights then surfaces in
/// <see cref="Data"/>, <see cref="Hash"/> naming the grid (clients keep it by that).</summary>
public sealed record TerrainMessage(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("x0")] double X0,
    [property: JsonPropertyName("z0")] double Z0,
    [property: JsonPropertyName("step")] double Step,
    [property: JsonPropertyName("nx")] int Nx,
    [property: JsonPropertyName("nz")] int Nz,
    [property: JsonPropertyName("heights")] string? HeightsBase64,
    [property: JsonPropertyName("surfaces")] string? SurfacesBase64,
    [property: JsonPropertyName("enc")] string? Enc = null,
    [property: JsonPropertyName("hash")] string? Hash = null,
    [property: JsonPropertyName("size")] long Size = 0,
    [property: JsonPropertyName("data")] string? Data = null)
{
    public TerrainGrid Decode()
    {
        var n = Nx * Nz;
        byte[] h, s;
        if (Enc is not null)
        {
            if (Enc != "xpress_huff" || Data is null) throw new InvalidDataException($"terrain encoding '{Enc}' unknown");
            var raw = Xpress.Decompress(Convert.FromBase64String(Data), (long)n * 3);
            h = raw[..(n * 2)];
            s = raw[(n * 2)..];
        }
        else
        {
            h = Convert.FromBase64String(HeightsBase64 ?? "");
            s = Convert.FromBase64String(SurfacesBase64 ?? "");
        }
        if (h.Length != n * 2 || s.Length != n) throw new InvalidDataException("terrain size mismatch");
        var heights = new short[n];
        Buffer.BlockCopy(h, 0, heights, 0, h.Length);
        return new TerrainGrid(Id, X0, Z0, Step, Nx, Nz, heights, s);
    }
}

/// <summary>The server's answer when this client already keeps the grid (its hello listed the
/// hash): load it from <see cref="TerrainCache"/>.</summary>
public sealed record TerrainCachedMessage([property: JsonPropertyName("hash")] string Hash);

/// <summary>The terrain grids this client got compressed, one file per hash, so a reconnect
/// downloads nothing (the hello lists them; the server answers "terrain_cached"). Keeps the
/// newest <see cref="Keep"/>.</summary>
public sealed class TerrainCache(string folder)
{
    public const int Keep = 8;

    private string PathOf(string hash) => System.IO.Path.Combine(folder, $"terrain_{hash}.json");

    private static bool IsHash(string hash) => hash.Length == 16 && hash.All(Uri.IsHexDigit);

    /// <summary>Hashes of the grids kept, newest first.</summary>
    public IReadOnlyList<string> Have()
    {
        try
        {
            if (!Directory.Exists(folder)) return [];
            return new DirectoryInfo(folder).GetFiles("terrain_*.json").OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => f.Name["terrain_".Length..^".json".Length]).Where(IsHash).Take(Keep).ToList();
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    /// <summary>Keeps a compressed grid (other forms are not kept) and drops the oldest beyond <see cref="Keep"/>.</summary>
    public void Save(TerrainMessage m)
    {
        if (m.Enc is null || m.Hash is null || !IsHash(m.Hash)) return;
        try
        {
            Directory.CreateDirectory(folder);
            var path = PathOf(m.Hash);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(m));
            File.Move(path + ".tmp", path, overwrite: true);
            foreach (var old in new DirectoryInfo(folder).GetFiles("terrain_*.json").OrderByDescending(f => f.LastWriteTimeUtc).Skip(Keep))
                old.Delete();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>The kept grid with this hash, or null (gone, unreadable).</summary>
    public TerrainMessage? Load(string hash)
    {
        if (!IsHash(hash)) return null;
        try
        {
            var path = PathOf(hash);
            if (!File.Exists(path)) return null;
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);  // used: newest
            return JsonSerializer.Deserialize<TerrainMessage>(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}

/// <summary>XPRESS Huffman, the Windows Compression API's buffered format (cabinet.dll), which
/// the server DLL writes.</summary>
public static class Xpress
{
    private const uint XpressHuff = 4;

    public static byte[] Decompress(byte[] packed, long maxSize)
    {
        if (!CreateDecompressor(XpressHuff, IntPtr.Zero, out var d)) throw new InvalidDataException("no decompressor");
        try
        {
            var outBuf = new byte[maxSize];
            if (!Decompress(d, packed, (nuint)packed.Length, outBuf, (nuint)outBuf.Length, out var size))
                throw new InvalidDataException($"terrain data damaged (error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()})");
            return size == (nuint)outBuf.Length ? outBuf : outBuf[..(int)size];
        }
        finally { CloseDecompressor(d); }
    }

    /// <summary>For tests: the same format the server writes.</summary>
    public static byte[] Compress(byte[] raw)
    {
        if (!CreateCompressor(XpressHuff, IntPtr.Zero, out var c)) throw new InvalidOperationException("no compressor");
        try
        {
            var outBuf = new byte[raw.Length + 65536];
            if (!Compress(c, raw, (nuint)raw.Length, outBuf, (nuint)outBuf.Length, out var size)) throw new InvalidOperationException("compress failed");
            return outBuf[..(int)size];
        }
        finally { CloseCompressor(c); }
    }

    [System.Runtime.InteropServices.DllImport("cabinet.dll", SetLastError = true)]
    private static extern bool CreateDecompressor(uint algorithm, IntPtr routines, out IntPtr handle);
    [System.Runtime.InteropServices.DllImport("cabinet.dll", SetLastError = true)]
    private static extern bool Decompress(IntPtr handle, byte[] data, nuint dataSize, byte[] buffer, nuint bufferSize, out nuint size);
    [System.Runtime.InteropServices.DllImport("cabinet.dll")]
    private static extern bool CloseDecompressor(IntPtr handle);
    [System.Runtime.InteropServices.DllImport("cabinet.dll", SetLastError = true)]
    private static extern bool CreateCompressor(uint algorithm, IntPtr routines, out IntPtr handle);
    [System.Runtime.InteropServices.DllImport("cabinet.dll", SetLastError = true)]
    private static extern bool Compress(IntPtr handle, byte[] data, nuint dataSize, byte[] buffer, nuint bufferSize, out nuint size);
    [System.Runtime.InteropServices.DllImport("cabinet.dll")]
    private static extern bool CloseCompressor(IntPtr handle);
}

public sealed record TerrainGrid(string Id, double X0, double Z0, double Step, int Nx, int Nz, short[] Heights, byte[] Surfaces)
{
    public short HeightAt(int i, int j) => Heights[i * Nz + j];
    public Surface SurfaceAt(int i, int j) => (Surface)Surfaces[i * Nz + j];
}
