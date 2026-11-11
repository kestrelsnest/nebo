using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Nebo.Client;

/// <summary>After a successful login: which side to control, among those the server offers.</summary>
public sealed class SidePickWindow : Window
{
    public string? Side { get; private set; }

    private SidePickWindow(IReadOnlyList<string> sides, string? user, string? last)
    {
        Title = "Join as";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = user is null ? "Logged in. Which side do you control?" : $"Logged in as {user}. Which side do you control?",
            Margin = new Thickness(0, 0, 0, 12),
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var side in new[] { "blue", "red" })
        {
            var open = sides.Contains(side);
            var b = new Button
            {
                Content = side == "blue" ? "Blue" : "Red",
                Width = 110,
                Height = 36,
                Margin = new Thickness(6, 0, 6, 0),
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(side == "blue" ? Color.FromRgb(0x2E, 0x6C, 0xD0) : Color.FromRgb(0xC8, 0x34, 0x34)),
                IsEnabled = open,
                IsDefault = open && (side == last || (last is null && sides[0] == side)),
                ToolTip = open ? null : "Not open to you on this server",
            };
            b.Click += (_, _) =>
            {
                Side = side;
                DialogResult = true;
            };
            buttons.Children.Add(b);
        }
        panel.Children.Add(buttons);
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 80, Margin = new Thickness(0, 12, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        panel.Children.Add(cancel);
        Content = panel;
        SourceInitialized += (_, _) => Themes.ThemeManager.Attach(this);
    }

    /// <summary>Asks on the UI thread; null when cancelled. A single open side is taken without asking.</summary>
    public static Task<string?> AskAsync(Window owner, IReadOnlyList<string> sides, string? user, string? last)
    {
        if (sides.Count == 1) return Task.FromResult<string?>(sides[0]);
        return owner.Dispatcher.InvokeAsync(() =>
        {
            var w = new SidePickWindow(sides, user, last) { Owner = owner };
            return w.ShowDialog() == true ? w.Side : null;
        }).Task;
    }
}
