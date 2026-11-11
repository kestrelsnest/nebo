using System.Globalization;
using Nebo.Client.Core;
using Nebo.Client.Map.Airfields;
using Nebo.Client.Map.Charts;
using SkiaSharp;

namespace Nebo.Client.Map.Gpu;

/// <summary>Airfields: runways (lines when zoomed out, true size with designators zoomed in),
/// approach paths for the ATC tab's airfield, and in the ATC airfield view also placed
/// diagrams, taxiways and parking.</summary>
public sealed class AirfieldsSk : MapLayer
{
    private const double OverviewMaxUnitsPerDip = 400;
    private const double DetailMaxUnitsPerDip = 12;
    private const double ParkingMaxUnitsPerDip = 6;
    private const double TaxiwayWidthM = 20;
    private const double ApproachMaxUnitsPerDip = 1500;
    private const double Nm = 1852;
    private static readonly double[] ApproachTicksNm = MapStyle.Numbers("airfields.approach.ticksNm");
    private static readonly SKColor LabelInk = MapStyle.Color("airfields.label");
    private static readonly SKColor RunwayFill = MapStyle.Color("airfields.runway");
    private static readonly SKColor TaxiFill = MapStyle.Color("airfields.taxiway");
    private static readonly SKColor Marking = MapStyle.Color("airfields.marking");
    private static readonly SKColor TaxiLine = MapStyle.Color("airfields.taxiLine");
    private static readonly SKColor Parking = MapStyle.Color("airfields.parking");
    private static readonly SKColor SelectedInk = MapStyle.Color("airfields.selected");
    private static readonly SKColor Outline = MapStyle.Color("airfields.chartOutline");
    private static readonly SKColor ApproachInk = MapStyle.Color("airfields.approach.color");
    private static readonly Ink.Pen ApproachPen = new(ApproachInk, MapStyle.Number("airfields.approach.width"));
    private static readonly Ink.Pen ApproachTick = new(ApproachInk, MapStyle.Number("airfields.approach.tickWidth"), Round: true);

    public bool FullDetail { get; set; }
    public SKColor Halo { get; set; } = SkPalette.HaloDark;
    public int? SelectedId { get; set; }
    /// <summary>The selected airfield's approach range marks: ATC windows only (MapPanel).</summary>
    public bool ShowApproaches { get; set; } = true;
    public bool ShowDiagrams { get; set; } = true;
    public bool NightDiagrams { get; set; }

    public AirfieldsSk()
    {
        Taxiways.Loaded += () => { if (FullDetail) Invalidate(); };
        TileCache.TileLoaded += () => { if (FullDetail && ShowDiagrams) Invalidate(); };
    }

    public override void Draw(SKCanvas canvas, in MapFrame frame)
    {
        var view = frame.View;
        var store = AppState.Store;
        if (store.Theatre is not { } theatre || store.Airfields.Count == 0) return;
        var upd = view.UnitsPerDip;
        if (ShowApproaches && !FullDetail && upd <= ApproachMaxUnitsPerDip && store.Airfields.FirstOrDefault(f => f.Id == SelectedId) is { } selected)
            foreach (var rw in selected.Runways) DrawApproaches(canvas, view, theatre, rw);
        if (upd > OverviewMaxUnitsPerDip) return;

        var detail = upd <= DetailMaxUnitsPerDip;
        var charts = FullDetail;
        var diagrams = detail && charts && ShowDiagrams ? AirfieldDiagrams.For(theatre) : null;
        var visible = store.Airfields.Where(f => InView(view, theatre, f, upd)).ToList();
        var charted = new HashSet<int>();
        if (diagrams is not null)
        {
            foreach (var field in visible)
            {
                if (diagrams.For(field.Id, NightDiagrams) is not { } entry) continue;
                diagrams.Draw(canvas, view, theatre, entry, 0.95f);
                charted.Add(field.Id);
            }
        }
        if (detail && charts && Taxiways.For(theatre) is { } taxi)
        {
            var skip = store.Airfields.Where(f => charted.Contains(f.Id)).Select(f => f.Name.Replace(' ', '_')).ToHashSet(StringComparer.OrdinalIgnoreCase);
            DrawTaxiways(canvas, view, taxi, upd, skip);
        }
        foreach (var field in visible)
        {
            var c = SkDraw.Screen(view, theatre, field.X, field.Z);
            foreach (var rw in field.Runways) DrawRunway(canvas, view, theatre, rw, detail, charted.Contains(field.Id));
            if (FullDetail && upd <= ParkingMaxUnitsPerDip) DrawParking(canvas, view, theatre, field);
            if (field.Id == SelectedId) canvas.DrawCircle(c, 22, Ink.Stroke(SelectedInk, 2));
            if (upd > DetailMaxUnitsPerDip || field.Runways.Count == 0)
                Text.Draw(canvas, field.IsHelipad ? $"H {field.Name}" : field.Name, c.X + 8, c.Y + 6, LabelInk, Halo, Text.Face.Bold, 10);
        }
    }

    private static bool InView(MapView view, Theatre theatre, Airfield field, double upd)
    {
        var c = SkDraw.Screen(view, theatre, field.X, field.Z);
        var extent = 400 + (field.Runways.Count > 0 ? field.Runways.Max(r => r.Length) : 1000) * 1.5 / upd;
        return c.X >= -extent && c.Y >= -extent && c.X <= view.Width + extent && c.Y <= view.Height + extent;
    }

    private static void DrawTaxiways(SKCanvas canvas, MapView view, Taxiways taxi, double upd, HashSet<string> skip)
    {
        var (minX, minY, maxX, maxY) = view.WorldBounds;
        var widthDip = (float)Math.Max(1.5, TaxiwayWidthM / upd);
        using var path = new SKPath();
        var area = SkClip.Area(view);
        area.Inflate(widthDip, widthDip);
        var runs = new VisibleRuns(path, area);
        foreach (var f in taxi.Fields)
        {
            if (skip.Contains(f.Name) || f.MaxX < minX || f.MinX > maxX || f.MaxY < minY || f.MinY > maxY) continue;
            foreach (var line in f.Lines)
            {
                var (x0, y0) = view.ToScreen(line[0], line[1]);
                runs.Start(new SKPoint((float)x0, (float)y0));
                for (int i = 2; i + 1 < line.Length; i += 2)
                {
                    var (x, y) = view.ToScreen(line[i], line[i + 1]);
                    runs.To(new SKPoint((float)x, (float)y));
                }
            }
        }
        canvas.DrawPath(path, Ink.Stroke(TaxiFill, widthDip, round: true));
        if (widthDip >= 6) canvas.DrawPath(path, Ink.Stroke(TaxiLine, Math.Max(0.6f, widthDip / 14)));
    }

    private static void DrawRunway(SKCanvas canvas, MapView view, Theatre theatre, Runway rw, bool detail, bool outlineOnly)
    {
        var h = rw.HeadingTrue * Math.PI / 180;
        double ax = Math.Cos(h), az = Math.Sin(h), cx = -Math.Sin(h), cz = Math.Cos(h);
        double hl = rw.Length / 2, hw = rw.Width / 2;
        SKPoint P(double along, double across) => SkDraw.Screen(view, theatre, rw.X + ax * along + cx * across, rw.Z + az * along + cz * across);
        if (!detail)
        {
            canvas.DrawLine(P(-hl, 0), P(hl, 0), Ink.Stroke(LabelInk, 2.5f, round: true));
            return;
        }
        SKPoint[] corners = [P(-hl, -hw), P(hl, -hw), P(hl, hw), P(-hl, hw)];
        using var outline = new SKPath();
        outline.AddPoly(corners, close: true);
        if (outlineOnly)
        {
            canvas.DrawPath(outline, Ink.Stroke(Outline, 1.2f));  // the chart draws the runway
            return;
        }
        canvas.DrawPath(outline, Ink.Fill(RunwayFill));
        canvas.DrawPath(outline, Ink.Stroke(Marking, 1));
        canvas.DrawLine(P(-hl + 120, 0), P(hl - 120, 0), new Ink.Pen(Marking, 1, [6, 6]).Paint());
        var (near, far) = rw.Designators;
        Centered(canvas, near, P(-hl + 70, 0));
        Centered(canvas, far, P(hl - 70, 0));
    }

    private static void Centered(SKCanvas canvas, string text, SKPoint at)
    {
        if (text.Length == 0) return;
        var (w, h) = Text.Measure(text, Text.Face.Bold, 11);
        Text.Draw(canvas, text, at.X - w / 2, at.Y - h / 2, Marking, SkPalette.HaloDark, Text.Face.Bold, 11);
    }

    /// <summary>Extended centre line out to 10 NM per runway end, ticks at 2/5/10 NM, "RWY xx"
    /// beyond the outer end; clipped to the view.</summary>
    private static void DrawApproaches(SKCanvas canvas, MapView view, Theatre theatre, Runway rw)
    {
        var h = rw.HeadingTrue * Math.PI / 180;
        double ax = Math.Cos(h), az = Math.Sin(h), hl = rw.Length / 2;
        var area = SkClip.Area(view);
        var (nearName, farName) = rw.Designators;
        foreach (var (sign, name) in new[] { (-1, nearName), (1, farName) })
        {
            double tx = rw.X + sign * ax * hl, tz = rw.Z + sign * az * hl;
            var threshold = SkDraw.Screen(view, theatre, tx, tz);
            var outer = SkDraw.Screen(view, theatre, tx + sign * ax * 10 * Nm, tz + sign * az * 10 * Nm);
            SkClip.Line(canvas, ApproachPen, threshold, outer, area);
            var d = outer - threshold;
            var len = d.Length;
            if (len < 1) continue;
            var dir = new SKPoint(d.X / len, d.Y / len);
            var across = new SKPoint(-dir.Y, dir.X);
            foreach (var nm in ApproachTicksNm)
            {
                var at = threshold + new SKPoint(d.X * (float)(nm / 10), d.Y * (float)(nm / 10));
                var half = nm == 10 ? 9f : 6f;
                if (!area.Contains(at)) continue;
                canvas.DrawLine(at - new SKPoint(across.X * half, across.Y * half), at + new SKPoint(across.X * half, across.Y * half), ApproachTick.Paint());
                var lp = at + new SKPoint(across.X * (half + 3), across.Y * (half + 3) - 7);
                Text.Draw(canvas, nm.ToString("0"), lp.X, lp.Y, ApproachInk, SkPalette.HaloDark, Text.Face.Bold, 10);
            }
            var tag = outer + new SKPoint(dir.X * 14, dir.Y * 14);
            if (name.Length > 0 && area.Contains(tag))
            {
                var text = $"RWY {name}";
                var (w, th) = Text.Measure(text, Text.Face.Bold, 10);
                Text.Draw(canvas, text, tag.X - w / 2, tag.Y - th / 2, ApproachInk, SkPalette.HaloDark, Text.Face.Bold, 10);
            }
        }
    }

    private void DrawParking(SKCanvas canvas, MapView view, Theatre theatre, Airfield field)
    {
        foreach (var spot in field.Parking)
        {
            var p = SkDraw.Screen(view, theatre, spot.X, spot.Z);
            if (p.X < -20 || p.Y < -20 || p.X > view.Width + 20 || p.Y > view.Height + 20) continue;
            canvas.DrawCircle(p, 4, Ink.Stroke(Parking, 1));
            Text.Draw(canvas, spot.Number.ToString(), p.X + 5, p.Y - 6, Parking, Halo, Text.Face.Regular, 9);
        }
    }
}
