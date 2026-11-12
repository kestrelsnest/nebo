using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Nebo.Client;

/// <summary>A button on a tray message. <c>Run</c> gets the button (a menu opens under it).
/// <c>Closes</c>: the message goes once the action ran.</summary>
public sealed record TrayAction(string Label, Func<FrameworkElement, Task> Run, string? Tip = null, bool Closes = true);

/// <summary>One message in the tray: a line, an optional detail, an accent colour, buttons.
/// Strong messages have a thick border; info ones go after a while; any message goes as soon
/// as <c>Obsolete</c> says so (the pilot was taken, the handoff answered elsewhere).</summary>
public sealed class TrayMessage
{
    public long Id { get; internal set; }
    public DateTime Time { get; init; } = DateTime.Now;
    public required string Title { get; init; }
    public string? Detail { get; init; }
    public Color? Accent { get; init; }
    public bool Strong { get; init; }
    public IReadOnlyList<TrayAction> Actions { get; init; } = [];
    /// <summary>A later message with the same key replaces this one.</summary>
    public string? Key { get; init; }
    public Func<bool>? Obsolete { get; init; }
    /// <summary>When it goes by itself; null: stays until answered or dismissed. Messages that
    /// are not strong always go after <see cref="MessageTray.InfoLife"/>.</summary>
    public DateTime? Expires { get; internal set; }
    /// <summary>When <see cref="OnDeadline"/> runs by itself (the card counts down to it with
    /// <see cref="DeadlineText"/>); null: no countdown. Clearing it stops the countdown.</summary>
    public DateTime? Deadline { get; set; }
    /// <summary>The countdown line, given the seconds left ("Keeping him in {0} s").</summary>
    public string DeadlineText { get; init; } = "{0} s";
    public Func<Task>? OnDeadline { get; init; }
}

/// <summary>The messages every map's tray shows (controllers on and off station, new pilots,
/// handoffs, take-offs, results). Lives on the UI thread.</summary>
public static class MessageTray
{
    public const int MaxKept = 30;
    public static readonly TimeSpan InfoLife = TimeSpan.FromSeconds(20);
    private static readonly List<TrayMessage> Items = [];
    private static long _nextId;
    private static DispatcherTimer? _timer;

    public static IReadOnlyList<TrayMessage> All => Items;
    public static event Action? Changed;

    public static TrayMessage Post(TrayMessage m)
    {
        _timer ??= StartTimer();
        m.Id = ++_nextId;
        if (!m.Strong) m.Expires ??= m.Time + InfoLife;  // only messages that need you stay
        if (m.Key is { } key) Items.RemoveAll(o => o.Key == key);
        Items.Add(m);
        if (Items.Count > MaxKept) Items.RemoveRange(0, Items.Count - MaxKept);
        Changed?.Invoke();
        return m;
    }

    /// <summary>A short line that goes by itself (results, errors, departures).</summary>
    public static void Info(string text, string? detail = null, Color? accent = null) =>
        Post(new TrayMessage { Title = text, Detail = detail, Accent = accent });

    /// <summary>How long a message fades before it goes.</summary>
    public static readonly TimeSpan FadeTime = TimeSpan.FromSeconds(5);

    public static void Remove(TrayMessage m)
    {
        if (Items.Remove(m)) Changed?.Invoke();
    }

    public static void Clear()
    {
        if (Items.Count == 0) return;
        Items.Clear();
        Changed?.Invoke();
    }

    /// <summary>Drops expired and obsolete messages and runs the default of those whose
    /// deadline passed (once a second and when the flights change).</summary>
    public static void Prune()
    {
        var now = DateTime.Now;
        var due = Items.Where(m => m.Deadline < now && m.Obsolete?.Invoke() != true).ToList();
        foreach (var m in due)
        {
            m.Deadline = null;
            Items.Remove(m);
            if (m.OnDeadline is { } run) _ = run();
        }
        if (Items.RemoveAll(m => m.Expires < now || m.Obsolete?.Invoke() == true) > 0 || due.Count > 0) Changed?.Invoke();
    }

    private static DispatcherTimer StartTimer()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        t.Tick += (_, _) => Prune();
        t.Start();
        return t;
    }
}

/// <summary>The tray in a map's bottom-right corner: messenger cards, newest at the bottom.
/// Only the cards take the mouse; the map around them stays usable.</summary>
public sealed class MessageTrayView : StackPanel
{
    public const int Shown = 6;

    public MessageTrayView()
    {
        Width = 300;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(8, 8, 8, 24);
        Loaded += (_, _) => { MessageTray.Changed += Rebuild; Themes.ThemeManager.Changed += Rebuild; Rebuild(); };
        Unloaded += (_, _) => { MessageTray.Changed -= Rebuild; Themes.ThemeManager.Changed -= Rebuild; };
    }

    /// <summary>Whether a mouse event's source is on a tray card (the map leaves those alone).</summary>
    public static bool Contains(object? source)
    {
        for (var d = source as DependencyObject; d is not null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is MessageTrayView) return true;
        return false;
    }

    private void Rebuild()
    {
        Children.Clear();
        var items = MessageTray.All;
        foreach (var m in items.Skip(Math.Max(0, items.Count - Shown))) Children.Add(Card(m));
        if (items.Count > Shown)
            Children.Insert(0, new TextBlock
            {
                Text = $"+{items.Count - Shown} older", HorizontalAlignment = HorizontalAlignment.Right, FontSize = 11,
                Foreground = Themes.ThemeBrush.Get("Nebo.TextDim"), Margin = new Thickness(0, 0, 4, 2),
            });
    }

    private static Border Card(TrayMessage m)
    {
        var text = Themes.ThemeBrush.Get("Nebo.Text");
        var dim = Themes.ThemeBrush.Get("Nebo.TextDim");
        var accent = m.Accent is { } a ? new SolidColorBrush(a) : (Brush)dim;
        var back = Themes.ThemeBrush.Get("Nebo.Panel").Clone();
        back.Opacity = 0.94;

        var body = new StackPanel();
        var head = new DockPanel();
        var close = new Button
        {
            Content = "✕", Padding = new Thickness(4, 0, 4, 0), FontSize = 10, Margin = new Thickness(6, 0, 0, 0),
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = dim, ToolTip = "Dismiss",
            VerticalAlignment = VerticalAlignment.Top,
        };
        close.Click += (_, _) => MessageTray.Remove(m);
        DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(close);
        var title = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = text };
        title.Inlines.Add(new System.Windows.Documents.Run($"{m.Time:HH:mm}  ") { Foreground = dim, FontSize = 11 });
        title.Inlines.Add(new System.Windows.Documents.Run(m.Title) { FontWeight = m.Strong ? FontWeights.SemiBold : FontWeights.Normal });
        head.Children.Add(title);
        body.Children.Add(head);
        if (m.Detail is { Length: > 0 } detail)
            body.Children.Add(new TextBlock { Text = detail, Foreground = dim, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        if (m.Deadline is not null)
        {
            // The countdown, ticking on its own while the card is shown.
            var count = new TextBlock { Foreground = accent, FontSize = 11, FontWeight = FontWeights.SemiBold };
            void Show()
            {
                if (m.Deadline is not { } at) { count.Visibility = Visibility.Collapsed; return; }
                count.Text = string.Format(m.DeadlineText, Math.Max(0, (int)Math.Ceiling((at - DateTime.Now).TotalSeconds)));
            }
            var tick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            tick.Tick += (_, _) => Show();
            count.Loaded += (_, _) => { Show(); tick.Start(); };
            count.Unloaded += (_, _) => tick.Stop();
            body.Children.Add(count);
        }
        if (m.Actions.Count > 0)
        {
            var row = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            foreach (var act in m.Actions)
            {
                var b = new Button { Content = act.Label, Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 4, 0), ToolTip = act.Tip };
                b.Click += async (_, _) =>
                {
                    b.IsEnabled = false;
                    try { await act.Run(b); }
                    finally { b.IsEnabled = true; }
                    if (act.Closes) MessageTray.Remove(m);
                };
                row.Children.Add(b);
            }
            body.Children.Add(row);
        }
        var card = new Border
        {
            Background = back, BorderBrush = accent, BorderThickness = new Thickness(m.Strong ? 2 : 1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 5, 4, 6), Margin = new Thickness(0, 3, 0, 0), Child = body,
        };
        // A message that goes by itself fades out over its last seconds (from where it is now:
        // the tray is rebuilt when other messages come and go).
        if (m.Expires is { } end)
        {
            var left = end - DateTime.Now;
            var fadeFrom = left > MessageTray.FadeTime ? left - MessageTray.FadeTime : TimeSpan.Zero;
            var fadeFor = left > MessageTray.FadeTime ? MessageTray.FadeTime : (left > TimeSpan.Zero ? left : TimeSpan.FromMilliseconds(1));
            var startAt = left > MessageTray.FadeTime ? 1.0 : Math.Max(0, left.TotalSeconds / MessageTray.FadeTime.TotalSeconds);
            card.Opacity = startAt;
            card.BeginAnimation(UIElement.OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(startAt, 0, fadeFor) { BeginTime = fadeFrom });
            // Pointing at it keeps it readable (it still goes when its time is up).
            card.MouseEnter += (_, _) => { card.BeginAnimation(UIElement.OpacityProperty, null); card.Opacity = 1; };
        }
        return card;
    }
}
