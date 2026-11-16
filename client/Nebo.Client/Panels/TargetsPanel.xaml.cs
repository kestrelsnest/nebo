using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Nebo.Client.Core;
using Nebo.Client.Map.Gpu;

namespace Nebo.Client.Panels;

/// <summary>One target zone under one of its flights (a zone given to two flights shows under both).</summary>
public sealed record TargetZoneRow(string Flight, TargetZone Zone, Brush Brush)
{
    public int Id => Zone.Id;
    public string Order => Zone.Order > 0 ? Zone.Order.ToString() : "";
    public string Name => Zone.Name;
    public string Progress => Zone.Progress;
    public int Damaged => Zone.Damaged;
    public int Count => Zone.Items.Count;
    public string What => Zone.What switch { "scenery" => "buildings", "all" => "units + buildings", _ => "units" };
    public string Status => Zone.Done ? "done ✓" : Zone.Dead > 0 || Zone.Damaged > 0 ? "in progress" : "untouched";
}

public sealed record TargetItemRow(TargetItem Item, Brush Brush)
{
    public string Name => Item.Kind == "scenery" ? $"map object {Item.Name}" : Item.ShortName;
    public string? Type => Item.Type;
    public string Kind => Item.Kind switch { "scenery" => "building", _ => Item.Kind };
    public string State => Item.State;
    public string When => Item.Time is { } t ? TimeSpan.FromSeconds(t).ToString(@"h\:mm\:ss") : "";
}

/// <summary>Training missions (EventTargets): the side's target zones grouped by flight, in task
/// order, with progress; the selected zone's targets below. Selection highlights the zone on
/// every map; double-click centres the map last used.</summary>
public partial class TargetsPanel : UserControl
{
    public TargetsPanel()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            AppState.Store.TargetsChanged += Refresh;
            AppState.TargetSelectionChanged += OnSelectionChanged;
            Themes.ThemeManager.Changed += Refresh;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            AppState.Store.TargetsChanged -= Refresh;
            AppState.TargetSelectionChanged -= OnSelectionChanged;
            Themes.ThemeManager.Changed -= Refresh;
        };
    }

    private bool _refreshing;

    private static Brush BrushOf(SkiaSharp.SKColor c)
    {
        var b = new SolidColorBrush(Color.FromArgb(255, c.Red, c.Green, c.Blue));
        b.Freeze();
        return b;
    }

    private void Refresh()
    {
        var targets = AppState.Store.Targets;
        var any = targets is { Zones.Count: > 0 };
        EmptyText.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        Body.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        if (!any)
        {
            SummaryText.Text = "";
            ZoneGrid.ItemsSource = null;
            ItemGrid.ItemsSource = null;
            return;
        }
        _refreshing = true;
        var rows = targets!.ByFlight()
            .SelectMany(g => g.Zones.Select(z => new TargetZoneRow(g.Flight, z, BrushOf(TargetsSk.InkFor(targets, z)))))
            .Where(r => (DoneBox.IsChecked == true || !r.Zone.Done) && (UnassignedBox.IsChecked == true || r.Zone.Assigned))
            .ToList();
        var view = new ListCollectionView(rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TargetZoneRow.Flight)));
        ZoneGrid.ItemsSource = view;
        ZoneGrid.SelectedItem = rows.FirstOrDefault(r => r.Id == AppState.SelectedTargetZone);
        _refreshing = false;
        ShowItems();
        int dead = targets.Zones.Sum(z => z.Dead), total = targets.Zones.Sum(z => z.Items.Count);
        SummaryText.Text = $"{targets.Zones.Count(z => z.Done)}/{targets.Zones.Count} zones done · {dead}/{total} targets destroyed";
    }

    private void ShowItems()
    {
        var zone = (ZoneGrid.SelectedItem as TargetZoneRow)?.Zone;
        if (zone is null || AppState.Store.Targets is not { } targets) { ItemGrid.ItemsSource = null; return; }
        var ink = BrushOf(TargetsSk.InkFor(targets, zone));
        Brush dead = Themes.ThemeBrush.Get("Nebo.Hostile"), damaged = Brushes.Goldenrod;
        ItemGrid.ItemsSource = zone.Items
            .OrderBy(i => i.Dead).ThenBy(i => i.Kind).ThenBy(i => i.Name)
            .Select(i => new TargetItemRow(i, i.Dead ? dead : i.Damaged ? damaged : ink)).ToList();
    }

    private void OnSelectionChanged()
    {
        if (ZoneGrid.ItemsSource is not ICollectionView view) return;
        var row = view.Cast<TargetZoneRow>().FirstOrDefault(r => r.Id == AppState.SelectedTargetZone);
        if (!ReferenceEquals(ZoneGrid.SelectedItem, row) && (row is not null || ZoneGrid.SelectedItem is not null)) ZoneGrid.SelectedItem = row;
    }

    private void Filter_Click(object sender, RoutedEventArgs e) => Refresh();

    private void ZoneGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ShowItems();
        if (!_refreshing) AppState.SelectTargetZone((ZoneGrid.SelectedItem as TargetZoneRow)?.Id);
    }

    private void ZoneGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ZoneGrid.SelectedItem is TargetZoneRow row) AppState.ActiveMap?.CenterOnTargetZone(row.Id);
    }
}
