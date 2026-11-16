using System.Text.Json.Nodes;

namespace Nebo.Client.Core.Dtc;

/// <summary>The F/A-18C's navigation section (data.WYPT) edited the way DCS's DTC manager does
/// it (CoreMods\aircraft\FA-18C\DTC\WYPT: WYPT_NAV.lua, ROUTE_SEQ.lua): waypoints STPT1–59 in DCS
/// map metres (x north, y east), altitude in metres; three route sequences of at most 15 points,
/// each point's place in a route kept as R<n> = true and R<n>_order, its leg data (speed km/h,
/// ETA, fixed time, target) in NAV_ROUTE[n]["STPT<num>"]. Works on the cartridge's JSON in place,
/// so fields this view does not know stay as they are.</summary>
public sealed class HornetNav(Cartridge cartridge)
{
    public const int MaxWaypoints = 59;
    public const int MaxRoutePoints = 15;
    public const int Routes = 3;
    public const double DefaultSpeedKmh = 463.0;   // ROUTE_SEQ.lua, a new leg
    public const double MinSpeedKmh = 70, MaxSpeedKmh = 1700;
    public const double FeetPerMeter = 3.28084;

    /// <summary>altitudeType: 1 barometric (MSL), 2 radar (AGL).</summary>
    public const int Baro = 1, Radar = 2;

    private JsonObject Wypt => Section(cartridge.Data, "WYPT");

    private JsonArray Points => Wypt["NAV_PTS"] as JsonArray ?? (JsonArray)(Wypt["NAV_PTS"] = new JsonArray());

    /// <summary>The waypoints by number.</summary>
    public IReadOnlyList<JsonObject> Waypoints =>
        Points.OfType<JsonObject>().OrderBy(Num).ToList();

    public JsonObject? Waypoint(int num) => Points.OfType<JsonObject>().FirstOrDefault(p => Num(p) == num);

    public static int Num(JsonObject p) => (int)(Number(p["wypt_num"]) ?? 0);

    /// <summary>A JSON number, whether DCS wrote it (parsed) or the editor set it (int or double).</summary>
    public static double? Number(JsonNode? n) => n is JsonValue v
        ? v.TryGetValue(out double d) ? d : v.TryGetValue(out int i) ? i : v.TryGetValue(out long l) ? l : null
        : null;

    /// <summary>A new waypoint with the first free number (DCS's defaults); null when all 59 are used.</summary>
    public JsonObject? Add(double x, double y, double altM)
    {
        var used = Points.OfType<JsonObject>().Select(Num).ToHashSet();
        var num = Enumerable.Range(1, MaxWaypoints).FirstOrDefault(n => !used.Contains(n));
        if (num == 0) return null;
        var p = new JsonObject
        {
            ["id"] = $"STPT{num}", ["idOA"] = $"OA{num}", ["idOA_Line"] = $"OA{num}Line", ["wypt_num"] = num,
            ["x"] = x, ["y"] = y, ["alt"] = altM, ["note"] = "",
            ["R1"] = false, ["R2"] = false, ["R3"] = false,
            ["altitudeType"] = Baro, ["velocityType"] = 3, ["text_note"] = "",
            ["isOA"] = false, ["OA_Range"] = 0.0, ["OA_Bearing"] = 0.0, ["OA_X"] = 0.0, ["OA_Y"] = 0.0, ["OA_Alt"] = 0.0,
            ["OA_DeltaX"] = 0.0, ["OA_DeltaY"] = 0.0, ["OA_Bearing_Units"] = 1, ["OA_Range_Units"] = 1, ["OA_Elevation_Units"] = 1,
        };
        Points.Add(p);
        return p;
    }

    public void Remove(int num)
    {
        if (Waypoint(num) is not { } p) return;
        for (var r = 1; r <= Routes; r++) RemoveFromRoute(r, num);
        Points.Remove(p);
    }

    public void Move(int num, double x, double y)
    {
        if (Waypoint(num) is not { } p) return;
        p["x"] = x;
        p["y"] = y;
    }

    /// <summary>Altitude in metres and its type (Baro / Radar), on the point and its route legs.</summary>
    public void SetAltitude(int num, double altM, int altitudeType)
    {
        if (Waypoint(num) is not { } p) return;
        p["alt"] = altM;
        p["altitudeType"] = altitudeType;
        for (var r = 1; r <= Routes; r++)
            if (Leg(r, num) is { } leg)
            {
                leg["alt"] = altM;
                leg["altitudeType"] = altitudeType;
            }
    }

    /// <summary>The 5-letter label shown beside the waypoint (text_note) and the longer note.</summary>
    public void SetText(int num, string label, string note)
    {
        if (Waypoint(num) is not { } p) return;
        p["text_note"] = label.Length > 5 ? label[..5] : label;
        p["note"] = note;
    }

    // ---- Routes ------------------------------------------------------------------------------

    /// <summary>The waypoint numbers of route 1–3 in their order.</summary>
    public IReadOnlyList<int> Route(int route) =>
        Points.OfType<JsonObject>()
            .Where(p => p[$"R{route}"]?.GetValue<bool>() == true)
            .OrderBy(p => Number(p[$"R{route}_order"]) ?? 999)
            .Select(Num).ToList();

    /// <summary>Appends a waypoint to a route (false when the route has 15 points or it is in it).</summary>
    public bool AddToRoute(int route, int num)
    {
        if (Waypoint(num) is not { } p || p[$"R{route}"]?.GetValue<bool>() == true) return false;
        var members = Points.OfType<JsonObject>().Where(q => q[$"R{route}"]?.GetValue<bool>() == true).ToList();
        if (members.Count >= MaxRoutePoints) return false;
        var order = members.Select(q => Number(q[$"R{route}_order"]) ?? 0).DefaultIfEmpty(0).Max();
        p[$"R{route}"] = true;
        p[$"R{route}_order"] = (int)order + 1;
        Legs(route)[$"STPT{num}"] = new JsonObject
        {
            ["route_num"] = route, ["wypt_num"] = num,
            ["alt"] = Number(p["alt"]) ?? 0, ["altitudeType"] = (int)(Number(p["altitudeType"]) ?? Baro),
            ["speed"] = DefaultSpeedKmh, ["ETA"] = 0, ["FIX_Time"] = false, ["TGT"] = false,
        };
        return true;
    }

    public void RemoveFromRoute(int route, int num)
    {
        if (Waypoint(num) is not { } p) return;
        p[$"R{route}"] = false;
        p.Remove($"R{route}_order");
        Legs(route).Remove($"STPT{num}");
    }

    /// <summary>Puts a route in a new order (the numbers must be the route's own).</summary>
    public void Reorder(int route, IReadOnlyList<int> nums)
    {
        for (var i = 0; i < nums.Count; i++)
            if (Waypoint(nums[i]) is { } p && p[$"R{route}"]?.GetValue<bool>() == true) p[$"R{route}_order"] = i + 1;
    }

    /// <summary>Makes a route of these waypoints in this order (the first 15), replacing what it
    /// held; returns how many went in.</summary>
    public int BuildRoute(int route, IReadOnlyList<int> nums)
    {
        foreach (var n in Route(route)) RemoveFromRoute(route, n);
        return nums.Count(n => AddToRoute(route, n));
    }

    /// <summary>Gives the waypoints the numbers 1, 2, 3… in this order (the list's queue): their
    /// ids follow, and the routes keep their points and order.</summary>
    public void Renumber(IReadOnlyList<int> order)
    {
        var map = order.Select((old, i) => (old, @new: i + 1)).ToDictionary(t => t.old, t => t.@new);
        var next = map.Count;
        foreach (var p in Waypoints)  // waypoints the order left out go after it
            if (!map.ContainsKey(Num(p))) map[Num(p)] = ++next;
        // Route legs are keyed by STPT<num>: rename them all at once (old and new numbers overlap).
        for (var r = 1; r <= Routes; r++)
        {
            var legs = Legs(r);
            var renamed = legs.Select(kv => (kv.Key, Node: kv.Value)).ToList();
            foreach (var (key, _) in renamed) legs.Remove(key);
            foreach (var (key, node) in renamed)
            {
                if (node is not JsonObject leg || !int.TryParse(key.AsSpan(4), out var old) || !map.TryGetValue(old, out var n)) continue;
                leg["wypt_num"] = n;
                legs[$"STPT{n}"] = leg;
            }
        }
        foreach (var p in Points.OfType<JsonObject>())
        {
            var n = map[Num(p)];
            p["wypt_num"] = n;
            p["id"] = $"STPT{n}";
            p["idOA"] = $"OA{n}";
            p["idOA_Line"] = $"OA{n}Line";
        }
    }

    /// <summary>The length of each leg of a route in metres: [i] is from point i to point i+1.</summary>
    public IReadOnlyList<double> LegLengths(int route)
    {
        var pts = Route(route).Select(Waypoint).OfType<JsonObject>().ToList();
        var lengths = new List<double>();
        for (var i = 1; i < pts.Count; i++)
        {
            var dx = (Number(pts[i]["x"]) ?? 0) - (Number(pts[i - 1]["x"]) ?? 0);
            var dy = (Number(pts[i]["y"]) ?? 0) - (Number(pts[i - 1]["y"]) ?? 0);
            lengths.Add(Math.Sqrt(dx * dx + dy * dy));
        }
        return lengths;
    }

    /// <summary>A waypoint's leg in a route: speed (km/h), ETA (s), FIX_Time, TGT.</summary>
    public JsonObject? Leg(int route, int num) => Legs(route)[$"STPT{num}"] as JsonObject;

    public void SetSpeed(int route, int num, double kmh)
    {
        if (Leg(route, num) is { } leg) leg["speed"] = Math.Clamp(kmh, MinSpeedKmh, MaxSpeedKmh);
    }

    private JsonObject Legs(int route)
    {
        var routes = Wypt["NAV_ROUTE"] as JsonArray;
        if (routes is null) Wypt["NAV_ROUTE"] = routes = [];
        while (routes.Count < Routes) routes.Add(new JsonObject());
        // DCS writes an empty route as [] (an empty Lua table): it becomes an object once used.
        if (routes[route - 1] is not JsonObject legs) routes[route - 1] = legs = new JsonObject();
        return legs;
    }

    // ---- NAV settings (TACAN, ICLS, ACLS, home waypoint, warnings) ----------------------------

    public JsonObject Settings => Section(Wypt, "NAV_SETTINGS");

    /// <summary>TACAN: mode 1 T/R, 2 REC, 3 A/A; band 1 X, 2 Y (NAV_SETTINGS_defs).</summary>
    public void SetTacan(int channel, char band, int mode, bool on)
    {
        var t = Section(Settings, "TACAN");
        t["Channel"] = channel;
        t["ChannelMode"] = char.ToUpperInvariant(band) == 'Y' ? 2 : 1;
        t["Mode"] = mode;
        t["OnOff"] = on;
    }

    public void SetIcls(int channel, bool on)
    {
        var i = Section(Settings, "ICLS");
        i["Channel"] = channel;
        i["OnOff"] = on;
    }

    private static JsonObject Section(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject o) return o;
        var created = new JsonObject();
        parent[key] = created;
        return created;
    }
}
