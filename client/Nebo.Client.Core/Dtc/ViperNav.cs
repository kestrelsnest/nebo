using System.Text.Json.Nodes;

namespace Nebo.Client.Core.Dtc;

/// <summary>The F-16C's steerpoints (data.MPD.NAV_PTS) edited the way DCS's DTC manager does it
/// (CoreMods\aircraft\F-16C\DTC\MPD: NAV_PTS.lua, NAV_Routes.lua): at most 25, numbered by their
/// place in the list (STPT1…; a delete renumbers the rest), DCS map metres (x north, y east),
/// elevation and route altitude in metres, speed km/h. Routes 1–3 are the R1/R2/R3 flags: a route
/// flies its points in steerpoint order. Each point has two offset aimpoints (OAP_1 / OAP_2).
/// Editing clears mirror_NAV_PTS: with it set, DCS takes the mission's waypoints instead.</summary>
public sealed class ViperNav(Cartridge cartridge)
{
    public const int MaxPoints = 25;
    public const int Routes = 3;
    public const double DefaultRouteAltM = 2000, DefaultSpeedKmh = 790;   // NAV_PTS.lua, a new point
    /// <summary>Steerpoint types (NAV_PTS_Types).</summary>
    public static readonly string[] Types = ["STPT", "IP", "TGT"];
    /// <summary>altitudeType: 1 MSL, 2 AGL; velocityType: 1 CAS, 2 IAS, 3 GS, 4 TAS (NAV_Routes.lua).</summary>
    public const int Msl = 1, Agl = 2;
    public const double MaxOapRangeM = 99999 / 3.280839895;

    private JsonObject Mpd => cartridge.Data["MPD"] as JsonObject ?? (JsonObject)(cartridge.Data["MPD"] = new JsonObject());

    private JsonArray List => Mpd["NAV_PTS"] as JsonArray ?? (JsonArray)(Mpd["NAV_PTS"] = new JsonArray());

    /// <summary>DCS takes the mission's waypoints (and routes) instead of these.</summary>
    public bool Mirror
    {
        get => Mpd["mirror_NAV_PTS"]?.GetValue<bool>() == true;
        set => Mpd["mirror_NAV_PTS"] = value;
    }

    /// <summary>The steerpoints in order (point n is [n − 1]).</summary>
    public IReadOnlyList<JsonObject> Points => List.OfType<JsonObject>().ToList();

    public JsonObject? Point(int num) => num >= 1 && num <= List.Count ? List[num - 1] as JsonObject : null;

    /// <summary>A new steerpoint at the end, with DCS's defaults; null when 25 are there.</summary>
    public JsonObject? Add(double x, double y, double elevM) => Insert(int.MaxValue, x, y, elevM);

    /// <summary>A new steerpoint at a place in the list (1-based, clamped); the ones after it move up.</summary>
    public JsonObject? Insert(int num, double x, double y, double elevM)
    {
        if (List.Count >= MaxPoints) return null;
        var p = new JsonObject
        {
            ["x"] = x, ["y"] = y, ["alt"] = elevM, ["routeAltitude"] = DefaultRouteAltM, ["speed"] = DefaultSpeedKmh, ["note"] = "",
            ["R1"] = false, ["R2"] = false, ["R3"] = false, ["TOS"] = -1, ["isTOSEnabled"] = false, ["FIX_Time"] = false,
            ["altitudeType"] = Msl, ["velocityType"] = 3, ["type"] = Types[0],
            ["isOAP_1"] = false, ["isOAP_2"] = false,
        };
        foreach (var n in new[] { 1, 2 })
            foreach (var f in new[] { "Range", "Bearing", "X", "Y", "Alt", "DeltaX", "DeltaY" })
                p[$"OAP_{n}_{f}"] = 0.0;
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
        FixVipVrp(num);
        Mirror = false;
    }

    /// <summary>Removes several at once (numbers as they are now).</summary>
    public void RemoveMany(IEnumerable<int> nums)
    {
        foreach (var n in nums.Distinct().OrderDescending()) Remove(n);
    }

    public void Move(int num, double x, double y)
    {
        if (Point(num) is not { } p) return;
        // Its offset aimpoints, and the VIP / VRP legs from it, keep their offsets (calculateVIPVRP).
        foreach (var n in new[] { 1, 2 })
            if (HasOap(p, n)) ViperOffset.Follow(p, $"OAP_{n}", x, y);
        var vip = new ViperVipVrp(cartridge);
        foreach (var leg in ViperVipVrp.Legs)
            if (vip.SteerpointOf(leg) == num) ViperOffset.Follow(vip.Data, leg, x, y);
        p["x"] = x;
        p["y"] = y;
        Mirror = false;
    }

    /// <summary>Puts the steerpoints in a new order (old numbers, every one once; others go after).</summary>
    public void Reorder(IReadOnlyList<int> order)
    {
        var pts = Points;
        var moved = order.Where(n => n >= 1 && n <= pts.Count).Distinct().Select(n => pts[n - 1]).ToList();
        moved.AddRange(pts.Where(p => !moved.Contains(p)));
        List.Clear();
        foreach (var p in moved) List.Add(p);
        Renumber();
        Mirror = false;
    }

    /// <summary>number, id and the map ids follow the place in the list (as a DCS delete does).</summary>
    private void Renumber()
    {
        var i = 0;
        foreach (var p in List.OfType<JsonObject>())
        {
            i++;
            p["number"] = i;
            p["id"] = $"STPT{i}";
            p["idOA1"] = $"OA1{i}";
            p["idOA2"] = $"OA2{i}";
            p["idOA1_Line"] = $"OA1{i}Line";
            p["idOA2_Line"] = $"OA2{i}Line";
        }
    }

    /// <summary>The VIP / VRP steerpoint numbers after a delete (NAV_PTS.lua onChange_delButton).</summary>
    private void FixVipVrp(int deleted)
    {
        if (Mpd["VIPVRP"] is not JsonObject v) return;
        foreach (var key in new[] { "VIP_Number", "VRP_Number" })
        {
            var n = (int)(HornetNav.Number(v[key]) ?? 0);
            if (n > List.Count) v[key] = List.Count;
            else if (deleted < n) v[key] = n - 1;
        }
    }

    /// <summary>Elevation (m), route altitude (m) and its type, speed (km/h) and its type, steerpoint type, note.</summary>
    public void Set(int num, double elevM, double routeAltM, int altitudeType, double speedKmh, int velocityType, string type, string note)
    {
        if (Point(num) is not { } p) return;
        p["alt"] = elevM;
        p["routeAltitude"] = routeAltM;
        p["altitudeType"] = altitudeType == Agl ? Agl : Msl;
        p["speed"] = Math.Max(0, speedKmh);
        p["velocityType"] = Math.Clamp(velocityType, 1, 4);
        p["type"] = Types.Contains(type) ? type : Types[0];
        p["note"] = note;
        Mirror = false;
    }

    /// <summary>Time over steerpoint in seconds of the mission day (null: none).</summary>
    public void SetTos(int num, double? seconds, bool fixTime)
    {
        if (Point(num) is not { } p) return;
        p["TOS"] = seconds is { } s ? Math.Max(0, s) : -1;
        p["isTOSEnabled"] = seconds is not null;
        p["FIX_Time"] = seconds is not null && fixTime;
        Mirror = false;
    }

    // ---- Routes -----------------------------------------------------------------------------------

    public static bool InRoute(JsonObject p, int route) => p[$"R{route}"]?.GetValue<bool>() == true;

    /// <summary>The steerpoint numbers of route 1–3, in the order they are flown.</summary>
    public IReadOnlyList<int> Route(int route) =>
        Points.Select((p, i) => (p, n: i + 1)).Where(t => InRoute(t.p, route)).Select(t => t.n).ToList();

    public void SetInRoute(int route, int num, bool on)
    {
        if (Point(num) is not { } p || route is < 1 or > Routes) return;
        p[$"R{route}"] = on;
        Mirror = false;
    }

    /// <summary>The length of each leg of a route in metres: [i] is from its point i to point i+1.</summary>
    public IReadOnlyList<double> LegLengths(int route)
    {
        var pts = Route(route).Select(Point).OfType<JsonObject>().ToList();
        return pts.Zip(pts.Skip(1), (a, b) => Distance(a, "x", "y", b, "x", "y")).ToList();
    }

    // ---- Offset aimpoints ---------------------------------------------------------------------------

    /// <summary>Places offset aimpoint 1 or 2 of a steerpoint; range (m) and bearing (rad, true) follow.</summary>
    public void SetOap(int num, int oap, double x, double y, double elevM)
    {
        if (Point(num) is not { } p || oap is not (1 or 2)) return;
        // The dialog's limit: on the same bearing at 99,999 ft.
        ViperOffset.Place(p, $"OAP_{oap}", HornetNav.Number(p["x"]) ?? 0, HornetNav.Number(p["y"]) ?? 0, x, y, MaxOapRangeM);
        p[$"isOAP_{oap}"] = true;
        p[$"OAP_{oap}_Alt"] = Math.Floor(elevM);
        Mirror = false;
    }

    public void ClearOap(int num, int oap)
    {
        if (Point(num) is not { } p || oap is not (1 or 2)) return;
        p[$"isOAP_{oap}"] = false;
        foreach (var f in new[] { "Range", "Bearing", "X", "Y", "Alt", "DeltaX", "DeltaY" })
            p[$"OAP_{oap}_{f}"] = 0.0;
        Mirror = false;
    }

    public static bool HasOap(JsonObject p, int oap) => p[$"isOAP_{oap}"]?.GetValue<bool>() == true;

    private static double Distance(JsonObject a, string ax, string ay, JsonObject b, string bx, string by)
    {
        var dx = (HornetNav.Number(b[bx]) ?? 0) - (HornetNav.Number(a[ax]) ?? 0);
        var dy = (HornetNav.Number(b[by]) ?? 0) - (HornetNav.Number(a[ay]) ?? 0);
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
