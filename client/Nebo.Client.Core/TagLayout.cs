namespace Nebo.Client.Core;

/// <summary>A screen rectangle (DIPs, y down).</summary>
public readonly record struct Box(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
    public bool Overlaps(Box o) => X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;
    public double OverlapArea(Box o) =>
        Math.Max(0, Math.Min(Right, o.Right) - Math.Max(X, o.X)) * Math.Max(0, Math.Min(Bottom, o.Bottom) - Math.Max(Y, o.Y));
    public bool Contains(double px, double py) => px >= X && px <= Right && py >= Y && py <= Bottom;
}

/// <summary>Where a moving aircraft's data block goes.</summary>
public enum TagPlacement
{
    /// <summary>Always above the symbol, on the side behind it (heading east: up-left): the block
    /// does not jump up and down as the aircraft turns; the side changes only once the heading is
    /// well past north or south.</summary>
    Above,
    /// <summary>Beside the speed vector (right of it, then left, then behind): turns with the aircraft.</summary>
    Vector,
}

/// <summary>Where data blocks go so they do not overlap one another, the symbols or the speed
/// vectors: each tries the spots around its symbol in turn and takes the first that is clear of
/// the blocks placed before it and of every obstacle, else the one overlapping least. A block
/// keeps the spot it had (<see cref="Memory"/>) while that is still clear, so blocks move only
/// when something would cover them. Spots per <see cref="TagPlacement"/>; without a heading,
/// up-right first (the scope convention). A block the controller dragged keeps its own place and
/// counts as placed first.</summary>
public static class TagLayout
{
    /// <summary>A block to place: its anchor (the symbol), its size, a dragged offset if any, and
    /// the screen direction its vector points (radians clockwise from screen up) if it moves.</summary>
    public readonly record struct Tag(long Key, double AnchorX, double AnchorY, double W, double H, (double Dx, double Dy)? Dragged,
        double? VectorRad = null);

    /// <summary>What blocks had last time, by key: the spot taken (its place in the tag's list) and,
    /// for <see cref="TagPlacement.Above"/>, the side (+1 right, −1 left). Kept by the caller.</summary>
    public sealed class Memory
    {
        internal readonly Dictionary<long, int> Spot = [];
        internal readonly Dictionary<long, int> Side = [];

        /// <summary>Forget blocks that are gone.</summary>
        public void Keep(IEnumerable<long> keys)
        {
            var keep = keys.ToHashSet();
            foreach (var k in Spot.Keys.Where(k => !keep.Contains(k)).ToList()) Spot.Remove(k);
            foreach (var k in Side.Keys.Where(k => !keep.Contains(k)).ToList()) Side.Remove(k);
        }
    }

    /// <summary>Gap between the symbol and the block's near corner.</summary>
    public const double Gap = 14;

    /// <summary>Above: the side flips only when the heading's east/west part passes this (sin 20°).</summary>
    public const double SideHysteresis = 0.342;

    // Spots as unit directions: up-right, right, down-right, up-left, left, down-left, up, down.
    private static readonly (double X, double Y)[] Spots =
        [(1, -1), (1, 0), (1, 1), (-1, -1), (-1, 0), (-1, 1), (0, -1.4), (0, 1.4)];

    // Beside a vector, as (along, across) multiples: right, left, behind-right, behind-left, behind,
    // ahead-right, ahead-left. "Across" is to the right of the direction of flight.
    private static readonly (double Along, double Across)[] VectorSpots =
        [(0, 1), (0, -1), (-0.7, 0.7), (-0.7, -0.7), (-1, 0), (0.7, 0.7), (0.7, -0.7)];

    /// <summary>The side behind a moving aircraft for <see cref="TagPlacement.Above"/>: west of it
    /// heading east (−1), east of it heading west (+1); near north or south the side it had.</summary>
    public static int BehindSide(double vectorRad, int? previous)
    {
        var east = Math.Sin(vectorRad);
        if (east > SideHysteresis) return -1;
        if (east < -SideHysteresis) return 1;
        return previous ?? (east >= 0 ? -1 : 1);
    }

    /// <summary>The spot directions a tag tries, in order (screen x right, y down).</summary>
    public static IReadOnlyList<(double X, double Y)> SpotsFor(Tag t, TagPlacement mode = TagPlacement.Vector, int? side = null)
    {
        if (t.VectorRad is not { } a) return Spots;
        if (mode == TagPlacement.Above)
        {
            var s = side ?? BehindSide(a, null);
            return [(s, -1), (-s, -1), (0, -1.4), (s, 1), (-s, 1), (s, 0), (-s, 0), (0, 1.4)];
        }
        double fx = Math.Sin(a), fy = -Math.Cos(a);   // along the vector
        double rx = -fy, ry = fx;                      // to its right
        return VectorSpots.Select(v => (v.Along * fx + v.Across * rx, v.Along * fy + v.Across * ry)).ToList();
    }

    /// <summary>The block's box for a spot direction. The screen spots (scope convention): its near
    /// corner Gap from the anchor. Any other direction: its centre out along it, far enough that
    /// the box keeps Gap clear of the anchor.</summary>
    public static Box At(Tag t, (double X, double Y) dir)
    {
        if (!Spots.Contains(dir))
        {
            var len = Math.Sqrt(dir.X * dir.X + dir.Y * dir.Y);
            double ux = dir.X / len, uy = dir.Y / len;
            var k = Gap + Math.Abs(ux) * t.W / 2 + Math.Abs(uy) * t.H / 2;
            return new Box(t.AnchorX + ux * k - t.W / 2, t.AnchorY + uy * k - t.H / 2, t.W, t.H);
        }
        return Corner(t, dir);
    }

    private static Box Corner(Tag t, (double X, double Y) dir)
    {
        var x = dir.X > 0 ? t.AnchorX + Gap : dir.X < 0 ? t.AnchorX - Gap - t.W : t.AnchorX - t.W / 2;
        var y = dir.Y < 0 ? t.AnchorY - Gap - t.H : dir.Y > 0 ? t.AnchorY + Gap : t.AnchorY - t.H / 2;
        if (dir.X == 0) y = dir.Y < 0 ? t.AnchorY - Gap * 1.4 - t.H : t.AnchorY + Gap * 1.4;
        return new Box(x, y, t.W, t.H);
    }

    /// <summary>Each tag's box, in the order given (callers put the most important first).
    /// <paramref name="symbols"/>: boxes no block should cover (symbols, pieces of the vectors).
    /// <paramref name="memory"/>: last frame's choices (updated), so blocks stay put.</summary>
    public static IReadOnlyList<Box> Place(IReadOnlyList<Tag> tags, IReadOnlyList<Box> symbols,
        TagPlacement mode = TagPlacement.Vector, Memory? memory = null)
    {
        var placed = new Box[tags.Count];
        var taken = new List<Box>();
        for (int i = 0; i < tags.Count; i++)
            if (tags[i].Dragged is { } d)
            {
                placed[i] = new Box(tags[i].AnchorX + d.Dx, tags[i].AnchorY + d.Dy, tags[i].W, tags[i].H);
                taken.Add(placed[i]);
            }
        for (int i = 0; i < tags.Count; i++)
        {
            var tag = tags[i];
            if (tag.Dragged is not null) continue;
            int? side = null;
            if (mode == TagPlacement.Above && tag.VectorRad is { } a)
            {
                side = BehindSide(a, memory is not null && memory.Side.TryGetValue(tag.Key, out var s) ? s : null);
                if (memory is not null) memory.Side[tag.Key] = side.Value;
            }
            var spots = SpotsFor(tag, mode, side);
            double Cost(Box box) => taken.Sum(o => box.OverlapArea(o)) + symbols.Sum(o => box.OverlapArea(o));
            // The spot it had, while clear.
            if (memory is not null && memory.Spot.TryGetValue(tag.Key, out var last) && last < spots.Count && Cost(At(tag, spots[last])) == 0)
            {
                placed[i] = At(tag, spots[last]);
                taken.Add(placed[i]);
                continue;
            }
            Box best = default;
            var bestCost = double.MaxValue;
            var bestIndex = 0;
            for (int k = 0; k < spots.Count; k++)
            {
                var box = At(tag, spots[k]);
                var cost = Cost(box);
                if (cost < bestCost) (best, bestCost, bestIndex) = (box, cost, k);
                if (cost == 0) break;
            }
            if (memory is not null) memory.Spot[tag.Key] = bestIndex;
            placed[i] = best;
            taken.Add(best);
        }
        return placed;
    }
}
