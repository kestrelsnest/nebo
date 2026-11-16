using Nebo.Client.Core.Dtc;
using Nebo.Client.Dtc;
using SkiaSharp;

namespace Nebo.Client.Map.Gpu;

/// <summary>The DTC tab's cartridge on its map, as DCS's DTC manager draws it: waypoints as
/// numbered circles with their 5-letter label, the three routes as lines in DCS's colours (route
/// 1 white, 2 black, 3 red) with each leg's length in NM at its middle, the selected waypoint
/// ringed.</summary>
public sealed class DtcSk : MapLayer
{
    private static readonly SKColor[] RouteInk = [new(255, 255, 255), new(20, 20, 20), new(230, 40, 40)];
    private static readonly SKColor PointInk = new(255, 214, 64);
    private static readonly SKColor LegInk = new(235, 240, 245);
    private static readonly SKColor Halo = new(0, 0, 0, 200);
    private const float Radius = 7;

    /// <summary>Shown on a GCI or ATC map (the "DTC" filter): the plan only, without the editing
    /// aids (selection rings, the picked JDAM, the delete and placing banners).</summary>
    public bool ReadOnly { get; set; }

    /// <summary>Screen positions of the waypoints last drawn, for hit tests.</summary>
    private readonly List<(int Num, SKPoint At)> _drawn = [];
    private readonly List<(int Num, SKPoint At)> _gpsDrawn = [];
    private static readonly SKColor GpsInk = new(80, 220, 255);

    public override void Draw(SKCanvas canvas, in MapFrame frame)
    {
        _drawn.Clear();
        var view = frame.View;
        if (AppState.Store.Theatre is not { } theatre) return;
        var s = DtcSession.Instance;
        _jdamDrawn.Clear();
        if (s.IsTomcat)
        {
            DrawTomcatLines(canvas, view, theatre, s);
            DrawGps(canvas, view, theatre, s);
            DrawJdam(canvas, view, theatre, s);
            DrawTomcat(canvas, view, theatre, s);
            DeleteBanner(canvas, view, s);
            return;
        }
        if (s.IsViper)
        {
            DrawViperSa(canvas, view, theatre, s);
            DrawGps(canvas, view, theatre, s);
            DrawViper(canvas, view, theatre, s);
            DeleteBanner(canvas, view, s);
            return;
        }
        var nav = s.Nav;
        var at = nav.Waypoints.ToDictionary(HornetNav.Num, p => SkDraw.Screen(view, theatre, Num(p, "x"), Num(p, "y")));

        for (var r = 1; r <= HornetNav.Routes; r++)
        {
            var route = nav.Route(r).Where(at.ContainsKey).Select(n => at[n]).ToList();
            var lengths = nav.LegLengths(r);
            for (var i = 1; i < route.Count; i++)
            {
                canvas.DrawLine(route[i - 1], route[i], Ink.Stroke(Halo, 4.5f, round: true));
                canvas.DrawLine(route[i - 1], route[i], Ink.Stroke(RouteInk[r - 1], 2, round: true));
            }
            // The legs' lengths, at their middles (only when the leg is long enough on screen).
            for (var i = 1; i < route.Count && i - 1 < lengths.Count; i++)
            {
                var (a, b) = (route[i - 1], route[i]);
                if ((b - a).Length < 60) continue;
                var label = $"{lengths[i - 1] / 1852:0.0} NM";
                Text.DrawCentered(canvas, label, (a.X + b.X) / 2, (a.Y + b.Y) / 2 - 7 + (r - 1) * 13, LegInk, Halo, Text.Face.Bold, 11);
            }
        }
        DrawSa(canvas, view, theatre, s);
        DeleteBanner(canvas, view, s);
        DrawGps(canvas, view, theatre, s);

        foreach (var p in nav.Waypoints)
        {
            var num = HornetNav.Num(p);
            var pt = at[num];
            _drawn.Add((num, pt));
            if (!ReadOnly && s.SelectedSet.Contains(num)) canvas.DrawCircle(pt, Radius + 5, Ink.Stroke(new SKColor(90, 200, 255), s.Selected == num ? 2.5f : 1.5f));
            canvas.DrawCircle(pt, Radius, Ink.Fill(Halo));
            canvas.DrawCircle(pt, Radius, Ink.Stroke(PointInk, 2));
            Text.DrawCentered(canvas, num.ToString(), pt.X, pt.Y - 6, PointInk, null, Text.Face.Bold, 9);
            if ((string?)p["text_note"] is { Length: > 0 } label)
                Text.Draw(canvas, label, pt.X + Radius + 4, pt.Y - 7, PointInk, Halo, Text.Face.Bold, 11);
        }
    }

    private static double Num(System.Text.Json.Nodes.JsonObject p, string key) => HornetNav.Number(p[key]) ?? 0;

    /// <summary>GPS waypoints (F/A-18C) or DEST points (F-16C): diamonds with their text, under the
    /// waypoints / steerpoints.</summary>
    private void DrawGps(SKCanvas canvas, Map.MapView view, Core.Theatre theatre, DtcSession s)
    {
        _gpsDrawn.Clear();
        foreach (var (num, g, label) in s.GpsPoints)
        {
            var pt = SkDraw.Screen(view, theatre, Num(g, "x"), Num(g, "y"));
            _gpsDrawn.Add((num, pt));
            using var diamond = new SKPath();
            diamond.MoveTo(pt.X, pt.Y - Radius);
            diamond.LineTo(pt.X + Radius, pt.Y);
            diamond.LineTo(pt.X, pt.Y + Radius);
            diamond.LineTo(pt.X - Radius, pt.Y);
            diamond.Close();
            if (!ReadOnly && s.SelectedGpsSet.Contains(num)) canvas.DrawCircle(pt, Radius + 5, Ink.Stroke(new SKColor(90, 200, 255), s.SelectedGps == num ? 2.5f : 1.5f));
            canvas.DrawPath(diamond, Ink.Fill(Halo));
            canvas.DrawPath(diamond, Ink.Stroke(GpsInk, 2));
            Text.Draw(canvas, label, pt.X + Radius + 4, pt.Y - 7, GpsInk, Halo, Text.Face.Bold, 11);
        }
    }

    /// <summary>An offset leg: a dashed line from its steerpoint to a triangle with its label.</summary>
    private static void DrawOffset(SKCanvas canvas, SKPoint from, SKPoint at, string label, SKColor ink)
    {
        canvas.DrawLine(from, at, new Ink.Pen(ink, 1.5f, [5, 4]).Paint());
        using var tri = new SKPath();
        tri.MoveTo(at.X, at.Y - 7);
        tri.LineTo(at.X + 6, at.Y + 5);
        tri.LineTo(at.X - 6, at.Y + 5);
        tri.Close();
        canvas.DrawPath(tri, Ink.Fill(Halo));
        canvas.DrawPath(tri, Ink.Stroke(ink, 2));
        Text.Draw(canvas, label, at.X + 9, at.Y - 7, ink, Halo, Text.Face.Bold, 10);
    }

    // ---- F-14B(U): the open plan and the JDAM targets ---------------------------------------------

    private static readonly SKColor LarInk = new(255, 100, 100), JdamInk = new(255, 60, 60), JdamPickInk = new(255, 230, 0);

    /// <summary>The F-14 plan's waypoints (numbered circles with their names) and its route line.</summary>
    private void DrawTomcat(SKCanvas canvas, Map.MapView view, Core.Theatre theatre, DtcSession s)
    {
        var wps = s.Waypoints;
        var at = wps.Select(w => SkDraw.Screen(view, theatre, Num(w.Point, "x"), Num(w.Point, "y"))).ToList();
        for (var i = 1; i < at.Count; i++)
        {
            canvas.DrawLine(at[i - 1], at[i], Ink.Stroke(Halo, 4.5f, round: true));
            canvas.DrawLine(at[i - 1], at[i], Ink.Stroke(RouteInk[0], 2, round: true));
            var (a, b) = (at[i - 1], at[i]);
            if ((b - a).Length >= 60)
            {
                var (p, q) = (wps[i - 1].Point, wps[i].Point);
                var nm = Math.Sqrt(Math.Pow(Num(q, "x") - Num(p, "x"), 2) + Math.Pow(Num(q, "y") - Num(p, "y"), 2)) / 1852;
                Text.DrawCentered(canvas, $"{nm:0.0} NM", (a.X + b.X) / 2, (a.Y + b.Y) / 2 - 7, LegInk, Halo, Text.Face.Bold, 11);
            }
        }
        for (var i = 0; i < wps.Count; i++)
        {
            var (num, p) = wps[i];
            var pt = at[i];
            _drawn.Add((num, pt));
            if (!ReadOnly && s.SelectedSet.Contains(num)) canvas.DrawCircle(pt, Radius + 5, Ink.Stroke(new SKColor(90, 200, 255), s.Selected == num ? 2.5f : 1.5f));
            canvas.DrawCircle(pt, Radius, Ink.Fill(Halo));
            canvas.DrawCircle(pt, Radius, Ink.Stroke(PointInk, 2));
            Text.DrawCentered(canvas, num.ToString(), pt.X, pt.Y - 6, PointInk, null, Text.Face.Bold, 9);
            if ((string?)p["name"] is { Length: > 0 } name) Text.Draw(canvas, name, pt.X + Radius + 4, pt.Y - 7, PointInk, Halo, Text.Face.Bold, 11);
        }
    }

    /// <summary>The F-14 plan's lines (a closed one joined back to its first point); the line new
    /// points go on drawn heavier.</summary>
    private void DrawTomcatLines(SKCanvas canvas, Map.MapView view, Core.Theatre theatre, DtcSession s)
    {
        _saDrawn.Clear();
        var lines = s.TNav.Lines(s.TomcatPlan);
        for (var i = 0; i < lines.Count; i++)
        {
            var pts = TomcatNav.PointsOf(lines[i]).Select(p => SkDraw.Screen(view, theatre, Num(p, "x"), Num(p, "y"))).ToList();
            if (pts.Count == 0) continue;
            var ink = GeoInk[i % GeoInk.Length];
            var current = s.Place == DtcPlace.Sa && s.SaLine == i;
            var width = current ? 3.2f : 1.8f;
            using var path = new SKPath();
            path.MoveTo(pts[0]);
            foreach (var p in pts.Skip(1)) path.LineTo(p);
            if (TomcatNav.IsClosed(lines[i]) && pts.Count > 2) path.Close();
            canvas.DrawPath(path, Ink.Stroke(Halo, width + 2));
            canvas.DrawPath(path, Ink.Stroke(ink, width));
            Text.Draw(canvas, $"LINE {i + 1}{(current ? "  ◀ current" : "")}", pts[0].X + 8, pts[0].Y - 16, ink, Halo, Text.Face.Bold, 11);
            for (var j = 0; j < pts.Count; j++)
            {
                var r = new SaRef(SaKind.Geo, i, j);
                _saDrawn.Add((r, pts[j]));
                if (!ReadOnly && s.SaSelectedSet.Contains(r)) canvas.DrawCircle(pts[j], 9, Ink.Stroke(SelectInk, s.SaSelected == r ? 2.5f : 1.5f));
                canvas.DrawCircle(pts[j], 4, Ink.Fill(ink));
            }
        }
    }

    /// <summary>A JDAM part on the map: the target (0), its launch point (1), its heading handle (2).</summary>
    public readonly record struct JdamHit(int Station, int Target, int Part);
    private readonly List<(JdamHit Hit, SKPoint At)> _jdamDrawn = [];

    /// <summary>Every placed GBU-31 target: its launch zone (behind it, opposite the attack heading),
    /// the launch point, the heading handle at the zone's far end; the picked one in yellow.</summary>
    private void DrawJdam(SKCanvas canvas, Map.MapView view, Core.Theatre theatre, DtcSession s)
    {
        SKPoint At(double x, double y) => SkDraw.Screen(view, theatre, x, y);
        foreach (var (station, target, t) in s.Jdam.Active)
        {
            var picked = !ReadOnly && s.JdamPick == (station, target);
            var ink = picked ? JdamPickInk : LarInk;
            using var zone = new SKPath();
            var pts = TomcatJdam.Zone(t).Select(p => At(p.X, p.Y)).ToList();
            zone.MoveTo(pts[0]);
            foreach (var p in pts.Skip(1)) zone.LineTo(p);
            zone.Close();
            canvas.DrawPath(zone, Ink.Fill(ink.WithAlpha(25)));
            canvas.DrawPath(zone, Ink.Stroke(ink, picked ? 2 : 1.4f));
            var c = At(Num(t, "x"), Num(t, "y"));
            var lp = At(Num(t, "launch_x"), Num(t, "launch_y"));
            var (_, far) = TomcatJdam.Axis(t);
            var hdg = At(far.X, far.Y);
            var name = $"{TomcatJdam.StationNames[station - 1]} T{target}";
            // Target: a crossed square.
            canvas.DrawRect(new SKRect(c.X - 6, c.Y - 6, c.X + 6, c.Y + 6), Ink.Stroke(Halo, 4));
            canvas.DrawRect(new SKRect(c.X - 6, c.Y - 6, c.X + 6, c.Y + 6), Ink.Stroke(picked ? JdamPickInk : JdamInk, 2));
            canvas.DrawLine(c.X - 6, c.Y - 6, c.X + 6, c.Y + 6, Ink.Stroke(picked ? JdamPickInk : JdamInk, 1.5f));
            canvas.DrawLine(c.X - 6, c.Y + 6, c.X + 6, c.Y - 6, Ink.Stroke(picked ? JdamPickInk : JdamInk, 1.5f));
            var label = (string?)t["name"] is { Length: > 0 } n ? $"{name} {n}" : name;
            Text.Draw(canvas, label, c.X + 10, c.Y - 7, picked ? JdamPickInk : JdamInk, Halo, Text.Face.Bold, 11);
            // Launch point and heading handle.
            canvas.DrawCircle(lp, 5, Ink.Fill(Halo));
            canvas.DrawCircle(lp, 5, Ink.Stroke(ink, 2));
            Text.Draw(canvas, $"LP{target}", lp.X + 8, lp.Y - 6, ink, Halo, Text.Face.Bold, 10);
            canvas.DrawCircle(hdg, 4, Ink.Fill(ink));
            Text.Draw(canvas, $"{Num(t, "attack_heading"):0.0}°", hdg.X + 8, hdg.Y - 6, ink, Halo, Text.Face.Bold, 10);
            _jdamDrawn.Add((new JdamHit(station, target, 2), hdg));
            _jdamDrawn.Add((new JdamHit(station, target, 1), lp));
            _jdamDrawn.Add((new JdamHit(station, target, 0), c));
        }
    }

    /// <summary>The JDAM target, launch point or heading handle under a screen point (DIPs), or null.</summary>
    public JdamHit? HitTestJdam(double x, double y)
    {
        foreach (var (hit, pt) in Enumerable.Reverse(_jdamDrawn))
            if ((pt.X - x) * (pt.X - x) + (pt.Y - y) * (pt.Y - y) <= 9 * 9) return hit;
        return null;
    }

    // ---- F-16C threats and geographic lines -------------------------------------------------------

    /// <summary>Line colours 1–4, after the jet's default HSD GEO line colours where they differ enough.</summary>
    private static readonly SKColor[] GeoInk = [new(255, 214, 64), new(110, 230, 120), new(80, 220, 255), new(230, 110, 255)];

    /// <summary>Preplanned threats (ring of their radius, text inside; a hidden ring dashed) and
    /// geographic lines 1–4 joining their points; the line new points go on drawn heavier.</summary>
    private void DrawViperSa(SKCanvas canvas, Map.MapView view, Core.Theatre theatre, DtcSession s)
    {
        _saDrawn.Clear();
        SKPoint At(double x, double y) => SkDraw.Screen(view, theatre, x, y);
        void Handle(SaRef r, SKPoint pt, SKColor ink)
        {
            _saDrawn.Add((r, pt));
            if (!ReadOnly && s.SaSelectedSet.Contains(r)) canvas.DrawCircle(pt, 9, Ink.Stroke(SelectInk, s.SaSelected == r ? 2.5f : 1.5f));
            canvas.DrawCircle(pt, 4, Ink.Fill(ink));
        }
        var threats = s.Threats.Points;
        for (var i = 0; i < threats.Count; i++)
        {
            var t = threats[i];
            var (x, y) = (Num(t, "x"), Num(t, "y"));
            var c = At(x, y);
            var r = SKPoint.Distance(c, At(x, y + Num(t, "radius")));
            var shown = t["ring"]?.GetValue<bool>() != false;
            canvas.DrawCircle(c, r, Ink.Stroke(Halo, 3.5f));
            canvas.DrawCircle(c, r, shown ? Ink.Stroke(ThreatInk, 1.8f) : new Ink.Pen(ThreatInk, 1.2f, [6, 6]).Paint());
            Text.DrawCentered(canvas, (string?)t["text"] ?? "", c.X, c.Y + 5, ThreatInk, Halo, Text.Face.Bold, 11);
            Handle(new SaRef(SaKind.Threat, i), c, ThreatInk);
        }
        for (var line = 1; line <= ViperGeo.Lines; line++)
        {
            var pts = s.Geo.Line(line).Select(n => s.Geo.Point(n)!).Select(p => At(Num(p, "x"), Num(p, "y"))).ToList();
            if (pts.Count == 0) continue;
            var ink = GeoInk[line - 1];
            var current = s.Place == DtcPlace.Sa && s.SaPlace == SaKind.Geo && s.SaLine == line - 1;
            var width = current ? 3.2f : 1.8f;
            using var path = new SKPath();
            path.MoveTo(pts[0]);
            foreach (var p in pts.Skip(1)) path.LineTo(p);
            canvas.DrawPath(path, Ink.Stroke(Halo, width + 2));
            canvas.DrawPath(path, Ink.Stroke(ink, width));
            Text.Draw(canvas, $"LINE {line}{(current ? "  ◀ current" : "")}", pts[0].X + 8, pts[0].Y - 16, ink, Halo, Text.Face.Bold, 11);
            for (var j = 0; j < pts.Count; j++) Handle(new SaRef(SaKind.Geo, line - 1, j), pts[j], ink);
        }
    }

    /// <summary>Delete mode can't be missed: a banner across the top of the map.</summary>
    private void DeleteBanner(SKCanvas canvas, Map.MapView view, DtcSession s)
    {
        if (ReadOnly || !s.DeleteMode) return;
        var w = (float)view.Width;
        canvas.DrawRect(new SKRect(w / 2 - 190, 8, w / 2 + 190, 32), Ink.Fill(new SKColor(170, 20, 20, 220)));
        Text.DrawCentered(canvas, "DELETE MODE — right-click deletes · " + Input.Bindings.Get(Input.Bindings.DtcMode) + " switches back",
            w / 2, 14, SKColors.White, null, Text.Face.Bold, 12);
    }

    // ---- F-16C steerpoints ------------------------------------------------------------------------

    private static readonly SKColor OapInk = new(255, 150, 40), VipInk = new(230, 110, 255);

    /// <summary>A VIP / VRP leg as the jet's pages name its point.</summary>
    public static string LegName(string leg) => leg switch
    {
        "VIPTOTGT" => "VIP→TGT", "VIPTOPUP" => "VIP→PUP", "TGTTOVRP" => "TGT→VRP", _ => "TGT→PUP",
    };

    /// <summary>The F-16C's steerpoints (numbered circles, IP / TGT marked), its three routes in
    /// steerpoint order with leg NM, and each point's offset aimpoints (triangles on a dashed line).</summary>
    private void DrawViper(SKCanvas canvas, Map.MapView view, Core.Theatre theatre, DtcSession s)
    {
        var stpt = s.Stpt;
        var pts = stpt.Points;
        var at = pts.Select(p => SkDraw.Screen(view, theatre, Num(p, "x"), Num(p, "y"))).ToList();
        for (var r = 1; r <= ViperNav.Routes; r++)
        {
            var route = stpt.Route(r).Select(n => at[n - 1]).ToList();
            var lengths = stpt.LegLengths(r);
            for (var i = 1; i < route.Count; i++)
            {
                canvas.DrawLine(route[i - 1], route[i], Ink.Stroke(Halo, 4.5f, round: true));
                canvas.DrawLine(route[i - 1], route[i], Ink.Stroke(RouteInk[r - 1], 2, round: true));
                var (a, b) = (route[i - 1], route[i]);
                if ((b - a).Length >= 60 && i - 1 < lengths.Count)
                    Text.DrawCentered(canvas, $"{lengths[i - 1] / 1852:0.0} NM", (a.X + b.X) / 2, (a.Y + b.Y) / 2 - 7 + (r - 1) * 13, LegInk, Halo, Text.Face.Bold, 11);
            }
        }
        for (var i = 0; i < pts.Count; i++)
        {
            var p = pts[i];
            var num = i + 1;
            var pt = at[i];
            for (var o = 1; o <= 2; o++)
            {
                if (!ViperNav.HasOap(p, o)) continue;
                var (ox, oy) = ViperOffset.At(p, $"OAP_{o}");
                DrawOffset(canvas, pt, SkDraw.Screen(view, theatre, ox, oy), $"OA{o}", OapInk);
            }
            // The VIP / VRP legs from this steerpoint.
            foreach (var leg in ViperVipVrp.Legs)
            {
                if (s.VipVrp.SteerpointOf(leg) != num || !s.VipVrp.IsSet(leg)) continue;
                var (lx, ly) = ViperOffset.At(s.VipVrp.Data, leg);
                DrawOffset(canvas, pt, SkDraw.Screen(view, theatre, lx, ly), LegName(leg), s.VipVrp.Enabled(leg) ? VipInk : VipInk.WithAlpha(110));
            }
            _drawn.Add((num, pt));
            if (!ReadOnly && s.SelectedSet.Contains(num)) canvas.DrawCircle(pt, Radius + 5, Ink.Stroke(new SKColor(90, 200, 255), s.Selected == num ? 2.5f : 1.5f));
            canvas.DrawCircle(pt, Radius, Ink.Fill(Halo));
            canvas.DrawCircle(pt, Radius, Ink.Stroke(PointInk, 2));
            Text.DrawCentered(canvas, num.ToString(), pt.X, pt.Y - 6, PointInk, null, Text.Face.Bold, 9);
            var label = (string?)p["type"] is { } type && type != "STPT" ? type : "";
            if ((string?)p["note"] is { Length: > 0 } note) label = label.Length > 0 ? $"{label} {note}" : note;
            if (label.Length > 0) Text.Draw(canvas, label, pt.X + Radius + 4, pt.Y - 7, PointInk, Halo, Text.Face.Bold, 11);
        }
        if (!ReadOnly && s.PlaceOffset is { } offset)
        {
            var what = offset.StartsWith("OAP_", StringComparison.Ordinal)
                ? $"OA{offset[^1]} of steerpoint {s.Selected}"
                : $"{LegName(offset)} from steerpoint {s.VipVrp.SteerpointOf(offset)}";
            var w = (float)view.Width;
            canvas.DrawRect(new SKRect(w / 2 - 190, 8, w / 2 + 190, 32), Ink.Fill(new SKColor(150, 80, 10, 220)));
            Text.DrawCentered(canvas, $"Right-click places {what}", w / 2, 14, SKColors.White, null, Text.Face.Bold, 12);
        }
    }

    // ---- SA page objects ------------------------------------------------------------------------

    private static readonly SKColor ThreatInk = new(255, 70, 70), CapInk = new(80, 220, 255), CorridorInk = new(110, 230, 120),
        FaorInk = new(255, 214, 64), FlotInk = new(255, 150, 40), SelectInk = new(90, 200, 255);
    private readonly List<(SaRef Ref, SKPoint At)> _saDrawn = [];

    private void DrawSa(SKCanvas canvas, Map.MapView view, Core.Theatre theatre, DtcSession s)
    {
        _saDrawn.Clear();
        var sa = s.Sa;
        SKPoint At(double x, double y) => SkDraw.Screen(view, theatre, x, y);
        void Handle(SaRef r, SKPoint pt, SKColor ink)
        {
            _saDrawn.Add((r, pt));
            if (!ReadOnly && s.SaSelectedSet.Contains(r)) canvas.DrawCircle(pt, 9, Ink.Stroke(SelectInk, s.SaSelected == r ? 2.5f : 1.5f));
            canvas.DrawCircle(pt, 4, Ink.Fill(ink));
        }

        // Threat rings: the radius in NM around the threat, its text inside.
        var threats = sa.Threats;
        for (var i = 0; i < threats.Count; i++)
        {
            var t = threats[i];
            var (x, y) = (Num(t, "x"), Num(t, "y"));
            var c = At(x, y);
            var edge = At(x, y + Num(t, "threat_ring_radius") * 1852);
            canvas.DrawCircle(c, SKPoint.Distance(c, edge), Ink.Stroke(Halo, 3.5f));
            canvas.DrawCircle(c, SKPoint.Distance(c, edge), Ink.Stroke(ThreatInk, 1.8f));
            Text.DrawCentered(canvas, (string?)t["text"] ?? "", c.X, c.Y + 5, ThreatInk, Halo, Text.Face.Bold, 11);
            Handle(new SaRef(SaKind.Threat, i), c, ThreatInk);
        }

        // CAP orbits: a racetrack from the anchor along the course, turning left or right.
        var caps = sa.Caps;
        for (var i = 0; i < caps.Count; i++)
        {
            var cap = caps[i];
            var (ax, ay) = (Num(cap, "x"), Num(cap, "y"));
            var course = Num(cap, "course") * Math.PI / 180;
            var (len, r) = (Num(cap, "length"), Num(cap, "diameter") / 2);
            var (dx, dy) = (Math.Cos(course), Math.Sin(course));                     // DCS x north, y east
            var (nx, ny) = (string?)cap["turn_direction"] == "Right" ? (-dy, dx) : (dy, -dx);
            using var path = new SKPath();
            path.MoveTo(At(ax, ay));
            var (bx, by) = (ax + len * dx, ay + len * dy);
            path.LineTo(At(bx, by));
            var (mx, my) = (bx + r * nx, by + r * ny);                                   // far turn, bulging forward
            for (var k = 1; k <= 16; k++)
            {
                var a = Math.PI * k / 16;
                path.LineTo(At(mx + r * (-Math.Cos(a) * nx + Math.Sin(a) * dx), my + r * (-Math.Cos(a) * ny + Math.Sin(a) * dy)));
            }
            var (m2x, m2y) = (ax + r * nx, ay + r * ny);                                 // near turn, bulging back
            path.LineTo(At(m2x + r * nx, m2y + r * ny));
            for (var k = 1; k <= 16; k++)
            {
                var a = Math.PI * k / 16;
                path.LineTo(At(m2x + r * (Math.Cos(a) * nx - Math.Sin(a) * dx), m2y + r * (Math.Cos(a) * ny - Math.Sin(a) * dy)));
            }
            canvas.DrawPath(path, Ink.Stroke(Halo, 3.5f));
            canvas.DrawPath(path, Ink.Stroke(CapInk, 1.8f));
            var a0 = At(ax, ay);
            Text.Draw(canvas, $"CAP {i + 1}", a0.X + 8, a0.Y - 16, CapInk, Halo, Text.Face.Bold, 11);
            Handle(new SaRef(SaKind.Cap, i), a0, CapInk);
        }

        // Corridors (10 NM wide), FAOR (closed, dashed) and FLOT lines with their points.
        var perNm = SKPoint.Distance(At(0, 0), At(0, 1852));
        void Lines(HornetSa.LineKind line, SaKind kind, SKColor ink, string label)
        {
            var lines = sa.LinesOf(line);
            for (var i = 0; i < lines.Count; i++)
            {
                var pts = HornetSa.PointsOf(lines[i]).Select(p => At(Num(p, "x"), Num(p, "y"))).ToList();
                if (pts.Count == 0) continue;
                using var path = new SKPath();
                path.MoveTo(pts[0]);
                foreach (var p in pts.Skip(1)) path.LineTo(p);
                if (kind == SaKind.Faor && pts.Count > 2) path.Close();
                if (kind == SaKind.Corridor)
                    canvas.DrawPath(path, Ink.Stroke(ink.WithAlpha(50), (float)(2 * HornetSa.CorridorHalfWidthM / 1852 * perNm), round: true));
                // The line new points go on (SA tab, its type picked): drawn heavier.
                var current = s.Place == DtcPlace.Sa && s.SaPlace == kind && s.SaLine == i;
                var width = current ? 3.2f : 1.8f;
                canvas.DrawPath(path, Ink.Stroke(Halo, width + 2));
                canvas.DrawPath(path, kind == SaKind.Faor ? new Ink.Pen(ink, width, [8, 5]).Paint() : Ink.Stroke(ink, width));
                Text.Draw(canvas, $"{label} {i + 1}{(current ? "  ◀ current" : "")}", pts[0].X + 8, pts[0].Y - 16, ink, Halo, Text.Face.Bold, 11);
                for (var j = 0; j < pts.Count; j++) Handle(new SaRef(kind, i, j), pts[j], ink);
            }
        }
        Lines(HornetSa.Corridor, SaKind.Corridor, CorridorInk, "CORR");
        Lines(HornetSa.Faor, SaKind.Faor, FaorInk, "FAOR");
        Lines(HornetSa.Flot, SaKind.Flot, FlotInk, "FLOT");
    }

    /// <summary>The SA object (or line point) drawn under a screen point (DIPs), or null.</summary>
    public SaRef? HitTestSa(double x, double y)
    {
        foreach (var (r, pt) in Enumerable.Reverse(_saDrawn))
            if ((pt.X - x) * (pt.X - x) + (pt.Y - y) * (pt.Y - y) <= 9 * 9) return r;
        return null;
    }

    /// <summary>The waypoint drawn under a screen point (DIPs), or null.</summary>
    public int? HitTest(double x, double y) => Hit(_drawn, x, y);

    /// <summary>The GPS waypoint drawn under a screen point (DIPs), or null.</summary>
    public int? HitTestGps(double x, double y) => Hit(_gpsDrawn, x, y);

    private static int? Hit(List<(int Num, SKPoint At)> drawn, double x, double y)
    {
        foreach (var (num, pt) in Enumerable.Reverse(drawn))
            if ((pt.X - x) * (pt.X - x) + (pt.Y - y) * (pt.Y - y) <= (Radius + 4) * (Radius + 4)) return num;
        return null;
    }
}
