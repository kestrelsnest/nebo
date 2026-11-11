using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Nebo.Client.Core;
using Nebo.Client.Map;

namespace Nebo.Client.Srs;

/// <summary>Where the controller's radio stands (Settings → SRS): an airfield, helipad, own
/// carrier or own AWACS from the list (a carrier or AWACS is followed as it moves), or a fixed
/// point; and the transmit-range overlay on the maps.</summary>
public partial class RadioPositionView : UserControl
{
    private static SrsSettings Settings => AppState.Settings.Srs;
    private static readonly Brush BlueOwner = Frozen(Color.FromRgb(0x5A, 0xA0, 0xFF));
    private static readonly Brush RedOwner = Frozen(Color.FromRgb(0xFF, 0x60, 0x60));

    private bool _loading = true;
    private bool _selectingAirfield;
    private string _placesShown = "";

    /// <summary>A place the radio can stand: an airfield (id) or an own carrier / AWACS (unit name).</summary>
    private sealed record Place(string Kind, string Text, string Owner, int? FieldId, string? UnitName);

    public RadioPositionView()
    {
        InitializeComponent();
        (Settings.Position == SrsPosition.Fixed ? PositionFixed : PositionAtc).IsChecked = true;
        CoverageBox.IsChecked = Settings.ShowCoverage;
        CoverageAltBox.Text = Settings.CoverageAltFt.ToString("0", CultureInfo.InvariantCulture);
        _loading = false;
        ShowFixed();
        Loaded += (_, _) =>
        {
            AppState.Store.Changed += ShowAirfields;  // carriers and AWACS come and go
            AppState.Store.AirfieldsChanged += ShowAirfields;
            AppState.AirfieldSelectionChanged += ShowAirfieldSelection;
            RadioPosition.Changed += OnPositionChanged;
            ShowAirfields();
        };
        Unloaded += (_, _) =>
        {
            AppState.Store.Changed -= ShowAirfields;
            AppState.Store.AirfieldsChanged -= ShowAirfields;
            AppState.AirfieldSelectionChanged -= ShowAirfieldSelection;
            RadioPosition.Changed -= OnPositionChanged;
        };
    }

    /// <summary>Moved elsewhere (the best spot for a player): show it.</summary>
    private void OnPositionChanged()
    {
        _loading = true;
        (Settings.Position == SrsPosition.Fixed ? PositionFixed : PositionAtc).IsChecked = true;
        _loading = false;
        ShowFixed();
        ShowAirfieldSelection();
    }

    /// <summary>What the filters let through: own carriers and AWACS first, then airfields A–Z.</summary>
    private List<Place> Places()
    {
        var store = AppState.Store;
        var own = store.Coalition ?? "";
        var places = new List<Place>();
        if (ShowCarriers.IsChecked == true)
        {
            var carrierIds = store.Carriers.Select(c => c.Id).ToHashSet();
            foreach (var u in store.Tracks.Select(t => t.Unit).Where(u => !u.Detected && carrierIds.Contains(u.Id) && u.Name is not null))
                places.Add(new Place("carrier", $"⚓ {Symbology.NameOf(u) ?? u.Name}  ·  {u.Type}", own, null, u.Name));
        }
        if (ShowAwacs.IsChecked == true)
            foreach (var u in store.Tracks.Select(t => t.Unit).Where(u => !u.Detected && u.Name is not null && RadioPosition.IsAwacs(u)))
                places.Add(new Place("awacs", $"✈ {Symbology.NameOf(u) ?? u.Name}  ·  {u.Type}", own, null, u.Name));
        foreach (var a in store.Airfields.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (a.IsHelipad ? ShowHelipads.IsChecked != true : ShowFields.IsChecked != true) continue;
            places.Add(new Place(a.IsHelipad ? "helipad" : "airfield", $"{(a.IsHelipad ? "H " : "")}{a.Name.Replace('_', ' ')}  ·  {a.Owner}", a.Owner, a.Id, null));
        }
        var find = AirfieldFind.Text.Trim();
        bool OwnerShown(string o) => o == "blue" ? ShowBlue.IsChecked == true : o == "red" ? ShowRed.IsChecked == true : ShowNeutral.IsChecked == true;
        return places.Where(p => OwnerShown(p.Owner) && (find.Length == 0 || p.Text.Contains(find, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    /// <summary>Rebuilds the list when its contents change (carriers and AWACS come and go).</summary>
    private Panels.OwnSideFilter? _sides;

    private void ShowAirfields()
    {
        (_sides ??= new Panels.OwnSideFilter(ShowBlue, ShowRed, ShowNeutral)).Apply(AppState.Store.Coalition);
        var places = Places();
        var signature = string.Join("|", places.Select(p => p.Text));
        if (signature == _placesShown) return;
        _placesShown = signature;
        _selectingAirfield = true;
        AirfieldList.Items.Clear();
        foreach (var p in places)
        {
            var item = new ListBoxItem { Content = p.Text, Tag = p, Padding = new Thickness(2, 0, 2, 0) };
            if (p.Owner is "blue" or "red") item.Foreground = p.Owner == "blue" ? BlueOwner : RedOwner;
            else item.SetResourceReference(ForegroundProperty, "Nebo.TextDim");
            AirfieldList.Items.Add(item);
        }
        _selectingAirfield = false;
        ShowAirfieldSelection();
    }

    private void ShowAirfieldSelection()
    {
        _selectingAirfield = true;
        AirfieldList.SelectedItem = AirfieldList.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag is Place p &&
            (Settings.Position == SrsPosition.Unit ? p.UnitName is not null && p.UnitName == Settings.PositionUnit : p.FieldId is not null && p.FieldId == AppState.SelectedAirfieldId));
        if (AirfieldList.SelectedItem is not null) AirfieldList.ScrollIntoView(AirfieldList.SelectedItem);
        _selectingAirfield = false;
    }

    private void AirfieldFind_Changed(object sender, TextChangedEventArgs e) => ShowAirfields();

    private void AirfieldFilter_Click(object sender, RoutedEventArgs e) => ShowAirfields();

    private void AirfieldList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_selectingAirfield || AirfieldList.SelectedItem is not ListBoxItem { Tag: Place p }) return;
        // Picking a place here means: stand there.
        if (p.UnitName is { } name)
        {
            Settings.PositionUnit = name;
            Settings.Position = SrsPosition.Unit;
        }
        else if (p.FieldId is { } id)
        {
            AppState.SelectAirfield(id);
            Settings.Position = SrsPosition.AtcAirfield;
        }
        _loading = true;
        PositionAtc.IsChecked = true;
        _loading = false;
        RadioPosition.NotifyChanged();
    }

    private void ShowFixed() =>
        FixedText.Text = Settings.FixedLat is { } lat && Settings.FixedLon is { } lon ? $"{lat:F4}, {lon:F4}" : "(not set)";

    private void Position_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        // Back to the list: a carrier or AWACS picked there before stays the position.
        Settings.Position = PositionFixed.IsChecked == true ? SrsPosition.Fixed
            : Settings.PositionUnit is not null && AirfieldList.SelectedItem is ListBoxItem { Tag: Place { UnitName: not null } } ? SrsPosition.Unit
            : SrsPosition.AtcAirfield;
        RadioPosition.NotifyChanged();
    }

    private void Coverage_Click(object sender, RoutedEventArgs e)
    {
        Settings.ShowCoverage = CoverageBox.IsChecked == true;
        SrsCoverage.Instance.Update();
    }

    private void ApplyCoverageAlt()
    {
        if (double.TryParse(CoverageAltBox.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ft) && ft is >= 0 and <= 60000)
        {
            Settings.CoverageAltFt = Math.Round(ft);
            SrsCoverage.Instance.Update();
        }
        CoverageAltBox.Text = Settings.CoverageAltFt.ToString("0", CultureInfo.InvariantCulture);
    }

    private void CoverageAlt_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ApplyCoverageAlt();
    }

    private void CoverageAlt_LostFocus(object sender, RoutedEventArgs e) => ApplyCoverageAlt();

    private void SetFixed_Click(object sender, RoutedEventArgs e)
    {
        if (AppState.ActiveMap is not { } map)
        {
            MessageBox.Show(Window.GetWindow(this), "Click on a map first: its centre becomes the point.", "SRS");
            return;
        }
        var (x, y, _) = map.ViewCenter;
        var (lon, lat) = Mercator.ToLonLat(x, y);
        Settings.FixedLat = Math.Round(lat, 5);
        Settings.FixedLon = Math.Round(lon, 5);
        PositionFixed.IsChecked = true;
        ShowFixed();
        RadioPosition.NotifyChanged();
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
