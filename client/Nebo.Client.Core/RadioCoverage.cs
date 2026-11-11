namespace Nebo.Client.Core;

/// <summary>Where an aircraft at a given altitude can hear a radio: rays every few degrees from
/// the radio out to the radio horizon, and along each the stretches where the aircraft clears
/// every rise of the terrain in between (Earth's bulge with the radio radius, 4/3 R). Off the
/// terrain grid the ground counts as sea level.</summary>
public sealed record RadioCoverage(double X, double Z, double HorizonM, double RayStepDeg, IReadOnlyList<IReadOnlyList<(double From, double To)>> Rays)
{
    private const double EffectiveEarthRadius = 6_371_000 * 4.0 / 3.0;

    /// <summary>Line-of-sight distance between two heights above the surface (m), ignoring terrain.</summary>
    public static double HorizonMeters(double heightA, double heightB) =>
        Math.Sqrt(2 * EffectiveEarthRadius * Math.Max(0, heightA)) + Math.Sqrt(2 * EffectiveEarthRadius * Math.Max(0, heightB));

    /// <summary>The coverage of a radio at (x, z, altitude ASL m) for an aircraft at
    /// <paramref name="targetAltM"/> ASL. Cost: rays × (horizon / step) height samples.</summary>
    public static RadioCoverage Compute(TerrainGrid? grid, double x, double z, double altM, double targetAltM,
        double rayStepDeg = 2, double sampleM = 1000)
    {
        var horizon = HorizonMeters(altM, targetAltM);
        var rays = new List<IReadOnlyList<(double, double)>>();
        int count = (int)Math.Round(360 / rayStepDeg);
        for (int r = 0; r < count; r++)
        {
            var b = r * rayStepDeg * Math.PI / 180;
            double dx = Math.Cos(b), dz = Math.Sin(b);  // x north, z east
            var intervals = new List<(double, double)>();
            var maxAngle = double.NegativeInfinity;  // steepest terrain seen so far
            double? start = null;
            for (double d = sampleM; d <= horizon + 1; d += sampleM)
            {
                var drop = d * d / (2 * EffectiveEarthRadius);
                var ground = grid is null ? 0 : Math.Max(0, LineOfSight.HeightAt(grid, x + dx * d, z + dz * d) ?? 0);
                // The aircraft at this distance, seen from the radio, against what lies before it.
                var target = (targetAltM - drop - altM) / d;
                var visible = target >= maxAngle && targetAltM > ground;
                if (visible) start ??= d - sampleM;
                else if (start is { } s)
                {
                    intervals.Add((s, d - sampleM));
                    start = null;
                }
                maxAngle = Math.Max(maxAngle, (ground - drop - altM) / d);
            }
            if (start is { } open) intervals.Add((open, horizon));
            rays.Add(intervals);
        }
        return new RadioCoverage(x, z, horizon, rayStepDeg, rays);
    }
}
