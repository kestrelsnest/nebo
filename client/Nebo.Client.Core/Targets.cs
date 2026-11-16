using System.Text.Json.Serialization;

namespace Nebo.Client.Core;

/// <summary>The "targets" message (training missions running the EventTargets script): the target
/// zones the controller's coalition attacks and may see, each a flight group's task.</summary>
public sealed record TargetsMessage(
    [property: JsonPropertyName("coalition")] string? Coalition,
    [property: JsonPropertyName("version")] string? Version,
    [property: JsonPropertyName("zones")] IReadOnlyList<TargetZone> Zones)
{
    public TargetZone? Find(int id) => Zones.FirstOrDefault(z => z.Id == id);

    /// <summary>Zones grouped by flight (comma-separated flights count for each), in task order;
    /// zones given to no flight last, under "unassigned".</summary>
    public IEnumerable<(string Flight, IReadOnlyList<TargetZone> Zones)> ByFlight() =>
        Zones.SelectMany(z => z.FlightList.DefaultIfEmpty("").Select(f => (Flight: f, Zone: z)))
            .GroupBy(e => e.Flight, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key.Length == 0).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => (g.Key.Length == 0 ? "unassigned" : g.First().Flight,
                          (IReadOnlyList<TargetZone>)g.Select(e => e.Zone).OrderBy(z => z.Order).ThenBy(z => z.Name).ToList()));
}

/// <summary>One target zone. Position and radius (or <see cref="Verts"/> for a quad) are the
/// trigger zone in DCS map metres.</summary>
public sealed record TargetZone(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("zone")] string? Zone,
    [property: JsonPropertyName("flights")] string? Flights,
    [property: JsonPropertyName("assigned")] bool Assigned,
    [property: JsonPropertyName("order")] int Order,
    [property: JsonPropertyName("reveal")] string? Reveal,
    [property: JsonPropertyName("what")] string? What,
    [property: JsonPropertyName("need")] int Need,
    [property: JsonPropertyName("dead")] int Dead,
    [property: JsonPropertyName("damaged")] int Damaged,
    [property: JsonPropertyName("done")] bool Done,
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("z")] double Z,
    [property: JsonPropertyName("r")] double Radius,
    [property: JsonPropertyName("verts")] IReadOnlyList<double[]>? Verts,
    [property: JsonPropertyName("items")] IReadOnlyList<TargetItem> Items)
{
    public IReadOnlyList<string> FlightList => (Flights ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(f => !f.Equals("unassigned", StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>"3/5" destroyed of what the zone needs.</summary>
    public string Progress => $"{Dead}/{Need}";

    /// <summary>Map label: "2 · TGT zone 5 · Aerial-2 · 3/5".</summary>
    public string Label => string.Join(" · ", new[] { Order > 0 ? Order.ToString() : null, Name,
        Assigned ? Flights : "unassigned", Done ? Progress + " ✓" : Progress }.Where(s => !string.IsNullOrEmpty(s)));
}

/// <summary>One target: a unit, a static object or a map building ("scenery"). State is
/// "alive", "damaged" or "dead"; <see cref="Time"/> the mission time it was destroyed.</summary>
public sealed record TargetItem(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("z")] double Z,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("t")] double? Time)
{
    public bool Dead => State == "dead";
    public bool Damaged => State == "damaged";

    /// <summary>The unit name without the Kneeboard Generator's "[Flight:n] " tag.</summary>
    public string ShortName => Name is { } n && n.StartsWith('[') && n.IndexOf("] ", StringComparison.Ordinal) is var i and > 0 ? n[(i + 2)..] : Name ?? "";
}
