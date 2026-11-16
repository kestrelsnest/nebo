using Nebo.Client.Core;
using Nebo.Client.Map.Charts;
using SkiaSharp;

namespace Nebo.Client.Map.Gpu;

/// <summary>Training missions (EventTargets): target zones outlined in their flight's colour,
/// each target as a dot (destroyed crossed out) once zoomed in, and a label under the zone:
/// "order · name · flight · dead/need". A done zone is grey with a tick.</summary>
public sealed class TargetsSk : MapLayer
{
    private static readonly SKColor[] FlightInks = MapStyle.Colors("targets.flights");
    private static readonly SKColor UnassignedInk = MapStyle.Color("targets.unassigned"), DoneInk = MapStyle.Color("targets.done");
    private static readonly SKColor DeadInk = MapStyle.Color("targets.item.dead"), DamagedInk = MapStyle.Color("targets.item.damaged");
    private static readonly float Width = MapStyle.Number("targets.width"), SelectedWidth = MapStyle.Number("targets.selectedWidth");
    private static readonly float[] Dash = MapStyle.Dash("targets.dash");
    private static readonly float FillAlpha = MapStyle.Number("targets.fillAlpha"), MinRadius = MapStyle.Number("targets.minRadius");
    private static readonly float ItemSize = MapStyle.Number("targets.item.size"), ItemMinZone = MapStyle.Number("targets.item.minZoneRadius");
    private static readonly double LabelMaxUnitsPerDip = MapStyle.Number("targets.label.maxMetersPerDip");
    private static readonly Text.Face LabelFace = Text.FaceOf(MapStyle.Text("targets.label.font"));
    private static readonly float LabelSize = MapStyle.Number("targets.label.size");

    public SKColor Halo { get; set; } = SkPalette.HaloDark;
    public int? SelectedZone { get; set; }
    public Func<MapView>? ViewProvider { get; set; }

    /// <summary>A flight's colour: flights in name order take the palette in turn, so every map
    /// and the Targets panel agree.</summary>
    public static SKColor InkFor(TargetsMessage targets, TargetZone z)
    {
        if (z.Done) return DoneInk;
        if (!z.Assigned || z.FlightList.Count == 0) return UnassignedInk;
        var flights = targets.Zones.SelectMany(t => t.FlightList).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToList();
        var i = flights.FindIndex(f => f.Equals(z.FlightList[0], StringComparison.OrdinalIgnoreCase));
        return FlightInks[Math.Max(0, i) % FlightInks.Length];
    }

    private static (SKPoint Center, float Radius, SKPoint[]? Poly) Shape(MapView view, Theatre theatre, TargetZone z)
    {
        var c = SkDraw.Screen(view, theatre, z.X, z.Z);
        SKPoint[]? poly = z.Verts is { Count: >= 3 } v ? v.Select(p => SkDraw.Screen(view, theatre, p[0], p[1])).ToArray() : null;
        float r;
        if (poly is not null) r = poly.Max(p => SKPoint.Distance(p, c));
        else
        {
            var (lat, _) = theatre.ToLatLon(z.X, z.Z);
            r = (float)(z.Radius * Mercator.ScaleAt(lat) / view.UnitsPerDip);
        }
        return (c, Math.Max(MinRadius, r), poly);
    }

    /// <summary>Target zone under a screen point, if any (the smallest when they overlap).</summary>
    public int? HitTest(double x, double y)
    {
        var store = AppState.Store;
        if (store.Theatre is not { } theatre || store.Targets is not { } targets || ViewProvider?.Invoke() is not { IsValid: true } view) return null;
        var at = new SKPoint((float)x, (float)y);
        return targets.Zones.Select(z => (z.Id, S: Shape(view, theatre, z)))
            .Where(e => SKPoint.Distance(e.S.Center, at) <= Math.Max(e.S.Radius, 10))
            .OrderBy(e => e.S.Radius).Select(e => (int?)e.Id).FirstOrDefault();
    }

    public override void Draw(SKCanvas canvas, in MapFrame frame)
    {
        var view = frame.View;
        if (AppState.Store.Theatre is not { } theatre || AppState.Store.Targets is not { Zones.Count: > 0 } targets) return;
        var area = SkClip.Area(view);
        var labels = view.UnitsPerDip <= LabelMaxUnitsPerDip;
        foreach (var z in targets.Zones.OrderBy(z => z.Id == SelectedZone))
        {
            var (c, r, poly) = Shape(view, theatre, z);
            if (c.X < -r - 150 || c.Y < -r - 40 || c.X > view.Width + r + 150 || c.Y > view.Height + r + 40) continue;
            var ink = InkFor(targets, z);
            var pen = new Ink.Pen(ink, z.Id == SelectedZone ? SelectedWidth : Width, z.Done ? null : Dash);
            var fill = ink.WithAlpha((byte)(FillAlpha * 255));
            if (poly is not null)
            {
                var clipped = SkClip.ClipPolygon(poly, area);
                SkClip.Polygon(canvas, clipped, Ink.Fill(fill));
                for (int i = 0; i < poly.Length; i++) SkClip.Line(canvas, pen, poly[i], poly[(i + 1) % poly.Length], area);
            }
            else SkClip.Circle(canvas, fill, pen, c, r, area);

            if (r >= ItemMinZone)
                foreach (var it in z.Items) DrawItem(canvas, SkDraw.Screen(view, theatre, it.X, it.Z), it, ink);

            if (!labels) continue;
            var bottom = poly is not null ? poly.Max(p => p.Y) : c.Y + r;
            Text.DrawCentered(canvas, z.Label, c.X, bottom + 3, ink, Halo, LabelFace, LabelSize);
        }
    }

    // Units round, buildings and statics square; damaged amber, destroyed crossed out in red.
    private void DrawItem(SKCanvas canvas, SKPoint p, TargetItem it, SKColor ink)
    {
        var s = ItemSize;
        var colour = it.Dead ? DeadInk : it.Damaged ? DamagedInk : ink;
        var fill = Ink.Fill(it.Dead ? Halo.WithAlpha(160) : colour);  // shared paints: never disposed
        var edge = Ink.Stroke(Halo, 1);
        if (it.Kind == "unit") { canvas.DrawCircle(p, s, fill); canvas.DrawCircle(p, s, edge); }
        else { var rect = SKRect.Create(p.X - s, p.Y - s, 2 * s, 2 * s); canvas.DrawRect(rect, fill); canvas.DrawRect(rect, edge); }
        if (!it.Dead) return;
        var x = Ink.Stroke(DeadInk, 1.6f);
        var k = s + 1.5f;
        canvas.DrawLine(p.X - k, p.Y - k, p.X + k, p.Y + k, x);
        canvas.DrawLine(p.X - k, p.Y + k, p.X + k, p.Y - k, x);
    }
}
