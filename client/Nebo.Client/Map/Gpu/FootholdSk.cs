using System.Globalization;
using Nebo.Client.Core;
using Nebo.Client.Map.Airfields;
using Nebo.Client.Map.Charts;
using SkiaSharp;

namespace Nebo.Client.Map.Gpu;

/// <summary>Foothold zones coloured by owner, links between them, names and strength bars.</summary>
public sealed class FootholdSk : MapLayer
{
    private static readonly float MinRadius = MapStyle.Number("zones.minRadius");
    private static readonly double LabelMaxUnitsPerDip = MapStyle.Number("zones.label.maxMetersPerDip");
    private static readonly Ink.Pen LinkPen = new(MapStyle.Color("zones.link.color"), MapStyle.Number("zones.link.width"), MapStyle.Dash("zones.link.dash"));
    private static readonly float Width = MapStyle.Number("zones.width"), SelectedWidth = MapStyle.Number("zones.selectedWidth");
    private static readonly float FillAlpha = MapStyle.Number("zones.fillAlpha"), SuspendedFillAlpha = MapStyle.Number("zones.suspendedFillAlpha");
    private static readonly float BigFactor = MapStyle.Number("zones.bigZoneFillFactor");
    private static readonly float[] SuspendedDash = MapStyle.Dash("zones.suspendedDash");
    private static readonly Text.Face LabelFace = Text.FaceOf(MapStyle.Text("zones.label.font"));
    private static readonly float LabelSize = MapStyle.Number("zones.label.size");

    public SKColor Halo { get; set; } = SkPalette.HaloDark;
    public string? SelectedZone { get; set; }
    public Func<MapView>? ViewProvider { get; set; }

    /// <summary>Zone under a screen point, if any.</summary>
    public string? HitTest(double x, double y)
    {
        var store = AppState.Store;
        if (store.Theatre is not { } theatre || store.Foothold is not { } state || ViewProvider?.Invoke() is not { IsValid: true } view) return null;
        foreach (var z in state.Zones)
        {
            var (c, r) = Circle(view, theatre, z);
            if (SKPoint.Distance(c, new SKPoint((float)x, (float)y)) <= Math.Max(r, 10)) return z.Name;
        }
        return null;
    }

    private static (SKPoint Center, float Radius) Circle(MapView view, Theatre theatre, FootholdZone z)
    {
        var c = SkDraw.Screen(view, theatre, z.X, z.Z);
        var (lat, _) = theatre.ToLatLon(z.X, z.Z);
        return (c, (float)Math.Max(MinRadius, z.Radius * Mercator.ScaleAt(lat) / view.UnitsPerDip));
    }

    public override void Draw(SKCanvas canvas, in MapFrame frame)
    {
        var view = frame.View;
        if (AppState.Store.Theatre is not { } theatre || AppState.Store.Foothold is not { } state) return;
        var circles = state.Zones.ToDictionary(z => z.Name, z => Circle(view, theatre, z));
        var area = SkClip.Area(view);
        foreach (var link in state.Links)
        {
            if (link.Length < 2 || !circles.TryGetValue(link[0], out var a) || !circles.TryGetValue(link[1], out var b)) continue;
            var d = b.Center - a.Center;
            var len = d.Length;
            if (len <= a.Radius + b.Radius) continue;
            var u = new SKPoint(d.X / len, d.Y / len);
            SkClip.Line(canvas, LinkPen, a.Center + new SKPoint(u.X * a.Radius, u.Y * a.Radius), b.Center - new SKPoint(u.X * b.Radius, u.Y * b.Radius), area);
        }
        var labels = view.UnitsPerDip <= LabelMaxUnitsPerDip;
        foreach (var z in state.Zones)
        {
            var (c, r) = circles[z.Name];
            if (c.X < -r - 100 || c.Y < -r - 40 || c.X > view.Width + r + 100 || c.Y > view.Height + r + 40) continue;
            // Suspended (idle) zones fainter; big zones (naval areas) only a faint fill.
            var solid = SkPalette.Coalition(z.Side);
            var alpha = (z.Suspended ? SuspendedFillAlpha : FillAlpha) * (r > 60 ? BigFactor : 1);
            var pen = new Ink.Pen(solid, z.Name == SelectedZone ? SelectedWidth : Width, z.Suspended ? SuspendedDash : null);
            SkClip.Circle(canvas, solid.WithAlpha((byte)(alpha * 255)), pen, c, r, area);
            if (z.Logistic) canvas.DrawRect(c.X - 3, c.Y - 3, 6, 6, Ink.Fill(solid));
            if (!labels) continue;
            var title = z.Waypoint is { } w ? $"{z.Name} · {w}" : z.Name;
            var (_, th) = Text.DrawCentered(canvas, title, c.X, c.Y + r + 3, solid, Halo, LabelFace, LabelSize);
            if (z.Strength is { } s)
            {
                var bar = SKRect.Create(c.X - 18, c.Y + r + 3 + th + 1, 36, 4);
                canvas.DrawRect(bar, Ink.Fill(Halo));
                canvas.DrawRect(SKRect.Create(bar.Left, bar.Top, bar.Width * (float)s, bar.Height), Ink.Fill(solid));
            }
        }
    }
}
