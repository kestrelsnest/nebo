using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Nebo.Client.Core;
using Vec = System.Windows.Vector;

namespace Nebo.Client.Map.Airfields;

/// <summary>NEBO's airport diagram for one airfield as a document page in the style of printed
/// airport diagrams: header (field, ICAO, position, elevation, variation), the plan (pavement,
/// runways with heading arrows and ILS feathers, one taxiway letter per leg, stand groups,
/// tower and navaids, live traffic), an information block (frequencies, navaids, runways with
/// ILS) and a footer. All geometry and numbers are DCS data — pavement from the terrain mesh,
/// taxiway network and letters from the field's rn4 and its signs, navaids from beacons.lua,
/// runways and stands from the server; ICAO codes from the C-130J's nav database; area names and
/// letters for unsigned taxiways from placed charts (local only). Imperial units. True north up;
/// pan/zoom inside the plan frame, clamped to the field.</summary>
public sealed class AirfieldDiagramView : FrameworkElement
{
    private const double MarginM = 250;           // around the field's extent
    private const double MaxScale = 6;            // DIP per metre, closest zoom
    private const double LiveRadiusM = 6000;      // traffic drawn within this distance of the field
    private const double SignRadiusM = 4500;      // DCS signs looked up around the field
    private const double StandNumbersScale = 0.9; // DIP per metre from which single stand numbers replace groups
    private const double StandClusterM = 150;     // stands this close (chained) form one group
    private const double LabelEveryM = 1500;      // a long taxiway leg repeats its letter
    private const double FeetPerMeter = 3.28084;
    private const double MetersPerNm = 1852;

    // Page layout (DIP).
    private const double PageInset = 8, HeaderH = 54, FooterH = 20, InfoW = 270, InfoH = 150, Pad = 8;

    private static readonly Brush Desk = Frozen(Color.FromRgb(0xD4, 0xD4, 0xD0));
    private static readonly Brush Paper = Frozen(Colors.White);
    private static readonly Brush Pavement = Frozen(Color.FromRgb(0xC4, 0xBF, 0xB3));
    private static readonly Brush PavementEdge = Frozen(Color.FromRgb(0x6E, 0x69, 0x5F));
    private static readonly Brush TaxiLine = Frozen(Color.FromRgb(0xC9, 0x9A, 0x1E));
    private static readonly Brush FieldGround = Frozen(Color.FromRgb(0xF1, 0xF0, 0xEA));
    private static readonly Brush FieldEdge = Frozen(Color.FromRgb(0xB8, 0xB4, 0xA8));
    private static readonly Brush RunwayFill = Frozen(Colors.Black);
    private static readonly Brush Marking = Frozen(Colors.White);
    private static readonly Brush Ink = Frozen(Colors.Black);
    private static readonly Brush StandBadge = Frozen(Color.FromRgb(0x2E, 0x5C, 0x9A));
    private static readonly Brush Muted = Frozen(Color.FromRgb(0x5A, 0x5A, 0x5A));
    private static readonly Brush NavaidInk = Frozen(Color.FromRgb(0x6A, 0x2C, 0x8A));
    private static readonly Brush Warning = Frozen(Color.FromRgb(0xC0, 0x10, 0x10));
    private static readonly Brush HaloLight = Frozen(Colors.White);

    /// <summary>A taxiway name placed on the plan; <c>FromChart</c> when DCS has no sign for it.</summary>
    private sealed record TaxiLabel(string Text, double X, double Z, bool FromChart, double LegLength);

    private Airfield? _field;
    private Theatre? _theatre;
    private LocalFrame? _frame;
    private (double MinE, double MinN, double MaxE, double MaxN) _extent;
    private double _scale;            // DIP per metre
    private double _centerE, _centerN; // view centre in local metres (east, north of origin)
    private Point? _dragFrom;
    private bool _userView;            // zoomed or panned by the controller since the last fit
    private Geometry? _pavement;       // local metres (east, −north), frozen
    private List<(double WidthM, Geometry Lines)> _network = [];  // taxiway network at its widths (centre lines)
    private List<(double WidthM, Geometry Lines)> _networkFill = [];  // the part the mesh leaves out, drawn as pavement
    private Dictionary<Runway, (double Near, double Far)> _overruns = [];  // paved metres beyond each runway end
    private bool _slab;  // the terrain's "pavement" mesh is a slab under the whole field

    /// <summary>Terrains whose Runway mesh is one textured slab per airfield, not the pavement
    /// (docs/DCS_MAP_FORMAT.md): their taxiways come from the taxi network instead.</summary>
    private static readonly HashSet<string> SlabTerrains = new(StringComparer.OrdinalIgnoreCase) { "MarianaIslands" };
    private IReadOnlyList<(IReadOnlyList<int> Numbers, double X, double Z)> _stands = [];
    private readonly List<Rect> _placed = [];  // labels drawn this frame: nothing is drawn over them
    private IReadOnlyList<TaxiLabel> _taxiLabels = [];
    private IReadOnlyList<DiagramLabel> _areas = [];
    private IReadOnlyList<Beacon> _beacons = [];
    private (double X, double Z)? _tower;
    private string? _icao;

    public IReadOnlyList<DiagramLabel> Labels { get; set; } = [];

    /// <summary>Printed sheet (chart builder): no traffic, a NATOPS-style frame around the page
    /// (running header, page footer) and a runway data block, one line per runway end.</summary>
    public bool Sheet { get; set; }
    public string SheetDocument { get; set; } = "";
    public string SheetPage { get; set; } = "";

    // Sheet margins (DIP) and the runway data block: title, column heads, one row per end.
    private const double SheetSide = 20, SheetTop = 40, SheetBottom = 36, RowH = 13;
    private double TableH => Sheet && _field is { } f ? 48 + RowH * 2 * f.Runways.Count : 0;

    /// <summary>The information block's height: on a sheet, tall enough for every navaid line.</summary>
    private double InfoHeight => Sheet
        ? Math.Max(InfoH, 50 + 15 * _beacons.Count(b => !b.IsLocalizer && !b.IsGlideslope && !b.IsTacan && !b.Type.StartsWith("VOR")))
        : InfoH;

    /// <summary>Where the taxiway letters come from: "DCS signs", "DCS signs + chart", "chart" or "".</summary>
    public string LetterSource { get; private set; } = "";

    public AirfieldDiagramView()
    {
        ClipToBounds = true;
        Focusable = true;
        Taxiways.Loaded += () => Dispatcher.BeginInvoke(() => { if (_field is not null) { Show(_field); } });
        DcsAirfieldData.Loaded += () => Dispatcher.BeginInvoke(() => { if (_field is not null) { Show(_field); } });
        // Until the controller zooms or pans, a resize (also the first layout) refits the field.
        SizeChanged += (_, _) =>
        {
            if (_field is not null && !_userView) Fit();
            else ClampView();
            InvalidateVisual();
        };
    }

    public Airfield? Field => _field;

    /// <summary>Snapshot aid: field, extent, view and scale.</summary>
    public string DebugState =>
        $"{_field?.Name} ({_icao}) extent E {_extent.MinE:F0}..{_extent.MaxE:F0} N {_extent.MinN:F0}..{_extent.MaxN:F0} plan {Plan.Width:F0}x{Plan.Height:F0} " +
        $"scale {_scale:F3} fit {FitScale:F3} userView {_userView} letters {LetterSource} ({_taxiLabels.Count}) stands {_stands.Count} " +
        $"pavement {_pavement is not null} network {_network.Count} (fill {_networkFill.Count}) beacons {_beacons.Count} tower {_tower is not null}";

    /// <summary>True when the field is drawn on the theatre now loaded. A field picked before the
    /// theatre arrived (or before the server changed map) is not: show it again then.</summary>
    public bool Shows(Airfield field) =>
        _field?.Id == field.Id && _frame is not null && ReferenceEquals(_theatre, AppState.Store.Theatre);

    /// <summary>Shows an airfield, fitted to the plan frame.</summary>
    public void Show(Airfield field, IReadOnlyList<DiagramLabel>? labels = null)
    {
        _field = field;
        _theatre = AppState.Store.Theatre;
        if (labels is not null) Labels = labels;
        if (_theatre is null) return;
        var id = _theatre.Id;
        var (ox, oz) = field.Runways.Count > 0 ? (field.Runways.Average(r => r.X), field.Runways.Average(r => r.Z)) : (field.X, field.Z);
        _frame = new LocalFrame(_theatre, ox, oz);
        var graph = DcsAirfieldData.GraphFor(id, field.Name, ox, oz);
        _extent = Extent(field, graph);
        _pavement = BuildPavement(id, ox, oz);
        _network = BuildNetwork(graph);
        // The rn4 lines are not exactly on the mesh: where the mesh has the taxiway, the line at
        // its width would bulge past the mesh's edges, so only legs off the mesh are filled.
        var mesh = DcsAirfieldData.PavementFor(id);
        _slab = SlabTerrains.Contains(id);
        _networkFill = _slab ? BuildNetwork(graph) : BuildNetwork(graph, e => !OnMesh(e.Xz, mesh));
        // On a slab every runway end looks paved: no overruns there.
        _overruns = _slab ? [] : field.Runways.ToDictionary(r => r, r => (Overrun(r, -1, mesh), Overrun(r, 1, mesh)));
        _stands = StandGroups(field.Parking);
        _areas = Labels.Where(l => l.Kind == DiagramLabel.Area).ToList();
        _taxiLabels = TaxiLabels(field, graph, ox, oz);
        _beacons = DcsAirfieldData.BeaconsFor(id, field.Id);
        // The tower: a tower model DCS places, else the chart's "TOWER" label (local only).
        _tower = DcsAirfieldData.TowerNear(id, ox, oz)
            ?? _areas.Where(a => a.Text.Contains("TOWER", StringComparison.OrdinalIgnoreCase)).Select(a => ((double X, double Z)?)(a.X, a.Z)).FirstOrDefault();
        var (lat, lon) = _theatre.ToLatLon(ox, oz);
        _icao = DcsAirfieldData.IcaoFor(id, lat, lon, field.Runways.Select(TrueHeading).ToList()) ?? ChartLabels.IcaoFor(field);
        Fit();
    }

    // ---- Content -----------------------------------------------------------------------------

    /// <summary>One label per taxiway leg (between turns): legs named by DCS's location signs
    /// (each sign board votes for its nearest leg), legs DCS has no sign on named by the chart's
    /// letters (grey); long legs repeat the letter every ~1.5 km. Without the field's network,
    /// the chart's letters where printed.</summary>
    private List<TaxiLabel> TaxiLabels(Airfield field, TaxiwayGraph? graph, double ox, double oz)
    {
        var chart = Labels.Where(l => l.Kind == DiagramLabel.Taxiway).ToList();
        if (graph is null)
        {
            LetterSource = chart.Count > 0 ? "chart" : "";
            return chart.Select(l => new TaxiLabel(l.Text, l.X, l.Z, false, 0)).ToList();
        }
        var others = AppState.Store.Airfields.Where(a => a.Id != field.Id && !a.IsHelipad).ToList();
        var boards = (DcsAirfieldData.SignBoardsNear(_theatre!.Id, ox, oz, SignRadiusM) ?? [])
            .Where(s =>
            {
                // Only this field's signs: closer to it than to any other airdrome.
                var d = (s.X - ox) * (s.X - ox) + (s.Z - oz) * (s.Z - oz);
                return others.All(a => (s.X - a.X) * (s.X - a.X) + (s.Z - a.Z) * (s.Z - a.Z) > d);
            })
            .ToList();
        var legs = graph.Legs();
        var fromSigns = TaxiwayGraph.Name(legs, boards.Select(b => (b.Text, b.X, b.Z)));
        var named = fromSigns.Select(n => n.Leg).ToHashSet();
        var signNames = fromSigns.Select(n => n.Name).ToHashSet();
        var fromChart = TaxiwayGraph.Name(legs.Where(l => !named.Contains(l)).ToList(),
            chart.Where(l => !signNames.Contains(l.Text)).Select(l => (l.Text, l.X, l.Z)), reachM: 80);
        var mixed = fromSigns.Count > 0 && fromChart.Count > 0;
        LetterSource = fromSigns.Count > 0 ? (mixed ? "DCS signs + chart" : "DCS signs") : fromChart.Count > 0 ? "chart" : "";

        var labels = new List<TaxiLabel>();
        void Place(TaxiwayGraph.Leg leg, string name, bool grey)
        {
            var count = Math.Max(1, (int)Math.Round(leg.Length / LabelEveryM));
            for (int i = 0; i < count; i++)
            {
                var (x, z) = TaxiwayGraph.PointAt(leg.Xz, leg.Length * (i + 0.5) / count);
                labels.Add(new TaxiLabel(name, x, z, grey, leg.Length));
            }
        }
        foreach (var (leg, name) in fromSigns) Place(leg, name, false);
        foreach (var (leg, name) in fromChart) Place(leg, name, mixed);
        return labels;
    }

    /// <summary>DCS's pavement around the field as one frozen geometry in local metres (east,
    /// −north), so a transform maps it to the screen for any zoom; null without terrain data.</summary>
    private Geometry? BuildPavement(string terrainId, double ox, double oz)
    {
        if (DcsAirfieldData.PavementFor(terrainId) is not { } pavement) return null;
        double minE = _extent.MinE - 500, maxE = _extent.MaxE + 500, minN = _extent.MinN - 500, maxN = _extent.MaxN + 500;
        var g = new StreamGeometry { FillRule = FillRule.Nonzero };
        var any = false;
        using (var c = g.Open())
        {
            var reach = Math.Max(maxE - minE, maxN - minN);
            foreach (var bucket in pavement.Near(ox, oz, reach))
                for (int i = 0; i + 5 < bucket.Length; i += 6)
                {
                    var (e0, n0) = _frame!.Local(bucket[i], bucket[i + 1]);
                    if (e0 < minE || e0 > maxE || n0 < minN || n0 > maxN) continue;
                    var (e1, n1) = _frame.Local(bucket[i + 2], bucket[i + 3]);
                    var (e2, n2) = _frame.Local(bucket[i + 4], bucket[i + 5]);
                    c.BeginFigure(new Point(e0, -n0), true, true);
                    // Every side stroked: the inked edge strokes all triangles under the fill.
                    c.LineTo(new Point(e1, -n1), true, false);
                    c.LineTo(new Point(e2, -n2), true, false);
                    any = true;
                }
        }
        g.Freeze();
        return any ? g : null;
    }

    /// <summary>The taxiway network at the widths DCS gives its edges (taxiway_23m…), grouped by
    /// width — drawn under the mesh pavement, it fills taxiways the mesh leaves out.</summary>
    private List<(double, Geometry)> BuildNetwork(TaxiwayGraph? graph, Func<TaxiwayGraph.Edge, bool>? keep = null)
    {
        var result = new List<(double, Geometry)>();
        if (graph is null) return result;
        foreach (var group in graph.Edges.Where(e => !e.IsRunway && (keep?.Invoke(e) ?? true)).GroupBy(e => e.WidthM))
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
                foreach (var e in group)
                {
                    var (e0, n0) = _frame!.Local(e.Xz[0], e.Xz[1]);
                    c.BeginFigure(new Point(e0, -n0), false, false);
                    for (int i = 2; i + 1 < e.Xz.Length; i += 2)
                    {
                        var (ei, ni) = _frame.Local(e.Xz[i], e.Xz[i + 1]);
                        c.LineTo(new Point(ei, -ni), true, true);
                    }
                }
            g.Freeze();
            result.Add((group.Key, g));
        }
        return result;
    }

    /// <summary>How far DCS's pavement runs on beyond a runway end along its centre line (side
    /// −1: the near end, +1: the far end), metres in 10 m steps up to 400 m: the overrun or
    /// stopway the runway's own length leaves out.</summary>
    private static double Overrun(Runway rw, int side, Pavement? pavement)
    {
        if (pavement is null) return 0;
        var h = rw.HeadingTrue * Math.PI / 180;
        double ux = Math.Cos(h) * side, uz = Math.Sin(h) * side;
        double ex = rw.X + ux * rw.Length / 2, ez = rw.Z + uz * rw.Length / 2;
        var d = 0.0;
        while (d < 400)
        {
            double x = ex + ux * (d + 10), z = ez + uz * (d + 10);
            if (!pavement.Near(x, z, 0).Any(b => InTriangles(b, x, z))) break;
            d += 10;
        }
        return d;
    }

    /// <summary>True when a taxiway line (x, z pairs) runs on the mesh pavement: nine in ten
    /// points sampled every 8 m lie in a pavement triangle.</summary>
    private static bool OnMesh(double[] xz, Pavement? pavement)
    {
        if (pavement is null || xz.Length < 4) return false;
        int inside = 0, total = 0;
        for (int i = 0; i + 3 < xz.Length; i += 2)
        {
            double x0 = xz[i], z0 = xz[i + 1], x1 = xz[i + 2], z1 = xz[i + 3];
            var steps = Math.Max(1, (int)(Math.Sqrt((x1 - x0) * (x1 - x0) + (z1 - z0) * (z1 - z0)) / 8));
            for (int s = 0; s < steps; s++)
            {
                double x = x0 + (x1 - x0) * s / steps, z = z0 + (z1 - z0) * s / steps;
                total++;
                if (pavement.Near(x, z, 0).Any(b => InTriangles(b, x, z))) inside++;
            }
        }
        return inside >= 0.9 * total;
    }

    private static bool InTriangles(float[] b, double x, double z)
    {
        for (int i = 0; i + 5 < b.Length; i += 6)
        {
            double ax = b[i], az = b[i + 1], bx = b[i + 2], bz = b[i + 3], cx = b[i + 4], cz = b[i + 5];
            var d1 = (x - bx) * (az - bz) - (ax - bx) * (z - bz);
            var d2 = (x - cx) * (bz - cz) - (bx - cx) * (z - cz);
            var d3 = (x - ax) * (cz - az) - (cx - ax) * (z - az);
            if (!((d1 < 0 || d2 < 0 || d3 < 0) && (d1 > 0 || d2 > 0 || d3 > 0))) return true;
        }
        return false;
    }

    /// <summary>Stand groups as on printed diagrams: stands standing within 150 m of each other
    /// (chained) form one group, labelled with its number ranges ("21-38", "2-4, 9") at its middle;
    /// groups whose badges would touch at the current zoom are merged when drawn.</summary>
    public static IReadOnlyList<(IReadOnlyList<int> Numbers, double X, double Z)> StandGroups(IReadOnlyList<ParkingSpot> spots)
    {
        var parent = Enumerable.Range(0, spots.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        for (int i = 0; i < spots.Count; i++)
            for (int j = i + 1; j < spots.Count; j++)
            {
                double dx = spots[i].X - spots[j].X, dz = spots[i].Z - spots[j].Z;
                if (dx * dx + dz * dz < StandClusterM * StandClusterM) parent[Find(i)] = Find(j);
            }
        return Enumerable.Range(0, spots.Count).GroupBy(Find)
            .Select(g => g.Select(i => spots[i]).ToList())
            .Select(g => ((IReadOnlyList<int>)g.Select(s => s.Number).ToList(), g.Average(s => s.X), g.Average(s => s.Z)))
            .ToList();
    }

    /// <summary>"1 2 3 7 9 10" → "1-3, 7, 9-10"; more than three runs → "1-10".</summary>
    public static string Ranges(IEnumerable<int> numbers)
    {
        var n = numbers.Distinct().Order().ToList();
        var runs = new List<(int A, int B)>();
        foreach (var v in n)
            if (runs.Count > 0 && runs[^1].B + 1 == v) runs[^1] = (runs[^1].A, v);
            else runs.Add((v, v));
        if (runs.Count > 3) return $"{n[0]}-{n[^1]}";
        return string.Join(", ", runs.Select(r => r.A == r.B ? $"{r.A}" : $"{r.A}-{r.B}"));
    }

    // ---- Page layout ---------------------------------------------------------------------------

    private Rect Page => Sheet
        ? Box(SheetSide, SheetTop, ActualWidth - 2 * SheetSide, ActualHeight - SheetTop - SheetBottom)
        : Box(PageInset, PageInset, Math.Max(1, ActualWidth - 2 * PageInset), Math.Max(1, ActualHeight - 2 * PageInset));
    private bool InfoAside => !Sheet && Page.Width >= 900;

    /// <summary>The plan frame: below the header, beside or above the information block.</summary>
    private Rect Plan
    {
        get
        {
            var p = Page;
            double top = p.Top + HeaderH, bottom = p.Bottom - FooterH - TableH - (InfoAside ? 0 : InfoHeight);
            double right = p.Right - (InfoAside ? InfoW : 0);
            return Box(p.Left + Pad, top, Math.Max(1, right - p.Left - 2 * Pad), Math.Max(1, bottom - top - Pad));
        }
    }

    private Rect Info
    {
        get
        {
            var p = Page;
            return InfoAside
                ? Box(p.Right - InfoW, p.Top + HeaderH, InfoW - Pad, p.Height - HeaderH - FooterH - Pad)
                : Box(p.Left + Pad, p.Bottom - FooterH - TableH - InfoHeight, p.Width - 2 * Pad, InfoHeight - Pad);
        }
    }

    /// <summary>The runway data block of a sheet, between the information block and the footer.</summary>
    private Rect Table
    {
        get
        {
            var p = Page;
            return Box(p.Left + Pad, p.Bottom - FooterH - TableH, p.Width - 2 * Pad, TableH - Pad);
        }
    }

    /// <summary>A rectangle that is never negative: docking panes pass through tiny sizes while
    /// they are dragged, and WPF's Rect throws on a negative size.</summary>
    private static Rect Box(double x, double y, double w, double h) => new(x, y, Math.Max(0, w), Math.Max(0, h));

    /// <summary>Below this the page layout does not fit; a note is drawn instead.</summary>
    private bool TooSmall => ActualWidth < 420 || ActualHeight < 300;

    // ---- View ----------------------------------------------------------------------------------

    /// <summary>Whole field in the plan frame.</summary>
    public void Fit()
    {
        _userView = false;
        _scale = 0;
        ClampView();
        _centerE = (_extent.MinE + _extent.MaxE) / 2;
        _centerN = (_extent.MinN + _extent.MaxN) / 2;
        InvalidateVisual();
    }

    private double FitScale =>
        ActualWidth < 1 || ActualHeight < 1 ? 1
        : Math.Min(Plan.Width / (_extent.MaxE - _extent.MinE), Plan.Height / (_extent.MaxN - _extent.MinN));

    private void ClampView()
    {
        var min = FitScale;
        _scale = Math.Clamp(_scale, min, Math.Max(min, MaxScale));
        double halfE = Plan.Width / 2 / _scale, halfN = Plan.Height / 2 / _scale;
        double loE = _extent.MinE + halfE, hiE = _extent.MaxE - halfE;
        double loN = _extent.MinN + halfN, hiN = _extent.MaxN - halfN;
        _centerE = loE <= hiE ? Math.Clamp(_centerE, loE, hiE) : (_extent.MinE + _extent.MaxE) / 2;
        _centerN = loN <= hiN ? Math.Clamp(_centerN, loN, hiN) : (_extent.MinN + _extent.MaxN) / 2;
    }

    private (double MinE, double MinN, double MaxE, double MaxN) Extent(Airfield field, TaxiwayGraph? graph)
    {
        var pts = new List<(double E, double N)>();
        foreach (var rw in field.Runways)
        {
            var h = rw.HeadingTrue * Math.PI / 180;
            double dx = Math.Cos(h) * rw.Length / 2, dz = Math.Sin(h) * rw.Length / 2;
            pts.Add(_frame!.Local(rw.X + dx, rw.Z + dz));
            pts.Add(_frame.Local(rw.X - dx, rw.Z - dz));
        }
        foreach (var p in field.Parking) pts.Add(_frame!.Local(p.X, p.Z));
        if (graph is not null)
            foreach (var e in graph.Edges)
                for (int i = 0; i + 1 < e.Xz.Length; i += 2) pts.Add(_frame!.Local(e.Xz[i], e.Xz[i + 1]));
        if (pts.Count == 0) pts.Add((0, 0));
        return (pts.Min(p => p.E) - MarginM, pts.Min(p => p.N) - MarginM, pts.Max(p => p.E) + MarginM, pts.Max(p => p.N) + MarginM);
    }

    private Point Screen(double e, double n)
    {
        var plan = Plan;
        return new(plan.Left + plan.Width / 2 + (e - _centerE) * _scale, plan.Top + plan.Height / 2 - (n - _centerN) * _scale);
    }

    private Point ScreenXz(double x, double z)
    {
        var (e, n) = _frame!.Local(x, z);
        return Screen(e, n);
    }

    /// <summary>A runway's heading from true north (DCS gives it from grid north).</summary>
    private double TrueHeading(Runway rw) => Nav.Normalize(rw.HeadingTrue + (_frame?.GridNorthBearing ?? 0));

    private double? Variation(double x, double z)
    {
        if (_theatre?.MagVar is not { } mv) return null;
        var (lat, lon) = _theatre.ToLatLon(x, z);
        return mv.At(lat, lon);
    }

    private string MagText(double trueDeg, double x, double z) =>
        Variation(x, z) is { } v ? $"{Nav.Bearing3(Nav.Magnetic(trueDeg, v))}°" : $"{Nav.Bearing3(trueDeg)}°T";

    private double? ElevationFt(double x, double z) =>
        AppState.Store.Terrain is { } grid && LineOfSight.HeightAt(grid, x, z) is { } h ? h * FeetPerMeter : null;

    // ---- Rendering -----------------------------------------------------------------------------

    protected override void OnRender(DrawingContext dc)
    {
        try
        {
            Render(dc);
        }
        catch (Exception ex)
        {
            // A drawing fault must never take the app down: log it, show a note.
            Map.Gpu.MapSurface.Log($"airfield diagram: {ex}");
            dc.DrawRectangle(Desk, null, Box(0, 0, ActualWidth, ActualHeight));
        }
    }

    private void Render(DrawingContext dc)
    {
        dc.DrawRectangle(Sheet ? Paper : Desk, null, Box(0, 0, ActualWidth, ActualHeight));
        if (TooSmall)
        {
            DrawCentered(dc, "Enlarge the pane to see the diagram", new Point(ActualWidth / 2, ActualHeight / 2), Muted, 12, false);
            return;
        }
        var page = Page;
        dc.DrawRectangle(Paper, new Pen(Muted, 0.6), page);
        if (_field is not { } field || _frame is null || _theatre is not { } theatre)
        {
            DrawCentered(dc, "Select an airfield", new Point(ActualWidth / 2, ActualHeight / 2), Muted, 14, true);
            return;
        }
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var plan = Plan;
        dc.PushClip(new RectangleGeometry(plan));
        var toScreen = new MatrixTransform(_scale, 0, 0, _scale, plan.Left + plan.Width / 2 - _centerE * _scale, plan.Top + plan.Height / 2 + _centerN * _scale);
        dc.PushTransform(toScreen);
        // Pavement with an inked edge: everything first in the edge tone a little wider (each mesh
        // triangle stroked), then filled on top, so only the outer rim of the edge stays.
        var edge = 0.8 / _scale;  // thin: DCS's edge strips are saw-toothed
        Pen Round(Brush b, double w) => new(b, w) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        if (_slab && _pavement is not null)
        {
            // A slab terrain's mesh is the whole airfield (the pavement is in its texture): a faint
            // outline of the field, the taxi network on it as the pavement.
            dc.DrawGeometry(null, Round(FieldEdge, 2 * edge), _pavement);
            dc.DrawGeometry(FieldGround, null, _pavement);
        }
        foreach (var (width, lines) in _networkFill) dc.DrawGeometry(null, Round(PavementEdge, width + 2 * edge), lines);
        if (_pavement is not null && !_slab) dc.DrawGeometry(null, Round(PavementEdge, 2 * edge), _pavement);
        foreach (var (width, lines) in _networkFill) dc.DrawGeometry(null, Round(Pavement, width), lines);
        if (_pavement is not null && !_slab) dc.DrawGeometry(Pavement, null, _pavement);
        // Taxiway centre lines, dashed in taxi yellow.
        var centre = new Pen(TaxiLine, 0.9 / _scale) { DashStyle = new DashStyle([5, 3.5], 0), DashCap = PenLineCap.Flat };
        foreach (var (_, lines) in _network) dc.DrawGeometry(null, centre, lines);
        dc.Pop();

        // Labels in order of importance; each takes a free spot or gives way (_placed).
        _placed.Clear();
        // The north arrow and the scale bar own their corners.
        Take(new Rect(plan.Right - 62, plan.Top + 4, 58, 70));
        Take(new Rect(plan.Left + 4, plan.Bottom - 26, 230, 24));
        foreach (var rw in field.Runways) DrawRunway(dc, field, rw, dpi);
        foreach (var b in _beacons.Where(b => b.IsLocalizer)) DrawIlsFeather(dc, field, b, dpi);
        DrawNavaids(dc, dpi);
        DrawTaxiLabels(dc, dpi);
        DrawStands(dc, field, dpi);
        DrawAreas(dc, dpi);
        if (!Sheet) DrawTraffic(dc, dpi);
        DrawNorthArrow(dc, field, dpi);
        DrawScaleBar(dc, dpi);
        dc.Pop();
        dc.DrawRectangle(null, new Pen(Ink, 1.2), plan);

        DrawHeader(dc, field, theatre, dpi);
        DrawInfo(dc, field, theatre, dpi);
        DrawFooter(dc, theatre, dpi);
        if (Sheet)
        {
            DrawRunwayTable(dc, field, theatre, dpi);
            DrawSheetFrame(dc, field, dpi);
        }
    }

    /// <summary>One line per runway end, what a landing needs: magnetic (and true) heading,
    /// length and width, threshold position and elevation, the ILS.</summary>
    private void DrawRunwayTable(DrawingContext dc, Airfield field, Theatre theatre, double dpi)
    {
        var r = Table;
        dc.DrawRectangle(null, new Pen(Ink, 1), r);
        var x = r.Left + 8;
        dc.DrawText(Format("RUNWAY DATA", Ink, 10, bold: true, dpi), new Point(x, r.Top + 5));
        double[] col = [0, 50, 130, 240, 440, 510];
        string[] heads = ["RWY", "HDG", "LENGTH X WIDTH FT", "THRESHOLD", "THR ELEV", "ILS"];
        var y = r.Top + 21;
        for (int i = 0; i < col.Length; i++) dc.DrawText(Format(heads[i], Muted, 8.5, bold: true, dpi), new Point(x + col[i], y));
        dc.DrawLine(new Pen(Muted, 0.6), new Point(x, y + 13), new Point(r.Right - 8, y + 13));
        y += 15;
        foreach (var rw in field.Runways)
        {
            var (near, far) = Names(field, rw);
            var trueHdg = TrueHeading(rw);
            var h = rw.HeadingTrue * Math.PI / 180;
            double ux = Math.Cos(h) * rw.Length / 2, uz = Math.Sin(h) * rw.Length / 2;
            foreach (var (des, hdg, ex, ez) in new[] { (near, trueHdg, rw.X - ux, rw.Z - uz), (far, Nav.Normalize(trueHdg + 180), rw.X + ux, rw.Z + uz) })
            {
                var (lat, lon) = theatre.ToLatLon(ex, ez);
                var ils = _beacons.Where(b => b.IsLocalizer && RunwayFor(field, b) == rw && LandingEnd(field, rw, b) == des)
                    .Select(b => $"{b.Callsign} {b.FrequencyText}  CRS {MagText(Nav.Normalize(b.InboundCourseTrue + (_frame?.GridNorthBearing ?? 0)), b.X, b.Z)}").FirstOrDefault();
                string[] cells =
                [
                    des, $"{MagText(hdg, rw.X, rw.Z)} ({Nav.Bearing3(hdg)}T)",
                    $"{rw.Length * FeetPerMeter:0} X {rw.Width * FeetPerMeter:0}", Coordinates.Ddm(lat, lon),
                    ElevationFt(ex, ez) is { } e ? $"{e:0}" : "—", ils ?? "—",
                ];
                for (int i = 0; i < col.Length; i++)
                    dc.DrawText(Format(cells[i], i == 5 && ils is not null ? NavaidInk : Ink, 9.5, bold: true, dpi), new Point(x + col[i], y));
                y += RowH;
            }
        }
    }

    /// <summary>The NATOPS-style frame around a sheet's page: running header, page footer.</summary>
    private void DrawSheetFrame(DrawingContext dc, Airfield field, double dpi)
    {
        double left = SheetSide, right = ActualWidth - SheetSide;
        var rule = new Pen(Ink, 1);
        var doc = Format(SheetDocument, Ink, 10, bold: true, dpi);
        dc.DrawText(doc, new Point(left, 14));
        var title = Format("AIRFIELD DIAGRAMS", Ink, 10, bold: true, dpi);
        dc.DrawText(title, new Point(right - title.Width, 14));
        dc.DrawLine(rule, new Point(left, 32), new Point(right, 32));

        var y = ActualHeight - 24;
        dc.DrawLine(rule, new Point(left, y - 6), new Point(right, y - 6));
        dc.DrawText(Format("ORIGINAL", Ink, 10, bold: true, dpi), new Point(left, y));
        var page = Format(SheetPage, Ink, 11, bold: true, dpi);
        dc.DrawText(page, new Point(right - page.Width, y - 1));
        DrawCentered(dc, "NOT FOR REAL NAVIGATION · DCS WORLD SIMULATION ONLY", new Point(ActualWidth / 2, y + 7), Warning, 9.5, true);
    }

    /// <summary>Black runway with a white edge, designators beyond each end, heading arrows at
    /// the thresholds (magnetic) and "length X width" in feet.</summary>
    private void DrawRunway(DrawingContext dc, Airfield field, Runway rw, double dpi)
    {
        var h = rw.HeadingTrue * Math.PI / 180;
        double ax = Math.Cos(h), az = Math.Sin(h), cx = -Math.Sin(h), cz = Math.Cos(h);
        double hl = rw.Length / 2, hw = rw.Width / 2;
        Point P(double along, double across) => ScreenXz(rw.X + ax * along + cx * across, rw.Z + az * along + cz * across);
        DrawOverruns(dc, rw, P);
        var outline = Poly(P(-hl, -hw), P(hl, -hw), P(hl, hw), P(-hl, hw));
        dc.DrawGeometry(RunwayFill, new Pen(Ink, 1), outline);
        var inset = Math.Min(hw * 0.25, 1.5 / Math.Max(_scale, 1e-6));
        dc.DrawGeometry(null, new Pen(Marking, 0.8), Poly(P(-hl + inset, -hw + inset), P(hl - inset, -hw + inset), P(hl - inset, hw - inset), P(-hl + inset, hw - inset)));

        Point a = P(-hl, 0), b = P(hl, 0);
        var screenHeading = Math.Atan2(b.X - a.X, -(b.Y - a.Y)) * 180 / Math.PI;
        var (near, far) = Names(field, rw);
        DrawMarkings(dc, rw, P, screenHeading, near, far, dpi);
        var off = 12 / _scale + 10;
        var size = Math.Clamp(14 * Math.Sqrt(_scale), 10, 22);
        // Beyond the overruns, so the numbers do not sit on their chevrons.
        var (runNear, runFar) = _overruns.GetValueOrDefault(rw);
        Point nearAt = P(-hl - off - runNear, 0), farAt = P(hl + off + runFar, 0);
        DrawRotated(dc, near, nearAt, screenHeading, Ink, size, dpi);
        DrawRotated(dc, far, farAt, screenHeading + 180, Ink, size, dpi);
        foreach (var end in new[] { nearAt, farAt })
            Take(new Rect(end.X - size * 1.4, end.Y - size * 1.4, size * 2.8, size * 2.8));
        // The thresholds' paint (keys, designator) stays clear of labels too.
        var keys = Math.Max(hw * _scale, 60 * _scale);
        foreach (var end in new[] { P(-hl + 60, 0), P(hl - 60, 0) })
            Take(new Rect(end.X - keys, end.Y - keys, keys * 2, keys * 2));

        // Heading arrows beside each threshold, pointing the landing direction (magnetic).
        var side = hw + 14 / _scale;
        var len = 46 / _scale;
        var trueHdg = TrueHeading(rw);
        DrawHeadingArrow(dc, P(-hl + 30 / _scale, -side), P(-hl + 30 / _scale + len, -side), MagText(trueHdg, rw.X, rw.Z), dpi);
        DrawHeadingArrow(dc, P(hl - 30 / _scale, side), P(hl - 30 / _scale - len, side), MagText(Nav.Normalize(trueHdg + 180), rw.X, rw.Z), dpi);

        var dims = $"{rw.Length * FeetPerMeter:0} X {rw.Width * FeetPerMeter:0}";
        var angle = Upright(screenHeading - 90);
        var at = P(0, -(hw + 12 / _scale + 4));
        var ft = Format(dims, Ink, 10, bold: true, dpi);
        var bounds = new RotateTransform(angle, at.X, at.Y).TransformBounds(new Rect(at.X - ft.Width / 2, at.Y - ft.Height / 2, ft.Width, ft.Height));
        Take(bounds);
        dc.PushTransform(new RotateTransform(angle, at.X, at.Y));
        dc.DrawText(ft, new Point(at.X - ft.Width / 2, at.Y - ft.Height / 2));
        dc.Pop();
    }

    /// <summary>The pavement DCS lays on past a runway's ends as a marked overrun: the runway's
    /// width in pavement grey with an inked edge and chevrons pointing at the threshold, as
    /// printed charts mark blast pads and stopways.</summary>
    private void DrawOverruns(DrawingContext dc, Runway rw, Func<double, double, Point> P)
    {
        if (!_overruns.TryGetValue(rw, out var o)) return;
        double hl = rw.Length / 2, hw = rw.Width / 2;
        foreach (var (side, len) in new[] { (-1, o.Near), (1, o.Far) })
        {
            if (len < 20) continue;
            double a0 = side * hl, a1 = side * (hl + len);
            dc.DrawGeometry(Pavement, new Pen(PavementEdge, 0.8), Poly(P(a0, -hw), P(a1, -hw), P(a1, hw), P(a0, hw)));
            // Chevrons every 30 m, their points toward the runway.
            var chevrons = new StreamGeometry();
            using (var c = chevrons.Open())
                for (var d = 15.0; d + 10 < len; d += 30)
                {
                    double tip = side * (hl + d), back = side * (hl + d + hw * 0.8);
                    c.BeginFigure(P(back, -hw * 0.85), false, false);
                    c.LineTo(P(tip, 0), true, true);
                    c.LineTo(P(back, hw * 0.85), true, true);
                }
            chevrons.Freeze();
            dc.DrawGeometry(null, new Pen(TaxiLine, Math.Max(0.6, 2.5 * _scale)), chevrons);
        }
    }

    /// <summary>The runway's paint as an aircraft sees it, in runway metres (ICAO layout): a
    /// threshold bar, piano keys, the designator, aiming-point blocks at 300 m and a dashed
    /// centre line. Only what is at least about a DIP wide at this zoom is drawn.</summary>
    private void DrawMarkings(DrawingContext dc, Runway rw, Func<double, double, Point> P, double screenHeading, string near, string far, double dpi)
    {
        double hl = rw.Length / 2, hw = rw.Width / 2, widthDip = rw.Width * _scale;
        if (widthDip < 4) return;
        Geometry Paint(double a0, double a1, double c0, double c1) => Poly(P(a0, c0), P(a1, c0), P(a1, c1), P(a0, c1));
        const double KeysFrom = 6, KeysLen = 45, AimAt = 300, AimLen = 45;
        // Piano keys: stripes and gaps alike across 80 % of the width, as many as stay ≥ 0.8 DIP.
        var stripes = Math.Clamp((int)(widthDip * 0.8 / 1.6) / 2 * 2, 2, 16);
        var unit = rw.Width * 0.8 / (2 * stripes - 1);
        var group = new GeometryGroup();
        foreach (var sign in new[] { 1, -1 })
        {
            // Along-runway distances from this end's threshold, inward.
            double From(double d) => sign * (-hl + d);
            group.Children.Add(Paint(From(1), From(2.5), -hw * 0.9, hw * 0.9));  // threshold bar
            if (unit * _scale >= 0.6)
                for (int i = 0; i < stripes; i++)
                {
                    var c0 = -rw.Width * 0.4 + i * 2 * unit;
                    group.Children.Add(Paint(From(KeysFrom), From(KeysFrom + KeysLen), c0, c0 + unit));
                }
            if (rw.Length > 2 * (AimAt + AimLen))
                foreach (var side in new[] { -1, 1 })
                    group.Children.Add(Paint(From(AimAt), From(AimAt + AimLen), side * hw * 0.22, side * hw * 0.42));
        }
        dc.DrawGeometry(Marking, null, group);

        // Designators painted beyond the keys, read from the approach.
        var size = Math.Clamp(widthDip * 0.5, 0, 13);
        if (size >= 5)
        {
            var at = KeysFrom + KeysLen + 10 + size / _scale * 0.6;
            DrawRotated(dc, near, P(-hl + at, 0), screenHeading, Marking, size, dpi);
            DrawRotated(dc, far, P(hl - at, 0), screenHeading + 180, Marking, size, dpi);
        }

        // Centre line: 30 m dashes, 20 m gaps, between the designators.
        var thick = Math.Max(0.5, 0.9 * _scale);
        var start = KeysFrom + KeysLen + 30 + size / _scale * 1.4;
        if (rw.Length > 2 * start && 20 * _scale >= 1.5)
            dc.DrawLine(new Pen(Marking, thick) { DashStyle = new DashStyle([30 * _scale / thick, 20 * _scale / thick], 0) },
                P(-hl + start, 0), P(hl - start, 0));
    }

    private static double Upright(double angle)
    {
        while (angle > 90) angle -= 180;
        while (angle < -90) angle += 180;
        return angle;
    }

    private void DrawHeadingArrow(DrawingContext dc, Point from, Point to, string text, double dpi)
    {
        var pen = new Pen(Ink, 1.2);
        dc.DrawLine(pen, from, to);
        var d = to - from;
        if (d.Length < 1) return;
        d.Normalize();
        var n = new Vec(-d.Y, d.X);
        dc.DrawGeometry(Ink, null, Poly(to, to - d * 7 + n * 3.5, to - d * 7 - n * 3.5));
        var mid = from + (to - from) / 2;
        var angle = Upright(Math.Atan2(d.Y, d.X) * 180 / Math.PI);
        var ft = Format(text, Ink, 9, bold: true, dpi);
        var lift = n * 8 * (Vec.CrossProduct(new Vec(1, 0), d) >= 0 ? -1 : 1);
        var at = mid + lift;
        dc.PushTransform(new RotateTransform(angle, at.X, at.Y));
        dc.DrawText(ft, new Point(at.X - ft.Width / 2, at.Y - ft.Height / 2));
        dc.Pop();
    }

    /// <summary>ILS feather: an arrow on the extended centreline ending at the threshold of the
    /// runway it serves, with ident, frequency and inbound course (magnetic).</summary>
    private void DrawIlsFeather(DrawingContext dc, Airfield field, Beacon loc, double dpi)
    {
        var inbound = loc.InboundCourseTrue;  // grid-relative like the runway courses
        if (RunwayFor(field, loc) is null) return;
        var rw = RunwayFor(field, loc)!;
        // Threshold of the landing end: the end you reach first flying the inbound course.
        var h = inbound * Math.PI / 180;
        double ux = Math.Cos(h), uz = Math.Sin(h);
        double tx = rw.X - ux * rw.Length / 2, tz = rw.Z - uz * rw.Length / 2;
        var start = ScreenXz(tx - ux * 1400, tz - uz * 1400);
        var end = ScreenXz(tx - ux * 120, tz - uz * 120);
        var pen = new Pen(NavaidInk, 1.4);
        dc.DrawLine(pen, start, end);
        var d = end - start;
        if (d.Length < 4) return;
        d.Normalize();
        var n = new Vec(-d.Y, d.X);
        // Feather: an arrowhead at the runway end, a half-feather at the tail.
        dc.DrawGeometry(NavaidInk, null, Poly(end, end - d * 9 + n * 4, end - d * 9 - n * 4));
        dc.DrawLine(pen, start, start - d * 6 + n * 7);
        var angle = Upright(Math.Atan2(d.Y, d.X) * 180 / Math.PI);
        var text = $"ILS {loc.Callsign} {loc.FrequencyText}  {MagText(Nav.Normalize(inbound + (_frame?.GridNorthBearing ?? 0)), loc.X, loc.Z)}";
        var ft = Format(text, NavaidInk, 9, bold: true, dpi);
        var at = start + (end - start) / 2 + n * 9;
        var textBox = new RotateTransform(angle, at.X, at.Y).TransformBounds(new Rect(at.X - ft.Width / 2, at.Y - ft.Height / 2, ft.Width, ft.Height));
        // The feather stays; its text gives way (see the info block) — also where it would leave the plan.
        if (!Free(textBox) || !Plan.Contains(textBox)) return;
        Take(textBox);
        dc.PushTransform(new RotateTransform(angle, at.X, at.Y));
        dc.DrawText(ft, new Point(at.X - ft.Width / 2, at.Y - ft.Height / 2));
        dc.Pop();
    }

    /// <summary>The runway a localizer serves: on its course axis (±10°), and of parallel ones
    /// the one whose centreline passes nearest the antenna.</summary>
    internal static Runway? RunwayFor(Airfield field, Beacon loc) =>
        field.Runways.Where(r => IcaoMatch.AxisDiff(r.HeadingTrue, loc.InboundCourseTrue) <= 10)
            .MinBy(r => Math.Abs(Lateral(r, loc.X, loc.Z)));

    /// <summary>Signed distance of a point from a runway's centreline (right of its heading positive).</summary>
    private static double Lateral(Runway r, double x, double z)
    {
        var h = r.HeadingTrue * Math.PI / 180;
        return -(x - r.X) * Math.Sin(h) + (z - r.Z) * Math.Cos(h);
    }

    /// <summary>Runway designators as printed: parallel runways DCS names alike get L/R (left or
    /// right looking along each end's heading), e.g. 05L/23R and 05R/23L.</summary>
    internal static (string Near, string Far) Names(Airfield field, Runway rw)
    {
        var (near, far) = Oriented(rw);
        // DCS may give the twin from its other end (Andersen: "06" and "24"): either way round.
        var twin = field.Runways.FirstOrDefault(o => !ReferenceEquals(o, rw) && IcaoMatch.AxisDiff(o.HeadingTrue, rw.HeadingTrue) <= 10
            && (Oriented(o) == (near, far) || Oriented(o) == (far, near)));
        if (twin is null || near.EndsWith('L') || near.EndsWith('R') || near.EndsWith('C')) return (near, far);
        // This runway lies left of the twin, looking along its heading → its near end is L.
        var left = Lateral(rw, twin.X, twin.Z) > 0;
        return left ? (near + "L", far + "R") : (near + "R", far + "L");
    }

    /// <summary>Designators ordered by the runway's course: near = the end landed on flying
    /// <c>HeadingTrue</c>. DCS sometimes names a runway after its other end (Damascus "23" with a
    /// 050° course), so the name alone does not say which end is which.</summary>
    internal static (string Near, string Far) Oriented(Runway rw)
    {
        var (near, far) = rw.Designators;
        var digits = new string(near.TakeWhile(char.IsDigit).ToArray());
        if (int.TryParse(digits, out var number) && Math.Abs(Nav.Normalize(number * 10 - rw.HeadingTrue + 180) - 180) > 90)
            (near, far) = (far, near);
        return (near, far);
    }

    /// <summary>The designator of the end an ILS lands on (its inbound course along or against the runway).</summary>
    private static string LandingEnd(Airfield field, Runway rw, Beacon loc)
    {
        var d = Nav.Normalize(loc.InboundCourseTrue - rw.HeadingTrue);
        var (near, far) = Names(field, rw);
        return d < 90 || d > 270 ? near : far;
    }

    // ---- Labels: each takes a free spot near its place, or gives way ----------------------------

    private bool Free(Rect r) => !_placed.Any(p => p.IntersectsWith(r));

    private void Take(Rect r) => _placed.Add(r);

    /// <summary>The first free spot among the label's place and small shifts around it.</summary>
    private Rect? Spot(Rect r, params Vec[] shifts)
    {
        if (Free(r)) return r;
        foreach (var s in shifts)
        {
            var moved = Rect.Offset(r, s);
            if (Free(moved)) return moved;
        }
        return null;
    }

    private void DrawAreas(DrawingContext dc, double dpi)
    {
        foreach (var label in _areas)
        {
            var p = ScreenXz(label.X, label.Z);
            var text = label.Text.ToUpperInvariant();
            var size = Map.Labels.Measure(text, Ink, HaloLight, 9.5, Map.Labels.Font.Regular, dpi);
            var spot = Spot(new Rect(p.X - size.Width / 2, p.Y - size.Height / 2, size.Width, size.Height), new Vec(0, -14), new Vec(0, 14), new Vec(0, -26), new Vec(0, 26));
            if (spot is not { } r) continue;  // no room at this zoom: zoom in to read it
            Take(r);
            Map.Labels.Draw(dc, text, r.TopLeft, Ink, HaloLight, 9.5, Map.Labels.Font.Regular, dpi);
        }
    }

    /// <summary>Stand groups (blue badges) zoomed out — badges that would touch merge into one
    /// ("13-25" + "26-30" → "13-30") — and each stand with its number closer in.</summary>
    private void DrawStands(DrawingContext dc, Airfield field, double dpi)
    {
        if (_scale < StandNumbersScale)
        {
            var groups = _stands.Select(g => (Numbers: g.Numbers.ToList(), P: ScreenXz(g.X, g.Z))).ToList();
            Rect Badge((List<int> Numbers, Point P) g, double pad)
            {
                var w = Format(Ranges(g.Numbers), Marking, 9, bold: true, dpi).Width + 9;
                return new Rect(g.P.X - w / 2 - pad, g.P.Y - 7.5 - pad, w + 2 * pad, 15 + 2 * pad);
            }
            for (var merged = true; merged;)
            {
                merged = false;
                for (int i = 0; i < groups.Count && !merged; i++)
                    for (int j = i + 1; j < groups.Count && !merged; j++)
                    {
                        if (!Badge(groups[i], 3).IntersectsWith(Badge(groups[j], 3))) continue;
                        int ni = groups[i].Numbers.Count, nj = groups[j].Numbers.Count;
                        var at = new Point((groups[i].P.X * ni + groups[j].P.X * nj) / (ni + nj), (groups[i].P.Y * ni + groups[j].P.Y * nj) / (ni + nj));
                        groups[i] = ([.. groups[i].Numbers, .. groups[j].Numbers], at);
                        groups.RemoveAt(j);
                        merged = true;
                    }
            }
            foreach (var g in groups)
            {
                var text = Ranges(g.Numbers);
                var ft = Format(text, Marking, 9, bold: true, dpi);
                var w = ft.Width + 9;
                // Stands are worth a longer look for room than area names: two rings of shifts.
                var spot = Spot(new Rect(g.P.X - w / 2, g.P.Y - 7.5, w, 15),
                    new Vec(0, -17), new Vec(0, 17), new Vec(w * 0.7, 0), new Vec(-w * 0.7, 0),
                    new Vec(w * 0.6, -17), new Vec(-w * 0.6, -17), new Vec(w * 0.6, 17), new Vec(-w * 0.6, 17),
                    new Vec(0, -34), new Vec(0, 34), new Vec(w * 1.3, 0), new Vec(-w * 1.3, 0));
                if (spot is not { } r) continue;
                Take(r);
                dc.DrawRoundedRectangle(StandBadge, null, r, 6, 6);
                dc.DrawText(ft, new Point(r.Left + 4.5, r.Top + 7.5 - ft.Height / 2));
            }
            return;
        }
        var pen = new Pen(StandBadge, 1.2);
        foreach (var spot in field.Parking)
        {
            var p = ScreenXz(spot.X, spot.Z);
            dc.DrawEllipse(Paper, pen, p, 3.5, 3.5);
            var text = spot.Number.ToString();
            var size = Map.Labels.Measure(text, StandBadge, HaloLight, 9, Map.Labels.Font.Bold, dpi);
            if (Spot(new Rect(p.X + 4, p.Y - 6, size.Width, size.Height), new Vec(-size.Width - 8, 0), new Vec(0, 8)) is not { } r) continue;
            Take(r);
            Map.Labels.Draw(dc, text, r.TopLeft, StandBadge, HaloLight, 9, Map.Labels.Font.Bold, dpi);
        }
    }

    /// <summary>Taxiway letters: white in a black oval (grey: from the chart, no DCS sign). Long
    /// legs first; a letter with no room nearby gives way (zoom in to see it).</summary>
    private void DrawTaxiLabels(DrawingContext dc, double dpi)
    {
        foreach (var l in _taxiLabels.OrderBy(l => l.FromChart).ThenByDescending(l => l.LegLength))
        {
            var p = ScreenXz(l.X, l.Z);
            var ft = Format(l.Text, Marking, 10, bold: true, dpi);
            var w = Math.Max(16, ft.Width + 9);
            var spot = Spot(new Rect(p.X - w / 2, p.Y - 7.5, w, 15), new Vec(0, -16), new Vec(0, 16), new Vec(w, 0), new Vec(-w, 0));
            if (spot is not { } r) continue;
            Take(r);
            var c = new Point(r.Left + r.Width / 2, r.Top + r.Height / 2);
            dc.DrawEllipse(l.FromChart ? Muted : Ink, null, c, w / 2, 7.5);
            dc.DrawText(ft, new Point(c.X - ft.Width / 2, c.Y - ft.Height / 2));
        }
    }
    /// <summary>Tower (star), TACAN/VOR (hexagon), NDB (dotted circle), localizer (bar) and
    /// glideslope (triangle) where DCS places them, with their idents.</summary>
    private void DrawNavaids(DrawingContext dc, double dpi)
    {
        var pen = new Pen(NavaidInk, 1.3);
        foreach (var b in _beacons)
        {
            var p = ScreenXz(b.X, b.Z);
            string? label = null;
            if (b.IsTacan || b.Type.StartsWith("VOR"))
            {
                dc.DrawGeometry(Paper, pen, Poly(Enumerable.Range(0, 6).Select(i => p + new Vec(Math.Cos(i * Math.PI / 3), Math.Sin(i * Math.PI / 3)) * 6).ToArray()));
                dc.DrawEllipse(NavaidInk, null, p, 1.5, 1.5);
                label = b.IsTacan ? $"{b.Callsign} {b.ChannelText}" : $"{b.Callsign} {b.FrequencyText}";
            }
            else if (b.Kind == "NDB")
            {
                dc.DrawEllipse(null, new Pen(NavaidInk, 1.2) { DashStyle = new DashStyle([1, 2], 0) }, p, 7, 7);
                dc.DrawEllipse(NavaidInk, null, p, 2, 2);
                label = $"{b.Callsign} {b.FrequencyText}";
            }
            else if (b.IsLocalizer)
            {
                dc.DrawRectangle(NavaidInk, null, new Rect(p.X - 4, p.Y - 2, 8, 4));
                label = "LOC";
            }
            else if (b.IsGlideslope)
            {
                dc.DrawGeometry(NavaidInk, null, Poly(new Point(p.X, p.Y - 5), new Point(p.X + 4.5, p.Y + 3), new Point(p.X - 4.5, p.Y + 3)));
                label = "GS";
            }
            Take(new Rect(p.X - 8, p.Y - 8, 16, 16));
            if (label is null) continue;
            var ls = Map.Labels.Measure(label, NavaidInk, HaloLight, 9, Map.Labels.Font.Bold, dpi);
            if (Spot(new Rect(p.X + 9, p.Y - 7, ls.Width, ls.Height), new Vec(-ls.Width - 18, 0), new Vec(0, 12), new Vec(0, -12)) is not { } lr) continue;
            Take(lr);
            Map.Labels.Draw(dc, label, lr.TopLeft, NavaidInk, HaloLight, 9, Map.Labels.Font.Bold, dpi);
        }
        if (_tower is { } t)
        {
            var p = ScreenXz(t.X, t.Z);
            var star = Enumerable.Range(0, 10).Select(i =>
            {
                var r = i % 2 == 0 ? 8.0 : 3.4;
                var a = -Math.PI / 2 + i * Math.PI / 5;
                return p + new Vec(Math.Cos(a), Math.Sin(a)) * r;
            }).ToArray();
            dc.DrawGeometry(Paper, new Pen(Ink, 1.2), Poly(star));
            Take(new Rect(p.X - 9, p.Y - 9, 18, 18));
            var ts = Map.Labels.Measure("TWR", Ink, HaloLight, 9, Map.Labels.Font.Bold, dpi);
            if (Spot(new Rect(p.X + 10, p.Y - 7, ts.Width, ts.Height), new Vec(-ts.Width - 20, 0)) is { } tr)
            {
                Take(tr);
                Map.Labels.Draw(dc, "TWR", tr.TopLeft, Ink, HaloLight, 9, Map.Labels.Font.Bold, dpi);
            }
        }
    }

    private void DrawTraffic(DrawingContext dc, double dpi)
    {
        var store = AppState.Store;
        if (store.Coalition is not { } own) return;
        var now = DateTime.UtcNow;
        foreach (var t in store.Tracks)
        {
            var u = t.Unit;
            if (u.Lat is not { } lat || u.Lon is not { } lon) continue;
            var (x, z) = _frame!.FromLatLon(lat, lon);
            var (e, n) = _frame.Local(x, z);
            if (Math.Abs(e) > LiveRadiusM || Math.Abs(n) > LiveRadiusM) continue;
            var p = Screen(e, n);
            var identity = Symbology.IdentityOf(u, own);
            var brush = identity switch { Identity.Friendly => Brushes.RoyalBlue, Identity.Hostile => Brushes.Firebrick, _ => Brushes.DarkGoldenrod };
            var air = Symbology.IsAir(Symbology.KindOf(u));
            dc.PushOpacity(t.Freshness(now));
            if (air)
            {
                if (u.Hdg is { } hdg)
                {
                    // Unit headings are relative to DCS grid north; the plan is true north up.
                    var rad = (hdg + _frame.GridNorthBearing) * Math.PI / 180;
                    dc.DrawLine(new Pen(brush, 1.5), p, new Point(p.X + Math.Sin(rad) * 16, p.Y - Math.Cos(rad) * 16));
                }
                dc.DrawEllipse(brush, new Pen(Paper, 1), p, 5, 5);
                var name = Symbology.NameOf(u) ?? (u.Type is null ? u.Assigned ?? "UNK" : Symbology.ShortType(u.Type));
                var line2 = $"{(u.Type is null ? "" : Symbology.ShortType(u.Type))} {u.SpdKt ?? 0:0}KT";
                Map.Labels.Draw(dc, name, new Point(p.X + 8, p.Y - 14), brush, HaloLight, 11, Map.Labels.Font.Bold, dpi);
                Map.Labels.Draw(dc, line2, new Point(p.X + 8, p.Y - 1), brush, HaloLight, 10, Map.Labels.Font.Regular, dpi);
            }
            else
            {
                dc.DrawRectangle(brush, null, new Rect(p.X - 3, p.Y - 3, 6, 6));
            }
            dc.Pop();
        }
    }

    private void DrawNorthArrow(DrawingContext dc, Airfield field, double dpi)
    {
        var plan = Plan;
        var at = new Point(plan.Right - 26, plan.Top + 28);
        dc.DrawGeometry(Ink, null, Poly(new Point(at.X, at.Y - 18), new Point(at.X + 7, at.Y + 8), new Point(at.X, at.Y + 3), new Point(at.X - 7, at.Y + 8)));
        DrawCentered(dc, "N", new Point(at.X, at.Y + 17), Ink, 11, true);
        if (Variation(field.X, field.Z) is { } v)
            DrawCentered(dc, $"VAR {Math.Abs(v):0.0}°{(v >= 0 ? "E" : "W")}", new Point(at.X - 6, at.Y + 31), Ink, 9, false);
    }

    private void DrawScaleBar(DrawingContext dc, double dpi)
    {
        var plan = Plan;
        // Feet: the largest of 500/1000/2000/5000 ft that fits in ~130 DIP.
        var feet = new[] { 5000, 2000, 1000, 500 }.FirstOrDefault(f => f / FeetPerMeter * _scale <= 130, 500);
        var len = feet / FeetPerMeter * _scale;
        var y = plan.Bottom - 14;
        var x0 = plan.Left + 12;
        var pen = new Pen(Ink, 2);
        dc.DrawLine(pen, new Point(x0, y), new Point(x0 + len, y));
        dc.DrawLine(pen, new Point(x0, y - 4), new Point(x0, y + 1));
        dc.DrawLine(pen, new Point(x0 + len, y - 4), new Point(x0 + len, y + 1));
        dc.DrawText(Format($"{feet} FT", Ink, 9, bold: false, dpi), new Point(x0 + len + 5, y - 7));
    }

    private void DrawHeader(DrawingContext dc, Airfield field, Theatre theatre, double dpi)
    {
        var p = Page;
        dc.DrawText(Format("AIRPORT DIAGRAM", Ink, 19, bold: true, dpi), new Point(p.Left + Pad, p.Top + 5));
        dc.DrawText(Format($"DCS {theatre.Id.ToUpperInvariant()}", Muted, 9.5, bold: false, dpi), new Point(p.Left + Pad, p.Top + 33));
        var name = field.Name.Replace('_', ' ').ToUpperInvariant() + (_icao is null ? "" : $" ({_icao})");
        var title = Format(name, Ink, 15, bold: true, dpi);
        dc.DrawText(title, new Point(p.Right - Pad - title.Width, p.Top + 6));
        var (lat, lon) = theatre.ToLatLon(field.X, field.Z);
        var parts = new List<string> { Coordinates.Ddm(lat, lon) };
        if (FieldElevationFt(field) is { } elev) parts.Add($"ELEV {elev:0} FT");
        if (Variation(field.X, field.Z) is { } v) parts.Add($"VAR {Math.Abs(v):0.0}°{(v >= 0 ? "E" : "W")}");
        var line = Format(string.Join("   ", parts), Ink, 10, bold: false, dpi);
        dc.DrawText(line, new Point(p.Right - Pad - line.Width, p.Top + 31));
        dc.DrawLine(new Pen(Ink, 1.5), new Point(p.Left + Pad, p.Top + HeaderH - 6), new Point(p.Right - Pad, p.Top + HeaderH - 6));
    }

    private double? FieldElevationFt(Airfield field) =>
        AppState.Store.Weather?.Airfields.FirstOrDefault(a => a.Id == field.Id) is { } w ? w.Elevation * FeetPerMeter : ElevationFt(field.X, field.Z);

    /// <summary>Information block: communications (radio.lua), navaids (beacons.lua), runways
    /// with magnetic headings, dimensions, threshold elevations and their ILS.</summary>
    private void DrawInfo(DrawingContext dc, Airfield field, Theatre theatre, double dpi)
    {
        if (Sheet)
        {
            DrawSheetInfo(dc, field, theatre, dpi);
            return;
        }
        var r = Info;
        dc.DrawRectangle(null, new Pen(Ink, 1), r);
        var x = r.Left + 8;
        var y = r.Top + 6;
        var col2 = InfoAside ? r.Left + 8 : r.Left + r.Width / 2 + 8;
        double Heading(string text, double at, double yy)
        {
            dc.DrawText(Format(text, Ink, 10, bold: true, dpi), new Point(at, yy));
            return yy + 16;
        }
        double Line(string text, double at, double yy, Brush? brush = null)
        {
            dc.DrawText(Format(text, brush ?? Ink, 9.5, bold: false, dpi), new Point(at, yy));
            return yy + 14;
        }

        y = Heading("COMMUNICATIONS", x, y);
        var freqs = Srs.SrsBridge.FrequenciesOf(theatre.Id, field.Id);
        y = freqs.Count == 0 ? Line("No frequencies in radio.lua", x, y, Muted)
            : Line("TOWER  " + string.Join("  ", freqs.Select(f => $"{f.Mhz:0.000}")), x, y);

        y += 4;
        y = Heading("NAVAIDS", x, y);
        var navaids = _beacons.Where(b => !b.IsLocalizer && !b.IsGlideslope).ToList();
        if (navaids.Count == 0) y = Line("None listed", x, y, Muted);
        foreach (var b in navaids)
            y = Line($"{b.Kind,-7} {b.Callsign,-4} {(b.IsTacan ? b.ChannelText + "  " : "")}{b.FrequencyText}", x, y);

        var y2 = InfoAside ? y + 4 : r.Top + 6;
        y2 = Heading("RUNWAYS", col2, y2);
        foreach (var rw in field.Runways)
        {
            var (near, far) = Names(field, rw);
            var trueHdg = TrueHeading(rw);
            var h = rw.HeadingTrue * Math.PI / 180;
            double ux = Math.Cos(h) * rw.Length / 2, uz = Math.Sin(h) * rw.Length / 2;
            string End(string des, double hdg, double ex, double ez) =>
                $"RWY {des,-4} {MagText(hdg, rw.X, rw.Z)}  ELEV {(ElevationFt(ex, ez) is { } e ? $"{e:0}" : "—")}";
            y2 = Line($"{near}/{far}  {rw.Length * FeetPerMeter:0} X {rw.Width * FeetPerMeter:0} FT", col2, y2);
            y2 = Line("  " + End(near, trueHdg, rw.X - ux, rw.Z - uz), col2, y2);
            y2 = Line("  " + End(far, Nav.Normalize(trueHdg + 180), rw.X + ux, rw.Z + uz), col2, y2);
        }
        foreach (var loc in _beacons.Where(b => b.IsLocalizer))
        {
            var landing = RunwayFor(field, loc) is { } rw ? LandingEnd(field, rw, loc) : "";
            var crs = MagText(Nav.Normalize(loc.InboundCourseTrue + (_frame?.GridNorthBearing ?? 0)), loc.X, loc.Z);
            y2 = Line($"ILS {landing,-4} {loc.Callsign,-5} {loc.FrequencyText}  CRS {crs}", col2, y2, NavaidInk);
        }
        // Letters' source, small, at the bottom of the block.
        if (LetterSource.Length > 0)
            dc.DrawText(Format($"Taxiway letters: {LetterSource}", Muted, 8.5, bold: false, dpi), new Point(x, r.Bottom - 15));
    }

    /// <summary>A sheet's information block in two columns, most used first: the field (code,
    /// TACAN, VOR, tower frequencies), then the other navaids. The values a pilot reads off are bold.</summary>
    private void DrawSheetInfo(DrawingContext dc, Airfield field, Theatre theatre, double dpi)
    {
        var r = Info;
        dc.DrawRectangle(null, new Pen(Ink, 1), r);
        var w = r.Width / 2;
        dc.DrawLine(new Pen(Muted, 0.6), new Point(r.Left + w, r.Top + 6), new Point(r.Left + w, r.Bottom - 6));
        double Heading(string text, double x, double y)
        {
            dc.DrawText(Format(text, Ink, 10, bold: true, dpi), new Point(x, y));
            return y + 17;
        }
        // A label in regular type and its value in bold, the values lined up at a tab.
        double Pair(string label, string value, double x, double y, double tab = 62, Brush? brush = null)
        {
            dc.DrawText(Format(label, Muted, 9, bold: false, dpi), new Point(x, y + 1));
            dc.DrawText(Format(value, brush ?? Ink, 10, bold: true, dpi), new Point(x + tab, y));
            return y + 15;
        }

        // 1: the field.
        double x1 = r.Left + 8, y1 = Heading("AIRFIELD", x1, r.Top + 6);
        y1 = Pair("CODE", _icao ?? "—", x1, y1);
        foreach (var b in _beacons.Where(b => b.IsTacan)) y1 = Pair("TACAN", $"{b.Callsign}  {b.ChannelText}", x1, y1, brush: NavaidInk);
        foreach (var b in _beacons.Where(b => !b.IsTacan && b.Type.StartsWith("VOR"))) y1 = Pair("VOR", $"{b.Callsign}  {b.FrequencyText}", x1, y1, brush: NavaidInk);
        var freqs = Srs.SrsBridge.FrequenciesOf(theatre.Id, field.Id);
        if (freqs.Count == 0) y1 = Pair("TOWER", "—", x1, y1);
        foreach (var f in freqs) y1 = Pair(freqs[0] == f ? "TOWER" : "", $"{f.Mhz:0.000}  {f.Band}", x1, y1);

        // 2: the other navaids.
        double x3 = r.Left + w + 8, y3 = Heading("NAVAIDS", x3, r.Top + 6);
        var rest = _beacons.Where(b => !b.IsLocalizer && !b.IsGlideslope && !b.IsTacan && !b.Type.StartsWith("VOR")).ToList();
        if (rest.Count == 0) y3 = Pair("", "None listed", x3, y3, 0, Muted);
        foreach (var b in rest) y3 = Pair(b.Kind == "NDB" ? b.Type.Replace("ILS_", "").Replace('_', ' ') : b.Kind, $"{b.Callsign}  {b.FrequencyText}", x3, y3, 112, NavaidInk);
        if (LetterSource.Length > 0)
            dc.DrawText(Format($"Taxiway letters: {LetterSource}", Muted, 8.5, bold: false, dpi), new Point(x3, r.Bottom - 15));
    }

    private void DrawFooter(DrawingContext dc, Theatre theatre, double dpi)
    {
        var p = Page;
        var y = p.Bottom - FooterH + 3;
        dc.DrawText(Format($"NEBO · DCS {theatre.Id.ToUpperInvariant()} DATA", Muted, 9, bold: false, dpi), new Point(p.Left + Pad, y));
        // A sheet says it once, in its page footer.
        if (!Sheet)
        {
            var warn = Format("NOT FOR REAL NAVIGATION", Warning, 10, bold: true, dpi);
            dc.DrawText(warn, new Point(p.Left + p.Width / 2 - warn.Width / 2, y - 1));
        }
        var date = Format(DateTime.Now.ToString("dd MMM yyyy", CultureInfo.InvariantCulture).ToUpperInvariant(), Muted, 9, bold: false, dpi);
        dc.DrawText(date, new Point(p.Right - Pad - date.Width, y));
    }

    // ---- Drawing helpers -----------------------------------------------------------------------

    private static Geometry Poly(params Point[] pts)
    {
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(pts[0], true, true);
            c.PolyLineTo(pts[1..], true, false);
        }
        g.Freeze();
        return g;
    }

    private static void DrawRotated(DrawingContext dc, string text, Point at, double screenHeading, Brush brush, double size, double dpi)
    {
        if (text.Length == 0) return;
        var ft = Format(text, brush, size, bold: true, dpi);
        dc.PushTransform(new RotateTransform(screenHeading, at.X, at.Y));
        dc.DrawText(ft, new Point(at.X - ft.Width / 2, at.Y - ft.Height / 2));
        dc.Pop();
    }

    private void DrawCentered(DrawingContext dc, string text, Point at, Brush brush, double size, bool bold)
    {
        var ft = Format(text, brush, size, bold, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point(at.X - ft.Width / 2, at.Y - ft.Height / 2));
    }

    private static FormattedText Format(string text, Brush brush, double size, bool bold, double dpi) =>
        new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
            size, brush, dpi);

    // ---- Interaction: wheel zooms at the cursor, drag pans, double-click fits (plan frame) -------

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_field is null) return;
        var at = e.GetPosition(this);
        var plan = Plan;
        if (!plan.Contains(at)) return;
        double cx = plan.Left + plan.Width / 2, cy = plan.Top + plan.Height / 2;
        var pe = _centerE + (at.X - cx) / _scale;
        var pn = _centerN + (cy - at.Y) / _scale;
        _scale *= e.Delta > 0 ? 1.25 : 0.8;
        _userView = true;
        var min = FitScale;
        _scale = Math.Clamp(_scale, min, Math.Max(min, MaxScale));
        _centerE = pe - (at.X - cx) / _scale;
        _centerN = pn - (cy - at.Y) / _scale;
        ClampView();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        if (!Plan.Contains(e.GetPosition(this))) return;
        if (e.ClickCount == 2)
        {
            Fit();
            return;
        }
        _dragFrom = e.GetPosition(this);
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragFrom is not { } from || e.LeftButton != MouseButtonState.Pressed) return;
        var at = e.GetPosition(this);
        _centerE -= (at.X - from.X) / _scale;
        _centerN += (at.Y - from.Y) / _scale;
        _dragFrom = at;
        _userView = true;
        ClampView();
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragFrom = null;
        ReleaseMouseCapture();
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>A name placed on the diagram, in DCS map metres.</summary>
public sealed record DiagramLabel(string Text, string Kind, double X, double Z)
{
    public const string Taxiway = "taxiway";
    public const string Area = "area";
}

/// <summary>True-north local frame around a DCS point: DCS x/z ↔ local east/north metres,
/// and lat/lon → DCS x/z by a linear fit around the origin (exact to well under a metre over
/// an airfield's few kilometres).</summary>
public sealed class LocalFrame
{
    private readonly double _ox, _oz, _lat0, _lon0;
    private readonly double _sin, _cos;                 // rotation grid → true north
    private readonly double _a, _b, _c, _d;             // inverse Jacobian (lat, lon) → (x, z)

    /// <summary>Bearing of DCS grid north from true north, degrees (meridian convergence).</summary>
    public double GridNorthBearing { get; }

    public LocalFrame(Theatre theatre, double ox, double oz)
    {
        _ox = ox;
        _oz = oz;
        (_lat0, _lon0) = theatre.ToLatLon(ox, oz);
        const double step = 500;
        var (latX, lonX) = theatre.ToLatLon(ox + step, oz);
        var (latZ, lonZ) = theatre.ToLatLon(ox, oz + step);
        // Bearing of the +x (grid north) axis from true north.
        var cosLat = Math.Cos(_lat0 * Math.PI / 180);
        var bearing = Math.Atan2((lonX - _lon0) * cosLat, latX - _lat0);
        GridNorthBearing = bearing * 180 / Math.PI;
        _sin = Math.Sin(bearing);
        _cos = Math.Cos(bearing);
        // Jacobian d(lat, lon)/d(x, z), inverted.
        double j11 = (latX - _lat0) / step, j12 = (latZ - _lat0) / step;
        double j21 = (lonX - _lon0) / step, j22 = (lonZ - _lon0) / step;
        var det = j11 * j22 - j12 * j21;
        _a = j22 / det; _b = -j12 / det; _c = -j21 / det; _d = j11 / det;
    }

    /// <summary>DCS x/z → (east, north) metres from the origin, true north.</summary>
    public (double E, double N) Local(double x, double z)
    {
        double dx = x - _ox, dz = z - _oz;
        // Grid x points at bearing β, grid z at β + 90°.
        return (dx * _sin + dz * _cos, dx * _cos - dz * _sin);
    }

    public (double X, double Z) FromLatLon(double lat, double lon)
    {
        double dLat = lat - _lat0, dLon = lon - _lon0;
        return (_ox + _a * dLat + _b * dLon, _oz + _c * dLat + _d * dLon);
    }
}
