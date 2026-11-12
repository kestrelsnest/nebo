using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Nebo.Client.Core;
using Nebo.Client.Srs;

namespace Nebo.Client;

/// <summary>What the maps show of other controllers: everything (all areas and owner colours),
/// only this controller's (its area and jets), or none.</summary>
public enum OwnerView { All, Mine, None }

/// <summary>This controller's side of the flight board (DLL 0.17): tells the server who we are
/// (callsign, colour, GCI or ATC, frequency, area of operations) and keeps that current, turns
/// what happens (controllers on and off station, new pilots, handoffs, take-offs) into tray
/// messages with their buttons, picks up pilots, hands them over and answers handoffs.</summary>
public static class FlightDesk
{
    private static FlightBoard Board => AppState.Flights;
    private static (string Position, long Khz)? _sent;
    private static DateTime _lastTry;
    private static bool _areaSent;
    private static readonly Dictionary<int, DateTime> AtcUsed = [];
    /// <summary>The channel we transmitted on before a pick-up moved us to an ATC channel.</summary>
    private static int? _gciChannel;
    /// <summary>The other controllers last seen (null until the first list after connecting).</summary>
    private static Dictionary<long, ControllerInfo>? _others;

    /// <summary>This controller's area changed (drawn, cleared, or after connecting).</summary>
    public static event Action? AreaChanged;

    public static void Start()
    {
        AppState.Connection.Connected += () =>
        {
            _sent = null;
            _areaSent = false;
            _myAreas = "";
            _myF10 = "";
            _others = null;
            AtcUsed.Clear();
            MessageTray.Clear();
            _ = SendInfoAsync();
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) =>
        {
            if (!AppState.Connection.IsConnected || !Board.Available) return;
            // Resend when the window or frequency changed; retry a failed first one every 10 s.
            if (Board.MyId is null ? DateTime.UtcNow - _lastTry > TimeSpan.FromSeconds(10) : Current() != _sent)
                _ = SendInfoAsync();
            CheckAreas();
        };
        timer.Start();
        Board.Changed += OnBoardChanged;
        Board.Spawned += OnSpawned;
        Board.Offered += OnOffered;
        Board.TookOff += OnTookOff;
        Board.Answered += (h, ok) => MessageTray.Info(
            $"{Board.Controller(h.To)?.Display ?? "The other controller"} {(ok ? "took" : "declined")} {Pilots(h.Units)}",
            accent: ColorOf(Board.Controller(h.To)));
    }

    private static string Callsign => AppState.Connection.Callsign ?? "";

    // ---- Who we are -----------------------------------------------------------------------

    /// <summary>What this controller works now: the ATC tab's airfield, else GCI; and the
    /// frequency we transmit on.</summary>
    public static (string Position, long Khz) Current()
    {
        var atc = Application.Current.MainWindow is MainWindow m && m.CurrentPreset == MainWindow.AtcPreset;
        var position = atc && AppState.SelectedAirfieldId is { } id ? $"atc:{id}" : "gci";
        var mhz = SrsChannels.Transmitting is { } ch ? SrsChannels.Mhz(ch) : null;
        return (position, Khz(mhz));
    }

    private static long Khz(double? mhz) => mhz is { } f and > 0 and <= 400 ? (long)Math.Round(f * 1000) : 0;

    private static async Task SendInfoAsync()
    {
        var now = Current();
        _lastTry = DateTime.UtcNow;
        _sent = now;
        var r = await AppState.Connection.SendCommandAsync("controller_info", new Dictionary<string, object>
        {
            ["callsign"] = Callsign,
            ["position"] = now.Position,
            ["khz"] = now.Khz,
            ["color"] = AppState.Connection.Color ?? "",
        });
        if (r.Ok && long.TryParse(r.Value, out var id))
        {
            Board.MyId = id;
            if (!_areaSent && AppState.Connection.Profile is { Area: { Length: > 0 } saved } profile)
            {
                _areaSent = true;
                _myAreas = saved;
                await SendAreaAsync(saved, profile.AreaF10 ?? "");
            }
        }
        else if (!r.Ok) _sent = null;  // an older server or a hiccup: tried again later
    }

    // ---- Area of operations -----------------------------------------------------------------

    /// <summary>This controller's areas as last sent ("…|…").</summary>
    private static string _myAreas = "";
    /// <summary>Which of them the side's pilots see on their F10 map ("0110"), as last sent.</summary>
    private static string _myF10 = "";

    /// <summary>Whether the side's pilots see this controller's area (its index) on their F10 map.</summary>
    public static bool MyAreaOnF10(int index) => Areas.Flag(_myF10, index);

    /// <summary>Shows one of this controller's areas to the side's pilots on their F10 map, or not.</summary>
    public static Task ShowAreaToPilotsAsync(int index, bool show)
    {
        var areas = MyAreas;
        if (index < 0 || index >= areas.Count) return Task.CompletedTask;
        return SetAreasAsync(areas, Areas.SetFlag(_myF10, index, show, areas.Count));
    }

    /// <summary>This controller's areas, each its corners.</summary>
    public static IReadOnlyList<IReadOnlyList<(double Lat, double Lon)>> MyAreas => Areas.ParseAll(_myAreas);

    /// <summary>Adds an area (3–40 corners) to this controller's (at most 10), for every controller
    /// of the side; kept with the server profile and sent again on the next connect.</summary>
    public static async Task AddAreaAsync(IReadOnlyList<(double Lat, double Lon)> points)
    {
        var areas = MyAreas.ToList();
        if (areas.Count >= Areas.MaxAreas) { MessageTray.Info($"You have {Areas.MaxAreas} areas: remove one first"); return; }
        areas.Add(points);
        await SetAreasAsync(areas, _myF10);
    }

    /// <summary>Removes one of this controller's areas (its index in <see cref="MyAreas"/>).</summary>
    public static Task RemoveAreaAsync(int index)
    {
        var areas = MyAreas.ToList();
        if (index < 0 || index >= areas.Count) return Task.CompletedTask;
        areas.RemoveAt(index);
        return SetAreasAsync(areas, Areas.RemoveFlag(_myF10, index));
    }

    public static Task ClearAreasAsync() => SetAreasAsync([], "");

    /// <summary>The index of this controller's area holding a point, or -1.</summary>
    public static int MyAreaAt(double lat, double lon)
    {
        var areas = MyAreas;
        for (var i = areas.Count - 1; i >= 0; i--)
            if (Areas.Contains(areas[i], lat, lon)) return i;
        return -1;
    }

    private static async Task SetAreasAsync(IReadOnlyList<IReadOnlyList<(double Lat, double Lon)>> areas, string f10)
    {
        var text = Areas.FormatAll(areas);
        if (AppState.Connection.Profile is { } p) { p.Area = text; p.AreaF10 = f10; }
        var before = _myF10;
        await SendAreaAsync(text, f10);
        if (_myF10 != before && _myF10.Count(ch => ch == '1') > before.Count(ch => ch == '1'))
            MessageTray.Info("Area shown to your side's pilots on their F10 map");
    }

    private static async Task SendAreaAsync(string text, string f10)
    {
        var r = await AppState.Connection.SendCommandAsync("area_set", new Dictionary<string, object> { ["area"] = text, ["f10"] = f10 });
        if (!r.Ok) { MessageTray.Info($"Area not set: {r.Error}"); return; }
        _myAreas = text;
        _myF10 = f10;
        if (text.Length == 0) MessageTray.Info("Your areas of operations are cleared");
        AreaChanged?.Invoke();
    }

    /// <summary>The controllers whose areas a map shows for its owner filter.</summary>
    public static IEnumerable<ControllerInfo> AreasShown(OwnerView view) => view switch
    {
        OwnerView.All => Board.Controllers.Where(c => c.Area.Length > 0),
        OwnerView.Mine => Board.Controllers.Where(c => c.Id == Board.MyId && c.Area.Length > 0),
        _ => [],
    };

    /// <summary>The controller whose area holds a point, preferring this controller's.</summary>
    public static ControllerInfo? AreaOwnerAt(double lat, double lon) =>
        Board.Controllers.Where(c => Areas.Contains(c.AreaPolygons, lat, lon)).OrderBy(c => c.Id == Board.MyId ? 0 : 1).FirstOrDefault();

    // ---- Owner colours -----------------------------------------------------------------------

    public static Color? ColorOf(ControllerInfo? c) => c?.Rgb is var (r, g, b) ? Color.FromRgb(r, g, b) : null;

    /// <summary>The colour a taken aircraft is drawn in on a map with this filter, or null.</summary>
    public static Color? OwnerColor(long unit, OwnerView view)
    {
        if (view == OwnerView.None || Board.Flight(unit) is not { Owner: { } owner } f) return null;
        if (view == OwnerView.Mine && !Board.Mine(f)) return null;
        return ColorOf(Board.Controller(owner)) ?? Color.FromRgb(0xB0, 0xB0, 0xB0);
    }

    /// <summary>Whether a pilot with a handoff waiting for an answer is in the off half of its
    /// blink (twice a second).</summary>
    public static bool HandoffBlinkOff(long unit) =>
        Board.HandoffOf(unit) is not null && Environment.TickCount64 / 250 % 2 == 1;

    // ---- Tray messages -----------------------------------------------------------------------

    private static void OnBoardChanged()
    {
        if (!Board.Available && Board.Controllers.Count == 0) return;
        var now = Board.Others.ToDictionary(c => c.Id);
        if (_others is not null && Board.MyId is not null)
        {
            foreach (var (id, c) in now)
                if (!_others.ContainsKey(id))
                {
                    MessageTray.Info($"{c.Describe} on station", c.User.Length > 0 && c.User != c.Callsign ? c.User : null, ColorOf(c));
                    Chime.Play(Chime.Station);
                }
            foreach (var (id, c) in _others)
                if (!now.ContainsKey(id)) MessageTray.Info($"{c.Display} off station", null, ColorOf(c));
        }
        if (Board.MyId is not null) _others = now;
        MessageTray.Prune();
    }

    /// <summary>Where a pilot is: his aircraft on the picture, else his airfield.</summary>
    private static (double Lat, double Lon)? PositionOf(FlightInfo f)
    {
        if (AppState.Store.Find(f.Unit)?.Unit is { Lat: { } lat, Lon: { } lon }) return (lat, lon);
        if (AppState.Store.Theatre is { } t && AppState.Store.Airfields.FirstOrDefault(a => a.Id == f.Field) is { } field)
            return t.ToLatLon(field.X, field.Z);
        return null;
    }

    private static void OnSpawned(FlightInfo f)
    {
        var where = f.FieldName.Length > 0 ? f.FieldName : "the ground";
        var area = PositionOf(f) is var (lat, lon) ? AreaOwnerAt(lat, lon) : null;
        var mine = area is not null && area.Id == Board.MyId;
        // Strong for me when it is in my area or in nobody's; a quiet line in another's area.
        var strong = area is null || mine;
        MessageTray.Post(new TrayMessage
        {
            Title = $"New pilot {f.Player}",
            Detail = $"{Symbology.ShortType(f.Type)} · {where}" + (mine ? " · in your area" : area is not null ? $" · in {area.Display}'s area" : ""),
            Accent = strong ? Color.FromRgb(0xEF, 0x9F, 0x27) : ColorOf(area),
            Strong = strong,
            Key = $"spawn:{f.Unit}",
            Actions = [new TrayAction("Take", _ => PickUpAsync(f), "An ATC channel (4–6) goes to his tower, you transmit there, and he is told on screen")],
            Obsolete = () => Board.Flight(f.Unit) is not { Owner: null } now || now.Since != f.Since,
        });
        if (strong) Chime.Play(Chime.Rise);
    }

    private static void OnOffered(HandoffInfo h)
    {
        var from = Board.Controller(h.From);
        var pkg = h.Package.Length > 0 ? $"Package {h.Package}{(h.Khz > 0 ? $" · {h.Khz / 1000.0:0.000}" : "")}" : null;
        MessageTray.Post(new TrayMessage
        {
            Title = $"{from?.Display ?? "A controller"} hands you {Pilots(h.Units)}",
            Detail = pkg,
            Accent = Color.FromRgb(0x63, 0x99, 0x22),
            Strong = true,
            Key = $"offer:{h.Id}",
            Actions =
            [
                new TrayAction("Accept", _ => AnswerAsync(h, true), "Take these pilots: they are told to contact you"),
                new TrayAction("Decline", _ => AnswerAsync(h, false), "They stay with the other controller"),
            ],
            Obsolete = () => Board.Handoffs.All(o => o.Id != h.Id),
        });
        Chime.Play(Chime.Rise);
    }

    private static void OnTookOff(FlightInfo f)
    {
        MessageTray.Post(new TrayMessage
        {
            Title = $"{f.Player} airborne",
            Detail = f.FieldName.Length > 0 ? $"From {f.FieldName}" : null,
            Accent = ColorOf(Board.Controller(Board.MyId)) ?? Color.FromRgb(0x4C, 0xB8, 0x64),
            Strong = true,
            Key = $"air:{f.Unit}",
            Actions = [new TrayAction("Keep", _ => KeepAsync(f), "Back to your channel; he is told to contact you there")],
            Obsolete = () => Board.Flight(f.Unit) is not { } now || !Board.Mine(now) || now.OnGround || Board.HandoffOf(f.Unit) is not null,
        });
        Chime.Play(Chime.Rise);
    }

    // ---- My pilot enters another controller's area of operations -----------------------------

    private static readonly AreaEntries Entries = new();

    /// <summary>Every 2 s: an airborne pilot of mine inside another controller's area (and in none
    /// of mine) → one prompt to hand him off to that controller, again only after he left it.</summary>
    private static void CheckAreas()
    {
        if (Board.MyId is null) return;
        var others = Board.Others.Where(c => c.Area.Length > 0).ToList();
        var mineAreas = Board.Controller(Board.MyId)?.AreaPolygons;
        var tracks = AppState.Store.Tracks.ToDictionary(t => t.Unit.Id);
        var flying = Board.Flights.Where(f => Board.Mine(f) && !f.OnGround).ToList();
        Entries.Keep(flying.Select(f => f.Unit));
        foreach (var f in flying)
        {
            if (!tracks.TryGetValue(f.Unit, out var t) || t.Unit.Lat is not { } lat || t.Unit.Lon is not { } lon) continue;
            var inMine = mineAreas is not null && Areas.Contains(mineAreas, lat, lon);
            var other = inMine ? null : others.FirstOrDefault(c => Areas.Contains(c.AreaPolygons, lat, lon));
            if (Entries.Entered(f.Unit, other?.Id) is null || other is null || Board.HandoffOf(f.Unit) is not null) continue;
            PostAreaEntry(f, other);
        }
    }

    private static void PostAreaEntry(FlightInfo f, ControllerInfo to)
    {
        MessageTray.Post(new TrayMessage
        {
            Title = $"{f.Player} entering {to.Display}'s area",
            Detail = to.Describe,
            Accent = ColorOf(to) ?? Color.FromRgb(0x4C, 0xB8, 0xE4),
            Strong = true,
            Key = $"area:{f.Unit}",
            Actions =
            [
                new TrayAction($"Hand off to {to.Display}", _ => HandOffAsync([f.Unit], to), $"Offer {f.Player} to {to.Display}; he is told to contact them once accepted"),
                new TrayAction("Keep", _ => Task.CompletedTask, "Keep him; asked again only if he leaves the area and comes back"),
            ],
            Obsolete = () => Board.Flight(f.Unit) is not { } now || !Board.Mine(now) || Board.HandoffOf(f.Unit) is not null
                || Board.Controller(to.Id) is null,
        });
        Chime.Play(Chime.Station);
    }

    // ---- Menus (map, Objects, tray) -----------------------------------------------------------

    /// <summary>"Take", "Hand off to ▸", "Release" for a player's aircraft (none for others, or
    /// when the server sends no flights).</summary>
    public static IEnumerable<object> MenuItems(long unit)
    {
        if (!Board.Available || Board.Flight(unit) is not { } f) yield break;
        var owner = Board.Controller(f.Owner);
        var waiting = Board.HandoffOf(unit) is not null;
        if (f.Owner is null)
        {
            var take = new MenuItem { Header = $"Take {f.Player}", IsEnabled = !waiting };
            take.Click += async (_, _) => await PickUpAsync(f);
            yield return take;
        }
        else if (Board.Mine(f))
        {
            var hand = new MenuItem { Header = "Hand off to", IsEnabled = !waiting };
            foreach (var item in HandOffItems(f)) hand.Items.Add(item);
            yield return hand;
            var release = new MenuItem { Header = $"Release {f.Player}" };
            release.Click += async (_, _) => await ReleaseAsync([unit]);
            yield return release;
        }
        else
            yield return new MenuItem { Header = $"{f.Player}: with {owner?.Display ?? "another controller"}", IsEnabled = false };
    }

    /// <summary>One item per other controller (GCI first); a pilot in one of my packages can go
    /// with the whole package.</summary>
    private static IEnumerable<MenuItem> HandOffItems(FlightInfo f)
    {
        var others = Board.Others.OrderBy(c => c.IsGci ? 0 : 1).ThenBy(c => c.Display).ToList();
        if (others.Count == 0) yield return new MenuItem { Header = "No other controller on this side", IsEnabled = false };
        var package = AppState.Packages.PackageOf(f.Unit);
        var packUnits = package?.Members.Where(u => Board.Flight(u) is { } pf && Board.Mine(pf)).ToList();
        foreach (var c in others)
        {
            var item = new MenuItem { Header = $"{f.Player} → {c.Describe}" };
            item.Click += async (_, _) => await HandOffAsync([f.Unit], c);
            yield return item;
            if (package is not null && packUnits is { Count: > 1 })
            {
                var whole = new MenuItem { Header = $"{package.Tag} {package.Name} ({packUnits.Count} pilots) → {c.Describe}" };
                whole.Click += async (_, _) => await HandOffAsync(packUnits, c, package);
                yield return whole;
            }
        }
    }

    // ---- Actions ------------------------------------------------------------------------------

    /// <summary>Picks up a pilot: claims him, tunes an ATC channel (4–6) to his airfield's tower
    /// and transmits on it, and tells him on screen who is with him on which frequency.</summary>
    public static async Task PickUpAsync(FlightInfo f)
    {
        var r = await AppState.Connection.SendCommandAsync("claim", new Dictionary<string, object> { ["units"] = new[] { f.Unit.ToString() } });
        if (!r.Ok) { MessageTray.Info($"Not taken: {r.Error}"); return; }

        var field = f.FieldName.Length > 0 ? f.FieldName : "NEBO";
        string text;
        if (TowerOf(f) is { } tower)
        {
            var ch = ChannelPlan.ChooseAtc(SrsChannels.Mhz, tower.Mhz, AtcUsed);
            AtcUsed[ch] = DateTime.UtcNow;
            if (SrsChannels.Transmitting is { } was && !ChannelPlan.IsAtc(was)) _gciChannel = was;
            SrsChannels.Set(ch, tower.Mhz);
            SrsChannels.Transmit(ch);
            text = $"NEBO: {Callsign}, {field} Tower, with you on {tower.Mhz:0.000} {tower.Modulation}. Call when ready to taxi.";
            MessageTray.Info($"You have {f.Player}", $"CH{ch} on {field} Tower {tower.Mhz:0.000}", ColorOf(Board.Controller(Board.MyId)));
        }
        else
        {
            var mine = Current().Khz;
            text = mine > 0
                ? $"NEBO: {Callsign} with you on {mine / 1000.0:0.000}. Call when ready to taxi."
                : $"NEBO: {Callsign} is watching you. Call when ready to taxi.";
            MessageTray.Info($"You have {f.Player}", $"No tower frequency known for {field}", ColorOf(Board.Controller(Board.MyId)));
        }
        await TextAsync(f.Unit, text);
    }

    /// <summary>The airfield's tower frequency the pilot can hear: one his SRS radios are already
    /// on, else UHF, else the first one listed.</summary>
    private static AirfieldFrequency? TowerOf(FlightInfo f)
    {
        if (f.Field <= 0 || AppState.Store.Theatre is not { } theatre) return null;
        var list = SrsBridge.FrequenciesOf(theatre.Id, (int)f.Field);
        if (list.Count == 0) return null;
        if (SrsClientOf(f) is { } c)
            foreach (var (_, ch, _) in SrsRoster.RadiosOf(c))
                if (list.FirstOrDefault(t => Math.Abs(t.Mhz * 1e6 - ch.Hz) < 500) is { } heard) return heard;
        return list.FirstOrDefault(t => t.Band.Contains("UHF", StringComparison.OrdinalIgnoreCase)) ?? list[0];
    }

    /// <summary>The pilot's SRS client: by DCS's runtime id (what SRS reports), else by name.</summary>
    public static SrsClient? SrsClientOf(FlightInfo f)
    {
        var roster = SrsListen.Instance.Roster;
        return roster.Own.FirstOrDefault(c => f.Rid > 0 && c.RadioInfo?.UnitId == (uint)f.Rid) ?? roster.ByPlayer(f.Player);
    }

    public static async Task ReleaseAsync(IReadOnlyList<long> units)
    {
        var r = await AppState.Connection.SendCommandAsync("release", new Dictionary<string, object> { ["units"] = units.Select(u => u.ToString()).ToArray() });
        MessageTray.Info(r.Ok ? $"Released {Pilots(units)}" : $"Not released: {r.Error}");
    }

    /// <summary>Offers pilots to another controller; a package's name and frequency go along.</summary>
    public static async Task HandOffAsync(IReadOnlyList<long> units, ControllerInfo to, Package? package = null)
    {
        var fields = new Dictionary<string, object>
        {
            ["units"] = units.Select(u => u.ToString()).ToArray(),
            ["to"] = to.Id,
            ["package"] = package is null ? "" : SafeName(package.Name),
            ["khz"] = Khz(package?.Mhz),
        };
        var r = await AppState.Connection.SendCommandAsync("handoff", fields);
        if (!r.Ok) { MessageTray.Info($"Not handed off: {r.Error}"); return; }
        if (!long.TryParse(r.Value, out var id)) return;
        // The flights list shows the handoff a moment later: gone only once it was there.
        var seen = false;
        var posted = DateTime.Now;
        bool Answered()
        {
            if (Board.Handoffs.Any(h => h.Id == id)) { seen = true; return false; }
            return seen || DateTime.Now - posted > TimeSpan.FromSeconds(30);
        }
        MessageTray.Post(new TrayMessage
        {
            Title = $"Offered {Pilots(units)} to {to.Display}",
            Detail = "Waiting for an answer",
            Accent = ColorOf(to),
            Key = $"waiting:{id}",
            Actions = [new TrayAction("Take back", _ => CancelAsync(id), "Withdraw the offer")],
            Obsolete = Answered,
        });
    }

    private static async Task CancelAsync(long handoff)
    {
        var r = await AppState.Connection.SendCommandAsync("handoff_answer", new Dictionary<string, object> { ["handoff"] = handoff, ["accept"] = false });
        if (!r.Ok) MessageTray.Info($"Not taken back: {r.Error}");
    }

    /// <summary>Answers a handoff offered to us. Accepted: the pilots are told to contact us (on
    /// the package's frequency if it came with one), and a handed-over package fills a free one.</summary>
    public static async Task AnswerAsync(HandoffInfo h, bool accept)
    {
        var r = await AppState.Connection.SendCommandAsync("handoff_answer", new Dictionary<string, object> { ["handoff"] = h.Id, ["accept"] = accept });
        if (!r.Ok) { MessageTray.Info($"Not answered: {r.Error}"); return; }
        if (!accept) return;

        var khz = h.Khz > 0 ? h.Khz : Current().Khz;
        if (h.Package.Length > 0 && AppState.Packages.All.FirstOrDefault(p => p.Members.Count == 0) is { } free)
        {
            free.Name = h.Package;
            if (h.Khz > 0) free.Mhz = h.Khz / 1000.0;
            foreach (var u in h.Units) AppState.Packages.Add(free.Number, u);
            AppState.Packages.Edited();
        }
        var text = khz > 0 ? $"NEBO: {Callsign} has you. Contact {Callsign} on {khz / 1000.0:0.000}." : $"NEBO: {Callsign} has you now.";
        foreach (var u in h.Units) await TextAsync(u, text);
    }

    /// <summary>After take-off, keeping the pilot: back to the channel we used before the pick-up,
    /// and the pilot is told to contact us there.</summary>
    public static async Task KeepAsync(FlightInfo f)
    {
        if (_gciChannel is { } ch && SrsChannels.Mhz(ch) is not null) SrsChannels.Transmit(ch);
        var khz = Current().Khz;
        await TextAsync(f.Unit, khz > 0 ? $"NEBO: {Callsign}. Airborne: contact {Callsign} on {khz / 1000.0:0.000}." : $"NEBO: {Callsign}. Airborne: stay with me.");
    }

    private static Task<CommandResult> TextAsync(long unit, string text) =>
        AppState.Connection.SendCommandAsync("message_unit", new Dictionary<string, object> { ["unit"] = unit, ["text"] = text, ["seconds"] = 20 });

    private static string Pilots(IReadOnlyList<long> units)
    {
        var names = units.Select(u => Board.Flight(u)?.Player ?? $"#{u}").ToList();
        return names.Count <= 2 ? string.Join(" and ", names) : $"{names[0]} and {names.Count - 1} more";
    }

    /// <summary>A package name the server takes (its callsign rule), cut to fit.</summary>
    private static string SafeName(string name)
    {
        var s = new string(name.Where(c => c >= 0x80 || char.IsAsciiLetterOrDigit(c) || " -/.':".Contains(c)).ToArray()).Trim();
        while (!Callsigns.IsValid(s) && s.Length > 0) s = s[..^1];
        return s;
    }

    /// <summary>Short sounds at half the launch alarm's loudness: a rising two-note chime (new
    /// pilot, handoff, take-off) and one soft note (a controller on station).</summary>
    private static class Chime
    {
        public static readonly double[] Rise = [880, 1320];
        public static readonly double[] Station = [660];
        private static readonly Dictionary<double[], (double Volume, byte[] Wav)> Cache = [];
        private static int _busy;

        public static void Play(double[] notes)
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1) return;
            Task.Run(() =>
            {
                try
                {
                    var volume = Math.Clamp(AppState.Settings.LaunchAlarm.Volume, 0.05, 1);
                    byte[] wav;
                    lock (Cache)
                    {
                        if (!Cache.TryGetValue(notes, out var c) || c.Volume != volume) Cache[notes] = c = (volume, Make(notes, volume));
                        wav = c.Wav;
                    }
                    using var player = new SoundPlayer(new MemoryStream(wav));
                    player.Load();
                    player.PlaySync();
                }
                catch (Exception) { /* no audio device: the tray still shows it */ }
                finally { Interlocked.Exchange(ref _busy, 0); }
            });
        }

        private static byte[] Make(double[] notes, double loudness)
        {
            const int rate = 22050;
            var samples = new List<short>();
            foreach (var hz in notes)
            {
                var n = (int)(rate * (notes.Length == 1 ? 0.16 : 0.09));
                for (var i = 0; i < n; i++)
                {
                    var edge = Math.Min(1, Math.Min(i, n - i) / (rate * 0.008));
                    samples.Add((short)(Math.Sin(2 * Math.PI * hz * i / rate) * loudness * 0.5 * edge * short.MaxValue));
                }
                samples.AddRange(new short[(int)(rate * 0.03)]);
            }
            samples.AddRange(new short[(int)(rate * 0.2)]);
            return LaunchAlarm.Wav(samples, rate);
        }
    }
}
