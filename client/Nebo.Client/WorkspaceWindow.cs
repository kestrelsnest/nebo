using System.Windows;
using System.Windows.Controls;

namespace Nebo.Client;

/// <summary>A workspace detached from the main window's tabs (e.g. ATC on a second screen).
/// "Back to tabs", or closing the window, returns it to the main window as a tab.</summary>
public sealed class WorkspaceWindow : Window
{
    public WorkspaceView View { get; }
    private bool _attaching;

    public WorkspaceWindow(WorkspaceView view, WindowPlacement? placement)
    {
        View = view;
        Title = $"NEBO — {view.Title}";
        Width = 1200;
        Height = 800;
        var back = new Button { Content = "⇤ Back to tabs", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(4, 3, 4, 3),
            ToolTip = "Put this workspace back as a tab of the main window" };
        back.Click += (_, _) => Close();
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        bar.Children.Add(back);
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(view);
        Content = root;
        view.DetachedWindow = this;
        MainWindow.Place(this, placement);
        Activated += (_, _) => WorkspaceView.Current = View;
        SourceInitialized += (_, _) => Themes.ThemeManager.Attach(this);
        Closing += (_, _) =>
        {
            if (_attaching || Application.Current.MainWindow is not MainWindow main || main.IsClosing) return;
            _attaching = true;
            root.Children.Remove(view);
            view.DetachedWindow = null;
            main.AttachTab(view);
        };
    }

    public WindowPlacement Placement() => MainWindow.PlacementOf(this, View.Id, View.Preset, detached: true);
}
