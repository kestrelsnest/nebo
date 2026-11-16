using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media.Imaging;

namespace Nebo.Client;

/// <summary>Help → About NEBO: the logo for the current theme, the version, the community and its
/// links, and the licence.</summary>
public static class AboutWindow
{
    public static string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "?";

    public static void Open(Window owner)
    {
        var logo = new Image
        {
            Source = new BitmapImage(new Uri($"pack://application:,,,/Assets/nebo-{(Themes.ThemeManager.IsDark ? "dark" : "light")}.png")),
            Width = 420, Margin = new Thickness(0, 0, 0, 8),
        };
        var text = new TextBlock { TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
        text.SetResourceReference(TextBlock.ForegroundProperty, "Nebo.Text");
        text.Inlines.Add(new Run($"Version {Version}"));
        text.Inlines.Add(new LineBreak());
        text.Inlines.Add(new Run("by Kestrel's Nest Community"));
        text.Inlines.Add(new LineBreak());
        text.Inlines.Add(Link("kestrelsnest.co.ua", "https://kestrelsnest.co.ua/"));
        text.Inlines.Add(new Run("  ·  "));
        text.Inlines.Add(Link("Discord", "https://discord.gg/FZGRqwc2Eg"));
        text.Inlines.Add(new Run("  ·  "));
        text.Inlines.Add(Link("GitHub", "https://github.com/kestrelsnest/nebo"));
        text.Inlines.Add(new LineBreak());
        text.Inlines.Add(new LineBreak());
        var small = new Run("Free software under the GNU GPL v3 with additional permissions (LICENSE-EXCEPTION.md). "
            + "Not affiliated with Eagle Dynamics; DCS World is a trademark of Eagle Dynamics SA.") { FontSize = 11 };
        small.SetResourceReference(TextElement.ForegroundProperty, "Nebo.TextDim");
        text.Inlines.Add(small);

        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(logo);
        panel.Children.Add(text);
        var w = new Window
        {
            Title = "About NEBO", Owner = owner, Content = panel, Width = 480, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        };
        w.SetResourceReference(Control.BackgroundProperty, "Nebo.Bg");
        w.ShowDialog();
    }

    private static Hyperlink Link(string text, string url)
    {
        var link = new Hyperlink(new Run(text)) { NavigateUri = new Uri(url) };
        link.SetResourceReference(TextElement.ForegroundProperty, "Nebo.Accent");
        link.RequestNavigate += (_, e) => OpenUrl(e.Uri.AbsoluteUri);
        return link;
    }

    /// <summary>Opens a web link in the default browser.</summary>
    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception) { /* no browser registered: nothing to do */ }
    }
}
