using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AvalonDock.Layout;
using Nebo.Client.Panels;

namespace Nebo.Client.Dev;

/// <summary>Development aid, off unless started with --snapshot: connect, open a second map floated in its own
/// window, wait, save PNGs of every window, exit. Lets the UI be checked without a person.
/// usage: Nebo.Client.exe --snapshot &lt;dir&gt; &lt;host&gt; &lt;port&gt; &lt;password&gt; [seconds] [basemap|restore] [lat,lon,res[,coast]]
/// The basemap argument picks the floated map's base map, or "restore" to open nothing and
/// show the saved layout. The view argument centres the floated map; ",coast" adds the
/// Natural Earth coastline on top to check alignment. Layout and settings are saved on exit only when NEBO_HOME is set,
/// so a snapshot never touches the real profile.</summary>
public static class DebugSnapshot
{
    public static bool Active { get; private set; }
    public static bool Persist => AppPaths.CustomHome;

    /// <summary>"…+q" modes: for a minute, every 2 s, queue whatever the runway queue suggests.</summary>
    private static async Task QueueSuggested(AtcPanel atc)
    {
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            atc.Queue.AddAllSuggested();
        }
    }

    public static void TryStart(MainWindow window, string[] args)
    {
        if (args.Length < 5 || args[0] != "--snapshot") return;
        Active = true;
        var dir = args[1];
        var seconds = args.Length > 5 ? int.Parse(args[5]) : 10;
        var mode = args.Length > 6 ? args[6] : nameof(BaseMap.Scope);
        List<string> zoomReport = [];
        window.Loaded += async (_, _) =>
        {
            // The side: NEBO_SIDE, else the first the server offers.
            _ = AppState.Connection.ConnectAsync(args[2], int.Parse(args[3]), new Core.LoginOptions(() => args[4],
                (sides, _) => Task.FromResult(Environment.GetEnvironmentVariable("NEBO_SIDE") is { Length: > 0 } side ? side : sides.FirstOrDefault())));
            await Task.Delay(TimeSpan.FromSeconds(3));

            if (mode.StartsWith("atis:"))
            {
                // Open the ATIS send window for an airfield, and send one message_unit to the
                // first own aircraft through the client's command path: "atis:<airfield>".
                await Task.Delay(TimeSpan.FromSeconds(65));  // the weather comes once a minute
                if (AppState.Store.Airfields.FirstOrDefault(a => a.Name.Equals(mode[5..], StringComparison.OrdinalIgnoreCase)) is { } field)
                {
                    new SendMessageWindow(field, null) { Owner = window }.Show();
                    var own = AppState.Store.Tracks.Select(t => t.Unit).FirstOrDefault(u => !u.Detected && u.Category == "airplane");
                    if (own is not null)
                    {
                        var r = await AppState.Connection.SendCommandAsync("message_unit", new Dictionary<string, object>
                            { ["unit"] = own.Id, ["text"] = "NEBO snapshot test", ["seconds"] = 5 });
                        zoomReport.Add($"atis: command to {own.Name}: ok={r.Ok} error={r.Error}");
                    }
                    zoomReport.Add($"atis: weather {(AppState.Store.Weather is null ? "missing" : "present")}");
                }
            }
            else if (mode.StartsWith("srstab"))
            {
                // The SRS tab against an SRS server: "srstab[:<SRS user to listen to>]"; the EAM
                // password comes from NEBO_SNAPSHOT_EAM. Channels 1, 2, 5 and package 1 are set.
                var listen = Srs.SrsListen.Instance;
                listen.SessionPassword = Environment.GetEnvironmentVariable("NEBO_SNAPSHOT_EAM");
                listen.Reconnect();
                await Task.Delay(TimeSpan.FromSeconds(3));
                Srs.SrsChannels.Set(1, 251.0);
                Srs.SrsChannels.Set(2, 124.5);
                Srs.SrsChannels.Set(Core.ChannelPlan.Atc, 250.6);
                Srs.SrsChannels.Set(Core.ChannelPlan.ForPackage(1), 262.5);
                Srs.SrsChannels.Transmit(1);
                if (mode.Split(':') is [_, var who])
                {
                    Panels.SrsPanel.DebugListenName = who;
                    AppState.ListenTo(AppState.Store.Tracks.FirstOrDefault(t => !t.Unit.Detected && t.Unit.Category == "airplane")?.Unit.Id);
                }
                window.ShowSrs();
                await Task.Delay(TimeSpan.FromSeconds(2));
                zoomReport.Add($"srstab: {listen.Status} | roster {listen.Roster.Own.Count()} own users");
            }
            else if (mode.StartsWith("views:"))
            {
                // The Diagram and Approach views as panels beside each other, for "views:<airfield>".
                await Task.Delay(TimeSpan.FromSeconds(2));
                if (AppState.Store.Airfields.FirstOrDefault(a => a.Name.Equals(mode[6..], StringComparison.OrdinalIgnoreCase)) is { } field)
                    AppState.SelectAirfield(field.Id);
                window.ShowAtcView(Panels.AtcViewPanel.Diagram);
                window.ShowAtcView(Panels.AtcViewPanel.Approach);
            }
            else if (mode.StartsWith("chart:") || mode == "charts")
            {
                // Printed airfield sheets into <dir>/Airfield charts <theatre>: "chart:<airfield>" or "charts" (every airdrome).
                // Up to 3 min: with Discord login someone has to click Authorize first.
                for (int wait = 0; wait < 360 && (AppState.Store.Airfields.Count == 0 || AppState.Store.Theatre is null); wait++) await Task.Delay(500);
                if (AppState.Store.Theatre is { } th)
                {
                    // The local DCS data (pavement, signs, navaids, nav database) loads on first use.
                    Map.Airfields.DcsAirfieldData.PavementFor(th.Id);
                    Map.Airfields.Taxiways.For(th);
                    for (int wait = 0; wait < 240 && (!Map.Airfields.DcsAirfieldData.IsLoaded(th.Id) || Map.Airfields.Taxiways.For(th) is null); wait++) await Task.Delay(500);
                    // The weather (field elevations) comes once a minute.
                    for (int wait = 0; wait < 140 && AppState.Store.Weather is null; wait++) await Task.Delay(500);
                    var fields = AppState.Store.Airfields.Where(a => !a.IsHelipad && a.Runways.Count > 0
                        && (mode == "charts" || a.Name.Equals(mode[6..], StringComparison.OrdinalIgnoreCase))).OrderBy(a => a.Name).ToList();
                    zoomReport.Add($"chart: {fields.Count} field(s), weather {(AppState.Store.Weather is null ? "missing" : "present")}");
                    zoomReport.AddRange(ChartSheets.Save(fields, th, Path.Combine(dir, $"Airfield charts {th.Id}")));
                }
                else zoomReport.Add("chart: no theatre");
            }
            else if (mode == "dcsfolder")
            {
                // The first-start DCS location dialog with what the search finds on this PC.
                DcsLocationWindow.ShowForSnapshot(window);
                zoomReport.Add("dcsfolder: " + string.Join(" | ", Map.Charts.DcsInstall.Search().Select(c => $"{c.Path} ({c.Source}: {string.Join(",", c.Terrains)})")));
            }
            else if (mode == "perf")
            {
                // The UI-thread cost per picture with the busy panels on show: the ATC tab on a
                // carrier (plan, queue), SRS and Packages beside the map. Timings in status.txt.
                await Task.Delay(TimeSpan.FromSeconds(2));
                if (window.FloatPanel("atc") is AtcPanel atc)
                    for (int tries = 0; tries < 10 && !atc.Select("Stennis"); tries++) await Task.Delay(1000);
                window.ShowPackages();
                window.ShowSrs();
                RenderStats.Reset();
            }
            else if (mode == "packages")
            {
                // Two own airborne aircraft into P1, two into P2 (frequencies set), the Packages
                // panel floated, the main map centred on the first.
                await Task.Delay(TimeSpan.FromSeconds(5));
                var own = AppState.Store.Tracks.Where(t => !t.Unit.Detected && !t.OnGround && t.Unit.Category == "airplane")
                    .OrderBy(t => t.Unit.Id).Select(t => t.Unit.Id).Take(4).ToList();
                for (var k = 0; k < own.Count; k++) AppState.Packages.Add(k < 2 ? 1 : 2, own[k]);
                (AppState.Packages[1].Mhz, AppState.Packages[2].Mhz) = (251.0, 262.5);
                AppState.Packages.Edited();
                var panel = window.ShowPackages() as FrameworkElement;
                if (own.Count > 0 && Descendants(window).OfType<MapPanel>().FirstOrDefault() is { } main) main.CenterOn(own[0]);
                await Task.Delay(TimeSpan.FromSeconds(3));
                zoomReport.Add($"packages: {own.Count} aircraft packaged; panel {panel?.GetType().Name ?? "null"} loaded={panel?.IsLoaded} " +
                    $"{panel?.ActualWidth:0}x{panel?.ActualHeight:0} in {(panel is null ? "-" : Window.GetWindow(panel)?.GetType().Name)}");
            }
            else if (mode == "dtc:viper")
            {
                // An F-16C cartridge made through the session as the map would (steerpoints with
                // an OA and a VIP leg, DEST, a threat, a geographic line), every Viper tab opened;
                // what failed, and what the cartridge holds, into status.txt. Nothing is pushed.
                MainWindow.OpenWorkspace(MainWindow.DtcPreset);
                await Task.Delay(TimeSpan.FromSeconds(2));
                var dtc = Dtc.DtcSession.Instance;
                void Step(string what, Action act)
                {
                    try { act(); zoomReport.Add($"dtc: {what} ok"); }
                    catch (Exception ex) { zoomReport.Add($"dtc: {what} FAILED {ex.GetType().Name}: {ex.Message} @ {ex.StackTrace?.Split('\n').FirstOrDefault()?.Trim()}"); }
                }
                var (x0, z0) = AppState.Store.Theatre is { } th ? ((th.X0 + th.X1) / 2, (th.Z0 + th.Z1) / 2) : (0.0, 0.0);
                Step("new cartridges", () => dtc.New());
                Step("F-16C open", () => dtc.SetAircraft(Core.Dtc.Cartridge.ViperType));
                Step("steerpoints", () => { for (var k = 0; k < 3; k++) dtc.AddHere(x0 + k * 9000, z0); });
                Step("OA1", () => { dtc.Select(2); dtc.SetPlaceOffset("OAP_1"); dtc.AddHere(x0 + 9000, z0 + 4000); });
                Step("VIP leg", () => { dtc.VipVrp.SetSteerpoint(true, 1); dtc.SetPlaceOffset("VIPTOTGT"); dtc.AddHere(x0 - 6000, z0 + 6000); });
                Step("route 1", () => { foreach (var n in new[] { 1, 2, 3 }) dtc.Stpt.SetInRoute(1, n, true); dtc.Notify(); });
                Step("DEST", () => { dtc.SetPlace(Dtc.DtcPlace.Gps); dtc.AddHere(x0, z0 - 8000); dtc.AddHere(x0 + 3000, z0 - 8000); });
                Step("threat", () => { dtc.SetPlace(Dtc.DtcPlace.Sa); dtc.SetSaPlace(Dtc.SaKind.Threat); dtc.ViperThreatType = 6; dtc.AddHere(x0 + 20000, z0 + 10000); });
                Step("geo line 2", () => { dtc.SetSaPlace(Dtc.SaKind.Geo); dtc.SetSaLine(1); for (var k = 0; k < 3; k++) dtc.AddHere(x0 - 10000, z0 - 4000 + k * 4000); });
                Step("geo insert", () => { dtc.SelectSa(new Dtc.SaRef(Dtc.SaKind.Geo, 1, 0)); dtc.AddHere(x0 - 12000, z0 - 2000); });
                Step("move DEST 1", () => { dtc.SetPlace(Dtc.DtcPlace.Gps); dtc.SelectGps(1); dtc.MoveSelectedTo(x0 + 500, z0 - 9000); });
                Step("delete steerpoint 1", () => { dtc.SetPlace(Dtc.DtcPlace.Waypoints); dtc.DeleteWaypoint(1); });
                if (Descendants(window).OfType<Panels.ViperTabs>().FirstOrDefault() is { } tabs)
                    for (var k = 0; k < tabs.Tabs.Items.Count; k++)
                    {
                        var index = k;
                        Step($"tab {(tabs.Tabs.Items[index] as System.Windows.Controls.TabItem)?.Header}", () => tabs.Tabs.SelectedIndex = index);
                        await Task.Delay(400);
                    }
                else zoomReport.Add("dtc: no ViperTabs on screen");
                Step("prepare", () => dtc.Prepare(Core.Dtc.Cartridge.ViperType));
                zoomReport.Add($"dtc: {dtc.Stpt.Points.Count} STPT, route 1 {string.Join(",", dtc.Stpt.Route(1))}, {dtc.Dest.Points.Count} DEST, {dtc.Threats.Points.Count} threats, " +
                    $"line 2 {dtc.Geo.Line(2).Count} points, VIP→TGT set {dtc.VipVrp.IsSet("VIPTOTGT")}, mirror NAV {dtc.Stpt.Mirror}");
                if (Descendants(window).OfType<Panels.ViperTabs>().FirstOrDefault() is { } back) back.Tabs.SelectedIndex = 0;

                // The F-14 the same way: plan 2's waypoints, a line, an additional point, a JDAM target.
                Step("F-14 open", () => dtc.SetAircraft(Core.Dtc.Cartridge.TomcatType));
                Step("F-14 plan 1 refuses waypoints", () => { dtc.SetTomcatPlan(1); if (dtc.AddHere(x0, z0) is null) throw new InvalidOperationException("plan 1 took a waypoint"); });
                Step("F-14 waypoints", () => { dtc.SetTomcatPlan(2); dtc.SetPlace(Dtc.DtcPlace.Waypoints); for (var k = 0; k < 3; k++) dtc.AddHere(x0 + k * 7000, z0 + 3000); });
                Step("F-14 line", () => { dtc.SetPlace(Dtc.DtcPlace.Sa); dtc.SetSaPlace(Dtc.SaKind.Geo); dtc.SetSaLine(null); for (var k = 0; k < 3; k++) dtc.AddHere(x0 - 5000, z0 + k * 3000); });
                Step("F-14 extra point", () => { dtc.SetPlace(Dtc.DtcPlace.Gps); dtc.AddHere(x0 + 1000, z0 + 1000); });
                Step("F-14 JDAM", () => { dtc.SetPlace(Dtc.DtcPlace.Jdam); dtc.SelectJdam(2, 1); dtc.AddHere(x0 + 15000, z0); dtc.TurnJdam(2, 1, x0 + 15000, z0 + 8000); });
                if (Descendants(window).OfType<Panels.TomcatTabs>().FirstOrDefault() is { } ttabs)
                    for (var k = 0; k < ttabs.Tabs.Items.Count; k++)
                    {
                        var index = k;
                        Step($"F-14 tab {(ttabs.Tabs.Items[index] as System.Windows.Controls.TabItem)?.Header}", () => ttabs.Tabs.SelectedIndex = index);
                        await Task.Delay(400);
                    }
                else zoomReport.Add("dtc: no TomcatTabs on screen");
                Step("F-14 prepare", () => dtc.Prepare(Core.Dtc.Cartridge.TomcatType));
                var jt = dtc.Jdam.Target(2, 1)!;
                zoomReport.Add($"dtc: F-14 plan 2 {dtc.TNav.Waypoints(2).Count} waypoints, {dtc.TNav.Lines(2).Count} lines, {dtc.TNav.Extra(2).Count} extra, " +
                    $"JDAM STA4 T1 active {Core.Dtc.TomcatJdam.IsActive(jt)} heading {jt["attack_heading"]}, next pick {dtc.JdamPick}; share: {dtc.ShareNote ?? "all went"}");
                if (Descendants(window).OfType<Panels.TomcatTabs>().FirstOrDefault() is { } tback) tback.Tabs.SelectedIndex = 0;
            }
            else if (mode.StartsWith("workspace:"))
            {
                // Open a workspace window: "workspace:GCI" or "workspace:ATC".
                await Task.Delay(TimeSpan.FromSeconds(1));
                MainWindow.OpenWorkspace(mode["workspace:".Length..]);
            }
            else if (mode.StartsWith("detach:"))
            {
                // Detach a workspace tab into its own window: "detach:ATC".
                await Task.Delay(TimeSpan.FromSeconds(2));
                var detached = window.DetachFirst(mode["detach:".Length..]) as WorkspaceWindow;
                await Task.Delay(TimeSpan.FromSeconds(2));
                if (detached is null) zoomReport.Add("detach: no such tab");
                else
                {
                    var dock = detached.View.Dock;
                    var contents = dock.Layout.Descendents().OfType<AvalonDock.Layout.LayoutContent>().Select(c => $"{c.ContentId}:{c.Content?.GetType().Name ?? "null"}").ToList();
                    zoomReport.Add($"detach: {detached.Title} {detached.ActualWidth:0}x{detached.ActualHeight:0}; dock {dock.ActualWidth:0}x{dock.ActualHeight:0} loaded={dock.IsLoaded}; view {detached.View.ActualWidth:0}x{detached.View.ActualHeight:0} visible={detached.View.Visibility}; {contents.Count} contents: {string.Join(", ", contents)}");
                }
            }
            else if (mode.StartsWith("coverage:"))
            {
                // The SRS transmit coverage from an airfield ("coverage:<airfield>[@<feet>]"), the map
                // centred on it; no command goes to the SRS app.
                for (int wait = 0; wait < 40 && (AppState.Store.Airfields.Count == 0 || AppState.Store.Terrain is null); wait++) await Task.Delay(250);
                var spec = mode["coverage:".Length..].Split('@');
                if (spec.Length > 1 && double.TryParse(spec[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ft))
                    AppState.Settings.Srs.CoverageAltFt = ft;
                if (AppState.Store.Airfields.FirstOrDefault(a => a.Name.Equals(spec[0], StringComparison.OrdinalIgnoreCase)) is { } field && AppState.Store.Theatre is { } th)
                {
                    AppState.Settings.Srs.Position = SrsPosition.AtcAirfield;
                    AppState.Settings.Srs.ShowCoverage = true;
                    AppState.SelectAirfield(field.Id);
                    Srs.SrsCoverage.Instance.Update();
                    var (lat, lon) = th.ToLatLon(field.X, field.Z);
                    if (Descendants(window).OfType<MapPanel>().FirstOrDefault(m => !m.IsAirfieldView) is { } map)
                        map.ShowAt(lat, lon, 500 * Core.Nav.MetersPerNm * Map.Mercator.ScaleAt(lat) / Math.Max(300, map.ActualWidth));
                    for (int wait = 0; wait < 20 && Srs.SrsCoverage.Instance.Coverage is null; wait++) await Task.Delay(250);
                    var c = Srs.SrsCoverage.Instance.Coverage;
                    zoomReport.Add(c is null ? "coverage: none" : $"coverage: {field.Name}, horizon {c.HorizonM / 1852:0} NM, {c.Rays.Sum(r => r.Count)} visible stretches over {c.Rays.Count} rays");
                }
                else zoomReport.Add($"coverage: airfield not found ({AppState.Store.Airfields.Count} airfields: {string.Join(", ", AppState.Store.Airfields.Take(6).Select(a => a.Name))}; terrain {(AppState.Store.Terrain is null ? "no" : "yes")})");
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
            else if (mode.StartsWith("eam:"))
            {
                // Drives the controller's real SRS app into External AWACS Mode once ("eam:<host>:<port>",
                // the EAM password from NEBO_EAM_PW): only when asked for by this mode.
                var t = mode.Split(':');
                Srs.SrsAutoConnect.Start(t[1], int.Parse(t[2]), Environment.GetEnvironmentVariable("NEBO_EAM_PW") ?? "", "NEBO test");
                for (int wait = 0; wait < 400 && (Srs.SrsAutoConnect.Status.Length == 0 || Srs.SrsAutoConnect.Status.EndsWith('…')); wait++)
                    await Task.Delay(250);
                zoomReport.Add("eam: " + Srs.SrsAutoConnect.Status);
            }
            else if (mode.StartsWith("srs:"))
            {
                // Float the SRS tab and send one tune command: "srs:<radio>:<MHz>[:<airfield>]",
                // the airfield picked as if in the ATC tab.
                await Task.Delay(TimeSpan.FromSeconds(2));
                window.FloatPanel("srs");
                var t = mode.Split(':');
                Srs.SrsBridge.Instance.Tune(int.Parse(t[1]), double.Parse(t[2], System.Globalization.CultureInfo.InvariantCulture));
                if (t.Length > 3 && AppState.Store.Airfields.FirstOrDefault(a => a.Name.Equals(t[3], StringComparison.OrdinalIgnoreCase)) is { } field)
                    AppState.SelectAirfield(field.Id);
            }
            else if (mode.StartsWith("lso:") || mode.StartsWith("carrier:"))
            {
                // Float the ATC tab and pick a carrier: "carrier:<name>[@<NM>]" or "lso:<name>" (e.g. Stennis, Stennis@3).
                await Task.Delay(TimeSpan.FromSeconds(2));
                if (window.FloatPanel("atc") is AtcPanel atc)
                {
                    var name = mode[(mode.IndexOf(':') + 1)..];
                    if (name.EndsWith("+q"))
                    {
                        name = name[..^2];
                        _ = QueueSuggested(atc);
                    }
                    if (name.Split('@') is [var n, var nm] && double.TryParse(nm, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var range))
                    {
                        name = n;
                        atc.CarrierView.RangeNm = range;
                    }
                    for (int tries = 0; tries < 10 && !atc.Select(name); tries++) await Task.Delay(1000);  // carriers come with the pictures
                    if (mode.StartsWith("lso:")) atc.ShowLso();
                }
            }
            else if (mode.StartsWith("atc:") || mode.StartsWith("approach:"))
            {
                // Float the ATC tab and pick an airfield in it: "atc:<airfield name>" (diagram)
                // or "approach:<airfield name>" (the 10 NM approach view).
                await Task.Delay(TimeSpan.FromSeconds(2));
                if (window.FloatPanel("atc") is AtcPanel atc)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1));
                    var name = mode[(mode.IndexOf(':') + 1)..];
                    if (name.EndsWith("+q"))
                    {
                        name = name[..^2];
                        _ = QueueSuggested(atc);
                    }
                    atc.Select(name);
                    if (mode.StartsWith("approach:")) atc.ShowApproach();
                    // Like dragging the pane around: its window passes through tiny sizes.
                    if (Environment.GetEnvironmentVariable("NEBO_SNAPSHOT_SHRINK") == "1" && Window.GetWindow(atc) is { } floating)
                    {
                        foreach (var (w, h) in new[] { (60.0, 40.0), (200.0, 90.0), (430.0, 150.0), (300.0, 600.0), (990.0, 824.0) })
                        {
                            floating.Width = w;
                            floating.Height = h;
                            floating.UpdateLayout();
                            atc.ShowApproach();
                            floating.UpdateLayout();
                            atc.ShowDiagram();
                            floating.UpdateLayout();
                            await Task.Delay(200);
                        }
                        if (mode.StartsWith("approach:")) atc.ShowApproach();
                    }
                    // Optional lat,lon,res: where the docked main map looks meanwhile.
                    if (args.Length > 7 && Descendants(window).OfType<MapPanel>().FirstOrDefault() is { } main)
                    {
                        var v = args[7].Split(',');
                        main.ShowAt(double.Parse(v[0], System.Globalization.CultureInfo.InvariantCulture),
                            double.Parse(v[1], System.Globalization.CultureInfo.InvariantCulture),
                            double.Parse(v[2], System.Globalization.CultureInfo.InvariantCulture));
                    }
                }
            }
            else if (mode.StartsWith("hero"))
            {
                // The front page's GCI scene on the main map: up to three of our airborne aircraft,
                // each pinned to the nearest hostile air contact (engagement lines with intercepts,
                // markers 1-3, an alert on the first), all framed, the Engagements panel open.
                // "hero[:<base map>]".
                await Task.Delay(TimeSpan.FromSeconds(10));  // the pictures fill the store
                if (Descendants(window).OfType<MapPanel>().FirstOrDefault() is { } main)
                {
                    if (mode.Split(':') is [_, var bm]) main.SetBaseMap(Enum.Parse<BaseMap>(bm));
                    main.SetFilter("airfields", false);  // helipad names clutter the scene
                    bool Air(Core.Track t) => !t.OnGround && t.Unit.Lat is not null && t.Unit.Category is "airplane" or "helicopter";
                    var own = AppState.Store.Tracks.Where(t => Air(t) && !t.Unit.Detected && t.Unit.Category == "airplane").ToList();
                    var hostile = AppState.Store.Tracks.Where(t => Air(t) && t.Unit.Detected).ToList();
                    double Nm(Core.Track a, Core.Track b) => Core.Nav.BearingRange(a.Unit.Lat!.Value, a.Unit.Lon!.Value, b.Unit.Lat!.Value, b.Unit.Lon!.Value).Meters / Core.Nav.MetersPerNm;
                    // One line per own flight and per hostile group: leads only, nearest first.
                    string Key(Core.Track t) => t.Unit.Group is { Length: > 0 } g ? g : $"#{t.Unit.Id}";
                    var pairs = new List<(Core.Track Own, Core.Track Hostile)>();
                    foreach (var h in hostile.OrderBy(h => own.Count == 0 ? 0 : own.Min(o => Nm(o, h))))
                    {
                        if (pairs.Any(p => Nm(p.Hostile, h) < 3)) continue;  // a wingman of a contact already paired
                        var o = own.Where(o => pairs.All(p => Key(p.Own) != Key(o))).OrderBy(o => Nm(o, h)).FirstOrDefault();
                        if (o is null || Nm(o, h) > 80 || pairs.Count >= 3) continue;
                        pairs.Add((o, h));
                    }
                    if (pairs.Count > 0)
                    {
                        var lats = pairs.SelectMany(p => new[] { p.Own.Unit.Lat!.Value, p.Hostile.Unit.Lat!.Value }).ToList();
                        var lons = pairs.SelectMany(p => new[] { p.Own.Unit.Lon!.Value, p.Hostile.Unit.Lon!.Value }).ToList();
                        var span = Core.Nav.BearingRange(lats.Min(), lons.Min(), lats.Max(), lons.Max()).Meters;
                        main.ShowAt((lats.Min() + lats.Max()) / 2, (lons.Min() + lons.Max()) / 2, Math.Max(40, span * 1.6 / Math.Max(300, main.ActualWidth)));
                        await Task.Delay(TimeSpan.FromSeconds(1));
                        foreach (var (o, h) in pairs)
                        {
                            var (ox, oy) = main.DebugScreen(o.Unit.Lat!.Value, o.Unit.Lon!.Value);
                            var (hx, hy) = main.DebugScreen(h.Unit.Lat!.Value, h.Unit.Lon!.Value);
                            main.DebugPin(new Point(ox, oy), new Point(hx, hy));
                        }
                        var lines = AppState.Engagements.Lines;
                        for (var k = 0; k < lines.Count && k < 3; k++) AppState.Engagements.SetMarker(lines[k].Id, k + 1);
                        if (lines.Count > 0) AppState.Engagements.SetAlert(lines[0].Id, 40);
                    }
                    window.ShowEngagements();
                    zoomReport.Add($"hero: {own.Count} own airborne, {hostile.Count} hostile air contacts; {pairs.Count} engagements: " +
                                   string.Join("; ", pairs.Select(p => $"{p.Own.Unit.Name} -> {p.Hostile.Unit.Type ?? "?"} {Nm(p.Own, p.Hostile):0} NM")));
                    await Task.Delay(TimeSpan.FromSeconds(3));
                }
            }
            else if (mode != "restore")
            {
                var second = window.OpenMap(Enum.Parse<BaseMap>(mode));
                await Task.Delay(TimeSpan.FromSeconds(2));  // like a user: the tab is shown before it is dragged out
                second.Float();
                if (args.Length > 7 && second.Content is MapPanel panel)
                {
                    var v = args[7].Split(',');
                    await Task.Delay(TimeSpan.FromSeconds(1));
                    panel.ShowAt(double.Parse(v[0], System.Globalization.CultureInfo.InvariantCulture),
                        double.Parse(v[1], System.Globalization.CultureInfo.InvariantCulture),
                        double.Parse(v[2], System.Globalization.CultureInfo.InvariantCulture));
                    if (v.Length > 3 && v[3] == "coast") panel.ShowCoastlineOverlay();
                    if (v.Length > 3 && v[3] == "grid") panel.SetGrid(true);
                    if (v.Length > 3 && v[3].StartsWith("dim")) panel.SetBrightness(double.Parse(v[3][3..], System.Globalization.CultureInfo.InvariantCulture));
                    if (v.Length > 3 && v[3].StartsWith("zone=")) panel.CenterOnZone(v[3][5..], westEdge: true);  // keeps the resolution
                    if (args.Length > 8 && args[8] == "point")
                    {
                        // Alt+click at the view centre (test aid).
                        await Task.Delay(TimeSpan.FromSeconds(2));
                        panel.ShowPoint(new Point(panel.Surface.ActualWidth / 2, panel.Surface.ActualHeight / 2));
                    }
                    if (args.Length > 8 && args[8] == "settletest")
                    {
                        await Task.Delay(TimeSpan.FromSeconds(seconds));
                        zoomReport = await SettleTest(panel);
                    }
                    if (args.Length > 8 && args[8] == "switchtest")
                    {
                        // Flip through the tile-heavy base maps at close zoom (each queues many
                        // tiles), then the DCS Map: do its tiles still come first?
                        await Task.Delay(TimeSpan.FromSeconds(seconds));
                        panel.ShowAt(34.70, 36.62, 8);
                        foreach (var b in new[] { BaseMap.DcsSatellite, BaseMap.Satellite, BaseMap.DcsCharts, BaseMap.DcsSatellite, BaseMap.Satellite })
                        {
                            panel.SetBaseMap(b);
                            await Task.Delay(1500);
                        }
                        panel.SetBaseMap(BaseMap.DcsMap);
                        zoomReport = await SettleTest(panel);
                        zoomReport.Insert(0, "switchtest: DCS Satellite, Satellite, DCS Charts, DCS Satellite, Satellite (1.5 s each), then DCS Map");
                    }
                    if (args.Length > 8 && args[8] == "wheeltest")
                    {
                        await Task.Delay(TimeSpan.FromSeconds(seconds));
                        zoomReport = await WheelTest(panel, panel.Surface);
                    }
                    if (args.Length > 8 && args[8] == "zoomtest")
                    {
                        await Task.Delay(TimeSpan.FromSeconds(seconds));  // let tiles load first
                        zoomReport = await ZoomTest(panel, double.Parse(v[0], System.Globalization.CultureInfo.InvariantCulture),
                            double.Parse(v[1], System.Globalization.CultureInfo.InvariantCulture),
                            double.Parse(v[2], System.Globalization.CultureInfo.InvariantCulture));
                    }
                    if (args.Length > 8 && args[8] == "measure")
                    {
                        // Ruler from the bullseye to the first own aircraft (BRAA), the bulls key
                        // held at its end (the cursor while dragging): the two labels must not overlap.
                        await Task.Delay(TimeSpan.FromSeconds(2));
                        var bulls = AppState.Store.Latest?.Bullseye;
                        var air = AppState.Store.Tracks.FirstOrDefault(t => t.Unit.Category is "airplane" && t.Unit.Lat is not null);
                        if (bulls is not null && air is not null)
                        {
                            // Frame both ends.
                            double midLat = (bulls.Lat + air.Unit.Lat!.Value) / 2, midLon = (bulls.Lon + air.Unit.Lon!.Value) / 2;
                            var spanM = Math.Max(Math.Abs(bulls.Lat - air.Unit.Lat!.Value) * 111_000 * 1.25, Math.Abs(bulls.Lon - air.Unit.Lon!.Value) * 111_000);
                            panel.ShowAt(midLat, midLon, Math.Max(20, spanM * 1.6 / Math.Max(300, panel.ActualWidth)));
                            await Task.Delay(TimeSpan.FromSeconds(1));
                            var (bx, by) = panel.DebugScreen(bulls.Lat, bulls.Lon);
                            var (ax, ay) = panel.DebugScreen(air.Unit.Lat!.Value, air.Unit.Lon!.Value);
                            panel.DebugMeasure(new Point(bx, by), new Point(ax, ay), new Point(ax, ay));
                            zoomReport.Add($"measure: bulls {bulls.Lat:F4},{bulls.Lon:F4} -> {air.Unit.Name} {air.Unit.Lat:F4},{air.Unit.Lon:F4} hdg {air.Unit.Hdg:F0} alt {air.Unit.AltFt:F0} ft");
                        }
                        else zoomReport.Add($"measure: bulls={bulls is not null} aircraft={air is not null}");
                    }
                    if (args.Length > 8 && args[8] == "braa")
                    {
                        // A pinned BRAA line from the first aircraft of group "BRAA FTR" to the first of
                        // "BRAA TGT" (spawned for the test), both framed: the intercept must show.
                        await Task.Delay(TimeSpan.FromSeconds(2));
                        Core.Track? First(string group) => AppState.Store.Tracks.Where(t => t.Unit.Group == group && t.Unit.Lat is not null).OrderBy(t => t.Unit.Id).FirstOrDefault();
                        if (First("BRAA FTR") is { } ftr && First("BRAA TGT") is { } tgt)
                        {
                            panel.ShowAt((ftr.Unit.Lat!.Value + tgt.Unit.Lat!.Value) / 2, (ftr.Unit.Lon!.Value + tgt.Unit.Lon!.Value) / 2,
                                Math.Max(20, Core.Nav.BearingRange(ftr.Unit.Lat!.Value, ftr.Unit.Lon!.Value, tgt.Unit.Lat!.Value, tgt.Unit.Lon!.Value).Meters * 1.8 / Math.Max(300, panel.ActualWidth)));
                            await Task.Delay(TimeSpan.FromSeconds(1));
                            var (fx, fy) = panel.DebugScreen(ftr.Unit.Lat!.Value, ftr.Unit.Lon!.Value);
                            var (tx, ty) = panel.DebugScreen(tgt.Unit.Lat!.Value, tgt.Unit.Lon!.Value);
                            panel.DebugPin(new Point(fx, fy), new Point(tx, ty));
                            var icpt = Core.Intercept.Solve(ftr.Unit.Lat!.Value, ftr.Unit.Lon!.Value, ftr.Unit.Spd ?? 0, tgt.Unit.Lat!.Value, tgt.Unit.Lon!.Value, tgt.Unit.Hdg ?? 0, tgt.Unit.Spd ?? 0);
                            zoomReport.Add($"braa: {ftr.Unit.Name} -> {tgt.Unit.Name}; intercept {(icpt is null ? "none" : $"{icpt.Seconds:0} s, hdg {icpt.HeadingTrue:0} T")}");
                        }
                        else zoomReport.Add("braa: groups BRAA FTR / BRAA TGT not in the picture");
                    }
                    if (args.Length > 8 && args[8].StartsWith("gci"))
                    {
                        // "gci" or "gci+send": the triangulation board on the first own aircraft.
                        await Task.Delay(TimeSpan.FromSeconds(2));
                        var air = AppState.Store.Tracks.FirstOrDefault(t => !t.Unit.Detected && t.Unit.Category is "airplane" && t.Unit.Hdg is not null && !t.OnGround);
                        zoomReport.Add(air is null ? "gci: no own aircraft in the air" : await panel.DebugGci(air.Unit.Id, args[8] == "gci+send"));
                        await Task.Delay(TimeSpan.FromSeconds(6));  // the marks feed comes back within ~5 s
                        zoomReport.Add($"gci: marks {AppState.Board.Marks.Count}, nebo {AppState.Board.Marks.Count(m => m.Nebo)}, threats {AppState.Board.AllThreats().Count}");
                    }
                    if (args.Length > 8 && args[8] == "nctr")
                    {
                        // NCTR on the first contact (small box) through the client's command path;
                        // every second for 30 s: the contacts the store holds, frozen and lost ones.
                        await Task.Delay(TimeSpan.FromSeconds(2));
                        var store = AppState.Store;
                        string State() => string.Join(" ", store.Tracks.Where(t => t.Unit.Detected).OrderBy(t => t.Unit.Id)
                            .Select(t => $"{t.Unit.Id}{(t.Unit.Frozen ? "F" : "")}{(t.Lost ? "L" : "")}"));
                        zoomReport.Add($"nctr: before {State()}");
                        // The contact nearest one of our ships (carriers' radars can work it), else any.
                        var ships = store.Tracks.Where(t => !t.Unit.Detected && t.Unit.Category == "ship" && t.Unit.Lat is not null).Select(t => t.Unit).ToList();
                        double NearShip(Core.Track t) => ships.Count == 0 || t.Unit.Lat is not { } la || t.Unit.Lon is not { } lo ? 0
                            : ships.Min(s => Core.Nav.BearingRange(s.Lat!.Value, s.Lon!.Value, la, lo).Meters);
                        // NEBO_NCTR_UNIT=<contact id> picks the contact (screenshots).
                        var wanted = long.TryParse(Environment.GetEnvironmentVariable("NEBO_NCTR_UNIT"), out var w) ? w : (long?)null;
                        if (store.Tracks.Where(t => t.Unit.Detected && !t.Lost).OrderBy(t => wanted == t.Unit.Id ? -1 : NearShip(t)).FirstOrDefault() is { Unit: { Lat: { } lat, Lon: { } lon } } first && store.Theatre is { } th)
                        {
                            var (x, z) = th.FromLatLon(lat, lon);
                            var r = await AppState.Connection.SendCommandAsync("nctr", new Dictionary<string, object>
                                { ["x"] = (long)x - 3000, ["z"] = (long)z - 3000, ["x2"] = (long)x + 3000, ["z2"] = (long)z + 3000, ["methods"] = "jem,size" });
                            zoomReport.Add($"nctr: on {first.Unit.Id}: ok={r.Ok} {r.Value}{r.Error}");
                            // 12 s: the request is still running when the windows are captured (cones drawn).
                            for (int sec = 1; sec <= 12; sec++)
                            {
                                await Task.Delay(TimeSpan.FromSeconds(1));
                                if (sec % 3 == 0)
                                    zoomReport.Add($"nctr: {sec,2} s {State()} | active " + string.Join(" ", store.Nctr?.Requests.Where(q => q.Running)
                                        .SelectMany(q => q.Results).Where(x => x.Active).Select(x => $"{x.Unit}:{x.Method}") ?? []));
                            }
                            foreach (var c in store.Nctr?.Requests.Where(q => q.Running).SelectMany(q => q.Cones ?? []) ?? [])
                                zoomReport.Add($"nctr: cone {c.Platform} centre {c.Center:0}° ±{c.Half:0}° to {c.Range / 1852:0} NM");
                            panel.ShowAt(lat, lon, 120 * Core.Nav.MetersPerNm * Map.Mercator.ScaleAt(lat) / Math.Max(300, panel.ActualWidth));
                            await Task.Delay(TimeSpan.FromSeconds(1));
                        }
                        else zoomReport.Add("nctr: no contact");
                    }
                    if (args.Length > 8 && args[8] == "rwr")
                    {
                        // The RWR ring on an own airborne aircraft, with its 3 o'clock line.
                        await Task.Delay(TimeSpan.FromSeconds(2));
                        zoomReport.Add(await panel.DebugRwr());
                        await Task.Delay(TimeSpan.FromSeconds(2));
                    }
                    if (args.Length > 8 && args[8] == "pantest")
                    {
                        await Task.Delay(TimeSpan.FromSeconds(seconds));
                        zoomReport = await PanTest(panel, double.Parse(v[0], System.Globalization.CultureInfo.InvariantCulture),
                            double.Parse(v[1], System.Globalization.CultureInfo.InvariantCulture),
                            double.Parse(v[2], System.Globalization.CultureInfo.InvariantCulture));
                    }
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(seconds));

            Directory.CreateDirectory(dir);
            var i = 0;
            // Memory after a full collection: comparable between runs, unlike raw private bytes.
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var proc = System.Diagnostics.Process.GetCurrentProcess();
            var info = new List<string>
            {
                AppState.Connection.Status, $"tracks={AppState.Store.Tracks.Count}",
                $"timings (count, avg, max ms): {string.Join(" | ", RenderStats.Report().Where(r => r.StartsWith("UI:") || r.StartsWith("panel:") || r.StartsWith("ObjectsPanel")))}",
                $"memory: managed {GC.GetTotalMemory(false) / 1048576} MB, private {proc.PrivateMemorySize64 / 1048576} MB (after full GC)",
            };
            foreach (Window w in Application.Current.Windows)
            {
                var name = w == window ? "main.png" : $"window{++i}.png";
                info.Add($"{name}: {w.GetType().Name} {w.ActualWidth:F0}x{w.ActualHeight:F0} visible={w.IsVisible}");
                foreach (var map in Descendants(w).OfType<MapPanel>())
                {
                    var (cx, cy, res) = map.ViewCenter;
                    info.Add($"  MapPanel {map.ContentId[..12]} {map.ActualWidth:F0}x{map.ActualHeight:F0} visible={map.IsVisible} loaded={map.IsLoaded} " +
                             $"viewport {cx:F0},{cy:F0} res {res:F2} {Renderer(map)}");
                }
                Save(w, Path.Combine(dir, name));
            }
            // Floating panes live in their own HwndSource (AvalonDock hosts them via HwndHost),
            // invisible to the window's visual tree: capture every source's root as well.
            var s = 0;
            foreach (var source in PresentationSource.CurrentSources.OfType<PresentationSource>())
            {
                if (source.RootVisual is not FrameworkElement root || root is Window) continue;
                var maps = Descendants(root).Where(d => d is MapPanel or AtcPanel or SrsPanel or Panels.AtcViewPanel).ToList();
                if (maps.Count == 0 && root.GetType().Name != "PopupRoot") continue;  // pop-ups too (Alt+click point)
                var name = $"source{++s}.png";
                info.Add($"{name}: {root.GetType().Name} {root.ActualWidth:F0}x{root.ActualHeight:F0} maps={maps.Count}");
                foreach (var map in maps.OfType<MapPanel>())
                {
                    var (cx, cy, res) = map.ViewCenter;
                    info.Add($"  MapPanel {map.ContentId[..12]} viewport {cx:F0},{cy:F0} res {res:F2} {Renderer(map)}");
                }
                foreach (var diagram in Descendants(root).OfType<Map.Airfields.AirfieldDiagramView>())
                    info.Add($"  Diagram {diagram.DebugState}");
                Save(root, Path.Combine(dir, name));
            }
            info.AddRange(zoomReport);
            File.WriteAllLines(Path.Combine(dir, "status.txt"), info);
            window.Close();  // through OnClosing, so the layout is saved when Persist
            Application.Current.Shutdown();
        };
    }

    /// <summary>Zooms in and out (and pans) in 60 steps at ~60 Hz like a mouse wheel.</summary>
    private static Task<List<string>> ZoomTest(MapPanel panel, double lat, double lon, double res) =>
        Steps(panel, "zoomtest", 60, i =>
        {
            var k = i < 30 ? i : 59 - i;  // in for 30 steps, back out for 30
            panel.ShowAt(lat + i * 0.002, lon + i * 0.003, res * Math.Pow(0.92, k));
        });

    /// <summary>Pans at a fixed zoom in 90 steps at ~60 Hz like a mouse drag.</summary>
    private static Task<List<string>> PanTest(MapPanel panel, double lat, double lon, double res)
    {
        var degPerStep = res * 8 / 111_000;  // ~8 DIP per step
        return Steps(panel, "pantest", 90, i =>
        {
            var k = i < 45 ? i : 89 - i;
            panel.ShowAt(lat + k * degPerStep * 0.5, lon + k * degPerStep, res);
        });
    }

    /// <summary>Runs view steps paced at ~60 Hz. The honest figure is how long the steps take
    /// (16.7 ms each when the UI keeps up: 1.5 s for 90): a blocked UI thread stretches them.
    /// WPF's Rendering events fire even when frames are not drawn, so they are not counted as
    /// frames; the map surface reports the frames it painted and their cost.</summary>
    private static async Task<List<string>> Steps(MapPanel panel, string name, int steps, Action<int> step)
    {
        RenderStats.Reset();
        panel.Surface.ResetStats();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var next = 0.0;
        for (int i = 0; i < steps; i++)
        {
            step(i);
            next += 1000.0 / 60;
            var wait = next - sw.Elapsed.TotalMilliseconds;
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, wait)));
        }
        var stepsMs = sw.ElapsedMilliseconds;
        await Task.Delay(300);
        var ideal = steps * 1000 / 60;
        var report = new List<string> { $"{name}: {steps} steps in {stepsMs} ms (ideal {ideal}) = {steps * 1000.0 / stepsMs:F0} steps/s, renderer {Renderer(panel)}" };
        var (frames, total, max) = panel.Surface.Stats;
        report.Add($"{name}: painted {frames} frames, paint avg {total / Math.Max(1, frames):F2} ms, max {max:F2} ms");
        report.AddRange(RenderStats.Report());
        return report;
    }

    /// <summary>Zooms like a mouse wheel through the smooth-zoom animation: 8 notches in, 120 ms
    /// apart, around a point off-centre, then 8 out; reports gaps between painted frames and
    /// frames drawn with tiles still missing.</summary>
    private static async Task<List<string>> WheelTest(MapPanel panel, Map.Gpu.MapSurface surface)
    {
        RenderStats.Reset();
        surface.ResetStats();
        double x = surface.ActualWidth * 0.6, y = surface.ActualHeight * 0.4;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var notches in new[] { 1, -1 })
        {
            for (int i = 0; i < 8; i++)
            {
                surface.Wheel(notches, x, y);
                await Task.Delay(120);
            }
            await Task.Delay(700);
        }
        var (frames, total, max) = surface.Stats;
        var (gap, over25, over50) = surface.Gaps;
        var report = new List<string>
        {
            $"wheeltest: 16 notches in {sw.ElapsedMilliseconds} ms, renderer {Renderer(panel)}",
            $"wheeltest: painted {frames} frames, paint avg {total / Math.Max(1, frames):F2} ms, max {max:F2} ms; longest gap {gap:F0} ms, gaps >25 ms: {over25}, >50 ms: {over50}",
        };
        report.AddRange(RenderStats.CounterReport());
        report.AddRange(surface.GapLog);
        report.AddRange(RenderStats.Report());
        return report;
    }

    /// <summary>DCS Map: how long after a view change until every tile of the wanted level is on
    /// screen. Jumps into close-up areas (tiles rendered on demand), then zooms in and out.</summary>
    private static async Task<List<string>> SettleTest(MapPanel panel)
    {
        RenderStats.Reset();
        var report = new List<string>();
        (string Name, double Lat, double Lon, double Res)[] steps =
        [
            ("approach: Homs at city zoom", 34.73, 36.71, 40),
            ("jump to Homs, close (cold tiles)", 34.73, 36.71, 4),
            ("pan to next area", 34.76, 36.76, 4),
            ("zoom in 3x", 34.76, 36.76, 1.3),
            ("zoom out 8x", 34.76, 36.76, 10),
        ];
        foreach (var (name, lat, lon, res) in steps)
        {
            panel.ShowAt(lat, lon, res);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var framesAtStart = panel.Surface.Stats.Frames;
            int firstMissing = -1;
            while (sw.Elapsed < TimeSpan.FromSeconds(60))
            {
                await Task.Delay(50);
                if (panel.Surface.Stats.Frames <= framesAtStart) continue;  // no frame of the new view yet
                if (firstMissing < 0) firstMissing = Map.Gpu.DcsMapSk.LastMissing;
                if (Map.Gpu.DcsMapSk.LastMissing == 0) break;
            }
            report.Add($"settletest: {name}: {sw.ElapsedMilliseconds} ms to complete ({firstMissing} tiles missing at first)");
            if (name.StartsWith("approach")) await Task.Delay(1500);  // a person zooming in steps
        }
        report.AddRange(RenderStats.CounterReport());
        report.AddRange(RenderStats.Report());
        return report;
    }

    private static string Renderer(MapPanel panel) => panel.Surface.UsesGpu ? "gpu" : "skia-cpu";

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    private static void Save(FrameworkElement w, string path)
    {
        if (w.ActualWidth < 1 || !w.IsVisible) return;
        var dpi = VisualTreeHelper.GetDpi(w);
        var bmp = new RenderTargetBitmap((int)(w.ActualWidth * dpi.DpiScaleX), (int)(w.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(w);
        // GPU maps are not in RenderTargetBitmap's picture: draw each one's own CPU render
        // over its place, then the WPF elements above the surface (attribution) again.
        var maps = Descendants(w).OfType<MapPanel>().Where(m => m.Surface.IsVisible).ToList();
        if (maps.Count > 0)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawImage(bmp, new Rect(0, 0, w.ActualWidth, w.ActualHeight));
                foreach (var map in maps)
                {
                    var s = map.Surface;
                    using var image = s.RenderToImage((float)dpi.DpiScaleX);
                    using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                    var frame = BitmapFrame.Create(new MemoryStream(data.ToArray()), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    var at = s.TransformToAncestor(w).Transform(new Point(0, 0));
                    dc.DrawImage(frame, new Rect(at.X, at.Y, s.ActualWidth, s.ActualHeight));
                    // The RWR clock is a WPF overlay above the surface: draw it again on top.
                    if (map.RwrCanvas.IsVisible)
                    {
                        var c = map.RwrCanvas;
                        var cAt = c.TransformToAncestor(w).Transform(new Point(0, 0));
                        var brush = new VisualBrush(c)
                        {
                            Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
                            ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, c.ActualWidth, c.ActualHeight),
                        };
                        dc.DrawRectangle(brush, null, new Rect(cAt.X, cAt.Y, c.ActualWidth, c.ActualHeight));
                    }
                }
            }
            bmp = new RenderTargetBitmap(bmp.PixelWidth, bmp.PixelHeight, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bmp.Render(visual);
        }
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(path);
        enc.Save(f);
    }
}
