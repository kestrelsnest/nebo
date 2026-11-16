using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Nebo.Client.Core;
using Nebo.Client.Core.Dtc;
using Nebo.Client.Dtc;

namespace Nebo.Client.Panels;

/// <summary>The DTC tab's F-14B(U) sections: the flight plans (waypoints, lines, additional points),
/// JDAM targets, CMDS and TIS. Shown by DtcPanel in place of the other jets' tabs; edits go
/// straight into the cartridge (DtcSession), which redraws the map and calls <see cref="Refresh"/>.</summary>
public partial class TomcatTabs : UserControl
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static DtcSession Session => DtcSession.Instance;
    private bool _loading;

    public TomcatTabs()
    {
        InitializeComponent();
        // The built cells first: setting the selections below already fills them.
        BuildProgram();
        BuildAuto();
        _loading = true;
        StationBox.ItemsSource = TomcatJdam.StationNames;
        StationBox.SelectedIndex = 0;
        CmdsProgram.ItemsSource = Enumerable.Range(1, TomcatCmds.Programs.Count).Select(n => TomcatCmds.AutoNames.ElementAtOrDefault(n) ?? $"Program {n}").ToList();
        CmdsProgram.SelectedIndex = 0;
        OverrideType.ItemsSource = TomcatCmds.KnownThreats.OrderBy(t => t.Category).ThenBy(t => t.Display)
            .Select(t => new Choice(t.Key, $"{t.Category}: {t.Display}")).ToList();
        OverrideType.SelectedIndex = 0;
        OverrideProgram.ItemsSource = TomcatCmds.AutoNames;
        OverrideProgram.SelectedIndex = 1;
        OverrideThreshold.ItemsSource = TomcatCmds.Thresholds;
        OverrideThreshold.SelectedIndex = 2;
        _loading = false;
    }

    private sealed record Choice(string Key, string Text);
    private sealed record Row(int Num, string Text)
    {
        public override string ToString() => Text;
    }

    private static double N(JsonObject p, string key, double fallback = 0) => HornetNav.Number(p[key]) ?? fallback;
    private static double? Number(TextBox box) => double.TryParse(box.Text.Trim(), NumberStyles.Float, Inv, out var v) ? v : null;

    private static void Set(TextBox box, string text)
    {
        if (!box.IsKeyboardFocused) box.Text = text;
    }

    private static string Pos(JsonObject p) => p["lat"] is null ? "" : Coordinates.Ddm(N(p, "lat"), N(p, "lon"));

    private int Plan => Session.TomcatPlan;

    /// <summary>A point tab is open: right-clicks and typed coordinates place its points.</summary>
    public bool PointsTabOpen => OpenTab is "WPT" or "LINES" or "POINTS" or "JDAM";

    private string? OpenTab => (Tabs.SelectedItem as TabItem)?.Header as string;

    /// <summary>The open tab decides what a right-click places, through the session's own modes:
    /// waypoints, lines as SA lines, additional points as GPS points, JDAM targets.</summary>
    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || !ReferenceEquals(e.OriginalSource, Tabs)) return;   // lists and boxes inside bubble up here
        Session.SetPlace(OpenTab switch { "LINES" => DtcPlace.Sa, "POINTS" => DtcPlace.Gps, "JDAM" => DtcPlace.Jdam, _ => DtcPlace.Waypoints });
        if (OpenTab == "LINES") Session.SetSaPlace(SaKind.Geo);
        Session.Notify();
    }

    public void Refresh(Theatre? theatre)
    {
        _loading = true;
        ShowPlan();
        ShowWaypoints();
        ShowLines();
        ShowPoints();
        ShowJdam();
        ShowCmds();
        ShowTis();
        _loading = false;
    }

    // ---- The plan -------------------------------------------------------------------------------------

    private void ShowPlan()
    {
        var nav = Session.TNav;
        PlanBox.ItemsSource = Enumerable.Range(1, TomcatNav.Plans).Select(n =>
        {
            var name = (string?)nav.Plan(n)["name"] ?? "";
            var label = name.Length > 0 ? $"{n}: {name}" : n == 1 ? "1: ME Route" : n == DtcPlan.TomcatPlan ? $"Plan {n} (shared)" : $"Plan {n}";
            return new Row(n, label);
        }).ToList();
        PlanBox.SelectedIndex = Plan - 1;
        var p = nav.Plan(Plan);
        Set(PlanName, (string?)p["name"] ?? "");
        RouteAsLine.IsChecked = TomcatNav.RouteAsLine(p);
        PlanInfo.Text = $"{nav.TotalPoints(Plan)} of {TomcatNav.MaxPoints} points" +
                        (Plan == 1 ? " · plan 1's waypoints are the mission's" : Plan == DtcPlan.TomcatPlan ? " · the shared route goes here" : "");
    }

    private void PlanBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;
        if (!_loading && PlanBox.SelectedItem is Row r) Session.SetTomcatPlan(r.Num);
    }

    private void PlanName_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading || ((string?)Session.TNav.Plan(Plan)["name"] ?? "") == PlanName.Text.Trim()) return;
        Session.TNav.SetPlanName(Plan, PlanName.Text.Trim());
        Session.Notify();
    }

    private void RouteAsLine_Click(object sender, RoutedEventArgs e)
    {
        Session.TNav.SetRouteAsLine(Plan, RouteAsLine.IsChecked == true);
        Session.Notify();
    }

    private void List_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Delete) return;
        Session.DeleteSelected();
        e.Handled = true;
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => Session.DeleteSelected();

    // ---- Waypoints (the session's waypoints) -------------------------------------------------------------

    private void ShowWaypoints()
    {
        var wps = Session.TNav.Waypoints(Plan);
        var rows = wps.Select((p, i) => new Row(i + 1,
            $"{i + 1,2} {(string?)p["name"],-8} {Pos(p)}  {N(p, "elev"),6:0} ft  " +
            ((string?)p["tot"] is { Length: > 0 } tot ? $"TOT {tot}" : N(p, "spd") > 0 ? $"{N(p, "spd"):0} kt" : ""))).ToList();
        WptList.ItemsSource = rows;
        WptList.SelectedItems.Clear();
        foreach (var r in rows.Where(r => Session.SelectedSet.Contains(r.Num))) WptList.SelectedItems.Add(r);
        var one = Session.SelectedSet.Count == 1 && Session.Selected is { } n ? wps.ElementAtOrDefault(n - 1) : null;
        WptEditor.IsEnabled = one is not null;
        WptTitle.Text = !TomcatNav.EditsWaypoints(Plan) ? "Plan 1's waypoints come from the mission: pick plan 2–12 for your own."
            : one is null ? "Pick one waypoint, or right-click the map to add one." : $"Waypoint {Session.Selected}";
        if (one is null) return;
        Set(WptName, (string?)one["name"] ?? "");
        Set(WptElev, N(one, "elev").ToString("0", Inv));
        Set(WptSpeed, N(one, "spd") > 0 ? N(one, "spd").ToString("0", Inv) : "");
        Set(WptTot, (string?)one["tot"] ?? "");
    }

    private void Wpt_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading || Session.SelectedSet.Count != 1 || Session.Selected is not { } n || Session.Waypoint(n) is not { } p) return;
        Session.TNav.SetWaypoint(Plan, n, WptName.Text.Trim(), Number(WptElev) ?? N(p, "elev"), Number(WptSpeed) ?? 0, WptTot.Text);
        Session.Notify();
    }

    private void WptList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;
        if (!_loading) Session.SelectMany(WptList.SelectedItems.OfType<Row>().Select(r => r.Num).ToList());
    }

    private void Up_Click(object sender, RoutedEventArgs e) => Shift(-1);
    private void Down_Click(object sender, RoutedEventArgs e) => Shift(+1);

    private void Shift(int by)
    {
        if (Session.SelectedSet.Count != 1 || Session.Selected is not { } n) return;
        var count = Session.TNav.Waypoints(Plan).Count;
        var to = n + by;
        if (to < 1 || to > count) return;
        var order = Enumerable.Range(1, count).ToList();
        (order[n - 1], order[to - 1]) = (order[to - 1], order[n - 1]);
        Session.TNav.ReorderWaypoints(Plan, order);
        Session.Select(to);
    }

    // ---- Lines (the session's SA lines) ----------------------------------------------------------------

    private sealed record LineChoice(int? Index, string Text);

    private void ShowLines()
    {
        var lines = Session.TNav.Lines(Plan);
        var current = Session.SaPlace == SaKind.Geo ? Session.SaLine : null;
        var choices = lines.Select((l, i) => new LineChoice(i, $"Line {i + 1} ({TomcatNav.PointsOf(l).Count} pts{(TomcatNav.IsClosed(l) ? ", closed" : "")})")).ToList();
        if (lines.Count < Session.TNav.LineLimit(Plan)) choices.Add(new LineChoice(null, "New line"));
        LineBox.ItemsSource = choices;
        LineBox.SelectedItem = choices.FirstOrDefault(c => c.Index == current) ?? choices.LastOrDefault();
        var line = current is { } ci ? lines.ElementAtOrDefault(ci) : null;
        LineClosed.IsEnabled = line is not null;
        LineClosed.IsChecked = line is not null && TomcatNav.IsClosed(line);
        var rows = line is null ? [] : TomcatNav.PointsOf(line).Select((p, k) => new Row(k, $"{k + 1,2} {Pos(p)}  {N(p, "elev"),6:0} ft")).ToList();
        LineList.ItemsSource = rows;
        LineList.SelectedItems.Clear();
        foreach (var r in rows.Where(r => current is { } c && Session.SaSelectedSet.Contains(new SaRef(SaKind.Geo, c, r.Num)))) LineList.SelectedItems.Add(r);
    }

    private void LineBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;
        if (!_loading && LineBox.SelectedItem is LineChoice c) Session.SetSaLine(c.Index);
    }

    private void LineClosed_Click(object sender, RoutedEventArgs e)
    {
        if (Session.SaLine is not { } l) return;
        if (!Session.TNav.SetClosed(Plan, l, LineClosed.IsChecked == true))
            MessageBox.Show(Window.GetWindow(this), $"A closed line holds {TomcatNav.MaxClosedLinePoints} points at most (the jet repeats the first one).",
                "F-14 line", MessageBoxButton.OK, MessageBoxImage.Information);
        Session.Notify();
    }

    private void DeleteLine_Click(object sender, RoutedEventArgs e)
    {
        if (Session.SaLine is not { } l) return;
        Session.TNav.RemoveLine(Plan, l);
        Session.SelectSa(null);
        Session.SetSaLine(null);
    }

    private void LineList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;
        if (_loading || Session.SaLine is not { } l) return;
        Session.SelectSaMany(LineList.SelectedItems.OfType<Row>().Select(r => new SaRef(SaKind.Geo, l, r.Num)).ToList());
    }

    // ---- Additional points (the session's GPS points) --------------------------------------------------

    private void ShowPoints()
    {
        var aps = Session.TNav.Extra(Plan);
        var rows = aps.Select((p, i) => new Row(i + 1, $"{i + 1,2} {(string?)p["name"],-8} {Pos(p)}  {N(p, "elev"),6:0} ft")).ToList();
        ApList.ItemsSource = rows;
        ApList.SelectedItems.Clear();
        foreach (var r in rows.Where(r => Session.SelectedGpsSet.Contains(r.Num))) ApList.SelectedItems.Add(r);
        var one = Session.SelectedGpsSet.Count == 1 && Session.SelectedGps is { } n ? aps.ElementAtOrDefault(n - 1) : null;
        ApEditor.IsEnabled = one is not null;
        if (one is null) return;
        Set(ApName, (string?)one["name"] ?? "");
        Set(ApElev, N(one, "elev").ToString("0", Inv));
    }

    private void Ap_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading || Session.SelectedGpsSet.Count != 1 || Session.SelectedGps is not { } n || Session.GpsPoint(n) is not { } p) return;
        Session.TNav.SetExtra(Plan, n, ApName.Text.Trim(), Number(ApElev) ?? N(p, "elev"));
        Session.Notify();
    }

    private void ApList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;
        if (!_loading) Session.SelectGpsMany(ApList.SelectedItems.OfType<Row>().Select(r => r.Num).ToList());
    }

    // ---- JDAM -------------------------------------------------------------------------------------------

    private void ShowJdam()
    {
        var (station, target) = Session.JdamPick;
        StationBox.SelectedIndex = station - 1;
        var rows = Enumerable.Range(1, TomcatJdam.TargetsPerStation).Select(t =>
        {
            var d = Session.Jdam.Target(station, t)!;
            return new Row(t, TomcatJdam.IsActive(d)
                ? $"T{t} {(string?)d["name"],-10} {Pos(d)}  {N(d, "attack_heading"),5:0.0}°  {N(d, "drop_alt"),5:0} ft {N(d, "drop_spd"),3:0} kt"
                : $"T{t} (empty)");
        }).ToList();
        JdamList.ItemsSource = rows;
        JdamList.SelectedItem = rows[target - 1];
        var tgt = Session.Jdam.Target(station, target)!;
        var active = TomcatJdam.IsActive(tgt);
        JdamEditor.IsEnabled = active;
        JdamTitle.Text = active ? $"{TomcatJdam.StationNames[station - 1]} target {target}"
            : $"{TomcatJdam.StationNames[station - 1]} target {target}: right-click the map to place it";
        Set(JdamName, (string?)tgt["name"] ?? "");
        Set(JdamHeading, N(tgt, "attack_heading").ToString("0.0", Inv));
        Set(JdamAlt, N(tgt, "drop_alt", 20000).ToString("0", Inv));
        Set(JdamSpeed, N(tgt, "drop_spd", 450).ToString("0", Inv));
        JdamHasImpactHeading.IsChecked = tgt["has_impact_heading"]?.GetValue<bool>() == true;
        Set(JdamImpactHeading, N(tgt, "impact_heading").ToString("0", Inv));
        JdamHasImpactAngle.IsChecked = tgt["has_impact_angle"]?.GetValue<bool>() == true;
        Set(JdamImpactAngle, N(tgt, "impact_angle", 65).ToString("0", Inv));
        JdamImpactHeading.IsEnabled = JdamHasImpactHeading.IsChecked == true;
        JdamImpactAngle.IsEnabled = JdamHasImpactAngle.IsChecked == true;
        JdamZone.Text = active
            ? $"Launch zone {N(tgt, "lar_rmin_nmi"):0.0}–{N(tgt, "lar_rmax_nmi"):0.0} NM, ±{N(tgt, "lar_half_angle_deg"):0}° (impact heading and angle do not change it)"
            : "";
    }

    private void ApplyJdam()
    {
        var (s, t) = Session.JdamPick;
        if (_loading || Session.Jdam.Target(s, t) is not { } tgt || !TomcatJdam.IsActive(tgt)) return;
        Session.Jdam.Set(s, t, JdamName.Text.Trim(), Number(JdamHeading) ?? N(tgt, "attack_heading"), Number(JdamAlt) ?? N(tgt, "drop_alt"),
            Number(JdamSpeed) ?? N(tgt, "drop_spd"), JdamHasImpactHeading.IsChecked == true ? Number(JdamImpactHeading) ?? 0 : null,
            JdamHasImpactAngle.IsChecked == true ? Number(JdamImpactAngle) ?? 65 : null);
        Session.Notify();
    }

    private void Jdam_LostFocus(object sender, RoutedEventArgs e) => ApplyJdam();
    private void Jdam_Click(object sender, RoutedEventArgs e) => ApplyJdam();

    private void StationBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;
        if (!_loading && IsLoaded && StationBox.SelectedIndex >= 0) Session.SelectJdam(StationBox.SelectedIndex + 1, 1);
    }

    private void JdamList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;
        if (!_loading && JdamList.SelectedItem is Row r) Session.SelectJdam(Session.JdamPick.Station, r.Num);
    }

    // ---- CMDS ---------------------------------------------------------------------------------------------

    private static readonly string[] ProgramFields = ["BurstQuantity", "BurstInterval", "SalvoQuantity", "SalvoInterval"];
    private readonly TextBox[,] _program = new TextBox[4, 4];
    private readonly Dictionary<string, (ComboBox Program, ComboBox Threshold)> _auto = [];

    private void BuildProgram()
    {
        string[] heads = ["", "Burst qty", "Burst int (s)", "Salvo qty", "Salvo int (s)"];
        for (var r = 0; r <= TomcatCmds.Expendables.Length; r++) ProgramGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var c = 0; c < heads.Length; c++)
        {
            var h = new TextBlock { Text = heads[c], FontWeight = FontWeights.SemiBold };
            Grid.SetColumn(h, c);
            ProgramGrid.Children.Add(h);
        }
        for (var e = 0; e < TomcatCmds.Expendables.Length; e++)
        {
            var label = new TextBlock { Text = TomcatCmds.Expendables[e], VerticalAlignment = VerticalAlignment.Center };
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

    private void BuildAuto()
    {
        var row = 0;
        foreach (var cat in TomcatCmds.Categories)
        {
            AutoGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = cat, VerticalAlignment = VerticalAlignment.Center };
            var program = new ComboBox { ItemsSource = TomcatCmds.AutoNames, Margin = new Thickness(0, 1, 6, 1) };
            var threshold = new ComboBox { ItemsSource = TomcatCmds.Thresholds, Margin = new Thickness(0, 1, 0, 1) };
            var category = cat;
            void Changed(object? s, SelectionChangedEventArgs e)
            {
                e.Handled = true;
                if (_loading || !IsLoaded || program.SelectedIndex < 0 || threshold.SelectedIndex < 0) return;
                Session.TCmds.SetAuto(category, program.SelectedIndex, threshold.SelectedIndex + 1);
                threshold.IsEnabled = program.SelectedIndex > 0;
            }
            program.SelectionChanged += Changed;
            threshold.SelectionChanged += Changed;
            Grid.SetRow(label, row);
            Grid.SetRow(program, row);
            Grid.SetColumn(program, 1);
            Grid.SetRow(threshold, row);
            Grid.SetColumn(threshold, 2);
            AutoGrid.Children.Add(label);
            AutoGrid.Children.Add(program);
            AutoGrid.Children.Add(threshold);
            _auto[cat] = (program, threshold);
            row++;
        }
    }

    private string ProgramName => TomcatCmds.Programs[Math.Max(0, CmdsProgram.SelectedIndex)];

    private void ShowCmds()
    {
        var cmds = Session.TCmds;
        ShowProgram();
        var b = cmds.Bingo;
        Set(BingoChaff, ((int)N(b, "ChaffNum")).ToString(Inv));
        Set(BingoFlares, ((int)N(b, "FlaresNum")).ToString(Inv));
        Set(BingoOther1, ((int)N(b, "Other1Num")).ToString(Inv));
        Set(BingoOther2, ((int)N(b, "Other2Num")).ToString(Inv));
        foreach (var (cat, (program, threshold)) in _auto)
        {
            var (p, t) = cmds.Auto(cat);
            program.SelectedIndex = p;
            threshold.SelectedIndex = t - 1;
            threshold.IsEnabled = p > 0;
        }
        var names = TomcatCmds.KnownThreats.ToDictionary(t => t.Key, t => t.Display);
        OverrideList.ItemsSource = cmds.Overrides.Select(o => new Choice(o.Key,
            $"{names.GetValueOrDefault(o.Key, o.Key),-22} {TomcatCmds.AutoNames.ElementAtOrDefault(o.Program),-28} {TomcatCmds.Thresholds.ElementAtOrDefault(o.Threshold - 1)}")).ToList();
        OverrideList.DisplayMemberPath = "Text";
    }

    private void ShowProgram()
    {
        var p = Session.TCmds.Program(ProgramName);
        Set(CmdsPriority, ((int)N(p, "Priority")).ToString(Inv));
        for (var e = 0; e < TomcatCmds.Expendables.Length; e++)
            for (var f = 0; f < ProgramFields.Length; f++)
            {
                var v = HornetNav.Number(p[TomcatCmds.Expendables[e]]?[ProgramFields[f]]) ?? 0;
                Set(_program[e, f], f % 2 == 0 ? v.ToString("0", Inv) : v.ToString("0.00", Inv));
            }
    }

    private void CmdsProgram_Changed(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;
        if (IsLoaded) ShowProgram();
    }

    private void CmdsApply_Click(object sender, RoutedEventArgs e)
    {
        var name = ProgramName;
        var p = Session.TCmds.Program(name);
        if (Number(CmdsPriority) is { } prio) Session.TCmds.SetPriority(name, (int)prio);
        for (var x = 0; x < TomcatCmds.Expendables.Length; x++)
        {
            double V(int f) => Number(_program[x, f]) ?? HornetNav.Number(p[TomcatCmds.Expendables[x]]?[ProgramFields[f]]) ?? 0;
            Session.TCmds.SetProgram(name, TomcatCmds.Expendables[x], (int)V(0), V(1), (int)V(2), V(3));
        }
        Session.Notify();
    }

    private void BingoApply_Click(object sender, RoutedEventArgs e)
    {
        static int I(TextBox b) => int.TryParse(b.Text.Trim(), out var v) ? v : 0;
        Session.TCmds.SetBingo(I(BingoChaff), I(BingoFlares), I(BingoOther1), I(BingoOther2));
        Session.Notify();
    }

    private void OverrideAdd_Click(object sender, RoutedEventArgs e)
    {
        if (OverrideType.SelectedItem is not Choice c) return;
        Session.TCmds.SetOverride(c.Key, Math.Max(0, OverrideProgram.SelectedIndex), Math.Max(0, OverrideThreshold.SelectedIndex) + 1);
        Session.Notify();
    }

    private void OverrideRemove_Click(object sender, RoutedEventArgs e)
    {
        if (OverrideList.SelectedItem is not Choice c) return;
        Session.TCmds.RemoveOverride(c.Key);
        Session.Notify();
    }

    // ---- TIS -----------------------------------------------------------------------------------------------

    private void ShowTis()
    {
        var tis = Session.Tis;
        TisMission.IsChecked = tis.UseMissionCallsign;
        TisOwn.IsEnabled = !tis.UseMissionCallsign;
        Set(TisOwn, tis.OwnCallsign);
        TisWingmen.IsChecked = tis.AddWingmen;
        TisList.ItemsSource = tis.SendTo;
    }

    private void Tis_Click(object sender, RoutedEventArgs e)
    {
        Session.Tis.UseMissionCallsign = TisMission.IsChecked == true;
        Session.Tis.AddWingmen = TisWingmen.IsChecked == true;
        Session.Notify();
    }

    private void TisOwn_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading || Session.Tis.OwnCallsign == TomcatTis.Callsign(TisOwn.Text).TrimEnd()) return;
        Session.Tis.OwnCallsign = TisOwn.Text;
        Session.Notify();
    }

    private void TisAdd_Click(object sender, RoutedEventArgs e)
    {
        if (Session.Tis.AddSendTo(TisNew.Text)) TisNew.Text = "";
        Session.Notify();
    }

    private void TisNew_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        TisAdd_Click(sender, e);
        e.Handled = true;
    }

    private void TisRemove_Click(object sender, RoutedEventArgs e)
    {
        Session.Tis.RemoveSendTo(TisList.SelectedIndex);
        Session.Notify();
    }
}
