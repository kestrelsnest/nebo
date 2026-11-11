using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Nebo.Client;

/// <summary>Pick a saved server and connect. The fields on the right are the selected server's
/// (or a new one's); Connect saves them under the name first. Saved passwords are never
/// decrypted here: the boxes stay empty with "(saved)", and typing replaces them.</summary>
public partial class ConnectDialog : Window
{
    /// <summary>The server to connect to (saved, or unnamed and not saved).</summary>
    public ServerProfile Server { get; private set; } = new();

    private static AppSettings Settings => AppState.Settings;
    private ServerProfile? _selected;
    private bool _filling;

    public ConnectDialog()
    {
        InitializeComponent();
        MigrateOldLogin();
        Refresh(Settings.Servers.FirstOrDefault(s => s.Name == Settings.LastServer) ?? Settings.Servers.FirstOrDefault());
        Loaded += (_, _) => (_selected is null ? HostBox : (UIElement)ServerList).Focus();
    }

    /// <summary>Before saved servers (0.9): the one remembered login becomes a saved server.</summary>
    private static void MigrateOldLogin()
    {
        if (Settings.Servers.Count > 0 || !Settings.RememberLogin) return;
        Settings.Servers.Add(new ServerProfile
        {
            Name = Settings.Host,
            Host = Settings.Host,
            Port = Settings.Port,
            SrsPort = Settings.Srs.ListenPort,
            ProtectedPassword = Settings.RememberedPassword is { } pw ? new ServerProfile { Password = pw }.ProtectedPassword : null,
            ProtectedEamPassword = Settings.Srs.EamPassword is { } eam ? new ServerProfile { EamPassword = eam }.ProtectedEamPassword : null,
        });
    }

    private void Refresh(ServerProfile? select)
    {
        _filling = true;
        ServerList.ItemsSource = null;
        ServerList.ItemsSource = Settings.Servers.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(s => new Row(s)).ToList();
        ServerList.SelectedItem = ((IEnumerable<Row>)ServerList.ItemsSource).FirstOrDefault(r => r.Profile == select);
        _filling = false;
        Show(select);
    }

    private sealed record Row(ServerProfile Profile)
    {
        public string Name => Profile.Name;
        public string Summary => $"{Profile.Host}:{Profile.Port} · SRS {Profile.SrsPort}";
    }

    private void Show(ServerProfile? p)
    {
        _selected = p;
        NameBox.Text = p?.Name ?? "";
        HostBox.Text = p?.Host ?? "";
        PortBox.Text = (p?.Port ?? Core.NeboConnection.DefaultPort).ToString();
        SrsPortBox.Text = (p?.SrsPort ?? Core.SrsListener.DefaultPort).ToString();
        PasswordBox.Password = "";
        EamBox.Password = "";
        DeleteButton.IsEnabled = p is not null;
        UpdateHints();
        ErrorText.Text = "";
    }

    private void UpdateHints()
    {
        PasswordHint.Visibility = PasswordBox.Password.Length == 0 && _selected?.HasPassword == true ? Visibility.Visible : Visibility.Collapsed;
        EamHint.Visibility = EamBox.Password.Length == 0 && _selected?.HasEamPassword == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Password_Changed(object sender, RoutedEventArgs e) => UpdateHints();

    private void ServerList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_filling && ServerList.SelectedItem is Row r) Show(r.Profile);
    }

    private void ServerList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ServerList.SelectedItem is Row) Connect_Click(sender, e);
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        ServerList.SelectedItem = null;
        Show(null);
        NameBox.Focus();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        Settings.Servers.Remove(_selected);
        if (Settings.LastServer == _selected.Name) Settings.LastServer = null;
        SaveSettings();
        Refresh(Settings.Servers.FirstOrDefault());
    }

    private void Connect_Click(object sender, RoutedEventArgs e)
    {
        var host = HostBox.Text.Trim();
        if (host.Length == 0) { ErrorText.Text = "Enter the server's address"; return; }
        if (!int.TryParse(PortBox.Text, out var port) || port is < 1 or > 65535) { ErrorText.Text = "Port must be 1–65535"; return; }
        if (!int.TryParse(SrsPortBox.Text, out var srsPort) || srsPort is < 1 or > 65535) { ErrorText.Text = "SRS port must be 1–65535"; return; }
        var name = NameBox.Text.Trim();

        // A named server is saved (a new name adds one); unnamed ones connect without saving.
        var p = name.Length == 0 ? new ServerProfile() : Settings.Servers.FirstOrDefault(s => s.Name == name);
        if (p is null)
        {
            p = new ServerProfile { Name = name };
            if (_selected is not null && _selected.Name != name)
                (p.ProtectedPassword, p.ProtectedEamPassword) = (_selected.ProtectedPassword, _selected.ProtectedEamPassword);
            Settings.Servers.Add(p);
        }
        else if (_selected is not null && p != _selected && PasswordBox.Password.Length == 0)
        {
            (p.ProtectedPassword, p.ProtectedEamPassword) = (_selected.ProtectedPassword, _selected.ProtectedEamPassword);
        }
        p.Host = host;
        p.Port = port;
        p.SrsPort = srsPort;
        if (PasswordBox.Password.Length > 0) p.Password = PasswordBox.Password;
        if (EamBox.Password.Length > 0) p.EamPassword = EamBox.Password;
        if (name.Length > 0)
        {
            Settings.LastServer = name;
            SaveSettings();
        }
        Server = p;
        DialogResult = true;
    }

    private static void SaveSettings()
    {
        if (!MainWindow.PersistOnExit) return;
        try { Settings.Save(); }
        catch (IOException) { /* kept in memory; saved again on exit */ }
    }
}
