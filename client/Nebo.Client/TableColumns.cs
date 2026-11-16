using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Nebo.Client;

/// <summary>A table column as the controller left it: width (null: as designed), hidden, order.</summary>
public sealed class ColumnState
{
    public double? Width { get; set; }
    public bool Hidden { get; set; }
    public int Index { get; set; } = -1;
}

/// <summary>Every table (DataGrid) of the client: right-click a column header for a menu that
/// hides and shows columns (Show all, Reset); columns are resized by dragging a header's edge and
/// moved by dragging a header. Widths, hidden columns and order are kept per table in the settings
/// (key: the panel or window type and the table's name) and restored when the table opens.</summary>
public static class TableColumns
{
    private static readonly ConditionalWeakTable<DataGrid, Defaults> Seen = new();

    /// <summary>The columns as the table was designed (for Reset).</summary>
    private sealed class Defaults(List<(DataGridColumn Column, DataGridLength Width, int Index)> columns)
    {
        public List<(DataGridColumn Column, DataGridLength Width, int Index)> Columns { get; } = columns;
    }

    public static void Start()
    {
        EventManager.RegisterClassHandler(typeof(DataGrid), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnLoaded));
        EventManager.RegisterClassHandler(typeof(DataGrid), UIElement.PreviewMouseRightButtonUpEvent, new MouseButtonEventHandler(OnRightUp));
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not DataGrid grid || Seen.TryGetValue(grid, out _) || KeyOf(grid) is not { } key) return;
        Seen.Add(grid, new Defaults(grid.Columns.Select(c => (c, c.Width, c.DisplayIndex)).ToList()));
        grid.CanUserResizeColumns = true;
        grid.CanUserReorderColumns = true;
        Restore(grid, key);
        grid.ColumnReordered += (_, _) => Save(grid, key);
        // A resize ends with the button released over the header row.
        grid.PreviewMouseLeftButtonUp += (_, _) => grid.Dispatcher.BeginInvoke(() => Save(grid, key));
        grid.Unloaded += (_, _) => Save(grid, key);
    }

    /// <summary>"ObjectsPanel.Grid": the nearest panel or window type and the table's name.</summary>
    private static string? KeyOf(DataGrid grid)
    {
        for (DependencyObject? d = grid; d is not null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is UserControl or Window)
                return $"{d.GetType().Name}.{(grid.Name.Length > 0 ? grid.Name : "table")}";
        return null;
    }

    private static string ColumnKey(DataGridColumn c, int i) => c.Header switch
    {
        string s when s.Length > 0 => s,
        TextBlock { Text.Length: > 0 } t => t.Text,
        _ => c.SortMemberPath is { Length: > 0 } p ? p : $"#{i}",
    };

    private static string ColumnTitle(DataGridColumn c, int i) => c.Header switch
    {
        string s when s.Length > 0 => s,
        TextBlock { Text.Length: > 0 } t => t.Text,
        _ => c.SortMemberPath is { Length: > 0 } p ? p : $"Column {i + 1}",
    };

    private static void Restore(DataGrid grid, string key)
    {
        if (!AppState.Settings.Tables.TryGetValue(key, out var saved)) return;
        var cols = grid.Columns.Select((c, i) => (Column: c, Key: ColumnKey(c, i))).ToList();
        foreach (var (c, k) in cols)
        {
            if (!saved.TryGetValue(k, out var s)) continue;
            c.Visibility = s.Hidden ? Visibility.Collapsed : Visibility.Visible;
            if (s.Width is { } w and > 8) c.Width = new DataGridLength(w);
        }
        // Order: by the saved index, columns not saved keep their place after them.
        var ordered = cols.OrderBy(x => saved.TryGetValue(x.Key, out var s) && s.Index >= 0 ? s.Index : 1000 + x.Column.DisplayIndex).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            try { ordered[i].Column.DisplayIndex = i; }
            catch (ArgumentOutOfRangeException) { /* frozen columns: keep the designed order */ }
        }
        if (grid.Columns.All(c => c.Visibility != Visibility.Visible) && grid.Columns.Count > 0) grid.Columns[0].Visibility = Visibility.Visible;
    }

    private static void Save(DataGrid grid, string key)
    {
        var state = new Dictionary<string, ColumnState>();
        for (var i = 0; i < grid.Columns.Count; i++)
        {
            var c = grid.Columns[i];
            state[ColumnKey(c, i)] = new ColumnState
            {
                Hidden = c.Visibility != Visibility.Visible,
                Width = c.Visibility == Visibility.Visible && c.ActualWidth > 8 ? Math.Round(c.ActualWidth) : (c.Width.IsAbsolute ? c.Width.Value : null),
                Index = c.DisplayIndex,
            };
        }
        AppState.Settings.Tables[key] = state;
    }

    /// <summary>A right-click on a column header: the columns menu (rows keep their own menus).</summary>
    private static void OnRightUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not DataGrid grid || KeyOf(grid) is not { } key) return;
        DependencyObject? d = e.OriginalSource as DependencyObject;
        while (d is not null and not DataGridColumnHeader and not DataGridRow) d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        if (d is not DataGridColumnHeader) return;
        e.Handled = true;  // not the table's own menu
        Menu(grid, key).IsOpen = true;
    }

    private static ContextMenu Menu(DataGrid grid, string key)
    {
        var menu = new ContextMenu { PlacementTarget = grid, Placement = PlacementMode.MousePoint };
        menu.Items.Add(new MenuItem { Header = "Columns (drag a header's edge to resize, a header to move)", IsEnabled = false });
        foreach (var (c, i) in grid.Columns.Select((c, i) => (c, i)).OrderBy(x => x.c.DisplayIndex))
        {
            var item = new MenuItem { Header = ColumnTitle(c, i), IsCheckable = true, IsChecked = c.Visibility == Visibility.Visible, StaysOpenOnClick = true };
            item.Click += (_, _) =>
            {
                var showing = grid.Columns.Count(x => x.Visibility == Visibility.Visible);
                if (!item.IsChecked && showing <= 1) { item.IsChecked = true; return; }  // one column always stays
                c.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                Save(grid, key);
            };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var all = new MenuItem { Header = "Show all columns" };
        all.Click += (_, _) =>
        {
            foreach (var c in grid.Columns) c.Visibility = Visibility.Visible;
            Save(grid, key);
        };
        menu.Items.Add(all);
        var reset = new MenuItem { Header = "Reset columns (as designed)" };
        reset.Click += (_, _) =>
        {
            if (Seen.TryGetValue(grid, out var defaults))
                foreach (var (c, width, index) in defaults.Columns.OrderBy(x => x.Index))
                {
                    c.Visibility = Visibility.Visible;
                    c.Width = width;
                    try { c.DisplayIndex = index; } catch (ArgumentOutOfRangeException) { }
                }
            AppState.Settings.Tables.Remove(key);
        };
        menu.Items.Add(reset);
        return menu;
    }
}
