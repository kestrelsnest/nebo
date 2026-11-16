using SkiaSharp;

namespace Nebo.Client.Map.Gpu;

/// <summary>Controllers' areas of operations (DLL 0.17): each a polygon in its controller's
/// colour, lightly filled, with the callsign at its top corner; and the area being drawn (its
/// corners and a line to the pointer).</summary>
public sealed class AreasSk : MapLayer
{
    /// <summary>Which areas this map shows (the map's owner filter).</summary>
    public OwnerView Owners { get; set; } = OwnerView.All;
    /// <summary>The corners placed so far while drawing an area, else null.</summary>
    public List<(double Lat, double Lon)>? Drawing { get; set; }
    public (double Lat, double Lon)? Pointer { get; set; }
    public SKColor DrawColor { get; set; } = new(0xFF, 0x7A, 0x1A);

    public override void Draw(SKCanvas canvas, in MapFrame frame)
    {
        var view = frame.View;
        foreach (var c in FlightDesk.AreasShown(Owners))
        foreach (var (pts, index) in c.AreaPolygons.Select((p, i) => (p, i)))
        {
            var color = c.Rgb is var (r, g, b) ? new SKColor(r, g, b) : new SKColor(0xB0, 0xB0, 0xB0);
            using var path = Path(view, pts, close: true);
            canvas.DrawPath(path, Ink.Fill(color.WithAlpha(0x1C)));
            canvas.DrawPath(path, Ink.Stroke(color.WithAlpha(0xD0), 2));
            var top = pts.Select(p => SkDraw.ScreenLonLat(view, p.Lon, p.Lat)).OrderBy(p => p.Y).First();
            // "· F10": the side's pilots see this area on their F10 map.
            Text.DrawCentered(canvas, c.OnF10(index) ? c.Display + " · F10" : c.Display, top.X, top.Y - 8, color, SkPalette.HaloDark, Text.Face.Bold, 13);
        }
        if (Drawing is { Count: > 0 } drawing)
        {
            var points = Pointer is { } p ? [.. drawing, p] : drawing;
            using var path = Path(view, points, close: false);
            canvas.DrawPath(path, Ink.Dashed(DrawColor, 2, [8, 5]).Paint());
            if (points.Count >= 3)
            {
                var a = SkDraw.ScreenLonLat(view, points[^1].Lon, points[^1].Lat);
                var z = SkDraw.ScreenLonLat(view, points[0].Lon, points[0].Lat);
                canvas.DrawLine(a, z, Ink.Dashed(DrawColor.WithAlpha(0x70), 1.5f, [4, 6]).Paint());
            }
            foreach (var c in drawing)
                canvas.DrawCircle(SkDraw.ScreenLonLat(view, c.Lon, c.Lat), 4, Ink.Fill(DrawColor));
            Text.Note(canvas, view, $"Area: click to add corners ({drawing.Count}/{Core.Areas.MaxPoints}) · first corner, double-click, Enter or Done: finish · Backspace: undo · Esc: cancel");
        }
    }

    private static SKPath Path(MapView view, IReadOnlyList<(double Lat, double Lon)> pts, bool close)
    {
        var path = new SKPath();
        for (var i = 0; i < pts.Count; i++)
        {
            var s = SkDraw.ScreenLonLat(view, pts[i].Lon, pts[i].Lat);
            if (i == 0) path.MoveTo(s); else path.LineTo(s);
        }
        if (close) path.Close();
        return path;
    }
}
