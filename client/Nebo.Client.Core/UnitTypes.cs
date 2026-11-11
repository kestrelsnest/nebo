using System.Text.Json.Serialization;

namespace Nebo.Client.Core;

/// <summary>The server's unit_types message (public, sent at login): every unit type DCS knows with
/// its wsType (four levels), from the server's own Mission Editor unit database. The data cartridge
/// turns threats DCS names only by wsType into unit types with it (Dtc.HornetEw.ThreatUnits).</summary>
public sealed record UnitTypesMessage(
    [property: JsonPropertyName("types")] IReadOnlyDictionary<string, int[]> Types);
