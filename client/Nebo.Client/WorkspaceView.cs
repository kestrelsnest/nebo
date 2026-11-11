using System.IO;
using System.Windows;
using System.Windows.Controls;
using AvalonDock;
using AvalonDock.Layout;
using AvalonDock.Serializer.Xml;
using Nebo.Client.Panels;

namespace Nebo.Client;

/// <summary>One workspace — a tab of the main window (GCI or ATC), or detached into its own
/// window: a docking area where every panel can be docked, tabbed or floated, its layout kept in
/// %AppData%\NEBO. All workspaces share the connection, picture, selection and settings.</summary>
public sealed class WorkspaceView : ContentControl
{
    public const string ObjectsId = "objects";
    public const string ZonesId = "zones";
    public const string SrsId = "srs";
    public const string AtcId = "atc";
    public const string PackagesId = "packages";
    public const string EngagementsId = "engagements";
    public const string DtcId = "dtc";
    private const string MapPrefix = "map:";

    public static readonly List<WorkspaceView> All = [];

    /// <summary>The workspace the controller used last (menu commands act on it).</summary>
    public static WorkspaceView? Current { get; set; }

    public string Id { get; }
    /// <summary>"GCI" or "ATC".</summary>
    public string Preset { get; }
    public DockingManager Dock { get; } = new();
    public bool IsAtc => Preset == MainWindow.AtcPreset;
    public bool IsDtc => Preset == MainWindow.DtcPreset;
    public string Title => Preset;

    /// <summary>Where it stands when detached (null: a tab of the main window).</summary>
    public WorkspaceWindow? DetachedWindow { get; set; }

    public WorkspaceView(string id, string preset)
    {
        Id = id;
        Preset = preset is MainWindow.AtcPreset or MainWindow.DtcPreset ? preset : MainWindow.GciPreset;
        Content = Dock;
        All.Add(this);
        Dock.GotKeyboardFocus += (_, _) => Current = this;
        Dock.PreviewMouseDown += (_, _) => Current = this;
        Themes.ThemeManager.ApplyDockTheme(Dock);
        HideMapTabs();
    }

    private static bool _tabsHidden;

    /// <summary>One map per mode, without a tab row: when a document pane loads, its tab strip and
    /// its drop-down button are collapsed (AvalonDock's own style is kept for the content).</summary>
    private static void HideMapTabs()
    {
        if (_tabsHidden) return;
        _tabsHidden = true;
        EventManager.RegisterClassHandler(typeof(AvalonDock.Controls.LayoutDocumentPaneControl), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                foreach (var e in Descendants((DependencyObject)sender))
                    if (e is AvalonDock.Controls.DocumentPaneTabPanel or AvalonDock.Controls.DropDownButton) e.Visibility = Visibility.Collapsed;
            }));
    }

    private static IEnumerable<FrameworkElement> Descendants(DependencyObject root)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe) yield return fe;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    // ---- Layout -------------------------------------------------------------------------

    public void RestoreLayout()
    {
        var path = AppSettings.LayoutPathFor(Id);
        if (File.Exists(path))
        {
            try
            {
                var serializer = new XmlLayoutSerializer(Dock);
                // Unknown ids get no content and are dropped by the serializer's default handling.
                serializer.LayoutSerializationCallback += (_, e) => e.Content = CreateContent(e.Model.ContentId);
                serializer.Deserialize(path);
                if (Dock.Layout.Descendents().OfType<LayoutContent>().Any())
                {
                    if (!IsAtc && !IsDtc) FitGciLayout();
                    OneMap();
                    return;
                }
            }
            catch (Exception)
            {
                // Unreadable or incompatible layout file: fall through to the default layout.
            }
        }
        BuildDefaultLayout();
    }

    public void SaveLayout() => new XmlLayoutSerializer(Dock).Serialize(AppSettings.LayoutPathFor(Id));

    private object? CreateContent(string? contentId) => contentId switch
    {
        ObjectsId => new ObjectsPanel(),
        ZonesId => new ZonesPanel(),
        SrsId => new SrsPanel(),
        AtcId => new AtcPanel(),
        PackagesId => new PackagesPanel(),
        EngagementsId => new EngagementsPanel(),
        DtcId => new DtcPanel(),
        AtcViewPanel.Diagram or AtcViewPanel.Approach or AtcViewPanel.Carrier or AtcViewPanel.Lso => new AtcViewPanel(contentId),
        not null when contentId.StartsWith(MapPrefix) => new MapPanel(contentId) { WorkspacePreset = Preset },
        _ => null,
    };

    /// <summary>One map per mode (no map tabs): a saved layout keeps its first map; with none, one is added.</summary>
    private void OneMap()
    {
        var docs = Dock.Layout.Descendents().OfType<LayoutDocument>().ToList();
        foreach (var extra in docs.Skip(1))
        {
            AppState.Settings.Maps.Remove(extra.ContentId);
            extra.Parent?.RemoveChild(extra);
        }
        if (docs.Count == 0) OpenMap(IsAtc ? BaseMap.DcsMap : null);
        else docs[0].CanClose = false;
    }

    /// <summary>GCI workspaces need no ATC tab, but the airfield list for the SRS position, which
    /// the SRS tab has: a saved layout loses its ATC tab and gains SRS if missing.</summary>
    private void FitGciLayout()
    {
        foreach (var atc in Dock.Layout.Descendents().OfType<LayoutAnchorable>().Where(a => a.ContentId == AtcId).ToList())
            atc.Parent?.RemoveChild(atc);
        if (Dock.Layout.Descendents().OfType<LayoutAnchorable>().Any(a => a.ContentId == SrsId)) return;
        var pane = Dock.Layout.Descendents().OfType<LayoutAnchorablePane>().FirstOrDefault(p => p.Children.Any(c => c.ContentId == ObjectsId))
                   ?? Dock.Layout.Descendents().OfType<LayoutAnchorablePane>().FirstOrDefault();
        if (pane is not null) pane.Children.Add(Anchorable(SrsId, "SRS"));
    }

    public void BuildDefaultLayout()
    {
        if (IsDtc)
        {
            // DTC: the map (waypoints, routes) with the cartridge editor beside it.
            var dtcMap = new LayoutDocumentPaneGroup(new LayoutDocumentPane(MapDocument(null, 1)));
            var editor = new LayoutAnchorablePane(Anchorable(DtcId, "DTC")) { DockWidth = new GridLength(520) };
            Dock.Layout = new LayoutRoot { RootPanel = new LayoutPanel(dtcMap) };
            Dock.Layout.RootPanel.Children.Add(editor);
            return;
        }
        if (IsAtc)
        {
            // ATC tab large on the left with SRS under it; a DCS Map beside them.
            var atc = new LayoutAnchorablePane(Anchorable(AtcId, "ATC")) { DockHeight = new GridLength(3, GridUnitType.Star) };
            var srs = new LayoutAnchorablePane(Anchorable(SrsId, "SRS")) { DockHeight = new GridLength(1, GridUnitType.Star) };
            srs.Children.Add(Anchorable(PackagesId, "Packages"));
            // A fixed width: AvalonDock lets the document area take all star space.
            var left = new LayoutAnchorablePaneGroup(atc) { Orientation = Orientation.Vertical, DockWidth = new GridLength(700) };
            left.Children.Add(srs);
            var map = new LayoutDocumentPaneGroup(new LayoutDocumentPane(MapDocument(BaseMap.DcsMap, 1)));
            Dock.Layout = new LayoutRoot { RootPanel = new LayoutPanel(left) };
            Dock.Layout.RootPanel.Children.Add(map);
            return;
        }
        // GCI: a large map, the track and zone lists, and SRS under them (its list sets the
        // radio position). No ATC tab: ATC has its own workspace.
        var documents = new LayoutDocumentPane(MapDocument(null, 1));
        var objects = new LayoutAnchorablePane(Anchorable(ObjectsId, "Objects")) { DockHeight = new GridLength(1, GridUnitType.Star) };
        objects.Children.Add(Anchorable(ZonesId, "Zones"));
        objects.Children.Add(Anchorable(PackagesId, "Packages"));
        objects.Children.Add(Anchorable(EngagementsId, "Engagements"));
        var tools = new LayoutAnchorablePane(Anchorable(SrsId, "SRS")) { DockHeight = new GridLength(1, GridUnitType.Star) };
        var side = new LayoutAnchorablePaneGroup(objects) { Orientation = Orientation.Vertical, DockWidth = new GridLength(420) };
        side.Children.Add(tools);
        Dock.Layout = new LayoutRoot { RootPanel = new LayoutPanel(new LayoutDocumentPaneGroup(documents)) };
        Dock.Layout.RootPanel.Children.Add(side);
    }

    private LayoutDocument MapDocument(BaseMap? baseMap, int number)
    {
        var id = MapPrefix + Guid.NewGuid().ToString("N");
        if (baseMap is { } b) AppState.Settings.MapFor(id).BaseMap = b;
        return new LayoutDocument { Title = $"Map {number}", ContentId = id, Content = CreateContent(id), CanClose = false };
    }

    private LayoutAnchorable Anchorable(string id, string title) => new()
    {
        ContentId = id,
        Title = title,
        Content = CreateContent(id),
        CanClose = false,
        // A floated panel takes its docked size; the ATC tab needs room for its airfield map.
        FloatingWidth = id == AtcId ? 1000 : 500,
        FloatingHeight = id == AtcId ? 850 : 500,
    };

    public void ShowAnchorable(string id, string title)
    {
        var existing = Dock.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(a => a.ContentId == id);
        if (existing is null)
        {
            existing = Anchorable(id, title);
            existing.AddToLayout(Dock, AnchorableShowStrategy.Right);
        }
        existing.Show();
        existing.IsActive = true;
    }

    public IEnumerable<MapPanel> Maps => Dock.Layout.Descendents().OfType<LayoutContent>().Select(c => c.Content).OfType<MapPanel>();

    public bool Has(string contentId) => Dock.Layout.Descendents().OfType<LayoutAnchorable>().Any(a => a.ContentId == contentId);

    /// <summary>Adds a map window to the document area and activates it.</summary>
    public LayoutDocument OpenMap(BaseMap? baseMap)
    {
        var pane = Dock.Layout.Descendents().OfType<LayoutDocumentPane>().FirstOrDefault();
        if (pane is null)
        {
            pane = new LayoutDocumentPane();
            Dock.Layout.RootPanel.Children.Insert(0, new LayoutDocumentPaneGroup(pane));
        }
        var doc = MapDocument(baseMap, Dock.Layout.Descendents().OfType<LayoutDocument>().Count() + 1);
        pane.Children.Add(doc);
        doc.IsActive = true;
        return doc;
    }

    /// <summary>Floats a tool panel into its own window (snapshot aid); returns its content.</summary>
    public object? FloatPanel(string contentId)
    {
        var anchorable = Dock.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(a => a.ContentId == contentId);
        if (anchorable is null) return null;
        anchorable.Show();
        anchorable.IsSelected = true;  // an unselected tab floats without its content
        if (contentId == AtcId) (anchorable.FloatingWidth, anchorable.FloatingHeight) = (1000, 850);
        anchorable.Float();
        return anchorable.Content;
    }

    /// <summary>Opens one of the ATC views (AtcViewPanel ids) as a panel of its own: a new one
    /// floats in its own window, so several can be seen at once; it can be docked anywhere.</summary>
    public void ShowAtcView(string id)
    {
        if (Dock.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(a => a.ContentId == id) is { } existing)
        {
            existing.Show();
            existing.IsActive = true;
            return;
        }
        var view = Anchorable(id, AtcViewPanel.TitleOf(id));
        (view.FloatingWidth, view.FloatingHeight) = (900, 700);
        view.CanClose = true;
        view.AddToLayout(Dock, AnchorableShowStrategy.Right);
        view.Float();
    }

    public object? ShowPackages()
    {
        ShowAnchorable(PackagesId, "Packages");
        return Dock.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(a => a.ContentId == PackagesId)?.Content;
    }

    public object? ShowEngagements()
    {
        ShowAnchorable(EngagementsId, "Engagements");
        return Dock.Layout.Descendents().OfType<LayoutAnchorable>().FirstOrDefault(a => a.ContentId == EngagementsId)?.Content;
    }

    public void ResetLayout()
    {
        foreach (var map in Maps.ToList()) AppState.Settings.Maps.Remove(map.ContentId);
        BuildDefaultLayout();
    }
}
