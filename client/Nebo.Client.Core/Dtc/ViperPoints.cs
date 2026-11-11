using System.Text.Json.Nodes;

namespace Nebo.Client.Core.Dtc;

/// <summary>An offset point from a steerpoint, as DCS's F-16C dialog keeps one (an offset aimpoint
/// OAP_1 / OAP_2, a VIP or VRP leg): &lt;prefix&gt;_X / _Y the point, _DeltaX / _DeltaY steerpoint
/// minus point, _Range (m) and _Bearing (rad from true north, 0–2π) from the steerpoint. Past the
/// dialog's limit the point goes on the same bearing at the limit.</summary>
public static class ViperOffset
{
    public static void Place(JsonObject o, string prefix, double px, double py, double x, double y, double maxRangeM)
    {
        var brg = Math.Atan2(y - py, x - px);
        var range = Math.Sqrt((x - px) * (x - px) + (y - py) * (y - py));
        if (range > maxRangeM)
        {
            range = maxRangeM;
            x = px + Math.Cos(brg) * range;
            y = py + Math.Sin(brg) * range;
        }
        o[$"{prefix}_X"] = x;
        o[$"{prefix}_Y"] = y;
        o[$"{prefix}_DeltaX"] = px - x;
        o[$"{prefix}_DeltaY"] = py - y;
        o[$"{prefix}_Range"] = range;
        o[$"{prefix}_Bearing"] = brg < 0 ? brg + 2 * Math.PI : brg;   // RadTo2PI
    }

    /// <summary>The point after its steerpoint moved (the offset stays).</summary>
    public static void Follow(JsonObject o, string prefix, double px, double py)
    {
        o[$"{prefix}_X"] = px - (HornetNav.Number(o[$"{prefix}_DeltaX"]) ?? 0);
        o[$"{prefix}_Y"] = py - (HornetNav.Number(o[$"{prefix}_DeltaY"]) ?? 0);
    }

    public static (double X, double Y) At(JsonObject o, string prefix) =>
        (HornetNav.Number(o[$"{prefix}_X"]) ?? 0, HornetNav.Number(o[$"{prefix}_Y"]) ?? 0);
}

/// <summary>A numbered point list of the F-16C's MPD (DEST, GEO_LINES, THREAT_PTS): at most
/// <see cref="Max"/>, numbered by place (a delete renumbers, as DCS's onChange_delButton), ids
/// &lt;Prefix&gt;&lt;IdBase + number&gt;; editing clears the section's mirror flag (with it set DCS
/// takes the mission's).</summary>
public abstract class ViperList(Cartridge cartridge, string key, string idPrefix, int idBase, int max)
{
    public int Max => max;

    protected JsonObject Mpd => cartridge.Data["MPD"] as JsonObject ?? (JsonObject)(cartridge.Data["MPD"] = new JsonObject());
    protected JsonArray List => Mpd[key] as JsonArray ?? (JsonArray)(Mpd[key] = new JsonArray());

    public bool Mirror
    {
        get => Mpd[$"mirror_{key}"]?.GetValue<bool>() == true;
        set => Mpd[$"mirror_{key}"] = value;
    }

    public IReadOnlyList<JsonObject> Points => List.OfType<JsonObject>().ToList();

    public JsonObject? Point(int num) => num >= 1 && num <= List.Count ? List[num - 1] as JsonObject : null;

    /// <summary>A point at a place in the list (1-based, clamped); null when the list is full.</summary>
    protected JsonObject? Insert(int num, JsonObject p)
    {
        if (List.Count >= max) return null;
        List.Insert(Math.Clamp(num - 1, 0, List.Count), p);
        Renumber();
        Mirror = false;
        return p;
    }

    public void Remove(int num)
    {
        if (Point(num) is not { } p) return;
        List.Remove(p);
        Renumber();
        Mirror = false;
    }

    public void Move(int num, double x, double y)
    {
        if (Point(num) is not { } p) return;
        p["x"] = x;
        p["y"] = y;
        Mirror = false;
    }

    private void Renumber()
    {
        var i = 0;
        foreach (var p in List.OfType<JsonObject>())
        {
            i++;
            p["number"] = i;
            p["id"] = $"{idPrefix}{idBase + i}";
        }
    }

    /// <summary>Letters and digits, upper case, at most n (custom_input_filter_number_letter).</summary>
    public static string Code(string text, int n)
    {
        var s = new string(text.Where(char.IsAsciiLetterOrDigit).ToArray()).ToUpperInvariant();
        return s.Length > n ? s[..n] : s;
    }

    protected static string Note(string note) => note.Length > 30 ? note[..30] : note;
}

/// <summary>The F-16C's destination points (data.MPD.DEST, DEST.lua): 19 at most, steerpoints 81–99
/// in the jet, a 3-character text and a note, elevation in metres.</summary>
public sealed class ViperDest(Cartridge cartridge) : ViperList(cartridge, "DEST", "DEST", 80, 19)
{
    public const int FirstNumber = 81;

    public JsonObject? Add(double x, double y, double elevM) => Insert(int.MaxValue, x, y, elevM);

    public JsonObject? Insert(int num, double x, double y, double elevM) => Insert(num, new JsonObject
    {
        ["x"] = x, ["y"] = y, ["alt"] = elevM, ["text"] = $"D{FirstNumber + Points.Count}", ["note"] = "",   // as DCS names a new one
    });

    public void Set(int num, string text, string note, double elevM)
    {
        if (Point(num) is not { } p) return;
        p["text"] = Code(text, 3);
        p["note"] = Note(note);
        p["alt"] = elevM;
        Mirror = false;
    }
}

/// <summary>The F-16C's geographic lines (data.MPD.GEO_LINES, GEO_LINES.lua): 25 points at most
/// (steerpoints 31–55), each on one of lines 1–4 or none (L1–L4, one at most); a line joins its
/// points in list order.</summary>
public sealed class ViperGeo(Cartridge cartridge) : ViperList(cartridge, "GEO_LINES", "GEO_LINES", 30, 25)
{
    public const int Lines = 4;

    public static int LineOf(JsonObject p) =>
        Enumerable.Range(1, Lines).FirstOrDefault(l => p[$"L{l}"]?.GetValue<bool>() == true);

    /// <summary>The point numbers of line 1–4, in order.</summary>
    public IReadOnlyList<int> Line(int line) =>
        Points.Select((p, i) => (p, n: i + 1)).Where(t => LineOf(t.p) == line).Select(t => t.n).ToList();

    /// <summary>A point on a line (0: none) at a place in the list.</summary>
    public JsonObject? Insert(int num, int line, double x, double y, double elevM)
    {
        var p = new JsonObject { ["x"] = x, ["y"] = y, ["alt"] = elevM, ["note"] = "" };
        for (var l = 1; l <= Lines; l++) p[$"L{l}"] = l == line;
        return Insert(num, p);
    }

    public void SetLine(int num, int line)
    {
        if (Point(num) is not { } p) return;
        for (var l = 1; l <= Lines; l++) p[$"L{l}"] = l == line;
        Mirror = false;
    }

    public void Set(int num, string note, double elevM)
    {
        if (Point(num) is not { } p) return;
        p["note"] = Note(note);
        p["alt"] = elevM;
        Mirror = false;
    }
}

/// <summary>The F-16C's preplanned threats (data.MPD.THREAT_PTS, THREAT_PTS.lua): 15 at most
/// (steerpoints 56–70), a threat type of THREAT_PTS_defs giving radius (m), altitude (m) and a
/// 3-character text; only "Custom" (the first) takes its own radius and altitude.</summary>
public sealed class ViperThreats(Cartridge cartridge) : ViperList(cartridge, "THREAT_PTS", "THREAT_PTS", 55, 15)
{
    /// <summary>The threat types (name, detectionRange m, altitude m, default_text), Custom first.</summary>
    public static IReadOnlyList<JsonObject> Types { get; } =
        DtcDefs.For(Cartridge.ViperType)["THREAT_PTS_defs/THREAT_PTS_defs"]!.AsArray().OfType<JsonObject>().ToList();

    public JsonObject? Add(double x, double y, double elevM, int type) => Insert(int.MaxValue, x, y, elevM, type);

    public JsonObject? Insert(int num, double x, double y, double elevM, int type)
    {
        var d = Types[Math.Clamp(type, 1, Types.Count) - 1];
        return Insert(num, new JsonObject
        {
            ["x"] = x, ["y"] = y, ["elev"] = elevM, ["threatName"] = (string?)d["name"], ["radius"] = HornetNav.Number(d["detectionRange"]) ?? 0,
            ["alt"] = HornetNav.Number(d["altitude"]) ?? 0, ["text"] = (string?)d["default_text"], ["ring"] = true,
            ["def_num"] = Math.Clamp(type, 1, Types.Count),
        });
    }

    /// <summary>Another threat type: its radius, altitude and text come with it.</summary>
    public void SetType(int num, int type)
    {
        if (Point(num) is not { } p) return;
        var d = Types[Math.Clamp(type, 1, Types.Count) - 1];
        p["def_num"] = Math.Clamp(type, 1, Types.Count);
        p["threatName"] = (string?)d["name"];
        p["radius"] = HornetNav.Number(d["detectionRange"]) ?? 0;
        p["alt"] = HornetNav.Number(d["altitude"]) ?? 0;
        p["text"] = (string?)d["default_text"];
        Mirror = false;
    }

    public static bool IsCustom(JsonObject p) => (HornetNav.Number(p["def_num"]) ?? 1) == 1;

    /// <summary>Text (3), ring shown; radius (m) and altitude (m) for a Custom threat only.</summary>
    public void Set(int num, string text, bool ring, double radiusM, double altM)
    {
        if (Point(num) is not { } p) return;
        p["text"] = Code(text, 3);
        p["ring"] = ring;
        if (IsCustom(p))
        {
            p["radius"] = Math.Max(0, radiusM);
            p["alt"] = Math.Max(0, altM);
        }
        Mirror = false;
    }
}

/// <summary>The F-16C's visual initial / reference points (data.MPD.VIPVRP, VIPVRP.lua): VIP_Number
/// and VRP_Number are steerpoints; from the VIP the target (VIPTOTGT) and pop-up (VIPTOPUP) legs,
/// from the VRP's target the reference point (TGTTOVRP) and pop-up (TGTTOPUP); each leg an offset
/// of at most 80 NM with its elevation and an enabled flag.</summary>
public sealed class ViperVipVrp(Cartridge cartridge)
{
    public static readonly string[] Legs = ["VIPTOTGT", "VIPTOPUP", "TGTTOVRP", "TGTTOPUP"];
    public const double MaxRangeM = 80 * 1852;

    private JsonObject Mpd => cartridge.Data["MPD"] as JsonObject ?? (JsonObject)(cartridge.Data["MPD"] = new JsonObject());
    public JsonObject Data => Mpd["VIPVRP"] as JsonObject ?? (JsonObject)(Mpd["VIPVRP"] = new JsonObject());

    /// <summary>The leg's enabled flag (DCS's names differ per leg).</summary>
    public static string EnabledKey(string leg) => leg switch
    {
        "VIPTOTGT" => "VIP_Enabled", "VIPTOPUP" => "VIPPUP_Enabled", "TGTTOVRP" => "VRP_Enabled", _ => "VRPPUP_Enabled",
    };

    /// <summary>The steerpoint a leg starts from: the VIP's or the VRP's.</summary>
    public int SteerpointOf(string leg) => (int)(HornetNav.Number(Data[leg.StartsWith("VIP") ? "VIP_Number" : "VRP_Number"]) ?? 1);

    /// <summary>The VIP or VRP on another steerpoint: its legs keep their offsets from it.</summary>
    public void SetSteerpoint(bool vip, int num)
    {
        Data[vip ? "VIP_Number" : "VRP_Number"] = Math.Max(1, num);
        if (new ViperNav(cartridge).Point(num) is not { } p) return;
        foreach (var leg in Legs.Where(l => l.StartsWith("VIP") == vip && IsSet(l)))
            ViperOffset.Follow(Data, leg, HornetNav.Number(p["x"]) ?? 0, HornetNav.Number(p["y"]) ?? 0);
    }

    public bool Enabled(string leg) => Data[EnabledKey(leg)]?.GetValue<bool>() == true;
    public void SetEnabled(string leg, bool on) => Data[EnabledKey(leg)] = on;

    /// <summary>A leg placed (from its steerpoint at px, py) and switched on.</summary>
    public void Place(string leg, double px, double py, double x, double y, double elevM)
    {
        ViperOffset.Place(Data, leg, px, py, x, y, MaxRangeM);
        Data[$"{leg}_Elevation"] = elevM;
        SetEnabled(leg, true);
    }

    /// <summary>As onChange_bClear*: on its steerpoint, no offset, off.</summary>
    public void Clear(string leg, double px, double py)
    {
        Data[$"{leg}_X"] = px;
        Data[$"{leg}_Y"] = py;
        foreach (var f in new[] { "DeltaX", "DeltaY", "Bearing", "Range", "Elevation" }) Data[$"{leg}_{f}"] = 0.0;
        SetEnabled(leg, false);
    }

    public bool IsSet(string leg) => HornetNav.Number(Data[$"{leg}_Range"]) is > 0;
}

/// <summary>The F-16C's symbol colours (data.COLR, COLR.lua): each symbol of the MFD FCR, HSD / HAD
/// and MISC pages one of colorName (1 White … 8 Gray).</summary>
public sealed class ViperColr(Cartridge cartridge)
{
    public static readonly string[] Pages = ["MFDFCR", "HSDHAD", "MISC"];

    /// <summary>The colours by id (COLR_defs colorName).</summary>
    public static IReadOnlyList<string> Colors { get; } =
        (DtcDefs.For(Cartridge.ViperType)["COLR_defs/colorName"] as JsonObject ?? [])
            .OrderBy(kv => HornetNav.Number(kv.Value) ?? 0).Select(kv => kv.Key).ToList();

    private JsonObject Colr => cartridge.Data["COLR"] as JsonObject ?? (JsonObject)(cartridge.Data["COLR"] = new JsonObject());

    /// <summary>A page's symbols (key, colour id) in DCS's default set's order of names.</summary>
    public IReadOnlyList<(string Key, int Color)> Page(string page) =>
        Colr.Where(kv => kv.Key.StartsWith(page + "_", StringComparison.Ordinal))
            .Select(kv => (kv.Key, (int)(HornetNav.Number(kv.Value) ?? 1))).OrderBy(t => t.Key).ToList();

    public void Set(string key, int color)
    {
        if (Colr.ContainsKey(key)) Colr[key] = Math.Clamp(color, 1, Colors.Count);
    }
}
