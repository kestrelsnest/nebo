using Nebo.Client.Core;
using SkiaSharp;

namespace Nebo.Client.Map.Gpu;

/// <summary>The SRS position's transmit coverage (Srs.SrsCoverage): light-blue where an aircraft at
/// the chosen altitude hears the controller, terrain shadows left clear, and a dashed circle at
/// the radio horizon.</summary>
public sealed class SrsCoverageSk : MapLayer
{
    private static readonly SKColor Ink = new(110, 190, 255);

    public override void Draw(SKCanvas canvas, in MapFrame frame)
    {
        var view = frame.View;
        if (Srs.SrsCoverage.Instance.Coverage is not { } c || AppState.Store.Theatre is not { } theatre) return;
        var half = c.RayStepDeg / 2 * Math.PI / 180;
        SKPoint At(double bearing, double d) => SkDraw.Screen(view, theatre, c.X + Math.Cos(bearing) * d, c.Z + Math.Sin(bearing) * d);
        using var area = new SKPath();
        for (int r = 0; r < c.Rays.Count; r++)
        {
            var b = r * c.RayStepDeg * Math.PI / 180;
            foreach (var (from, to) in c.Rays[r])
            {
                // One sector piece of this ray's width from the near to the far edge of the stretch.
                area.MoveTo(At(b - half, from));
                area.LineTo(At(b - half, to));
                area.LineTo(At(b + half, to));
                area.LineTo(At(b + half, from));
                area.Close();
            }
        }
        canvas.DrawPath(area, Gpu.Ink.Fill(Ink.WithAlpha(46)));
        using var horizon = new SKPath();
        for (int k = 0; k <= 72; k++)
        {
            var p = At(k * 5 * Math.PI / 180, c.HorizonM);
            if (k == 0) horizon.MoveTo(p); else horizon.LineTo(p);
        }
        canvas.DrawPath(horizon, new Ink.Pen(Ink.WithAlpha(170), 1.2f, [6, 5]).Paint());
    }
}
