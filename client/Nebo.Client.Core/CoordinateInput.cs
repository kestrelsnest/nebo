using System.Globalization;
using System.Text.RegularExpressions;

namespace Nebo.Client.Core;

/// <summary>A position typed by a controller: decimal degrees ("41.66, 44.95", "-33.5 151.2"),
/// degrees and decimal minutes as DCS shows them ("N 41°40.123' E 044°56.789'", "N41 40.123
/// E44 56.789"), degrees-minutes-seconds ("N 41°40'07.4\" E 44°56'27.3\"") or MGRS ("38T LM 12345
/// 67890", "38TLM1234567890"). Hemisphere letters before or after the numbers; without them
/// latitude comes first and a minus sign means south or west. What <see cref="Coordinates"/>
/// prints parses back.</summary>
public static partial class CoordinateInput
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static bool TryParse(string? text, out double lat, out double lon)
    {
        lat = lon = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().ToUpperInvariant();
        return TryMgrs(s, out lat, out lon) || TryDegrees(s, out lat, out lon);
    }

    // ---- Degrees: decimal, DDM, DMS --------------------------------------------------------------

    private static bool TryDegrees(string s, out double lat, out double lon)
    {
        lat = lon = 0;
        // Commas between the halves, or as a decimal mark ("41,66 44,95") when no dot is used.
        if (!s.Contains('.') && Regex.Matches(s, @"\d,\d").Count == 2) s = Regex.Replace(s, @"(\d),(\d)", "$1.$2");
        var tokens = Token().Matches(s).Select(m => m.Value).ToList();
        if (tokens.Count == 0 || Regex.IsMatch(Token().Replace(s, ""), @"[A-Z0-9]")) return false;  // stray letters or digits

        var letters = tokens.Where(t => t.Length == 1 && char.IsLetter(t[0])).ToList();
        var numbers = tokens.Where(t => !(t.Length == 1 && char.IsLetter(t[0]))).ToList();
        if (letters.Count is not (0 or 2) || numbers.Count is not (2 or 4 or 6)) return false;
        var lonFirst = false;
        double latSign = 1, lonSign = 1;
        if (letters.Count == 2)
        {
            var ns = letters.FirstOrDefault(l => l is "N" or "S");
            var ew = letters.FirstOrDefault(l => l is "E" or "W");
            if (ns is null || ew is null) return false;
            lonFirst = letters[0] is "E" or "W";
            latSign = ns == "S" ? -1 : 1;
            lonSign = ew == "W" ? -1 : 1;
        }
        var half = numbers.Count / 2;
        var first = numbers.Take(half).ToList();
        var second = numbers.Skip(half).ToList();
        if (!Angle(lonFirst ? second : first, out var a) || !Angle(lonFirst ? first : second, out var b)) return false;
        lat = a * latSign;
        lon = b * lonSign;
        return Math.Abs(lat) <= 90 && Math.Abs(lon) <= 180;
    }

    /// <summary>[deg], [deg, min] or [deg, min, sec]; a sign only on the degrees.</summary>
    private static bool Angle(IReadOnlyList<string> parts, out double value)
    {
        value = 0;
        var v = new double[parts.Count];
        for (var i = 0; i < parts.Count; i++)
            if (!double.TryParse(parts[i], NumberStyles.Float, Inv, out v[i]) || (i > 0 && (v[i] < 0 || v[i] >= 60))) return false;
        if (parts.Count > 1 && v[0] != Math.Floor(v[0])) return false;  // "41.5 30" is not an angle
        var sign = parts[0].StartsWith('-') ? -1 : 1;
        value = Math.Abs(v[0]) + (parts.Count > 1 ? v[1] / 60 : 0) + (parts.Count > 2 ? v[2] / 3600 : 0);
        value *= sign;
        return true;
    }

    // A hemisphere letter, or a number (sign and decimals); everything else (° ' " , spaces) separates.
    [GeneratedRegex(@"[NSEW]|[-+]?\d+(?:\.\d+)?")]
    private static partial Regex Token();

    // ---- MGRS --------------------------------------------------------------------------------------

    private const double A = 6378137, F = 1 / 298.257223563, K0 = 0.9996;
    private const string Bands = "CDEFGHJKLMNPQRSTUVWX";
    private const string RowLetters = "ABCDEFGHJKLMNPQRSTUV";
    private static readonly string[] ColumnLetters = ["ABCDEFGH", "JKLMNPQR", "STUVWXYZ"];

    [GeneratedRegex(@"^(\d{1,2})\s*([C-HJ-NP-X])\s*([A-HJ-NP-Z])([A-HJ-NP-V])\s*(\d*)\s*(\d*)$")]
    private static partial Regex MgrsText();

    private static bool TryMgrs(string s, out double lat, out double lon)
    {
        lat = lon = 0;
        var m = MgrsText().Match(s);
        if (!m.Success) return false;
        var zone = int.Parse(m.Groups[1].Value, Inv);
        if (zone is < 1 or > 60) return false;
        var band = m.Groups[2].Value[0];
        string e = m.Groups[5].Value, n = m.Groups[6].Value;
        if (n.Length == 0)
        {
            // "12345678": easting and northing run together, half each.
            if (e.Length % 2 != 0) return false;
            (e, n) = (e[..(e.Length / 2)], e[(e.Length / 2)..]);
        }
        if (e.Length != n.Length || e.Length > 5) return false;
        var scale = Math.Pow(10, 5 - e.Length);
        var east = (e.Length == 0 ? 0 : double.Parse(e, Inv)) * scale;
        var north = (n.Length == 0 ? 0 : double.Parse(n, Inv)) * scale;

        var set = (zone - 1) % 6 + 1;
        var col = ColumnLetters[(set - 1) % 3].IndexOf(m.Groups[3].Value[0]);
        var row = RowLetters.IndexOf(m.Groups[4].Value[0]);
        if (col < 0 || row < 0) return false;
        var easting = (col + 1) * 100_000 + east;
        var northing = ((row - (set % 2 == 0 ? 5 : 0) + 20) % 20) * 100_000 + north;
        // The band fixes which 2 000 km cycle of rows: the first reaching the band's south edge
        // (100 km of slack: latitude lines bend away from the central meridian).
        var bandSouth = -80 + 8 * Bands.IndexOf(band);
        var bandNorthing = Coordinates.Utm(bandSouth, (zone - 1) * 6 - 180 + 3).Northing;
        while (northing < bandNorthing - 100_000) northing += 2_000_000;
        (lat, lon) = FromUtm(zone, easting, northing, band < 'N');
        return lat >= bandSouth - 1 && lat <= bandSouth + (band == 'X' ? 12 : 8) + 1;
    }

    /// <summary>UTM (zone, easting, northing; southern northings with the 10 000 km false
    /// northing) → lat/lon on WGS84.</summary>
    public static (double Lat, double Lon) FromUtm(int zone, double easting, double northing, bool southern)
    {
        var e2 = F * (2 - F);
        var ep2 = e2 / (1 - e2);
        var x = easting - 500_000;
        var y = southern ? northing - 10_000_000 : northing;
        var m = y / K0;
        var mu = m / (A * (1 - e2 / 4 - 3 * e2 * e2 / 64 - 5 * e2 * e2 * e2 / 256));
        var e1 = (1 - Math.Sqrt(1 - e2)) / (1 + Math.Sqrt(1 - e2));
        var phi1 = mu + (3 * e1 / 2 - 27 * Math.Pow(e1, 3) / 32) * Math.Sin(2 * mu)
                      + (21 * e1 * e1 / 16 - 55 * Math.Pow(e1, 4) / 32) * Math.Sin(4 * mu)
                      + 151 * Math.Pow(e1, 3) / 96 * Math.Sin(6 * mu)
                      + 1097 * Math.Pow(e1, 4) / 512 * Math.Sin(8 * mu);
        double sin = Math.Sin(phi1), cos = Math.Cos(phi1), tan = Math.Tan(phi1);
        var n1 = A / Math.Sqrt(1 - e2 * sin * sin);
        var t1 = tan * tan;
        var c1 = ep2 * cos * cos;
        var r1 = A * (1 - e2) / Math.Pow(1 - e2 * sin * sin, 1.5);
        var d = x / (n1 * K0);
        var lat = phi1 - n1 * tan / r1 * (d * d / 2 - (5 + 3 * t1 + 10 * c1 - 4 * c1 * c1 - 9 * ep2) * Math.Pow(d, 4) / 24
                                          + (61 + 90 * t1 + 298 * c1 + 45 * t1 * t1 - 252 * ep2 - 3 * c1 * c1) * Math.Pow(d, 6) / 720);
        var lon = (d - (1 + 2 * t1 + c1) * Math.Pow(d, 3) / 6
                   + (5 - 2 * c1 + 28 * t1 - 3 * c1 * c1 + 8 * ep2 + 24 * t1 * t1) * Math.Pow(d, 5) / 120) / cos;
        return (lat * 180 / Math.PI, (zone - 1) * 6 - 180 + 3 + lon * 180 / Math.PI);
    }
}
