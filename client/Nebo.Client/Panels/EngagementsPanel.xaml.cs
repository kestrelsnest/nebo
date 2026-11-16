using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Nebo.Client.Core;

namespace Nebo.Client.Panels;

/// <summary>One row of the Engagements panel: an engagement line (editable: marker, alert
/// distance, intercept point) or a launch at one of the side's players. Rows are updated in place,
/// so an edit in progress is not disturbed by the once-a-second refresh.</summary>
public sealed class EngagementItem(string key) : INotifyPropertyChanged
{
    public static IReadOnlyList<string> MarkerChoices { get; } = ["", .. Enumerable.Range(1, EngagementBook.Markers).Select(n => n.ToString())];

    public string Key { get; } = key;
    public int? LineId { get; private set; }
    public long? TargetId { get; private set; }
    public bool IsLaunch { get; private set; }
    public bool IsLine => LineId is not null;
    public string From { get; private set; } = "";
    public string To { get; private set; } = "";
    public string Range { get; private set; } = "";
    public string Bra { get; private set; } = "";
    public string Closure { get; private set; } = "";
    public string Icpt { get; private set; } = "";
    public bool Inside { get; private set; }

    private string _marker = "";
    public string Marker
    {
        get => _marker;
        set
        {
            if (value == _marker || LineId is not { } id) return;
            AppState.Engagements.SetMarker(id, int.TryParse(value, out var n) ? n : null);
        }
    }

    private string _alert = "";
    public string Alert
    {
        get => _alert;
        set
        {
            if (LineId is not { } id) return;
            var text = (value ?? "").Trim().Replace(',', '.');
            double? nm = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? v : null;
            AppState.Engagements.SetAlert(id, nm);
            _alert = nm is { } x ? x.ToString("0.#", CultureInfo.InvariantCulture) : "";
            Changed();
        }
    }

    private bool _showIntercept = true;
    public bool ShowIntercept
    {
        get => _showIntercept;
        set { if (LineId is { } id) AppState.Engagements.SetShowIntercept(id, value); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(EngagementRow r, MagVarGrid? magvar)
    {
        if (LineId != r.Line?.Id)
        {
            LineId = r.Line?.Id;
            Changed(nameof(IsLine));
        }
        TargetId = r.Launch?.Target ?? r.Line?.To.TrackId;
        if (IsLaunch != r.IsLaunch) { IsLaunch = r.IsLaunch; Changed(nameof(IsLaunch)); }
        SetText(From, r.From, v => From = v, nameof(From));
        SetText(To, r.To, v => To = v, nameof(To));
        SetText(Range, r.RangeNm.ToString("0.0", CultureInfo.InvariantCulture), v => Range = v, nameof(Range));
        var brg = magvar is null ? Nav.Bearing3(r.BearingTrue) + "T" : Nav.Bearing3(Nav.Magnetic(r.BearingTrue, MagAt(r, magvar)));
        SetText(Bra, $"{brg}/{r.RangeNm:0}", v => Bra = v, nameof(Bra));
        SetText(Closure, r.ClosureKt is { } c ? $"{c:+0;-0;0} kt" : "", v => Closure = v, nameof(Closure));
        string icpt = "";
        if (r.IsLaunch && r.ClosureKt is > 10 and var ck) icpt = Mmss(r.RangeNm / ck * 3600) + " TTI";
        else if (r.Intercept is { } s)
        {
            var h = magvar is null ? Nav.Bearing3(s.HeadingTrue) + "T" : Nav.Bearing3(Nav.Magnetic(s.HeadingTrue, MagAt(r, magvar)));
            icpt = $"{Mmss(s.Seconds)}  {h}";
        }
        else if (r.Line is not null && r.ClosureKt is not null) icpt = "NO ICPT";
        SetText(Icpt, icpt, v => Icpt = v, nameof(Icpt));
        if (Inside != r.Inside) { Inside = r.Inside; Changed(nameof(Inside)); }
        var marker = r.Line?.Marker?.ToString() ?? "";
        if (marker != _marker) { _marker = marker; Changed(nameof(Marker)); }
        var alert = r.Line?.AlertNm is { } a ? a.ToString("0.#", CultureInfo.InvariantCulture) : "";
        if (alert != _alert) { _alert = alert; Changed(nameof(Alert)); }
        var show = r.Line?.ShowIntercept ?? false;
        if (show != _showIntercept) { _showIntercept = show; Changed(nameof(ShowIntercept)); }
    }

    // Magnetic variation where the line starts (its first end), as the BRAA ruler gives it.
    private static double MagAt(EngagementRow r, MagVarGrid magvar)
    {
        var (lat, lon, _) = r.Line is { } l ? EngagementBook.Resolve(l.From, id => AppState.Store.Find(id)?.Unit) : (0, 0, null);
        return magvar.At(lat, lon);
    }

    private static string Mmss(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    private void SetText(string current, string value, Action<string> assign, string name)
    {
        if (current == value) return;
        assign(value);
        Changed(name);
    }

    private void Changed([CallerMemberName] string name = "") => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>The engagement list: the side's launches at its players on top, then the
/// controller's engagement lines (pinned BRAA lines), each sorted by range or, with an alert
/// distance set, by the range left to it. Refreshed once a second.</summary>
public partial class EngagementsPanel : UserControl
{
    private readonly ObservableCollection<EngagementItem> _rows = [];
    private readonly Dictionary<string, EngagementItem> _byKey = [];
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    public EngagementsPanel()
    {
        InitializeComponent();
        Grid.ItemsSource = _rows;
        Grid.PreviewMouseRightButtonUp += Grid_RightClick;
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) =>
        {
            AppState.Engagements.Changed += Refresh;
            _timer.Start();
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            AppState.Engagements.Changed -= Refresh;
            _timer.Stop();
        };
    }

    private void Refresh()
    {
        var store = AppState.Store;
        var rows = AppState.Engagements.Rows(store);
        var magvar = store.Theatre?.MagVar;
        var wanted = new List<EngagementItem>();
        foreach (var r in rows)
        {
            var key = r.Line is { } l ? $"L{l.Id}" : $"M{r.Launch!.Missile}";
            if (!_byKey.TryGetValue(key, out var item)) _byKey[key] = item = new EngagementItem(key);
            item.Update(r, magvar);
            wanted.Add(item);
        }
        var keep = wanted.ToHashSet();
        foreach (var k in _byKey.Where(kv => !keep.Contains(kv.Value)).Select(kv => kv.Key).ToList()) _byKey.Remove(k);
        for (int i = _rows.Count - 1; i >= 0; i--)
            if (!keep.Contains(_rows[i])) _rows.RemoveAt(i);
        for (int i = 0; i < wanted.Count; i++)
        {
            var at = i < _rows.Count && ReferenceEquals(_rows[i], wanted[i]) ? i : _rows.IndexOf(wanted[i]);
            if (at < 0) _rows.Insert(i, wanted[i]);
            else if (at != i) _rows.Move(at, i);
        }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EngagementItem { LineId: { } id }) AppState.Engagements.Remove(id);
    }

    /// <summary>Right-click an engagement line's row: remove it (or all of them). Launch rows have
    /// nothing to remove; they go when the missile does.</summary>
    private void Grid_RightClick(object sender, MouseButtonEventArgs e)
    {
        var row = e.OriginalSource is DependencyObject d ? FindRow(d) : null;
        if (row?.Item is not EngagementItem { LineId: { } id } item) return;
        Grid.SelectedItem = item;
        var book = AppState.Engagements;
        var menu = new ContextMenu { PlacementTarget = row };
        var one = new MenuItem { Header = $"Remove engagement {item.From} → {item.To}" };
        one.Click += (_, _) => book.Remove(id);
        menu.Items.Add(one);
        if (book.Lines.Count > 1)
        {
            var all = new MenuItem { Header = $"Remove all {book.Lines.Count} engagement lines" };
            all.Click += (_, _) => book.Clear();
            menu.Items.Add(all);
        }
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static DataGridRow? FindRow(DependencyObject? d)
    {
        while (d is not null and not DataGridRow)
            d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        return d as DataGridRow;
    }

    private void RemoveAll_Click(object sender, RoutedEventArgs e) => AppState.Engagements.Clear();

    private void TestAlert_Click(object sender, RoutedEventArgs e) => EngagementAlert.Test();

    /// <summary>Enter in an alert box applies it (as leaving the box does).</summary>
    private void Alert_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        e.Handled = true;
    }

    /// <summary>Double-click a row: the map last used shows both ends of the line, or centres on
    /// a launch's target.</summary>
    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Grid.SelectedItem is EngagementItem { LineId: { } lid } && AppState.Engagements.Find(lid) is { } line) AppState.ActiveMap?.FitEngagement(line);
        else if (Grid.SelectedItem is EngagementItem { TargetId: { } id }) AppState.ActiveMap?.CenterOn(id);
    }
}
