using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Nebo.Client.Input;

/// <summary>While the bindings window waits for a key or button, the app's own key and mouse
/// handlers stand back.</summary>
public static class KeyCapture
{
    public static bool Active { get; set; }
}

/// <summary>Window → Key bindings: every action with its key or mouse button; Change listens for
/// the next one (Esc cancels); the bullseye readout held or toggled.</summary>
public sealed class KeyBindingsWindow : Window
{
    private readonly Dictionary<string, TextBlock> _shown = [];
    private string? _listening;
    private Button? _listeningButton;
    private readonly TextBlock _hint = new() { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };

    public KeyBindingsWindow()
    {
        Title = "Key bindings";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        var grid = new Grid { Margin = new Thickness(12) };
        foreach (var w in new[] { 190.0, 200, 90 }) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(w) });
        var row = 0;
        foreach (var (action, name, _, hint) in Bindings.All)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = name, ToolTip = hint, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 8, 3) };
            var shown = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
            var change = new Button { Content = "Change", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 2, 0, 2) };
            var a = action;
            change.Click += (_, _) => Listen(a, change);
            Place(grid, label, row, 0);
            Place(grid, shown, row, 1);
            Place(grid, change, row, 2);
            _shown[action] = shown;
            row++;
        }
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var toggle = new CheckBox { Content = "Bullseye call: press to switch on and off (instead of while held)", IsChecked = AppState.Settings.BullseyeToggle, Margin = new Thickness(0, 10, 0, 0) };
        toggle.Click += (_, _) =>
        {
            AppState.Settings.BullseyeToggle = toggle.IsChecked == true;
            AppState.SetBullsKeyHeld(false);
        };
        Place(grid, toggle, row++, 0, 3);
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Place(grid, _hint, row++, 0, 3);
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var reset = new Button { Content = "Reset to defaults", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(0, 0, 8, 0) };
        reset.Click += (_, _) =>
        {
            Bindings.ResetAll();
            Show();
        };
        var close = new Button { Content = "Close", IsCancel = true, MinWidth = 80 };
        close.Click += (_, _) => Close();
        buttons.Children.Add(reset);
        buttons.Children.Add(close);
        Place(grid, buttons, row, 0, 3);
        Content = grid;
        Show();
        PreviewKeyDown += OnKey;
        PreviewMouseDown += OnMouse;
        Closed += (_, _) =>
        {
            KeyCapture.Active = false;
            if (!Dev.DebugSnapshot.Active || Dev.DebugSnapshot.Persist) AppState.Settings.Save();
        };
    }

    private static void Place(Grid g, UIElement e, int row, int column, int span = 1)
    {
        Grid.SetRow(e, row);
        Grid.SetColumn(e, column);
        Grid.SetColumnSpan(e, span);
        g.Children.Add(e);
    }

    private new void Show()
    {
        foreach (var (action, block) in _shown) block.Text = Bindings.Get(action).Display;
        _hint.Text = _listening is null ? "Change, then press the key or click the mouse button you want (Esc cancels)." : "";
    }

    private void Listen(string action, Button button)
    {
        _listening = action;
        _listeningButton = button;
        KeyCapture.Active = true;
        _shown[action].Text = action == Bindings.Ruler ? "click a mouse button…" : "press a key or click…";
        _hint.Text = "Esc cancels. Plain left click is kept for selecting and moving the map.";
    }

    private void Stop()
    {
        _listening = null;
        _listeningButton = null;
        KeyCapture.Active = false;
        Show();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (_listening is not { } action) return;
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            Stop();
            return;
        }
        // Wait for the real key when only a modifier is down; the ruler is dragged with a button.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
        if (action == Bindings.Ruler)
        {
            _hint.Text = "The ruler is dragged: click a mouse button (with Ctrl / Shift / Alt if you like).";
            return;
        }
        Bindings.Set(action, new Gesture(key, null, Keyboard.Modifiers));
        Stop();
    }

    private void OnMouse(object sender, MouseButtonEventArgs e)
    {
        if (_listening is not { } action) return;
        // The Change button itself starts listening on its own click.
        if (e.ChangedButton == MouseButton.Left && _listeningButton?.IsMouseOver == true) return;
        e.Handled = true;
        if (e.ChangedButton == MouseButton.Left && Keyboard.Modifiers == ModifierKeys.None)
        {
            _hint.Text = "Plain left click selects and moves the map: add Ctrl, Shift or Alt, or use another button.";
            return;
        }
        Bindings.Set(action, new Gesture(null, e.ChangedButton, Keyboard.Modifiers));
        Stop();
    }
}
