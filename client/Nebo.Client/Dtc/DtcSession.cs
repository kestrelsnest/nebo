using System.Text.Json.Nodes;
using Nebo.Client.Core;
using Nebo.Client.Core.Dtc;

namespace Nebo.Client.Dtc;

/// <summary>What a right-click on the DTC map places: the open tab of the editor decides.</summary>
public enum DtcPlace { Waypoints, Gps, Sa, Jdam }

/// <summary>The SA object a right-click places: F/A-18C SA page threat rings, CAPs, corridors,
/// FAOR, FLOT; F-16C preplanned threats (Threat) and geographic lines 1–4 (Geo); F-14 lines (Geo).</summary>
public enum SaKind { Threat, Cap, Corridor, Faor, Flot, Geo }

/// <summary>An SA object on the map: its kind, its index in its list, and for a line the point
/// (a geographic / F-14 line: Index is the line from 0, Point its n-th point).</summary>
public readonly record struct SaRef(SaKind Kind, int Index, int Point = 0)
{
    public HornetSa.LineKind? Line => LineOf(Kind);

    public static HornetSa.LineKind? LineOf(SaKind kind) => kind switch
    {
        SaKind.Corridor => HornetSa.Corridor,
        SaKind.Faor => HornetSa.Faor,
        SaKind.Flot => HornetSa.Flot,
        _ => null,
    };
}

/// <summary>The data cartridges being made in the DTC tab, shared by its panel and its map: one
/// per aircraft (F/A-18C, F-16C, F-14B(U); DCS's defaults to start with), the one open in the tab
/// (<see cref="Cartridge"/>) and the section views of each. With <see cref="SharePoints"/> the
/// open jet's route, free points, lines and threat rings go into the other two on every change
/// (<see cref="DtcPlan"/>), each keeping its own settings. Also what is selected (waypoints, GPS
/// points, SA objects: several with Ctrl/Shift), what a right-click on the map does (adds a point
/// of the editor's open tab, or in delete mode deletes the one under the pointer), and the line
/// new points go on. Lives on the UI thread.</summary>
public sealed class DtcSession
{
    /// <summary>The aircraft types the tab makes cartridges for, with their names. Before
    /// <see cref="Instance"/>: static initializers run in source order, and the constructor reads it.</summary>
    public static readonly (string Type, string Name)[] Aircraft =
        [(Cartridge.HornetType, "F/A-18C"), (Cartridge.ViperType, "F-16C"), (Cartridge.TomcatType, "F-14B(U)")];

    public static DtcSession Instance { get; } = new();

    private readonly Dictionary<string, Cartridge> _jets = [];
    private readonly Dictionary<string, string> _paths = [];

    /// <summary>The aircraft open in the tab.</summary>
    public string ActiveType { get; private set; } = Cartridge.HornetType;
    /// <summary>The cartridge open in the tab.</summary>
    public Cartridge Cartridge => _jets[ActiveType];
    public Cartridge CartridgeOf(string type) => _jets[type];

    public HornetNav Nav { get; private set; }
    public HornetComm Comm { get; private set; }
    public HornetGps Gps { get; private set; }
    public HornetTacan Tacan { get; private set; }
    public HornetSa Sa { get; private set; }
    public HornetEw Ew { get; private set; }
    public ViperNav Stpt { get; private set; }
    public ViperComm VComm { get; private set; }
    public ViperEw VEw { get; private set; }
    public ViperDest Dest { get; private set; }
    public ViperGeo Geo { get; private set; }
    public ViperThreats Threats { get; private set; }
    public ViperVipVrp VipVrp { get; private set; }
    public ViperColr Colr { get; private set; }
    public TomcatNav TNav { get; private set; }
    public TomcatCmds TCmds { get; private set; }
    public TomcatJdam Jdam { get; private set; }
    public TomcatTis Tis { get; private set; }

    public bool IsViper => ActiveType == Cartridge.ViperType;
    public bool IsTomcat => ActiveType == Cartridge.TomcatType;
    /// <summary>F-14: the flight plan the map and the NAV tab work on (2–12; 1, the mission's, for its lines).</summary>
    public int TomcatPlan { get; private set; } = DtcPlan.TomcatPlan;
    /// <summary>F-16C: the next right-click places this offset instead of a point: "OAP_1" / "OAP_2"
    /// of the selected steerpoint, or a VIP / VRP leg (ViperVipVrp.Legs); null: none.</summary>
    public string? PlaceOffset { get; private set; }
    /// <summary>F-16C: the threat type (THREAT_PTS_defs, 1-based) of a new preplanned threat.</summary>
    public int ViperThreatType { get; set; } = 1;
    /// <summary>F-14: the JDAM station (1–4, STA 3–6) and target (1–8) a right-click in the JDAM tab places.</summary>
    public (int Station, int Target) JdamPick { get; private set; } = (1, 1);
    /// <summary>Where the open cartridge came from or was last saved ("" for a new one).</summary>
    public string Path => _paths.GetValueOrDefault(ActiveType, "");
    public DtcPlace Place { get; private set; }

    /// <summary>Right-clicks on the map delete what is under the pointer instead of adding.</summary>
    public bool DeleteMode { get; private set; }

    // ---- Selections --------------------------------------------------------------------------------

    /// <summary>The waypoint picked last (the one the editor shows), or null.</summary>
    public int? Selected { get; private set; }
    /// <summary>Every selected waypoint (Ctrl/Shift adds to it); holds <see cref="Selected"/>.</summary>
    public IReadOnlySet<int> SelectedSet => _selected;
    private readonly HashSet<int> _selected = [];

    /// <summary>The GPS waypoint picked last, or null; every selected one.</summary>
    public int? SelectedGps { get; private set; }
    public IReadOnlySet<int> SelectedGpsSet => _selectedGps;
    private readonly HashSet<int> _selectedGps = [];

    /// <summary>The SA object picked last (the one the editor shows), or null; every selected one.</summary>
    public SaRef? SaSelected { get; private set; }
    public IReadOnlySet<SaRef> SaSelectedSet => _selectedSa;
    private readonly HashSet<SaRef> _selectedSa = [];

    // ---- SA: what a right-click places -------------------------------------------------------------

    /// <summary>The kind of SA object a right-click places (the SA tab's type selector).</summary>
    public SaKind SaPlace { get; private set; }
    /// <summary>The ring type for a new threat ring.</summary>
    public JsonObject? SaRingType { get; set; }
    /// <summary>The line of <see cref="SaPlace"/> new points go on; null: the next point starts a new line.</summary>
    public int? SaLine { get; private set; }

    /// <summary>Anything in the cartridges or the selection changed.</summary>
    public event Action? Changed;

    private DtcSession()
    {
        foreach (var (type, _) in Aircraft) _jets[type] = NewCartridge(type);
        Bind();
        _planKey = DtcPlan.From(Cartridge).Key;
        _friendlyPending = true;
        AppState.Store.Changed += () => { if (_friendlyPending) MarkFriendly(); };
    }

    /// <summary>The views on each aircraft's cartridge.</summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(Nav), nameof(Comm), nameof(Gps), nameof(Tacan), nameof(Sa), nameof(Ew),
        nameof(Stpt), nameof(VComm), nameof(VEw), nameof(Dest), nameof(Geo), nameof(Threats), nameof(VipVrp), nameof(Colr),
        nameof(TNav), nameof(TCmds), nameof(Jdam), nameof(Tis))]
    private void Bind()
    {
        var (hornet, viper, tomcat) = (_jets[Cartridge.HornetType], _jets[Cartridge.ViperType], _jets[Cartridge.TomcatType]);
        (Nav, Comm, Gps, Tacan, Sa, Ew) = (new(hornet), new(hornet), new(hornet), new(hornet), new(hornet), new(hornet));
        (Stpt, VComm, VEw, Dest, Geo, Threats, VipVrp, Colr) = (new(viper), new(viper), new(viper), new(viper), new(viper), new(viper), new(viper), new(viper));
        (TNav, TCmds, Jdam, Tis) = (new(tomcat), new(tomcat), new(tomcat), new(tomcat));
    }

    /// <summary>A jet's default cartridge from its tables (made from the controller's DCS install,
    /// DtcData); without them an empty placeholder, so maps and the tab still open, and the tab
    /// says why (<see cref="TablesMissing"/>).</summary>
    private Cartridge NewCartridge(string type)
    {
        try
        {
            var c = Cartridge.NewDefault(type, CurrentTerrain());
            _missing.Remove(type);
            return c;
        }
        catch (NotSupportedException)
        {
            _missing[type] = Core.Dtc.DtcData.Problem ?? $"DCS's data cartridge files for the {NameOf(type)} are not in this DCS install.";
            return Cartridge.Parse("{\"type\":\"" + type + "\",\"name\":\"\",\"data\":{\"terrain\":\"\",\"WYPT\":{\"NAV_PTS\":[],\"NAV_ROUTE\":[[],[],[]],\"terrain\":\"\"}}}");
        }
    }

    private readonly Dictionary<string, string> _missing = [];

    /// <summary>Why the open jet has no real cartridge (no DCS on this PC, or its tables could not
    /// be made); null when it has DCS's defaults.</summary>
    public string? TablesMissing => _missing.GetValueOrDefault(ActiveType);

    public static string NameOf(string type) => Aircraft.FirstOrDefault(a => a.Type == type).Name ?? type;
    public string AircraftName => NameOf(ActiveType);

    private static string CurrentTerrain() => AppState.Store.Theatre?.Id ?? "";

    // ---- One plan for every jet ----------------------------------------------------------------------

    /// <summary>The open jet's points go into the other jets' cartridges on every change.</summary>
    public bool SharePoints { get; private set; } = true;
    /// <summary>What the last sharing could not fit, or why it waits; null: all went.</summary>
    public string? ShareNote { get; private set; }
    private string _planKey;

    public void SetSharePoints(bool on)
    {
        SharePoints = on;
        if (on) _planKey = "";   // the open jet's points go to the others now
        Notify();
    }

    /// <summary>The open jet's plan into the others when it changed since the last time.</summary>
    private void Share()
    {
        if (!SharePoints) return;
        var plan = DtcPlan.From(Cartridge);
        var key = plan.Key;
        if (key == _planKey) return;
        if (AppState.Store.Theatre is not { } theatre)
        {
            ShareNote = "The points go to the other jets once NEBO has the map (connect to a server).";
            return;
        }
        _planKey = key;
        var cut = new List<string>();
        foreach (var (type, _) in Aircraft.Where(a => a.Type != ActiveType))
        {
            if (_jets[type].Terrain.Length == 0) _jets[type].Terrain = theatre.Id;
            cut.AddRange(plan.ApplyTo(_jets[type], theatre.ToLatLon, Ground));
        }
        ShareNote = cut.Count == 0 ? null : "Not in every jet: " + string.Join("; ", cut) + ".";
    }

    /// <summary>Another jet open in the tab (the cartridges stay as they are).</summary>
    public void SetAircraft(string type)
    {
        if (type == ActiveType || !_jets.ContainsKey(type)) return;
        ActiveType = type;
        PlaceOffset = null;
        Place = DtcPlace.Waypoints;
        ClearSelections();
        SaPlace = IsViper || IsTomcat ? SaKind.Geo : SaKind.Threat;
        SaLine = LastLine(SaPlace);
        _planKey = DtcPlan.From(Cartridge).Key;   // a switch shares nothing by itself
        Notify();
    }

    // ---- EW: friendly by side ---------------------------------------------------------------------

    /// <summary>New cartridges wait for a picture of the own side to mark its systems friendly.</summary>
    private bool _friendlyPending;
    /// <summary>Counts changes made to the EW tables from outside their grids (the grids rebuild).</summary>
    public int EwRevision { get; private set; }

    /// <summary>The unit types of the own coalition in the picture (NEBO sees its own side undetected),
    /// or of the other side's contacts DCS has identified.</summary>
    private static HashSet<string> Types(bool own) =>
        AppState.Store.Tracks.Where(t => t.Unit.Detected != own && t.Unit.Type is not null).Select(t => t.Unit.Type!).ToHashSet();

    /// <summary>Every jet after the own side's systems: F/A-18C RWR friend flags and HARM TOO classes,
    /// F-16C ROE sovereignty (own types friendly, the other side's hostile, the rest unknown). What was done.</summary>
    public string MarkFriendly()
    {
        var own = Types(own: true);
        if (own.Count == 0) return "No picture of your side yet: connect to NEBO first.";
        _friendlyPending = false;
        var (rwr, harm) = Ew.MarkFriendly(own);
        var (friends, hostiles) = VEw.MarkFriendly(own, Types(own: false));
        FriendlyNote = $"{AppState.Store.Coalition} side: F/A-18C {rwr} RWR emitters and {harm} HARM radars friendly, the rest hostile; " +
                       $"F-16C ROE {friends} aircraft groups friendly, {hostiles} hostile, the rest unknown.";
        EwRevision++;
        Notify();
        return FriendlyNote;
    }

    /// <summary>What the last marking did; null when these cartridges were never marked here.</summary>
    public string? FriendlyNote { get; private set; }

    // ---- Cartridges ---------------------------------------------------------------------------------

    /// <summary>New cartridges with DCS's defaults for every jet (the open one stays open).</summary>
    public void New()
    {
        foreach (var (type, _) in Aircraft) _jets[type] = NewCartridge(type);
        _paths.Clear();
        Bind();
        FriendlyNote = ShareNote = null;
        PlaceOffset = null;
        JdamPick = (1, 1);
        ClearSelections();
        SaLine = LastLine(SaPlace);
        _planKey = DtcPlan.From(Cartridge).Key;
        _friendlyPending = true;
        MarkFriendly();  // at once when there is a picture, else with the first one
        Notify();
    }

    /// <summary>A .dtc file into its aircraft's place, open in the tab; with shared points its
    /// points go to the other jets.</summary>
    public void Open(string path)
    {
        var c = Cartridge.Load(path);
        if (!_jets.ContainsKey(c.Type))
            throw new NotSupportedException($"{c.Type} cartridges are not supported yet ({string.Join(", ", Aircraft.Select(a => a.Name))} only).");
        _jets[c.Type] = c;
        _paths[c.Type] = path;
        Bind();
        ActiveType = c.Type;
        PlaceOffset = null;
        Place = DtcPlace.Waypoints;
        ClearSelections();
        SaLine = LastLine(SaPlace);
        Notify();
    }

    public void Save(string path)
    {
        Prepare(ActiveType).Save(path);
        _paths[ActiveType] = path;
        Notify();
    }

    /// <summary>A jet's cartridge as it goes out (saved or pushed): its terrain set, and for the
    /// F-16C the tables its avionics read rebuilt (DCS's dialog does that on every change; the
    /// F/A-18C's are rebuilt by DCS's own code in the pilot hook, the F-14's need none).</summary>
    public Cartridge Prepare(string type)
    {
        var c = _jets[type];
        if (c.Terrain.Length == 0) c.Terrain = CurrentTerrain();
        if (type == Cartridge.ViperType) VEw.Compile();
        return c;
    }

    private void ClearSelections()
    {
        Selected = SelectedGps = null;
        SaSelected = null;
        _selected.Clear();
        _selectedGps.Clear();
        _selectedSa.Clear();
    }

    public void SetPlace(DtcPlace place)
    {
        if (Place == place) return;
        Place = place;
        Notify();
    }

    /// <summary>F-16C: the next right-click places this offset (see <see cref="PlaceOffset"/>); null: points again.</summary>
    public void SetPlaceOffset(string? offset)
    {
        PlaceOffset = offset;
        Notify();
    }

    /// <summary>F-14: the flight plan the map and NAV tab work on.</summary>
    public void SetTomcatPlan(int plan)
    {
        TomcatPlan = Math.Clamp(plan, 1, TomcatNav.Plans);
        ClearSelections();
        SaLine = LastLine(SaKind.Geo);
        Notify();
    }

    public void SetDeleteMode(bool on)
    {
        if (DeleteMode == on) return;
        DeleteMode = on;
        Notify();
    }

    // ---- Waypoint selection ------------------------------------------------------------------------

    /// <summary>Selects one waypoint only (null: none).</summary>
    public void Select(int? num)
    {
        _selected.Clear();
        if (num is { } n) _selected.Add(n);
        Selected = num;
        Notify();
    }

    /// <summary>Adds a waypoint to the selection, or takes it out (Ctrl/Shift+click).</summary>
    public void Toggle(int num)
    {
        if (!_selected.Remove(num)) _selected.Add(num);
        Selected = _selected.Contains(num) ? num : _selected.Count > 0 ? _selected.Max() : null;
        Notify();
    }

    /// <summary>Selects these waypoints (the list's own selection); the last one is the editor's.</summary>
    public void SelectMany(IReadOnlyList<int> nums)
    {
        _selected.Clear();
        _selected.UnionWith(nums);
        Selected = nums.Count > 0 ? nums[^1] : null;
        Notify();
    }

    // ---- GPS selection -----------------------------------------------------------------------------

    public void SelectGps(int? num)
    {
        _selectedGps.Clear();
        if (num is { } n) _selectedGps.Add(n);
        SelectedGps = num;
        Notify();
    }

    public void ToggleGps(int num)
    {
        if (!_selectedGps.Remove(num)) _selectedGps.Add(num);
        SelectedGps = _selectedGps.Contains(num) ? num : _selectedGps.Count > 0 ? _selectedGps.Max() : null;
        Notify();
    }

    public void SelectGpsMany(IReadOnlyList<int> nums)
    {
        _selectedGps.Clear();
        _selectedGps.UnionWith(nums);
        SelectedGps = nums.Count > 0 ? nums[^1] : null;
        Notify();
    }

    // ---- SA selection and the current line ---------------------------------------------------------

    /// <summary>Selects one SA object (null: none). A line's point makes its kind and line the ones
    /// right-clicks work on.</summary>
    public void SelectSa(SaRef? r)
    {
        _selectedSa.Clear();
        if (r is { } s) _selectedSa.Add(s);
        SaSelected = r;
        Follow(r);
        Notify();
    }

    public void ToggleSa(SaRef r)
    {
        if (!_selectedSa.Remove(r)) _selectedSa.Add(r);
        SaSelected = _selectedSa.Contains(r) ? r : _selectedSa.Count > 0 ? _selectedSa.Last() : null;
        if (_selectedSa.Contains(r)) Follow(r);
        Notify();
    }

    public void SelectSaMany(IReadOnlyList<SaRef> refs)
    {
        _selectedSa.Clear();
        _selectedSa.UnionWith(refs);
        SaSelected = refs.Count > 0 ? refs[^1] : null;
        Follow(SaSelected);
        Notify();
    }

    /// <summary>The type selector and current line follow a picked object.</summary>
    private void Follow(SaRef? r)
    {
        if (r is not { } s) return;
        SaPlace = s.Kind;
        if (s.Line is not null || s.Kind == SaKind.Geo && s.Index >= 0) SaLine = s.Index;   // a point on no line (−1) leaves it
    }

    /// <summary>The SA type right-clicks place (what was selected belongs to the type left); a line
    /// type continues its last line.</summary>
    public void SetSaPlace(SaKind kind)
    {
        if (SaPlace == kind) return;
        SaPlace = kind;
        SaLine = LastLine(kind);
        _selectedSa.Clear();
        SaSelected = null;
        Notify();
    }

    /// <summary>The lines of the open jet's geographic kind: F-16C lines 1–4 (point numbers), F-14
    /// the plan's lines (their points).</summary>
    private int GeoLinePoints(int line) => IsTomcat
        ? TNav.Lines(TomcatPlan).ElementAtOrDefault(line) is { } l ? TomcatNav.PointsOf(l).Count : 0
        : Geo.Line(line + 1).Count;

    /// <summary>The line new points go on (null: the next point starts a new line).</summary>
    public void SetSaLine(int? line)
    {
        SaLine = line;
        // Its last point selected: new points follow it.
        if (SaPlace == SaKind.Geo)
        {
            _selectedSa.Clear();
            SaSelected = null;
            if (line is { } g && GeoLinePoints(g) is > 0 and var count)
            {
                SaSelected = new SaRef(SaKind.Geo, g, count - 1);
                _selectedSa.Add(SaSelected.Value);
            }
        }
        else if (line is { } l && SaRef.LineOf(SaPlace) is { } kind && Sa.LinesOf(kind).ElementAtOrDefault(l) is { } obj)
        {
            _selectedSa.Clear();
            SaSelected = new SaRef(SaPlace, l, HornetSa.PointsOf(obj).Count - 1);
            _selectedSa.Add(SaSelected.Value);
        }
        Notify();
    }

    /// <summary>A line type's line new points go on: its last (F-16C geographic lines: line 1).</summary>
    private int? LastLine(SaKind kind) => kind == SaKind.Geo
        ? IsTomcat ? TNav.Lines(TomcatPlan).Count > 0 ? TNav.Lines(TomcatPlan).Count - 1 : null : 0
        : SaRef.LineOf(kind) is { } line && Sa.LinesOf(line).Count > 0 ? Sa.LinesOf(line).Count - 1 : null;

    // ---- JDAM ---------------------------------------------------------------------------------------

    public void SelectJdam(int station, int target)
    {
        JdamPick = (Math.Clamp(station, 1, TomcatJdam.Stations), Math.Clamp(target, 1, TomcatJdam.TargetsPerStation));
        Notify();
    }

    /// <summary>The picked target where it is (map point), its launch zone and launch point following.</summary>
    public void MoveJdam(int station, int target, double x, double z) => Jdam.Place(station, target, TomcatAt(x, z));

    /// <summary>The attack heading from the heading handle dragged to a map point.</summary>
    public void TurnJdam(int station, int target, double x, double z)
    {
        if (Jdam.Target(station, target) is not { } t) return;
        Jdam.Set(station, target, (string?)t["name"] ?? "", TomcatJdam.HeadingFrom(t, x, z), HornetNav.Number(t["drop_alt"]) ?? 20000,
            HornetNav.Number(t["drop_spd"]) ?? 450, t["has_impact_heading"]?.GetValue<bool>() == true ? HornetNav.Number(t["impact_heading"]) : null,
            t["has_impact_angle"]?.GetValue<bool>() == true ? HornetNav.Number(t["impact_angle"]) : null);
    }

    public void DeleteJdam(int station, int target)
    {
        Jdam.Clear(station, target);
        Notify();
    }

    // ---- Adding ------------------------------------------------------------------------------------

    private double Ground(double x, double z) =>
        AppState.Store.Terrain is { } grid ? LineOfSight.HeightAt(grid, x, z) ?? 0 : 0;

    /// <summary>An F-14 point: map metres, lat / lon (the jet keeps both), ground elevation.</summary>
    private TomcatNav.At TomcatAt(double x, double z)
    {
        var (lat, lon) = AppState.Store.Theatre is { } t ? t.ToLatLon(x, z) : (0.0, 0.0);
        return new TomcatNav.At(x, z, lat, lon, Ground(x, z));
    }

    private void Terrain()
    {
        if (Cartridge.Terrain.Length == 0) Cartridge.Terrain = CurrentTerrain();
    }

    /// <summary>A point of the open tab at a map point (DCS x north / z east): a waypoint, a GPS
    /// waypoint, an SA object or a JDAM target; selected. Null when added, else why not.</summary>
    public string? AddHere(double x, double z) => Place switch
    {
        _ when IsViper && PlaceOffset is not null => PlaceOffsetAt(x, z),
        DtcPlace.Jdam when IsTomcat => PlaceJdam(x, z),
        DtcPlace.Gps => AddGps(x, z) is null
            ? IsTomcat ? $"Plan {TomcatPlan} holds {TomcatNav.MaxPoints} points in all."
            : $"The cartridge holds {(IsViper ? Dest.Max + " DEST points" : HornetGps.MaxPoints + " GPS waypoints")} at most." : null,
        DtcPlace.Sa => AddSa(x, z),
        _ => AddWaypoint(x, z) is null
            ? IsTomcat ? TomcatNav.EditsWaypoints(TomcatPlan) ? $"Plan {TomcatPlan} holds {TomcatNav.MaxPoints} points in all."
                : "Plan 1 is the mission's route: pick plan 2–12 for waypoints."
            : $"The cartridge holds {(IsViper ? ViperNav.MaxPoints + " steerpoints" : HornetNav.MaxWaypoints + " waypoints")} at most." : null,
    };

    /// <summary>F-14: the picked JDAM target at a map point; the next free target of its station is picked next.</summary>
    private string? PlaceJdam(double x, double z)
    {
        var (s, t) = JdamPick;
        Jdam.Place(s, t, TomcatAt(x, z));
        var next = Enumerable.Range(1, TomcatJdam.TargetsPerStation).FirstOrDefault(n => Jdam.Target(s, n) is { } d && !TomcatJdam.IsActive(d));
        if (next > 0) JdamPick = (s, next);
        Notify();
        return null;
    }

    /// <summary>F-16C: the chosen offset at a map point: an offset aimpoint of the one selected
    /// steerpoint, or a VIP / VRP leg from its steerpoint. One at a time: points come next again.</summary>
    private string? PlaceOffsetAt(double x, double z)
    {
        var offset = PlaceOffset!;
        if (offset.StartsWith("OAP_", StringComparison.Ordinal))
        {
            if (Selected is not { } n || _selected.Count != 1 || Stpt.Point(n) is null) return "Pick one steerpoint first: its offset aimpoint goes where you right-click.";
            Stpt.SetOap(n, offset[^1] - '0', x, z, Ground(x, z));
        }
        else
        {
            if (Stpt.Point(VipVrp.SteerpointOf(offset)) is not { } from) return $"Steerpoint {VipVrp.SteerpointOf(offset)} does not exist: pick the VIP / VRP steerpoint first.";
            VipVrp.Place(offset, HornetNav.Number(from["x"]) ?? 0, HornetNav.Number(from["y"]) ?? 0, x, z, Ground(x, z));
        }
        PlaceOffset = null;
        Notify();
        return null;
    }

    /// <summary>A waypoint at a map point, at ground level; selected.</summary>
    public int? AddWaypoint(double x, double z)
    {
        Terrain();
        int? num = null;
        if (IsViper) num = Stpt.Add(x, z, Ground(x, z)) is null ? null : Stpt.Points.Count;
        else if (IsTomcat) num = TNav.AddWaypoint(TomcatPlan, TomcatAt(x, z)) is null ? null : TNav.Waypoints(TomcatPlan).Count;
        else if (Nav.Add(x, z, Ground(x, z)) is { } p) num = HornetNav.Num(p);
        if (num is null) Notify();
        else Select(num);
        return num;
    }

    /// <summary>A GPS waypoint (F-16C: DEST, F-14: additional point) at a map point, at ground level; selected.</summary>
    public int? AddGps(double x, double z)
    {
        Terrain();
        int? num = null;
        if (IsViper) num = Dest.Add(x, z, Ground(x, z)) is null ? null : Dest.Points.Count;
        else if (IsTomcat) num = TNav.AddExtra(TomcatPlan, TomcatAt(x, z)) is null ? null : TNav.Extra(TomcatPlan).Count;
        else if (Gps.Add(x, z, Ground(x, z)) is { } p) num = HornetNav.Num(p);
        if (num is null) Notify();
        else SelectGps(num);
        return num;
    }

    /// <summary>The SA type's object at a map point: a threat ring (of <see cref="SaRingType"/>), a
    /// CAP, or a point on the current line — after its selected point when that is not its last,
    /// else at its end; without a current line a new line starts (and becomes current). Null when
    /// added, else why not.</summary>
    public string? AddSa(double x, double z)
    {
        Terrain();
        if (IsViper) return AddViperSa(x, z);
        if (IsTomcat) return AddTomcatLinePoint(x, z);
        switch (SaPlace)
        {
            case SaKind.Threat:
                var type = SaRingType ?? DtcDefs.For(Cartridge.HornetType)["MEZ_THRTS_defs/MEZ_THRTS_defs"]?.AsArray()[0] as JsonObject;
                if (type is null || Sa.AddThreat(x, z, type) is null) return $"The cartridge holds {HornetSa.MaxThreats} threat rings at most.";
                SelectSa(new SaRef(SaKind.Threat, Sa.Threats.Count - 1));
                return null;
            case SaKind.Cap:
                if (Sa.AddCap(x, z) is null) return $"The cartridge holds {HornetSa.MaxCaps} CAPs at most.";
                SelectSa(new SaRef(SaKind.Cap, Sa.Caps.Count - 1));
                return null;
            default:
                var line = SaRef.LineOf(SaPlace);
                if (line is null) return null;
                var name = line == HornetSa.Corridor ? "corridor" : line.Prefix;
                if (SaLine is not { } l || Sa.LinesOf(line).ElementAtOrDefault(l) is not { } obj)
                {
                    if (Sa.AddLine(line, x, z) is null) return $"The cartridge holds {line.Max} {name} lines at most: delete one, or pick one to add to.";
                    SaLine = Sa.LinesOf(line).Count - 1;
                    SelectSa(new SaRef(SaPlace, SaLine.Value, 0));
                    return null;
                }
                var count = HornetSa.PointsOf(obj).Count;
                var at = SaSelected is { } s && s.Kind == SaPlace && s.Index == l && s.Point < count - 1 ? s.Point + 1 : count;
                if (!Sa.InsertPoint(line, l, at, x, z))
                    return $"{line.Prefix} {l + 1} has its {line.MaxPoints} points: pick “New line” to start another.";
                SelectSa(new SaRef(SaPlace, l, at));
                return null;
        }
    }

    /// <summary>F-16C: a preplanned threat (of <see cref="ViperThreatType"/>), or a point on the
    /// current geographic line, after its selected point when that is not its last, else at its end.</summary>
    private string? AddViperSa(double x, double z)
    {
        if (SaPlace == SaKind.Threat)
        {
            if (Threats.Add(x, z, Ground(x, z), ViperThreatType) is null) return $"The cartridge holds {Threats.Max} preplanned threats at most.";
            SelectSa(new SaRef(SaKind.Threat, Threats.Points.Count - 1));
            return null;
        }
        var line = (SaLine ?? 0) + 1;
        var members = Geo.Line(line);
        var pick = SaSelected is { Kind: SaKind.Geo } s && s.Index == line - 1 && s.Point < members.Count - 1 ? s.Point : members.Count - 1;
        // In the list: after the picked point of the line, or after its last, or at the end.
        var at = pick >= 0 ? members[pick] + 1 : Geo.Points.Count + 1;
        if (Geo.Insert(at, line, x, z, Ground(x, z)) is null) return $"The cartridge holds {Geo.Max} geographic line points at most.";
        SelectSa(new SaRef(SaKind.Geo, line - 1, pick + 1));
        return null;
    }

    /// <summary>F-14: a point on the plan's current line, after its selected point when that is not
    /// its last, else at its end; without a current line a new line starts.</summary>
    private string? AddTomcatLinePoint(double x, double z)
    {
        var plan = TomcatPlan;
        if (SaLine is not { } l || TNav.Lines(plan).ElementAtOrDefault(l) is not { } line)
        {
            if (TNav.AddLine(plan) is not { } added) return $"Plan {plan} has its {TNav.LineLimit(plan)} lines: delete one, or pick one to add to.";
            if (!TNav.InsertLinePoint(plan, added, 0, TomcatAt(x, z)))
            {
                TNav.RemoveLine(plan, added);
                return $"Plan {plan} holds {TomcatNav.MaxPoints} points in all.";
            }
            SaLine = added;
            SelectSa(new SaRef(SaKind.Geo, added, 0));
            return null;
        }
        var count = TomcatNav.PointsOf(line).Count;
        var at = SaSelected is { Kind: SaKind.Geo } s && s.Index == l && s.Point < count - 1 ? s.Point + 1 : count;
        if (!TNav.InsertLinePoint(plan, l, at, TomcatAt(x, z)))
            return $"Line {l + 1} has its {TomcatNav.LinePointLimit(line)} points (or the plan its {TomcatNav.MaxPoints}): pick “New line” to start another.";
        SelectSa(new SaRef(SaKind.Geo, l, at));
        return null;
    }

    // ---- GPS / DEST / additional points of any aircraft (the map works on these) -------------------

    /// <summary>The F/A-18C's GPS waypoints, the F-16C's DEST points or the F-14 plan's additional
    /// points: number, point, map label.</summary>
    public IReadOnlyList<(int Num, JsonObject Point, string Label)> GpsPoints => IsViper
        ? Dest.Points.Select((p, i) => (i + 1, p, (string?)p["text"] ?? $"D{ViperDest.FirstNumber + i}")).ToList()
        : IsTomcat
            ? TNav.Extra(TomcatPlan).Select((p, i) => (i + 1, p, (string?)p["name"] is { Length: > 0 } n ? n : $"AP{i + 1}")).ToList()
            : Gps.All.Select(p => (HornetNav.Num(p), p, (string?)p["text"] ?? $"GW{HornetNav.Num(p)}")).ToList();

    public JsonObject? GpsPoint(int num) => IsViper ? Dest.Point(num) : IsTomcat ? TNav.Extra(TomcatPlan).ElementAtOrDefault(num - 1) : Gps.Point(num);

    public void MoveGps(int num, double x, double z)
    {
        if (IsViper) Dest.Move(num, x, z);
        else if (IsTomcat) TNav.MoveExtra(TomcatPlan, num, TomcatAt(x, z));
        else Gps.Move(num, x, z);
    }

    /// <summary>How the map and menus call a GPS point: "GPS waypoint 3", "DEST 83", "Additional point 2".</summary>
    public string GpsName(int num) => IsViper ? $"DEST {ViperDest.FirstNumber - 1 + num}" : IsTomcat ? $"Additional point {num}" : $"GPS waypoint {num}";

    // ---- Moving ------------------------------------------------------------------------------------

    /// <summary>A waypoint (F-16C: steerpoint; F-14: of the open plan) by number.</summary>
    public JsonObject? Waypoint(int num) =>
        IsViper ? Stpt.Point(num) : IsTomcat ? TNav.Waypoints(TomcatPlan).ElementAtOrDefault(num - 1) : Nav.Waypoint(num);

    /// <summary>The open jet's waypoints by number (F-14: of the open plan).</summary>
    public IReadOnlyList<(int Num, JsonObject Point)> Waypoints =>
        IsViper ? Stpt.Points.Select((p, i) => (i + 1, p)).ToList()
        : IsTomcat ? TNav.Waypoints(TomcatPlan).Select((p, i) => (i + 1, p)).ToList()
        : Nav.Waypoints.Select(p => (HornetNav.Num(p), p)).ToList();

    public void MoveWaypoint(int num, double x, double z)
    {
        if (IsViper) Stpt.Move(num, x, z);
        else if (IsTomcat) TNav.MoveWaypoint(TomcatPlan, num, TomcatAt(x, z));
        else Nav.Move(num, x, z);
    }

    /// <summary>A geographic line's point (F-16C: by list number; F-14: the plan's line point).</summary>
    private JsonObject? GeoPoint(SaRef r) => IsTomcat
        ? TNav.Lines(TomcatPlan).ElementAtOrDefault(r.Index) is { } l ? TomcatNav.PointsOf(l).ElementAtOrDefault(r.Point) : null
        : Geo.Line(r.Index + 1).ElementAtOrDefault(r.Point) is var n and > 0 ? Geo.Point(n) : null;

    public void MoveSa(SaRef r, double x, double z)
    {
        if (r.Kind == SaKind.Geo)
        {
            if (IsTomcat) TNav.MoveLinePoint(TomcatPlan, r.Index, r.Point, TomcatAt(x, z));
            else if (Geo.Line(r.Index + 1).ElementAtOrDefault(r.Point) is var n and > 0) Geo.Move(n, x, z);
            return;
        }
        if (IsViper)
        {
            Threats.Move(r.Index + 1, x, z);
            return;
        }
        switch (r.Kind)
        {
            case SaKind.Threat: Sa.MoveThreat(r.Index, x, z); break;
            case SaKind.Cap: Sa.MoveCap(r.Index, x, z); break;
            default: Sa.MovePoint(r.Line!, r.Index, r.Point, x, z); break;
        }
    }

    /// <summary>A map position (x, z) of an SA object, or null when it is gone.</summary>
    public (double X, double Z)? SaPosition(SaRef r)
    {
        var o = r.Kind == SaKind.Geo ? GeoPoint(r)
            : IsViper ? Threats.Point(r.Index + 1)
            : r.Kind switch
            {
                SaKind.Threat => Sa.Threats.ElementAtOrDefault(r.Index),
                SaKind.Cap => Sa.Caps.ElementAtOrDefault(r.Index),
                _ => Sa.LinesOf(r.Line!).ElementAtOrDefault(r.Index) is { } l ? HornetSa.PointsOf(l).ElementAtOrDefault(r.Point) : null,
            };
        return o is null ? null : (HornetNav.Number(o["x"]) ?? 0, HornetNav.Number(o["y"]) ?? 0);
    }

    /// <summary>The one selected point of the open tab moved to a map point (typed coordinates);
    /// false when not exactly one is selected.</summary>
    public bool MoveSelectedTo(double x, double z)
    {
        switch (Place)
        {
            case DtcPlace.Gps when _selectedGps.Count == 1:
                MoveGps(_selectedGps.First(), x, z);
                break;
            case DtcPlace.Sa when _selectedSa.Count == 1:
                MoveSa(_selectedSa.First(), x, z);
                break;
            case DtcPlace.Waypoints when _selected.Count == 1:
                MoveWaypoint(_selected.First(), x, z);
                break;
            case DtcPlace.Jdam when IsTomcat:
                MoveJdam(JdamPick.Station, JdamPick.Target, x, z);
                break;
            default:
                return false;
        }
        Notify();
        return true;
    }

    // ---- Deleting ----------------------------------------------------------------------------------

    /// <summary>The open tab's selection deleted (Delete button, Delete key).</summary>
    public void DeleteSelected()
    {
        switch (Place)
        {
            case DtcPlace.Gps:
                foreach (var n in _selectedGps.OrderDescending().ToList())
                    if (IsViper) Dest.Remove(n);
                    else if (IsTomcat) TNav.RemoveExtra(TomcatPlan, n);
                    else Gps.Remove(n);
                SelectGps(null);
                break;
            case DtcPlace.Sa:
                DeleteSa(_selectedSa.ToList());
                SelectSa(null);
                break;
            case DtcPlace.Jdam:
                DeleteJdam(JdamPick.Station, JdamPick.Target);
                break;
            default:
                if (IsViper) Stpt.RemoveMany(_selected);
                else if (IsTomcat) foreach (var n in _selected.OrderDescending().ToList()) TNav.RemoveWaypoint(TomcatPlan, n);
                else foreach (var n in _selected.ToList()) Nav.Remove(n);
                Select(null);
                break;
        }
    }

    /// <summary>The numbers after a deleted one move up: so does a selection.</summary>
    private static void Shift(HashSet<int> set, int deleted)
    {
        var kept = set.Where(n => n != deleted).Select(n => n > deleted ? n - 1 : n).ToList();
        set.Clear();
        set.UnionWith(kept);
    }

    /// <summary>Delete mode's right-click: the waypoint, GPS waypoint or SA object under the pointer.</summary>
    public void DeleteWaypoint(int num)
    {
        if (IsViper) Stpt.Remove(num);
        else if (IsTomcat) TNav.RemoveWaypoint(TomcatPlan, num);
        else Nav.Remove(num);
        if (IsViper || IsTomcat) Shift(_selected, num);
        else _selected.Remove(num);
        Selected = _selected.Count > 0 ? _selected.Max() : null;
        Notify();
    }

    public void DeleteGps(int num)
    {
        if (IsViper) Dest.Remove(num);
        else if (IsTomcat) TNav.RemoveExtra(TomcatPlan, num);
        else Gps.Remove(num);
        if (IsViper || IsTomcat) Shift(_selectedGps, num);
        else _selectedGps.Remove(num);
        SelectedGps = _selectedGps.Count > 0 ? _selectedGps.Max() : null;
        Notify();
    }

    public void DeleteSaObject(SaRef r)
    {
        DeleteSa([r]);
        _selectedSa.Clear();
        SaSelected = null;
        Notify();
    }

    /// <summary>SA objects deleted, from the end of each list back (indexes stay right). A line left
    /// without points goes; the current line follows what is left.</summary>
    private void DeleteSa(IReadOnlyList<SaRef> refs)
    {
        if (IsTomcat)
        {
            foreach (var r in refs.Where(r => r.Kind == SaKind.Geo).OrderByDescending(r => r.Index).ThenByDescending(r => r.Point))
                TNav.RemoveLinePoint(TomcatPlan, r.Index, r.Point);
            if (SaLine is { } tl && tl >= TNav.Lines(TomcatPlan).Count) SaLine = LastLine(SaKind.Geo);
            return;
        }
        if (IsViper)
        {
            // List numbers first, then from the last back (the numbers stay right).
            var geo = refs.Where(r => r.Kind == SaKind.Geo).Select(r => Geo.Line(r.Index + 1).ElementAtOrDefault(r.Point)).Where(n => n > 0).ToList();
            foreach (var n in geo.Distinct().OrderDescending()) Geo.Remove(n);
            foreach (var r in refs.Where(r => r.Kind == SaKind.Threat).OrderByDescending(r => r.Index)) Threats.Remove(r.Index + 1);
            return;
        }
        foreach (var r in refs.Where(r => r.Kind == SaKind.Threat).OrderByDescending(r => r.Index)) Sa.RemoveThreat(r.Index);
        foreach (var r in refs.Where(r => r.Kind == SaKind.Cap).OrderByDescending(r => r.Index)) Sa.RemoveCap(r.Index);
        foreach (var r in refs.Where(r => r.Line is not null).OrderByDescending(r => r.Index).ThenByDescending(r => r.Point))
            Sa.RemovePoint(r.Line!, r.Index, r.Point);
        if (SaRef.LineOf(SaPlace) is { } line && (SaLine is not { } l || l >= Sa.LinesOf(line).Count)) SaLine = LastLine(SaPlace);
    }

    /// <summary>Something changed: the shared points go to the other jets, then everyone redraws.</summary>
    public void Notify()
    {
        Share();
        Changed?.Invoke();
    }
}
