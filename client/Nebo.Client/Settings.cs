using System.IO;
using System.Text.Json;
using Nebo.Client.Core;

namespace Nebo.Client;

public enum BaseMap { Scope, Satellite, DcsCharts, DcsMap, DcsSatellite }

/// <summary>Reads base map names, mapping the ones since removed (2026-10-21) to the nearest
/// remaining map, so older settings files still load.</summary>
public sealed class BaseMapConverter : System.Text.Json.Serialization.JsonConverter<BaseMap>
{
    public override BaseMap Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var name = reader.GetString();
        if (Enum.TryParse<BaseMap>(name, out var map)) return map;
        return name switch
        {
            "DcsTerrain" => BaseMap.DcsMap,
            "DcsImagery" => BaseMap.DcsSatellite,
            _ => BaseMap.Scope,  // OpenStreetMap, or anything unknown
        };
    }

    public override void Write(Utf8JsonWriter writer, BaseMap value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

/// <summary>Where the controller's radio stands for SRS range and line of sight.</summary>
/// <summary>Where the controller's radio stands for SRS: an airfield, a fixed point, or an own
/// carrier or AWACS (followed as it moves, at its altitude; <see cref="SrsSettings.PositionUnit"/>).</summary>
public enum SrsPosition { Off, AtcAirfield, Fixed, Unit }

/// <summary>The bridge to the controller's own SRS client. Ports are
/// SRS's defaults for controller tools.</summary>
public sealed class SrsSettings
{
    public SrsPosition Position { get; set; } = SrsPosition.AtcAirfield;
    public double? FixedLat { get; set; }
    public double? FixedLon { get; set; }
    /// <summary>The own carrier or AWACS the radio stands on (unit name: survives reconnects).</summary>
    public string? PositionUnit { get; set; }
    /// <summary>Draw the transmit coverage of the radio position on the maps.</summary>
    public bool ShowCoverage { get; set; } = true;
    /// <summary>The aircraft altitude the coverage is drawn for, feet.</summary>
    public double CoverageAltFt { get; set; } = 10000;
    /// <summary>Also make a tuned radio the one that transmits, when the caller does not say.</summary>
    public bool SelectOnTune { get; set; }
    public int PositionPort { get; set; } = 10710;
    public int LineOfSightPort { get; set; } = 10711;
    public int CommandPort { get; set; } = 9040;

    /// <summary>The controller's channels 1–10 as NEBO last set them, MHz (index 0 = channel 1;
    /// 7–10 are the packages' and kept with them).</summary>
    public double?[] Channels { get; set; } = new double?[Core.ChannelPlan.Count];
    /// <summary>The channel NEBO last made the transmitting one.</summary>
    public int? Transmit { get; set; }
    /// <summary>The controller's default channels 1–10 (SRS tab → Save as default), MHz: tuned into
    /// the SRS app whenever it connects. Null: none saved (the app's own presets stay).</summary>
    public double?[]? DefaultChannels { get; set; }
    /// <summary>Start the SRS client app on connecting to a NEBO server when it is not running.</summary>
    public bool StartClient { get; set; } = true;
    /// <summary>Log the SRS app into External AWACS Mode by driving its window (SrsAutoConnect).</summary>
    public bool AutoEam { get; set; } = true;
    /// <summary>SR-ClientRadio.exe; null = from the SRS installer's registry entry.</summary>
    public string? ClientPath { get; set; }

    /// <summary>NEBO's own listen-only connection to the SRS server (who is on which frequency,
    /// who talks): runs while NEBO is connected, on the NEBO server's address at this port.</summary>
    public int ListenPort { get; set; } = Core.SrsListener.DefaultPort;
    /// <summary>The name other SRS users see for NEBO's listener.</summary>
    public string ListenName { get; set; } = "NEBO (listen)";
    /// <summary>The External AWACS Mode password, DPAPI-encrypted like the NEBO password.</summary>
    public string? ProtectedEamPassword { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string? EamPassword
    {
        get => Secret.Unprotect(ProtectedEamPassword, Entropy);
        set => ProtectedEamPassword = Secret.Protect(value, Entropy);
    }

    // Encryption labels kept from 0.15 (code name DCB) so saved passwords still open. Never change them.
    private static readonly byte[] Entropy = "DCB SRS EAM"u8.ToArray();
}

/// <summary>Windows DPAPI for the current user: unreadable to other accounts and other PCs.</summary>
internal static class Secret
{
    public static string? Protect(string? value, byte[] entropy) =>
        value is null ? null : Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(
            System.Text.Encoding.UTF8.GetBytes(value), entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser));

    public static string? Unprotect(string? data, byte[] entropy)
    {
        if (data is null) return null;
        try
        {
            return System.Text.Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(
                Convert.FromBase64String(data), entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser));
        }
        catch (Exception)
        {
            return null;  // another user's or PC's data, or damaged: ask again
        }
    }
}

/// <summary>A saved server ("connection template"): the Connect dialog's fields under a name, the
/// passwords DPAPI-encrypted for the current Windows user like the remembered login.</summary>
public sealed class ServerProfile
{
    public string Name { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = Core.NeboConnection.DefaultPort;
    public int SrsPort { get; set; } = Core.SrsListener.DefaultPort;
    public string? ProtectedPassword { get; set; }
    public string? ProtectedEamPassword { get; set; }
    /// <summary>The side last joined on this server ("blue"/"red"): the side picker's default.</summary>
    public string? LastSide { get; set; }
    /// <summary>The controller's callsign on this server, announced to the side's pilots (0.17).</summary>
    public string? Callsign { get; set; }
    /// <summary>The colour the controller's jets and area are marked in ("#rrggbb", 0.17).</summary>
    public string? Color { get; set; }
    /// <summary>The controller's area of operations on this server ("lat,lon;…"), sent again on connect.</summary>
    public string? Area { get; set; }
    /// <summary>Which of the areas the side's pilots see on their F10 map ("0110").</summary>
    public string? AreaF10 { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasPassword => ProtectedPassword is not null;
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasEamPassword => ProtectedEamPassword is not null;

    [System.Text.Json.Serialization.JsonIgnore]
    public string? Password { get => Secret.Unprotect(ProtectedPassword, Entropy); set => ProtectedPassword = Secret.Protect(value, Entropy); }

    [System.Text.Json.Serialization.JsonIgnore]
    public string? EamPassword { get => Secret.Unprotect(ProtectedEamPassword, Entropy); set => ProtectedEamPassword = Secret.Protect(value, Entropy); }

    // Kept from 0.15 (code name DCB) so saved server passwords still open. Never change it.
    private static readonly byte[] Entropy = "DCB server profile"u8.ToArray();
}

/// <summary>A workspace window's screen placement and what it was opened as.</summary>
public sealed class WindowPlacement
{
    public string Id { get; set; } = "";
    /// <summary>"GCI" or "ATC" for extra windows (title, default layout); "" for the main one.</summary>
    public string Preset { get; set; } = "";
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
    /// <summary>A workspace in a window of its own rather than a tab of the main window.</summary>
    public bool Detached { get; set; }
}

/// <summary>A package's name, frequency and radio, kept between sessions (members are not).</summary>
public sealed class PackageSettings
{
    public string? Name { get; set; }
    public double? Mhz { get; set; }
}

/// <summary>Per map window settings, keyed by the window's layout ContentId.</summary>
public sealed class MapSettings
{
    /// <summary>Ground units (vehicles, structures); null = the window's default (hidden in an
    /// ATC window, shown otherwise).</summary>
    public bool? ShowGroundUnits { get; set; }
    /// <summary>Aircraft on the ground or a deck; null = the default (hidden).</summary>
    public bool? ShowLanded { get; set; }
    public BaseMap BaseMap { get; set; } = BaseMap.Scope;
    public LabelLevel Labels { get; set; } = LabelLevel.Full;
    /// <summary>Each map filter's stage while it is on, by name ("zones" → 1 dim, 2 medium, 3 bright;
    /// missing = bright). On/off stays in the filter's own setting.</summary>
    public Dictionary<string, int> FilterLevels { get; set; } = [];
    /// <summary>Where moving aircraft's data blocks go: above them, behind (steady), or beside the vector.</summary>
    public TagPlacement Tags { get; set; } = TagPlacement.Above;
    public bool ThreatRings { get; set; } = true;
    /// <summary>The coalition's F10 map marks (players', scripts', NEBO's).</summary>
    public bool ShowMarks { get; set; } = true;
    /// <summary>Listen to pilots' designations (F-16 targeting pod / HAD); off until the controller wants them.</summary>
    public bool ShowDesignations { get; set; }
    public bool FootholdZones { get; set; } = true;
    public bool ShowAirfields { get; set; } = true;
    /// <summary>The DTC tab's plan (the open jet's cartridge) on this map, read-only.</summary>
    public bool ShowDtc { get; set; }
    /// <summary>Airport-diagram pictures under the live airfield data, when a folder is set.</summary>
    public bool ShowDiagrams { get; set; } = true;
    /// <summary>MGRS grid on the DCS Map base map.</summary>
    public bool ShowGrid { get; set; }
    /// <summary>Other controllers' areas and owner colours on this map (0.17).</summary>
    public OwnerView Owners { get; set; } = OwnerView.All;
    /// <summary>Base map brightness, 0.2..1; markers and overlays are not dimmed.</summary>
    public double Brightness { get; set; } = 1.0;
    public double? CenterX { get; set; }
    public double? CenterY { get; set; }
    public double? Resolution { get; set; }
}

/// <summary>The missile launch alarm's sound (Settings → Alerts). The banner and the ring
/// around the target show whatever these say.</summary>
public sealed class LaunchAlarmSettings
{
    public bool Sound { get; set; } = true;
    /// <summary>Loudness of the alarm, 0–1 of full scale (Quiet 0.2, Normal 0.5, Loud 1).</summary>
    public double Volume { get; set; } = 0.5;
    /// <summary>Alarms per launch, 1–3.</summary>
    public int Repeats { get; set; } = 2;
}

public sealed class AppSettings
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = Core.NeboConnection.DefaultPort;
    /// <summary>Saved servers (Connect dialog), and the name of the one used last.</summary>
    public List<ServerProfile> Servers { get; set; } = [];
    public string? LastServer { get; set; }
    /// <summary>Hold this key for the cursor's bullseye call (a WPF Key name: "B", "Space", "LeftShift"…).</summary>
    public string BullseyeKey { get; set; } = "B";
    /// <summary>Press this key over a map for the RWR scope of the selected (or pointed-at) own
    /// unit: bearing lines from what it hears.</summary>
    public string RwrKey { get; set; } = "R";
    /// <summary>Press this key over a map, then drag a box: NCTR on the contacts inside it.</summary>
    public string NctrKey { get; set; } = "N";
    /// <summary>Key and mouse bindings by action (Input.Bindings), e.g. "RwrScope" → "Mouse:XButton1";
    /// missing ones use the defaults (or the three older keys above).</summary>
    public Dictionary<string, string> Bindings { get; set; } = [];
    /// <summary>The bullseye binding switches the readout on and off instead of showing it while held.</summary>
    public bool BullseyeToggle { get; set; }
    /// <summary>"System" (follow Windows' app mode), "Dark" or "Light".</summary>
    public string Theme { get; set; } = "System";
    /// <summary>Ask GitHub once a day for a newer NEBO release (Updates), and when it last did.</summary>
    public bool CheckForUpdates { get; set; } = true;
    public DateTime LastUpdateCheck { get; set; }
    /// <summary>Recovery cases drawn on the Carrier view ("1".."3" → on/off); missing = only the current case.</summary>
    public Dictionary<string, bool> CarrierCases { get; set; } = [];
    /// <summary>"Remember" ticked in the connect dialog: the password is kept, encrypted with
    /// Windows DPAPI for the current user (unreadable to other accounts and other PCs).</summary>
    public bool RememberLogin { get; set; }
    public string? ProtectedPassword { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string? RememberedPassword
    {
        get
        {
            if (ProtectedPassword is null) return null;
            try
            {
                var data = System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(ProtectedPassword), Entropy,
                    System.Security.Cryptography.DataProtectionScope.CurrentUser);
                return System.Text.Encoding.UTF8.GetString(data);
            }
            catch (Exception)
            {
                return null;  // another user's or PC's data, or damaged: ask again
            }
        }
        set => ProtectedPassword = value is null ? null : Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(
            System.Text.Encoding.UTF8.GetBytes(value), Entropy, System.Security.Cryptography.DataProtectionScope.CurrentUser));
    }

    // Kept from 0.15 (code name DCB) so the saved connect password still opens. Never change it.
    private static readonly byte[] Entropy = "DCB connect"u8.ToArray();
    /// <summary>The DCS install the DCS maps, charts and airfield data are read from: null = search
    /// for it, "" = the controller chose to run without DCS.</summary>
    public string? DcsPath { get; set; }
    /// <summary>The DCS location was confirmed on first start (or later in Window → Map data).</summary>
    public bool DcsPathConfirmed { get; set; }
    /// <summary>Airport-diagram folder (with diagrams.json) per theatre id; never copied.</summary>
    public Dictionary<string, string> Diagrams { get; set; } = [];
    public Dictionary<string, MapSettings> Maps { get; set; } = [];
    public SrsSettings Srs { get; set; } = new();
    public LaunchAlarmSettings LaunchAlarm { get; set; } = new();
    public List<PackageSettings> Packages { get; set; } = [];
    /// <summary>The local port the NEBO Stream Deck plugin connects to (Integration.DeckLink).</summary>
    public int DeckPort { get; set; } = 47810;
    /// <summary>Contacts within this many NM of the mouse show their full labels (Settings → General).</summary>
    public double LabelHoverNm { get; set; } = 10;
    /// <summary>When the maps draw (Settings → General): "change" (only when something changed;
    /// the default), "hover" (every display frame while the mouse is on that map), "always"
    /// (every display frame). Drawing all the time keeps the GPU and WPF's frame loop awake, so
    /// the first frames of a zoom or drag are not late; it costs GPU time.</summary>
    public string MapFrames { get; set; } = "change";
    /// <summary>The controller's tags on players (Core.ObjectTags: "p:&lt;name&gt;" → tag).</summary>
    public Dictionary<string, string> ObjectTags { get; set; } = [];
    /// <summary>Every table's columns as the controller left them ("ObjectsPanel.Grid" → column → state).</summary>
    public Dictionary<string, Dictionary<string, ColumnState>> Tables { get; set; } = [];
    /// <summary>Where the main window was.</summary>
    public WindowPlacement? MainPlacement { get; set; }
    /// <summary>Extra workspace windows of older versions (before tabs): turned into tabs once.</summary>
    public List<WindowPlacement> Workspaces { get; set; } = [];
    /// <summary>The main window's workspace tabs in order (GCI, ATC…), detached ones with their window's placement.</summary>
    public List<WindowPlacement> Tabs { get; set; } = [];

    public static string LayoutPath => Path.Combine(AppPaths.Settings, "layout.xml");

    /// <summary>Docking layout of a workspace window: layout.xml for the main one.</summary>
    public static string LayoutPathFor(string workspaceId) =>
        workspaceId == Nebo.Client.MainWindow.MainId ? LayoutPath : Path.Combine(AppPaths.Settings, $"layout-{workspaceId}.xml");
    private static string SettingsPath => Path.Combine(AppPaths.Settings, "settings.json");
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new BaseMapConverter(), new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), Json) ?? new();
        }
        catch (Exception)
        {
            // A broken settings file must not stop the client starting; fall back to defaults.
        }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.Settings);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>ContentId of the ATC tab's airfield map (not a docking window of its own).</summary>
    public const string AtcMapId = "atc:airfield";

    /// <summary>Airfield the ATC tab showed last, reopened on start.</summary>
    public int? AtcAirfieldId { get; set; }

    public MapSettings MapFor(string contentId)
    {
        if (!Maps.TryGetValue(contentId, out var s))
        {
            // The ATC airfield view starts on the DCS map; general maps on the scope.
            Maps[contentId] = s = new MapSettings { BaseMap = contentId == AtcMapId ? BaseMap.DcsMap : BaseMap.Scope };
        }
        return s;
    }
}
