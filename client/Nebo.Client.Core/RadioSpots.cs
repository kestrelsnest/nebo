namespace Nebo.Client.Core;

/// <summary>A place the controller's radio can stand: an own airfield (id), carrier or AWACS
/// (unit name), at DCS map metres and an antenna height (m ASL).</summary>
public sealed record RadioSpot(string Kind, string Name, int? FieldId, string? UnitName, double X, double Z, double AltM);

/// <summary>The best radio spot for talking to an aircraft: the nearest spot with clear terrain
/// line of sight to it; with none clear, the nearest spot (and <c>Clear</c> false).</summary>
public static class RadioSpots
{
    /// <summary>Antenna heights above the ground (m) where the place gives none.</summary>
    public const double AirfieldMast = 10, CarrierMast = 30;

    public static (RadioSpot Spot, bool Clear)? Best(IEnumerable<RadioSpot> spots, double x, double z, double altM, TerrainGrid? grid)
    {
        var byRange = spots.OrderBy(s => (s.X - x) * (s.X - x) + (s.Z - z) * (s.Z - z)).ToList();
        if (byRange.Count == 0) return null;
        if (grid is null) return (byRange[0], true);  // no terrain yet: the nearest
        foreach (var s in byRange)
            if (LineOfSight.Visible(grid, s.X, s.Z, s.AltM, x, z, altM)) return (s, true);
        return (byRange[0], false);
    }
}
