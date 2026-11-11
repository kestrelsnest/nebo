using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Nebo.Client.Core.Dtc;

/// <summary>The F/A-18C's SA page objects (data.SA), as DCS's SA\*.lua make them: threat rings
/// (MEZ_THRTS, at most 40: type from MEZ_THRTS_defs, text, radius NM, level), CAP orbits (CAP_PTS,
/// at most 9: anchor, course, length and diameter in metres, turn Left/Right), corridors (at most
/// 7 of up to 14 points, 10 NM wide), FAOR and FLOT lines (3 each, up to 7 points). Positions in
/// DCS map metres. DCS leaves holes when it deletes a corridor or line; these are kept without
/// holes (renumbered), and read in either form.</summary>
public sealed partial class HornetSa(Cartridge cartridge)
{
    public const int MaxThreats = 40, MaxCaps = 9;
    public const double DefaultCapLengthM = 10 * 1852, DefaultCapDiameterM = 5 * 1852, CorridorHalfWidthM = 5 * 1852;

    /// <summary>A kind of line: its list in data.SA, id prefix, how many and how many points.</summary>
    public sealed record LineKind(string Name, string Prefix, int Max, int MaxPoints);
    public static readonly LineKind Corridor = new("CORRIDORS", "CORR", 7, 14);
    public static readonly LineKind Faor = new("FAOR", "FAOR", 3, 7);
    public static readonly LineKind Flot = new("FLOT", "FLOT", 3, 7);

    private JsonObject Sa => cartridge.Data["SA"] as JsonObject ?? (JsonObject)(cartridge.Data["SA"] = new JsonObject());

    private JsonArray List(JsonObject parent, string key)
    {
        switch (parent[key])
        {
            case JsonArray a:
                return a;
            case JsonObject o:  // DCS with a hole ({"1":…,"3":…}): made a list in number order
                var list = new JsonArray();
                foreach (var kv in o.Where(kv => kv.Value is JsonObject).OrderBy(kv => int.TryParse(kv.Key, out var n) ? n : 999).ToList())
                {
                    o.Remove(kv.Key);
                    list.Add(kv.Value);
                }
                parent[key] = list;
                return list;
            default:
                var created = new JsonArray();
                parent[key] = created;
                return created;
        }
    }

    // ---- Threat rings ---------------------------------------------------------------------------

    public IReadOnlyList<JsonObject> Threats => List(Sa, "MEZ_THRTS").OfType<JsonObject>().ToList();

    /// <summary>A threat ring of a type (MEZ_THRTS_defs: name, default_text, detectionRange m,
    /// lvl); null when 40 are there.</summary>
    public JsonObject? AddThreat(double x, double y, JsonObject type)
    {
        var list = List(Sa, "MEZ_THRTS");
        if (list.Count >= MaxThreats) return null;
        var num = list.Count + 1;
        var t = new JsonObject
        {
            ["id"] = $"MEZ_THRTS_{num}", ["num"] = num, ["x"] = x, ["y"] = y,
            ["text"] = (string?)type["default_text"] ?? "", ["threat_type"] = (string?)type["name"] ?? "Custom",
            ["threat_ring_radius"] = (string?)type["name"] == "Custom" ? 20 : (HornetNav.Number(type["detectionRange"]) ?? 0) / 1852,
            ["threat_level"] = (int)(HornetNav.Number(type["lvl"]) ?? 1),
        };
        list.Add(t);
        return t;
    }

    public void SetThreat(int index, JsonObject type, string text, double radiusNm)
    {
        if (Threats.ElementAtOrDefault(index) is not { } t) return;
        t["threat_type"] = (string?)type["name"] ?? "Custom";
        t["threat_level"] = (int)(HornetNav.Number(type["lvl"]) ?? 1);
        t["text"] = text.Length > 5 ? text[..5] : text;
        t["threat_ring_radius"] = Math.Max(0, radiusNm);
    }

    public void RemoveThreat(int index)
    {
        var list = List(Sa, "MEZ_THRTS");
        if (index < 0 || index >= list.Count) return;
        list.RemoveAt(index);
        for (var i = 0; i < list.Count; i++)
            if (list[i] is JsonObject t)
            {
                t["num"] = i + 1;
                t["id"] = $"MEZ_THRTS_{i + 1}";
            }
    }

    // ---- CAP orbits -----------------------------------------------------------------------------

    public IReadOnlyList<JsonObject> Caps => List(Sa, "CAP_PTS").OfType<JsonObject>().ToList();

    public JsonObject? AddCap(double x, double y)
    {
        var list = List(Sa, "CAP_PTS");
        if (list.Count >= MaxCaps) return null;
        // As getNextCAP_PTS_PointId: one past the highest CAP_PTS_<n> in use.
        var next = list.OfType<JsonObject>().Select(c => CapId().Match((string?)c["id"] ?? "")).Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value)).DefaultIfEmpty(0).Max() + 1;
        var c = new JsonObject
        {
            ["id"] = $"CAP_PTS_{next}", ["num"] = list.Count + 1, ["x"] = x, ["y"] = y, ["course"] = 0,
            ["length"] = DefaultCapLengthM, ["diameter"] = DefaultCapDiameterM, ["turn_direction"] = "Left", ["note"] = "",
        };
        list.Add(c);
        return c;
    }

    public void SetCap(int index, double courseDeg, double lengthNm, double diameterNm, bool leftTurn, string note)
    {
        if (Caps.ElementAtOrDefault(index) is not { } c) return;
        c["course"] = ((courseDeg % 360) + 360) % 360;
        c["length"] = Math.Max(0.5, lengthNm) * 1852;
        c["diameter"] = Math.Max(0.5, diameterNm) * 1852;
        c["turn_direction"] = leftTurn ? "Left" : "Right";
        c["note"] = note;
    }

    public void RemoveCap(int index)
    {
        var list = List(Sa, "CAP_PTS");
        if (index < 0 || index >= list.Count) return;
        list.RemoveAt(index);
        for (var i = 0; i < list.Count; i++)
            if (list[i] is JsonObject c) c["num"] = i + 1;
    }

    [GeneratedRegex(@"^CAP_PTS_(\d+)$")]
    private static partial Regex CapId();

    // ---- Corridors, FAOR, FLOT ------------------------------------------------------------------

    private JsonArray Lines(LineKind kind) =>
        kind == Corridor ? List(Sa, "CORRIDORS")
        : List(Sa["FAOR_FLOT"] as JsonObject ?? (JsonObject)(Sa["FAOR_FLOT"] = new JsonObject()), kind.Name);

    public IReadOnlyList<JsonObject> LinesOf(LineKind kind) => Lines(kind).OfType<JsonObject>().ToList();

    public static IReadOnlyList<JsonObject> PointsOf(JsonObject line) => (line["points"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];

    /// <summary>A new line with one point; null when the kind is full.</summary>
    public JsonObject? AddLine(LineKind kind, double x, double y)
    {
        var lines = Lines(kind);
        if (lines.Count >= kind.Max) return null;
        var num = lines.Count + 1;
        var line = new JsonObject
        {
            ["id"] = $"{kind.Prefix}_{num}", ["num"] = num, ["note"] = "",
            ["points"] = new JsonArray(new JsonObject { ["id"] = $"{kind.Prefix}_{num}_PT_1", ["x"] = x, ["y"] = y }),
        };
        lines.Add(line);
        return line;
    }

    /// <summary>Appends a point to a line (false when it has its most points).</summary>
    public bool AddPoint(LineKind kind, int line, double x, double y) => InsertPoint(kind, line, int.MaxValue, x, y);

    /// <summary>A point into a line at a position (0 = first; past the end appends); false when the
    /// line has its most points. Point ids follow the new order.</summary>
    public bool InsertPoint(LineKind kind, int line, int at, double x, double y)
    {
        if (LinesOf(kind).ElementAtOrDefault(line) is not { } l || l["points"] is not JsonArray pts || pts.Count >= kind.MaxPoints) return false;
        pts.Insert(Math.Clamp(at, 0, pts.Count), new JsonObject { ["id"] = "", ["x"] = x, ["y"] = y });
        Renumber(kind);
        return true;
    }

    public void MovePoint(LineKind kind, int line, int point, double x, double y)
    {
        if (LinesOf(kind).ElementAtOrDefault(line) is not { } l || PointsOf(l).ElementAtOrDefault(point) is not { } p) return;
        p["x"] = x;
        p["y"] = y;
    }

    public void RemovePoint(LineKind kind, int line, int point)
    {
        if (LinesOf(kind).ElementAtOrDefault(line) is not { } l || l["points"] is not JsonArray pts) return;
        if (point < 0 || point >= pts.Count) return;
        pts.RemoveAt(point);
        if (pts.Count == 0) RemoveLine(kind, line);
        else Renumber(kind);
    }

    public void RemoveLine(LineKind kind, int line)
    {
        var lines = Lines(kind);
        if (line < 0 || line >= lines.Count) return;
        lines.RemoveAt(line);
        Renumber(kind);
    }

    public void SetLineNote(LineKind kind, int line, string note)
    {
        if (LinesOf(kind).ElementAtOrDefault(line) is { } l) l["note"] = note;
    }

    /// <summary>Lines numbered 1… in order, their points' ids following (as DCS rebuilds them).</summary>
    private void Renumber(LineKind kind)
    {
        var lines = LinesOf(kind);
        for (var i = 0; i < lines.Count; i++)
        {
            var n = i + 1;
            lines[i]["num"] = n;
            lines[i]["id"] = $"{kind.Prefix}_{n}";
            var pts = PointsOf(lines[i]);
            for (var j = 0; j < pts.Count; j++) pts[j]["id"] = $"{kind.Prefix}_{n}_PT_{j + 1}";
        }
    }

    // ---- Moving any of them ---------------------------------------------------------------------

    public void MoveThreat(int index, double x, double y)
    {
        if (Threats.ElementAtOrDefault(index) is not { } t) return;
        t["x"] = x;
        t["y"] = y;
    }

    public void MoveCap(int index, double x, double y)
    {
        if (Caps.ElementAtOrDefault(index) is not { } c) return;
        c["x"] = x;
        c["y"] = y;
    }

    // ---- Settings (declutter, sensors) ----------------------------------------------------------

    public JsonObject Settings => Sa["SETTINGS"] as JsonObject ?? (JsonObject)(Sa["SETTINGS"] = new JsonObject());

    // ---- A known threat as one of the SA page's ring types --------------------------------------

    private static readonly (string Key, string Type)[] Keys =
    [
        ("patriot", "SAM Patriot"), ("hawk", "SAM Hawk"), ("nasams", "SAM NASAMS"), ("rapier", "SAM Rapier"),
        ("roland", "SAM Roland"), ("iris", "SAM IRIS-T SLM"), ("hq-7", "SAM HQ-7"), ("avenger", "SAM Avenger"),
        ("chaparral", "SAM Chaparrel"), ("gepard", "SPAAA Gepard"), ("vulcan", "SPAAA Vulcan"),
        ("zsu-23", "SPAAA ZSU-23-4"), ("zsu-57", "SPAAA ZSU-57-2"), ("pgz", "SPAAA PGZ-09"),
        ("fire can", "AAA SON-9 - Fire Can"), ("son-9", "AAA SON-9 - Fire Can"), ("c-ram", "LPWS C-RAM"),
        ("kuznetsov", "Admiral Kuznetsov heavy aircraft cruiser"), ("1143.5", "Admiral Kuznetsov heavy aircraft cruiser"),
        ("arleigh", "Arleigh Burke guided missile destroyer"), ("condell", "Condell frigate"),
        ("forrestal", "Forrestal aircraft carrier"), ("grisha", "Grisha anti-submarine corvette"),
        ("invincible", "Invincible light aircraft carrier"), ("054a", "Jiangkai II guided missile frigate"),
        ("pyotr", "Kirov guided missile cruiser"), ("1144", "Kirov guided missile cruiser"),
        ("rezky", "Krivak II frigate/guard ship"), ("lynch", "Leander frigate"), ("052b", "Luyang I guided missile destroyer"),
        ("052c", "Luyang II guided missile destroyer"), ("neustrash", "Neustrashimy guided missile frigate"),
        ("cvn", "Nimitz aircraft carrier"), ("perry", "Oliver Hazard Perry guided missile frigate"),
        ("ropucha", "Ropucha I large landing ship"), ("moskva", "Slava guided missile cruiser"),
        ("molniya", "Tarantul III missile corvette"), ("tarawa", "Tarawa amphibious assault ship"),
        ("ticonderoga", "Ticonderoga guided missile cruiser"), ("22160", "Vasily Bykov patrol ship"),
        ("bykov", "Vasily Bykov patrol ship"), ("071", "Yuzhao amphibious transport dock"), ("021", "Huangfeng missile boat"),
    ];

    /// <summary>The ring type for a threat's name (a DCS unit name, or a controller's label like
    /// "SA-11" or an RWR "11"): by SA number, then by system or ship name; null when none fits.</summary>
    public static JsonObject? RingTypeFor(IEnumerable<JsonObject> types, string name)
    {
        var lower = name.ToLowerInvariant();
        var list = types.ToList();
        var sa = Regex.Match(lower, @"sa-(\d+)");
        var number = sa.Success ? sa.Groups[1].Value : Regex.Match(lower, @"^\s*(\d+)\s*$") is { Success: true } m ? m.Groups[1].Value : null;
        if (number is not null)
        {
            var matches = list.Where(d => Regex.IsMatch((string?)d["name"] ?? "", $@"SA-{number}(?!\d)")).ToList();
            var m2 = matches.FirstOrDefault(d => ((string?)d["name"] ?? "").Contains("(M2)"));
            if (m2 is not null && lower.Contains("m2")) return m2;
            if (matches.FirstOrDefault(d => !((string?)d["name"] ?? "").Contains("(M2)")) is { } plain) return plain;
        }
        foreach (var (key, type) in Keys)
            if (lower.Contains(key))
                return list.FirstOrDefault(d => (string?)d["name"] == type);
        return null;
    }
}
