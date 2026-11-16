using System.Text.Json.Serialization;

namespace Nebo.Client.Core;

/// <summary>A runway from DCS's Airbase:getRunways(): centre in DCS map metres, size in metres.
/// DCS gives the course in radians with the opposite sign of the true heading.</summary>
public sealed record Runway(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("course")] double Course,
    [property: JsonPropertyName("length")] double Length,
    [property: JsonPropertyName("width")] double Width,
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("z")] double Z)
{
    /// <summary>True heading of the runway direction named by <see cref="Name"/>, degrees.</summary>
    public double HeadingTrue => ((-Course * 180 / Math.PI) % 360 + 360) % 360;

    /// <summary>Designators for both ends, e.g. "10" → ("10", "28"), "05L" → ("05L", "23R").</summary>
    public (string Near, string Far) Designators
    {
        get
        {
            var digits = new string(Name.TakeWhile(char.IsDigit).ToArray());
            if (!int.TryParse(digits, out var n)) return (Name, "");
            var suffix = Name[digits.Length..].Trim().ToUpperInvariant();
            var opposite = suffix switch { "L" => "R", "R" => "L", _ => suffix };
            var far = (n + 18 - 1) % 36 + 1;
            return ($"{n:00}{suffix}", $"{far:00}{opposite}");
        }
    }
}

/// <summary>A parking spot from Airbase:getParking(): number, position, DCS terminal type.</summary>
public sealed record ParkingSpot(int Number, double X, double Z, int Type)
{
    /// <summary>DCS Term_Type 40 = helicopter only; 68 hardened shelter; 72/104 open/airliner; 100 small.</summary>
    public bool HelicopterOnly => Type == 40;
}

/// <summary>An airdrome or helipad (ships are tracked in the picture instead).</summary>
public sealed record Airfield(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("category")] int Category,
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("z")] double Z,
    [property: JsonPropertyName("coalition")] int Coalition,
    [property: JsonPropertyName("runways")] IReadOnlyList<Runway> Runways,
    [property: JsonPropertyName("parking")] IReadOnlyList<double[]> ParkingRaw)
{
    private IReadOnlyList<ParkingSpot>? _parking;

    /// <summary>Wire format is compact arrays: [number, x, z, type].</summary>
    public IReadOnlyList<ParkingSpot> Parking => _parking ??= ParkingRaw
        .Where(p => p.Length >= 4)
        .Select(p => new ParkingSpot((int)p[0], p[1], p[2], (int)p[3]))
        .ToList();

    public bool IsHelipad => Category == 1;

    /// <summary>DCS coalition: 0 neutral, 1 red, 2 blue.</summary>
    public string Owner => Coalition switch { 1 => "red", 2 => "blue", _ => "neutral" };
}

public sealed record AirfieldsMessage(
    [property: JsonPropertyName("airfields")] IReadOnlyList<Airfield> Airfields);

/// <summary>Runway names as DCS gives them fixed up. DCS names parallel runways without L/R
/// ("17" and "17") and may give one of a pair the course of its other end, so its labels come
/// out reversed. Each runway's direction is turned to match its number (within 90°), then
/// runways sharing a number get L / C / R by their side-by-side position.</summary>
public static class RunwayNames
{
    public static Airfield Normalize(Airfield field)
    {
        if (field.Runways.Count == 0) return field;
        var fixedUp = field.Runways.Select(Orient).ToList();
        foreach (var group in fixedUp.Select((r, i) => (r, i))
                     .Where(x => Number(x.r.Name) is not null && Suffix(x.r.Name).Length == 0)
                     .GroupBy(x => Number(x.r.Name)).Where(g => g.Count() is 2 or 3))
        {
            // Offset to the right of the landing direction: smallest is L, largest R.
            var h = group.First().r.HeadingTrue * Math.PI / 180;
            double rx = -Math.Sin(h), rz = Math.Cos(h);
            var sorted = group.OrderBy(x => x.r.X * rx + x.r.Z * rz).ToList();
            var letters = sorted.Count == 2 ? new[] { "L", "R" } : ["L", "C", "R"];
            for (int k = 0; k < sorted.Count; k++)
                fixedUp[sorted[k].i] = sorted[k].r with { Name = $"{Number(sorted[k].r.Name):00}{letters[k]}" };
        }
        return field with { Runways = fixedUp };
    }

    /// <summary>The runway turned so its course points the way its number says.</summary>
    private static Runway Orient(Runway r)
    {
        if (Number(r.Name) is not { } n) return r;
        var off = Math.Abs(((r.HeadingTrue - n * 10) % 360 + 540) % 360 - 180);
        return off > 90 ? r with { Course = r.Course - Math.PI } : r;
    }

    private static int? Number(string name) =>
        int.TryParse(new string(name.TakeWhile(char.IsDigit).ToArray()), out var n) && n is >= 1 and <= 36 ? n : null;

    private static string Suffix(string name) => name.SkipWhile(char.IsDigit).Aggregate("", (s, c) => s + c).Trim();
}
