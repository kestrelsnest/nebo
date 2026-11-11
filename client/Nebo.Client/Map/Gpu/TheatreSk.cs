using System.Globalization;
using Nebo.Client.Core;
using Nebo.Client.Map.Airfields;
using Nebo.Client.Map.Charts;
using SkiaSharp;

namespace Nebo.Client.Map.Gpu;

/// <summary>Theatre border with the outside dimmed, plus optional town names.</summary>
public sealed class TheatreSk : MapLayer
{
    private const int MaxTowns = 400;
    private static readonly double TownsMaxUnitsPerDip = MapStyle.Number("theatre.town.maxMetersPerDip");
    private static readonly Ink.Pen BorderPen = new(MapStyle.Color("theatre.border.color"), MapStyle.Number("theatre.border.width"), MapStyle.Dash("theatre.border.dash"));
    private static readonly SKColor OutsideDark = MapStyle.Color("theatre.outsideDark");
    private static readonly SKColor OutsideLight = MapStyle.Color("theatre.outsideLight");
    private static readonly SKColor TownDark = MapStyle.Color("theatre.town.dark");
    private static readonly SKColor TownLight = MapStyle.Color("theatre.town.light");
    private static readonly float TownSize = MapStyle.Number("theatre.town.size");

    public bool ShowTowns { get; set; }
    public bool LightBaseMap { get; set; }

    private Theatre? _theatre;
    private List<(double X, double Y)> _borderMerc = [];
    private readonly Dictionary<string, string> _titles = [];

    public override void Draw(SKCanvas canvas, in MapFrame frame)
    {
        var view = frame.View;
        if (AppState.Store.Theatre is not { } theatre) return;
        if (!ReferenceEquals(theatre, _theatre))
        {
            _theatre = theatre;
            _borderMerc = theatre.Border().Select(p => Mercator.FromLonLat(p.Lon, p.Lat)).ToList();
        }
        var area = SkClip.Area(view);
        var border = _borderMerc.Select(p => { var (x, y) = view.ToScreen(p.X, p.Y); return new SKPoint((float)x, (float)y); }).ToList();
        // Outside dimmed: the view rectangle and the clipped border filled even-odd.
        var inside = SkClip.ClipPolygon(border, area);
        using (var dim = new SKPath { FillType = SKPathFillType.EvenOdd })
        {
            dim.AddRect(area);
            if (inside.Count >= 3) dim.AddPoly(inside.ToArray(), close: true);
            canvas.DrawPath(dim, Ink.Fill(LightBaseMap ? OutsideLight : OutsideDark));
        }
        var along = 0.0;
        for (int i = 0; i < border.Count; i++)
        {
            SKPoint a = border[i], b = border[(i + 1) % border.Count];
            SkClip.Line(canvas, BorderPen, a, b, area, along);
            along += (b - a).Length;
        }
        if (ShowTowns && view.UnitsPerDip <= TownsMaxUnitsPerDip) DrawTowns(canvas, view, theatre);
    }

    private void DrawTowns(SKCanvas canvas, MapView view, Theatre theatre)
    {
        var ink = LightBaseMap ? TownLight : TownDark;
        // Declutter: one town per cell of at least 110×22 DIPs. The cells are fixed in map
        // metres for each doubling of the zoom, so which names show only changes when the zoom
        // crosses a doubling — not on every frame of a zoom, where names would flicker.
        var q = Math.Pow(2, Math.Ceiling(Math.Log2(view.UnitsPerDip)));
        double cellW = 110 * q, cellH = 22 * q;
        var taken = new HashSet<(long, long)>();
        var drawn = 0;
        foreach (var town in theatre.Towns)
        {
            var (mx, my) = Mercator.FromLonLat(town.Lon, town.Lat);
            var (sx, sy) = view.ToScreen(mx, my);
            var p = new SKPoint((float)sx, (float)sy);
            if (p.X < -50 || p.Y < -20 || p.X > view.Width + 50 || p.Y > view.Height + 20) continue;
            if (!taken.Add(((long)Math.Floor(mx / cellW), (long)Math.Floor(my / cellH)))) continue;
            canvas.DrawCircle(p, 1.6f, Ink.Fill(ink));
            if (!_titles.TryGetValue(town.Name, out var title))
                _titles[town.Name] = title = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(town.Name.ToLowerInvariant());
            Text.Draw(canvas, title, p.X + 3, p.Y - 7, ink, null, Text.Face.Regular, TownSize);
            if (++drawn >= MaxTowns) break;
        }
    }
}
