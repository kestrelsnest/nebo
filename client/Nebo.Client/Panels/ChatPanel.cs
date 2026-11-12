using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Nebo.Client.Core;

namespace Nebo.Client.Panels;

/// <summary>The server's chat as the side's controllers see it (DLL 0.17): players' all chat and
/// side chat, the mission's announcements, players joining and leaving, and what controllers
/// told pilots. Below it, a text to every pilot of the side or to everyone, shown on their
/// screens as "NEBO &lt;callsign&gt;: …".</summary>
public sealed class ChatPanel : UserControl
{
    private static Brush Back => Themes.ThemeBrush.Get("Nebo.Panel");
    private static Brush Text => Themes.ThemeBrush.Get("Nebo.Text");
    private static Brush Dim => Themes.ThemeBrush.Get("Nebo.TextDim");

    private static readonly Color MissionColor = Color.FromRgb(0xE0, 0xB0, 0x40);
    private static readonly Color NeboColor = Color.FromRgb(0x4C, 0xB8, 0xE4);
    private static readonly Color SideColor = Color.FromRgb(0x7C, 0xD9, 0x7C);

    private readonly StackPanel _list = new();
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _empty;
    private readonly CheckBox _chat = Chip("Chat", "Players' all chat and side chat");
    private readonly CheckBox _mission = Chip("Mission", "The mission's on-screen announcements for your side or for all");
    private readonly CheckBox _joins = Chip("Joins", "Players connecting and leaving");
    private readonly CheckBox _nebo = Chip("NEBO", "What your side's controllers told pilots");
    private readonly ComboBox _audience = new() { Width = 96, Margin = new Thickness(0, 0, 4, 0) };
    private readonly TextBox _text = new() { AcceptsReturn = false, VerticalContentAlignment = VerticalAlignment.Center };
    private readonly Button _send = new() { Content = "Send", Padding = new Thickness(10, 1, 10, 1), Margin = new Thickness(4, 0, 0, 0) };

    public ChatPanel()
    {
        _scroll = new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(6, 2, 6, 2) };
        _empty = new TextBlock
        {
            Text = "Nothing yet. Players' chat, the mission's announcements and players joining appear here (server 0.17).",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8),
        };
        foreach (var c in new[] { _chat, _mission, _joins, _nebo })
        {
            c.IsChecked = true;
            c.Checked += (_, _) => Rebuild();
            c.Unchecked += (_, _) => Rebuild();
        }
        _audience.Items.Add(new ComboBoxItem { Content = "My side", Tag = "side", ToolTip = "Every pilot of your side sees it on screen" });
        _audience.Items.Add(new ComboBoxItem { Content = "Everyone", Tag = "all", ToolTip = "Every pilot of both sides sees it on screen" });
        _audience.SelectedIndex = 0;
        _text.ToolTip = "On the pilots' screens for 15 s as \"NEBO <your callsign>: …\". Enter sends";
        _text.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; _ = SendAsync(); } };
        _send.Click += (_, _) => _ = SendAsync();

        var filters = new WrapPanel { Margin = new Thickness(6, 4, 6, 2) };
        foreach (var c in new[] { _chat, _mission, _joins, _nebo }) filters.Children.Add(c);
        var send = new DockPanel { Margin = new Thickness(6, 4, 6, 6), LastChildFill = true };
        DockPanel.SetDock(_audience, Dock.Left);
        DockPanel.SetDock(_send, Dock.Right);
        send.Children.Add(_audience);
        send.Children.Add(_send);
        send.Children.Add(_text);

        var root = new DockPanel();
        DockPanel.SetDock(filters, Dock.Top);
        DockPanel.SetDock(send, Dock.Bottom);
        root.Children.Add(filters);
        root.Children.Add(send);
        var middle = new Grid();
        middle.Children.Add(_scroll);
        middle.Children.Add(_empty);
        root.Children.Add(middle);
        Content = root;

        Loaded += (_, _) =>
        {
            ChatDesk.Log.Changed += OnChanged;
            AppState.Connection.StatusChanged += OnStatus;
            Themes.ThemeManager.Changed += Rebuild;
            Rebuild();
            UpdateSend();
        };
        Unloaded += (_, _) =>
        {
            ChatDesk.Log.Changed -= OnChanged;
            AppState.Connection.StatusChanged -= OnStatus;
            Themes.ThemeManager.Changed -= Rebuild;
        };
    }

    private static CheckBox Chip(string label, string tip) => new()
    {
        Content = label, ToolTip = tip, Margin = new Thickness(0, 0, 4, 2),
        Style = Application.Current.TryFindResource("Nebo.Chip") as Style,
    };

    private void OnStatus() => Dispatcher.InvokeAsync(UpdateSend);

    private void UpdateSend() => _send.IsEnabled = _text.IsEnabled = AppState.Connection.IsConnected;

    private bool Shown(ChatLine l) => l.Kind switch
    {
        "chat" or "side" => _chat.IsChecked == true,
        "mission" => _mission.IsChecked == true,
        "join" or "leave" => _joins.IsChecked == true,
        "nebo" => _nebo.IsChecked == true,
        _ => true,
    };

    private void OnChanged()
    {
        var atBottom = _scroll.VerticalOffset >= _scroll.ScrollableHeight - 4;
        Rebuild();
        if (atBottom) _scroll.ScrollToEnd();
    }

    private void Rebuild()
    {
        Background = Back;
        _list.Children.Clear();
        var lines = ChatDesk.Log.Lines.Where(Shown).ToList();
        foreach (var l in lines.Skip(Math.Max(0, lines.Count - 300))) _list.Children.Add(Line(l));
        _empty.Visibility = ChatDesk.Log.Lines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _scroll.ScrollToEnd();
    }

    private static TextBlock Line(ChatLine l)
    {
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1), Foreground = Text };
        tb.Inlines.Add(new Run($"{l.LocalTime:HH:mm}  ") { Foreground = Dim, FontSize = 11 });
        switch (l.Kind)
        {
            case "join":
            case "leave":
                tb.Inlines.Add(new Run($"{l.From} {(l.Kind == "join" ? "connected" : "left")}") { Foreground = Dim, FontStyle = FontStyles.Italic });
                break;
            case "mission":
                tb.Inlines.Add(new Run(l.To == "all" ? "Mission (all): " : "Mission: ") { Foreground = new SolidColorBrush(MissionColor), FontWeight = FontWeights.SemiBold });
                tb.Inlines.Add(new Run(l.Text));
                break;
            case "nebo":
                var to = l.To switch { "all" => "everyone", "blue" or "red" => "side", _ => l.To };
                tb.Inlines.Add(new Run($"{l.From} → {to}: ") { Foreground = new SolidColorBrush(NeboColor), FontWeight = FontWeights.SemiBold });
                tb.Inlines.Add(new Run(l.Text));
                break;
            default:
                var side = l.Kind == "side";
                tb.Inlines.Add(new Run($"{l.From}{(side ? " (side)" : "")}: ") { Foreground = side ? new SolidColorBrush(SideColor) : Text, FontWeight = FontWeights.SemiBold });
                tb.Inlines.Add(new Run(l.Text));
                break;
        }
        return tb;
    }

    private async Task SendAsync()
    {
        var text = _text.Text.Trim();
        if (text.Length == 0 || !AppState.Connection.IsConnected) return;
        var audience = (_audience.SelectedItem as ComboBoxItem)?.Tag as string ?? "side";
        _send.IsEnabled = false;
        try
        {
            var r = await AppState.Connection.SendCommandAsync("message_side", new Dictionary<string, object>
            {
                ["text"] = text, ["seconds"] = 15, ["audience"] = audience,
            });
            if (r.Ok) _text.Clear();
            else MessageTray.Info($"Not sent: {r.Error}");
        }
        finally { UpdateSend(); }
    }
}
