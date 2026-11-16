using System.Text.Json.Nodes;

namespace Nebo.Client.Core.Dtc;

/// <summary>The F-14B(U)'s limits, read from DCS's own file (tools/DtcDefs/tomcat.lua).</summary>
internal static class TomcatDefs
{
    public static DtcDefs Defs => DtcDefs.For(Cartridge.TomcatType);
    public static int Int(string name) => (int)Defs.Number($"constants/{name}");
}

/// <summary>The F-14B(U)'s flight plans (data.NAV, F-14BU_DTC.lua): 12 plans, plan 1 being the
/// mission's route (its waypoints are not the cartridge's: only its lines are edited); each plan
/// holds waypoints (name of 8, elevation ft, ground speed kt or a time over target), up to 4 lines
/// (3 when the route is drawn as a line) of up to 9 points (8 when closed: the jet repeats the first
/// point), and additional points, 50 points in all. Points carry DCS map metres and lat / lon (the
/// jet reads both); elevations are in feet.</summary>
public sealed class TomcatNav(Cartridge cartridge)
{
    public static int Plans => TomcatDefs.Int("NAV_MAX_PLANS");
    public static int MaxPoints => TomcatDefs.Int("NAV_MAX_WAYPOINTS");
    public static int MaxLines => TomcatDefs.Int("NAV_MAX_LINES");
    public static int MaxLinePoints => TomcatDefs.Int("NAV_MAX_LINE_PTS");
    public static int MaxClosedLinePoints => TomcatDefs.Int("NAV_MAX_CLOSED_LINE_PTS");
    public static int NameLength => TomcatDefs.Int("NAV_WP_NAME_MAXLEN");
    public static int PlanNameLength => TomcatDefs.Int("NAV_PLAN_NAME_MAXLEN");
    /// <summary>The first plan whose waypoints the cartridge sets (plan 1 is the mission's route).</summary>
    public const int FirstOwnPlan = 2;

    /// <summary>A point's position: map metres and lat / lon, elevation in metres (stored in feet).</summary>
    public readonly record struct At(double X, double Y, double Lat, double Lon, double ElevM);

    private JsonArray List
    {
        get
        {
            if (cartridge.Data["NAV"] is not JsonArray a) cartridge.Data["NAV"] = a = [];
            while (a.Count < Plans) a.Add(new JsonObject { ["name"] = "", ["waypoints"] = new JsonArray(), ["lines"] = new JsonArray(), ["additional_points"] = new JsonArray(), ["route_as_line"] = false });
            return a;
        }
    }

    public JsonObject Plan(int plan) => (JsonObject)List[Math.Clamp(plan, 1, Plans) - 1]!;

    private static JsonArray Array(JsonObject o, string key) => o[key] as JsonArray ?? (JsonArray)(o[key] = new JsonArray());

    public IReadOnlyList<JsonObject> Waypoints(int plan) => Array(Plan(plan), "waypoints").OfType<JsonObject>().ToList();
    public IReadOnlyList<JsonObject> Lines(int plan) => Array(Plan(plan), "lines").OfType<JsonObject>().ToList();
    public static IReadOnlyList<JsonObject> PointsOf(JsonObject line) => Array(line, "points").OfType<JsonObject>().ToList();
    public IReadOnlyList<JsonObject> Extra(int plan) => Array(Plan(plan), "additional_points").OfType<JsonObject>().ToList();

    public int TotalPoints(int plan) => Waypoints(plan).Count + Lines(plan).Sum(l => PointsOf(l).Count) + Extra(plan).Count;

    public static bool EditsWaypoints(int plan) => plan >= FirstOwnPlan;

    /// <summary>Feet as the dialog keeps them (metersToFeet: whole feet).</summary>
    public static double Feet(double m) => Math.Floor(m * 3.28084 + 0.5);

    private static void Place(JsonObject p, At at)
    {
        p["x"] = at.X;
        p["y"] = at.Y;
        p["lat"] = at.Lat;
        p["lon"] = at.Lon;
        p["elev"] = Feet(at.ElevM);
    }

    // ---- Plans ------------------------------------------------------------------------------------

    public void SetPlanName(int plan, string name) => Plan(plan)["name"] = name.Length > PlanNameLength ? name[..PlanNameLength] : name;

    public static bool RouteAsLine(JsonObject plan) => plan["route_as_line"]?.GetValue<bool>() == true;

    /// <summary>The route drawn as a line takes one of the four lines: a fourth goes.</summary>
    public void SetRouteAsLine(int plan, bool on)
    {
        var p = Plan(plan);
        p["route_as_line"] = on;
        var lines = Array(p, "lines");
        while (lines.Count > LineLimit(plan)) lines.RemoveAt(lines.Count - 1);
    }

    public int LineLimit(int plan) => RouteAsLine(Plan(plan)) ? MaxLines - 1 : MaxLines;

    // ---- Waypoints --------------------------------------------------------------------------------

    /// <summary>A waypoint at a place in the plan (1-based, clamped); null in plan 1 or when the plan is full.</summary>
    public JsonObject? InsertWaypoint(int plan, int at, At pos)
    {
        if (!EditsWaypoints(plan) || TotalPoints(plan) >= MaxPoints) return null;
        var wp = new JsonObject { ["name"] = "", ["spd"] = 0, ["tot"] = "" };
        Place(wp, pos);
        var list = Array(Plan(plan), "waypoints");
        list.Insert(Math.Clamp(at - 1, 0, list.Count), wp);
        return wp;
    }

    public JsonObject? AddWaypoint(int plan, At pos) => InsertWaypoint(plan, int.MaxValue, pos);

    public void MoveWaypoint(int plan, int num, At pos)
    {
        if (Waypoints(plan).ElementAtOrDefault(num - 1) is { } wp) Place(wp, pos);
    }

    public void RemoveWaypoint(int plan, int num)
    {
        var list = Array(Plan(plan), "waypoints");
        if (num >= 1 && num <= list.Count) list.RemoveAt(num - 1);
    }

    /// <summary>Name (8), elevation (ft, 0–60,000), ground speed (kt, 0–999) or a time over target:
    /// the dialog takes one of the two, the time when both are given.</summary>
    public void SetWaypoint(int plan, int num, string name, double elevFt, double speedKt, string tot)
    {
        if (Waypoints(plan).ElementAtOrDefault(num - 1) is not { } wp) return;
        wp["name"] = name.Length > NameLength ? name[..NameLength] : name;
        wp["elev"] = Math.Clamp(Math.Round(elevFt), 0, 60000);
        tot = tot.Trim();
        wp["tot"] = tot;
        wp["spd"] = tot.Length > 0 ? 0 : Math.Clamp(Math.Round(speedKt), 0, 999);
    }

    /// <summary>The waypoints in a new order (old numbers; the rest after them).</summary>
    public void ReorderWaypoints(int plan, IReadOnlyList<int> order)
    {
        var list = Array(Plan(plan), "waypoints");
        var pts = list.OfType<JsonObject>().ToList();
        var moved = order.Where(n => n >= 1 && n <= pts.Count).Distinct().Select(n => pts[n - 1]).ToList();
        moved.AddRange(pts.Where(p => !moved.Contains(p)));
        list.Clear();
        foreach (var p in moved) list.Add(p);
    }

    // ---- Lines ------------------------------------------------------------------------------------

    /// <summary>A new empty line; its index, or null when the plan has its lines.</summary>
    public int? AddLine(int plan)
    {
        var lines = Array(Plan(plan), "lines");
        if (lines.Count >= LineLimit(plan)) return null;
        lines.Add(new JsonObject { ["points"] = new JsonArray(), ["closed"] = false });
        return lines.Count - 1;
    }

    public void RemoveLine(int plan, int line)
    {
        var lines = Array(Plan(plan), "lines");
        if (line >= 0 && line < lines.Count) lines.RemoveAt(line);
    }

    public static bool IsClosed(JsonObject line) => line["closed"]?.GetValue<bool>() == true;

    public static int LinePointLimit(JsonObject line) => IsClosed(line) ? MaxClosedLinePoints : MaxLinePoints;

    /// <summary>A point on a line at a place (0-based, clamped); false when the line or plan is full.</summary>
    public bool InsertLinePoint(int plan, int line, int at, At pos)
    {
        if (Lines(plan).ElementAtOrDefault(line) is not { } l || PointsOf(l).Count >= LinePointLimit(l) || TotalPoints(plan) >= MaxPoints) return false;
        var p = new JsonObject();
        Place(p, pos);
        var pts = Array(l, "points");
        pts.Insert(Math.Clamp(at, 0, pts.Count), p);
        return true;
    }

    public void MoveLinePoint(int plan, int line, int point, At pos)
    {
        if (Lines(plan).ElementAtOrDefault(line) is { } l && PointsOf(l).ElementAtOrDefault(point) is { } p) Place(p, pos);
    }

    /// <summary>A line's point deleted; a line left without points goes.</summary>
    public void RemoveLinePoint(int plan, int line, int point)
    {
        if (Lines(plan).ElementAtOrDefault(line) is not { } l) return;
        var pts = Array(l, "points");
        if (point >= 0 && point < pts.Count) pts.RemoveAt(point);
        if (pts.Count == 0) RemoveLine(plan, line);
    }

    /// <summary>Closed (a polygon): refused, as in the dialog, when the line has more than 8 points.</summary>
    public bool SetClosed(int plan, int line, bool closed)
    {
        if (Lines(plan).ElementAtOrDefault(line) is not { } l || closed && PointsOf(l).Count > MaxClosedLinePoints) return false;
        l["closed"] = closed;
        return true;
    }

    // ---- Additional points ------------------------------------------------------------------------

    public JsonObject? AddExtra(int plan, At pos)
    {
        if (TotalPoints(plan) >= MaxPoints) return null;
        var p = new JsonObject { ["name"] = "" };
        Place(p, pos);
        Array(Plan(plan), "additional_points").Add(p);
        return p;
    }

    public void MoveExtra(int plan, int num, At pos)
    {
        if (Extra(plan).ElementAtOrDefault(num - 1) is { } p) Place(p, pos);
    }

    public void RemoveExtra(int plan, int num)
    {
        var list = Array(Plan(plan), "additional_points");
        if (num >= 1 && num <= list.Count) list.RemoveAt(num - 1);
    }

    public void SetExtra(int plan, int num, string name, double elevFt)
    {
        if (Extra(plan).ElementAtOrDefault(num - 1) is not { } p) return;
        p["name"] = name.Length > NameLength ? name[..NameLength] : name;
        p["elev"] = Math.Clamp(Math.Round(elevFt), 0, 60000);
    }
}

/// <summary>The F-14B(U)'s countermeasures (data.CMDS): programs PROG_1–8 with a priority (0–9) and
/// per expendable burst / salvo quantity (0–99) and interval (0–10 s, 10 ms steps); bingo
/// quantities (0–99); per threat category (SAM, AAA, Aircraft, Naval, Unknown) the AUTO program
/// (0 none, 1–8) and threshold (1 Search, 2 Track, 3 Launch); per known DCS unit type an override.</summary>
public sealed class TomcatCmds(Cartridge cartridge)
{
    public static readonly string[] Expendables = ["Chaff", "Flare", "Other1", "Other2"];

    public static IReadOnlyList<string> Programs { get; } = Names("CMDS_programs_name");
    public static IReadOnlyList<string> Categories { get; } =
        (TomcatDefs.Defs["CMDS_THREAT_CATEGORIES"] as JsonArray ?? []).Select(v => (string?)v ?? "").ToList();
    /// <summary>The AUTO program names, 0 (NONE) to 8.</summary>
    public static IReadOnlyList<string> AutoNames { get; } =
        (TomcatDefs.Defs["CMDS_AUTO_PROGRAM_NAMES"] as JsonObject ?? []).OrderBy(kv => int.Parse(kv.Key)).Select(kv => (string?)kv.Value ?? "").ToList();
    public static IReadOnlyList<string> Thresholds { get; } = Names("CMDS_THRESHOLD_NAMES");
    /// <summary>The DCS unit types an override can be for: key, display name, category.</summary>
    public static IReadOnlyList<(string Key, string Display, string Category)> KnownThreats { get; } =
        (TomcatDefs.Defs["CMDS_KNOWN_THREATS"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(t => ((string?)t["key"] ?? "", (string?)t["display"] ?? "", (string?)t["cat"] ?? "")).ToList();

    private static List<string> Names(string key) => TomcatDefs.Defs[key] switch
    {
        JsonArray a => a.Select(v => (string?)v ?? "").ToList(),
        JsonObject o => o.OrderBy(kv => int.Parse(kv.Key)).Select(kv => (string?)kv.Value ?? "").ToList(),
        _ => [],
    };

    private JsonObject Cmds => cartridge.Data["CMDS"] as JsonObject ?? (JsonObject)(cartridge.Data["CMDS"] = new JsonObject());
    private JsonObject Child(string key) => Cmds[key] as JsonObject ?? (JsonObject)(Cmds[key] = new JsonObject());

    public JsonObject Program(string name) => Child("CMDSProgramSettings")[name] as JsonObject ?? new JsonObject();

    public void SetPriority(string program, int priority) => Program(program)["Priority"] = Math.Clamp(priority, 0, 9);

    public void SetProgram(string program, string expendable, int burstQty, double burstInterval, int salvoQty, double salvoInterval)
    {
        if (Program(program)[expendable] is not JsonObject e) return;
        e["BurstQuantity"] = Math.Clamp(burstQty, 0, 99);
        e["BurstInterval"] = Math.Round(Math.Clamp(burstInterval, 0, 10), 2);
        e["SalvoQuantity"] = Math.Clamp(salvoQty, 0, 99);
        e["SalvoInterval"] = Math.Round(Math.Clamp(salvoInterval, 0, 10), 2);
    }

    public JsonObject Bingo => Child("CMDSBingoSettings");

    public void SetBingo(int chaff, int flares, int other1, int other2)
    {
        var b = Bingo;
        b["ChaffNum"] = Math.Clamp(chaff, 0, 99);
        b["FlaresNum"] = Math.Clamp(flares, 0, 99);
        b["Other1Num"] = Math.Clamp(other1, 0, 99);
        b["Other2Num"] = Math.Clamp(other2, 0, 99);
    }

    public (int Program, int Threshold) Auto(string category) =>
        Child("CMDSAutoPrograms")[category] is JsonObject a ? ((int)(HornetNav.Number(a["Program"]) ?? 0), (int)(HornetNav.Number(a["Threshold"]) ?? 1)) : (0, 1);

    public void SetAuto(string category, int program, int threshold) =>
        Child("CMDSAutoPrograms")[category] = new JsonObject { ["Program"] = Math.Clamp(program, 0, 8), ["Threshold"] = Math.Clamp(threshold, 1, 3) };

    /// <summary>The per-type overrides, sorted by key (DCS writes an empty one as []).</summary>
    public IReadOnlyList<(string Key, int Program, int Threshold)> Overrides =>
        (Cmds["CMDSAutoOverrides"] as JsonObject ?? []).Where(kv => kv.Value is JsonObject).OrderBy(kv => kv.Key)
            .Select(kv => (kv.Key, (int)(HornetNav.Number(kv.Value!["Program"]) ?? 0), (int)(HornetNav.Number(kv.Value["Threshold"]) ?? 2))).ToList();

    public void SetOverride(string key, int program, int threshold)
    {
        if (!KnownThreats.Any(t => t.Key == key)) return;
        if (Cmds["CMDSAutoOverrides"] is not JsonObject o) Cmds["CMDSAutoOverrides"] = o = new JsonObject();
        o[key] = new JsonObject { ["Program"] = Math.Clamp(program, 0, 8), ["Threshold"] = Math.Clamp(threshold, 1, 3) };
    }

    public void RemoveOverride(string key) => (Cmds["CMDSAutoOverrides"] as JsonObject)?.Remove(key);
}

/// <summary>The F-14B(U)'s GBU-31 targets (data.JDAM): 4 stations (STA 3–6) of 8 pre-planned targets,
/// each a point (map metres, lat / lon, elevation m) with an attack heading (0–359.9°), drop altitude
/// (10,000–50,000 ft) and ground speed (250–750 kt), optional impact heading and angle (30–85°), and
/// a launch point inside the launch zone. The zone's ranges and half angle come from DCS's GBU-31
/// table (lookupJDAMLAR) and are kept with the target, as the jet reads them from there.</summary>
public sealed class TomcatJdam(Cartridge cartridge)
{
    public static int Stations => TomcatDefs.Int("JDAM_STATIONS");
    public static int TargetsPerStation => TomcatDefs.Int("JDAM_TARGETS_PER_STATION");
    public static IReadOnlyList<string> StationNames { get; } =
        (TomcatDefs.Defs["constants/JDAM_STATION_NAMES"] as JsonArray ?? []).Select(v => (string?)v ?? "").ToList();

    private JsonArray StationList => cartridge.Data["JDAM"]?["stations"] as JsonArray ?? [];

    public JsonObject? Target(int station, int target) =>
        StationList.ElementAtOrDefault(station - 1)?["targets"] is JsonArray t ? t.ElementAtOrDefault(target - 1) as JsonObject : null;

    public static bool IsActive(JsonObject t) => t["active"]?.GetValue<bool>() == true;

    /// <summary>Every active target: station 1–4, target 1–8.</summary>
    public IReadOnlyList<(int Station, int Target, JsonObject Data)> Active =>
        Enumerable.Range(1, Stations).SelectMany(s => Enumerable.Range(1, TargetsPerStation).Select(t => (s, t, Target(s, t))))
            .Where(x => x.Item3 is { } d && IsActive(d)).Select(x => (x.s, x.t, x.Item3!)).ToList();

    /// <summary>A target placed (or moved): its launch point stays inside the zone, or goes to its middle.</summary>
    public void Place(int station, int target, TomcatNav.At at)
    {
        if (Target(station, target) is not { } t) return;
        t["x"] = at.X;
        t["y"] = at.Y;
        t["lat"] = at.Lat;
        t["lon"] = at.Lon;
        t["elev"] = at.ElevM;
        t["active"] = true;
        FitLaunchPoint(t);
    }

    /// <summary>As onClick_JDAM_btnRemoveTarget: the target back to DCS's defaults.</summary>
    public void Clear(int station, int target)
    {
        if (StationList.ElementAtOrDefault(station - 1)?["targets"] is not JsonArray list || target < 1 || target > list.Count) return;
        var d = (JsonObject)TomcatDefs.Defs["constants/JDAM_DEFAULTS"]!.DeepClone();
        UpdateLar(d);
        list[target - 1] = d;
    }

    /// <summary>Name, attack heading (°), drop altitude (ft), drop speed (kt), impact heading / angle (null: none).</summary>
    public void Set(int station, int target, string name, double heading, double dropAltFt, double dropSpdKt, double? impactHeading, double? impactAngle)
    {
        if (Target(station, target) is not { } t) return;
        var old = HornetNav.Number(t["attack_heading"]) ?? 0;
        heading = Math.Round(Math.Clamp(heading, 0, 359.9), 1);
        t["name"] = name;
        t["attack_heading"] = heading;
        t["drop_alt"] = Math.Clamp(Math.Round(dropAltFt), 10000, 50000);
        t["drop_spd"] = Math.Clamp(Math.Round(dropSpdKt), 250, 750);
        t["has_impact_heading"] = impactHeading is not null;
        if (impactHeading is { } ih) t["impact_heading"] = Math.Clamp(Math.Round(ih), 0, 359);
        t["has_impact_angle"] = impactAngle is not null;
        if (impactAngle is { } ia) t["impact_angle"] = Math.Clamp(Math.Round(ia), 30, 85);
        UpdateLar(t);
        RotateLaunchPoint(t, old, heading);
        FitLaunchPoint(t);
    }

    /// <summary>The launch point dragged: inside the zone, else onto its nearest edge.</summary>
    public void SetLaunchPoint(int station, int target, double x, double y)
    {
        if (Target(station, target) is not { } t || !IsActive(t)) return;
        var zone = Zone(t);
        if (!Inside(x, y, zone)) (x, y) = NearestOnEdge(x, y, zone);
        t["launch_x"] = x;
        t["launch_y"] = y;
    }

    // ---- DCS's launch zone (F-14BU_DTC.lua: lookupJDAMLAR, calcLARPoints) ---------------------------

    private static JsonObject Table => (JsonObject)TomcatDefs.Defs["constants/JDAM_LAR_TABLE"]!;

    private static (int, int, double) Bracket(IReadOnlyList<double> arr, double v)
    {
        if (v <= arr[0]) return (0, 0, 0);
        if (v >= arr[^1]) return (arr.Count - 1, arr.Count - 1, 0);
        for (var i = 0; i < arr.Count - 1; i++)
            if (v >= arr[i] && v <= arr[i + 1]) return (i, i + 1, (v - arr[i]) / (arr[i + 1] - arr[i]));
        return (arr.Count - 1, arr.Count - 1, 0);
    }

    /// <summary>Rmin, Rmax (NM) and half angle (°) for a ground speed (kt) and altitude (kft).</summary>
    public static (double RminNm, double RmaxNm, double HalfAngle) Lar(double kgs, double altKft)
    {
        var altFt = altKft * 1000;
        var sound = 38.967 * Math.Sqrt(altFt < 36089 ? 288.15 - 0.001981 * altFt : 216.65);
        var mach = kgs / sound;
        var alts = Table["alts_kft"]!.AsArray().Select(v => HornetNav.Number(v) ?? 0).ToList();
        var machs = Table["machs"]!.AsArray().Select(v => HornetNav.Number(v) ?? 0).ToList();
        var data = Table["data"]!.AsArray();
        var (a1, a2, at) = Bracket(alts, altKft);
        var (m1, m2, mt) = Bracket(machs, mach);
        double Cell(int a, int m, int f) => HornetNav.Number(data[a]![m]![f]) ?? 0;
        double Field(int f)
        {
            double IMach(int a) => Cell(a, m1, f) * (1 - mt) + Cell(a, m2, f) * mt;
            return IMach(a1) * (1 - at) + IMach(a2) * at;
        }
        return (Field(0), Field(1), Field(2));
    }

    private static void UpdateLar(JsonObject t)
    {
        var (rmin, rmax, ha) = Lar(HornetNav.Number(t["drop_spd"]) ?? 450, (HornetNav.Number(t["drop_alt"]) ?? 20000) / 1000);
        t["lar_rmin_nmi"] = rmin;
        t["lar_rmax_nmi"] = rmax;
        t["lar_half_angle_deg"] = ha;
    }

    /// <summary>The zone's outline (map metres): behind the target, opposite the attack heading.</summary>
    public static List<(double X, double Y)> Zone(JsonObject t)
    {
        double x = HornetNav.Number(t["x"]) ?? 0, y = HornetNav.Number(t["y"]) ?? 0;
        var (rmin, rmax, half) = Lar(HornetNav.Number(t["drop_spd"]) ?? 450, (HornetNav.Number(t["drop_alt"]) ?? 20000) / 1000);
        double min = rmin * 1852, max = rmax * 1852;
        var center = ((HornetNav.Number(t["attack_heading"]) ?? 0) + 180) % 360 * Math.PI / 180;
        double left = center - half * Math.PI / 180, right = center + half * Math.PI / 180;
        var pts = new List<(double, double)>();
        for (var i = 0; i <= 3; i++)
        {
            var a = left + (right - left) / 3 * i;
            pts.Add((x + min * Math.Cos(a), y + min * Math.Sin(a)));
        }
        pts.Add((x + max * Math.Cos(right), y + max * Math.Sin(right)));
        for (var i = 5; i >= 0; i--)
        {
            var a = left + (right - left) / 6 * i;
            pts.Add((x + max * Math.Cos(a), y + max * Math.Sin(a)));
        }
        return pts;
    }

    /// <summary>The zone's middle (where a new launch point goes) and its far end (the heading handle).</summary>
    public static ((double X, double Y) Middle, (double X, double Y) Far) Axis(JsonObject t)
    {
        double x = HornetNav.Number(t["x"]) ?? 0, y = HornetNav.Number(t["y"]) ?? 0;
        var (rmin, rmax, _) = Lar(HornetNav.Number(t["drop_spd"]) ?? 450, (HornetNav.Number(t["drop_alt"]) ?? 20000) / 1000);
        var c = ((HornetNav.Number(t["attack_heading"]) ?? 0) + 180) % 360 * Math.PI / 180;
        var mid = (rmin + rmax) / 2 * 1852;
        return ((x + mid * Math.Cos(c), y + mid * Math.Sin(c)), (x + rmax * 1852 * Math.Cos(c), y + rmax * 1852 * Math.Sin(c)));
    }

    /// <summary>The attack heading from a point dragged to (the heading handle sits behind the target).</summary>
    public static double HeadingFrom(JsonObject t, double x, double y)
    {
        var deg = Math.Atan2(y - (HornetNav.Number(t["y"]) ?? 0), x - (HornetNav.Number(t["x"]) ?? 0)) * 180 / Math.PI;
        return Math.Floor(((deg + 180) % 360 + 360) % 360 * 10 + 0.5) / 10;
    }

    private static void RotateLaunchPoint(JsonObject t, double oldHeading, double newHeading)
    {
        if (HornetNav.Number(t["launch_x"]) is not { } lx || HornetNav.Number(t["launch_y"]) is not { } ly) return;
        double x = HornetNav.Number(t["x"]) ?? 0, y = HornetNav.Number(t["y"]) ?? 0;
        double dx = lx - x, dy = ly - y, d = (newHeading - oldHeading) * Math.PI / 180;
        t["launch_x"] = x + dx * Math.Cos(d) - dy * Math.Sin(d);
        t["launch_y"] = y + dx * Math.Sin(d) + dy * Math.Cos(d);
    }

    /// <summary>As drawJDAMLAR: a launch point outside the zone goes to its nearest edge; none: the middle.</summary>
    private static void FitLaunchPoint(JsonObject t)
    {
        if (!IsActive(t)) return;
        var zone = Zone(t);
        if (HornetNav.Number(t["launch_x"]) is { } lx && HornetNav.Number(t["launch_y"]) is { } ly)
        {
            if (Inside(lx, ly, zone)) return;
            (lx, ly) = NearestOnEdge(lx, ly, zone);
            t["launch_x"] = lx;
            t["launch_y"] = ly;
            return;
        }
        var (mid, _) = Axis(t);
        t["launch_x"] = mid.X;
        t["launch_y"] = mid.Y;
    }

    private static bool Inside(double px, double py, List<(double X, double Y)> poly)
    {
        var inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            if ((poly[i].Y > py) != (poly[j].Y > py) && px < (poly[j].X - poly[i].X) * (py - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                inside = !inside;
        return inside;
    }

    private static (double, double) NearestOnEdge(double px, double py, List<(double X, double Y)> poly)
    {
        var best = double.MaxValue;
        var (bx, by) = (px, py);
        for (var i = 0; i < poly.Count; i++)
        {
            var (a, b) = (poly[i], poly[(i + 1) % poly.Count]);
            double dx = b.X - a.X, dy = b.Y - a.Y, len = dx * dx + dy * dy;
            var k = len > 0 ? Math.Clamp(((px - a.X) * dx + (py - a.Y) * dy) / len, 0, 1) : 0;
            double cx = a.X + k * dx, cy = a.Y + k * dy, d = (px - cx) * (px - cx) + (py - cy) * (py - cy);
            if (d < best) (best, bx, by) = (d, cx, cy);
        }
        return (bx, by);
    }
}

/// <summary>The F-14B(U)'s TIS (data.TIS): the own callsign (or the mission's), the send-to list
/// (with or without the flight's wingmen); callsigns are 6 characters A–Z, 0–9 and blanks.</summary>
public sealed class TomcatTis(Cartridge cartridge)
{
    public static int CallsignLength => (int)TomcatDefs.Defs.Number("TIS_CALLSIGN_LEN");

    private JsonObject Tis => cartridge.Data["TIS"] as JsonObject ?? (JsonObject)(cartridge.Data["TIS"] = new JsonObject());

    /// <summary>As sanitizeTISCallsign: upper case, A–Z 0–9 blank, padded to 6.</summary>
    public static string Callsign(string s)
    {
        var c = new string(s.ToUpperInvariant().Where(ch => ch is >= 'A' and <= 'Z' or >= '0' and <= '9' or ' ').ToArray());
        return (c.Length > CallsignLength ? c[..CallsignLength] : c).PadRight(CallsignLength);
    }

    public bool UseMissionCallsign
    {
        get => Tis["use_mission_callsign"]?.GetValue<bool>() != false;
        set => Tis["use_mission_callsign"] = value;
    }

    public bool AddWingmen
    {
        get => Tis["add_wingmen_to_list"]?.GetValue<bool>() != false;
        set => Tis["add_wingmen_to_list"] = value;
    }

    public string OwnCallsign
    {
        get => ((string?)Tis["own_callsign"] ?? "").TrimEnd();
        set => Tis["own_callsign"] = Callsign(value);
    }

    public IReadOnlyList<string> SendTo => (Tis["send_to_callsigns"] as JsonArray ?? []).Select(v => ((string?)v ?? "").TrimEnd()).ToList();

    /// <summary>A callsign added to the send-to list (not a blank or one already there).</summary>
    public bool AddSendTo(string callsign)
    {
        var cs = Callsign(callsign);
        if (cs.Trim().Length == 0) return false;
        if (Tis["send_to_callsigns"] is not JsonArray list) Tis["send_to_callsigns"] = list = [];
        if (list.Any(v => (string?)v == cs)) return false;
        list.Add(cs);
        return true;
    }

    public void RemoveSendTo(int index)
    {
        if (Tis["send_to_callsigns"] is JsonArray list && index >= 0 && index < list.Count) list.RemoveAt(index);
    }
}
