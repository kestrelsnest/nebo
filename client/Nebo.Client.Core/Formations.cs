namespace Nebo.Client.Core;

/// <summary>Aircraft flying together, shown as one data block: the same side and identity, each
/// within <see cref="Formations.RangeM"/> of another member, with altitude, speed and heading
/// close to that member's. <c>Key</c>: the lowest member id, so a block keeps its key (and the
/// place a controller dragged it to) while members join or leave.</summary>
public sealed record Flight(long Key, IReadOnlyList<Unit> Members)
{
    /// <summary>The member the shared altitude line and the leader line belong to: the lowest id.</summary>
    public Unit Lead => Members[0];
}

public static class Formations
{
    public const double RangeM = 1.5 * Nav.MetersPerNm;
    public const double AltFt = 1000;
    public const double SpeedKt = 50;
    public const double HeadingDeg = 30;

    /// <summary>Whether two aircraft fly in formation (both moving, close in every respect, and in
    /// the same package or neither in one: a block has one colour). <paramref name="packageOf"/>:
    /// a unit's package number, null when in none.</summary>
    public static bool Together(Unit a, Unit b, string own, Func<Unit, int?>? packageOf = null)
    {
        if (packageOf is not null && packageOf(a) != packageOf(b)) return false;
        if (a.Lat is not { } la || a.Lon is not { } lo || b.Lat is not { } lb || b.Lon is not { } ln) return false;
        if (Symbology.IdentityOf(a, own) != Symbology.IdentityOf(b, own) || a.Coalition != b.Coalition) return false;
        if (Math.Abs((a.AltFt ?? 0) - (b.AltFt ?? 0)) > AltFt) return false;
        if (a.SpdKt is not { } sa || b.SpdKt is not { } sb || Math.Abs(sa - sb) > SpeedKt) return false;
        // Slow movers (hovering helicopters) have no meaningful heading: their position decides.
        if (sa > 30 && sb > 30 && a.Hdg is { } ha && b.Hdg is { } hb && Math.Abs(Nav.Normalize(ha - hb + 180) - 180) > HeadingDeg) return false;
        return Nav.BearingRange(la, lo, lb, ln).Meters <= RangeM;
    }

    /// <summary>The aircraft grouped into flights (a lone aircraft is a flight of one), members
    /// by id. Pairs chain: a trail of four 1 NM apart is one flight.</summary>
    public static IReadOnlyList<Flight> Group(IReadOnlyList<Unit> air, string own, Func<Unit, int?>? packageOf = null)
    {
        var sorted = air.OrderBy(u => u.Id).ToList();
        var parent = Enumerable.Range(0, sorted.Count).ToArray();
        int Root(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        for (int i = 0; i < sorted.Count; i++)
            for (int j = i + 1; j < sorted.Count; j++)
                if (Together(sorted[i], sorted[j], own, packageOf))
                {
                    int ri = Root(i), rj = Root(j);
                    if (ri != rj) parent[Math.Max(ri, rj)] = Math.Min(ri, rj);
                }
        return Enumerable.Range(0, sorted.Count)
            .GroupBy(Root)
            .Select(g => g.Select(i => sorted[i]).ToList())
            .Select(m => new Flight(m[0].Id, m))
            .OrderBy(f => f.Key)
            .ToList();
    }

    /// <summary>The flight's data block: one line per member ("Bullat95 F16C"; contacts, which
    /// carry no names, by type: "2× MiG29"), then the lead's altitude line. A flight of one is
    /// the unit's own block.</summary>
    public static IReadOnlyList<string> Block(Flight f, LabelLevel level)
    {
        if (f.Members.Count == 1) return Symbology.DataBlock(f.Lead, level);
        if (level == LabelLevel.None) return [];
        var count = $"{f.Members.Count}×";
        // Short: "2× F16C 250↑" (mixed types joined: "F16C+F18C").
        var types = string.Join("+", f.Members.Select(Symbology.TypeOf).Distinct());
        if (level == LabelLevel.Short) return [$"{count} {types} {Symbology.AltitudeHundreds(f.Lead)}{Symbology.TrendOf(f.Lead)}"];
        var lines = new List<string>();
        if (Symbology.NameOf(f.Lead) is null)
            lines.AddRange(f.Members.GroupBy(Symbology.TypeOf).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()}× {g.Key}"));
        else
            lines.AddRange(f.Members.Select(u => $"{Symbology.NameOf(u)} {Symbology.TypeOf(u)}"));
        lines.Add(Symbology.AltLine(f.Lead));
        return lines;
    }
}
