namespace Nebo.Client.Core;

/// <summary>An end of an engagement line: on a track (it follows it) or a fixed map point.</summary>
public readonly record struct LineEnd(long? TrackId, double Lat, double Lon);

/// <summary>An engagement: a BRAA line the controller pinned. <c>From</c> is the interceptor.
/// <c>Marker</c>: 1–8 (one line per marker, for the Stream Deck); <c>AlertNm</c>: the range at
/// which the engagement alert sounds; <c>ShowIntercept</c>: the intercept point drawn.</summary>
public sealed class EngagementLine(int id, LineEnd from, LineEnd to)
{
    public int Id { get; } = id;
    public LineEnd From { get; } = from;
    public LineEnd To { get; } = to;
    public int? Marker { get; internal set; }
    public double? AlertNm { get; internal set; }
    public bool ShowIntercept { get; internal set; } = true;
    /// <summary>The alert sounds when the range closes to AlertNm while armed; it re-arms once the
    /// range opens <see cref="EngagementBook.RearmNm"/> past it.</summary>
    internal bool Armed { get; set; } = true;
}

/// <summary>One row of the engagement list, as of now: a pinned line or a launch at one of the
/// side's players. Range in metres, closure in knots (positive closing), the intercept when
/// solvable, bearing true from the interceptor (or missile) to the target.</summary>
public sealed record EngagementRow(EngagementLine? Line, Launch? Launch, string From, string To, double RangeM, double BearingTrue,
    double? ClosureKt, InterceptSolution? Intercept, bool Inside)
{
    public bool IsLaunch => Launch is not null;
    public double RangeNm => RangeM / Nav.MetersPerNm;

    /// <summary>Sort key: launches first (by range), then lines by the range left to their alert
    /// distance, or by range when none is set.</summary>
    public double SortKey => IsLaunch ? -1e9 + RangeM : RangeM - (Line?.AlertNm ?? 0) * Nav.MetersPerNm;
}

/// <summary>The controller's engagements (this client; every map draws them) and the side's launches.</summary>
public sealed class EngagementBook
{
    public const int Markers = 8;
    public const double RearmNm = 2;

    /// <summary>A line's urgency for the Stream Deck key colour: "inside" (red) at or inside its
    /// alert distance, "near" (amber) within 10 NM beyond it, "far" (green) further; without an
    /// alert: under 20 NM inside, 20–40 near, beyond far.</summary>
    public static string Level(double rangeNm, double? alertNm) => alertNm is { } a
        ? rangeNm <= a ? "inside" : rangeNm <= a + 10 ? "near" : "far"
        : rangeNm < 20 ? "inside" : rangeNm <= 40 ? "near" : "far";

    private readonly List<EngagementLine> _lines = [];
    private int _nextId = 1;

    public IReadOnlyList<EngagementLine> Lines => _lines;
    public event Action? Changed;

    public EngagementLine Add(LineEnd from, LineEnd to)
    {
        var line = new EngagementLine(_nextId++, from, to);
        _lines.Add(line);
        Changed?.Invoke();
        return line;
    }

    public void Remove(int id)
    {
        if (_lines.RemoveAll(l => l.Id == id) > 0) Changed?.Invoke();
    }

    public void Clear()
    {
        if (_lines.Count == 0) return;
        _lines.Clear();
        Changed?.Invoke();
    }

    public EngagementLine? Find(int id) => _lines.FirstOrDefault(l => l.Id == id);

    /// <summary>Drops every line one of whose ends is on a track that is lost (fading) or gone:
    /// an engagement with a contact nobody holds any more is over. Returns how many went.</summary>
    public int DropLost(PictureStore store)
    {
        bool Lost(LineEnd e) => e.TrackId is { } id && store.Find(id) is not { Lost: false };
        var dropped = _lines.RemoveAll(l => Lost(l.From) || Lost(l.To));
        if (dropped > 0) Changed?.Invoke();
        return dropped;
    }

    /// <summary>Marker 1–8 on a line (null takes it off); a marker is on one line at a time.</summary>
    public void SetMarker(int id, int? marker)
    {
        if (Find(id) is not { } line || marker is < 1 or > Markers) return;
        foreach (var other in _lines.Where(l => marker is not null && l.Marker == marker)) other.Marker = null;
        line.Marker = marker;
        Changed?.Invoke();
    }

    /// <summary>The line carrying a marker, if any (the Stream Deck's button).</summary>
    public EngagementLine? WithMarker(int marker) => _lines.FirstOrDefault(l => l.Marker == marker);

    public void SetAlert(int id, double? nm)
    {
        if (Find(id) is not { } line) return;
        line.AlertNm = nm is > 0 ? nm : null;
        line.Armed = true;
        Changed?.Invoke();
    }

    public void SetShowIntercept(int id, bool show)
    {
        if (Find(id) is not { } line || line.ShowIntercept == show) return;
        line.ShowIntercept = show;
        Changed?.Invoke();
    }

    /// <summary>An end's position now and its unit (null for a map point, or a track that is gone:
    /// then its last known point).</summary>
    public static (double Lat, double Lon, Unit? Unit) Resolve(LineEnd end, Func<long, Unit?> find) =>
        end.TrackId is { } id && find(id) is { Lat: { } lat, Lon: { } lon } u ? (lat, lon, u) : (end.Lat, end.Lon, null);

    /// <summary>Every row as of now, sorted (launches first, see <see cref="EngagementRow.SortKey"/>).</summary>
    public IReadOnlyList<EngagementRow> Rows(PictureStore store)
    {
        Unit? Find(long id) => store.Find(id)?.Unit;
        var rows = new List<EngagementRow>();
        foreach (var launch in store.Launches)
        {
            var missile = store.Missiles.FirstOrDefault(m => m.Weapon.Id == launch.Missile)?.Weapon;
            if (missile is null || Find(launch.Target) is not { Lat: { } tlat, Lon: { } tlon } target) continue;
            var (brg, range) = Nav.BearingRange(missile.Lat, missile.Lon, tlat, tlon);
            rows.Add(new EngagementRow(null, launch, missile.Type ?? "MSL", launch.Player, range, brg,
                Closure(missile.Spd, missile.Hdg, target.Spd, target.Hdg, brg), null, false));
        }
        foreach (var line in _lines)
        {
            var (alat, alon, a) = Resolve(line.From, Find);
            var (blat, blon, b) = Resolve(line.To, Find);
            var (brg, range) = Nav.BearingRange(alat, alon, blat, blon);
            InterceptSolution? icpt = null;
            if (a is { Spd: { } aspd } && b is { Spd: { } bspd, Hdg: { } bhdg } && Symbology.IsAir(Symbology.KindOf(a)) && Symbology.IsAir(Symbology.KindOf(b)))
                icpt = Intercept.Solve(alat, alon, aspd, blat, blon, bhdg, bspd);
            rows.Add(new EngagementRow(line, null, Label(a, line.From), Label(b, line.To), range, brg,
                Closure(a?.Spd, a?.Hdg, b?.Spd, b?.Hdg, brg), icpt, line.AlertNm is { } al && range <= al * Nav.MetersPerNm));
        }
        rows.Sort((x, y) => x.SortKey.CompareTo(y.SortKey));
        return rows;
    }

    /// <summary>Lines whose range has just closed to their alert distance (sound once each); lines
    /// that opened past it again re-arm.</summary>
    public IReadOnlyList<EngagementLine> CheckAlerts(IReadOnlyList<EngagementRow> rows)
    {
        var fired = new List<EngagementLine>();
        foreach (var row in rows)
        {
            if (row.Line is not { AlertNm: { } alert } line) continue;
            if (line.Armed && row.RangeNm <= alert)
            {
                line.Armed = false;
                fired.Add(line);
            }
            else if (!line.Armed && row.RangeNm > alert + RearmNm) line.Armed = true;
        }
        return fired;
    }

    /// <summary>How fast the range closes (kt): each end's speed along the line from a to b.</summary>
    public static double? Closure(double? aSpd, double? aHdg, double? bSpd, double? bHdg, double bearingAtoB)
    {
        if (aSpd is null && bSpd is null) return null;
        double Along(double? spd, double? hdg) => spd is { } s && hdg is { } h ? s * Math.Cos((h - bearingAtoB) * Math.PI / 180) : 0;
        return (Along(aSpd, aHdg) - Along(bSpd, bHdg)) * Unit.KnotsPerMps;
    }

    private static string Label(Unit? u, LineEnd end) =>
        u is not null ? (Symbology.NameOf(u) is { } n ? $"{n} {Symbology.TypeOf(u)}" : Symbology.TypeOf(u))
        : end.TrackId is not null ? "lost" : "point";
}
