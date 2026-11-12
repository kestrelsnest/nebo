using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Media = System.Windows.Media;
using Nebo.Client.Core;
using Nebo.Client.Map;
using Nebo.Client.Map.Airfields;
using Nebo.Client.Map.Gpu;

namespace Nebo.Client.Panels;

/// <summary>One map window. Each has its own base map, label level and view; any number can
/// be open, docked or floating. Everything on it is drawn by Skia into one surface (Map/Gpu).</summary>
public partial class MapPanel : UserControl
{
    private const double CaucasusX = 4_680_000, CaucasusY = 5_220_000;
    private const double DefaultResolution = 1200;

    private readonly MapSettings _settings;
    private bool _viewPlaced;
    private Point? _pressAt;
    private bool _initializing = true;

    public string ContentId { get; }

    /// <summary>The ATC tab's airfield map: airfields drawn in full (diagram, taxiways,
    /// parking); positioned by the ATC airfield list rather than fitting the whole picture.</summary>
    public bool IsAirfieldView { get; }

    // Layers, bottom-up: base maps, the dimming, then overlays (static first, tracks, ruler).
    private readonly ScopeReliefSk _scopeRelief = new();
    private readonly ScopeFeaturesSk _scopeFeatures = new();
    private readonly ScopeLinesSk _scope = new();
    private readonly WebTilesSk _esri = WebTilesSk.EsriImagery();
    private readonly DcsMapSk _dcsMap = new();
    private readonly MgrsGridSk _mgrs = new();
    private readonly DcsChartsSk _charts = new();
    private readonly DcsSatelliteSk _satellite = new();
    private readonly DimSk _dim = new();
    private readonly TheatreSk _theatre = new();
    private readonly AirfieldsSk _airfields = new();
    private readonly FootholdSk _foothold = new();
    private readonly SrsCoverageSk _srsCoverage = new();
    private readonly GciSk _gci = new();
    private readonly SymbolsSk _symbols = new();
    private readonly MeasureSk _measure = new();
    private readonly DtcSk _dtc = new();
    private readonly AreasSk _areas = new();
    /// <summary>A right press finished an area: its release opens no menu.</summary>
    private bool _swallowRightUp;
    // DTC points being dragged: where the press was and where each selected one started.
    private (bool Gps, (double X, double Z) Start, Dictionary<int, (double X, double Z)> From)? _dtcDrag;
    private ((double X, double Z) Start, Dictionary<Dtc.SaRef, (double X, double Z)> From)? _dtcSaDrag;
    /// <summary>F-14: a JDAM target, its launch point or its heading handle being dragged.</summary>
    private Map.Gpu.DtcSk.JdamHit? _dtcJdamDrag;
    // A press on a DTC point: a click without moving selects that one alone.
    private (Point At, Action SelectAlone)? _dtcPress;

    private MapNavigator Nav => Surface.Navigator;

    public MapPanel(string contentId, bool airfieldView = false)
    {
        ContentId = contentId;
        IsAirfieldView = airfieldView;
        _settings = AppState.Settings.MapFor(contentId);
        InitializeComponent();
        DiagramsBox.Visibility = airfieldView ? Visibility.Visible : Visibility.Collapsed;

        foreach (var layer in new MapLayer[] { _scopeRelief, _scopeFeatures, _scope, _esri, _dcsMap, _mgrs, _charts, _satellite,
                     _dim, _theatre, _airfields, _foothold, _areas, _srsCoverage, _gci, _symbols, _dtc, _measure })
            Surface.Add(layer);
        _gci.ShowMarks = _settings.ShowMarks;
        _gci.ShowDesignations = _settings.ShowDesignations;
        _gci.ViewProvider = () => Nav.View;
        _dim.Amount = 1 - _settings.Brightness;
        _airfields.FullDetail = airfieldView;
        _airfields.ShowDiagrams = _settings.ShowDiagrams;
        _airfields.Visible = _settings.ShowAirfields;
        _foothold.Visible = _settings.FootholdZones;
        _foothold.ViewProvider = () => Nav.View;
        _symbols.Settings = _settings;
        _symbols.Owners = _areas.Owners = _settings.Owners;
        ShowOwners();
        AreaButton.Visibility = OwnersButton.Visibility = airfieldView ? Visibility.Collapsed : Visibility.Visible;
        if (!airfieldView) MapArea.Children.Add(new MessageTrayView());
        _symbols.PlayersOnlyApplies = WorkspacePreset == MainWindow.GciPreset;
        _measure.Obstacles = () => _symbols.Obstacles;
        _symbols.ViewProvider = () => Nav.View;
        Nav.Changed += OnViewChanged;

        MapArea.SizeChanged += (_, _) =>
        {
            ApplyTheatreBounds();
            if (_pendingAirfield is { } p && DateTime.UtcNow - p.At < TimeSpan.FromSeconds(3))
                Dispatcher.BeginInvoke(() => CenterAirfield(p.Id), System.Windows.Threading.DispatcherPriority.Loaded);
        };

        BaseMapBox.ItemsSource = Enum.GetValues<BaseMap>();
        LabelsBox.ItemsSource = Enum.GetValues<LabelLevel>();
        TagsBox.ItemsSource = Enum.GetValues<TagPlacement>();
        TagsBox.SelectedItem = _settings.Tags;
        BaseMapBox.SelectedItem = _settings.BaseMap;
        LabelsBox.SelectedItem = _settings.Labels;
        ThreatRingsBox.IsChecked = _settings.ThreatRings;
        MarksBox.IsChecked = _settings.ShowMarks;
        DesignationsBox.IsChecked = _settings.ShowDesignations;
        FootholdBox.IsChecked = _settings.FootholdZones;
        AirfieldsBox.IsChecked = _settings.ShowAirfields;
        DtcBox.IsChecked = _settings.ShowDtc;
        DiagramsBox.IsChecked = _settings.ShowDiagrams;
        GridBox.IsChecked = _settings.ShowGrid;
        BrightnessSlider.Value = _settings.Brightness;
        _initializing = false;
        WireFilterStages();
        ApplyBaseMap();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>Centre (Mercator) and resolution (Mercator metres per DIP) of the view.</summary>
    public (double X, double Y, double Resolution) ViewCenter => (Nav.CenterX, Nav.CenterY, Nav.Resolution);

    private void OnViewChanged()
    {
        // The bullseye call follows the ground under a still pointer while the map moves.
        if (_measure.BullsCursor is null) return;
        var p = Mouse.GetPosition(MapArea);
        _measure.BullsCursor = (p.X, p.Y);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AppState.Store.Changed += Surface.Invalidate;
        Srs.SrsListen.Instance.TalkChanged += Surface.Invalidate;
        AppState.Store.Changed += OnPictureChanged;
        AppState.Store.SelectionChanged += Surface.Invalidate;
        AppState.Store.TheatreChanged += ApplyTheatreBounds;
        AppState.Store.FootholdChanged += Surface.Invalidate;
        AppState.Store.AirfieldsChanged += Surface.Invalidate;
        AppState.ZoneSelectionChanged += OnZoneSelectionChanged;
        AppState.AirfieldSelectionChanged += OnAirfieldSelectionChanged;
        AppState.BullsKeyChanged += OnBullsKeyChanged;
        AppState.Packages.Changed += Surface.Invalidate;
        AppState.Board.Changed += Surface.Invalidate;
        AppState.RwrClockRequested += OnRwrKey;
        AppState.NctrRequested += OnNctrKey;
        AppState.PointInfoRequested += OnPointInfoKey;
        LaunchAlarm.JumpRequested += OnLaunchJump;
        Srs.SrsCoverage.Instance.Changed += Surface.Invalidate;
        Dtc.DtcSession.Instance.Changed += Surface.Invalidate;
        Dtc.DtcSession.Instance.Changed += DtcCursor;
        // The cartridge: edited in the DTC tab; elsewhere only shown, with the DTC filter.
        DtcBox.Visibility = InDtc ? Visibility.Collapsed : Visibility.Visible;
        ApplyDtcFilter();
        DtcCursor();
        _srsCoverage.Visible = !IsAirfieldView;  // the ATC tab's own airfield map stays clean
        AppState.Store.NctrChanged += Surface.Invalidate;
        AppState.Store.DesignationsChanged += Surface.Invalidate;
        AppState.Tags.Changed += Surface.Invalidate;
        AppState.PlayersOnlyChanged += Surface.Invalidate;
        AppState.Engagements.Changed += _measure.Invalidate;
        AppState.Flights.Changed += OnFlightsChanged;
        FlightDesk.AreaChanged += OnFlightsChanged;
        // The methods being worked flash beside their contacts while an NCTR request runs.
        // So do pilots whose handoff waits for an answer (250 ms halves).
        _nctrFlash ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(250), System.Windows.Threading.DispatcherPriority.Background,
            (_, _) =>
            {
                if (AppState.Flights.Handoffs.Count > 0 || AppState.Store.Nctr?.Requests.Any(r => r.Running) == true || AppState.Store.Launches.Count > 0)
                    Surface.Invalidate();
            }, Dispatcher);
        _nctrFlash.Start();
        OnAirfieldSelectionChanged();  // picked before this map loaded
        ApplyTrackFilters();
        PlaceInitialView();
        ApplyTheatreBounds();
    }

    /// <summary>The workspace this map belongs to: "GCI" or "ATC" (set by the workspace that made it).</summary>
    public string WorkspacePreset { get; init; } = MainWindow.GciPreset;

    private bool InAtc => WorkspacePreset == MainWindow.AtcPreset;
    private bool InDtc => WorkspacePreset == MainWindow.DtcPreset;

    /// <summary>Ground units and landed aircraft as the map's settings say, or by its window:
    /// an ATC window hides both, other windows hide landed aircraft only.</summary>
    private void ApplyTrackFilters()
    {
        var atc = InAtc;
        _symbols.ShowGroundUnits = _settings.ShowGroundUnits ?? !atc;
        _symbols.ShowLanded = _settings.ShowLanded ?? false;
        var was = _initializing;
        _initializing = true;
        GroundUnitsBox.IsChecked = _symbols.ShowGroundUnits;
        LandedBox.IsChecked = _symbols.ShowLanded;
        _initializing = was;
        ApplyFilterLevels();
    }

    private void TrackFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.ShowGroundUnits = GroundUnitsBox.IsChecked == true;
        _settings.ShowLanded = LandedBox.IsChecked == true;
        ApplyTrackFilters();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Docking moves the panel between windows (Unloaded then Loaded); resubscribe on load.
        AppState.Store.Changed -= Surface.Invalidate;
        Srs.SrsListen.Instance.TalkChanged -= Surface.Invalidate;
        Dtc.DtcSession.Instance.Changed -= Surface.Invalidate;
        Dtc.DtcSession.Instance.Changed -= DtcCursor;
        AppState.Store.Changed -= OnPictureChanged;
        AppState.Store.SelectionChanged -= Surface.Invalidate;
        AppState.Store.TheatreChanged -= ApplyTheatreBounds;
        AppState.Store.FootholdChanged -= Surface.Invalidate;
        AppState.Store.AirfieldsChanged -= Surface.Invalidate;
        AppState.ZoneSelectionChanged -= OnZoneSelectionChanged;
        AppState.AirfieldSelectionChanged -= OnAirfieldSelectionChanged;
        AppState.BullsKeyChanged -= OnBullsKeyChanged;
        AppState.Packages.Changed -= Surface.Invalidate;
        AppState.Board.Changed -= Surface.Invalidate;
        AppState.RwrClockRequested -= OnRwrKey;
        AppState.NctrRequested -= OnNctrKey;
        AppState.PointInfoRequested -= OnPointInfoKey;
        LaunchAlarm.JumpRequested -= OnLaunchJump;
        Srs.SrsCoverage.Instance.Changed -= Surface.Invalidate;
        AppState.Store.NctrChanged -= Surface.Invalidate;
        AppState.Store.DesignationsChanged -= Surface.Invalidate;
        AppState.Tags.Changed -= Surface.Invalidate;
        AppState.PlayersOnlyChanged -= Surface.Invalidate;
        AppState.Engagements.Changed -= _measure.Invalidate;
        AppState.Flights.Changed -= OnFlightsChanged;
        FlightDesk.AreaChanged -= OnFlightsChanged;
        StopArea();
        _nctrFlash?.Stop();
        CloseRwrClock();
        CloseNctr();
    }

    private void OnAirfieldSelectionChanged()
    {
        _airfields.SelectedId = AppState.SelectedAirfieldId;
        // Approach range marks belong to ATC: the ATC workspace's maps.
        _airfields.ShowApproaches = IsAirfieldView || InAtc;
        DiagramsBox.Visibility = IsAirfieldView || InAtc ? Visibility.Visible : Visibility.Collapsed;
        _airfields.Invalidate();
    }

    /// <summary>Centres on an airfield and zooms so its runways fill the view (ATC view).</summary>
    public void ShowAirfield(int id)
    {
        // Centre after layout, and again if the panel is resized right after (a floated or
        // freshly docked panel is still settling): otherwise the map centres on a stale size.
        _pendingAirfield = (id, DateTime.UtcNow);
        Dispatcher.BeginInvoke(() => CenterAirfield(id), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private (int Id, DateTime At)? _pendingAirfield;

    private void CenterAirfield(int id)
    {
        if (AppState.Store.Theatre is not { } theatre || AppState.Store.Airfields.FirstOrDefault(a => a.Id == id) is not { } field) return;
        // Centre on the runways: DCS's airfield reference point can sit at one end of the field.
        var (cx, cz) = field.Runways.Count > 0 ? (field.Runways.Average(r => r.X), field.Runways.Average(r => r.Z)) : (field.X, field.Z);
        var (lat, lon) = theatre.ToLatLon(cx, cz);
        var longest = field.Runways.Count > 0 ? field.Runways.Max(r => r.Length) : 1500;
        var res = Math.Clamp(longest * 1.4 * Mercator.ScaleAt(lat) / Math.Max(300, Nav.Width), 0.6, 12);
        ShowAt(lat, lon, res);
    }

    private void OnZoneSelectionChanged()
    {
        _foothold.SelectedZone = AppState.SelectedZone;
        _foothold.Invalidate();
    }

    /// <summary>Centres on a zone (or, <paramref name="westEdge"/>, on the west point of its circle).</summary>
    public void CenterOnZone(string name, bool westEdge = false)
    {
        if (AppState.Store.Theatre is { } theatre && AppState.Store.Foothold?.Find(name) is { } zone)
        {
            var (lat, lon) = theatre.ToLatLon(zone.X, westEdge ? zone.Z - zone.Radius : zone.Z);
            var (x, y) = Mercator.FromLonLat(lon, lat);
            Nav.CenterOn(x, y);
        }
    }

    /// <summary>Keeps the view on the DCS theatre: the centre cannot leave it and zooming out
    /// stops once the whole theatre is in view.</summary>
    private void ApplyTheatreBounds()
    {
        if (AppState.Store.Theatre is not { } theatre) return;
        var pts = theatre.Border().Select(p => Mercator.FromLonLat(p.Lon, p.Lat)).ToList();
        double minX = pts.Min(p => p.X), minY = pts.Min(p => p.Y), maxX = pts.Max(p => p.X), maxY = pts.Max(p => p.Y);
        Nav.PanBounds = (minX, minY, maxX, maxY);
        if (Nav.Width > 0 && Nav.Height > 0)
            Nav.ZoomBounds = (0.5, Math.Max((maxX - minX) / Nav.Width, (maxY - minY) / Nav.Height) * 1.05);
        Nav.CenterOnAndZoomTo(Nav.CenterX, Nav.CenterY, Nav.Resolution);  // re-clamp
    }

    private void PlaceInitialView()
    {
        if (_viewPlaced) return;
        if (_settings.CenterX is { } x && _settings.CenterY is { } y && _settings.Resolution is { } res)
        {
            Nav.CenterOnAndZoomTo(x, y, res);
            _viewPlaced = true;
        }
        else if (IsAirfieldView || !FitPicture())
        {
            // The ATC view waits for an airfield; others show the Caucasus and fit on the first picture.
            Nav.CenterOnAndZoomTo(CaucasusX, CaucasusY, DefaultResolution);
        }
    }

    private void OnPictureChanged()
    {
        if (!_viewPlaced && !IsAirfieldView) FitPicture();
    }

    /// <summary>Zooms to all current tracks. Returns false when there are none.</summary>
    public bool FitPicture()
    {
        var points = AppState.Store.Tracks
            .Where(t => !t.Lost && t.Unit.Lat is not null && t.Unit.Lon is not null)
            .Select(t => Mercator.FromLonLat(t.Unit.Lon!.Value, t.Unit.Lat!.Value))
            .ToList();
        if (points.Count == 0) return false;
        var pad = 20_000;
        Nav.ZoomToBox(points.Min(p => p.X) - pad, points.Min(p => p.Y) - pad, points.Max(p => p.X) + pad, points.Max(p => p.Y) + pad);
        _viewPlaced = true;
        return true;
    }

    /// <summary>The launch jump key: GCI maps on screen centre on the launch's target.</summary>
    private bool OnLaunchJump(Launch launch)
    {
        if (WorkspacePreset != MainWindow.GciPreset || !IsVisible || AppState.Store.Find(launch.Target) is null) return false;
        CenterOn(launch.Target);
        return true;
    }

    public void CenterOn(long trackId)
    {
        if (AppState.Store.Find(trackId)?.Unit is { Lat: { } lat, Lon: { } lon })
        {
            var (x, y) = Mercator.FromLonLat(lon, lat);
            Nav.CenterOn(x, y);
        }
    }

    // ---- Stream Deck (Integration.DeckLink): labels, label position and filters ------------

    public LabelLevel Labels => _settings.Labels;
    public void SetLabels(LabelLevel level) => LabelsBox.SelectedItem = level;  // its handler applies it
    public TagPlacement LabelPos => _settings.Tags;
    public void SetLabelPos(TagPlacement placement) => TagsBox.SelectedItem = placement;

    /// <summary>The map filters the Stream Deck toggles, by name.</summary>
    public static readonly string[] DeckFilters = ["ground", "landed", "rings", "marks", "tgp", "zones", "airfields"];

    private CheckBox? FilterBox(string name) => name switch
    {
        "ground" => GroundUnitsBox,
        "landed" => LandedBox,
        "rings" => ThreatRingsBox,
        "marks" => MarksBox,
        "tgp" => DesignationsBox,
        "zones" => FootholdBox,
        "airfields" => AirfieldsBox,
        _ => null,
    };

    public bool? Filter(string name) => FilterBox(name)?.IsChecked;

    /// <summary>Sets a filter as its toolbar box would (the box's handler applies it).</summary>
    public void SetFilter(string name, bool on)
    {
        if (FilterBox(name) is { } box) box.IsChecked = on;
    }

    // ---- Filter stages: off, 30 %, 60 %, 100 % (level 0-3; off = the box unchecked) --------

    private static readonly float[] Stages = [0, 0.3f, 0.6f, 1f];

    private int StageOf(string name) => _settings.FilterLevels.TryGetValue(name, out var l) ? Math.Clamp(l, 1, 3) : 3;

    /// <summary>A filter's stage: 0 off, 1 dim (30 %), 2 medium (60 %), 3 bright.</summary>
    public int FilterLevel(string name) => Filter(name) == true ? StageOf(name) : 0;

    /// <summary>Sets a filter's stage (0 turns it off and keeps the stage it had).</summary>
    public void SetFilterLevel(string name, int level)
    {
        if (level > 0) _settings.FilterLevels[name] = Math.Clamp(level, 1, 3);
        SetFilter(name, level > 0);
        ApplyFilterLevels();
    }

    /// <summary>Left click and the Stream Deck key: off → 30 % → 60 % → 100 % → off.</summary>
    public void CycleFilter(string name) => SetFilterLevel(name, (FilterLevel(name) + 1) % 4);

    private void WireFilterStages()
    {
        foreach (var name in DeckFilters)
        {
            if (FilterBox(name) is not { } box) continue;
            box.Tag = box.Content;  // the plain label; a dimmed stage is added to it
            box.PreviewMouseLeftButtonDown += (_, e) => { CycleFilter(name); e.Handled = true; };
            box.PreviewMouseRightButtonDown += (_, e) => { SetFilter(name, box.IsChecked != true); ApplyFilterLevels(); e.Handled = true; };
            box.PreviewMouseWheel += (_, e) =>
            {
                var level = FilterLevel(name);
                SetFilterLevel(name, level == 0 ? (e.Delta > 0 ? 1 : 0) : Math.Clamp(level + Math.Sign(e.Delta), 1, 3));
                e.Handled = true;
            };
            var tip = box.ToolTip is string t ? t + "\n" : "";
            box.ToolTip = tip + "Click: off → 30 % → 60 % → 100 %. Right click: on/off. Wheel: brighter/dimmer";
        }
        ApplyFilterLevels();
    }

    private void ApplyFilterLevels()
    {
        float O(string name) => Stages[StageOf(name)];
        _symbols.GroundOpacity = O("ground");
        _symbols.LandedOpacity = O("landed");
        _symbols.RingsOpacity = O("rings");
        _gci.MarksOpacity = O("marks");
        _gci.DesignationsOpacity = O("tgp");
        _foothold.Opacity = O("zones");
        _airfields.Opacity = O("airfields");
        foreach (var name in DeckFilters)
            if (FilterBox(name) is { Tag: { } label } box)
                box.Content = FilterLevel(name) is 1 or 2 ? $"{label} {Stages[StageOf(name)] * 100:0}%" : label;
        Surface.Invalidate();
    }

    /// <summary>Shows two points with a margin around them (an engagement line's ends): centred
    /// between them, zoomed so both fit, never closer than about 5 NM across.</summary>
    public void Fit(double lat1, double lon1, double lat2, double lon2)
    {
        var (x1, y1) = Mercator.FromLonLat(lon1, lat1);
        var (x2, y2) = Mercator.FromLonLat(lon2, lat2);
        double w = Math.Max(200, MapArea.ActualWidth), h = Math.Max(200, MapArea.ActualHeight);
        var resolution = Math.Max(Math.Max(Math.Abs(x2 - x1) / w, Math.Abs(y2 - y1) / h) * 1.5, 10000 / Math.Min(w, h));
        Nav.CenterOnAndZoomTo((x1 + x2) / 2, (y1 + y2) / 2, resolution);
        _viewPlaced = true;
    }

    /// <summary>Fits an engagement line's two ends (their tracks' positions now).</summary>
    public void FitEngagement(EngagementLine line)
    {
        Unit? Find(long id) => AppState.Store.Find(id)?.Unit;
        var (alat, alon, _) = EngagementBook.Resolve(line.From, Find);
        var (blat, blon, _) = EngagementBook.Resolve(line.To, Find);
        Fit(alat, alon, blat, blon);
    }

    /// <summary>Centres on a point at a resolution (Mercator metres per DIP).</summary>
    public void ShowAt(double lat, double lon, double resolution)
    {
        var (x, y) = Mercator.FromLonLat(lon, lat);
        Nav.CenterOnAndZoomTo(x, y, resolution);
        _viewPlaced = true;
    }

    public void SetGrid(bool on) => GridBox.IsChecked = on;

    /// <summary>Switches the base map as the toolbar does (tests).</summary>
    public void SetBaseMap(BaseMap map) => BaseMapBox.SelectedItem = map;

    public void SetBrightness(double value) => BrightnessSlider.Value = Math.Clamp(value, BrightnessSlider.Minimum, 1);

    /// <summary>Debug aid: Natural Earth coastline over any base map, to check alignment.</summary>
    public void ShowCoastlineOverlay() => _scope.Visible = true;

    public void SaveView()
    {
        if (Nav.Resolution <= 0) return;
        _settings.CenterX = Nav.CenterX;
        _settings.CenterY = Nav.CenterY;
        _settings.Resolution = Nav.Resolution;
    }

    private void ApplyBaseMap()
    {
        var b = _settings.BaseMap;
        var scope = b == BaseMap.Scope;
        var dcsMap = b == BaseMap.DcsMap;
        var dcsCharts = b == BaseMap.DcsCharts;
        var dcsSatellite = b == BaseMap.DcsSatellite;
        GridBox.Visibility = dcsMap ? Visibility.Visible : Visibility.Collapsed;
        Surface.BackColor = dcsSatellite ? DcsSatelliteSk.Sea
            : scope ? MapStyle.Color("scope.background")
            : dcsMap || dcsCharts ? MapStyle.Color("baseMaps.dcsBackground")
            : MapStyle.Color("baseMaps.webBackground");
        _scope.Visible = _scopeRelief.Visible = _scopeFeatures.Visible = scope;
        _esri.Visible = b == BaseMap.Satellite;
        _dcsMap.Visible = dcsMap;
        _mgrs.Visible = dcsMap && _settings.ShowGrid;
        _charts.Visible = dcsCharts;
        _satellite.Visible = dcsSatellite;
        // A dimmed light map reads as dark: label halos follow what the controller sees.
        var light = (dcsMap || dcsCharts) && _settings.Brightness >= 0.65;
        _theatre.LightBaseMap = light;
        _theatre.ShowTowns = scope || dcsMap || dcsSatellite;  // charts and web tiles print their own names
        var halo = light ? SkPalette.HaloLight : SkPalette.HaloDark;
        _symbols.Halo = halo;
        _foothold.Halo = halo;
        _gci.Halo = halo;
        _airfields.Halo = SkPalette.HaloDark;  // airfield labels are light: always a dark halo
        _airfields.NightDiagrams = !light;     // NVG-green pages on dark maps
        var attribution = TileBaseMaps.AttributionFor(b);
        AttributionText.Text = attribution;
        AttributionText.Visibility = attribution.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Surface.Invalidate();
    }

    // ---- Toolbar ------------------------------------------------------------------------

    private void BaseMapBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || BaseMapBox.SelectedItem is not BaseMap b) return;
        _settings.BaseMap = b;
        ApplyBaseMap();
    }

    private void TagsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || TagsBox.SelectedItem is not TagPlacement p) return;
        _settings.Tags = p;
        Surface.Invalidate();
    }

    private void LabelsBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || LabelsBox.SelectedItem is not LabelLevel l) return;
        _settings.Labels = l;
        Surface.Invalidate();
    }

    private void ThreatRingsBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.ThreatRings = ThreatRingsBox.IsChecked == true;
        Surface.Invalidate();
    }

    private void MarksBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.ShowMarks = MarksBox.IsChecked == true;
        _gci.ShowMarks = _settings.ShowMarks;
        Surface.Invalidate();
    }

    private void DesignationsBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.ShowDesignations = DesignationsBox.IsChecked == true;
        _gci.ShowDesignations = _settings.ShowDesignations;
        Surface.Invalidate();
    }

    private void FootholdBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.FootholdZones = FootholdBox.IsChecked == true;
        _foothold.Visible = _settings.FootholdZones;
    }

    private void DtcBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.ShowDtc = DtcBox.IsChecked == true;
        ApplyDtcFilter();
    }

    private void ApplyDtcFilter()
    {
        _dtc.ReadOnly = !InDtc;
        _dtc.Visible = InDtc || _settings.ShowDtc;
        Surface.Invalidate();
    }

    private void AirfieldsBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.ShowAirfields = AirfieldsBox.IsChecked == true;
        _airfields.Visible = _settings.ShowAirfields;
    }

    private void DiagramsBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.ShowDiagrams = DiagramsBox.IsChecked == true;
        _airfields.ShowDiagrams = _settings.ShowDiagrams;
        _airfields.Invalidate();
    }

    private void GridBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _settings.ShowGrid = GridBox.IsChecked == true;
        _mgrs.Visible = _settings.ShowGrid && _settings.BaseMap == BaseMap.DcsMap;
    }

    private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        var wasLight = _settings.Brightness >= 0.65;
        _settings.Brightness = Math.Round(e.NewValue, 2);
        _dim.Amount = 1 - _settings.Brightness;
        _dim.Invalidate();
        if (wasLight != _settings.Brightness >= 0.65) ApplyBaseMap();  // halos switch light/dark
    }

    private void Fit_Click(object sender, RoutedEventArgs e) => FitPicture();

    // ---- Controllers: owner colours and areas of operations (0.17) ----------------------------

    private void OnFlightsChanged()
    {
        _areas.Invalidate();
        _symbols.Invalidate();
    }

    private void Owners_Click(object sender, RoutedEventArgs e)
    {
        _settings.Owners = (OwnerView)(((int)_settings.Owners + 1) % 3);
        _symbols.Owners = _areas.Owners = _settings.Owners;
        ShowOwners();
        OnFlightsChanged();
    }

    private void ShowOwners() => OwnersButton.Content = _settings.Owners switch
    {
        OwnerView.All => "Owners: all",
        OwnerView.Mine => "Owners: mine",
        _ => "Owners: off",
    };

    private void Area_Click(object sender, RoutedEventArgs e)
    {
        if (_areas.Drawing is null) StartArea();
        else _ = FinishAreaAsync();
    }

    private void StartArea()
    {
        if (!AppState.Connection.IsConnected || !AppState.Flights.Available)
        {
            AreaButton.IsChecked = false;
            MessageTray.Info("Areas need a connection to a NEBO 0.17 server");
            return;
        }
        _areas.Drawing = [];
        _areas.DrawColor = FlightDesk.ColorOf(AppState.Flights.Controller(AppState.Flights.MyId)) is { } c
            ? new SkiaSharp.SKColor(c.R, c.G, c.B) : new SkiaSharp.SKColor(0xFF, 0x7A, 0x1A);
        AreaButton.IsChecked = true;
        AreaButton.Content = "Done";
        MapArea.Cursor = Cursors.Cross;
        if (Window.GetWindow(this) is { } w) w.PreviewKeyDown += AreaKey;
        _areas.Invalidate();
    }

    private void StopArea()
    {
        if (_areas.Drawing is null) return;
        _areas.Drawing = null;
        _areas.Pointer = null;
        AreaButton.IsChecked = false;
        AreaButton.Content = "Area";
        MapArea.Cursor = null;
        if (Window.GetWindow(this) is { } w) w.PreviewKeyDown -= AreaKey;
        _areas.Invalidate();
    }

    private async Task FinishAreaAsync()
    {
        var points = _areas.Drawing?.ToList() ?? [];
        StopArea();
        if (points.Count < Core.Areas.MinPoints)
        {
            MessageTray.Info($"An area needs at least {Core.Areas.MinPoints} corners");
            return;
        }
        await FlightDesk.AddAreaAsync(points);
    }

    private void AreaKey(object sender, KeyEventArgs e)
    {
        if (_areas.Drawing is not { } drawing) return;
        switch (e.Key)
        {
            case Key.Escape: StopArea(); break;
            case Key.Enter: _ = FinishAreaAsync(); break;
            case Key.Back when drawing.Count > 0: drawing.RemoveAt(drawing.Count - 1); _areas.Invalidate(); break;
            default: return;
        }
        e.Handled = true;
    }

    // ---- Ruler (right-drag) and bullseye readout (hold the bullseye key) -----------------

    /// <summary>A ruler end at a screen point: on the track under it if there is one.</summary>
    private MeasureSk.End? EndAt(Point p)
    {
        if (!Nav.IsValid) return null;
        if (_symbols.HitTest(p.X, p.Y) is { } id && AppState.Store.Find(id)?.Unit is { Lat: { } tl, Lon: { } tn })
            return new(id, tl, tn);
        var (lat, lon) = Nav.View.ToLatLon(p.X, p.Y);
        return new(null, lat, lon);
    }

    private void SetRuler(MeasureSk.End? from, MeasureSk.End? to)
    {
        _measure.RulerFrom = from;
        _measure.RulerTo = to;
        _measure.Invalidate();
    }

    private void SetBullsCursor(Point? p)
    {
        _measure.BullsCursor = p is { } q ? (q.X, q.Y) : null;
        _measure.Invalidate();
    }

    private Point _rightDownAt;

    private MouseButton? _rulerButton;  // the button dragging the ruler now
    private bool _rightPressed;

    private void StartRuler(MouseButton button, Point at)
    {
        _rulerButton = button;
        var from = EndAt(at);
        SetRuler(from, from);
        MapArea.CaptureMouse();
    }

    /// <summary>A left click while right-dragging the ruler: the line becomes an engagement
    /// (AppState.Engagements, every map), following its tracks; the drag goes on.</summary>
    private void PinRuler()
    {
        if (_measure.RulerFrom is not { } from || _measure.RulerTo is not { } to) return;
        if (from.TrackId is null && to.TrackId is null && Math.Abs(from.Lat - to.Lat) + Math.Abs(from.Lon - to.Lon) < 1e-9) return;
        AppState.Engagements.Add(new LineEnd(from.TrackId, from.Lat, from.Lon), new LineEnd(to.TrackId, to.Lat, to.Lon));
    }

    // A data block being dragged: the block as it was placed when the press began, and where.
    private (SymbolsSk.PlacedBlock Block, Point At)? _blockDrag;

    private void EndRuler()
    {
        // Measuring only while dragging (user's choice): the line goes on release.
        _rulerButton = null;
        SetRuler(null, null);
        MapArea.ReleaseMouseCapture();
    }

    private void MapArea_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (MessageTrayView.Contains(e.OriginalSource)) return;  // the tray's cards take their own clicks
        if (_areas.Drawing is not null)
        {
            _swallowRightUp = true;  // drawing an area: the right button does nothing
            e.Handled = true;
            return;
        }
        if (RwrCanvas.IsVisible) return;  // the clock takes the clicks while open
        _rightDownAt = e.GetPosition(MapArea);
        e.Handled = true;  // the map itself does nothing with the right button here
        if (Input.Bindings.Get(Input.Bindings.PointInfo) is { Button: MouseButton.Right } point && point.Matches(e))
        {
            ShowPoint(_rightDownAt);
            return;
        }
        _rightPressed = true;
        if (Input.Bindings.Get(Input.Bindings.Ruler) is { Button: MouseButton.Right } ruler && ruler.Matches(e))
            StartRuler(MouseButton.Right, _rightDownAt);
    }

    private void MapArea_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (MessageTrayView.Contains(e.OriginalSource)) return;  // the tray's cards take their own clicks
        if (_swallowRightUp)
        {
            _swallowRightUp = false;
            e.Handled = true;
            return;
        }
        if (!_rightPressed) return;
        _rightPressed = false;
        if (_rulerButton == MouseButton.Right) EndRuler();
        e.Handled = true;
        // A right-click without dragging: the menu for what is under the pointer (DTC tab: a
        // waypoint's menu, or a new waypoint there, whatever else is on the map).
        var at = e.GetPosition(MapArea);
        if ((at - _rightDownAt).Length >= 4) return;
        if (InDtc) DtcRightClick(at);
        else ShowMapMenu(at);
    }

    /// <summary>Middle and side buttons: point info or the ruler when bound to them.</summary>
    private void MapArea_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (MessageTrayView.Contains(e.OriginalSource)) return;  // the tray's cards take their own clicks
        if (RwrCanvas.IsVisible || NctrCanvas.IsVisible || e.ChangedButton is MouseButton.Left or MouseButton.Right) return;
        var at = e.GetPosition(MapArea);
        if (Input.Bindings.Get(Input.Bindings.PointInfo).Matches(e))
        {
            ShowPoint(at);
            e.Handled = true;
        }
        else if (Input.Bindings.Get(Input.Bindings.Ruler).Matches(e))
        {
            StartRuler(e.ChangedButton, at);
            e.Handled = true;
        }
    }

    private void MapArea_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (MessageTrayView.Contains(e.OriginalSource)) return;  // the tray's cards take their own clicks
        if (_rulerButton is { } b && b == e.ChangedButton && b is not (MouseButton.Left or MouseButton.Right))
        {
            EndRuler();
            e.Handled = true;
        }
    }

    /// <summary>Point info bound to a key: the point under the pointer on the map it is over.</summary>
    private bool OnPointInfoKey()
    {
        if (!MapArea.IsMouseOver || !Nav.IsValid) return false;
        ShowPoint(Mouse.GetPosition(MapArea));
        return true;
    }

    /// <summary>The right-click menu for what is under the pointer: an own aircraft, a placed
    /// threat, an F10 mark, or the map itself (place a threat, at a fix when on one).</summary>
    private void ShowMapMenu(Point at)
    {
        if (!Nav.IsValid) return;
        var menu = new ContextMenu();
        var store = AppState.Store;
        if (_measure.HitPinned(at.X, at.Y) is { } pinned && AppState.Engagements.Find(pinned) is { } line)
        {
            var book = AppState.Engagements;
            menu.Items.Add(Item(line.ShowIntercept ? "Hide intercept point" : "Show intercept point", () => book.SetShowIntercept(line.Id, !line.ShowIntercept)));
            var markers = new MenuItem { Header = "Marker" };
            for (int k = 1; k <= EngagementBook.Markers; k++)
            {
                var n = k;
                markers.Items.Add(new MenuItem { Header = $"{n}", IsCheckable = true, IsChecked = line.Marker == n });
                ((MenuItem)markers.Items[^1]).Click += (_, _) => book.SetMarker(line.Id, line.Marker == n ? null : n);
            }
            menu.Items.Add(markers);
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Remove this engagement line", () => book.Remove(line.Id)));
            if (book.Lines.Count > 1)
                menu.Items.Add(Item($"Remove all {book.Lines.Count} engagement lines", book.Clear));
            menu.PlacementTarget = MapArea;
            menu.IsOpen = true;
            return;
        }
        if (_symbols.HitTest(at.X, at.Y) is { } id && store.Find(id)?.Unit is { } unit && PackageMenu.Packable(unit))
            AddTrackItems(menu, unit);
        else if (_symbols.HitTest(at.X, at.Y) is { } cid && store.Find(cid)?.Unit is { Detected: true } contact)
            AddContactItems(menu, contact);
        else if (_gci.HitDesignation(at.X, at.Y) is { } designation)
            AddDesignationItems(menu, designation);
        else if (_gci.HitThreat(at.X, at.Y) is { } threat)
            AddThreatItems(menu, threat);
        else if (_gci.HitMark(at.X, at.Y) is { } mark)
            AddMarkItems(menu, mark);
        else
            AddMapItems(menu, at);
        if (menu.Items.Count > 0 && menu.Items[^1] is Separator) menu.Items.RemoveAt(menu.Items.Count - 1);
        if (menu.Items.Count == 0) return;
        menu.PlacementTarget = MapArea;
        menu.IsOpen = true;
    }

    private static MenuItem Item(string header, Action click, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        item.Click += (_, _) => click();
        return item;
    }

    private void AddTrackItems(ContextMenu menu, Unit unit)
    {
        var board = AppState.Board;
        var key = AppState.Settings.RwrKey;
        menu.Items.Add(Item($"RWR… ({key})", () => OpenRwrScope(unit.Id)));
        if (board.Lines.Any(l => l.TrackId == unit.Id))
            menu.Items.Add(Item($"Clear bearing lines of {LineLabel(unit)}", () => board.ClearLines(unit.Id)));
        menu.Items.Add(new Separator());
        foreach (var item in PackageMenu.Items(unit)) menu.Items.Add(item);
        menu.Items.Add(new Separator());
        var flightItems = FlightDesk.MenuItems(unit.Id).ToList();
        if (flightItems.Count > 0)
        {
            foreach (var item in flightItems) menu.Items.Add(item);
            menu.Items.Add(new Separator());
        }
        if (LandingQueueItem(unit) is { } landing)
        {
            menu.Items.Add(landing);
            menu.Items.Add(new Separator());
        }
        // What can be sent to a player.
        if (unit.Player is not null && AirfieldFor(unit) is { } field)
        {
            var name = field.Name.Replace('_', ' ');
            void Add(string header, bool atis)
            {
                var item = new MenuItem { Header = header };
                item.Click += (_, _) => new Panels.SendMessageWindow(field, unit.Id, atis) { Owner = Window.GetWindow(this) }.Show();
                menu.Items.Add(item);
            }
            Add($"Send ATIS: {name}…", true);
            Add($"Send airfield info: {name}…", false);
        }
        // The player's SRS radios are shown in the SRS tab.
        if (PackageMenu.ListenItem(unit, this) is { } listen) menu.Items.Add(listen);
        if (PackageMenu.BestSpotItem(unit) is { } best) menu.Items.Add(best);
    }

    /// <summary>"Add to Kutaisi landing queue" for an own aircraft at any range (the queue panel
    /// suggests only those closing within 15 NM): the base the ATC tab shows, else the nearest
    /// airfield. Already an arrival there: its place, greyed out.</summary>
    private static MenuItem? LandingQueueItem(Unit unit)
    {
        if (unit.Detected || !Symbology.IsAir(Symbology.KindOf(unit))) return null;
        var bas = AppState.AtcQueueKey is { } key ? QueueBase.ForKey(key) : null;
        if (bas is null && NearestAirfield(unit) is { } near) bas = QueueBase.ForAirfield(near);
        if (bas is null) return null;
        var queue = AppState.Queues.For(bas.Key);
        if (queue.Place(unit.Id) is var place and > 0 && queue.Entries[place - 1].Kind == QueueKind.Arrival)
            return Item($"#{place} in {bas.Name} landing queue", () => { }, enabled: false);
        return Item($"Add to {bas.Name} landing queue", () => AppState.Queues.Add(bas.Key, unit.Id, QueueKind.Arrival));
    }

    /// <summary>A radar contact: identify it for the coalition — first the NCTR answers, then any
    /// label or a typed name — or clear its identity.</summary>
    private void AddContactItems(ContextMenu menu, Unit contact)
    {
        var store = AppState.Store;
        var connected = AppState.Connection.IsConnected;
        var what = contact.Type ?? (contact.Assigned is { } a ? $"identified as {a}" : "unidentified");
        menu.Items.Add(new MenuItem { Header = $"Contact: {what}", IsEnabled = false });
        var identify = new MenuItem { Header = "Identify as", IsEnabled = connected };
        // NCTR answers first (newest request).
        foreach (var n in store.NctrFor(contact.Id))
        {
            var label = NctrMethods.LabelOf(n);
            var header = $"{n.Result}   NCTR {NctrMethods.NameOf(n.Method)}" + (n.Platform is { } p ? $" by {p}" : "");
            identify.Items.Add(label is null
                ? new MenuItem { Header = header, IsEnabled = false }  // a size class or raid count is a hint, not an identity
                : Item(header, () => _ = IdentifyAsync(contact.Id, label)));
        }
        if (identify.Items.Count > 0) identify.Items.Add(new Separator());
        foreach (var (group, labels) in RwrLabels.Groups)
        {
            var sub = new MenuItem { Header = group };
            foreach (var l in labels)
            {
                var label = l;
                sub.Items.Add(Item(label, () => _ = IdentifyAsync(contact.Id, label)));
            }
            identify.Items.Add(sub);
        }
        identify.Items.Add(Item("Type a name…", () =>
        {
            if (PromptText("Identify contact", "Identity for your coalition (up to 24 letters, digits, '-', '/', spaces), e.g. \"Su-34\" or \"SA-11\":") is { } text)
                _ = IdentifyAsync(contact.Id, text);
        }));
        menu.Items.Add(identify);
        if (contact.Assigned is { } current)
            menu.Items.Add(Item($"Clear identity ({current})", () => _ = IdentifyAsync(contact.Id, ""), connected));
    }

    /// <summary>Sends an identity (empty: clears it); the ids channel brings it back to every
    /// controller of the coalition.</summary>
    private async Task IdentifyAsync(long unitId, string label)
    {
        label = label.Trim();
        if (label.Length > 24 || !System.Text.RegularExpressions.Regex.IsMatch(label, @"^[A-Za-z0-9 /\-]*$"))
        {
            MessageBox.Show(Window.GetWindow(this), "Up to 24 letters, digits, '-', '/' and spaces.", "Identify", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var result = await AppState.Connection.SendCommandAsync("identify", new Dictionary<string, object> { ["unit"] = unitId, ["label"] = label });
        if (!result.Ok)
            MessageBox.Show(Window.GetWindow(this), $"Not identified: {result.Error}", "Identify", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void AddMapItems(ContextMenu menu, Point at)
    {
        var board = AppState.Board;
        // On a fix the threat goes on the fix and that aircraft's lines have done their job.
        var fixHit = board.Fixes().FirstOrDefault(f => _gci.HitFix(at.X, at.Y) == f.Fix);
        var (lat, lon) = fixHit.Fix is { } fix ? (fix.Lat, fix.Lon) : Nav.View.ToLatLon(at.X, at.Y);
        if (SelectAirfieldItem(lat, lon) is { } select)
        {
            menu.Items.Add(select);
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(ThreatMenu(fixHit.Fix is null ? "Place threat here" : "Place threat at fix", lat, lon,
            fixHit.Fix is null ? null : () => board.ClearLines(fixHit.TrackId)));
        menu.Items.Add(Item("Add F10 mark here…", () => _ = AddMarkAsync(lat, lon), AppState.Connection.IsConnected));
        menu.Items.Add(Item("Coordinates…", () => ShowPointAt(lat, lon)));
        // Areas are drawn with the toolbar's Area button; here they are removed.
        if (!IsAirfieldView && AppState.Flights.Available && FlightDesk.MyAreas.Count > 0)
        {
            menu.Items.Add(new Separator());
            if (FlightDesk.MyAreaAt(lat, lon) is var index and >= 0)
            {
                var shown = FlightDesk.MyAreaOnF10(index);
                menu.Items.Add(Item(shown ? "Hide this area from pilots (F10)" : "Show this area to pilots (F10)",
                    () => _ = FlightDesk.ShowAreaToPilotsAsync(index, !shown)));
                menu.Items.Add(Item("Remove this area of operations", () => _ = FlightDesk.RemoveAreaAsync(index)));
            }
            menu.Items.Add(Item($"Remove all my areas ({FlightDesk.MyAreas.Count})", () => _ = FlightDesk.ClearAreasAsync()));
        }
        if (board.Lines.Count > 0)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item($"Clear all bearing lines ({board.Lines.Count})", () => board.ClearLines()));
        }
    }

    /// <summary>The threat types by group; a pick places that threat at the point (connected:
    /// shared with the coalition's controllers; offline: a note here), then runs <paramref name="placed"/>.</summary>
    private MenuItem ThreatMenu(string header, double lat, double lon, Action? placed)
    {
        var board = AppState.Board;
        var place = new MenuItem { Header = header };
        foreach (var group in ThreatType.All.GroupBy(t => t.Group))
        {
            if (place.Items.Count > 0) place.Items.Add(new Separator());
            foreach (var type in group)
            {
                var t = type;
                place.Items.Add(Item($"{t.Name}  ({t.RangeKm / 1.852:0} NM)", () =>
                {
                    if (AppState.Connection.IsConnected) _ = ShareThreatAsync(t, lat, lon);
                    else board.AddThreat(t, lat, lon, DateTime.UtcNow);
                    placed?.Invoke();
                }));
            }
        }
        return place;
    }

    /// <summary>A pilot's designation: what it is, a threat placed on it (the designation then
    /// goes), its coordinates, or hidden here until the pilot designates elsewhere.</summary>
    private void AddDesignationItems(ContextMenu menu, DesignationView d)
    {
        var detail = d.AltFt is { } ft ? $" · {ft:0} ft" : "";
        menu.Items.Add(new MenuItem { Header = $"{d.Label}{detail}", IsEnabled = false });
        menu.Items.Add(ThreatMenu("Place threat here", d.Lat, d.Lon, () => AppState.Store.DismissDesignation(d.Player, d.Emitter)));
        menu.Items.Add(Item("Coordinates…", () => ShowPointAt(d.Lat, d.Lon, $"{d.Name}'s designation")));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Clear (until the pilot designates elsewhere)", () => AppState.Store.DismissDesignation(d.Player, d.Emitter)));
    }

    private void AddThreatItems(ContextMenu menu, ThreatView threat)
    {
        var board = AppState.Board;
        var connected = AppState.Connection.IsConnected;
        var mark = board.Marks.FirstOrDefault(m => m.Id == threat.MarkId);
        var source = threat.SharedId is not null ? "controllers only"
            : threat.MarkId is null ? "not sent"
            : mark is null ? "F10 mark" : mark.Nebo ? "F10 mark by NEBO" : mark.ByMission ? "F10 mark by the mission" : $"F10 mark by {mark.Author}";
        menu.Items.Add(new MenuItem { Header = $"{threat.Type.Name} ({source})", IsEnabled = false });
        if (threat.SharedId is { } sharedId)
        {
            // To the pilots: the F10 mark takes over, so the shared one goes.
            menu.Items.Add(Item("Send to F10 map", async () =>
            {
                if (await SendMarkAsync(threat.Lat, threat.Lon, $"{threat.Type.Name} (GCI)", threat.Type.Name) is not null)
                    await RemoveSharedThreatAsync(sharedId);
            }, connected));
            menu.Items.Add(Item("Coordinates…", () => ShowPointAt(threat.Lat, threat.Lon)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Delete threat (for every controller)", () => _ = RemoveSharedThreatAsync(sharedId), connected));
            return;
        }
        if (threat.Local is { MarkId: null } local)
            menu.Items.Add(Item("Send to F10 map", () => _ = SendThreatAsync(local), connected));
        // The mark is the threat: removing it removes the threat for everyone.
        if (threat.MarkId is { } markId && threat.Removable)
            menu.Items.Add(Item("Remove from F10 map", () => _ = RemoveMarkAsync(markId, threat.Local), connected));
        menu.Items.Add(Item("Coordinates…", () => ShowPointAt(threat.Lat, threat.Lon, mark?.Text.Trim())));
        if (threat.Local is { MarkId: null } note)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Delete threat", () => board.RemoveThreat(note)));
        }
    }

    private void AddMarkItems(ContextMenu menu, F10Mark mark)
    {
        var by = mark.Nebo ? "NEBO" : mark.ByMission ? "mission" : mark.Author;
        menu.Items.Add(new MenuItem { Header = $"F10 mark by {by}: {GciSk.FirstLine(mark.Text)}", IsEnabled = false });
        menu.Items.Add(Item("Coordinates…", () => ShowPointAt(mark.Lat, mark.Lon, mark.Text.Trim())));
        if (mark.Nebo) menu.Items.Add(Item("Remove from F10 map", () => _ = RemoveMarkAsync(mark.Id, null), AppState.Connection.IsConnected));
    }

    private void ShowPointAt(double lat, double lon, string? title = null)
    {
        var (x, y) = Nav.View.ToScreenLonLat(lon, lat);
        ShowPoint(new Point(x, y), title);
    }

    // ---- F10 marks for the own coalition (its players can delete them in the game) ------------

    /// <summary>Sends a mark; returns its id, or null (the reason shown) when it was not made.</summary>
    private async Task<long?> SendMarkAsync(double lat, double lon, string text, string? threat)
    {
        if (AppState.Store.Theatre is not { } theatre) return null;
        var (x, z) = theatre.FromLatLon(lat, lon);
        var fields = new Dictionary<string, object> { ["x"] = (long)Math.Round(x), ["z"] = (long)Math.Round(z), ["text"] = text };
        if (threat is not null) fields["threat"] = threat;
        var result = await AppState.Connection.SendCommandAsync("mark_add", fields);
        if (result.Ok && long.TryParse(result.Value, out var id)) return id;
        MessageBox.Show(Window.GetWindow(this), $"Not sent: {result.Error ?? "no mark id"}", "F10 mark", MessageBoxButton.OK, MessageBoxImage.Warning);
        return null;
    }

    /// <summary>A threat for the coalition's controllers (not the pilots): the threats channel
    /// brings it to every one of them, this client included.</summary>
    private async Task ShareThreatAsync(ThreatType type, double lat, double lon)
    {
        if (AppState.Store.Theatre is not { } theatre) return;
        var (x, z) = theatre.FromLatLon(lat, lon);
        var result = await AppState.Connection.SendCommandAsync("threat_add", new Dictionary<string, object>
            { ["x"] = (long)Math.Round(x), ["z"] = (long)Math.Round(z), ["threat"] = type.Name });
        if (!result.Ok)
            MessageBox.Show(Window.GetWindow(this), $"Not shared: {result.Error}", "Threat", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async Task RemoveSharedThreatAsync(long id)
    {
        var result = await AppState.Connection.SendCommandAsync("threat_remove", new Dictionary<string, object> { ["threat_id"] = id });
        if (!result.Ok)
            MessageBox.Show(Window.GetWindow(this), $"Not removed: {result.Error}", "Threat", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async Task SendThreatAsync(ThreatMarker threat)
    {
        if (await SendMarkAsync(threat.Lat, threat.Lon, $"{threat.Type.Name} (GCI)", threat.Type.Name) is { } id)
            AppState.Board.SetMarkId(threat, id, DateTime.UtcNow);
    }

    /// <summary>A plain mark with the controller's text. Text that starts with a threat type
    /// ("SA-11 …") makes it a threat with its ring, like a player's mark would.</summary>
    private async Task AddMarkAsync(double lat, double lon)
    {
        var text = PromptText("F10 mark", "Text of the mark (your coalition sees it on the F10 map; " +
            "start with a type such as \"SA-11\" to mark a threat):");
        if (text is null) return;
        await SendMarkAsync(lat, lon, text, ThreatType.Parse(text)?.Name);
    }

    private async Task RemoveMarkAsync(long markId, ThreatMarker? local)
    {
        var result = await AppState.Connection.SendCommandAsync("mark_remove", new Dictionary<string, object> { ["mark"] = markId });
        if (result.Ok)
        {
            if (local is not null) AppState.Board.RemoveThreat(local);
        }
        else MessageBox.Show(Window.GetWindow(this), $"Not removed: {result.Error}", "F10 mark", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>A one-line text from the controller (≤ 200 bytes), or null when cancelled or empty.</summary>
    private string? PromptText(string title, string prompt)
    {
        // 95 characters stay within the server's 200 bytes even in a two-byte script (Cyrillic).
        var box = new TextBox { MinWidth = 360, Margin = new Thickness(0, 6, 0, 10), MaxLength = 95 };
        var ok = new Button { Content = "Send", IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var dialog = new Window
        {
            Title = title, Content = panel, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        };
        ok.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) => box.Focus();
        if (dialog.ShowDialog() != true) return null;
        var text = box.Text.Trim();
        return text.Length == 0 ? null : text;
    }

    // ---- RWR ring: bearing lines from a pilot's RWR call ---------------------------------------
    // A ring around the own unit under the pointer or the selected one, turned with its nose:
    // 12, 3, 6 and 9 o'clock are buttons (a click draws a bearing line at that clock, ±15°: half
    // an hour), the other hours ticks; a click anywhere else on the ring draws a line at that
    // azimuth. It follows the unit with every picture until Esc or a click outside it. The mouse
    // wheel over it sizes it (kept for the session).

    private const double ScopeMinRadius = 70;
    private const double ClockSigmaDeg = 15;
    private double _scopeRadius = 150;
    private double ScopeRadius => Math.Clamp(_scopeRadius, ScopeMinRadius, Math.Max(ScopeMinRadius, Math.Min(MapArea.ActualWidth, MapArea.ActualHeight) / 2 - 24));
    private long? _rwrTrack;
    private Point? _scopeMouse;

    private static Media.SolidColorBrush Brush(byte a, byte r, byte g, byte b)
    {
        var brush = new Media.SolidColorBrush(Media.Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static readonly Media.SolidColorBrush ScopeInk = Brush(190, 255, 179, 71);
    private static readonly Media.SolidColorBrush ScopeFill = Brush(150, 8, 12, 10);
    private static readonly Media.SolidColorBrush ClockFill = Brush(230, 20, 24, 20);

    private static string LineLabel(Unit u) => Symbology.NameOf(u) ?? u.Callsign ?? $"#{u.Id}";

    /// <summary>The RWR key: the ring for the own unit under the pointer, else the selected one.</summary>
    private bool OnRwrKey()
    {
        if (!MapArea.IsMouseOver || !Nav.IsValid) return false;
        var p = Mouse.GetPosition(MapArea);
        var store = AppState.Store;
        static bool Own(Unit? u) => u is { Detected: false, Lat: not null, Lon: not null };
        long? id = _symbols.HitTest(p.X, p.Y) is { } hit && Own(store.Find(hit)?.Unit) ? hit
            : store.SelectedId is { } sel && Own(store.Find(sel)?.Unit) ? sel : null;
        if (id is null) return false;
        OpenRwrScope(id.Value);
        return true;
    }

    private void OpenRwrScope(long trackId)
    {
        _rwrTrack = trackId;
        _scopeMouse = null;
        AppState.Store.Changed -= BuildRwrScope;
        AppState.Store.Changed += BuildRwrScope;
        BuildRwrScope();
        if (_rwrTrack is null) return;
        RwrCanvas.Focusable = true;
        RwrCanvas.Visibility = Visibility.Visible;
        RwrCanvas.Focus();
        RwrCanvas.KeyDown -= RwrCanvas_KeyDown;
        RwrCanvas.KeyDown += RwrCanvas_KeyDown;
        RwrCanvas.MouseMove -= RwrCanvas_MouseMove;
        RwrCanvas.MouseMove += RwrCanvas_MouseMove;
        RwrCanvas.PreviewMouseWheel -= RwrCanvas_MouseWheel;
        RwrCanvas.PreviewMouseWheel += RwrCanvas_MouseWheel;
    }

    /// <summary>The wheel sizes the ring (not the map's zoom while it is open).</summary>
    private void RwrCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _scopeRadius = Math.Clamp(ScopeRadius * Math.Pow(1.15, e.Delta / 120.0), ScopeMinRadius, 2000);
        BuildRwrScope();
        e.Handled = true;
    }

    /// <summary>Grid → true at a point (the picture's headings come from DCS grid north).</summary>
    private static double GridToTrue(double lat, double lon) => AppState.Store.Theatre?.GridNorthAt(lat, lon) ?? 0;

    private (double X, double Y, Unit Unit)? ScopeCentre() =>
        _rwrTrack is { } id && AppState.Store.Find(id)?.Unit is { Lat: { } lat, Lon: { } lon } u && Nav.IsValid
            ? (Nav.View.ToScreenLonLat(lon, lat).X, Nav.View.ToScreenLonLat(lon, lat).Y, u) : null;

    private void BuildRwrScope()
    {
        if (ScopeCentre() is not var (cx, cy, unit))
        {
            CloseRwrClock();
            return;
        }
        var R = ScopeRadius;
        var kids = RwrCanvas.Children;
        kids.Clear();
        void Place(UIElement e, double x, double y) { Canvas.SetLeft(e, x); Canvas.SetTop(e, y); kids.Add(e); }

        // The disc takes the azimuth clicks.
        var disc = new System.Windows.Shapes.Ellipse { Width = 2 * R, Height = 2 * R, Stroke = ScopeInk, StrokeThickness = 1.4, Fill = ScopeFill, Cursor = Cursors.Cross };
        disc.MouseLeftButtonDown += (_, e) =>
        {
            var at = e.GetPosition(RwrCanvas);
            AddRwrLine(Core.Nav.Normalize(Math.Atan2(at.X - cx, cy - at.Y) * 180 / Math.PI), Core.Triangulation.BearingSigmaDeg, null);
            e.Handled = true;
        };
        Place(disc, cx - R, cy - R);

        // Clock positions from the nose (true): ticks for every hour, buttons at 12, 3, 6 and 9.
        if (unit.Hdg is { } hdg)
        {
            var nose = hdg + GridToTrue(unit.Lat!.Value, unit.Lon!.Value);
            for (int hour = 0; hour < 12; hour++)
            {
                var brg = Core.Nav.Normalize(nose + hour * 30);
                var a = brg * Math.PI / 180;
                double sin = Math.Sin(a), cos = Math.Cos(a);
                var inner = hour % 3 == 0 ? R - 8 : R - 6;
                kids.Add(new System.Windows.Shapes.Line
                {
                    X1 = cx + sin * inner, Y1 = cy - cos * inner, X2 = cx + sin * R, Y2 = cy - cos * R,
                    Stroke = ScopeInk, StrokeThickness = hour % 3 == 0 ? 2.4 : 1.2, IsHitTestVisible = false,
                });
                if (hour % 3 != 0) continue;
                var clock = hour == 0 ? 12 : hour;
                var button = new Border
                {
                    Child = new TextBlock { Text = clock.ToString(), Foreground = ScopeInk, FontWeight = FontWeights.Bold, FontSize = 15, TextAlignment = TextAlignment.Center },
                    Width = 30, Height = 24, CornerRadius = new CornerRadius(5),
                    Background = ClockFill, BorderBrush = ScopeInk, BorderThickness = new Thickness(hour == 0 ? 2 : 1.2),
                    Cursor = Cursors.Hand,
                    ToolTip = $"{clock} o'clock: bearing line {brg:000}° ±{ClockSigmaDeg:0}° from {LineLabel(unit)}",
                };
                var bearing = brg;
                button.MouseLeftButtonDown += (_, e) =>
                {
                    AddRwrLine(bearing, ClockSigmaDeg, null);
                    e.Handled = true;
                };
                var r = R - 24;
                Place(button, cx + sin * r - 15, cy - cos * r - 12);
            }
        }

        // Below: the unit, and what a click does.
        var title = new TextBlock
        {
            Text = $"{LineLabel(unit)}\n" + (unit.Hdg is null ? "no heading: click an azimuth" : "click 12 · 3 · 6 · 9 or any azimuth") + " · wheel: size",
            Foreground = Media.Brushes.White, FontSize = 10.5, TextAlignment = TextAlignment.Center, IsHitTestVisible = false,
        };
        title.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Place(title, cx - title.DesiredSize.Width / 2, cy + R + 4);
        kids.Add(new System.Windows.Shapes.Ellipse { Width = 5, Height = 5, Fill = ScopeInk, IsHitTestVisible = false });
        Canvas.SetLeft(kids[^1], cx - 2.5);
        Canvas.SetTop(kids[^1], cy - 2.5);
        UpdateScopeCursor();
    }

    /// <summary>The azimuth under the pointer: a line from the centre and its bearing.</summary>
    private void RwrCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        _scopeMouse = e.GetPosition(RwrCanvas);
        UpdateScopeCursor();
    }

    private System.Windows.Shapes.Line? _scopeLine;
    private TextBlock? _scopeText;

    private void UpdateScopeCursor()
    {
        if (_scopeLine is not null) RwrCanvas.Children.Remove(_scopeLine);
        if (_scopeText is not null) RwrCanvas.Children.Remove(_scopeText);
        _scopeLine = null;
        _scopeText = null;
        if (_scopeMouse is not { } m || ScopeCentre() is not var (cx, cy, _)) return;
        double dx = m.X - cx, dy = m.Y - cy, d = Math.Sqrt(dx * dx + dy * dy);
        if (d > ScopeRadius || d < 8) return;
        var brg = Core.Nav.Normalize(Math.Atan2(dx, -dy) * 180 / Math.PI);
        _scopeLine = new System.Windows.Shapes.Line
        {
            X1 = cx, Y1 = cy, X2 = cx + dx / d * ScopeRadius, Y2 = cy + dy / d * ScopeRadius,
            Stroke = ScopeInk, StrokeThickness = 1, StrokeDashArray = [4, 3], IsHitTestVisible = false,
        };
        _scopeText = new TextBlock { Text = $"{brg:000}°", Foreground = ScopeInk, FontSize = 11, FontWeight = FontWeights.Bold, IsHitTestVisible = false };
        Canvas.SetLeft(_scopeText, cx + dx / d * (ScopeRadius + 6) - 12);
        Canvas.SetTop(_scopeText, cy + dy / d * (ScopeRadius + 6) - 8);
        // Under the emitter symbols, over the disc.
        RwrCanvas.Children.Insert(Math.Min(1, RwrCanvas.Children.Count), _scopeLine);
        RwrCanvas.Children.Add(_scopeText);
    }

    /// <summary>A bearing line from where the unit is now, true degrees; the scope stays open.</summary>
    private void AddRwrLine(double bearingTrue, double sigmaDeg, string? emitter)
    {
        if (_rwrTrack is { } id && AppState.Store.Find(id)?.Unit is { Lat: { } lat, Lon: { } lon } u)
            AppState.Board.AddLine(id, LineLabel(u), lat, lon, bearingTrue, DateTime.UtcNow, sigmaDeg, emitter);
    }

    private void CloseRwrClock()
    {
        _rwrTrack = null;
        _scopeMouse = null;
        _scopeLine = null;
        _scopeText = null;
        AppState.Store.Changed -= BuildRwrScope;
        RwrCanvas.Visibility = Visibility.Collapsed;
        RwrCanvas.Children.Clear();
    }

    private void RwrCanvas_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // A click outside the scope closes it (and does nothing else on the map).
        if (ReferenceEquals(e.OriginalSource, RwrCanvas))
        {
            CloseRwrClock();
            e.Handled = true;
        }
    }

    private void RwrCanvas_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CloseRwrClock();
            e.Handled = true;
        }
    }

    // ---- NCTR: the NCTR key, a box dragged on the map, the methods, Run ----------------------
    // The server's AI radars holding the contacts in the box try each method; results come back
    // on the nctr channel and show beside the contacts and first in their Identify menu.

    private Point? _nctrFrom;
    private System.Windows.Threading.DispatcherTimer? _nctrFlash;
    private System.Windows.Shapes.Rectangle? _nctrBox;
    private static readonly HashSet<string> NctrChosen = ["jem", "hrrp", "isar", "size", "rotor", "raid"];  // remembered for the session

    private bool OnNctrKey()
    {
        if (!MapArea.IsMouseOver || !Nav.IsValid || !AppState.Connection.IsConnected) return false;
        CloseRwrClock();
        CloseNctr();
        NctrCanvas.Visibility = Visibility.Visible;
        NctrCanvas.Focusable = true;
        NctrCanvas.Focus();
        NctrCanvas.MouseLeftButtonDown += NctrCanvas_Down;
        NctrCanvas.MouseMove += NctrCanvas_Move;
        NctrCanvas.MouseLeftButtonUp += NctrCanvas_Up;
        NctrCanvas.KeyDown += NctrCanvas_Key;
        var hint = new TextBlock { Text = "NCTR: drag a box around the contacts (Esc cancels)", Foreground = ScopeInk, Background = Brush(170, 0, 0, 0),
            Padding = new Thickness(6, 2, 6, 2), IsHitTestVisible = false };
        Canvas.SetLeft(hint, 10);
        Canvas.SetTop(hint, 36);
        NctrCanvas.Children.Add(hint);
        return true;
    }

    private void NctrCanvas_Down(object sender, MouseButtonEventArgs e)
    {
        if (_nctrBox is not null && !ReferenceEquals(e.OriginalSource, NctrCanvas)) return;  // the method picker
        foreach (var card in NctrCanvas.Children.OfType<Border>().ToList()) NctrCanvas.Children.Remove(card);  // a new box
        _nctrFrom = e.GetPosition(NctrCanvas);
        if (_nctrBox is null)
        {
            _nctrBox = new System.Windows.Shapes.Rectangle { Stroke = ScopeInk, StrokeThickness = 1.5, StrokeDashArray = [5, 3], Fill = Brush(30, 255, 179, 71), IsHitTestVisible = false };
            NctrCanvas.Children.Add(_nctrBox);
        }
        NctrCanvas.CaptureMouse();
        NctrCanvas_Move(sender, e);
        e.Handled = true;
    }

    private void NctrCanvas_Move(object sender, MouseEventArgs e)
    {
        if (_nctrFrom is not { } a || _nctrBox is null || !NctrCanvas.IsMouseCaptured) return;
        var b = e.GetPosition(NctrCanvas);
        Canvas.SetLeft(_nctrBox, Math.Min(a.X, b.X));
        Canvas.SetTop(_nctrBox, Math.Min(a.Y, b.Y));
        _nctrBox.Width = Math.Abs(b.X - a.X);
        _nctrBox.Height = Math.Abs(b.Y - a.Y);
    }

    private void NctrCanvas_Up(object sender, MouseButtonEventArgs e)
    {
        if (_nctrFrom is not { } a || !NctrCanvas.IsMouseCaptured) return;
        NctrCanvas.ReleaseMouseCapture();
        var b = e.GetPosition(NctrCanvas);
        e.Handled = true;
        if (Math.Abs(b.X - a.X) < 8 || Math.Abs(b.Y - a.Y) < 8)
        {
            CloseNctr();  // a click, not a box
            return;
        }
        ShowNctrMethods(a, b);
    }

    private void NctrCanvas_Key(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        CloseNctr();
        e.Handled = true;
    }

    /// <summary>The methods to try (each with its rule as a tooltip), then Run.</summary>
    private void ShowNctrMethods(Point a, Point b)
    {
        var panel = new StackPanel { Margin = new Thickness(8) };
        panel.Children.Add(new TextBlock { Text = "NCTR methods", FontWeight = FontWeights.Bold, Foreground = Media.Brushes.White, Margin = new Thickness(0, 0, 0, 4) });
        var boxes = new List<(string Id, CheckBox Box)>();
        foreach (var (id, name, hint) in NctrMethods.All)
        {
            var box = new CheckBox { Content = name, IsChecked = NctrChosen.Contains(id), ToolTip = hint, Foreground = Media.Brushes.White, Margin = new Thickness(0, 1, 0, 1) };
            boxes.Add((id, box));
            panel.Children.Add(box);
        }
        var run = new Button { Content = "Run", IsDefault = true, MinWidth = 64, Margin = new Thickness(0, 6, 6, 0) };
        var cancel = new Button { Content = "Cancel", MinWidth = 64, Margin = new Thickness(0, 6, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(run);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        var card = new Border { Child = panel, Background = Brush(235, 22, 26, 30), BorderBrush = ScopeInk, BorderThickness = new Thickness(1) };
        cancel.Click += (_, _) => CloseNctr();
        run.Click += async (_, _) =>
        {
            var methods = boxes.Where(x => x.Box.IsChecked == true).Select(x => x.Id).ToList();
            NctrChosen.Clear();
            foreach (var m in methods) NctrChosen.Add(m);
            CloseNctr();
            if (methods.Count > 0) await RunNctrAsync(a, b, methods);
        };
        Canvas.SetLeft(card, Math.Min(Math.Max(a.X, b.X) + 6, Math.Max(0, NctrCanvas.ActualWidth - 150)));
        Canvas.SetTop(card, Math.Min(Math.Min(a.Y, b.Y), Math.Max(0, NctrCanvas.ActualHeight - 200)));
        NctrCanvas.Children.Add(card);
    }

    private async Task RunNctrAsync(Point a, Point b, IReadOnlyList<string> methods)
    {
        if (AppState.Store.Theatre is not { } theatre) return;
        var (la, loa) = Nav.View.ToLatLon(a.X, a.Y);
        var (lb, lob) = Nav.View.ToLatLon(b.X, b.Y);
        var (x1, z1) = theatre.FromLatLon(la, loa);
        var (x2, z2) = theatre.FromLatLon(lb, lob);
        var result = await AppState.Connection.SendCommandAsync("nctr", new Dictionary<string, object>
        {
            ["x"] = (long)Math.Round(x1), ["z"] = (long)Math.Round(z1), ["x2"] = (long)Math.Round(x2), ["z2"] = (long)Math.Round(z2),
            ["methods"] = string.Join(",", methods),
        });
        if (!result.Ok)
            MessageBox.Show(Window.GetWindow(this), $"NCTR not started: {result.Error}", "NCTR", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void CloseNctr()
    {
        NctrCanvas.MouseLeftButtonDown -= NctrCanvas_Down;
        NctrCanvas.MouseMove -= NctrCanvas_Move;
        NctrCanvas.MouseLeftButtonUp -= NctrCanvas_Up;
        NctrCanvas.KeyDown -= NctrCanvas_Key;
        if (NctrCanvas.IsMouseCaptured) NctrCanvas.ReleaseMouseCapture();
        NctrCanvas.Children.Clear();
        NctrCanvas.Visibility = Visibility.Collapsed;
        _nctrFrom = null;
        _nctrBox = null;
    }

    /// <summary>Snapshot aid: the RWR ring on an own airborne aircraft with a heading, and the
    /// 3 o'clock bearing line drawn as a click on that button would.</summary>
    public async Task<string> DebugRwr()
    {
        var store = AppState.Store;
        Unit? u = null;
        for (int i = 0; i < 10 && u is null; i++)
        {
            u = store.Tracks.Select(t => t.Unit).FirstOrDefault(x => !x.Detected && x.Category == "airplane" && x.Hdg is not null
                && x.Lat is not null && x.Lon is not null && (x.SpdKt ?? 0) > 60);
            if (u is null) await Task.Delay(500);
        }
        if (u is null) return "rwr: no own airborne aircraft";
        double lat = u.Lat!.Value, lon = u.Lon!.Value;
        ShowAt(lat, lon, 80 * Core.Nav.MetersPerNm * Mercator.ScaleAt(lat) / Math.Max(300, Math.Min(Nav.Width, Nav.Height)));
        await Task.Delay(500);
        OpenRwrScope(u.Id);
        var nose = u.Hdg!.Value + GridToTrue(lat, lon);
        var brg = Core.Nav.Normalize(nose + 90);
        AddRwrLine(brg, ClockSigmaDeg, null);
        await Task.Delay(1500);
        return $"rwr: ring on {LineLabel(u)} ({u.Type}), nose {Core.Nav.Normalize(nose):000}° true, 3 o'clock line {brg:000}° ±{ClockSigmaDeg:0}°, "
            + (RwrCanvas.IsVisible ? "ring open" : "ring closed");
    }

    /// <summary>Snapshot aid: three bearing lines from an own aircraft's track towards a made-up
    /// emitter 35 NM off its right wing (2–4° of RWR error), the fix, an SA-6 placed at it (sent
    /// as an F10 mark when <paramref name="send"/>), and the RWR clock open on the aircraft.</summary>
    public async Task<string> DebugGci(long trackId, bool send)
    {
        if (AppState.Store.Find(trackId)?.Unit is not { Lat: { } lat, Lon: { } lon, Hdg: { } hdg } u) return "gci: no aircraft";
        var board = AppState.Board;
        var (eLat, eLon) = Core.Nav.Destination(lat, lon, hdg + 90, 35 * Core.Nav.MetersPerNm);
        var origins = new[] { (lat, lon), Core.Nav.Destination(lat, lon, hdg + 180, 18 * Core.Nav.MetersPerNm), Core.Nav.Destination(lat, lon, hdg + 130, 22 * Core.Nav.MetersPerNm) };
        var errors = new[] { 3.0, -2.0, 4.0 };
        var clocks = new[] { 3, 2, 4 };
        for (int i = 0; i < origins.Length; i++)
        {
            var brg = Core.Nav.BearingRange(origins[i].Item1, origins[i].Item2, eLat, eLon).TrueDeg + errors[i];
            board.AddLine(trackId, LineLabel(u), origins[i].Item1, origins[i].Item2, brg, DateTime.UtcNow.AddMinutes(-6 + 3 * i));
        }
        var report = $"gci: emitter {eLat:F4},{eLon:F4}";
        if (board.Fixes().FirstOrDefault(f => f.TrackId == trackId).Fix is { } fix)
        {
            report += $"; fix {fix.Lat:F4},{fix.Lon:F4} off by {Core.Nav.BearingRange(fix.Lat, fix.Lon, eLat, eLon).Meters / Core.Nav.MetersPerNm:0.0} NM, ±{fix.MajorM / Core.Nav.MetersPerNm:0.0}/{fix.MinorM / Core.Nav.MetersPerNm:0.0} NM";
            var threat = board.AddThreat(ThreatType.Find("SA-6")!, fix.Lat, fix.Lon, DateTime.UtcNow);
            if (send)
            {
                await SendThreatAsync(threat);
                report += $"; F10 mark {threat.MarkId?.ToString() ?? "not sent"}";
            }
        }
        else report += "; no fix";
        ShowAt((lat + eLat) / 2, (lon + eLon) / 2, 70 * Core.Nav.MetersPerNm * Mercator.ScaleAt(lat) / Math.Max(300, Math.Min(Nav.Width, Nav.Height)));
        await Task.Delay(500);
        OpenRwrScope(trackId);
        return report;
    }

    /// <summary>The ATC tab's airfield, or else the airdrome nearest to the unit.</summary>
    private static Airfield? AirfieldFor(Unit unit)
    {
        var store = AppState.Store;
        if (AppState.SelectedAirfieldId is { } id && store.Airfields.FirstOrDefault(a => a.Id == id) is { } picked) return picked;
        return NearestAirfield(unit);
    }

    private static Airfield? NearestAirfield(Unit unit) =>
        unit.Lat is { } lat && unit.Lon is { } lon ? NearestAirfield(lat, lon) : null;

    private static Airfield? NearestAirfield(double lat, double lon)
    {
        var store = AppState.Store;
        if (store.Theatre is not { } theatre) return null;
        var (x, z) = theatre.FromLatLon(lat, lon);
        return store.Airfields.Where(a => !a.IsHelipad).MinBy(a => (a.X - x) * (a.X - x) + (a.Z - z) * (a.Z - z));
    }

    /// <summary>"Select Kutaisi": the airfield nearest the click becomes the selected one (ATC
    /// tab, SRS list and every map follow; an SRS radio at the ATC airfield moves with it).</summary>
    private static MenuItem? SelectAirfieldItem(double lat, double lon)
    {
        if (NearestAirfield(lat, lon) is not { } field) return null;
        var name = field.Name.Replace('_', ' ');
        if (AppState.SelectedAirfieldId == field.Id) return Item($"{name} is selected", () => { }, enabled: false);
        return Item($"Select {name}", () => AppState.SelectAirfield(field.Id));
    }

    private void MapArea_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (MessageTrayView.Contains(e.OriginalSource)) return;  // the tray's cards take their own clicks
        var p = e.GetPosition(MapArea);
        if (_areas.Drawing is not null)
        {
            _areas.Pointer = Nav.View.ToLatLon(p.X, p.Y);
            _areas.Invalidate();
        }
        // Every contact near the pointer (Settings → General, 10 NM) shows its full label.
        var hover = _symbols.HoverTest(p.X, p.Y);
        if (!hover.SetEquals(_symbols.HoverIds))
        {
            _symbols.HoverIds = hover;
            _symbols.Invalidate();
        }
        if (_blockDrag is { } bd)
        {
            var d = p - bd.At;
            _symbols.DragBlock(bd.Block.Key, bd.Block.Box.X + d.X - bd.Block.AnchorX, bd.Block.Box.Y + d.Y - bd.Block.AnchorY);
            _symbols.Invalidate();
            return;
        }
        if (_dtcJdamDrag is { } jd && MapXz(p) is { } jTo)
        {
            var dtc = Dtc.DtcSession.Instance;
            switch (jd.Part)
            {
                case 0: dtc.MoveJdam(jd.Station, jd.Target, jTo.X, jTo.Z); break;
                case 1: dtc.Jdam.SetLaunchPoint(jd.Station, jd.Target, jTo.X, jTo.Z); break;
                default: dtc.TurnJdam(jd.Station, jd.Target, jTo.X, jTo.Z); break;
            }
            dtc.Notify();
            return;
        }
        if (_dtcSaDrag is { } saDrag && MapXz(p) is { } saTo)
        {
            foreach (var (r, (x, z)) in saDrag.From)
                Dtc.DtcSession.Instance.MoveSa(r, x + saTo.X - saDrag.Start.X, z + saTo.Z - saDrag.Start.Z);
            Dtc.DtcSession.Instance.Notify();
            return;
        }
        if (_dtcDrag is { } drag && MapXz(p) is { } to)
        {
            var (dx, dz) = (to.X - drag.Start.X, to.Z - drag.Start.Z);
            foreach (var (num, (x, z)) in drag.From)
            {
                if (drag.Gps) Dtc.DtcSession.Instance.MoveGps(num, x + dx, z + dz);
                else Dtc.DtcSession.Instance.MoveWaypoint(num, x + dx, z + dz);
            }
            Dtc.DtcSession.Instance.Notify();
            return;
        }
        if (_measure.RulerFrom is { } from && _rulerButton is not null) SetRuler(from, EndAt(p));
        if (AppState.BullsKeyHeld) SetBullsCursor(p);
    }

    private void MapArea_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_symbols.HoverIds.Count > 0)
        {
            _symbols.HoverIds = new HashSet<long>();
            _symbols.Invalidate();
        }
        if (_measure.BullsCursor is not null) SetBullsCursor(null);
    }

    /// <summary>Snapshot aid: a ruler between two screen points (snapping like a right-drag)
    /// and the bullseye readout at a third.</summary>
    public void DebugMeasure(Point from, Point to, Point? bullsCursor)
    {
        SetRuler(EndAt(from), EndAt(to));
        SetBullsCursor(bullsCursor);
    }

    public (double X, double Y) DebugScreen(double lat, double lon) => Nav.View.ToScreenLonLat(lon, lat);

    /// <summary>Snapshot aid: a pinned BRAA line between two screen points (snapping to tracks).</summary>
    public void DebugPin(Point from, Point to)
    {
        SetRuler(EndAt(from), EndAt(to));
        PinRuler();
        SetRuler(null, null);
    }

    private void OnBullsKeyChanged() =>
        SetBullsCursor(AppState.BullsKeyHeld && MapArea.IsMouseOver ? Mouse.GetPosition(MapArea) : null);

    // ---- Selection: a click (not a drag) on the map selects the nearest track -------------

    private void MapArea_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (MessageTrayView.Contains(e.OriginalSource)) return;  // the tray's cards take their own clicks
        if (RwrCanvas.IsVisible) return;  // the clock takes the clicks while open
        if (_areas.Drawing is { } corners)
        {
            e.Handled = true;  // a corner, not a pan or a selection
            var at = e.GetPosition(MapArea);
            // Back on the first corner, or a double-click: the area is done.
            var first = corners.Count >= Core.Areas.MinPoints ? SkDraw.ScreenLonLat(Nav.View, corners[0].Lon, corners[0].Lat) : default;
            if (corners.Count >= Core.Areas.MinPoints && Math.Abs(first.X - at.X) <= 10 && Math.Abs(first.Y - at.Y) <= 10)
            {
                _ = FinishAreaAsync();
                return;
            }
            if (e.ClickCount == 2)
            {
                _ = FinishAreaAsync();  // the first click of the double-click placed the last corner
                return;
            }
            if (corners.Count < Core.Areas.MaxPoints) corners.Add(Nav.View.ToLatLon(at.X, at.Y));
            _areas.Invalidate();
            return;
        }
        if (_rulerButton == MouseButton.Right)
        {
            PinRuler();
            e.Handled = true;
            return;
        }
        if (!IsAirfieldView) AppState.ActiveMap = this;  // the ATC view is driven by its airfield list
        if (Input.Bindings.Get(Input.Bindings.PointInfo) is { Button: MouseButton.Left } point && point.Matches(e))
        {
            ShowPoint(e.GetPosition(MapArea));
            if (point.Modifiers.HasFlag(ModifierKeys.Alt)) AppState.AltClicked = true;  // releasing Alt must not open the menu bar
            e.Handled = true;            // no pan, no selection
            return;
        }
        if (Input.Bindings.Get(Input.Bindings.Ruler) is { Button: MouseButton.Left } ruler && ruler.Matches(e))
        {
            StartRuler(MouseButton.Left, e.GetPosition(MapArea));
            e.Handled = true;
            return;
        }
        if (InDtc && DtcMouseDown(e.GetPosition(MapArea)))
        {
            e.Handled = true;  // no pan, no track selection
            return;
        }
        if (!InDtc && _symbols.HitBlock(e.GetPosition(MapArea).X, e.GetPosition(MapArea).Y) is { } block)
        {
            _blockDrag = (block, e.GetPosition(MapArea));
            MapArea.CaptureMouse();
            e.Handled = true;  // a block drags; it does not pan the map
            return;
        }
        _pressAt = e.GetPosition(MapArea);
    }

    // ---- Alt+click: the point's coordinates ----------------------------------------------

    /// <summary>Shows a point's position in every format a controller passes on: DDM (as DCS
    /// shows it), DMS, decimal, MGRS, bullseye call and ground elevation.</summary>
    public void ShowPoint(Point p, string? title = null)
    {
        if (!Nav.IsValid) return;
        var (lat, lon) = Nav.View.ToLatLon(p.X, p.Y);
        var store = AppState.Store;
        var lines = new List<string>();
        if (title is { Length: > 0 }) lines.Add(title);
        lines.AddRange(new[]
        {
            Coordinates.Ddm(lat, lon),
            Coordinates.Dms(lat, lon),
            $"{lat.ToString("F5", System.Globalization.CultureInfo.InvariantCulture)}, {lon.ToString("F5", System.Globalization.CultureInfo.InvariantCulture)}",
            "MGRS " + Coordinates.Mgrs(lat, lon),
        });
        if (store.Latest?.Bullseye is { } bulls)
        {
            var magvar = store.Theatre?.MagVar;
            lines.Add(Core.Nav.BullseyeCall(bulls, lat, lon, magvar) + (magvar is null ? " (true)" : ""));
        }
        if (store.Theatre is { } theatre && store.Terrain is { } grid)
        {
            var (x, z) = theatre.FromLatLon(lat, lon);
            if (LineOfSight.HeightAt(grid, x, z) is { } h)
                lines.Add($"Elevation {Math.Max(0, h):0} m / {Math.Max(0, h) / 0.3048:0} ft");
        }
        PointText.Text = string.Join(Environment.NewLine, lines);
        _measure.Pin = (lat, lon);
        _measure.Invalidate();
        PointPopup.HorizontalOffset = p.X + 14;
        PointPopup.VerticalOffset = p.Y + 10;
        PointPopup.IsOpen = false;  // re-place when already open
        PointPopup.IsOpen = true;
        Dispatcher.BeginInvoke(() => PointText.Focus(), System.Windows.Threading.DispatcherPriority.Input);  // for Esc
    }

    private void PointPopup_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        PointPopup.IsOpen = false;
        e.Handled = true;
    }

    private void PointCopy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(PointText.Text);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another program holds the clipboard; the text can still be selected by hand.
        }
    }

    private void PointClose_Click(object sender, RoutedEventArgs e) => PointPopup.IsOpen = false;

    private void PointPopup_Closed(object? sender, EventArgs e)
    {
        _measure.Pin = null;
        _measure.Invalidate();
    }

    private void MapArea_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (MessageTrayView.Contains(e.OriginalSource)) return;  // the tray's cards take their own clicks
        var at = e.GetPosition(MapArea);
        if (RwrCanvas.IsVisible) return;
        if (_blockDrag is { } bd)
        {
            _blockDrag = null;
            MapArea.ReleaseMouseCapture();
            if ((at - bd.At).Length < 4) AppState.Store.Select(bd.Block.Lead);  // a click on a block selects its lead
            e.Handled = true;
            return;
        }
        if (_dtcPress is { } press)
        {
            _dtcPress = null;
            if (_dtcDrag is not null || _dtcSaDrag is not null || _dtcJdamDrag is not null)
            {
                _dtcDrag = null;
                _dtcSaDrag = null;
                _dtcJdamDrag = null;
                MapArea.ReleaseMouseCapture();
            }
            // A click (no drag) on a point of a multi-selection selects that one alone.
            if ((at - press.At).Length < 4) press.SelectAlone();
            e.Handled = true;
            return;
        }
        if (_rulerButton == MouseButton.Left)
        {
            EndRuler();
            e.Handled = true;
            return;
        }
        if (_pressAt is { } p && (at - p).Length < 4)
        {
            // A track wins over the zone around it.
            var track = _symbols.HitTest(at.X, at.Y);
            AppState.Store.Select(track);
            if (track is null && _foothold.Visible) AppState.SelectZone(_foothold.HitTest(at.X, at.Y));
        }
        _pressAt = null;
    }

    // ---- DTC tab: place, select and drag the cartridge's waypoints --------------------------

    /// <summary>Delete mode shows on the DTC map's pointer (and in its banner, DtcSk).</summary>
    private void DtcCursor() => MapArea.Cursor = InDtc && Dtc.DtcSession.Instance.DeleteMode ? Cursors.Cross : null;

    /// <summary>A screen point (DIPs) as DCS map metres (x north, z east), or null without a theatre.</summary>
    private (double X, double Z)? MapXz(Point p)
    {
        if (!Nav.IsValid || AppState.Store.Theatre is not { } theatre) return null;
        var (lat, lon) = Nav.View.ToLatLon(p.X, p.Y);
        return theatre.FromLatLon(lat, lon);
    }

    /// <summary>A left press in the DTC tab on a waypoint, GPS waypoint or SA object: with Ctrl or
    /// Shift it joins or leaves its kind's selection; without, it is selected (alone, unless it is
    /// part of the selection) and the whole selection of its kind is dragged along. False
    /// elsewhere: the left button pans the map as on every map.</summary>
    private bool DtcMouseDown(Point p)
    {
        var session = Dtc.DtcSession.Instance;
        var many = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
        static (double X, double Z) Xz(System.Text.Json.Nodes.JsonObject o) => (Core.Dtc.HornetNav.Number(o["x"]) ?? 0, Core.Dtc.HornetNav.Number(o["y"]) ?? 0);

        if (_dtc.HitTestJdam(p.X, p.Y) is { } jdam)
        {
            // F-14: pick the target and drag what was pressed (target, launch point, heading handle).
            session.SelectJdam(jdam.Station, jdam.Target);
            _dtcPress = (p, () => { });
            _dtcJdamDrag = jdam;
            MapArea.CaptureMouse();
            return true;
        }
        if (_dtc.HitTest(p.X, p.Y) is { } hit)
        {
            if (many)
            {
                session.Toggle(hit);
                return true;
            }
            if (!session.SelectedSet.Contains(hit)) session.Select(hit);
            _dtcPress = (p, () => { if (session.SelectedSet.Count > 1) session.Select(hit); });
            if (MapXz(p) is not { } start) return true;
            // Where each selected waypoint started, to move them all by the pointer's offset.
            _dtcDrag = (false, start, session.SelectedSet.Select(n => (n, p: session.Waypoint(n))).Where(t => t.p is not null)
                .ToDictionary(t => t.n, t => Xz(t.p!)));
            MapArea.CaptureMouse();
            return true;
        }
        if (_dtc.HitTestGps(p.X, p.Y) is { } gps)
        {
            if (many)
            {
                session.ToggleGps(gps);
                return true;
            }
            if (!session.SelectedGpsSet.Contains(gps)) session.SelectGps(gps);
            _dtcPress = (p, () => { if (session.SelectedGpsSet.Count > 1) session.SelectGps(gps); });
            if (MapXz(p) is not { } start) return true;
            _dtcDrag = (true, start, session.SelectedGpsSet.Select(n => (n, p: session.GpsPoint(n))).Where(t => t.p is not null)
                .ToDictionary(t => t.n, t => Xz(t.p!)));
            MapArea.CaptureMouse();
            return true;
        }
        if (_dtc.HitTestSa(p.X, p.Y) is { } sa)
        {
            // A ring's centre, a CAP's anchor, a line's point.
            if (many)
            {
                session.ToggleSa(sa);
                return true;
            }
            if (!session.SaSelectedSet.Contains(sa)) session.SelectSa(sa);
            _dtcPress = (p, () => { if (session.SaSelectedSet.Count > 1) session.SelectSa(sa); });
            if (MapXz(p) is not { } start) return true;
            var from = new Dictionary<Dtc.SaRef, (double X, double Z)>();
            foreach (var r in session.SaSelectedSet)
                if (session.SaPosition(r) is { } at) from[r] = at;
            _dtcSaDrag = (start, from);
            MapArea.CaptureMouse();
            return true;
        }
        return false;
    }

    /// <summary>A right-click in the DTC tab. Delete mode: the waypoint, GPS waypoint or SA object
    /// under the pointer is deleted. Add mode: on one of them its menu; anywhere else a new point of
    /// the editor's open tab there, whatever other markers (tracks, zones, airfields) are under it.</summary>
    private void DtcRightClick(Point p)
    {
        var session = Dtc.DtcSession.Instance;
        if (_dtc.HitTestJdam(p.X, p.Y) is { } jdam)
        {
            if (session.DeleteMode)
            {
                session.DeleteJdam(jdam.Station, jdam.Target);
                return;
            }
            session.SelectJdam(jdam.Station, jdam.Target);
            var jdamMenu = new ContextMenu();
            jdamMenu.Items.Add(new MenuItem { Header = $"{Core.Dtc.TomcatJdam.StationNames[jdam.Station - 1]} target {jdam.Target}", IsEnabled = false, FontWeight = FontWeights.SemiBold });
            jdamMenu.Items.Add(new MenuItem { Header = "Drag the target, its launch point or its heading handle", IsEnabled = false });
            jdamMenu.Items.Add(Item("Clear target", () => session.DeleteJdam(jdam.Station, jdam.Target)));
            jdamMenu.PlacementTarget = MapArea;
            jdamMenu.IsOpen = true;
            return;
        }
        int? wp = _dtc.HitTest(p.X, p.Y), gpsHit = wp is null ? _dtc.HitTestGps(p.X, p.Y) : null;
        Dtc.SaRef? saHit = wp is null && gpsHit is null ? _dtc.HitTestSa(p.X, p.Y) : null;
        if (session.DeleteMode)
        {
            if (wp is { } w) session.DeleteWaypoint(w);
            else if (gpsHit is { } g) session.DeleteGps(g);
            else if (saHit is { } s) session.DeleteSaObject(s);
            return;
        }
        if (gpsHit is { } gps)
        {
            session.SelectGps(gps);
            var gpsMenu = new ContextMenu();
            gpsMenu.Items.Add(new MenuItem { Header = session.GpsName(gps), IsEnabled = false, FontWeight = FontWeights.SemiBold });
            gpsMenu.Items.Add(Item("Delete", () => session.DeleteGps(gps)));
            gpsMenu.PlacementTarget = MapArea;
            gpsMenu.IsOpen = true;
            return;
        }
        if (saHit is { } sa)
        {
            session.SelectSa(sa);
            var saMenu = new ContextMenu();
            var what = sa.Kind switch
            {
                Dtc.SaKind.Threat => $"Threat ring {sa.Index + 1}", Dtc.SaKind.Cap => $"CAP {sa.Index + 1}",
                Dtc.SaKind.Geo => $"{(session.IsTomcat ? "Line" : "Geographic line")} {sa.Index + 1}, point {sa.Point + 1}",
                _ when session.IsViper => $"Threat {sa.Index + 1}",
                _ => $"{sa.Line!.Prefix} {sa.Index + 1}, point {sa.Point + 1}",
            };
            saMenu.Items.Add(new MenuItem { Header = what, IsEnabled = false, FontWeight = FontWeights.SemiBold });
            if (sa.Line is { } line)
            {
                saMenu.Items.Add(new MenuItem { Header = "New points go after this one (right-click the map)", IsEnabled = false });
                saMenu.Items.Add(Item("Start a new line instead", () => session.SetSaLine(null)));
                saMenu.Items.Add(new Separator());
                saMenu.Items.Add(Item("Delete this point", () => session.DeleteSaObject(sa)));
                saMenu.Items.Add(Item($"Delete {line.Prefix} {sa.Index + 1}", () =>
                {
                    session.Sa.RemoveLine(line, sa.Index);
                    session.SelectSa(null);
                    session.SetSaLine(null);
                }));
            }
            else saMenu.Items.Add(Item("Delete", () => session.DeleteSaObject(sa)));
            saMenu.PlacementTarget = MapArea;
            saMenu.IsOpen = true;
            return;
        }
        if (wp is not { } num)
        {
            if (MapXz(p) is not { } at) return;
            if (session.AddHere(at.X, at.Z) is { } why)
                MessageBox.Show(Window.GetWindow(this), why, "DTC", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        session.Select(num);
        if (session.IsViper)
        {
            ViperPointMenu(session, num);
            return;
        }
        if (session.IsTomcat)
        {
            var tMenu = new ContextMenu();
            tMenu.Items.Add(new MenuItem { Header = $"Plan {session.TomcatPlan}, waypoint {num}", IsEnabled = false, FontWeight = FontWeights.SemiBold });
            tMenu.Items.Add(Item("Delete", () => session.DeleteWaypoint(num)));
            tMenu.PlacementTarget = MapArea;
            tMenu.IsOpen = true;
            return;
        }
        var nav = session.Nav;
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = $"Waypoint {num}", IsEnabled = false, FontWeight = FontWeights.SemiBold });
        for (var r = 1; r <= Core.Dtc.HornetNav.Routes; r++)
        {
            var route = r;
            var inRoute = nav.Route(route).Contains(num);
            menu.Items.Add(Item(inRoute ? $"Remove from route {route}" : $"Add to route {route}", () =>
            {
                if (inRoute) nav.RemoveFromRoute(route, num);
                else nav.AddToRoute(route, num);
                session.Notify();
            }, inRoute || nav.Route(route).Count < Core.Dtc.HornetNav.MaxRoutePoints));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Delete", () => session.DeleteWaypoint(num)));
        menu.PlacementTarget = MapArea;
        menu.IsOpen = true;
    }

    /// <summary>An F-16C steerpoint's menu: its routes (a route flies its points in steerpoint
    /// order), its offset aimpoints (placed by the next right-click), delete.</summary>
    private void ViperPointMenu(Dtc.DtcSession session, int num)
    {
        var stpt = session.Stpt;
        var p = stpt.Point(num)!;
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = $"Steerpoint {num} ({(string?)p["type"]})", IsEnabled = false, FontWeight = FontWeights.SemiBold });
        for (var r = 1; r <= Core.Dtc.ViperNav.Routes; r++)
        {
            var route = r;
            var inRoute = Core.Dtc.ViperNav.InRoute(p, route);
            menu.Items.Add(Item(inRoute ? $"Remove from route {route}" : $"Add to route {route}", () =>
            {
                stpt.SetInRoute(route, num, !inRoute);
                session.Notify();
            }));
        }
        menu.Items.Add(new Separator());
        for (var o = 1; o <= 2; o++)
        {
            var oap = o;
            menu.Items.Add(Item(Core.Dtc.ViperNav.HasOap(p, oap) ? $"Move OA{oap} (right-click the map)" : $"Place OA{oap} (right-click the map)",
                () => session.SetPlaceOffset($"OAP_{oap}")));
            if (Core.Dtc.ViperNav.HasOap(p, oap))
                menu.Items.Add(Item($"Clear OA{oap}", () =>
                {
                    stpt.ClearOap(num, oap);
                    session.Notify();
                }));
        }
        menu.Items.Add(new Separator());
        // VIP / VRP: this steerpoint, then its legs are placed from the VIP/VRP tab or right here.
        var vipvrp = session.VipVrp;
        foreach (var (vip, name, leg) in new[] { (true, "VIP", "VIPTOTGT"), (false, "VRP", "TGTTOVRP") })
        {
            if (vipvrp.SteerpointOf(leg) == num)
                menu.Items.Add(Item($"Place {Map.Gpu.DtcSk.LegName(leg)} (right-click the map)", () => session.SetPlaceOffset(leg)));
            else
                menu.Items.Add(Item($"Use as {name}", () =>
                {
                    vipvrp.SetSteerpoint(vip, num);
                    session.Notify();
                }));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Delete", () => session.DeleteWaypoint(num)));
        menu.PlacementTarget = MapArea;
        menu.IsOpen = true;
    }
}
