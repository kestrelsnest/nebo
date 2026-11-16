using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Nebo.Client.Core;
using Nebo.Client.Core.Dtc;
using Nebo.Client.Dtc;

namespace Nebo.Client.Panels;

/// <summary>The DTC tab's F-16C sections: steerpoints with their routes and offset aimpoints,
/// COMM1 / COMM2, CMDS, RWR and ROE. Shown by DtcPanel in place of the F/A-18C's tabs; edits go
/// straight into the cartridge (DtcSession), which redraws the map and calls <see cref="Refresh"/>.</summary>
public partial class ViperTabs : UserControl
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static DtcSession Session => DtcSession.Instance;
    private const double Ft = HornetNav.FeetPerMeter;
    private bool _loading;

    public ViperTabs()
    {
        InitializeComponent();
        // The built cells first: setting the selections below already fills them.
        BuildComm();
        BuildProgram();
        TypeBox.ItemsSource = ViperNav.Types;
        AltTypeBox.ItemsSource = new[] { "MSL", "AGL" };
        SpeedTypeBox.ItemsSource = new[] { "CAS", "IAS", "GS", "TAS" };
        CmdsProgram.ItemsSource = ViperEw.Programs;
        CmdsProgram.SelectedIndex = 0;
        CmdsDelay.ItemsSource = Enumerable.Range(0, 11).Select(s => s == 0 ? "No delay" : $"{s} s").ToList();
        RwrTable.ItemsSource = ViperEw.RwrTables;
        RwrTable.SelectedIndex = 0;
        var threatNames = ViperThreats.Types.Select(d => (string?)d["name"] ?? "").ToList();
        NewThreatType.ItemsSource = threatNames;
        ThreatType.ItemsSource = threatNames;
        NewThreatType.SelectedIndex = 0;
        GeoLineBox.ItemsSource = new[] { "None", "Line 1", "Line 2", "Line 3", "Line 4" };
        ColrPage.ItemsSource = ViperColr.Pages;
        ColrPage.SelectedIndex = 0;
        BuildLegs();
    }

    /// <summary>The steerpoint tab is open: right-clicks and typed coordinates place steerpoints.</summary>
    public bool PointsTabOpen => OpenTab is "STPT" or "DEST" or "THREAT" or "GEO";

    private string? OpenTab => (Tabs.SelectedItem as TabItem)?.Header as string;

    /// <summary>The open tab decides what a right-click places, through the session's own modes:
    /// steerpoints as waypoints, DEST as GPS points, threats and lines as SA objects.</summary>
    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || !ReferenceEquals(e.OriginalSource, Tabs)) return;   // lists and boxes inside bubble up here
        Session.SetPlace(OpenTab switch { "DEST" => DtcPlace.Gps, "THREAT" or "GEO" => DtcPlace.Sa, _ => DtcPlace.Waypoints });
        if (OpenTab == "THREAT") Session.SetSaPlace(SaKind.Threat);
        if (OpenTab == "GEO") Session.SetSaPlace(SaKind.Geo);
        Session.Notify();                                       // the point tools show for the point tabs only
    }

    public void Refresh(Theatre? theatre)
    {
        _loading = true;
        ShowSteerpoints(theatre);
        ShowDest(theatre);
        ShowThreats(theatre);
        ShowGeo(theatre);
        ShowVipVrp();
        ShowColr();
        ShowComm();
        ShowCmds();
        ShowTables();
        _loading = false;
    }

    // ---- Steerpoints ----------------------------------------------------------------------------

    private sealed record Row(int Num, string Text)
    {
        public override string ToString() => Text;
    }

    private static double N(JsonObject p, string key, double fallback = 0) => HornetNav.Number(p[key]) ?? fallback;

    private void ShowSteerpoints(Theatre? theatre)
    {
        var stpt = Session.Stpt;
        MirrorBox.IsChecked = stpt.Mirror;
        var rows = stpt.Points.Select((p, i) =>
        {
            var pos = theatre is null ? "" : Coordinates.Ddm(theatre.ToLatLon(N(p, "x"), N(p, "y")).Lat, theatre.ToLatLon(N(p, "x"), N(p, "y")).Lon);
            var routes = string.Concat(Enumerable.Range(1, ViperNav.Routes).Select(r => ViperNav.InRoute(p, r) ? $" R{r}" : "   "));
            var oap = (ViperNav.HasOap(p, 1) ? " OA1" : "") + (ViperNav.HasOap(p, 2) ? " OA2" : "");
            return new Row(i + 1, $"{i + 1,2} {(string?)p["type"],-4} {pos}  {N(p, "alt") * Ft,6:0} ft{routes}{oap}  {(string?)p["note"]}");
        }).ToList();
        StptList.ItemsSource = rows;
        StptList.SelectedItems.Clear();
        foreach (var r in rows.Where(r => Session.SelectedSet.Contains(r.Num))) StptList.SelectedItems.Add(r);
        ShowEditor();
    }

    private void ShowEditor()
    {
        var stpt = Session.Stpt;
        var p = Session.Selected is { } n ? stpt.Point(n) : null;
        var many = Session.SelectedSet.Count > 1;
        Editor.IsEnabled = p is not null;
        TypeBox.IsEnabled = NoteBox.IsEnabled = ElevBox.IsEnabled = TosBox.IsEnabled = FixBox.IsEnabled = !many;
        Oa1Place.IsEnabled = Oa2Place.IsEnabled = !many;
        if (p is null)
        {
            EditorTitle.Text = "Pick a steerpoint, or right-click the map to add one. Ctrl/Shift picks several.";
            NoteBox.Text = ElevBox.Text = AltBox.Text = SpeedBox.Text = TosBox.Text = Oa1Text.Text = Oa2Text.Text = "";
            R1Box.IsChecked = R2Box.IsChecked = R3Box.IsChecked = false;
            return;
        }
        EditorTitle.Text = many
            ? $"{Session.SelectedSet.Count} steerpoints ({string.Join(", ", Session.SelectedSet.Order())}): route altitude, speed and routes apply to all"
            : $"Steerpoint {Session.Selected}" + (Session.PlaceOffset is { } o && o.StartsWith("OAP_", StringComparison.Ordinal) ? $" · right-click the map to place OA{o[^1]}" : "");
        TypeBox.SelectedItem = (string?)p["type"] ?? "STPT";
        Set(NoteBox, (string?)p["note"] ?? "");
        Set(ElevBox, (N(p, "alt") * Ft).ToString("0", Inv));
        Set(AltBox, (N(p, "routeAltitude", ViperNav.DefaultRouteAltM) * Ft).ToString("0", Inv));
        AltTypeBox.SelectedIndex = N(p, "altitudeType", ViperNav.Msl) == ViperNav.Agl ? 1 : 0;
        Set(SpeedBox, (N(p, "speed", ViperNav.DefaultSpeedKmh) / 1.852).ToString("0", Inv));
        SpeedTypeBox.SelectedIndex = Math.Clamp((int)N(p, "velocityType", 3) - 1, 0, 3);
        var tos = p["isTOSEnabled"]?.GetValue<bool>() == true ? N(p, "TOS", -1) : -1;
        Set(TosBox, tos < 0 ? "" : TimeSpan.FromSeconds(tos % 86400).ToString(@"hh\:mm\:ss", Inv));
        FixBox.IsChecked = p["FIX_Time"]?.GetValue<bool>() == true;
        bool? InRoute(int r)
        {
            var count = Session.SelectedSet.Count(num => stpt.Point(num) is { } q && ViperNav.InRoute(q, r));
            return count == 0 ? false : count == Session.SelectedSet.Count ? true : null;
        }
        R1Box.IsChecked = InRoute(1);
        R2Box.IsChecked = InRoute(2);
        R3Box.IsChecked = InRoute(3);
        string Oap(int o) => ViperNav.HasOap(p, o)
            ? $"{N(p, $"OAP_{o}_Range") * Ft,6:0} ft  {N(p, $"OAP_{o}_Bearing") * 180 / Math.PI:000.0}°  elev {N(p, $"OAP_{o}_Alt") * Ft:0} ft"
            : "none";
        Oa1Text.Text = Oap(1);
        Oa2Text.Text = Oap(2);
    }

    private static void Set(TextBox box, string text)
    {
        if (!box.IsKeyboardFocused) box.Text = text;
    }

    private static double? Number(TextBox box) => double.TryParse(box.Text.Trim(), NumberStyles.Float, Inv, out var v) ? v : null;

    private void Field_LostFocus(object sender, RoutedEventArgs e) => Apply();
    private void Field_Click(object sender, RoutedEventArgs e) => Apply();

    private void Field_Changed(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;   // not the tab control's change
        Apply();
    }

    /// <summary>The editor's values into the selected steerpoints (several: route altitude, speed and their types).</summary>
    private void Apply()
    {
        if (_loading || Session.Selected is null) return;
        var stpt = Session.Stpt;
        var many = Session.SelectedSet.Count > 1;
        foreach (var n in Session.SelectedSet)
        {
            if (stpt.Point(n) is not { } p) continue;
            var elev = !many && Number(ElevBox) is { } ef ? ef / Ft : N(p, "alt");
            var alt = Number(AltBox) is { } af ? af / Ft : N(p, "routeAltitude", ViperNav.DefaultRouteAltM);
            var speed = Number(SpeedBox) is { } kt ? kt * 1.852 : N(p, "speed", ViperNav.DefaultSpeedKmh);
            var type = !many && TypeBox.SelectedItem is string t ? t : (string?)p["type"] ?? "STPT";
            var note = many ? (string?)p["note"] ?? "" : NoteBox.Text.Trim();
            stpt.Set(n, elev, alt, AltTypeBox.SelectedIndex == 1 ? ViperNav.Agl : ViperNav.Msl, speed, SpeedTypeBox.SelectedIndex + 1, type, note);
            if (!many)
            {
                var tos = TosBox.Text.Trim();
                double? seconds = tos.Length == 0 ? null
                    : TimeSpan.TryParseExact(tos, [@"h\:mm\:ss", @"hh\:mm\:ss", @"h\:mm", @"hh\:mm"], Inv, out var ts) ? ts.TotalSeconds : N(p, "TOS", -1) is var old && old >= 0 ? old : null;
                stpt.SetTos(n, seconds, FixBox.IsChecked == true);
            }
        }
        Session.Notify();
    }

    private void Route_Click(object sender, RoutedEventArgs e)
    {
        var box = (CheckBox)sender;
        var route = int.Parse((string)box.Tag, Inv);
        var on = box.IsChecked != false;   // a mixed box clicked: all in
        foreach (var n in Session.SelectedSet) Session.Stpt.SetInRoute(route, n, on);
        Session.Notify();
    }

    private void OapPlace_Click(object sender, RoutedEventArgs e) => Session.SetPlaceOffset($"OAP_{(string)((Button)sender).Tag}");

    private void OapClear_Click(object sender, RoutedEventArgs e)
    {
        if (Session.Selected is not { } n) return;
        Session.Stpt.ClearOap(n, int.Parse((string)((Button)sender).Tag, Inv));
        Session.Notify();
    }

    private void Mirror_Click(object sender, RoutedEventArgs e)
    {
        Session.Stpt.Mirror = MirrorBox.IsChecked == true;
        Session.Notify();
    }

    private void StptList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;
        if (_loading) return;
        // In the order picked: the last one is the editor's.
        var nums = StptList.SelectedItems.OfType<Row>().Select(r => r.Num).ToList();
        Session.SelectMany(nums);
    }

    private void StptList_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Delete) return;
        Session.DeleteSelected();
        e.Handled = true;
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => Session.DeleteSelected();

    private void Up_Click(object sender, RoutedEventArgs e) => Shift(-1);
    private void Down_Click(object sender, RoutedEventArgs e) => Shift(+1);

    /// <summary>The one selected steerpoint a place up or down: the two swap numbers.</summary>
    private static void Shift(int by)
    {
        if (Session.SelectedSet.Count != 1 || Session.Selected is not { } n) return;
        var count = Session.Stpt.Points.Count;
        var to = n + by;
        if (to < 1 || to > count) return;
        var order = Enumerable.Range(1, count).ToList();
        (order[n - 1], order[to - 1]) = (order[to - 1], order[n - 1]);
        Session.Stpt.Reorder(order);
        Session.Select(to);
    }

    private string Pos(JsonObject p, Theatre? theatre)
    {
        if (theatre is null) return "";
        var (lat, lon) = theatre.ToLatLon(N(p, "x"), N(p, "y"));
        return Coordinates.Ddm(lat, lon);
    }

    /// <summary>The point lists' mirror boxes (DEST, THREAT_PTS, GEO_LINES).</summary>
    private void ListMirror_Click(object sender, RoutedEventArgs e)
    {
        var box = (CheckBox)sender;
        ViperList list = (string)box.Tag switch { "DEST" => Session.Dest, "THREAT_PTS" => Session.Threats, _ => Session.Geo };
        list.Mirror = box.IsChecked == true;
        Session.Notify();
    }

    // ---- DEST (the session's GPS points) -----------------------------------------------------------

    private void ShowDest(Theatre? theatre)
    {
        var dest = Session.Dest;
        DestMirror.IsChecked = dest.Mirror;
        var rows = dest.Points.Select((p, i) => new Row(i + 1,
            $"{ViperDest.FirstNumber + i,2} {(string?)p["text"],-3} {Pos(p, theatre)}  {N(p, "alt") * Ft,6:0} ft  {(string?)p["note"]}")).ToList();
        DestList.ItemsSource = rows;
        DestList.SelectedItems.Clear();
        foreach (var r in rows.Where(r => Session.SelectedGpsSet.Contains(r.Num))) DestList.SelectedItems.Add(r);
        var p1 = Session.SelectedGpsSet.Count == 1 && Session.SelectedGps is { } n ? dest.Point(n) : null;
        DestEditor.IsEnabled = p1 is not null;
        DestTitle.Text = p1 is null ? "Pick one DEST point, or right-click the map to add one." : $"DEST {ViperDest.FirstNumber - 1 + Session.SelectedGps}";
        Set(DestText, p1 is null ? "" : (string?)p1["text"] ?? "");
        Set(DestNote, p1 is null ? "" : (string?)p1["note"] ?? "");
        Set(DestElev, p1 is null ? "" : (N(p1, "alt") * Ft).ToString("0", Inv));
    }

    private void Dest_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading || Session.SelectedGpsSet.Count != 1 || Session.SelectedGps is not { } n || Session.Dest.Point(n) is not { } p) return;
        Session.Dest.Set(n, DestText.Text, DestNote.Text.Trim(), Number(DestElev) is { } ft ? ft / Ft : N(p, "alt"));
        Session.Notify();
    }

    private void DestList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;
        if (!_loading) Session.SelectGpsMany(DestList.SelectedItems.OfType<Row>().Select(r => r.Num).ToList());
    }

    // ---- Preplanned threats (the session's SA threat rings) -------------------------------------------

    private void ShowThreats(Theatre? theatre)
    {
        var threats = Session.Threats;
        ThreatMirror.IsChecked = threats.Mirror;
        NewThreatType.SelectedIndex = Session.ViperThreatType - 1;
        var rows = threats.Points.Select((p, i) => new Row(i,
            $"{55 + i + 1,2} {(string?)p["text"],-3} {(string?)p["threatName"],-26} {N(p, "radius") / 1852,5:0.0} NM  {Pos(p, theatre)}")).ToList();
        ThreatList.ItemsSource = rows;
        ThreatList.SelectedItems.Clear();
        foreach (var r in rows.Where(r => Session.SaSelectedSet.Contains(new SaRef(SaKind.Threat, r.Num)))) ThreatList.SelectedItems.Add(r);
        var one = Session.SaSelectedSet.Count == 1 && Session.SaSelected is { Kind: SaKind.Threat } s ? threats.Point(s.Index + 1) : null;
        ThreatEditor.IsEnabled = one is not null;
        ThreatTitle.Text = one is null ? "Pick one threat, or right-click the map to add one." : $"Threat {55 + Session.SaSelected!.Value.Index + 1}";
        if (one is null)
        {
            ThreatText.Text = ThreatRadius.Text = ThreatAlt.Text = "";
            return;
        }
        ThreatType.SelectedIndex = (int)N(one, "def_num", 1) - 1;
        Set(ThreatText, (string?)one["text"] ?? "");
        ThreatRing.IsChecked = one["ring"]?.GetValue<bool>() != false;
        Set(ThreatRadius, (N(one, "radius") / 1852).ToString("0.0", Inv));
        Set(ThreatAlt, (N(one, "alt") * Ft).ToString("0", Inv));
        ThreatRadius.IsEnabled = ThreatAlt.IsEnabled = ViperThreats.IsCustom(one);
    }

    private int? SelectedThreat => Session.SaSelectedSet.Count == 1 && Session.SaSelected is { Kind: SaKind.Threat } s ? s.Index + 1 : null;

    private void ApplyThreat()
    {
        if (_loading || SelectedThreat is not { } n || Session.Threats.Point(n) is not { } p) return;
        Session.Threats.Set(n, ThreatText.Text, ThreatRing.IsChecked == true,
            Number(ThreatRadius) is { } nm ? nm * 1852 : N(p, "radius"), Number(ThreatAlt) is { } ft ? ft / Ft : N(p, "alt"));
        Session.Notify();
    }

    private void Threat_LostFocus(object sender, RoutedEventArgs e) => ApplyThreat();
    private void Threat_Click(object sender, RoutedEventArgs e) => ApplyThreat();

    private void ThreatType_Changed(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;
        if (_loading || SelectedThreat is not { } n || ThreatType.SelectedIndex < 0) return;
        Session.Threats.SetType(n, ThreatType.SelectedIndex + 1);
        Session.Notify();
    }

    private void NewThreatType_Changed(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;
        if (!_loading && NewThreatType.SelectedIndex >= 0) Session.ViperThreatType = NewThreatType.SelectedIndex + 1;
    }

    private void ThreatList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;
        if (!_loading) Session.SelectSaMany(ThreatList.SelectedItems.OfType<Row>().Select(r => new SaRef(SaKind.Threat, r.Num)).ToList());
    }

    // ---- Geographic lines (the session's SA lines) ---------------------------------------------------

    /// <summary>A list point as the session's SA object: line (0–3, −1 none) and its place on it.</summary>
    private static SaRef GeoRef(int num)
    {
        var line = ViperGeo.LineOf(Session.Geo.Point(num)!);
        return new SaRef(SaKind.Geo, line - 1, Session.Geo.Line(line).ToList().IndexOf(num));
    }

    private int? GeoNum(SaRef r) => r.Kind == SaKind.Geo && Session.Geo.Line(r.Index + 1).ElementAtOrDefault(r.Point) is var n and > 0 ? n : null;

    private void ShowGeo(Theatre? theatre)
    {
        var geo = Session.Geo;
        GeoMirror.IsChecked = geo.Mirror;
        var current = Session.SaPlace == SaKind.Geo ? Session.SaLine ?? 0 : 0;
        Geo1.IsChecked = current == 0;
        Geo2.IsChecked = current == 1;
        Geo3.IsChecked = current == 2;
        Geo4.IsChecked = current == 3;
        var rows = geo.Points.Select((p, i) => new Row(i + 1,
            $"{30 + i + 1,2} {(ViperGeo.LineOf(p) is var l and > 0 ? $"L{l}" : "--")} {Pos(p, theatre)}  {N(p, "alt") * Ft,6:0} ft  {(string?)p["note"]}")).ToList();
        GeoList.ItemsSource = rows;
        GeoList.SelectedItems.Clear();
        var picked = Session.SaSelectedSet.Select(GeoNum).OfType<int>().ToHashSet();
        foreach (var r in rows.Where(r => picked.Contains(r.Num))) GeoList.SelectedItems.Add(r);
        var one = Session.SaSelectedSet.Count == 1 && Session.SaSelected is { } s && GeoNum(s) is { } n ? n : (int?)null;
        GeoEditor.IsEnabled = one is not null;
        GeoTitle.Text = one is null ? "Pick one point, or right-click the map to add one to the current line." : $"Point {30 + one}";
        if (one is not { } num) return;
        var p1 = geo.Point(num)!;
        GeoLineBox.SelectedIndex = ViperGeo.LineOf(p1);
        Set(GeoNote, (string?)p1["note"] ?? "");
        Set(GeoElev, (N(p1, "alt") * Ft).ToString("0", Inv));
    }

    private int? SelectedGeo => Session.SaSelectedSet.Count == 1 && Session.SaSelected is { } s ? GeoNum(s) : null;

    private void Geo_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading || SelectedGeo is not { } n || Session.Geo.Point(n) is not { } p) return;
        Session.Geo.Set(n, GeoNote.Text.Trim(), Number(GeoElev) is { } ft ? ft / Ft : N(p, "alt"));
        Session.Notify();
    }

    private void GeoLineBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;
        if (_loading || SelectedGeo is not { } n || GeoLineBox.SelectedIndex < 0) return;
        Session.Geo.SetLine(n, GeoLineBox.SelectedIndex);
        Session.SelectSa(GeoRef(n));   // where it is now
    }

    private void GeoLine_Click(object sender, RoutedEventArgs e) => Session.SetSaLine(int.Parse((string)((RadioButton)sender).Tag, Inv));

    private void GeoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;
        if (!_loading) Session.SelectSaMany(GeoList.SelectedItems.OfType<Row>().Select(r => GeoRef(r.Num)).ToList());
    }

    // ---- VIP / VRP ------------------------------------------------------------------------------------

    private readonly Dictionary<string, (CheckBox On, TextBlock Text)> _legs = [];

    private void BuildLegs()
    {
        foreach (var leg in ViperVipVrp.Legs)
        {
            var row = new DockPanel { Margin = new Thickness(12, 0, 0, 3) };
            row.Children.Add(new TextBlock { Text = Map.Gpu.DtcSk.LegName(leg), Width = 70, VerticalAlignment = VerticalAlignment.Center });
            var place = new Button { Content = "Place", Padding = new Thickness(6, 0, 6, 0), ToolTip = "The next right-click on the map places this point" };
            place.Click += (_, _) => Session.SetPlaceOffset(leg);
            var clear = new Button { Content = "Clear", Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(4, 0, 0, 0) };
            clear.Click += (_, _) =>
            {
                var from = Session.Stpt.Point(Session.VipVrp.SteerpointOf(leg));
                Session.VipVrp.Clear(leg, from is null ? 0 : N(from, "x"), from is null ? 0 : N(from, "y"));
                Session.Notify();
            };
            var on = new CheckBox { Content = "On", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            on.Click += (_, _) =>
            {
                Session.VipVrp.SetEnabled(leg, on.IsChecked == true);
                Session.Notify();
            };
            var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            text.SetResourceReference(TextBlock.ForegroundProperty, "Nebo.TextDim");
            row.Children.Add(place);
            row.Children.Add(clear);
            row.Children.Add(on);
            row.Children.Add(text);
            (leg.StartsWith("VIP", StringComparison.Ordinal) ? VipLegs : VrpLegs).Children.Add(row);
            _legs[leg] = (on, text);
        }
    }

    private void ShowVipVrp()
    {
        var v = Session.VipVrp;
        var nums = Enumerable.Range(1, Session.Stpt.Points.Count).ToList();
        VipNumber.ItemsSource = nums;
        VrpNumber.ItemsSource = nums;
        VipNumber.SelectedItem = v.SteerpointOf("VIPTOTGT");
        VrpNumber.SelectedItem = v.SteerpointOf("TGTTOVRP");
        foreach (var (leg, (on, text)) in _legs)
        {
            on.IsChecked = v.Enabled(leg);
            text.Text = v.IsSet(leg)
                ? $"{N(v.Data, $"{leg}_Range") / 1852:0.0} NM  {N(v.Data, $"{leg}_Bearing") * 180 / Math.PI:000.0}°  elev {N(v.Data, $"{leg}_Elevation") * Ft:0} ft"
                : "not placed";
        }
    }

    private void VipNumber_Changed(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;
        var box = (ComboBox)sender;
        if (_loading || box.SelectedItem is not int n) return;
        Session.VipVrp.SetSteerpoint((string)box.Tag == "VIP", n);
        Session.Notify();
    }

    // ---- COLR -----------------------------------------------------------------------------------------

    /// <summary>A symbol's colour; the name without the page and with spaces between its words.</summary>
    public sealed class ColrRow(ViperColr colr, string key, int color)
    {
        private int _color = color;
        public string Name { get; } = System.Text.RegularExpressions.Regex.Replace(key[(key.IndexOf('_') + 1)..], "(?<=[a-z0-9])(?=[A-Z])", " ");
        public IReadOnlyList<string> Choices => ViperColr.Colors;
        public string Color
        {
            get => ViperColr.Colors[Math.Clamp(_color - 1, 0, ViperColr.Colors.Count - 1)];
            set
            {
                _color = ViperColr.Colors.ToList().IndexOf(value) + 1;
                colr.Set(key, _color);
            }
        }
    }

    private Cartridge? _colrFor;

    private void ShowColr(bool force = false)
    {
        if (!force && ReferenceEquals(_colrFor, Session.Cartridge)) return;
        _colrFor = Session.Cartridge;
        if (ColrPage.SelectedItem is not string page) return;
        ColrGrid.ItemsSource = Session.Colr.Page(page).Select(p => new ColrRow(Session.Colr, p.Key, p.Color)).ToList();
    }

    private void ColrPage_Changed(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;
        if (IsLoaded) ShowColr(force: true);
    }

    // ---- COMM -----------------------------------------------------------------------------------

    private readonly TextBox[,] _comm = new TextBox[3, ViperComm.Channels + 1];
    private readonly TextBlock[] _comm2Mod = new TextBlock[ViperComm.Channels + 1];

    private void BuildComm()
    {
        CommGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        void Cell(UIElement e, int row, int col)
        {
            Grid.SetRow(e, row);
            Grid.SetColumn(e, col);
            CommGrid.Children.Add(e);
        }
        Cell(new TextBlock { Text = "Ch", FontWeight = FontWeights.SemiBold }, 0, 0);
        Cell(new TextBlock { Text = "COMM1 MHz", FontWeight = FontWeights.SemiBold }, 0, 1);
        Cell(new TextBlock { Text = "COMM2 MHz", FontWeight = FontWeights.SemiBold }, 0, 2);
        for (var ch = 1; ch <= ViperComm.Channels; ch++)
        {
            CommGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Cell(new TextBlock { Text = ch.ToString(Inv), VerticalAlignment = VerticalAlignment.Center }, ch, 0);
            for (var radio = 1; radio <= 2; radio++)
            {
                var box = new TextBox { Margin = new Thickness(0, 1, 8, 1), FontFamily = new System.Windows.Media.FontFamily("Consolas"), Tag = (radio, ch) };
                box.LostFocus += Comm_LostFocus;
                _comm[radio, ch] = box;
                Cell(box, ch, radio);
            }
            _comm2Mod[ch] = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            _comm2Mod[ch].SetResourceReference(TextBlock.ForegroundProperty, "Nebo.TextDim");
            Cell(_comm2Mod[ch], ch, 3);
        }
    }

    private void ShowComm()
    {
        var comm = Session.VComm;
        Comm1Mirror.IsChecked = comm.Mirror(1);
        Comm2Mirror.IsChecked = comm.Mirror(2);
        for (var ch = 1; ch <= ViperComm.Channels; ch++)
        {
            for (var radio = 1; radio <= 2; radio++)
                Set(_comm[radio, ch], (HornetNav.Number(comm.Channel(radio, ch)?["freq"]) ?? 0).ToString("0.000", Inv));
            _comm2Mod[ch].Text = HornetNav.Number(comm.Channel(2, ch)?["modulation"]) == ViperComm.Fm ? "FM" : "AM";
        }
    }

    private void Comm_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var box = (TextBox)sender;
        var (radio, ch) = ((int, int))box.Tag;
        var old = HornetNav.Number(Session.VComm.Channel(radio, ch)?["freq"]);
        if (Number(box) is not { } mhz || mhz == old) return;
        Session.VComm.Set(radio, ch, mhz);
        Session.Notify();
    }

    private void CommMirror_Click(object sender, RoutedEventArgs e)
    {
        var box = (CheckBox)sender;
        Session.VComm.SetMirror(int.Parse((string)box.Tag, Inv), box.IsChecked == true);
        Session.Notify();
    }

    // ---- CMDS -----------------------------------------------------------------------------------

    private static readonly string[] ProgramFields = ["BurstQuantity", "BurstInterval", "SalvoQuantity", "SalvoInterval"];
    private readonly TextBox[,] _program = new TextBox[4, 4];

    private void BuildProgram()
    {
        string[] heads = ["", "Burst qty", "Burst int (s)", "Salvo qty", "Salvo int (s)"];
        for (var r = 0; r <= ViperEw.Expendables.Length; r++) ProgramGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var c = 0; c < heads.Length; c++)
        {
            var h = new TextBlock { Text = heads[c], FontWeight = FontWeights.SemiBold };
            Grid.SetColumn(h, c);
            ProgramGrid.Children.Add(h);
        }
        for (var e = 0; e < ViperEw.Expendables.Length; e++)
        {
            var label = new TextBlock { Text = ViperEw.Expendables[e], VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(label, e + 1);
            ProgramGrid.Children.Add(label);
            for (var f = 0; f < ProgramFields.Length; f++)
            {
                var box = new TextBox { Margin = new Thickness(0, 1, 8, 1), ToolTip = f % 2 == 0 ? "0–99" : "0–10 s" };
                Grid.SetRow(box, e + 1);
                Grid.SetColumn(box, f + 1);
                ProgramGrid.Children.Add(box);
                _program[e, f] = box;
            }
        }
    }

    private void ShowCmds()
    {
        var ew = Session.VEw;
        CmdsDelay.SelectedIndex = ew.Delay;
        ShowProgram();
        var b = ew.Bingo;
        Set(BingoChaff, ((int)(HornetNav.Number(b["ChaffNum"]) ?? 0)).ToString(Inv));
        Set(BingoFlares, ((int)(HornetNav.Number(b["FlaresNum"]) ?? 0)).ToString(Inv));
        Set(BingoOther1, ((int)(HornetNav.Number(b["Other1Num"]) ?? 0)).ToString(Inv));
        Set(BingoOther2, ((int)(HornetNav.Number(b["Other2Num"]) ?? 0)).ToString(Inv));
        BingoFdbk.IsChecked = b["FDBK"]?.GetValue<bool>() == true;
        BingoReqctr.IsChecked = b["REQCTR"]?.GetValue<bool>() == true;
        BingoBingo.IsChecked = b["BINGO"]?.GetValue<bool>() == true;
    }

    private void ShowProgram()
    {
        if (CmdsProgram.SelectedItem is not string name) return;
        var p = Session.VEw.Program(name);
        for (var e = 0; e < ViperEw.Expendables.Length; e++)
            for (var f = 0; f < ProgramFields.Length; f++)
            {
                var v = HornetNav.Number(p[ViperEw.Expendables[e]]?[ProgramFields[f]]) ?? 0;
                Set(_program[e, f], f % 2 == 0 ? v.ToString("0", Inv) : v.ToString(f == 1 ? "0.000" : "0.00", Inv));
            }
    }

    private void CmdsProgram_Changed(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;
        ShowProgram();
    }

    private void CmdsApply_Click(object sender, RoutedEventArgs e)
    {
        if (CmdsProgram.SelectedItem is not string name) return;
        var p = Session.VEw.Program(name);
        for (var x = 0; x < ViperEw.Expendables.Length; x++)
        {
            double V(int f) => Number(_program[x, f]) ?? HornetNav.Number(p[ViperEw.Expendables[x]]?[ProgramFields[f]]) ?? 0;
            Session.VEw.SetProgram(name, ViperEw.Expendables[x], (int)V(0), V(1), (int)V(2), V(3));
        }
        Session.Notify();
    }

    private void BingoApply_Click(object sender, RoutedEventArgs e)
    {
        static int I(TextBox b) => int.TryParse(b.Text.Trim(), out var v) ? v : 0;
        Session.VEw.SetBingo(I(BingoChaff), I(BingoFlares), I(BingoOther1), I(BingoOther2),
            BingoFdbk.IsChecked == true, BingoReqctr.IsChecked == true, BingoBingo.IsChecked == true);
        Session.Notify();
    }

    private void CmdsDelay_Changed(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;
        if (!_loading && CmdsDelay.SelectedIndex >= 0) Session.VEw.Delay = CmdsDelay.SelectedIndex;
    }

    // ---- Tables: CMDS threats, RWR, ROE ------------------------------------------------------------

    private (Cartridge?, int) _tablesFor;   // the cartridge and EW revision the grids show

    /// <summary>A CMDS threat row: its AUTO program (1 none, 2–4 AUTO 1–3) and threshold.</summary>
    public sealed class CmdsRow
    {
        private static readonly string[] Names = ["NONE", "AUTO 1", "AUTO 2", "AUTO 3"];
        private readonly ViperEw _ew;
        private readonly IReadOnlyList<(int Index, string Label)> _offered;
        private string _program, _threshold;
        public string Category { get; }
        public string Group { get; }
        public IReadOnlyList<string> Programs => Names;
        public IReadOnlyList<string> Thresholds { get; }

        public CmdsRow(ViperEw ew, string category, string group, JsonObject row)
        {
            (_ew, Category, Group) = (ew, category, group);
            _offered = (row["thresholds"] as JsonArray)?.OfType<JsonObject>().Select(t => ((int)(HornetNav.Number(t["index"]) ?? 1), (string?)t["label"] ?? "")).ToList() ?? [];
            Thresholds = _offered.Select(t => t.Label).ToList();
            _program = Names[Math.Clamp((int)(HornetNav.Number(row["program"]) ?? 1) - 1, 0, Names.Length - 1)];
            _threshold = (string?)row["default_threshold"]?["label"] ?? Thresholds.FirstOrDefault() ?? "";
        }

        public string Program { get => _program; set { _program = value; Save(); } }
        public string Threshold { get => _threshold; set { _threshold = value; Save(); } }

        private void Save() => _ew.SetCmdsThreat(Category, Group, Array.IndexOf(Names, _program) + 1, _offered.FirstOrDefault(t => t.Label == _threshold).Index);
    }

    /// <summary>An RWR row: its place in the ranking, display, search, unknown.</summary>
    public sealed class RwrRow(ViperEw ew, string table, string group, JsonObject row, Action moved)
    {
        private bool _display = row["display"]?.GetValue<bool>() == true, _search = row["search"]?.GetValue<bool>() == true,
            _unknown = row["unknown"]?.GetValue<bool>() == true;
        public string Group { get; } = group;
        public int Priority
        {
            get => (int)(HornetNav.Number(row["PRI"]) ?? 1);
            set
            {
                ew.MoveRwr(table, Group, value);
                moved();
            }
        }
        public bool Display { get => _display; set { _display = value; Save(); } }
        public bool Search { get => _search; set { _search = value; Save(); } }
        public bool Unknown { get => _unknown; set { _unknown = value; Save(); } }
        private void Save() => ew.SetRwr(table, Group, _display, _search, _unknown);
    }

    /// <summary>An ROE row: an aircraft group's sovereignty.</summary>
    public sealed class RoeRow(ViperEw ew, JsonObject row)
    {
        private static readonly string[] Names = ["FRIENDLY", "HOSTILE", "UNKNOWN"];
        public string Group { get; } = (string?)row["group_name"] ?? "";
        public IReadOnlyList<string> Choices => Names;
        public string Sovereignty
        {
            get => Names[Math.Clamp((int)(HornetNav.Number(row["sovereignty"]) ?? ViperEw.Unknown) - 1, 0, 2)];
            set => ew.SetSovereignty(Group, Array.IndexOf(Names, value) + 1);
        }
    }

    private void ShowTables()
    {
        var ew = Session.VEw;
        TypeSovereignty.IsChecked = ew.RoeSettings["TypeSovereignty"]?.GetValue<bool>() == true;
        Mode4Status.IsChecked = ew.RoeSettings["Mode4Status"]?.GetValue<bool>() == true;
        FriendlyStatus.Text = Session.FriendlyNote ?? "";
        // Rebuilt only for another cartridge or an outside change, so editing a cell is never interrupted.
        if (ReferenceEquals(_tablesFor.Item1, Session.Cartridge) && _tablesFor.Item2 == Session.EwRevision) return;
        _tablesFor = (Session.Cartridge, Session.EwRevision);
        CmdsGrid.ItemsSource = ew.CmdsThreats.Select(t => new CmdsRow(ew, t.Category, t.Group, t.Row)).ToList();
        RoeGrid.ItemsSource = ew.RoeList.Select(r => new RoeRow(ew, r)).ToList();
        ShowRwr();
    }

    private void ShowRwr()
    {
        if (RwrTable.SelectedItem is not string table) return;
        var ew = Session.VEw;
        // After a priority edit the grid shows the new ranking (once the edit has finished).
        void Moved() => Dispatcher.BeginInvoke(ShowRwr);
        RwrGrid.ItemsSource = ew.Rwr(table).Select(r => new RwrRow(ew, table, r.Group, r.Row, Moved)).ToList();
    }

    private void RwrTable_Changed(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;
        if (IsLoaded) ShowRwr();
    }

    private void RoeSettings_Click(object sender, RoutedEventArgs e)
    {
        Session.VEw.RoeSettings["TypeSovereignty"] = TypeSovereignty.IsChecked == true;
        Session.VEw.RoeSettings["Mode4Status"] = Mode4Status.IsChecked == true;
    }

    private void Friendly_Click(object sender, RoutedEventArgs e) => FriendlyStatus.Text = Session.MarkFriendly();
}
