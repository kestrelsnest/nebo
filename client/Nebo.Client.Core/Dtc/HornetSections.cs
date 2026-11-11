using System.Text.Json.Nodes;

namespace Nebo.Client.Core.Dtc;

/// <summary>COMM1 / COMM2 of an F/A-18C cartridge (data.COMM.COMM1|2: Channel_1–20 and the
/// special C, G, M, S presets; frequency MHz, modulation 0 AM / 1 FM, name; Guard), with the
/// rules of DCS's COMM_common.lua: 30–399.975 MHz, gaps snapped to the nearest band edge, FM
/// only in 30–87.995 and 156–173.995, AM only in 88–135.995, either in the rest.</summary>
public sealed class HornetComm(Cartridge cartridge)
{
    public const int Am = 0, Fm = 1;
    public static readonly string[] Presets =
        [.. Enumerable.Range(1, 20).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)), "C", "G", "M", "S"];

    private JsonObject Radio(int radio)
    {
        var comm = cartridge.Data["COMM"] as JsonObject ?? (JsonObject)(cartridge.Data["COMM"] = new JsonObject());
        return comm[$"COMM{radio}"] as JsonObject ?? (JsonObject)(comm[$"COMM{radio}"] = new JsonObject());
    }

    public JsonObject? Preset(int radio, string preset) => Radio(radio)[$"Channel_{preset}"] as JsonObject;

    /// <summary>Sets a preset; the frequency snapped into a band and the modulation into what it allows.</summary>
    public void Set(int radio, string preset, double mhz, int modulation, string name)
    {
        var f = Snap(mhz);
        Radio(radio)[$"Channel_{preset}"] = new JsonObject
        {
            ["frequency"] = f, ["modulation"] = Allowed(f, modulation), ["name"] = name.Length > 8 ? name[..8] : name,
        };
    }

    public bool Guard(int radio) => Radio(radio)["Guard"]?.GetValue<bool>() == true;
    public void SetGuard(int radio, bool on) => Radio(radio)["Guard"] = on;

    /// <summary>The nearest frequency the radio tunes (COMM_common.lua's gaps and limits).</summary>
    public static double Snap(double mhz)
    {
        mhz = Math.Clamp(mhz, 30.0, 399.975);
        static double Nearest(double f, double low, double high) => f - low < high - f ? low : high;
        if (mhz > 135.995 && mhz < 136.0) return Nearest(mhz, 135.995, 136.0);
        if (mhz > 155.995 && mhz < 156.0) return Nearest(mhz, 155.995, 156.0);
        if (mhz > 173.995 && mhz < 225.0) return Nearest(mhz, 173.995, 225.0);
        return Math.Round(mhz, 3);
    }

    /// <summary>The modulation a frequency allows: the wanted one where both work.</summary>
    public static int Allowed(double mhz, int wanted)
    {
        if ((mhz >= 30.0 && mhz <= 87.995) || (mhz >= 156.0 && mhz <= 173.995)) return Fm;
        if ((mhz >= 225.0 && mhz <= 399.975) || (mhz >= 136.0 && mhz <= 155.995)) return wanted == Fm ? Fm : Am;
        return Am;
    }
}

/// <summary>GPS waypoints of an F/A-18C cartridge (data.GPS_WYPT): numbers 1–200 (two tables of
/// 100), DCS map metres, altitude m (−2,000 to 25,000 ft), a 5-letter text and a note, as DCS's
/// GPS_WYPT_*.lua makes them.</summary>
public sealed class HornetGps(Cartridge cartridge)
{
    public const int MaxPoints = 200;
    public const double MinAltFt = -2000, MaxAltFt = 25000;

    private JsonArray Points => cartridge.Data["GPS_WYPT"] as JsonArray ?? (JsonArray)(cartridge.Data["GPS_WYPT"] = new JsonArray());

    public IReadOnlyList<JsonObject> All => Points.OfType<JsonObject>().OrderBy(HornetNav.Num).ToList();

    public JsonObject? Point(int num) => Points.OfType<JsonObject>().FirstOrDefault(p => HornetNav.Num(p) == num);

    /// <summary>A new GPS waypoint with the first free number; null when all 200 are used.</summary>
    public JsonObject? Add(double x, double y, double altM)
    {
        var used = Points.OfType<JsonObject>().Select(HornetNav.Num).ToHashSet();
        var num = Enumerable.Range(1, MaxPoints).FirstOrDefault(n => !used.Contains(n));
        if (num == 0) return null;
        var p = new JsonObject { ["id"] = $"GPS_WYPT{num}", ["wypt_num"] = num, ["x"] = x, ["y"] = y, ["alt"] = altM, ["text"] = $"GW{num}", ["note"] = "" };
        // Kept in number order, as DCS sorts them (GPS_WYPT_sortByNum).
        var at = Points.OfType<JsonObject>().TakeWhile(q => HornetNav.Num(q) < num).Count();
        Points.Insert(at, p);
        return p;
    }

    public void Remove(int num)
    {
        if (Point(num) is { } p) Points.Remove(p);
    }

    public void Move(int num, double x, double y)
    {
        if (Point(num) is not { } p) return;
        p["x"] = x;
        p["y"] = y;
    }

    public void Set(int num, string text, string note, double altFt)
    {
        if (Point(num) is not { } p) return;
        p["text"] = text.Length > 5 ? text[..5] : text;
        p["note"] = note;
        p["alt"] = Math.Clamp(altFt, MinAltFt, MaxAltFt) / HornetNav.FeetPerMeter;
    }
}

/// <summary>The TACAN stations of an F/A-18C cartridge (data.TCN, at most 10), as DCS's
/// TACAN.lua stores a station from the terrain's beacons: channel, band, frequency (Hz, as
/// beacons.lua), callsign, display name (DCS matches terrain stations by it on load), position
/// and elevation.</summary>
public sealed class HornetTacan(Cartridge cartridge)
{
    public const int MaxStations = 10;

    private JsonArray List => cartridge.Data["TCN"] as JsonArray ?? (JsonArray)(cartridge.Data["TCN"] = new JsonArray());

    public IReadOnlyList<JsonObject> Stations => List.OfType<JsonObject>().ToList();

    public bool Has(string displayName) => List.OfType<JsonObject>().Any(s => (string?)s["display_name"] == displayName);

    /// <summary>Adds a terrain TACAN (false when 10 are there or it is already in).</summary>
    public bool Add(Beacon b)
    {
        if (List.Count >= MaxStations || Has(b.Name) || b.Channel is not { } ch) return false;
        List.Add(new JsonObject
        {
            ["channel"] = ch, ["modeChannel"] = "X", ["frequency"] = b.FrequencyHz, ["callsign"] = b.Callsign,
            ["display_name"] = b.Name, ["x"] = b.X, ["y"] = b.Z, ["elevation"] = b.Alt,
        });
        return true;
    }

    public void RemoveAt(int index)
    {
        if (index >= 0 && index < List.Count) List.RemoveAt(index);
    }

    public void MoveUp(int index)
    {
        if (index <= 0 || index >= List.Count) return;
        var s = List[index]!;
        List.RemoveAt(index);
        List.Insert(index - 1, s);
    }
}
