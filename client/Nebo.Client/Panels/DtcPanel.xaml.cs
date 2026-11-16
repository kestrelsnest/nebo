using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Nebo.Client.Core;
using Nebo.Client.Core.Dtc;
using Nebo.Client.Dtc;
using Nebo.Client.Map.Airfields;

namespace Nebo.Client.Panels;

/// <summary>The DTC tab's cartridge editor (F/A-18C): the cartridge, the departure field, the
/// waypoints and routes (placed on the tab's map), NAV settings, and the push to pilots. Edits
/// go straight into the cartridge (DtcSession); the map redraws from it.</summary>
public partial class DtcPanel : UserControl
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private bool _loading;

    private static DtcSession Session => DtcSession.Instance;

    /// <summary>A DTC editor is on screen: its add/delete key works.</summary>
    public static bool IsOpen => _open > 0;
    private static int _open;

    public DtcPanel()
    {
        InitializeComponent();
        BuildCommRows();
        Loaded += (_, _) =>
        {
            _open++;
            Session.Changed += Refresh;
            AppState.Store.PilotsChanged += RefreshPilots;
            AppState.Packages.Changed += RefreshPilots;
            Refresh();
            RefreshPilots();
        };
        Unloaded += (_, _) =>
        {
            _open--;
            Session.Changed -= Refresh;
            AppState.Store.PilotsChanged -= RefreshPilots;
            AppState.Packages.Changed -= RefreshPilots;
        };
    }

    private int CurrentRoute => Route2.IsChecked == true ? 2 : Route3.IsChecked == true ? 3 : 1;

    // ---- Show the cartridge ---------------------------------------------------------------------

    private void Refresh()
    {
        _loading = true;
        var c = Session.Cartridge;
        var nav = Session.Nav;
        if (!NameBox.IsKeyboardFocused) NameBox.Text = c.Name;
        if (AircraftBox.ItemsSource is null) AircraftBox.ItemsSource = DtcSession.Aircraft.Select(a => a.Name).ToList();
        AircraftBox.SelectedItem = Session.AircraftName;
        var points = Session.IsViper ? $"{Session.Stpt.Points.Count} steerpoints"
            : Session.IsTomcat ? $"plan {Session.TomcatPlan}: {Session.TNav.Waypoints(Session.TomcatPlan).Count} waypoints"
            : $"{nav.Waypoints.Count} waypoints";
        InfoText.Text = $"{Session.AircraftName} · {(c.Terrain.Length > 0 ? c.Terrain : "no map yet")} · {points}"
                        + (Session.Path.Length > 0 ? $" · {System.IO.Path.GetFileName(Session.Path)}" : "");

        var theatre = AppState.Store.Theatre;
        ShareBox.IsChecked = Session.SharePoints;
        ShareText.Text = Session.SharePoints ? Session.ShareNote ?? "" : "";
        ShareText.Visibility = ShareText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        // No tables for this jet (no DCS on this PC): say why instead of an editor on a placeholder.
        var missing = Session.TablesMissing;
        if (missing is not null) InfoText.Text = $"{Session.AircraftName}: {missing}";
        EditorTabs.Visibility = missing is null && !Session.IsViper && !Session.IsTomcat ? Visibility.Visible : Visibility.Collapsed;
        ViperTabs.Visibility = missing is null && Session.IsViper ? Visibility.Visible : Visibility.Collapsed;
        TomcatTabs.Visibility = missing is null && Session.IsTomcat ? Visibility.Visible : Visibility.Collapsed;
        if (Session.IsViper || Session.IsTomcat)
        {
            if (Session.IsViper) ViperTabs.Refresh(theatre);
            else TomcatTabs.Refresh(theatre);
            ShowPointTools(theatre);
            _loading = false;
            return;
        }
        PointList.ItemsSource = nav.Waypoints.Select(p => new PointRow(HornetNav.Num(p), Describe(p, theatre))).ToList();
        PointList.SelectedItems.Clear();
        foreach (var row in ((IEnumerable<PointRow>)PointList.ItemsSource).Where(r => Session.SelectedSet.Contains(r.Num)))
            PointList.SelectedItems.Add(row);
        ShowPoint();
        ShowRoute();
        ShowSettings();
        ShowComm();
        ShowGps(theatre);
        ShowTacan();
        ShowSa();
        ShowEw();
        ShowPointTools(theatre);
        _loading = false;
    }

    // ---- Right-click mode and typed coordinates ---------------------------------------------------

    private void ShowPointTools(Theatre? theatre)
    {
        ModeAdd.IsChecked = !Session.DeleteMode;
        ModeDelete.IsChecked = Session.DeleteMode;
        if (Session.DeleteMode) ModeDelete.Foreground = System.Windows.Media.Brushes.IndianRed;
        else ModeDelete.SetResourceReference(ForegroundProperty, "Nebo.Text");
        ModeHint.Text = $"({Input.Bindings.Get(Input.Bindings.DtcMode)} switches)";
        var pointsTab = Session.IsViper ? ViperTabs.PointsTabOpen : Session.IsTomcat ? TomcatTabs.PointsTabOpen
            : EditorTabs.SelectedItem is TabItem { Header: "Waypoints" or "GPS" or "SA" };
        PointTools.Visibility = pointsTab ? Visibility.Visible : Visibility.Collapsed;
        // The box shows where the one selected point is: edit it and Move selected, or Add another.
        var at = SelectedPosition();
        CoordMove.IsEnabled = at is not null;
        if (!CoordBox.IsKeyboardFocused && at is { } p && theatre is not null)
        {
            var (lat, lon) = theatre.ToLatLon(p.X, p.Z);
            CoordBox.Text = Coordinates.Ddm(lat, lon);
        }
    }

    /// <summary>The map position of the open tab's one selected point, or null (none or several).</summary>
    private static (double X, double Z)? SelectedPosition()
    {
        static (double, double) Xz(JsonObject o) => (HornetNav.Number(o["x"]) ?? 0, HornetNav.Number(o["y"]) ?? 0);
        return Session.Place switch
        {
            DtcPlace.Gps => Session.SelectedGpsSet.Count == 1 && Session.Gps.Point(Session.SelectedGpsSet.First()) is { } g ? Xz(g) : null,
            DtcPlace.Sa => Session.SaSelectedSet.Count == 1 ? Session.SaPosition(Session.SaSelectedSet.First()) : null,
            _ => Session.SelectedSet.Count == 1 && Session.Waypoint(Session.SelectedSet.First()) is { } w ? Xz(w) : null,
        };
    }

    private void Mode_Click(object sender, RoutedEventArgs e) => Session.SetDeleteMode(ModeDelete.IsChecked == true);

    /// <summary>The typed position as map metres, or null (and why, under the box).</summary>
    private (double X, double Z)? TypedPosition()
    {
        if (AppState.Store.Theatre is not { } theatre)
        {
            CoordStatus.Text = "Connect to a server first: positions need its map.";
            return null;
        }
        if (!CoordinateInput.TryParse(CoordBox.Text, out var lat, out var lon))
        {
            CoordStatus.Text = "Not a position I can read. Examples: N41 40.123 E44 56.789 · 41.669, 44.955 · 38T LM 12345 67890";
            return null;
        }
        var (x, z) = theatre.FromLatLon(lat, lon);
        if (x < theatre.X0 || x > theatre.X1 || z < theatre.Z0 || z > theatre.Z1)
        {
            CoordStatus.Text = $"{Coordinates.Ddm(lat, lon)} is outside the {theatre.Id} map.";
            return null;
        }
        CoordStatus.Text = "";
        return (x, z);
    }

    private void CoordAdd_Click(object sender, RoutedEventArgs e)
    {
        if (TypedPosition() is not { } at) return;
        if (Session.AddHere(at.X, at.Z) is { } why) CoordStatus.Text = why;
    }

    private void CoordMove_Click(object sender, RoutedEventArgs e)
    {
        if (TypedPosition() is { } at && !Session.MoveSelectedTo(at.X, at.Z)) CoordStatus.Text = "Select one point to move.";
    }

    private void CoordBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;
        CoordAdd_Click(sender, e);
        e.Handled = true;
    }

    /// <summary>The Delete key in a point list deletes its selection.</summary>
    private void List_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Delete) return;
        Session.DeleteSelected();
        e.Handled = true;
    }

    private sealed record PointRow(int Num, string Text)
    {
        public override string ToString() => Text;
    }

    private static string Describe(JsonObject p, Theatre? theatre)
    {
        var num = HornetNav.Num(p);
        var pos = theatre is null ? "" : Coordinates.Ddm(theatre.ToLatLon(HornetNav.Number(p["x"]) ?? 0, HornetNav.Number(p["y"]) ?? 0).Lat,
            theatre.ToLatLon(HornetNav.Number(p["x"]) ?? 0, HornetNav.Number(p["y"]) ?? 0).Lon);
        var ft = (HornetNav.Number(p["alt"]) ?? 0) * HornetNav.FeetPerMeter;
        var type = HornetNav.Number(p["altitudeType"]) == HornetNav.Radar ? "AGL" : "MSL";
        var routes = string.Concat(Enumerable.Range(1, HornetNav.Routes).Where(r => p[$"R{r}"]?.GetValue<bool>() == true).Select(r => $" R{r}"));
        return $"{num,2} {(string?)p["text_note"],-5} {pos}  {ft,6:0} ft {type}{routes}";
    }

    private void ShowPoint()
    {
        var p = Session.Selected is { } n ? Session.Nav.Waypoint(n) : null;
        var many = Session.SelectedSet.Count > 1;
        PointEditor.IsEnabled = p is not null;
        LabelBox.IsEnabled = NoteBox.IsEnabled = !many;
        if (p is null)
        {
            PointTitle.Text = "Pick a waypoint, or right-click the map to add one. Ctrl/Shift picks several.";
            LabelBox.Text = NoteBox.Text = AltBox.Text = "";
            R1Box.IsChecked = R2Box.IsChecked = R3Box.IsChecked = false;
            return;
        }
        PointTitle.Text = many
            ? $"{Session.SelectedSet.Count} waypoints ({string.Join(", ", Session.SelectedSet.Order())}): altitude and routes apply to all"
            : $"Waypoint {HornetNav.Num(p)}";
        LabelBox.Text = (string?)p["text_note"] ?? "";
        NoteBox.Text = (string?)p["note"] ?? "";
        AltBox.Text = ((HornetNav.Number(p["alt"]) ?? 0) * HornetNav.FeetPerMeter).ToString("0", Inv);
        AltTypeBox.SelectedIndex = HornetNav.Number(p["altitudeType"]) == HornetNav.Radar ? 1 : 0;
        // Several: ticked when all are in the route, empty when none, filled square when some.
        bool? InRoute(int r)
        {
            var count = Session.SelectedSet.Count(num => Session.Nav.Waypoint(num)?[$"R{r}"]?.GetValue<bool>() == true);
            return count == 0 ? false : count == Session.SelectedSet.Count ? true : null;
        }
        R1Box.IsChecked = InRoute(1);
        R2Box.IsChecked = InRoute(2);
        R3Box.IsChecked = InRoute(3);
    }

    private void ShowRoute()
    {
        var nav = Session.Nav;
        var route = CurrentRoute;
        var selected = (RouteList.SelectedItem as PointRow)?.Num;
        var lengths = nav.LegLengths(route);
        RouteList.ItemsSource = nav.Route(route).Select((num, i) =>
        {
            var p = nav.Waypoint(num)!;
            var kts = (HornetNav.Number(nav.Leg(route, num)?["speed"]) ?? HornetNav.DefaultSpeedKmh) / 1.852;
            var leg = i > 0 && i - 1 < lengths.Count ? $"{lengths[i - 1] / 1852,6:0.0} NM" : "";
            return new PointRow(num, $"{i + 1,2}. STPT {num,-2} {(string?)p["text_note"],-5} {kts,4:0} kt {leg}");
        }).ToList();
        RouteTotal.Text = lengths.Count == 0 ? "" : $"Total {lengths.Sum() / 1852:0.0} NM";
        RouteList.SelectedItem = ((IEnumerable<PointRow>)RouteList.ItemsSource).FirstOrDefault(r => r.Num == selected);
        ShowLeg();
    }

    private void ShowLeg()
    {
        var num = (RouteList.SelectedItem as PointRow)?.Num;
        var leg = num is { } n ? Session.Nav.Leg(CurrentRoute, n) : null;
        SpeedBox.IsEnabled = leg is not null;
        if (!SpeedBox.IsKeyboardFocused)
            SpeedBox.Text = leg is null ? "" : ((HornetNav.Number(leg["speed"]) ?? HornetNav.DefaultSpeedKmh) / 1.852).ToString("0", Inv);
    }

    private void ShowSettings()
    {
        var s = Session.Nav.Settings;
        TacanChannel.Text = Int(s["TACAN"]?["Channel"]).ToString(Inv);
        TacanBand.SelectedIndex = Int(s["TACAN"]?["ChannelMode"]) == 2 ? 1 : 0;
        TacanMode.SelectedIndex = Math.Clamp(Int(s["TACAN"]?["Mode"], 2) - 1, 0, 2);
        TacanOn.IsChecked = s["TACAN"]?["OnOff"]?.GetValue<bool>() == true;
        IclsChannel.Text = Int(s["ICLS"]?["Channel"], 1).ToString(Inv);
        IclsOn.IsChecked = s["ICLS"]?["OnOff"]?.GetValue<bool>() == true;
        AclsFreq.Text = (HornetNav.Number(s["ACLS"]?["Frequency"]) ?? 225).ToString("0.000", Inv);
        AclsOn.IsChecked = s["ACLS"]?["OnOff"]?.GetValue<bool>() == true;
        HomeWp.Text = Int(s["Home_Waypoint"]?["FPAS_HOME_WP"], 1).ToString(Inv);
        WarnBaro.Text = Int(s["Altitude_Warning"]?["Warn_Alt_Baro"]).ToString(Inv);
        WarnRadar.Text = Int(s["Altitude_Warning"]?["Warn_Alt_Rdr"]).ToString(Inv);
    }

    private static int Int(JsonNode? n, int fallback = 0) => (int)(HornetNav.Number(n) ?? fallback);

    // ---- Cartridge ------------------------------------------------------------------------------

    private void New_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(Window.GetWindow(this), "Start new cartridges for every jet with DCS's defaults? Unsaved changes are lost.", "New cartridges",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            Session.New();
    }

    private void Share_Click(object sender, RoutedEventArgs e) => Session.SetSharePoints(ShareBox.IsChecked == true);

    /// <summary>Another jet's cartridge in the tabs (each jet keeps its own).</summary>
    private void AircraftBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;
        if (_loading || !IsLoaded || AircraftBox.SelectedItem is not string name || name == Session.AircraftName) return;
        Session.SetAircraft(DtcSession.Aircraft.First(a => a.Name == name).Type);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Data cartridge (*.dtc)|*.dtc", InitialDirectory = DtcFolder() };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try { Session.Open(dialog.FileName); }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(this), ex.Message, "Open cartridge", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = Session.Cartridge.Name.Length > 0 ? Session.Cartridge.Name : "NEBO";
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "Data cartridge (*.dtc)|*.dtc", InitialDirectory = DtcFolder(),
            FileName = string.Concat(name.Split(System.IO.Path.GetInvalidFileNameChars())) + ".dtc",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) Session.Save(dialog.FileName);
    }

    private static string DtcFolder()
    {
        var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games", "DCS", "DTC");
        return System.IO.Directory.Exists(dir) ? dir : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private void Name_LostFocus(object sender, RoutedEventArgs e)
    {
        if (Session.Cartridge.Name == NameBox.Text) return;
        Session.Cartridge.Name = NameBox.Text.Trim();
        Session.Notify();
    }

    // ---- Departure: TACAN / ICLS and waypoint 1 from the field or ship --------------------------

    private sealed record Departure(string Label, string Short, double X, double Z, int? TacanChannel, char Band, int? Icls)
    {
        public override string ToString() => Label;  // the combo box's closed face uses this, not DisplayMemberPath
    }

    private void DepartureBox_DropDownOpened(object? sender, EventArgs e)
    {
        var store = AppState.Store;
        if (store.Theatre is not { } theatre) return;
        var side = store.Coalition == "red" ? 1 : 2;
        var list = new List<Departure>();
        foreach (var c in store.Latest?.Carriers ?? [])
        {
            if (store.Find(c.Id)?.Unit is not { Lat: { } lat, Lon: { } lon } u) continue;
            var (x, z) = theatre.FromLatLon(lat, lon);
            var m = Regex.Match(c.Tacan ?? "", @"^(\d+)\s*([XY])", RegexOptions.IgnoreCase);
            list.Add(new Departure($"{u.Name ?? c.Type} (carrier{(m.Success ? ", TACAN " + m.Value : "")}{(c.Icls is { } i ? ", ICLS " + i : "")})",
                Short(u.Name ?? c.Type), x, z, m.Success ? int.Parse(m.Groups[1].Value, Inv) : null,
                m.Success ? char.ToUpperInvariant(m.Groups[2].Value[0]) : 'X', c.Icls));
        }
        foreach (var f in store.Airfields.Where(a => !a.IsHelipad).OrderByDescending(a => a.Coalition == side).ThenBy(a => a.Name))
        {
            var tacan = DcsAirfieldData.BeaconsFor(theatre.Id, f.Id).FirstOrDefault(b => b.IsTacan && b.Channel is not null);
            list.Add(new Departure($"{f.Name}{(tacan is null ? "" : $" (TACAN {tacan.ChannelText})")}", Short(f.Name), f.X, f.Z,
                tacan?.Channel, 'X', null));
        }
        DepartureBox.ItemsSource = list;
    }

    private static string Short(string name)
    {
        var letters = new string(name.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return letters.Length > 5 ? letters[..5] : letters;
    }

    private void Departure_Click(object sender, RoutedEventArgs e)
    {
        if (DepartureBox.SelectedItem is not Departure d) return;
        if (Session.IsTomcat)
        {
            // The open plan's first waypoint on the field or ship.
            if (Session.Waypoint(1) is null && Session.AddWaypoint(d.X, d.Z) is null)
            {
                PushStatus.Text = "Plan 1 is the mission's route: pick plan 2–12 in the NAV tab.";
                return;
            }
            Session.MoveWaypoint(1, d.X, d.Z);
            Session.Waypoint(1)!["name"] = d.Short;
            Session.Select(1);
            return;
        }
        if (Session.IsViper)
        {
            // The F-16C's cartridge has no TACAN: steerpoint 1 on the field or ship.
            var stpt = Session.Stpt;
            var elev = AppState.Store.Terrain is { } g ? LineOfSight.HeightAt(g, d.X, d.Z) ?? 0 : 0;
            if (stpt.Point(1) is null) Session.AddWaypoint(d.X, d.Z);
            var p1 = stpt.Point(1)!;
            stpt.Move(1, d.X, d.Z);
            stpt.Set(1, d.Icls is null ? elev : 0, HornetNav.Number(p1["routeAltitude"]) ?? ViperNav.DefaultRouteAltM,
                (int)(HornetNav.Number(p1["altitudeType"]) ?? ViperNav.Msl), HornetNav.Number(p1["speed"]) ?? ViperNav.DefaultSpeedKmh,
                (int)(HornetNav.Number(p1["velocityType"]) ?? 3), (string?)p1["type"] ?? "STPT", d.Short);
            if (Session.Cartridge.Terrain.Length == 0 && AppState.Store.Theatre is { } vt) Session.Cartridge.Terrain = vt.Id;
            Session.Select(1);
            return;
        }
        var nav = Session.Nav;
        if (d.TacanChannel is { } ch) nav.SetTacan(ch, d.Band, 1, true);   // T/R: range and bearing to it
        if (d.Icls is { } icls) nav.SetIcls(icls, true);
        // Waypoint 1 on the field or ship: moved there, or made (1 is then the first free number).
        var ground = AppState.Store.Terrain is { } grid ? LineOfSight.HeightAt(grid, d.X, d.Z) ?? 0 : 0;
        if (nav.Waypoint(1) is null) Session.AddWaypoint(d.X, d.Z);
        nav.Move(1, d.X, d.Z);
        nav.SetAltitude(1, d.Icls is null ? ground : 0, HornetNav.Baro);
        nav.SetText(1, d.Short, d.Label);
        if (Session.Cartridge.Terrain.Length == 0 && AppState.Store.Theatre is { } t) Session.Cartridge.Terrain = t.Id;
        Session.Select(1);
    }

    // ---- Waypoints ------------------------------------------------------------------------------

    private void Build_Click(object sender, RoutedEventArgs e)
    {
        var route = int.Parse((string)((Button)sender).Tag, Inv);
        // The selection when two or more are picked, else every waypoint; in the list's order.
        var all = Session.Nav.Waypoints.Select(HornetNav.Num)
            .Where(n => Session.SelectedSet.Count < 2 || Session.SelectedSet.Contains(n)).ToList();
        if (all.Count < 2)
        {
            PointTitle.Text = "Place at least two waypoints first (right-click the map).";
            return;
        }
        var added = Session.Nav.BuildRoute(route, all);
        (route switch { 2 => Route2, 3 => Route3, _ => Route1 }).IsChecked = true;
        Session.Notify();
        if (added < all.Count)
            MessageBox.Show(Window.GetWindow(this), $"A route holds {HornetNav.MaxRoutePoints} waypoints: route {route} has waypoints 1–{added} of the list's order.",
                "Build route", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // Drag a waypoint in the list to a new place: the waypoints are renumbered in the list's order.
    private Point? _dragFrom;
    private PointRow? _dragRow;

    private void PointList_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _dragFrom = e.GetPosition(PointList);
        _dragRow = RowAt(e.OriginalSource);
    }

    private void PointList_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragFrom is not { } from || _dragRow is null || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
        var d = e.GetPosition(PointList) - from;
        if (Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var row = _dragRow;
        _dragFrom = null;
        DragDrop.DoDragDrop(PointList, row, DragDropEffects.Move);
    }

    private void PointList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(PointRow)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void PointList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(PointRow)) is not PointRow moved) return;
        var order = Session.Nav.Waypoints.Select(HornetNav.Num).ToList();
        var target = RowAt(e.OriginalSource);
        order.Remove(moved.Num);
        var at = target is null ? order.Count : order.IndexOf(target.Num);
        if (at < 0) at = order.Count;
        // Dropped on the row below its old place: it goes after that row.
        if (target is not null && target.Num > moved.Num) at++;
        order.Insert(Math.Min(at, order.Count), moved.Num);
        Session.Nav.Renumber(order);
        Session.Select(order.IndexOf(moved.Num) + 1);
    }

    private static PointRow? RowAt(object source)
    {
        for (var d = source as DependencyObject; d is not null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
            if (d is ListBoxItem item) return item.DataContext as PointRow;
        return null;
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => Session.DeleteSelected();

    private void PointList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        // In the order picked, so the last one picked is the editor's.
        var picked = PointList.SelectedItems.OfType<PointRow>().Select(r => r.Num).ToList();
        var last = (e.AddedItems.Count > 0 ? e.AddedItems[^1] : null) as PointRow;
        if (last is not null && picked.Remove(last.Num)) picked.Add(last.Num);
        Session.SelectMany(picked);
    }

    private void Point_LostFocus(object sender, RoutedEventArgs e) => ApplyPoint();

    private void AltType_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading) ApplyPoint();
    }

    private void ApplyPoint()
    {
        if (_loading || Session.Selected is not { } n || Session.Nav.Waypoint(n) is null) return;
        if (Session.SelectedSet.Count == 1) Session.Nav.SetText(n, LabelBox.Text.Trim().ToUpperInvariant(), NoteBox.Text.Trim());
        if (double.TryParse(AltBox.Text, NumberStyles.Float, Inv, out var ft) || double.TryParse(AltBox.Text, out ft))
            foreach (var num in Session.SelectedSet)
                Session.Nav.SetAltitude(num, Math.Clamp(ft, -1500, 80000) / HornetNav.FeetPerMeter, AltTypeBox.SelectedIndex == 1 ? HornetNav.Radar : HornetNav.Baro);
        Session.Notify();
    }

    private void Route_Click(object sender, RoutedEventArgs e)
    {
        if (Session.SelectedSet.Count == 0 || sender is not CheckBox box) return;
        var route = int.Parse((string)box.Tag, Inv);
        // Ticked: the selection joins the route (in the list's order); unticked: it leaves it.
        if (box.IsChecked == true)
        {
            var full = Session.SelectedSet.Order().Where(n => !Session.Nav.Route(route).Contains(n)).Count(n => !Session.Nav.AddToRoute(route, n));
            if (full > 0)
                MessageBox.Show(Window.GetWindow(this), $"Route {route} holds {HornetNav.MaxRoutePoints} waypoints: {full} did not fit.", "Route", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            foreach (var n in Session.SelectedSet) Session.Nav.RemoveFromRoute(route, n);
        }
        Session.Notify();
    }

    // ---- Routes ---------------------------------------------------------------------------------

    private void RouteTab_Checked(object sender, RoutedEventArgs e)
    {
        if (IsLoaded) ShowRoute();
    }

    private void RouteList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowLeg();

    private void RouteUp_Click(object sender, RoutedEventArgs e) => MoveInRoute(-1);
    private void RouteDown_Click(object sender, RoutedEventArgs e) => MoveInRoute(1);

    private void MoveInRoute(int by)
    {
        if ((RouteList.SelectedItem as PointRow)?.Num is not { } n) return;
        var order = Session.Nav.Route(CurrentRoute).ToList();
        var i = order.IndexOf(n);
        var j = i + by;
        if (i < 0 || j < 0 || j >= order.Count) return;
        (order[i], order[j]) = (order[j], order[i]);
        Session.Nav.Reorder(CurrentRoute, order);
        Session.Notify();
    }

    private void Speed_LostFocus(object sender, RoutedEventArgs e)
    {
        if ((RouteList.SelectedItem as PointRow)?.Num is not { } n) return;
        if (!double.TryParse(SpeedBox.Text, NumberStyles.Float, Inv, out var kts)) return;
        Session.Nav.SetSpeed(CurrentRoute, n, kts * 1.852);
        Session.Notify();
    }

    // ---- NAV settings ---------------------------------------------------------------------------

    private void Settings_LostFocus(object sender, RoutedEventArgs e) => ApplySettings();
    private void Settings_Click(object sender, RoutedEventArgs e) => ApplySettings();

    private void Settings_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && IsLoaded) ApplySettings();
    }

    private void ApplySettings()
    {
        if (_loading) return;
        var nav = Session.Nav;
        var s = nav.Settings;
        if (int.TryParse(TacanChannel.Text, out var tch))
            nav.SetTacan(Math.Clamp(tch, 1, 126), TacanBand.SelectedIndex == 1 ? 'Y' : 'X', TacanMode.SelectedIndex + 1, TacanOn.IsChecked == true);
        if (int.TryParse(IclsChannel.Text, out var ich)) nav.SetIcls(Math.Clamp(ich, 1, 20), IclsOn.IsChecked == true);
        if (double.TryParse(AclsFreq.Text, NumberStyles.Float, Inv, out var mhz))
            s["ACLS"] = new JsonObject { ["Frequency"] = Math.Clamp(mhz, 225, 399.975), ["OnOff"] = AclsOn.IsChecked == true };
        if (int.TryParse(HomeWp.Text, out var home)) s["Home_Waypoint"] = new JsonObject { ["FPAS_HOME_WP"] = Math.Clamp(home, 1, HornetNav.MaxWaypoints) };
        if (int.TryParse(WarnBaro.Text, out var baro) && int.TryParse(WarnRadar.Text, out var radar))
            s["Altitude_Warning"] = new JsonObject { ["Warn_Alt_Baro"] = Math.Clamp(baro, 0, 25000), ["Warn_Alt_Rdr"] = Math.Clamp(radar, 0, 5000) };
        Session.Notify();
    }

    // ---- What a right-click on the map places: the open tab --------------------------------------

    private void EditorTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, EditorTabs)) return;  // lists and boxes inside bubble up here
        var header = (EditorTabs.SelectedItem as TabItem)?.Header as string;
        Session.SetPlace(header switch { "GPS" => DtcPlace.Gps, "SA" => DtcPlace.Sa, _ => DtcPlace.Waypoints });
        ShowPointTools(AppState.Store.Theatre);
    }

    // ---- COMM 1 / 2 -----------------------------------------------------------------------------

    private int CommRadio => Comm2.IsChecked == true ? 2 : 1;
    private readonly List<(string Preset, TextBox Freq, ComboBox Mod, TextBox Name)> _commRows = [];

    /// <summary>One row per preset (1–20, C, G, M, S), made once so typing is never interrupted.</summary>
    private void BuildCommRows()
    {
        foreach (var preset in HornetComm.Presets)
        {
            var row = CommGrid.RowDefinitions.Count;
            CommGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = preset, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
            var freq = new TextBox { Margin = new Thickness(0, 1, 4, 1) };
            var mod = new ComboBox { Margin = new Thickness(0, 1, 4, 1), ItemsSource = new[] { "AM", "FM" } };
            var name = new TextBox { Margin = new Thickness(0, 1, 0, 1), MaxLength = 8 };
            Grid.SetRow(label, row); Grid.SetRow(freq, row); Grid.SetRow(mod, row); Grid.SetRow(name, row);
            Grid.SetColumn(freq, 1); Grid.SetColumn(mod, 2); Grid.SetColumn(name, 3);
            CommGrid.Children.Add(label); CommGrid.Children.Add(freq); CommGrid.Children.Add(mod); CommGrid.Children.Add(name);
            var p = preset;
            freq.LostFocus += (_, _) => ApplyComm(p);
            name.LostFocus += (_, _) => ApplyComm(p);
            mod.SelectionChanged += (_, _) => { if (!_loading) ApplyComm(p); };
            _commRows.Add((preset, freq, mod, name));
        }
    }

    private void ShowComm()
    {
        var comm = Session.Comm;
        GuardBox.IsChecked = comm.Guard(CommRadio);
        foreach (var (preset, freq, mod, name) in _commRows)
        {
            var p = comm.Preset(CommRadio, preset);
            if (!freq.IsKeyboardFocused) freq.Text = p is null ? "" : (HornetNav.Number(p["frequency"]) ?? 0).ToString("0.000", Inv);
            mod.SelectedIndex = p is null ? -1 : (int)(HornetNav.Number(p["modulation"]) ?? 0) == HornetComm.Fm ? 1 : 0;
            if (!name.IsKeyboardFocused) name.Text = (string?)p?["name"] ?? "";
        }
    }

    private void ApplyComm(string preset)
    {
        if (_loading) return;
        var (_, freq, mod, name) = _commRows.First(r => r.Preset == preset);
        if (!double.TryParse(freq.Text, NumberStyles.Float, Inv, out var mhz)) return;
        Session.Comm.Set(CommRadio, preset, mhz, mod.SelectedIndex == 1 ? HornetComm.Fm : HornetComm.Am, name.Text.Trim().ToUpperInvariant());
        Session.Notify();
    }

    private void CommRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _loading = true;
        ShowComm();
        _loading = false;
    }

    private void Guard_Click(object sender, RoutedEventArgs e)
    {
        Session.Comm.SetGuard(CommRadio, GuardBox.IsChecked == true);
        Session.Notify();
    }

    /// <summary>Presets 1… of the shown radio from the side's AWACS, tankers and towers (dtc_live).</summary>
    private void FillComm_Click(object sender, RoutedEventArgs e)
    {
        if (AppState.Store.DtcLive?["comms"] is not JsonArray comms || comms.Count == 0)
        {
            MessageBox.Show(Window.GetWindow(this), "No frequencies from the server yet (connected to a server with NEBO 0.13 or later?).",
                "Fill from mission", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var i = 0;
        foreach (var c in comms.OfType<JsonObject>().Take(20))
            Session.Comm.Set(CommRadio, (++i).ToString(Inv), HornetNav.Number(c["mhz"]) ?? 0, (int)(HornetNav.Number(c["mod"]) ?? 0),
                ((string?)c["name"] ?? "").ToUpperInvariant());
        Session.Notify();
    }

    // ---- GPS waypoints --------------------------------------------------------------------------

    private void ShowGps(Theatre? theatre)
    {
        var gps = Session.Gps;
        GpsList.ItemsSource = gps.All.Select(g =>
        {
            var (lat, lon) = theatre?.ToLatLon(HornetNav.Number(g["x"]) ?? 0, HornetNav.Number(g["y"]) ?? 0) ?? (0, 0);
            var ft = (HornetNav.Number(g["alt"]) ?? 0) * HornetNav.FeetPerMeter;
            return new PointRow(HornetNav.Num(g), $"{HornetNav.Num(g),3} {(string?)g["text"],-5} {(theatre is null ? "" : Coordinates.Ddm(lat, lon))} {ft,6:0} ft {(string?)g["note"]}");
        }).ToList();
        GpsList.SelectedItems.Clear();
        foreach (var row in ((IEnumerable<PointRow>)GpsList.ItemsSource).Where(r => Session.SelectedGpsSet.Contains(r.Num)))
            GpsList.SelectedItems.Add(row);
        var sel = Session.SelectedGpsSet.Count == 1 && Session.SelectedGps is { } n ? gps.Point(n) : null;
        GpsEditor.IsEnabled = sel is not null;
        if (!GpsText.IsKeyboardFocused) GpsText.Text = (string?)sel?["text"] ?? "";
        if (!GpsNote.IsKeyboardFocused) GpsNote.Text = (string?)sel?["note"] ?? "";
        if (!GpsAlt.IsKeyboardFocused) GpsAlt.Text = sel is null ? "" : ((HornetNav.Number(sel["alt"]) ?? 0) * HornetNav.FeetPerMeter).ToString("0", Inv);
    }

    private void GpsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var picked = GpsList.SelectedItems.OfType<PointRow>().Select(r => r.Num).ToList();
        if ((e.AddedItems.Count > 0 ? e.AddedItems[^1] : null) is PointRow last && picked.Remove(last.Num)) picked.Add(last.Num);
        Session.SelectGpsMany(picked);
    }

    private void Gps_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading || Session.SelectedGps is not { } n) return;
        if (!double.TryParse(GpsAlt.Text, NumberStyles.Float, Inv, out var ft)) ft = 0;
        Session.Gps.Set(n, GpsText.Text.Trim().ToUpperInvariant(), GpsNote.Text.Trim(), ft);
        Session.Notify();
    }

    private void GpsDelete_Click(object sender, RoutedEventArgs e) => Session.DeleteSelected();

    // ---- SA page -----------------------------------------------------------------------------------

    private sealed record RingType(JsonObject Def)
    {
        public string Label => $"{(string?)Def["name"]}  ({(HornetNav.Number(Def["detectionRange"]) ?? 0) / 1852:0} NM)";
        public override string ToString() => Label;  // the combo box's closed face uses this, not DisplayMemberPath
    }

    private sealed record SaRow(SaRef Ref, string Text)
    {
        public override string ToString() => Text;
    }

    private static IReadOnlyList<RingType> RingTypes { get; } =
        DtcDefs.For(Cartridge.HornetType)["MEZ_THRTS_defs/MEZ_THRTS_defs"]!.AsArray().OfType<JsonObject>().Select(d => new RingType(d)).ToList();

    private SaKind SelectedSaKind => SaCap.IsChecked == true ? SaKind.Cap : SaCorridor.IsChecked == true ? SaKind.Corridor
        : SaFaor.IsChecked == true ? SaKind.Faor : SaFlot.IsChecked == true ? SaKind.Flot : SaKind.Threat;

    private sealed record LineChoice(int? Index, string Text)
    {
        public override string ToString() => Text;
    }

    /// <summary>The SA tab shows one type at a time (the one right-clicks place): threat rings,
    /// CAPs, or one line of corridors / FAOR / FLOT — the current line, picked above, with its points.</summary>
    private void ShowSa()
    {
        if (SaRingType.ItemsSource is null)
        {
            SaRingType.ItemsSource = RingTypes;
            SaEditType.ItemsSource = RingTypes;
            SaRingType.SelectedItem = RingTypes.FirstOrDefault(r => ((string?)r.Def["name"] ?? "").StartsWith("SAM SA-11")) ?? RingTypes[0];
            Session.SaRingType = ((RingType)SaRingType.SelectedItem).Def;
        }
        var sa = Session.Sa;
        var theatre = AppState.Store.Theatre;
        var kind = Session.SaPlace;
        (kind switch { SaKind.Cap => SaCap, SaKind.Corridor => SaCorridor, SaKind.Faor => SaFaor, SaKind.Flot => SaFlot, _ => SaThreat }).IsChecked = true;
        string Where(JsonObject o) => theatre is null ? "" : Coordinates.Ddm(theatre.ToLatLon(HornetNav.Number(o["x"]) ?? 0, HornetNav.Number(o["y"]) ?? 0).Lat,
            theatre.ToLatLon(HornetNav.Number(o["x"]) ?? 0, HornetNav.Number(o["y"]) ?? 0).Lon);

        var rows = new List<SaRow>();
        var threats = sa.Threats;
        var caps = sa.Caps;
        var lineKind = SaRef.LineOf(kind);
        SaRingRow.Visibility = kind == SaKind.Threat ? Visibility.Visible : Visibility.Collapsed;
        SaLineRow.Visibility = lineKind is null ? Visibility.Collapsed : Visibility.Visible;
        if (kind == SaKind.Threat)
        {
            for (var i = 0; i < threats.Count; i++)
                rows.Add(new SaRow(new SaRef(SaKind.Threat, i), $"Ring {i + 1,-2} {(string?)threats[i]["text"],-5} {HornetNav.Number(threats[i]["threat_ring_radius"]),5:0.0} NM  {Where(threats[i])}  {(string?)threats[i]["threat_type"]}"));
            SaHint.Text = $"Right-click the map: a ring of the type above ({threats.Count} of {HornetSa.MaxThreats}).";
        }
        else if (kind == SaKind.Cap)
        {
            for (var i = 0; i < caps.Count; i++)
                rows.Add(new SaRow(new SaRef(SaKind.Cap, i), $"CAP {i + 1}  {HornetNav.Number(caps[i]["course"]),3:000}°  {(HornetNav.Number(caps[i]["length"]) ?? 0) / 1852:0.0} × {(HornetNav.Number(caps[i]["diameter"]) ?? 0) / 1852:0.0} NM  {(string?)caps[i]["turn_direction"],-5} {Where(caps[i])}  {(string?)caps[i]["note"]}"));
            SaHint.Text = $"Right-click the map: a CAP anchored there ({caps.Count} of {HornetSa.MaxCaps}).";
        }
        else if (lineKind is not null)
        {
            var lines = sa.LinesOf(lineKind);
            var choices = lines.Select((l, i) => new LineChoice(i, $"{lineKind.Prefix} {i + 1}  ({HornetSa.PointsOf(l).Count} of {lineKind.MaxPoints} points)  {(string?)l["note"]}"))
                .Append(new LineChoice(null, lines.Count < lineKind.Max ? "New line (next right-click starts it)" : $"New line — all {lineKind.Max} used"))
                .ToList();
            SaLineBox.ItemsSource = choices;
            SaLineBox.SelectedItem = choices.First(c => c.Index == Session.SaLine);
            if (Session.SaLine is { } cur && lines.ElementAtOrDefault(cur) is { } line)
            {
                var pts = HornetSa.PointsOf(line);
                for (var j = 0; j < pts.Count; j++) rows.Add(new SaRow(new SaRef(kind, cur, j), $"Point {j + 1,-2} {Where(pts[j])}"));
                SaHint.Text = "Right-click the map: a point after the selected one (or at the end). Ctrl/Shift picks several.";
            }
            else SaHint.Text = lines.Count < lineKind.Max
                ? $"Right-click the map: the first point of {lineKind.Prefix} {lines.Count + 1}; the next ones follow it."
                : $"All {lineKind.Max} lines are used: pick one to add to, or delete one.";
        }
        SaList.ItemsSource = rows;
        SaList.SelectedItems.Clear();
        foreach (var row in rows.Where(r => Session.SaSelectedSet.Contains(r.Ref))) SaList.SelectedItems.Add(row);

        // The selected object's fields (one at a time; several: delete or drag them).
        var sel = Session.SaSelectedSet.Count == 1 ? Session.SaSelected : null;
        SaEditor.IsEnabled = sel is not null;
        SaThreatFields.Visibility = sel?.Kind == SaKind.Threat ? Visibility.Visible : Visibility.Collapsed;
        SaCapFields.Visibility = sel?.Kind == SaKind.Cap ? Visibility.Visible : Visibility.Collapsed;
        SaNoteFields.Visibility = sel is { Kind: not SaKind.Threat } ? Visibility.Visible : Visibility.Collapsed;
        SaTitle.Text = Session.SaSelectedSet.Count > 1 ? $"{Session.SaSelectedSet.Count} selected: Delete, or drag them on the map."
            : sel is null ? "Pick one in the list or on the map."
            : sel.Value.Line is { } selLine ? $"{selLine.Prefix} {sel.Value.Index + 1}, point {sel.Value.Point + 1} — the note is the line's"
            : SaList.SelectedItem?.ToString() ?? "";
        if (sel is { Kind: SaKind.Threat } st && threats.ElementAtOrDefault(st.Index) is { } t)
        {
            SaEditType.SelectedItem = RingTypes.FirstOrDefault(r => (string?)r.Def["name"] == (string?)t["threat_type"]);
            SaEditText.Text = (string?)t["text"] ?? "";
            SaEditRadius.Text = (HornetNav.Number(t["threat_ring_radius"]) ?? 0).ToString("0.0", Inv);
        }
        else if (sel is { Kind: SaKind.Cap } sc && caps.ElementAtOrDefault(sc.Index) is { } c)
        {
            SaEditCourse.Text = (HornetNav.Number(c["course"]) ?? 0).ToString("0", Inv);
            SaEditLength.Text = ((HornetNav.Number(c["length"]) ?? 0) / 1852).ToString("0.0", Inv);
            SaEditDiameter.Text = ((HornetNav.Number(c["diameter"]) ?? 0) / 1852).ToString("0.0", Inv);
            SaEditTurn.SelectedIndex = (string?)c["turn_direction"] == "Right" ? 1 : 0;
            if (!SaEditNote.IsKeyboardFocused) SaEditNote.Text = (string?)c["note"] ?? "";
        }
        else if (sel is { Line: { } line } sl && sa.LinesOf(line).ElementAtOrDefault(sl.Index) is { } l && !SaEditNote.IsKeyboardFocused)
        {
            SaEditNote.Text = (string?)l["note"] ?? "";
        }
        ShowSaSettings();
    }

    /// <summary>Checkboxes for every on/off setting of the SA page the cartridge has (DCS's own
    /// keys: declutter per MREJ, sensors), made from the cartridge so nothing is left out.</summary>
    private void ShowSaSettings()
    {
        SaSettings.Children.Clear();
        void Flags(JsonObject group, string prefix)
        {
            foreach (var (key, value) in group.ToList())
            {
                if (value is JsonObject sub) Flags(sub, $"{prefix}{key} ");
                else if (value is JsonValue v && v.TryGetValue(out bool on))
                {
                    var box = new CheckBox { Content = $"{prefix}{key.Replace('_', ' ')}", IsChecked = on, Margin = new Thickness(0, 0, 12, 3) };
                    var k = key;
                    box.Click += (_, _) => { group[k] = box.IsChecked == true; Session.Notify(); };
                    SaSettings.Children.Add(box);
                }
            }
        }
        Flags(Session.Sa.Settings, "");
    }

    private void SaKind_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _loading) return;
        Session.SetSaPlace(SelectedSaKind);
    }

    private void SaRingType_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (SaRingType.SelectedItem is RingType r) Session.SaRingType = r.Def;
    }

    private void SaList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var picked = SaList.SelectedItems.OfType<SaRow>().Select(r => r.Ref).ToList();
        if ((e.AddedItems.Count > 0 ? e.AddedItems[^1] : null) is SaRow last && picked.Remove(last.Ref)) picked.Add(last.Ref);
        Session.SelectSaMany(picked);
    }

    private void SaLineBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && SaLineBox.SelectedItem is LineChoice c && c.Index != Session.SaLine) Session.SetSaLine(c.Index);
    }

    private void SaNewLine_Click(object sender, RoutedEventArgs e)
    {
        Session.SelectSa(null);
        Session.SetSaLine(null);
    }

    private void SaDeleteLine_Click(object sender, RoutedEventArgs e)
    {
        if (SaRef.LineOf(Session.SaPlace) is not { } line || Session.SaLine is not { } l) return;
        Session.Sa.RemoveLine(line, l);
        Session.SelectSa(null);
        Session.SetSaLine(Session.Sa.LinesOf(line).Count > 0 ? Math.Min(l, Session.Sa.LinesOf(line).Count - 1) : null);
    }

    private void SaDelete_Click(object sender, RoutedEventArgs e) => Session.DeleteSelected();

    private void SaApply_Click(object sender, RoutedEventArgs e)
    {
        if (Session.SaSelected is not { } sel) return;
        if (sel.Kind == SaKind.Threat && SaEditType.SelectedItem is RingType type
            && double.TryParse(SaEditRadius.Text, NumberStyles.Float, Inv, out var nm))
        {
            // A new type brings its own ring radius unless the radius was changed by hand too.
            var old = Session.Sa.Threats[sel.Index];
            var typeChanged = (string?)old["threat_type"] != (string?)type.Def["name"];
            var radius = typeChanged && Math.Abs(nm - (HornetNav.Number(old["threat_ring_radius"]) ?? 0)) < 0.05
                ? (HornetNav.Number(type.Def["detectionRange"]) ?? 0) / 1852 : nm;
            var text = typeChanged && SaEditText.Text == (string?)old["text"] ? (string?)type.Def["default_text"] ?? "" : SaEditText.Text.Trim().ToUpperInvariant();
            Session.Sa.SetThreat(sel.Index, type.Def, text, radius);
        }
        else if (sel.Kind == SaKind.Cap
                 && double.TryParse(SaEditCourse.Text, NumberStyles.Float, Inv, out var course)
                 && double.TryParse(SaEditLength.Text, NumberStyles.Float, Inv, out var len)
                 && double.TryParse(SaEditDiameter.Text, NumberStyles.Float, Inv, out var dia))
        {
            Session.Sa.SetCap(sel.Index, course, len, dia, SaEditTurn.SelectedIndex != 1, SaEditNote.Text.Trim());
        }
        Session.Notify();
    }

    private void SaNote_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading || Session.SaSelected is not { } sel) return;
        if (sel.Line is { } line) Session.Sa.SetLineNote(line, sel.Index, SaEditNote.Text.Trim());
        else if (sel.Kind == SaKind.Cap && Session.Sa.Caps.ElementAtOrDefault(sel.Index) is { } c) c["note"] = SaEditNote.Text.Trim();
        Session.Notify();
    }

    /// <summary>Threat rings for the threats the side knows (dtc_live: identified air defence and
    /// ships, controllers' identities and shared threats): one per ring type within 5 km, the
    /// largest rings first, up to the cartridge's 40.</summary>
    private void SaFromPicture_Click(object sender, RoutedEventArgs e)
    {
        if (AppState.Store.DtcLive?["threats"] is not JsonArray known || known.Count == 0)
        {
            MessageBox.Show(Window.GetWindow(this), "The server knows no threats for your side yet (identify one, or place one with right-click → threat on the GCI map).",
                "From picture", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var types = RingTypes.Select(r => r.Def).ToList();
        var have = Session.Sa.Threats.Select(t => ((string?)t["threat_type"], HornetNav.Number(t["x"]) ?? 0, HornetNav.Number(t["y"]) ?? 0)).ToList();
        var found = known.OfType<JsonObject>()
            .Select(t => (Def: HornetSa.RingTypeFor(types, (string?)t["type"] ?? "") ?? HornetSa.RingTypeFor(types, (string?)t["name"] ?? ""),
                X: HornetNav.Number(t["x"]) ?? 0, Z: HornetNav.Number(t["z"]) ?? 0))
            .Where(t => t.Def is not null)
            .OrderByDescending(t => HornetNav.Number(t.Def!["detectionRange"]) ?? 0)
            .ToList();
        var added = 0;
        foreach (var (def, x, z) in found)
        {
            var name = (string?)def!["name"];
            if (have.Any(h => h.Item1 == name && (h.Item2 - x) * (h.Item2 - x) + (h.Item3 - z) * (h.Item3 - z) < 5000.0 * 5000)) continue;
            if (Session.Sa.AddThreat(x, z, def) is null) break;
            have.Add((name, x, z));
            added++;
        }
        Session.Notify();
        SaTitle.Text = $"{added} threat ring(s) added from the picture ({found.Count} known threats with a ring type).";
    }

    // ---- HARM, CMDS, RWR --------------------------------------------------------------------------

    private static DtcDefs Defs => DtcDefs.For(Cartridge.HornetType);
    private (Cartridge?, int) _ewFor;  // the cartridge and EW revision the grids show (rebuilt only for another one)

    public sealed record Choice(int Id, string Text)
    {
        public override string ToString() => Text;
    }

    /// <summary>A HARM TOO-class row: edits go straight into the cartridge.</summary>
    public sealed class TooRow(HornetEw ew, string radar, int cls, IReadOnlyList<Choice> choices)
    {
        private Choice? _class = choices.FirstOrDefault(c => c.Id == cls);
        public string Radar { get; } = radar;
        public IReadOnlyList<Choice> Choices { get; } = choices;
        public Choice? Class
        {
            get => _class;
            set { _class = value; if (value is not null) ew.SetTooClass(Radar, value.Id); }
        }
    }

    /// <summary>A CMDS threat row: its AUTO program and threshold.</summary>
    public sealed class CmdsRow
    {
        private static readonly string[] Names = ["NONE", "AUTO 1", "AUTO 2", "AUTO 3"];
        private readonly HornetEw _ew;
        private string _program, _threshold;
        public string Category { get; }
        public string Group { get; }
        public IReadOnlyList<string> Programs => Names;
        public IReadOnlyList<string> Thresholds { get; }
        private readonly IReadOnlyList<(int Index, string Label)> _offered;

        public CmdsRow(HornetEw ew, string category, string group, JsonObject row)
        {
            (_ew, Category, Group) = (ew, category, group);
            _offered = (row["thresholds"] as JsonArray)?.OfType<JsonObject>().Select(t => ((int)(HornetNav.Number(t["index"]) ?? 1), (string?)t["label"] ?? "")).ToList() ?? [];
            Thresholds = _offered.Select(t => t.Label).ToList();
            _program = Names[Math.Max(0, Array.IndexOf(HornetEw.AutoPrograms, (int)(HornetNav.Number(row["program"]) ?? 0)))];
            _threshold = (string?)row["default_threshold"]?["label"] ?? Thresholds.FirstOrDefault() ?? "";
        }

        public string Program { get => _program; set { _program = value; Save(); } }
        public string Threshold { get => _threshold; set { _threshold = value; Save(); } }

        private void Save() => _ew.SetCmdsThreat(Category, Group, HornetEw.AutoPrograms[Math.Max(0, Array.IndexOf(Names, _program))],
            _offered.FirstOrDefault(t => t.Label == _threshold).Index);
    }

    /// <summary>An RWR row of one table: priority, display, friend, jammer.</summary>
    public sealed class RwrRow(HornetEw ew, string table, string group, JsonObject row)
    {
        private int _priority = (int)(HornetNav.Number(row["PRI"]) ?? 1);
        private bool _display = row["display"]?.GetValue<bool>() == true, _friend = row["friend"]?.GetValue<bool>() == true,
            _jammer = row["aspj_xmit"]?.GetValue<bool>() == true;
        public string Group { get; } = group;
        public int Priority { get => _priority; set { _priority = Math.Max(1, value); Save(); } }
        public bool Display { get => _display; set { _display = value; Save(); } }
        public bool Friend { get => _friend; set { _friend = value; Save(); } }
        public bool Jammer { get => _jammer; set { _jammer = value; Save(); } }
        private void Save() => ew.SetRwr(table, Group, _priority, _display, _friend, _jammer);
    }

    private static IReadOnlyList<Choice> Items(string path) =>
        Defs[path]?.AsArray().OfType<JsonObject>().Select(i => new Choice((int)(HornetNav.Number(i["id"]) ?? 0), (string?)i["text"] ?? "")).ToList() ?? [];

    private static IReadOnlyList<Choice> Enum(string path) =>
        (Defs[path] as JsonObject ?? []).Select(kv => new Choice((int)(HornetNav.Number(kv.Value) ?? 0), kv.Key)).OrderBy(c => c.Id).ToList();

    private void ShowEw()
    {
        var ew = Session.Ew;
        if (HarmMode.ItemsSource is null)
        {
            HarmMode.ItemsSource = Enum("HARM_SETTINGS_defs/HARM_Default_Mode");
            HarmPullup.ItemsSource = Enum("HARM_SETTINGS_defs/HARM_PULLUP_Cue");
            HarmClass.ItemsSource = Items("HARM_SETTINGS_defs/HARM_Threat_Class_Items");
            CmdsProgram.ItemsSource = HornetEw.Programs.Select(p => p.Replace('_', ' ')).ToList();
            CmdsDelay.ItemsSource = Enumerable.Range(0, 11).Select(s => s == 0 ? "No delay" : $"{s} s").ToList();
            RwrTable.ItemsSource = HornetEw.RwrTables;
            RwrTable.SelectedIndex = 0;
            CmdsProgram.SelectedIndex = 0;
        }
        var h = ew.HarmSettings;
        int Id(string key) => (int)(HornetNav.Number(h[key]) ?? 1);
        HarmMode.SelectedItem = ((IEnumerable<Choice>)HarmMode.ItemsSource).FirstOrDefault(c => c.Id == Id("Default_Mode"));
        HarmPullup.SelectedItem = ((IEnumerable<Choice>)HarmPullup.ItemsSource).FirstOrDefault(c => c.Id == Id("PULLUP_Cue"));
        HarmClass.SelectedItem = ((IEnumerable<Choice>)HarmClass.ItemsSource).FirstOrDefault(c => c.Id == Id("Threat_Class"));
        HarmLimit.IsChecked = h["LIMIT"]?.GetValue<bool>() == true;
        HarmPullback.IsChecked = h["Pullback"]?.GetValue<bool>() == true;
        if (!HarmCode.IsKeyboardFocused) HarmCode.Text = (string?)h["Target_Code"] ?? "";
        CmdsDelay.SelectedIndex = ew.Delay;
        ShowProgram();
        HarmFriendlyStatus.Text = RwrFriendlyStatus.Text = Session.FriendlyNote ?? "";
        if (ReferenceEquals(_ewFor.Item1, Session.Cartridge) && _ewFor.Item2 == Session.EwRevision) return;
        // Another cartridge: the tables again (not on every change, so editing a cell is never interrupted).
        _ewFor = (Session.Cartridge, Session.EwRevision);
        var tooChoices = Items("HARM_SETTINGS_defs/HARM_TOO_Threat_Class_Items");
        TooGrid.ItemsSource = ew.TooClasses.Select(t => new TooRow(ew, t.Radar, t.Class, tooChoices)).ToList();
        CmdsGrid.ItemsSource = ew.CmdsThreats.Select(t => new CmdsRow(ew, t.Category, t.Group, t.Row)).ToList();
        ShowRwr();
    }

    private void ShowProgram()
    {
        if (CmdsProgram.SelectedIndex < 0) return;
        var p = Session.Ew.Program(HornetEw.Programs[CmdsProgram.SelectedIndex]);
        string Q(string kind, string key = "Quantity") => (HornetNav.Number(p[kind]?[key]) ?? 0).ToString(Inv);
        ChaffQty.Text = Q("Chaff");
        ChaffRepeat.Text = Q("Chaff", "Repeat");
        ChaffInterval.Text = (HornetNav.Number(p["Chaff"]?["Interval"]) ?? 1).ToString("0.00", Inv);
        FlareQty.Text = Q("Flare");
        Other1Qty.Text = Q("Other1");
        Other2Qty.Text = Q("Other2");
    }

    private void ShowRwr()
    {
        if (RwrTable.SelectedItem is not string table) return;
        RwrGrid.ItemsSource = Session.Ew.Rwr(table).Select(r => new RwrRow(Session.Ew, table, r.Group, r.Row)).ToList();
    }

    private void Harm_Changed(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;  // not the editor's tab change
        if (!_loading) ApplyHarm();
    }

    private void Harm_Click(object sender, RoutedEventArgs e) => ApplyHarm();
    private void HarmCode_LostFocus(object sender, RoutedEventArgs e) => ApplyHarm();

    private void ApplyHarm()
    {
        if (_loading) return;
        var h = Session.Ew.HarmSettings;
        if (HarmMode.SelectedItem is Choice m) h["Default_Mode"] = m.Id;
        if (HarmPullup.SelectedItem is Choice p) h["PULLUP_Cue"] = p.Id;
        if (HarmClass.SelectedItem is Choice c) h["Threat_Class"] = c.Id;
        h["LIMIT"] = HarmLimit.IsChecked == true;
        h["Pullback"] = HarmPullback.IsChecked == true;
        h["Target_Code"] = HarmCode.Text.Trim();
    }

    private void CmdsProgram_Changed(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;
        ShowProgram();
    }

    private void CmdsApply_Click(object sender, RoutedEventArgs e)
    {
        if (CmdsProgram.SelectedIndex < 0) return;
        static int I(TextBox b) => int.TryParse(b.Text, out var v) ? v : 0;
        var interval = double.TryParse(ChaffInterval.Text, NumberStyles.Float, Inv, out var s) ? s : 1;
        Session.Ew.SetProgram(HornetEw.Programs[CmdsProgram.SelectedIndex], I(ChaffQty), I(ChaffRepeat), interval, I(FlareQty), I(Other1Qty), I(Other2Qty));
        ShowProgram();  // as stored: clamped, interval to 0.25 s
    }

    private void CmdsDelay_Changed(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;
        if (!_loading && CmdsDelay.SelectedIndex >= 0) Session.Ew.Delay = CmdsDelay.SelectedIndex;
    }

    private void Friendly_Click(object sender, RoutedEventArgs e) =>
        HarmFriendlyStatus.Text = RwrFriendlyStatus.Text = Session.MarkFriendly();

    private void RwrTable_Changed(object sender, SelectionChangedEventArgs e)
    {
        e.Handled = true;
        if (IsLoaded) ShowRwr();
    }

    // ---- TACAN stations -------------------------------------------------------------------------

    private sealed record TacanRow(Beacon Beacon)
    {
        public override string ToString() => $"{Beacon.Name} — {Beacon.ChannelText} {Beacon.Callsign}";
    }

    private void ShowTacan()
    {
        TacanList.ItemsSource = Session.Tacan.Stations.Select(s =>
            $"{(string?)s["display_name"],-22} {HornetNav.Number(s["channel"]):0}{(string?)s["modeChannel"]} {(string?)s["callsign"]}").ToList();
    }

    private void TacanPick_DropDownOpened(object? sender, EventArgs e)
    {
        if (AppState.Store.Theatre is not { } theatre) return;
        TacanPick.ItemsSource = DcsAirfieldData.TacansOf(theatre.Id).Where(b => !Session.Tacan.Has(b.Name)).Select(b => new TacanRow(b)).ToList();
    }

    private void TacanAdd_Click(object sender, RoutedEventArgs e)
    {
        if (TacanPick.SelectedItem is not TacanRow row) return;
        if (!Session.Tacan.Add(row.Beacon))
            MessageBox.Show(Window.GetWindow(this), $"The cartridge holds {HornetTacan.MaxStations} TACAN stations.", "TACAN", MessageBoxButton.OK, MessageBoxImage.Information);
        TacanPick.SelectedItem = null;
        Session.Notify();
    }

    private void TacanUp_Click(object sender, RoutedEventArgs e)
    {
        Session.Tacan.MoveUp(TacanList.SelectedIndex);
        Session.Notify();
    }

    private void TacanRemove_Click(object sender, RoutedEventArgs e)
    {
        Session.Tacan.RemoveAt(TacanList.SelectedIndex);
        Session.Notify();
    }

    // ---- Recipients and push --------------------------------------------------------------------

    private sealed record PilotRow(PilotInfo Pilot)
    {
        public override string ToString()
        {
            var where = Pilot.Slot.Length == 0 ? "choosing a spawn" : Pilot.Type.Length > 0 ? Pilot.Type : "in a slot";
            var warn = Pilot.Type.Length > 0 && !DtcSession.Aircraft.Any(a => a.Type == Pilot.Type) ? "  (no cartridge for this aircraft)" : "";
            return $"{Pilot.Player}  ·  {where}{warn}";
        }
    }

    private void RefreshPilots()
    {
        var selected = PilotList.SelectedItems.OfType<PilotRow>().Select(r => r.Pilot.Id).ToHashSet();
        var rows = AppState.Store.Pilots.OrderBy(p => p.Player).Select(p => new PilotRow(p)).ToList();
        PilotList.ItemsSource = rows;
        foreach (var r in rows.Where(r => selected.Contains(r.Pilot.Id))) PilotList.SelectedItems.Add(r);

        PackageBoxes.Children.Clear();
        foreach (var pkg in AppState.Packages.All)
        {
            var members = PilotsOf(pkg).Count;
            PackageBoxes.Children.Add(new CheckBox
            {
                Content = $"P{pkg.Number} {pkg.Name} ({members})", Tag = pkg, Margin = new Thickness(0, 0, 10, 2),
                IsEnabled = members > 0, ToolTip = "Everyone in this package who is in a slot",
            });
        }
    }

    /// <summary>The pilots in a package's aircraft (members are unit ids, a pilot's slot is its unit).</summary>
    private static List<PilotInfo> PilotsOf(Package pkg) =>
        AppState.Store.Pilots.Where(p => long.TryParse(p.Slot.Split('_')[0], out var unit) && pkg.Members.Contains(unit)).ToList();

    private async void Push_Click(object sender, RoutedEventArgs e)
    {
        var to = PilotList.SelectedItems.OfType<PilotRow>().Select(r => r.Pilot).ToList();
        foreach (var box in PackageBoxes.Children.OfType<CheckBox>().Where(b => b.IsChecked == true))
            to.AddRange(PilotsOf((Package)box.Tag));
        var ids = to.Select(p => p.Id.ToString(Inv)).Distinct().ToList();
        if (ids.Count == 0)
        {
            PushStatus.Text = "Pick pilots or a package first.";
            return;
        }
        if (!AppState.Connection.IsConnected)
        {
            PushStatus.Text = "Not connected to a server.";
            return;
        }
        // Each pilot the cartridge of their jet; one still choosing a spawn every jet's (the server keeps
        // one a jet). The open jet's goes last: a server that keeps one a pilot keeps that one.
        var groups = new Dictionary<string, List<string>>();
        var skipped = new List<string>();
        foreach (var pilot in to.DistinctBy(p => p.Id))
        {
            var types = pilot.Type.Length == 0 ? DtcSession.Aircraft.Select(a => a.Type).ToList()
                : DtcSession.Aircraft.Any(a => a.Type == pilot.Type) ? [pilot.Type] : [];
            if (types.Count == 0) skipped.Add($"{pilot.Player} ({pilot.Type})");
            foreach (var type in types)
            {
                if (!groups.TryGetValue(type, out var list)) groups[type] = list = [];
                list.Add(pilot.Id.ToString(Inv));
            }
        }
        if (groups.Count == 0)
        {
            PushStatus.Text = $"No cartridge for {string.Join(", ", skipped)}.";
            return;
        }
        PushButton.IsEnabled = false;
        PushStatus.Text = $"Pushing to {ids.Count} pilot(s)…";
        var lines = new List<string>();
        foreach (var (type, units) in groups.OrderBy(g => g.Key == Session.ActiveType))
        {
            // Recipients before the cartridge in the line: the server reads them first.
            var result = await AppState.Connection.SendCommandAsync("dtc_push", new Dictionary<string, object>
            {
                ["units"] = units,
                ["cartridge"] = Session.Prepare(type).Root,
            });
            lines.Add(result.Ok ? $"✓ {DtcSession.NameOf(type)}: {result.Value}" : $"{DtcSession.NameOf(type)} not sent: {result.Error}");
        }
        if (skipped.Count > 0) lines.Add($"No cartridge for {string.Join(", ", skipped)}.");
        PushButton.IsEnabled = true;
        PushStatus.Text = string.Join("\n", lines);
    }
}
