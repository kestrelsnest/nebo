using System.Windows.Controls;
using System.Windows.Shapes;
using Nebo.Client.Core;
using Nebo.Client.Map;

namespace Nebo.Client.Panels;

/// <summary>"Add to package ▸" and "Remove from package" for an own aircraft, on the map's and
/// the Objects list's right-click menus.</summary>
public static class PackageMenu
{
    /// <summary>Whether a unit can go into a package: an own aircraft or helicopter.</summary>
    public static bool Packable(Unit u) => !u.Detected && Symbology.IsAir(Symbology.KindOf(u));

    /// <summary>"Listen traffic" for an own player: the SRS tab shows that player's radios.</summary>
    public static MenuItem? ListenItem(Unit u, System.Windows.DependencyObject from)
    {
        if (u.Detected || u.Player is null) return null;
        var onSrs = Srs.SrsListen.Instance.ClientFor(u) is not null;
        var item = new MenuItem { Header = onSrs ? "Listen traffic" : "Listen traffic (not on SRS)" };
        item.Click += (_, _) =>
        {
            AppState.ListenTo(u.Id);
            MainWindow.Of(from)?.ShowSrs();
        };
        return item;
    }

    /// <summary>A player's aircraft: move the controller's radio to the best own spot for talking
    /// to it (the nearest airfield, carrier or AWACS with line of sight), named in the item.</summary>
    public static MenuItem? BestSpotItem(Unit u)
    {
        if (u.Detected || u.Player is null || Srs.RadioPosition.BestFor(u) is not var (spot, clear)) return null;
        var kind = spot.Kind == "awacs" ? "AWACS" : spot.Kind;
        var item = new MenuItem
        {
            Header = $"Radio to best spot: {spot.Name} ({kind}){(clear ? "" : " — no line of sight from any")}",
            ToolTip = "Your SRS radio position moves there (Settings → SRS); a carrier or AWACS is followed as it moves",
        };
        item.Click += (_, _) => Srs.RadioPosition.StandAt(spot);
        return item;
    }

    public static IEnumerable<object> Items(Unit u)
    {
        var book = AppState.Packages;
        var current = book.PackageOf(u.Id);
        var add = new MenuItem { Header = current is null ? "Add to package" : "Move to package" };
        foreach (var p in book.All)
        {
            if (p.Number == current?.Number) continue;
            var item = new MenuItem
            {
                Header = $"{p.Tag}  {p.Name}{(p.Mhz is { } f ? $"  ({f:0.000})" : "")}",
                Icon = new Rectangle { Width = 12, Height = 12, Fill = PackageColors.Brush(p.Number) },
            };
            var number = p.Number;
            item.Click += (_, _) => book.Add(number, u.Id);
            add.Items.Add(item);
        }
        yield return add;
        if (current is not null)
        {
            var remove = new MenuItem { Header = $"Remove from {current.Tag} {current.Name}" };
            remove.Click += (_, _) => book.Remove(u.Id);
            yield return remove;
        }
    }
}
