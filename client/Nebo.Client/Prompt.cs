using System.Windows;
using System.Windows.Controls;

namespace Nebo.Client;

/// <summary>A one-line text question in a small dialog.</summary>
public static class Prompt
{
    /// <summary>The text entered (trimmed, possibly empty), or null when cancelled.</summary>
    public static string? Text(DependencyObject owner, string title, string question, string initial = "", int maxLength = 0, string ok = "OK")
    {
        var box = new TextBox { MinWidth = 240, Margin = new Thickness(0, 6, 0, 10), MaxLength = maxLength, Text = initial };
        var okButton = new Button { Content = ok, IsDefault = true, MinWidth = 80, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(okButton);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock { Text = question, TextWrapping = TextWrapping.Wrap, MaxWidth = 300 });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var dialog = new Window
        {
            Title = title, Content = panel, SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            Owner = Window.GetWindow(owner), WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        };
        okButton.Click += (_, _) => dialog.DialogResult = true;
        dialog.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return dialog.ShowDialog() == true ? box.Text.Trim() : null;
    }
}
