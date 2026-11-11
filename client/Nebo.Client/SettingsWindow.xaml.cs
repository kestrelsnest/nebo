using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using Nebo.Client.Srs;

namespace Nebo.Client;

/// <summary>Settings (the main menu's Settings): theme and map data, key bindings, the launch
/// alarm and engagement alert, and SRS — the radio position, the SRS app, and what NEBO's SRS
/// connections are doing. Each change applies at once (saved on exit like all settings).</summary>
public partial class SettingsWindow : Window
{
    private readonly DispatcherTimer _status = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _loading = true;

    /// <summary>Opens the window (one at a time), on a section: "General", "Keys", "Alerts" or "SRS".</summary>
    public static void Open(Window owner, string? section = null)
    {
        var open = Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault();
        var w = open ?? new SettingsWindow { Owner = owner };
        if (section is not null)
            w.Tabs.SelectedItem = w.Tabs.Items.OfType<System.Windows.Controls.TabItem>().FirstOrDefault(t => (string)t.Header == section) ?? w.Tabs.SelectedItem;
        if (open is null) w.Show();
        w.Activate();
    }

    private SettingsWindow()
    {
        InitializeComponent();
        var mode = Themes.ThemeManager.Mode;
        (mode == Themes.ThemeMode.Dark ? ThemeDark : mode == Themes.ThemeMode.Light ? ThemeLight : ThemeSystem).IsChecked = true;
        var alarm = AppState.Settings.LaunchAlarm;
        AlarmSound.IsChecked = alarm.Sound;
        (alarm.Volume <= 0.3 ? AlarmQuiet : alarm.Volume >= 0.8 ? AlarmLoud : AlarmNormal).IsChecked = true;
        (alarm.Repeats <= 1 ? AlarmOnce : alarm.Repeats >= 3 ? AlarmThrice : AlarmTwice).IsChecked = true;
        AlarmAi.IsChecked = AppState.Store.LaunchesAtAi;
        StartClientBox.IsChecked = AppState.Settings.Srs.StartClient;
        AutoEamBox.IsChecked = AppState.Settings.Srs.AutoEam;
        ShowDcsPath();
        UpdatesBox.IsChecked = AppState.Settings.CheckForUpdates;
        HoverNmBox.Text = AppState.Settings.LabelHoverNm.ToString("0.#", CultureInfo.InvariantCulture);
        _loading = false;
        _status.Tick += (_, _) => SrsStatusText.Text = SrsStatus.Lines();
        Loaded += (_, _) =>
        {
            SrsStatusText.Text = SrsStatus.Lines();
            _status.Start();
        };
        Closed += (_, _) => _status.Stop();
    }

    private void ShowDcsPath() => DcsPathText.Text = AppState.Settings.DcsPath switch
    {
        null => "DCS World: found automatically.",
        "" => "DCS World: not used (no DCS maps or airfield details).",
        var path => $"DCS World: {path}",
    };

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (!_loading && (sender as FrameworkElement)?.Tag is string tag && Enum.TryParse<Themes.ThemeMode>(tag, out var mode)) Themes.ThemeManager.Set(mode);
    }

    private void DcsFolder_Click(object sender, RoutedEventArgs e)
    {
        DcsLocationWindow.Change(this);
        ShowDcsPath();
    }

    private void Diagrams_Click(object sender, RoutedEventArgs e) => MainWindow.ChooseDiagrams(this);

    private void KeyBindings_Click(object sender, RoutedEventArgs e) => new Input.KeyBindingsWindow { Owner = this }.ShowDialog();

    private void AlarmSound_Click(object sender, RoutedEventArgs e) => AppState.Settings.LaunchAlarm.Sound = AlarmSound.IsChecked == true;
    private void Updates_Click(object sender, RoutedEventArgs e) => AppState.Settings.CheckForUpdates = UpdatesBox.IsChecked == true;

    private void AlarmVolume_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading || (sender as FrameworkElement)?.Tag is not string tag) return;
        AppState.Settings.LaunchAlarm.Volume = double.Parse(tag, CultureInfo.InvariantCulture);
        LaunchAlarm.Test();  // hear the new level
    }

    private void AlarmRepeats_Checked(object sender, RoutedEventArgs e)
    {
        if (!_loading && (sender as FrameworkElement)?.Tag is string tag) AppState.Settings.LaunchAlarm.Repeats = int.Parse(tag, CultureInfo.InvariantCulture);
    }

    private void AlarmTest_Click(object sender, RoutedEventArgs e) => LaunchAlarm.Test();

    private void AlarmAi_Click(object sender, RoutedEventArgs e) => AppState.Store.LaunchesAtAi = AlarmAi.IsChecked == true;

    private void EngagementTest_Click(object sender, RoutedEventArgs e) => EngagementAlert.Test();

    private void StartClient_Click(object sender, RoutedEventArgs e) => AppState.Settings.Srs.StartClient = StartClientBox.IsChecked == true;

    private void AutoEam_Click(object sender, RoutedEventArgs e) => AppState.Settings.Srs.AutoEam = AutoEamBox.IsChecked == true;

    private void ApplyHoverNm()
    {
        if (double.TryParse(HoverNmBox.Text.Trim().Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var nm) && nm is >= 0 and <= 100)
            AppState.Settings.LabelHoverNm = nm;
        HoverNmBox.Text = AppState.Settings.LabelHoverNm.ToString("0.#", CultureInfo.InvariantCulture);
    }

    private void HoverNm_LostFocus(object sender, RoutedEventArgs e) => ApplyHoverNm();

    private void HoverNm_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) ApplyHoverNm();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        ApplyHoverNm();
        Close();
    }
}
