namespace Nebo.Client.Core;

/// <summary>A collision intercept: where an interceptor flying straight at its current speed
/// meets a target holding its heading and speed, how long it takes, and the heading to fly.</summary>
public sealed record InterceptSolution(double Lat, double Lon, double Seconds, double HeadingTrue);

public static class Intercept
{
    /// <summary>Beyond this the answer means nothing (targets turn): no intercept.</summary>
    public const double MaxSeconds = 3600;

    /// <summary>The intercept, or null when the interceptor cannot catch the target at these speeds
    /// (or is not moving). Flat-earth over the short distances a GCI intercept spans: positions
    /// relative to the interceptor in metres east/north.</summary>
    public static InterceptSolution? Solve(double fromLat, double fromLon, double fromSpeedMps,
        double toLat, double toLon, double toHdgTrue, double toSpeedMps)
    {
        if (fromSpeedMps <= 1) return null;
        var (brg, range) = Nav.BearingRange(fromLat, fromLon, toLat, toLon);
        double r = brg * Math.PI / 180, h = toHdgTrue * Math.PI / 180;
        double dx = range * Math.Sin(r), dy = range * Math.Cos(r);                 // target, east/north of the interceptor
        double vx = toSpeedMps * Math.Sin(h), vy = toSpeedMps * Math.Cos(h);      // target's velocity
        // |d + v t| = s t  →  (v·v − s²) t² + 2 (d·v) t + d·d = 0
        double a = vx * vx + vy * vy - fromSpeedMps * fromSpeedMps, b = 2 * (dx * vx + dy * vy), c = dx * dx + dy * dy;
        double t;
        if (Math.Abs(a) < 1e-6) t = b < 0 ? -c / b : double.NaN;
        else
        {
            var disc = b * b - 4 * a * c;
            if (disc < 0) return null;
            var q = Math.Sqrt(disc);
            double t1 = (-b - q) / (2 * a), t2 = (-b + q) / (2 * a);
            t = new[] { t1, t2 }.Where(x => x > 0).DefaultIfEmpty(double.NaN).Min();
        }
        if (double.IsNaN(t) || t <= 0 || t > MaxSeconds) return null;
        double px = dx + vx * t, py = dy + vy * t;
        var heading = Nav.Normalize(Math.Atan2(px, py) * 180 / Math.PI);
        var (lat, lon) = Nav.Destination(fromLat, fromLon, heading, Math.Sqrt(px * px + py * py));
        return new InterceptSolution(lat, lon, t, heading);
    }
}
