using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Nebo.Client.Core;
using Nebo.Client.Map;
using Nebo.Client.Srs;

namespace Nebo.Client.Panels;

/// <summary>SRS tab: where the controller's radio stands (one line; the list is in Settings →
/// SRS), the controller's ten channels (frequency, transmit, who of the own side is on each — from
/// NEBO's listen connection; Save as default keeps them for every SRS start), and the radios of a
/// player picked with "Listen traffic", each of which can be put on a free channel. The SRS
/// connection itself follows the NEBO connection (port and EAM password in the connect dialog);
/// its status is on the main window's status bar.</summary>
public partial class SrsPanel : UserControl
{
    private static readonly Brush AtcBrush = Frozen(Color.FromRgb(0x4C, 0xB8, 0x64));
    private static readonly Brush TxBrush = Frozen(Color.FromRgb(0xFF, 0x70, 0x40));
    private static readonly Brush SendingBrush = Frozen(Color.FromRgb(0xE0, 0x20, 0x20));
    private static Brush TalkBrush => Themes.ThemeBrush.Get("Nebo.Warn");

    private sealed class ChannelRow
    {
        public required int Channel;
        public required TextBox Box;
        public required TextBlock Role;
        public required Border RoleBadge;
        public required Button Tx;
        public required Button People;
        public required TextBlock Names;
        public bool Expanded;
    }

    private readonly DispatcherTimer _status = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<ChannelRow> _rows = [];
    private readonly HashSet<string> _expandedListen = [];
    private string _listenShown = "";
    private static SrsSettings Settings => AppState.Settings.Srs;

    public SrsPanel()
    {
        InitializeComponent();
        for (var ch = 1; ch <= ChannelPlan.Count; ch++) _rows.Add(BuildRow(ch));
        _status.Tick += (_, _) => ShowStatus();
        Loaded += (_, _) =>
        {
            _status.Start();
            SrsListen.Instance.Changed += Refresh;
            SrsListen.Instance.TalkChanged += Refresh;
            SrsChannels.Changed += Refresh;
            AppState.ListenUnitChanged += Refresh;
            AppState.Store.Changed += Refresh;
            RadioPosition.Changed += ShowStatus;
            AppState.AirfieldSelectionChanged += ShowStatus;
            Themes.ThemeManager.Changed += ThemeChanged;
            ShowStatus();
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            _status.Stop();
            SrsListen.Instance.Changed -= Refresh;
            SrsListen.Instance.TalkChanged -= Refresh;
            SrsChannels.Changed -= Refresh;
            AppState.ListenUnitChanged -= Refresh;
            AppState.Store.Changed -= Refresh;
            RadioPosition.Changed -= ShowStatus;
            AppState.AirfieldSelectionChanged -= ShowStatus;
            Themes.ThemeManager.Changed -= ThemeChanged;
        };
    }

    private void ThemeChanged()
    {
        _listenShown = "";  // the player's radio rows are built with the theme's colours
        Refresh();
    }

    // ---- Channels -------------------------------------------------------------------------

    private ChannelRow BuildRow(int ch)
    {
        var grid = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        foreach (var w in new[] { 34.0, 92, 72, double.NaN, double.NaN, double.NaN })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = double.IsNaN(w) ? GridLength.Auto : new GridLength(w) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Add(grid, new TextBlock { Text = $"CH{ch}", FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center }, 0);
        var role = new TextBlock { FontWeight = FontWeights.SemiBold, Foreground = Brushes.Black, TextTrimming = TextTrimming.CharacterEllipsis };
        var badge = new Border { CornerRadius = new CornerRadius(2), Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Child = role };
        Add(grid, badge, 1);
        var box = new TextBox { VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "MHz, e.g. 251.000 — Enter tunes the channel" };
        Add(grid, box, 2);
        var set = new Button { Content = "Set", Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(4, 0, 0, 0), ToolTip = "Tune this channel of your SRS app" };
        Add(grid, set, 3);
        var tx = new Button { Content = "TX", Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(4, 0, 0, 0), FontWeight = FontWeights.Bold, ToolTip = "Transmit on this channel now" };
        Add(grid, tx, 4);
        var people = new Button { Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(4, 0, 0, 0), ToolTip = "Own SRS users on this frequency: click for the list" };
        Add(grid, people, 5);
        var names = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(34, 1, 0, 3), Visibility = Visibility.Collapsed };
        names.SetResourceReference(TextBlock.ForegroundProperty, "Nebo.TextDim");
        Grid.SetRow(names, 1);
        Grid.SetColumnSpan(names, 7);
        grid.Children.Add(names);
        ChannelRows.Children.Add(grid);

        var row = new ChannelRow { Channel = ch, Box = box, Role = role, RoleBadge = badge, Tx = tx, People = people, Names = names };
        void Apply()
        {
            if (Parse(box.Text) is var (ok, mhz) && ok) SrsChannels.Set(ch, mhz);
            else box.Text = Format(SrsChannels.Mhz(ch));
        }
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Apply(); };
        box.LostFocus += (_, _) => { if (Parse(box.Text) is (true, var m) && m != SrsChannels.Mhz(ch)) SrsChannels.Set(ch, m); };
        set.Click += (_, _) => Apply();
        tx.Click += (_, _) =>
        {
            Apply();
            SrsChannels.Transmit(ch);
        };
        people.Click += (_, _) =>
        {
            row.Expanded = !row.Expanded;
            Refresh();
        };
        return row;
    }

    private static void Add(Grid g, UIElement e, int column)
    {
        Grid.SetColumn(e, column);
        g.Children.Add(e);
    }

    /// <summary>(true, MHz) for a frequency, (true, null) for an empty box, (false, _) otherwise.</summary>
    private static (bool Ok, double? Mhz) Parse(string text)
    {
        text = text.Trim().Replace(',', '.');
        if (text.Length == 0) return (true, null);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v is >= 1 and <= 1000 ? (true, v) : (false, null);
    }

    private static string Format(double? mhz) => mhz is { } f ? f.ToString("0.000", CultureInfo.InvariantCulture) : "";

    private void Refresh()
    {
        using var perf = RenderStats.Measure("panel: srs");
        var listen = SrsListen.Instance;
        var now = DateTime.UtcNow;
        string Who(SrsClient c) => listen.Roster.TalkingOn(c.ClientGuid, now) is not null ? $"▶ {c.Name}" : c.Name ?? "";

        foreach (var row in _rows)
        {
            var ch = row.Channel;
            var mhz = SrsChannels.Mhz(ch);
            if (!row.Box.IsKeyboardFocusWithin) row.Box.Text = Format(mhz);
            var package = ChannelPlan.PackageOf(ch);
            row.Role.Text = package > 0 ? $"P{package} {AppState.Packages[package].Name}" : ch == ChannelPlan.Atc ? "ATC" : SrsChannels.RadioName(ch) is { Length: > 0 } radioName ? radioName : "free";
            row.RoleBadge.Background = package > 0 ? PackageColors.Brush(package) : ch == ChannelPlan.Atc ? AtcBrush : Brushes.Transparent;
            row.Role.Foreground = package > 0 || ch == ChannelPlan.Atc ? Brushes.Black : Themes.ThemeBrush.Get("Nebo.TextDim");
            var transmitting = SrsChannels.Transmitting == ch;
            var sending = SrsChannels.SendingNow == ch;
            row.Tx.Background = sending ? SendingBrush : transmitting ? TxBrush : Themes.ThemeBrush.Get("Nebo.PanelAlt");
            row.Tx.Content = sending ? "TX ●" : "TX";
            row.Tx.Foreground = sending ? Brushes.White : transmitting ? Brushes.Black : Themes.ThemeBrush.Get("Nebo.Text");
            row.Tx.IsEnabled = mhz is not null;
            var people = mhz is { } f && listen.Connected ? listen.Roster.ClientsOn(f * 1e6) : [];
            var talking = people.Any(c => listen.Roster.TalkingOn(c.ClientGuid, now) is not null);
            // Without NEBO's SRS connection: the app's own count (no names).
            row.People.Content = listen.Connected ? $"{people.Count} on it {(row.Expanded ? "▴" : "▾")}"
                : SrsChannels.AppCount(ch) is { } appCount && mhz is not null ? $"{appCount} on it" : "—";
            row.People.Foreground = talking ? TalkBrush : Themes.ThemeBrush.Get("Nebo.Text");
            row.People.IsEnabled = people.Count > 0;
            row.Names.Text = string.Join(", ", people.Select(Who));
            row.Names.Visibility = row.Expanded && people.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        ShowListen();
    }

    // ---- Listen traffic -------------------------------------------------------------------

    /// <summary>Snapshot aid: the SRS user whose radios are shown, by SRS name (the test mission
    /// has no players to right-click).</summary>
    public static string? DebugListenName { get; set; }

    private void ShowListen()
    {
        var listen = SrsListen.Instance;
        var unit = AppState.ListenUnit is { } id ? AppState.Store.Find(id)?.Unit : null;
        var client = unit is null ? null : listen.ClientFor(unit);
        if (DebugListenName is { } debugName && unit is not null)
            client = listen.Roster.Own.FirstOrDefault(c => c.Name == debugName);
        StopListenButton.Visibility = unit is null ? Visibility.Collapsed : Visibility.Visible;
        if (unit is null)
        {
            ListenBox.Header = "Listen traffic";
            ListenText.Text = AppState.ListenUnit is null ? "Right-click a player's aircraft on a map or in Objects → Listen traffic." : "That aircraft is gone.";
            Rebuild("", []);
            return;
        }
        var name = Symbology.NameOf(unit) ?? unit.Player ?? $"#{unit.Id}";
        ListenBox.Header = $"Listen traffic: {name}";
        if (client is null)
        {
            ListenText.Text = !listen.Connected ? "NEBO is not connected to SRS." : $"{unit.Player ?? name} is not on SRS (or uses another name there).";
            Rebuild("", []);
            return;
        }
        ListenText.Text = $"{client.Name}'s radios as SRS reports them:";
        var now = DateTime.UtcNow;
        var radios = SrsRoster.RadiosOf(client).Select(r =>
        {
            var people = listen.Roster.ClientsOn(r.Channel.Hz);
            var who = string.Join(", ", people.Select(c => listen.Roster.TalkingOn(c.ClientGuid, now) is not null ? $"▶ {c.Name}" : c.Name));
            return (r.Radio, r.Channel, r.Guard, Count: people.Count, Who: who);
        }).ToList();
        var signature = string.Join("|", radios.Select(r => $"{r.Radio}{r.Channel}{r.Guard}{r.Who}")) + string.Join(",", _expandedListen);
        Rebuild(signature, radios);
    }

    /// <summary>Rebuilds the player's radio rows only when they change (keeps the channel pickers).</summary>
    private void Rebuild(string signature, List<(int Radio, SrsChannel Channel, bool Guard, int Count, string Who)> radios)
    {
        if (signature == _listenShown) return;
        _listenShown = signature;
        ListenRows.Children.Clear();
        foreach (var r in radios)
        {
            var key = $"{r.Radio}{r.Guard}";
            var line = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
            var put = new Button { Content = "Put on", Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(4, 0, 0, 0), ToolTip = "Tune that channel of yours to this frequency" };
            var pick = new ComboBox { Width = 58, ItemsSource = ChannelPlan.Free.Select(c => $"CH{c}").ToList(), SelectedIndex = 0 };
            DockPanel.SetDock(put, Dock.Right);
            DockPanel.SetDock(pick, Dock.Right);
            line.Children.Add(put);
            line.Children.Add(pick);
            var people = new Button { Content = $"{r.Count} on it {(_expandedListen.Contains(key) ? "▴" : "▾")}", Padding = new Thickness(6, 0, 6, 0), Margin = new Thickness(6, 0, 0, 0), IsEnabled = r.Count > 0 };
            DockPanel.SetDock(people, Dock.Right);
            line.Children.Add(people);
            line.Children.Add(new TextBlock { Text = $"Radio {r.Radio}{(r.Guard ? " guard" : "")}   {r.Channel}", FontFamily = new FontFamily("Consolas"), VerticalAlignment = VerticalAlignment.Center });
            var channel = r.Channel;
            put.Click += (_, _) =>
            {
                var ch = ChannelPlan.Free.ElementAt(Math.Max(0, pick.SelectedIndex));
                SrsChannels.Set(ch, channel.Mhz);
            };
            people.Click += (_, _) =>
            {
                if (!_expandedListen.Remove(key)) _expandedListen.Add(key);
                _listenShown = "";
                Refresh();
            };
            ListenRows.Children.Add(line);
            if (_expandedListen.Contains(key) && r.Who.Length > 0)
                ListenRows.Children.Add(new TextBlock { Text = r.Who, TextWrapping = TextWrapping.Wrap, Foreground = Themes.ThemeBrush.Get("Nebo.TextDim"), Margin = new Thickness(12, 0, 0, 3) });
        }
    }

    private void StopListen_Click(object sender, RoutedEventArgs e) => AppState.ListenTo(null);

    // ---- Position line, SRS app, default channels -----------------------------------------

    private void ShowStatus()
    {
        PositionText.Text = $"Radio at: {RadioPosition.Describe()}";
        StartAppButton.Visibility = SrsApp.Running ? Visibility.Collapsed : Visibility.Visible;
        DefaultText.Text = Settings.DefaultChannels is { } d && d.Any(m => m is not null)
            ? $"Default kept ({string.Join(" ", d.Select((m, i) => m is { } f ? $"{i + 1}:{f:0.###}" : null).OfType<string>())}): tuned in whenever the SRS app connects."
            : "No default yet: the SRS app's own presets apply when it starts.";
    }

    private void ChangePosition_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } owner) SettingsWindow.Open(owner, "SRS");
    }

    private void StartApp_Click(object sender, RoutedEventArgs e)
    {
        SrsApp.EnsureRunning();
        ShowStatus();
    }

    private void SaveDefault_Click(object sender, RoutedEventArgs e)
    {
        Settings.DefaultChannels = Enumerable.Range(1, ChannelPlan.Count).Select(SrsChannels.Mhz).ToArray();
        AppState.Settings.Save();
        ShowStatus();
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
