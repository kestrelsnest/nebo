using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AvalonDock.Layout;
using Nebo.Client.Panels;

namespace Nebo.Client;

/// <summary>The main window: menu, status bar and the workspace tabs (GCI, ATC, more on demand).
/// A tab's ⇱ moves its workspace into a window of its own (a second screen); closing that window
/// puts it back as a tab. The main window runs the app-wide jobs and closing it closes the app;
/// on exit every workspace's layout and the windows' placement are saved to %AppData%\NEBO.</summary>
public partial class MainWindow : Window
{
    public const string MainId = "main";
    public const string GciPreset = "GCI";
    public const string AtcPreset = "ATC";
    public const string DtcPreset = "DTC";

    private readonly DispatcherTimer _expiry;
    private readonly Action _statusChanged;
    private readonly List<WorkspaceView> _tabs = [];  // in tab order, detached ones included
    private WorkspaceView? _selected;

    public bool IsClosing { get; private set; }

    /// <summary>The workspace menu commands act on: the one used last, else the selected tab.</summary>
    private WorkspaceView Current =>
        WorkspaceView.Current is { } c && _tabs.Contains(c) ? c : _selected ?? _tabs[0];

    public MainWindow()
    {
        InitializeComponent();
        _statusChanged = () => Dispatcher.BeginInvoke(() => ConnectionText.Text = AppState.Connection.Status);
        AppState.Connection.StatusChanged += _statusChanged;
        AppState.Store.Changed += UpdatePictureText;
        ConnectionText.Text = AppState.Connection.Status;
        UpdatePictureText();
        // SRS in one line (the details are in Settings → SRS).
        var srsTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        srsTimer.Tick += (_, _) => SrsText.Text = Srs.SrsStatus.Short();
        srsTimer.Start();
        Place(this, AppState.Settings.MainPlacement);
        Activated += (_, _) => { if (_selected is not null) WorkspaceView.Current = _selected; };
        Loaded += (_, _) =>
        {
            OpenTabs();
            // First start: where DCS World is (found automatically, the controller confirms or changes it).
            if (!Dev.DebugSnapshot.Active)
                Dispatcher.BeginInvoke(() => DcsLocationWindow.AskOnFirstStart(this), DispatcherPriority.ContextIdle);
            _ = ShowUpdateAsync();
        };
        _expiry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _expiry.Tick += (_, _) => AppState.Store.Expire(DateTime.UtcNow);
        _expiry.Start();
        // Developer aid, off unless the client is started with --snapshot. First: the SRS
        // services below check Dev.DebugSnapshot.Active to leave the controller's SRS app alone.
        Dev.DebugSnapshot.TryStart(this, Environment.GetCommandLineArgs().Skip(1).ToArray());
        Srs.SrsBridge.Instance.Start();  // talks to the controller's own SRS client (local UDP)
        Srs.SrsListen.Instance.Start();  // NEBO's own listen-only SRS connection, if it was on
        Srs.SrsAppListener.Instance.Start();  // the controller's SRS app's radios (local UDP 7082)
    }

    // A snapshot run must not overwrite the controller's saved layout (unless NEBO_HOME points elsewhere).
    internal static bool PersistOnExit => !Dev.DebugSnapshot.Active || Dev.DebugSnapshot.Persist;

    private void UpdatePictureText()
    {
        var s = AppState.Store;
        if (s.Latest is not { } p)
        {
            PictureText.Text = "";
            return;
        }
        var contacts = s.Tracks.Count(t => t.Unit.Detected);
        PictureText.Text = $"{s.Coalition} · {p.Theatre} · mission time {TimeSpan.FromSeconds(p.Time):hh\\:mm\\:ss} · " +
                           $"{s.Tracks.Count - contacts} own, {contacts} contacts";
    }

    // ---- Tabs -------------------------------------------------------------------------------

    /// <summary>The saved tabs; from an older version: the main window's layout as the GCI tab,
    /// its extra windows as detached tabs, and an ATC tab when there was none.</summary>
    private void OpenTabs()
    {
        var s = AppState.Settings;
        var tabs = s.Tabs.ToList();
        if (tabs.Count == 0)
        {
            tabs.Add(new WindowPlacement { Id = MainId, Preset = GciPreset });
            tabs.AddRange(s.Workspaces.Select(w => new WindowPlacement
            {
                Id = w.Id, Preset = w.Preset, Detached = true, Left = w.Left, Top = w.Top, Width = w.Width, Height = w.Height, Maximized = w.Maximized,
            }));
            if (!tabs.Any(t => t.Preset == AtcPreset)) tabs.Add(new WindowPlacement { Id = "atc" + Guid.NewGuid().ToString("N")[..6], Preset = AtcPreset });
            s.Workspaces = [];
        }
        // The DTC tab (0.13): added once to every set of tabs.
        if (!tabs.Any(t => t.Preset == DtcPreset)) tabs.Add(new WindowPlacement { Id = "dtc" + Guid.NewGuid().ToString("N")[..6], Preset = DtcPreset });
        foreach (var t in tabs)
        {
            var view = new WorkspaceView(t.Id, t.Preset);
            _tabs.Add(view);
            // A detached one is built straight in its own window.
            if (t.Detached) Detach(view, t, rebuild: false);
            else
            {
                WorkspaceHost.Children.Add(view);
                view.RestoreLayout();
            }
        }
        Select(_tabs.FirstOrDefault(v => v.DetachedWindow is null) ?? _tabs[0]);
    }

    private void RebuildStrip()
    {
        TabStrip.Children.Clear();
        foreach (var view in _tabs)
        {
            var detached = view.DetachedWindow is not null;
            var selected = view == _selected && !detached;
            var title = new TextBlock { Text = view.Title + (detached ? "  (window)" : ""), VerticalAlignment = VerticalAlignment.Center, FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal };
            var pop = new Button
            {
                Content = detached ? "⇤" : "⇱", Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(6, 0, 0, 0), Background = null, BorderThickness = new Thickness(0),
                ToolTip = detached ? "Back to a tab" : "Move into a window of its own (drag it to another screen)",
            };
            var v = view;
            pop.Click += (_, _) =>
            {
                if (v.DetachedWindow is { } w) w.Close();  // closing attaches it back
                else Detach(v, null);
            };
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(title);
            header.Children.Add(pop);
            var tab = new Border
            {
                Child = header, Padding = new Thickness(12, 4, 6, 4), Margin = new Thickness(0, 0, 2, 0), Cursor = Cursors.Hand,
                BorderThickness = new Thickness(1, 1, 1, selected ? 0 : 1), CornerRadius = new CornerRadius(3, 3, 0, 0),
            };
            tab.SetResourceReference(Border.BorderBrushProperty, "Nebo.Border");
            tab.SetResourceReference(Border.BackgroundProperty, selected ? "Nebo.PanelAlt" : "Nebo.Panel");
            tab.MouseLeftButtonUp += (_, _) =>
            {
                if (v.DetachedWindow is { } w) w.Activate();
                else Select(v);
            };
            TabStrip.Children.Add(tab);
        }
    }

    private void Select(WorkspaceView view)
    {
        _selected = view;
        WorkspaceView.Current = view;
        // Only workspaces shown as tabs hide; a detached one stays visible in its own window.
        foreach (var v in _tabs) v.Visibility = v == view || v.DetachedWindow is not null ? Visibility.Visible : Visibility.Collapsed;
        RebuildStrip();
    }

    /// <summary>The same workspace rebuilt from its layout: AvalonDock does not redraw a docking
    /// area moved into another window, so detaching and attaching save the layout and restore it
    /// in a fresh one (the panels are made anew; settings and maps' views carry over).</summary>
    private WorkspaceView Rebuild(WorkspaceView old)
    {
        foreach (var map in old.Maps) map.SaveView();
        old.SaveLayout();
        WorkspaceView.All.Remove(old);
        var fresh = new WorkspaceView(old.Id, old.Preset);
        _tabs[_tabs.IndexOf(old)] = fresh;
        if (_selected == old) _selected = fresh;
        return fresh;
    }

    /// <summary>Moves a workspace out of the tabs into a window of its own.</summary>
    private void Detach(WorkspaceView view, WindowPlacement? placement, bool rebuild = true)
    {
        var wasSelected = _selected == view;
        WorkspaceHost.Children.Remove(view);
        // At start a detached workspace is new and empty: built in its window, nothing saved first.
        var fresh = rebuild ? Rebuild(view) : view;
        placement ??= new WindowPlacement { Left = Left + 60, Top = Top + 60, Width = Math.Max(900, ActualWidth - 120), Height = Math.Max(600, ActualHeight - 120) };
        new WorkspaceWindow(fresh, placement).Show();
        fresh.RestoreLayout();
        if (wasSelected || _selected == fresh) _selected = _tabs.FirstOrDefault(v => v != fresh && v.DetachedWindow is null);
        if (_selected is { } s) Select(s); else RebuildStrip();
    }

    /// <summary>A detached workspace's window closed: back as a tab.</summary>
    public void AttachTab(WorkspaceView view)
    {
        var fresh = Rebuild(view);
        WorkspaceHost.Children.Add(fresh);
        fresh.RestoreLayout();
        Select(fresh);
    }

    private void AddTab(string preset)
    {
        var view = new WorkspaceView(preset.ToLowerInvariant() + Guid.NewGuid().ToString("N")[..6], preset);
        _tabs.Add(view);
        WorkspaceHost.Children.Add(view);
        view.BuildDefaultLayout();
        Select(view);
    }

    /// <summary>The workspace on screen: "GCI", "ATC" or "DTC".</summary>
    public string CurrentPreset => _selected?.Preset ?? GciPreset;

    /// <summary>Switches between the GCI and ATC tabs (the Stream Deck's key); opens the other one
    /// when there is none.</summary>
    public void SwitchGciAtc()
    {
        var target = CurrentPreset == AtcPreset ? GciPreset : AtcPreset;
        if (_tabs.FirstOrDefault(v => v.Preset == target && v.DetachedWindow is null) is { } view) Select(view);
        else AddTab(target);
    }

    /// <summary>Snapshot aid: detaches the first tab with that preset; returns its window.</summary>
    public Window? DetachFirst(string preset)
    {
        if (_tabs.FirstOrDefault(v => v.Preset == preset && v.DetachedWindow is null) is not { } view) return null;
        var index = _tabs.IndexOf(view);
        Detach(view, null);
        return _tabs[index].DetachedWindow;  // the rebuilt workspace's window
    }

    /// <summary>Snapshot aid: opens a tab with that preset ("GCI" or "ATC").</summary>
    public static void OpenWorkspace(string preset)
    {
        if (Application.Current.MainWindow is MainWindow main) main.AddTab(preset);
    }

    // ---- Placement --------------------------------------------------------------------------

    /// <summary>Puts a window where it was, if that is still on a screen.</summary>
    public static void Place(Window window, WindowPlacement? p)
    {
        if (p is null || p.Width < 200 || p.Height < 150) return;
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        // At least a 100 DIP strip must be visible (a screen may have been unplugged).
        var visible = Rect.Intersect(screen, new Rect(p.Left, p.Top, p.Width, p.Height));
        if (visible.IsEmpty || visible.Width < 100 || visible.Height < 100) return;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        (window.Left, window.Top, window.Width, window.Height) = (p.Left, p.Top, p.Width, p.Height);
        // Maximise once the window exists, so it maximises on its own screen.
        if (p.Maximized) window.SourceInitialized += (_, _) => window.WindowState = WindowState.Maximized;
    }

    public static WindowPlacement PlacementOf(Window w, string id, string preset, bool detached)
    {
        var b = w.WindowState == WindowState.Normal ? new Rect(w.Left, w.Top, w.Width, w.Height) : w.RestoreBounds;
        return new WindowPlacement
        {
            Id = id, Preset = preset, Detached = detached,
            Left = b.Left, Top = b.Top, Width = b.Width, Height = b.Height,
            Maximized = w.WindowState == WindowState.Maximized,
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        IsClosing = true;
        SaveLayouts();
        AppState.Connection.Disconnect();
        foreach (var v in _tabs) v.DetachedWindow?.Close();
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        AppState.Connection.StatusChanged -= _statusChanged;
        AppState.Store.Changed -= UpdatePictureText;
        base.OnClosed(e);
    }

    /// <summary>Saves every workspace's maps and layout and the windows' placement (Window → Save layout, and on exit).</summary>
    private void SaveLayouts()
    {
        var maps = _tabs.SelectMany(v => v.Maps).ToList();
        foreach (var map in maps) map.SaveView();
        // Settings of closed map windows are dropped here, not on close events: AvalonDock
        // also raises those while moving a map into a floating window.
        foreach (var id in AppState.Settings.Maps.Keys.Except(maps.Select(m => m.ContentId)).ToList())
            AppState.Settings.Maps.Remove(id);
        if (!PersistOnExit) return;
        try
        {
            Directory.CreateDirectory(AppPaths.Settings);
            foreach (var v in _tabs) v.SaveLayout();
            AppState.Settings.MainPlacement = PlacementOf(this, MainId, GciPreset, detached: false);
            AppState.Settings.Tabs = _tabs.Select(v => v.DetachedWindow is { } w ? w.Placement() : new WindowPlacement { Id = v.Id, Preset = v.Preset }).ToList();
            // Layouts of tabs closed during the session are not needed any more.
            var keep = _tabs.Select(v => Path.GetFileName(AppSettings.LayoutPathFor(v.Id))).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var old in Directory.EnumerateFiles(AppPaths.Settings, "layout-*.xml"))
                if (!keep.Contains(Path.GetFileName(old))) File.Delete(old);
            AppState.Settings.Save();
        }
        catch (IOException)
        {
            // Losing the saved layout is not worth blocking exit.
        }
    }

    // ---- Menu ---------------------------------------------------------------------------

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        var s = AppState.Settings;
        var listen = Srs.SrsListen.Instance;
        var dialog = new ConnectDialog { Owner = this };
        if (dialog.ShowDialog() != true) return;
        var server = dialog.Server;
        s.Host = server.Host;
        s.Port = server.Port;
        s.Srs.ListenPort = server.SrsPort;
        // The SRS listener and auto-EAM use the selected server's EAM password (the older
        // remembered one gives way to it).
        s.Srs.EamPassword = null;
        listen.SessionPassword = server.EamPassword;
        listen.Reconnect();
        if (PersistOnExit)
        {
            try { s.Save(); }
            catch (IOException) { /* kept in memory; saved again on exit */ }
        }
        // The password is decrypted only when the login asks for it (after Discord passed).
        await AppState.Connection.ConnectAsync(server.Host, server.Port, new Core.LoginOptions(() => server.Password,
            async (sides, user) =>
            {
                var side = await SidePickWindow.AskAsync(this, sides, user, server.LastSide);
                if (side is not null) server.LastSide = side;
                return side;
            }));
    }

    private void Disconnect_Click(object sender, RoutedEventArgs e) => AppState.Connection.Disconnect();

    private void NewGciWindow_Click(object sender, RoutedEventArgs e) => AddTab(GciPreset);
    private void NewAtcWindow_Click(object sender, RoutedEventArgs e) => AddTab(AtcPreset);
    private void NewDtcWindow_Click(object sender, RoutedEventArgs e) => AddTab(DtcPreset);

    private void DetachTab_Click(object sender, RoutedEventArgs e)
    {
        if (Current.DetachedWindow is null) Detach(Current, null);
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        var view = Current;
        if (_tabs.Count(v => v.DetachedWindow is null) <= 1 && view.DetachedWindow is null)
        {
            MessageBox.Show(this, "The last tab stays open.", "Close tab");
            return;
        }
        if (MessageBox.Show(this, $"Close the {view.Title} tab and its panels?", "Close tab", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        _tabs.Remove(view);
        WorkspaceView.All.Remove(view);
        if (view.DetachedWindow is { } w)
        {
            view.DetachedWindow = null;
            w.Content = null;
            w.Close();
        }
        WorkspaceHost.Children.Remove(view);
        Select(_tabs.First(v => v.DetachedWindow is null));
    }

    /// <summary>Adds a map window to the current workspace and activates it.</summary>
    public LayoutDocument OpenMap(BaseMap? baseMap) => Current.OpenMap(baseMap);

    /// <summary>Floats a tool panel into its own window (snapshot aid): from the current
    /// workspace, else the first that has it (the ATC tab is in the ATC workspace).</summary>
    public object? FloatPanel(string contentId) =>
        (Current.Has(contentId) ? Current : _tabs.FirstOrDefault(v => v.Has(contentId)))?.FloatPanel(contentId);

    private void ShowObjects_Click(object sender, RoutedEventArgs e) => Current.ShowAnchorable(WorkspaceView.ObjectsId, "Objects");
    private void ShowZones_Click(object sender, RoutedEventArgs e) => Current.ShowAnchorable(WorkspaceView.ZonesId, "Zones");
    private void ShowSrs_Click(object sender, RoutedEventArgs e) => Current.ShowAnchorable(WorkspaceView.SrsId, "SRS");
    private void ShowAtc_Click(object sender, RoutedEventArgs e) => Current.ShowAnchorable(WorkspaceView.AtcId, "ATC");
    private void ShowPackages_Click(object sender, RoutedEventArgs e) => Current.ShowAnchorable(WorkspaceView.PackagesId, "Packages");
    private void ShowEngagements_Click(object sender, RoutedEventArgs e) => Current.ShowAnchorable(WorkspaceView.EngagementsId, "Engagements");

    private void ShowAtcView_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string id) ShowAtcView(id);
    }

    /// <summary>Opens one of the ATC views as a floating panel of the current workspace.</summary>
    public void ShowAtcView(string id) => Current.ShowAtcView(id);

    /// <summary>The docking chrome's theme (ThemeManager) for every workspace.</summary>
    public void SetDockTheme(AvalonDock.Themes.Theme theme)
    {
        foreach (var v in WorkspaceView.All)
            if (v.Dock.Theme?.GetType() != theme.GetType()) v.Dock.Theme = theme;
    }

    /// <summary>Saves every workspace's layout, maps and placement now (as on exit).</summary>
    private void SaveLayout_Click(object sender, RoutedEventArgs e)
    {
        SaveLayouts();
        ConnectionText.Text = $"{AppState.Connection.Status} · layout saved {DateTime.Now:HH:mm:ss}";
    }

    /// <summary>Brings the SRS tab to the front (Listen traffic) in the current workspace.</summary>
    public void ShowSrs() => Current.ShowAnchorable(WorkspaceView.SrsId, "SRS");

    /// <summary>The main window (all workspaces live in it or its detached windows).</summary>
    public static MainWindow? Of(DependencyObject element) => Application.Current.MainWindow as MainWindow;

    /// <summary>Brings the Packages panel to the front where it is docked (snapshot aid).</summary>
    public object? ShowPackages() => Current.ShowPackages();
    /// <summary>Brings the Engagements panel to the front (snapshot aid).</summary>
    public object? ShowEngagements() => Current.ShowEngagements();

    /// <summary>Picks the airport-diagram folder for the theatre on the server (Settings → General).</summary>
    public static void ChooseDiagrams(Window owner)
    {
        if (AppState.Store.Theatre is not { } theatre)
        {
            MessageBox.Show(owner, "Connect to a server first: the folder is set for the theatre it runs.", "Map data");
            return;
        }
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = $"Airfield diagrams for {theatre.Id} (folder with diagrams.json)" };
        if (dialog.ShowDialog(owner) != true) return;
        if (!File.Exists(Path.Combine(dialog.FolderName, "diagrams.json")))
        {
            MessageBox.Show(owner, "That folder has no diagrams.json. Run tools\\DiagramIndexer on it first.", "Map data");
            return;
        }
        AppState.Settings.Diagrams[theatre.Id] = dialog.FolderName;
        AppState.Settings.Save();
        Map.Airfields.AirfieldDiagrams.Forget(dialog.FolderName);
        Map.Airfields.ChartLabels.Forget(dialog.FolderName);
        AppState.Store.SetAirfields(AppState.Store.Airfields);  // maps redraw
    }

    private void ResetLayout_Click(object sender, RoutedEventArgs e) => Current.ResetLayout();

    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsWindow.Open(this);

    private void SrsText_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) => SettingsWindow.Open(this, "SRS");

    private void About_Click(object sender, RoutedEventArgs e) => AboutWindow.Open(this);

    // ---- New-version notice (Updates.cs) ----
    private Updates.Release? _update;

    private async Task ShowUpdateAsync()
    {
        if (await Updates.CheckAsync() is not { } release) return;
        _update = release;
        UpdateText.Text = $"NEBO {release.Version} is out (you have {AboutWindow.Version}).";
        UpdateInstall.Content = release.SetupUrl.Length > 0 ? "Download and install" : "Open the download page";
        UpdateBar.Visibility = Visibility.Visible;
    }

    private async void UpdateInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_update is not { } release) return;
        if (release.SetupUrl.Length == 0) { AboutWindow.OpenUrl(release.PageUrl); return; }
        UpdateInstall.IsEnabled = false;
        try
        {
            await Updates.DownloadAndStartAsync(release, new Progress<string>(text => UpdateText.Text = text));
            Close();  // the setup replaces the client: exit the usual way (layout and settings saved)
        }
        catch (Exception ex)
        {
            UpdateText.Text = $"The download failed ({ex.Message}). Try again, or get it from the Releases page.";
            UpdateInstall.IsEnabled = true;
        }
    }

    private void UpdateLater_Click(object sender, RoutedEventArgs e) => UpdateBar.Visibility = Visibility.Collapsed;

    private void OpenLink_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url }) AboutWindow.OpenUrl(url);
    }
}
