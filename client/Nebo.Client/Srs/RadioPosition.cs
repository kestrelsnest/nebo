using Nebo.Client.Core;

namespace Nebo.Client.Srs;

/// <summary>Where the controller's radio stands (SRS range and line of sight): described for the
/// SRS tab, moved to a place (Settings → SRS, or the best spot for a player), and the own places
/// it can stand at.</summary>
public static class RadioPosition
{
    private static SrsSettings Settings => AppState.Settings.Srs;

    /// <summary>AWACS types (unit type prefixes) the radio can stand on.</summary>
    public static readonly string[] AwacsTypes = ["E-3", "E-2", "A-50", "KJ-2000", "E-7"];

    /// <summary>The position changed (here or in the Settings window).</summary>
    public static event Action? Changed;

    public static void NotifyChanged()
    {
        SrsCoverage.Instance.Update();
        Changed?.Invoke();
    }

    public static bool IsAwacs(Unit u) => u.Type is { } t && AwacsTypes.Any(a => t.StartsWith(a, StringComparison.OrdinalIgnoreCase));

    /// <summary>"Overlord1-1 (AWACS)", "Batumi (airfield)", "fixed point 42.8582, 42.4885"…</summary>
    public static string Describe()
    {
        var store = AppState.Store;
        return Settings.Position switch
        {
            SrsPosition.Fixed => Settings.FixedLat is { } lat && Settings.FixedLon is { } lon ? $"fixed point {lat:F4}, {lon:F4}" : "fixed point (not set)",
            SrsPosition.Unit => store.Tracks.Select(t => t.Unit).FirstOrDefault(u => u.Name == Settings.PositionUnit) is { } u
                ? $"{Symbology.NameOf(u) ?? u.Name} ({(IsAwacs(u) ? "AWACS" : "carrier")})"
                : $"{Settings.PositionUnit} (not in the picture)",
            SrsPosition.AtcAirfield => AppState.SelectedAirfieldId is { } id && store.Airfields.FirstOrDefault(a => a.Id == id) is { } f
                ? $"{f.Name.Replace('_', ' ')} ({(f.IsHelipad ? "helipad" : "airfield")})"
                : "no airfield picked",
            _ => "off",
        };
    }

    /// <summary>The own side's airfields, carriers and AWACS, with antenna heights.</summary>
    public static List<RadioSpot> OwnSpots()
    {
        var store = AppState.Store;
        var own = store.Coalition;
        var spots = new List<RadioSpot>();
        if (own is null || store.Theatre is not { } theatre) return spots;
        double Ground(double x, double z) => store.Terrain is { } g && LineOfSight.HeightAt(g, x, z) is { } h ? Math.Max(0, h) : 0;
        foreach (var a in store.Airfields.Where(a => !a.IsHelipad && a.Owner == own))
            spots.Add(new RadioSpot("airfield", a.Name.Replace('_', ' '), a.Id, null, a.X, a.Z, Ground(a.X, a.Z) + RadioSpots.AirfieldMast));
        var carrierIds = store.Carriers.Select(c => c.Id).ToHashSet();
        foreach (var u in store.Tracks.Select(t => t.Unit).Where(u => !u.Detected && u.Name is not null && u.Lat is not null && u.Lon is not null))
        {
            var carrier = carrierIds.Contains(u.Id);
            if (!carrier && !IsAwacs(u)) continue;
            var (x, z) = theatre.FromLatLon(u.Lat!.Value, u.Lon!.Value);
            spots.Add(new RadioSpot(carrier ? "carrier" : "awacs", Symbology.NameOf(u) ?? u.Name!, null, u.Name, x, z,
                carrier ? RadioSpots.CarrierMast : u.Alt ?? 9000));
        }
        return spots;
    }

    /// <summary>Stands the radio at a spot: an airfield (it becomes the ATC airfield) or a carrier / AWACS (followed).</summary>
    public static void StandAt(RadioSpot spot)
    {
        if (spot.UnitName is { } name)
        {
            Settings.PositionUnit = name;
            Settings.Position = SrsPosition.Unit;
        }
        else if (spot.FieldId is { } id)
        {
            AppState.SelectAirfield(id);
            Settings.Position = SrsPosition.AtcAirfield;
        }
        NotifyChanged();
    }

    /// <summary>The best own spot for talking to an aircraft (nearest with line of sight), or null.</summary>
    public static (RadioSpot Spot, bool Clear)? BestFor(Unit u)
    {
        var store = AppState.Store;
        if (u.Lat is not { } lat || u.Lon is not { } lon || store.Theatre is not { } theatre) return null;
        var (x, z) = theatre.FromLatLon(lat, lon);
        return RadioSpots.Best(OwnSpots(), x, z, u.Alt ?? 0, store.Terrain);
    }
}
