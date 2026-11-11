using System.Windows.Controls;

namespace Nebo.Client.Panels;

/// <summary>Airfield lists (ATC tab, SRS position) start with only the controller's own side
/// ticked. Set once per coalition: after that the boxes are the controller's.</summary>
public sealed class OwnSideFilter(CheckBox blue, CheckBox red, CheckBox neutral)
{
    private string? _appliedFor;

    /// <summary>Ticks the own side's box only, when the coalition is new; true when it did.</summary>
    public bool Apply(string? coalition)
    {
        if (coalition is not ("blue" or "red") || coalition == _appliedFor) return false;
        _appliedFor = coalition;
        blue.IsChecked = coalition == "blue";
        red.IsChecked = coalition == "red";
        neutral.IsChecked = false;
        return true;
    }
}
