using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Nebo.Client.Core.Dtc;

/// <summary>A navigation point of the shared plan: map metres (x north, y east), ground
/// elevation (m), a name, and the routes 1–3 it is in (bit r−1).</summary>
public sealed record PlanNavPoint(double X, double Y, double ElevM, string Name, int Routes);

/// <summary>A free point (F/A-18C GPS waypoint, F-16C DEST, F-14 additional point).</summary>
public sealed record PlanPoint(double X, double Y, double ElevM, string Name);

/// <summary>A line: its points, closed (an area) or not, and the kind it came as (CORR, FAOR,
/// FLOT, GEO, LINE), so a jet that has kinds puts it back as one.</summary>
public sealed record PlanLine(IReadOnlyList<PlanPoint> Points, bool Closed, string Kind);

/// <summary>A threat ring: centre, radius (m), the threat type's name, its short text.</summary>
public sealed record PlanThreat(double X, double Y, double RadiusM, string Type, string Text);

/// <summary>What one mission plan puts into every jet's cartridge: the route, the free points,
/// the lines and the threat rings, read from one jet's cartridge (<see cref="From"/>) and written
/// into another's (<see cref="ApplyTo"/>). The jets keep their own settings (CMDS, RWR, COLR, …)
/// and their points' own details (altitudes, route legs, offset aimpoints, speeds): a point is
/// updated in place, by its place in the list. F/A-18C: waypoints with routes 1–3, GPS waypoints,
/// SA corridors / FAOR / FLOT and threat rings. F-16C: steerpoints with R1–R3, DEST, geographic
/// lines 1–4, preplanned threats. F-14B(U): flight plan 2 (the route: route 1's points, or all
/// when no point is in a route), its lines and additional points; it has no threat rings.</summary>
public sealed record DtcPlan(IReadOnlyList<PlanNavPoint> Nav, IReadOnlyList<PlanPoint> Points, IReadOnlyList<PlanLine> Lines, IReadOnlyList<PlanThreat> Threats)
{
    /// <summary>The F-14 flight plan the shared route goes into.</summary>
    public const int TomcatPlan = TomcatNav.FirstOwnPlan;

    private static double N(JsonObject o, string key, double fallback = 0) => HornetNav.Number(o[key]) ?? fallback;
    private static string S(JsonObject o, string key) => (string?)o[key] ?? "";

    /// <summary>Equal plans have equal keys (the session compares them to see what changed).</summary>
    public string Key
    {
        get
        {
            var sb = new StringBuilder();
            void P(double x, double y) => sb.Append(x.ToString("R", CultureInfo.InvariantCulture)).Append(',').Append(y.ToString("R", CultureInfo.InvariantCulture)).Append(';');
            foreach (var p in Nav) { P(p.X, p.Y); sb.Append(p.Name).Append(p.Routes).Append('|'); }
            sb.Append('#');
            foreach (var p in Points) { P(p.X, p.Y); sb.Append(p.Name).Append('|'); }
            sb.Append('#');
            foreach (var l in Lines) { sb.Append(l.Kind).Append(l.Closed); foreach (var p in l.Points) P(p.X, p.Y); sb.Append('|'); }
            sb.Append('#');
            foreach (var t in Threats) { P(t.X, t.Y); sb.Append(t.RadiusM.ToString("R", CultureInfo.InvariantCulture)).Append(t.Type).Append(t.Text).Append('|'); }
            return sb.ToString();
        }
    }

    // ---- Reading a cartridge --------------------------------------------------------------------------

    public static DtcPlan From(Cartridge c) => c.Type switch
    {
        Cartridge.ViperType => FromViper(c),
        Cartridge.TomcatType => FromTomcat(c),
        _ => FromHornet(c),
    };

    private static int Routes(JsonObject p) => Enumerable.Range(1, 3).Where(r => p[$"R{r}"]?.GetValue<bool>() == true).Sum(r => 1 << (r - 1));

    private static DtcPlan FromHornet(Cartridge c)
    {
        var nav = new HornetNav(c);
        var sa = new HornetSa(c);
        var lines = new List<PlanLine>();
        foreach (var kind in new[] { HornetSa.Corridor, HornetSa.Faor, HornetSa.Flot })
            foreach (var l in sa.LinesOf(kind))
                lines.Add(new PlanLine(HornetSa.PointsOf(l).Select(p => new PlanPoint(N(p, "x"), N(p, "y"), 0, "")).ToList(), kind == HornetSa.Faor, kind.Prefix));
        return new DtcPlan(
            nav.Waypoints.Select(p => new PlanNavPoint(N(p, "x"), N(p, "y"), N(p, "alt"), S(p, "text_note"), Routes(p))).ToList(),
            new HornetGps(c).All.Select(p => new PlanPoint(N(p, "x"), N(p, "y"), N(p, "alt"), S(p, "text"))).ToList(),
            lines,
            sa.Threats.Select(t => new PlanThreat(N(t, "x"), N(t, "y"), N(t, "threat_ring_radius") * 1852, S(t, "threat_type"), S(t, "text"))).ToList());
    }

    private static DtcPlan FromViper(Cartridge c)
    {
        var geo = new ViperGeo(c);
        // An area is written with its first point repeated last (ToViper): read it back closed.
        static PlanLine Line(List<PlanPoint> pts) =>
            pts.Count > 3 && pts[0].X == pts[^1].X && pts[0].Y == pts[^1].Y ? new PlanLine(pts[..^1], true, "GEO") : new PlanLine(pts, false, "GEO");
        var lines = Enumerable.Range(1, ViperGeo.Lines).Select(geo.Line).Where(l => l.Count > 0)
            .Select(l => Line(l.Select(n => geo.Point(n)!).Select(p => new PlanPoint(N(p, "x"), N(p, "y"), N(p, "alt"), "")).ToList())).ToList();
        return new DtcPlan(
            new ViperNav(c).Points.Select(p => new PlanNavPoint(N(p, "x"), N(p, "y"), N(p, "alt"), S(p, "note"), Routes(p))).ToList(),
            new ViperDest(c).Points.Select(p => new PlanPoint(N(p, "x"), N(p, "y"), N(p, "alt"), S(p, "text"))).ToList(),
            lines,
            new ViperThreats(c).Points.Select(t => new PlanThreat(N(t, "x"), N(t, "y"), N(t, "radius"), S(t, "threatName"), S(t, "text"))).ToList());
    }

    private static DtcPlan FromTomcat(Cartridge c)
    {
        var nav = new TomcatNav(c);
        const double ft = 3.28084;
        return new DtcPlan(
            nav.Waypoints(TomcatPlan).Select(p => new PlanNavPoint(N(p, "x"), N(p, "y"), N(p, "elev") / ft, S(p, "name"), 1)).ToList(),
            nav.Extra(TomcatPlan).Select(p => new PlanPoint(N(p, "x"), N(p, "y"), N(p, "elev") / ft, S(p, "name"))).ToList(),
            nav.Lines(TomcatPlan).Select(l => new PlanLine(TomcatNav.PointsOf(l).Select(p => new PlanPoint(N(p, "x"), N(p, "y"), N(p, "elev") / ft, "")).ToList(),
                TomcatNav.IsClosed(l), "LINE")).ToList(),
            []);
    }

    // ---- Writing into a cartridge -------------------------------------------------------------------

    /// <summary>Puts the plan into a cartridge. <paramref name="latLon"/> gives lat / lon of a map
    /// point (the F-14 keeps both), <paramref name="ground"/> the ground's elevation (m) for new or
    /// moved points. Returns what did not fit ("F-16C: 25 of 31 steerpoints").</summary>
    public IReadOnlyList<string> ApplyTo(Cartridge c, Func<double, double, (double Lat, double Lon)> latLon, Func<double, double, double> ground)
    {
        var cut = new List<string>();
        var now = From(c);
        switch (c.Type)
        {
            case Cartridge.ViperType: ToViper(c, now, ground, cut); break;
            case Cartridge.TomcatType: ToTomcat(c, now, latLon, ground, cut); break;
            default: ToHornet(c, now, ground, cut); break;
        }
        return cut;
    }

    private static bool Same<T>(IReadOnlyList<T> a, IReadOnlyList<T> b, Func<T, string> key) => a.Count == b.Count && a.Select(key).SequenceEqual(b.Select(key));
    private static string K(PlanNavPoint p) => $"{p.X:R},{p.Y:R},{p.Name},{p.Routes}";
    private static string K(PlanPoint p) => $"{p.X:R},{p.Y:R},{p.Name}";
    private static string K(PlanLine l) => $"{l.Kind}{l.Closed}" + string.Concat(l.Points.Select(p => $"{p.X:R},{p.Y:R};"));
    private static string K(PlanThreat t) => $"{t.X:R},{t.Y:R},{t.RadiusM:R},{t.Type},{t.Text}";

    private static void Note(List<string> cut, string jet, int kept, int all, string what)
    {
        if (kept < all) cut.Add($"{jet}: {kept} of {all} {what}");
    }

    private static string Cap(string s, int n) => s.Length > n ? s[..n] : s;

    /// <summary>A position changed (the stored one is not where the plan has it).</summary>
    private static bool Moved(JsonObject p, double x, double y) => N(p, "x") != x || N(p, "y") != y;

    private static JsonObject? TypeFor(IReadOnlyList<JsonObject> types, string name) =>
        types.FirstOrDefault(t => (string?)t["name"] == name) ?? HornetSa.RingTypeFor(types, name);

    // ---- F/A-18C --------------------------------------------------------------------------------------

    private void ToHornet(Cartridge c, DtcPlan now, Func<double, double, double> ground, List<string> cut)
    {
        const string jet = "F/A-18C";
        if (!Same(Nav, now.Nav, K))
        {
            var nav = new HornetNav(c);
            var want = Nav.Take(HornetNav.MaxWaypoints).ToList();
            Note(cut, jet, want.Count, Nav.Count, "waypoints");
            var have = nav.Waypoints.Select(HornetNav.Num).ToList();
            // Numbers 1…n first: a new waypoint takes the first free number, which must come last.
            if (!have.SequenceEqual(Enumerable.Range(1, have.Count)))
            {
                nav.Renumber(have);
                have = nav.Waypoints.Select(HornetNav.Num).ToList();
            }
            foreach (var n in have.Skip(want.Count)) nav.Remove(n);
            for (var i = 0; i < want.Count; i++)
            {
                var w = want[i];
                int num;
                if (i < have.Count)
                {
                    num = have[i];
                    if (Moved(nav.Waypoint(num)!, w.X, w.Y)) nav.Move(num, w.X, w.Y);
                }
                else num = HornetNav.Num(nav.Add(w.X, w.Y, ground(w.X, w.Y))!);
                nav.SetText(num, w.Name, (string?)nav.Waypoint(num)!["note"] ?? "");
            }
            // Routes: their points in list order (a route keeps its legs when it does not change).
            var nums = nav.Waypoints.Select(HornetNav.Num).ToList();
            for (var r = 1; r <= HornetNav.Routes; r++)
            {
                var members = want.Select((w, i) => (w, num: nums[i])).Where(t => (t.w.Routes & (1 << (r - 1))) != 0).Select(t => t.num).ToList();
                if (!nav.Route(r).SequenceEqual(members.Take(HornetNav.MaxRoutePoints)))
                {
                    var put = nav.BuildRoute(r, members);
                    Note(cut, jet, put, members.Count, $"points of route {r}");
                }
            }
        }
        if (!Same(Points, now.Points, K))
        {
            var gps = new HornetGps(c);
            var want = Points.Take(HornetGps.MaxPoints).ToList();
            Note(cut, jet, want.Count, Points.Count, "GPS waypoints");
            var have = gps.All.Select(HornetNav.Num).ToList();
            foreach (var n in have.Skip(want.Count)) gps.Remove(n);
            for (var i = 0; i < want.Count; i++)
            {
                var w = want[i];
                var num = i < have.Count ? have[i] : HornetNav.Num(gps.Add(w.X, w.Y, ground(w.X, w.Y))!);
                var p = gps.Point(num)!;
                if (Moved(p, w.X, w.Y)) { gps.Move(num, w.X, w.Y); p["alt"] = ground(w.X, w.Y); }
                if (w.Name.Length > 0) gps.Set(num, w.Name, (string?)p["note"] ?? "", N(p, "alt") * HornetNav.FeetPerMeter);
            }
        }
        if (!Same(Lines, now.Lines, K))
        {
            var sa = new HornetSa(c);
            // A line from a jet without kinds (F-16C, F-14) keeps the kind of the Hornet's line in
            // its place when both are open or both closed (a corridor stays a corridor); else an
            // area becomes a FAOR and an open line a FLOT.
            HornetSa.LineKind KindOf(PlanLine l, int i) => l.Kind switch
            {
                "CORR" => HornetSa.Corridor, "FAOR" => HornetSa.Faor, "FLOT" => HornetSa.Flot,
                _ when i < now.Lines.Count && now.Lines[i].Closed == l.Closed => KindOf(now.Lines[i], int.MaxValue),
                _ => l.Closed ? HornetSa.Faor : HornetSa.Flot,
            };
            var kinds = Lines.Select(KindOf).ToList();
            foreach (var kind in new[] { HornetSa.Corridor, HornetSa.Faor, HornetSa.Flot })
            {
                var want = Lines.Where((l, i) => kinds[i] == kind).ToList();
                var kept = want.Take(kind.Max).ToList();
                Note(cut, jet, kept.Count, want.Count, $"{kind.Prefix} lines");
                while (sa.LinesOf(kind).Count > kept.Count) sa.RemoveLine(kind, sa.LinesOf(kind).Count - 1);
                for (var i = 0; i < kept.Count; i++)
                {
                    var pts = kept[i].Points.Take(kind.MaxPoints).ToList();
                    Note(cut, jet, pts.Count, kept[i].Points.Count, $"points of {kind.Prefix} {i + 1}");
                    if (pts.Count == 0) continue;
                    if (i >= sa.LinesOf(kind).Count) sa.AddLine(kind, pts[0].X, pts[0].Y);
                    var line = sa.LinesOf(kind)[i];
                    while (HornetSa.PointsOf(line).Count > pts.Count) sa.RemovePoint(kind, i, HornetSa.PointsOf(line).Count - 1);
                    for (var k = 0; k < pts.Count; k++)
                        if (k < HornetSa.PointsOf(line).Count) sa.MovePoint(kind, i, k, pts[k].X, pts[k].Y);
                        else sa.AddPoint(kind, i, pts[k].X, pts[k].Y);
                }
            }
        }
        if (!Same(Threats, now.Threats, K))
        {
            var sa = new HornetSa(c);
            var types = DtcDefs.For(Cartridge.HornetType)["MEZ_THRTS_defs/MEZ_THRTS_defs"]!.AsArray().OfType<JsonObject>().ToList();
            var custom = types.FirstOrDefault(t => (string?)t["name"] == "Custom") ?? types[0];
            var want = Threats.Take(HornetSa.MaxThreats).ToList();
            Note(cut, jet, want.Count, Threats.Count, "threat rings");
            while (sa.Threats.Count > want.Count) sa.RemoveThreat(sa.Threats.Count - 1);
            for (var i = 0; i < want.Count; i++)
            {
                var t = want[i];
                var type = TypeFor(types, t.Type) ?? custom;
                if (i >= sa.Threats.Count) sa.AddThreat(t.X, t.Y, type);
                else sa.MoveThreat(i, t.X, t.Y);
                sa.SetThreat(i, type, t.Text, t.RadiusM / 1852);
            }
        }
    }

    // ---- F-16C ----------------------------------------------------------------------------------------

    private void ToViper(Cartridge c, DtcPlan now, Func<double, double, double> ground, List<string> cut)
    {
        const string jet = "F-16C";
        if (!Same(Nav, now.Nav, K))
        {
            var nav = new ViperNav(c);
            var want = Nav.Take(ViperNav.MaxPoints).ToList();
            Note(cut, jet, want.Count, Nav.Count, "steerpoints");
            while (nav.Points.Count > want.Count) nav.Remove(nav.Points.Count);
            for (var i = 0; i < want.Count; i++)
            {
                var w = want[i];
                var num = i + 1;
                if (num > nav.Points.Count) nav.Add(w.X, w.Y, ground(w.X, w.Y));
                var p = nav.Point(num)!;
                if (Moved(p, w.X, w.Y)) { nav.Move(num, w.X, w.Y); p["alt"] = ground(w.X, w.Y); }
                p["note"] = w.Name;
                for (var r = 1; r <= ViperNav.Routes; r++) nav.SetInRoute(r, num, (w.Routes & (1 << (r - 1))) != 0);
            }
        }
        if (!Same(Points, now.Points, K))
        {
            var dest = new ViperDest(c);
            var want = Points.Take(dest.Max).ToList();
            Note(cut, jet, want.Count, Points.Count, "DEST points");
            while (dest.Points.Count > want.Count) dest.Remove(dest.Points.Count);
            for (var i = 0; i < want.Count; i++)
            {
                var w = want[i];
                var num = i + 1;
                if (num > dest.Points.Count) dest.Add(w.X, w.Y, ground(w.X, w.Y));
                var p = dest.Point(num)!;
                if (Moved(p, w.X, w.Y)) { dest.Move(num, w.X, w.Y); p["alt"] = ground(w.X, w.Y); }
                if (w.Name.Length > 0) p["text"] = ViperList.Code(w.Name, 3);
            }
        }
        if (!Same(Lines, now.Lines, K))
        {
            // Lines 1–4 from the first four; an area repeats its first point to close it.
            var geo = new ViperGeo(c);
            while (geo.Points.Count > 0) geo.Remove(geo.Points.Count);
            var lines = Lines.Take(ViperGeo.Lines).ToList();
            Note(cut, jet, lines.Count, Lines.Count, "lines");
            var placed = 0;
            var wanted = 0;
            for (var l = 0; l < lines.Count; l++)
            {
                var pts = lines[l].Points.ToList();
                if (lines[l].Closed && pts.Count > 2) pts.Add(pts[0]);
                wanted += pts.Count;
                foreach (var p in pts)
                    if (geo.Insert(int.MaxValue, l + 1, p.X, p.Y, ground(p.X, p.Y)) is not null) placed++;
            }
            Note(cut, jet, placed, wanted, "line points");
        }
        if (!Same(Threats, now.Threats, K))
        {
            var threats = new ViperThreats(c);
            var types = ViperThreats.Types;
            var want = Threats.Take(threats.Max).ToList();
            Note(cut, jet, want.Count, Threats.Count, "threats");
            while (threats.Points.Count > want.Count) threats.Remove(threats.Points.Count);
            for (var i = 0; i < want.Count; i++)
            {
                var t = want[i];
                var num = i + 1;
                var type = TypeFor(types, t.Type) is { } match ? types.ToList().IndexOf(match) + 1 : 1;
                if (num > threats.Points.Count) threats.Add(t.X, t.Y, ground(t.X, t.Y), type);
                else
                {
                    threats.Move(num, t.X, t.Y);
                    if ((int)N(threats.Point(num)!, "def_num", 1) != type) threats.SetType(num, type);
                }
                var p = threats.Point(num)!;
                threats.Set(num, t.Text.Length > 0 ? t.Text : (string?)p["text"] ?? "", p["ring"]?.GetValue<bool>() != false, t.RadiusM, N(p, "alt"));
            }
        }
    }

    // ---- F-14B(U) ---------------------------------------------------------------------------------------

    private void ToTomcat(Cartridge c, DtcPlan now, Func<double, double, (double Lat, double Lon)> latLon, Func<double, double, double> ground, List<string> cut)
    {
        const string jet = "F-14";
        const int plan = TomcatPlan;
        var nav = new TomcatNav(c);
        TomcatNav.At At(double x, double y)
        {
            var (lat, lon) = latLon(x, y);
            return new TomcatNav.At(x, y, lat, lon, ground(x, y));
        }
        // The route: route 1's points, or every point when none is in a route.
        var route = Nav.Any(p => p.Routes != 0) ? Nav.Where(p => (p.Routes & 1) != 0).ToList() : Nav.ToList();
        var nowRoute = now.Nav;
        if (!Same(route, nowRoute, p => $"{p.X:R},{p.Y:R},{p.Name}") || !Same(Lines, now.Lines, K) || !Same(Points, now.Points, K))
        {
            // One budget of 50 points for the plan: the route first, then the lines, then the free points.
            var budget = TomcatNav.MaxPoints;
            var wps = route.Take(budget).ToList();
            Note(cut, jet, wps.Count, route.Count, $"waypoints (plan {plan})");
            budget -= wps.Count;
            var have = nav.Waypoints(plan).Count;
            for (var n = have; n > wps.Count; n--) nav.RemoveWaypoint(plan, n);
            // Lines and free points make room before new waypoints count against the budget.
            var lines = Lines.Take(nav.LineLimit(plan)).ToList();
            Note(cut, jet, lines.Count, Lines.Count, "lines");
            while (nav.Lines(plan).Count > 0) nav.RemoveLine(plan, nav.Lines(plan).Count - 1);
            while (nav.Extra(plan).Count > 0) nav.RemoveExtra(plan, nav.Extra(plan).Count);
            for (var i = 0; i < wps.Count; i++)
            {
                var w = wps[i];
                if (i >= nav.Waypoints(plan).Count) nav.AddWaypoint(plan, At(w.X, w.Y));
                else if (Moved(nav.Waypoints(plan)[i], w.X, w.Y)) nav.MoveWaypoint(plan, i + 1, At(w.X, w.Y));
                nav.Waypoints(plan)[i]["name"] = Cap(w.Name, TomcatNav.NameLength);
            }
            var linePts = 0;
            var lineWanted = 0;
            foreach (var l in lines)
            {
                var idx = nav.AddLine(plan)!.Value;
                var limit = l.Closed ? TomcatNav.MaxClosedLinePoints : TomcatNav.MaxLinePoints;
                lineWanted += l.Points.Count;
                foreach (var p in l.Points.Take(limit))
                    if (nav.InsertLinePoint(plan, idx, int.MaxValue, At(p.X, p.Y))) linePts++;
                if (l.Closed) nav.SetClosed(plan, idx, true);
            }
            Note(cut, jet, linePts, lineWanted, "line points");
            var extra = 0;
            foreach (var p in Points)
                if (nav.AddExtra(plan, At(p.X, p.Y)) is { } e)
                {
                    e["name"] = Cap(p.Name, TomcatNav.NameLength);
                    extra++;
                }
            Note(cut, jet, extra, Points.Count, "additional points");
        }
    }
}
