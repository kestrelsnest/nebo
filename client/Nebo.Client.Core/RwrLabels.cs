namespace Nebo.Client.Core;

/// <summary>RWR labels as pilots call them ("29", "SA-6" as "6"), for the Identify menu, and the
/// threat each stands for on the ground.</summary>
public static class RwrLabels
{
    /// <summary>Labels for the Identify menu, by group (aircraft, SAM and radars, ships).</summary>
    public static readonly IReadOnlyList<(string Group, IReadOnlyList<string> Labels)> Groups =
    [
        ("Aircraft", ["29", "30", "34", "24", "31", "25", "23", "21", "19", "JF", "10", "14", "15", "16", "18", "F4", "F5", "M2", "F1",
            "RF", "EF", "TO", "AV", "50", "KJ", "E3", "E2", "22", "95", "142", "160", "64", "28", "52"]),
        ("SAM and radars", ["2", "3", "6", "8", "10", "11", "15", "19", "SD", "BB", "CS", "A", "P", "HA", "HK", "RO", "RA", "NS", "HQ", "S"]),
        ("Ships", ["SH"]),
    ];

    private static readonly Dictionary<string, string> GroundThreat = new(StringComparer.OrdinalIgnoreCase)
    {
        ["2"] = "SA-2", ["3"] = "SA-3", ["6"] = "SA-6", ["8"] = "SA-8", ["10"] = "SA-10", ["11"] = "SA-11",
        ["15"] = "SA-15", ["19"] = "SA-19", ["SD"] = "SA-11", ["BB"] = "SA-10", ["CS"] = "SA-10", ["A"] = "AAA",
        ["P"] = "Patriot", ["HA"] = "Hawk", ["HK"] = "Hawk", ["NS"] = "NASAMS", ["RO"] = "Roland", ["RA"] = "Rapier", ["HQ"] = "HQ-7",
    };

    /// <summary>The threat (with its ring) an identity stands for on a ground unit or ship: an RWR
    /// label ("6" → SA-6) or a threat name ("SA-11", "Patriot"). Aircraft labels share digits with
    /// SAMs ("15": F-15 or SA-15), so airborne contacts never get one.</summary>
    public static ThreatType? ThreatFor(string? label, string category)
    {
        if (string.IsNullOrWhiteSpace(label) || category is "airplane" or "helicopter") return null;
        return GroundThreat.TryGetValue(label.Trim(), out var name) ? ThreatType.Find(name) : ThreatType.Parse(label);
    }

}
