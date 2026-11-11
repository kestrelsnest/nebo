using System.Globalization;
using Nebo.Client.Core;
using Nebo.Client.Map.Airfields;
using Nebo.Client.Map.Charts;
using SkiaSharp;

namespace Nebo.Client.Map.Gpu;

/// <summary>Bullseye, the right-drag ruler (magnetic bearing, NM; BRAA on an aircraft) and the
/// cursor's bullseye call while the bullseye key is held.</summary>
public sealed class MeasureSk : MapLayer
{
    public readonly record struct End(long? TrackId, double Lat, double Lon);

    private static readonly SKColor InkColor = MapStyle.Color("measure.ink");
    private static readonly SKColor BullsInk = MapStyle.Color("measure.bullseye");
    private static readonly SKColor Back = MapStyle.Color("measure.box");
    private static readonly SKColor Casing = MapStyle.Color("measure.casing");

    public End? RulerFrom { get; set; }
    public End? RulerTo { get; set; }

    // Engagement lines: the BRAA lines the controller pinned (a left click while right-dragging
    // the ruler), shared by every map (AppState.Engagements). They follow their tracks; between two
    // aircraft the "from" end is the interceptor and, unless switched off, the collision intercept
    // at both current speeds is drawn. A line's marker (1–8) sits in a badge at its middle.
    private static readonly SKColor MarkerInk = MapStyle.Color("measure.intercept");

    private MapView? _lastView;

    /// <summary>Boxes the BRAA and intercept boxes keep clear of (the symbols layer's labels and symbols).</summary>
    public Func<IReadOnlyList<Box>>? Obstacles { get; set; }

    // Boxes drawn so far this frame: later ones keep clear of them too.
    private readonly List<SKRect> _taken = [];

    /// <summary>A box of w×h near point p, placed like the data blocks: down-right of it first (dx,
    /// dy away), then up-right, down-left, up-left, below, above — the first clear of the labels,
    /// symbols and boxes already drawn, else the one overlapping least.</summary>
    private SKRect Place(float w, float h, SKPoint p, float dx, float dy)
    {
        var obstacles = (Obstacles?.Invoke() ?? []).Select(b => SKRect.Create((float)b.X, (float)b.Y, (float)b.W, (float)b.H)).Concat(_taken).ToList();
        (float X, float Y)[] spots = [(dx, dy), (dx, -dy - h), (-dx - w, dy), (-dx - w, -dy - h), (-w / 2, dy + 8), (-w / 2, -dy - 8 - h)];
        SKRect best = default;
        var bestCost = float.MaxValue;
        foreach (var (ox, oy) in spots)
        {
            var r = SKRect.Create(p.X + ox, p.Y + oy, w, h);
            var cost = 0f;
            foreach (var o in obstacles)
            {
                var i = SKRect.Intersect(r, o);
                if (!i.IsEmpty) cost += i.Width * i.Height;
            }
            if (cost < bestCost) (best, bestCost) = (r, cost);
            if (cost == 0) break;
        }
        _taken.Add(best);
        return best;
    }

    private static (End End, Unit? Unit) Resolve(LineEnd end) => Resolve(new End(end.TrackId, end.Lat, end.Lon));

    /// <summary>The engagement line passing within a few pixels of a screen point (its id), if any.</summary>
    public int? HitPinned(double px, double py)
    {
        if (_lastView is not { } view) return null;
        var lines = AppState.Engagements.Lines;
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            var (a, b) = (Resolve(lines[i].From).End, Resolve(lines[i].To).End);
            var (ax, ay) = view.ToScreenLonLat(a.Lon, a.Lat);
            var (bx, by) = view.ToScreenLonLat(b.Lon, b.Lat);
            double dx = bx - ax, dy = by - ay, len2 = dx * dx + dy * dy;
            var k = len2 < 1 ? 0 : Math.Clamp(((px - ax) * dx + (py - ay) * dy) / len2, 0, 1);
            if (Math.Sqrt(Math.Pow(ax + k * dx - px, 2) + Math.Pow(ay + k * dy - py, 2)) <= 6) return lines[i].Id;
        }
        return null;
    }

    private static void DrawMarker(SKCanvas canvas, SKPoint a, SKPoint b, int marker)
    {
        var c = new SKPoint((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        canvas.DrawCircle(c, 9, Ink.Fill(MarkerInk));
        canvas.DrawCircle(c, 9, Ink.Stroke(Casing, 1.4f));
        Text.DrawCentered(canvas, marker.ToString(), c.X, c.Y - 7, SKColors.Black, null, Text.Face.Bold, 12);
    }
    public (double X, double Y)? BullsCursor { get; set; }
    /// <summary>The Alt+clicked point whose coordinates are shown; null hides it.</summary>
    public (double Lat, double Lon)? Pin { get; set; }

    public override void Draw(SKCanvas canvas, in MapFrame frame)
    {
        var view = frame.View;
        _lastView = view;
        _taken.Clear();
        var store = AppState.Store;
        var magvar = store.Theatre?.MagVar;
        var bulls = store.Latest?.Bullseye;
        if (bulls is not null) DrawBullseye(canvas, view, bulls);
        foreach (var line in AppState.Engagements.Lines)
        {
            var (a, b) = (Resolve(line.From), Resolve(line.To));
            DrawRuler(canvas, view, a, b, magvar, line.ShowIntercept ? DrawIntercept(canvas, view, a.Unit, b.Unit, magvar) : null);
            if (line.Marker is { } m)
                DrawMarker(canvas, SkDraw.ScreenLonLat(view, a.End.Lon, a.End.Lat), SkDraw.ScreenLonLat(view, b.End.Lon, b.End.Lat), m);
        }
        if (Pin is { } pin)
        {
            var p = SkDraw.ScreenLonLat(view, pin.Lon, pin.Lat);
            foreach (var pen in new[] { Ink.Stroke(Casing, 3.4f), Ink.Stroke(InkColor, 1.4f) })
            {
                canvas.DrawCircle(p, 6, pen);
                canvas.DrawLine(p.X - 11, p.Y, p.X - 3, p.Y, pen);
                canvas.DrawLine(p.X + 3, p.Y, p.X + 11, p.Y, pen);
                canvas.DrawLine(p.X, p.Y - 11, p.X, p.Y - 3, pen);
                canvas.DrawLine(p.X, p.Y + 3, p.X, p.Y + 11, pen);
            }
        }
        SKRect? ruler = RulerFrom is { } from && RulerTo is { } to ? DrawRuler(canvas, view, Resolve(from), Resolve(to), magvar) : null;
        if (BullsCursor is { } c)
        {
            var text = bulls is null ? "no bullseye" : Nav.BullseyeCall(bulls, view.ToLatLon(c.X, c.Y).Lat, view.ToLatLon(c.X, c.Y).Lon, magvar) + (magvar is null ? " T" : "");
            // Left of the cursor, mirroring the ruler's label on the right, so the two never
            // overlap; at the map's left edge, below the ruler's label instead.
            var (w, _) = BoxSize([text]);
            float x = (float)c.X - 16 - w, y = (float)c.Y + 12;
            if (x < 4)
            {
                x = (float)c.X + 16;
                if (ruler is { } r && r.Left < x + w && r.Right > x && r.Bottom > y) y = r.Bottom + 4;
            }
            BoxAt(canvas, [text], x, y);
        }
    }

    private static (End End, Unit? Unit) Resolve(End end) =>
        end.TrackId is { } id && AppState.Store.Find(id)?.Unit is { Lat: { } lat, Lon: { } lon } u
            ? (end with { Lat = lat, Lon = lon }, u)
            : (end, null);

    private static readonly SKColor InterceptInk = MapStyle.Color("measure.intercept");

    /// <summary>A pinned line's intercept (interceptor a, target b, both aircraft moving): a dashed
    /// line from the interceptor to an ✕ where they meet, "ICPT 2:40  HDG 045°" beside it in the
    /// intercept colour. Returns the lines it adds to the BRAA box: "NO ICPT" when a cannot catch
    /// b at these speeds, else none.</summary>
    private IReadOnlyList<string> DrawIntercept(SKCanvas canvas, MapView view, Unit? a, Unit? b, MagVarGrid? magvar)
    {
        if (a is not { Lat: { } alat, Lon: { } alon, Spd: { } aspd } || b is not { Lat: { } blat, Lon: { } blon, Spd: { } bspd, Hdg: { } bhdg }
            || !Symbology.IsAir(Symbology.KindOf(a)) || !Symbology.IsAir(Symbology.KindOf(b)))
            return [];
        if (Intercept.Solve(alat, alon, aspd, blat, blon, bhdg, bspd) is not { } s) return ["NO ICPT"];
        var pa = SkDraw.ScreenLonLat(view, alon, alat);
        var pi = SkDraw.ScreenLonLat(view, s.Lon, s.Lat);
        foreach (var (color, width) in new[] { (Casing, 3.2f), (InterceptInk, 1.3f) })
        {
            canvas.DrawLine(pa, pi, new Ink.Pen(color, width, [6, 4]).Paint());
            var cross = Ink.Stroke(color, width + 0.6f);
            canvas.DrawLine(pi.X - 6, pi.Y - 6, pi.X + 6, pi.Y + 6, cross);
            canvas.DrawLine(pi.X - 6, pi.Y + 6, pi.X + 6, pi.Y - 6, cross);
        }
        var hdg = magvar is null ? Nav.Bearing3(s.HeadingTrue) + "°T" : Nav.Bearing3(Nav.Magnetic(s.HeadingTrue, magvar.At(alat, alon))) + "°";
        var t = TimeSpan.FromSeconds(s.Seconds);
        var text = $"ICPT {(int)t.TotalMinutes}:{t.Seconds:00}  HDG {hdg}";
        var (w, h) = Text.Measure(text, Text.Face.Mono, 12);
        var box = Place(w + 10, h + 6, pi, 10, 8);
        canvas.DrawRoundRect(box, 3, 3, Ink.Fill(Back));
        Text.Draw(canvas, text, box.Left + 5, box.Top + 3, InterceptInk, null, Text.Face.Mono, 12);
        return [];
    }

    /// <summary>Draws the ruler; returns where its label went. <paramref name="extra"/>: more label lines (the intercept).</summary>
    private SKRect DrawRuler(SKCanvas canvas, MapView view, (End End, Unit? Unit) a, (End End, Unit? Unit) b, MagVarGrid? magvar,
        IReadOnlyList<string>? extra = null)
    {
        var pa = SkDraw.ScreenLonLat(view, a.End.Lon, a.End.Lat);
        var pb = SkDraw.ScreenLonLat(view, b.End.Lon, b.End.Lat);
        foreach (var pen in new[] { Ink.Stroke(Casing, 3.6f), Ink.Stroke(InkColor, 1.6f) })
        {
            canvas.DrawLine(pa, pb, pen);
            canvas.DrawCircle(pa, 3, pen);
            canvas.DrawCircle(pb, 3, pen);
        }
        var (brgTrue, meters) = Nav.BearingRange(a.End.Lat, a.End.Lon, b.End.Lat, b.End.Lon);
        var brg = magvar is null ? Nav.Bearing3(brgTrue) + "°T" : Nav.Bearing3(Nav.Magnetic(brgTrue, magvar.At(a.End.Lat, a.End.Lon))) + "°";
        var lines = new List<string> { $"{brg}  {meters / Nav.MetersPerNm:0.0} NM" };
        if (b.Unit is { } target)
        {
            var parts = new List<string>();
            if (target.AltFt is { } ft) parts.Add($"{Math.Round(ft / 100) * 100:#,0} ft".Replace(',', ' '));
            if (target.Hdg is { } hdg && target.Spd is > 5)
                parts.Add(Nav.Aspect(hdg, Nav.BearingRange(b.End.Lat, b.End.Lon, a.End.Lat, a.End.Lon).TrueDeg));
            if (parts.Count > 0) lines.Add(string.Join("  ", parts));
        }
        if (extra is not null) lines.AddRange(extra);
        var (bw, bh) = BoxSize(lines);
        var place = Place(bw, bh, pb, 12, 10);
        return BoxAt(canvas, lines, place.Left, place.Top);
    }

    private static void DrawBullseye(SKCanvas canvas, MapView view, Bullseye bulls)
    {
        var c = SkDraw.ScreenLonLat(view, bulls.Lon, bulls.Lat);
        if (c.X < -40 || c.Y < -40 || c.X > view.Width + 40 || c.Y > view.Height + 40) return;
        foreach (var pen in new[] { Ink.Stroke(Casing, 3.2f), Ink.Stroke(BullsInk, 1.4f) })
        {
            canvas.DrawCircle(c, 7, pen);
            canvas.DrawCircle(c, 13, pen);
            canvas.DrawLine(c.X - 18, c.Y, c.X + 18, c.Y, pen);
            canvas.DrawLine(c.X, c.Y - 18, c.X, c.Y + 18, pen);
        }
        Text.Draw(canvas, "BULLS", c.X + 15, c.Y + 9, BullsInk, SkPalette.HaloDark, Text.Face.Bold, 10);
    }

    private static (float W, float H) BoxSize(IReadOnlyList<string> lines)
    {
        var sizes = lines.Select(l => Text.Measure(l, Text.Face.Mono, 12)).ToList();
        return (sizes.Max(s => s.Width) + 10, sizes.Sum(s => s.Height) + 6);
    }

    private static SKRect BoxAt(SKCanvas canvas, IReadOnlyList<string> lines, float x, float y)
    {
        var (w, h) = BoxSize(lines);
        var rect = SKRect.Create(x, y, w, h);
        canvas.DrawRoundRect(rect, 3, 3, Ink.Fill(Back));
        var ty = y + 3;
        foreach (var line in lines)
        {
            Text.Draw(canvas, line, x + 5, ty, InkColor, null, Text.Face.Mono, 12);
            ty += Text.Measure(line, Text.Face.Mono, 12).Height;
        }
        return rect;
    }
}
