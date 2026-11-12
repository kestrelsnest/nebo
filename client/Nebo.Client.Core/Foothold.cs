using System.Text.Json.Serialization;

namespace Nebo.Client.Core;

/// <summary>A Foothold campaign zone (Leka's Foothold, read from its BattleCommander). Position
/// and radius are the zone's trigger zone in DCS map metres.</summary>
public sealed record FootholdZone(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("side")] int Side,
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("z")] double Z,
    [property: JsonPropertyName("r")] double Radius,
    [property: JsonPropertyName("active")] bool Active,
    [property: JsonPropertyName("suspended")] bool Suspended,
    [property: JsonPropertyName("airbase")] string? Airbase,
    [property: JsonPropertyName("facility")] string? Facility,
    [property: JsonPropertyName("logistic")] bool Logistic,
    [property: JsonPropertyName("built")] int Built,
    [property: JsonPropertyName("max")] int Max,
    [property: JsonPropertyName("wpt")] int? Waypoint,
    [property: JsonPropertyName("tags")] IReadOnlyList<FootholdTag>? Tags = null)
{
    /// <summary>The mission tags a side sees under this zone on its F10 map ("Bomb runway!").</summary>
    public IEnumerable<FootholdTag> TagsFor(string? coalition)
    {
        var side = coalition switch { "red" => 1, "blue" => 2, _ => -1 };
        return (Tags ?? []).Where(t => t.Side == side);
    }

    /// <summary>Foothold sides: 0 neutral, 1 red, 2 blue.</summary>
    public string Owner => Side switch { 1 => "red", 2 => "blue", _ => "neutral" };

    /// <summary>Groups present out of the owner's upgrade slots, 0..1 (null when unknown).</summary>
    public double? Strength => Max > 0 ? Math.Min(1.0, (double)Built / Max) : null;
}

/// <summary>A mission Foothold has on a zone for one side (side 1 red, 2 blue): its tag
/// ("Bomb runway") and the mission's localised label.</summary>
public sealed record FootholdTag(
    [property: JsonPropertyName("side")] int Side,
    [property: JsonPropertyName("tag")] string Tag,
    [property: JsonPropertyName("label")] string? Label)
{
    /// <summary>Full text as on the F10 map: "Bomb runway!".</summary>
    public string Text => (string.IsNullOrWhiteSpace(Label) ? Tag : Label) + "!";

    /// <summary>Short badge for the zoomed-out map.</summary>
    public string Badge => Tag switch
    {
        "Attack" => "ATK",
        "Bomb runway" => "RWY",
        "Capture" => "CAPT",
        "DEAD" => "DEAD",
        "Recon" => "RCN",
        "Resupply" => "SUP",
        "SEAD" => "SEAD",
        _ => Tag.Length <= 4 ? Tag.ToUpperInvariant() : Tag[..4].ToUpperInvariant(),
    };
}

public sealed record FootholdState(
    [property: JsonPropertyName("zones")] IReadOnlyList<FootholdZone> Zones,
    [property: JsonPropertyName("links")] IReadOnlyList<string[]> Links)
{
    public FootholdZone? Find(string name) => Zones.FirstOrDefault(z => z.Name == name);
}
