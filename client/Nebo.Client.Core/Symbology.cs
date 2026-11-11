namespace Nebo.Client.Core;

public enum Identity { Friendly, Hostile, Unknown }

public enum SymbolKind { Aircraft, Helicopter, Ground, Ship }

public enum LabelLevel { Full, Short, None }

/// <summary>Style B symbol rules: what shape, colour and data block
/// a unit gets. Pure functions so the map, the object list and tests agree.</summary>
public static class Symbology
{
    /// <summary>Vertical speed beyond which the block shows a climb/descent arrow (~500 ft/min).</summary>
    public const double TrendThresholdMps = 2.5;

    /// <summary>Own side is friendly. A radar contact is hostile once identified — its type from
    /// DCS, or an identity from the coalition's controllers — unknown until then.</summary>
    public static Identity IdentityOf(Unit u, string ownCoalition) =>
        u.Coalition == ownCoalition && !u.Detected ? Identity.Friendly
        : u.Shown is null ? Identity.Unknown
        : Identity.Hostile;

    public static SymbolKind KindOf(Unit u) => u.Category switch
    {
        "airplane" => SymbolKind.Aircraft,
        "helicopter" => SymbolKind.Helicopter,
        "ship" => SymbolKind.Ship,
        _ => SymbolKind.Ground,
    };

    public static bool IsAir(SymbolKind k) => k is SymbolKind.Aircraft or SymbolKind.Helicopter;

    /// <summary>Compact aircraft type for data blocks: "F-16C_50" → "F16C", "MiG-29 Fulcrum" → "MiG29".</summary>
    public static string ShortType(string type)
    {
        var head = type.Split(' ', '_')[0];
        return head.Replace("-", "");
    }

    /// <summary>Who to name in line 1: player nickname first, then callsign. Contacts are never named.</summary>
    public static string? NameOf(Unit u) =>
        u.Detected ? null
        : !string.IsNullOrEmpty(u.Player) ? u.Player
        : !string.IsNullOrEmpty(u.Callsign) ? u.Callsign
        : u.Name;

    public static char TrendOf(Unit u) =>
        u.Vs > TrendThresholdMps ? '↑' : u.Vs < -TrendThresholdMps ? '↓' : ' ';

    /// <summary>Altitude in hundreds of feet, as on a scope: 25,000 ft → "250".</summary>
    public static string AltitudeHundreds(Unit u) =>
        u.AltFt is { } ft ? Math.Max(0, Math.Round(ft / 100)).ToString("000") : "---";

    /// <summary>Mach: the speed over the ISA speed of sound at the altitude (standard day, no wind:
    /// the picture's speed is the unit's speed over the ground).</summary>
    public static double? MachOf(Unit u) => u.Spd is { } v && u.Alt is { } h ? MachOf(v, h) : null;

    /// <summary>Mach of a speed (m/s) at an altitude (m), the same way (missiles).</summary>
    public static double MachOf(double speedMps, double altM)
    {
        var kelvin = altM < 11000 ? 288.15 - 0.0065 * Math.Max(0, altM) : 216.65;
        return speedMps / Math.Sqrt(1.4 * 287.05 * kelvin);
    }

    /// <summary>Data block lines for a unit at a label level. Empty when nothing is shown.</summary>
    /// <summary>Plot age from which a contact's data block shows it: longer than one sweep of the
    /// slowest radars (rotodomes, 10-12 s).</summary>
    public const double StaleAgeS = 15;

    public static IReadOnlyList<string> DataBlock(Unit u, LabelLevel level)
    {
        if (level == LabelLevel.None) return [];
        var kind = KindOf(u);
        if (!IsAir(kind))
        {
            // Ground and ships: one line, type only; nothing for unidentified contacts.
            return level == LabelLevel.Full && u.Shown is not null ? [u.Shown] : [];
        }

        // A player: the name alone on the first line (the map puts it on a plate), then the type
        // with the altitude ("F16C 250↑" short, "F16C 250↑ 450 M0.81" full).
        if (IsPlayer(u))
            return [u.Player!, level == LabelLevel.Short ? $"{TypeOf(u)} {AltitudeHundreds(u)}{TrendOf(u)}" : $"{TypeOf(u)} {AltLine(u)}"];
        if (level == LabelLevel.Short) return [$"{TypeOf(u)} {AltitudeHundreds(u)}{TrendOf(u)}"];  // "F16C 250↑"
        var name = NameOf(u);
        return [name is null ? TypeOf(u) : $"{name} {TypeOf(u)}", AltLine(u)];
    }

    /// <summary>An own aircraft flown by a person (the map highlights them).</summary>
    public static bool IsPlayer(Unit u) => !u.Detected && !string.IsNullOrEmpty(u.Player);

    /// <summary>A DCS type shortened; a controller's identity as given ("29", "Su-34").</summary>
    public static string TypeOf(Unit u) => u.Type is not null ? ShortType(u.Type) : u.Assigned ?? "UNK";

    /// <summary>The block's altitude line: "250↑ 420 M0.85". FRZ: not updated while the radars
    /// holding it run NCTR. An old plot (no radar painted it for StaleAgeS or longer) shows its age.</summary>
    public static string AltLine(Unit u)
    {
        var age = u.Age >= StaleAgeS && !u.Frozen ? $" {u.Age:0}s" : "";
        var mach = MachOf(u) is { } m ? $" M{m:0.00}" : "";
        return $"{AltitudeHundreds(u)}{TrendOf(u)} {u.SpdKt ?? 0:0}{mach}{(u.Frozen ? " FRZ" : "")}{age}";
    }
}
