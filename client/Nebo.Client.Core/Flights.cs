using System.Text.Json.Serialization;

namespace Nebo.Client.Core;

/// <summary>The "controllers" message (DLL 0.17): the side's logged-in controllers.</summary>
public sealed record ControllersMessage(
    [property: JsonPropertyName("coalition")] string Coalition,
    [property: JsonPropertyName("controllers")] IReadOnlyList<ControllerInfo> Controllers);

/// <summary>A logged-in controller: id (for handoffs), callsign, Discord name, what they work
/// ("gci", "atc:&lt;airfield id&gt;" or ""), their frequency in kHz (0: none), the colour their
/// jets are marked in ("#rrggbb" or "") and their area of operations ("lat,lon;…" or "").</summary>
public sealed record ControllerInfo(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("callsign")] string Callsign,
    [property: JsonPropertyName("user")] string User,
    [property: JsonPropertyName("position")] string Position,
    [property: JsonPropertyName("khz")] long Khz,
    [property: JsonPropertyName("color")] string Color = "",
    [property: JsonPropertyName("area")] string Area = "",
    [property: JsonPropertyName("f10")] string F10 = "")
{
    /// <summary>The controller's areas (each its corners), empty when none.</summary>
    [JsonIgnore] public IReadOnlyList<IReadOnlyList<(double Lat, double Lon)>> AreaPolygons => Areas.ParseAll(Area);
    /// <summary>Whether the side's pilots see this controller's area (its index) on their F10 map.</summary>
    public bool OnF10(int index) => Areas.Flag(F10, index);
    /// <summary>The colour as RGB, or null when none was given.</summary>
    [JsonIgnore] public (byte R, byte G, byte B)? Rgb => OwnerColors.Parse(Color);

    /// <summary>Callsign, else Discord name, else "#id".</summary>
    public string Display => Callsign.Length > 0 ? Callsign : User.Length > 0 ? User : $"#{Id}";
    public bool IsGci => Position == "gci";
    public bool IsAtc => Position.StartsWith("atc:", StringComparison.Ordinal);
    /// <summary>The airfield an ATC controller works ("atc:&lt;id&gt;"), else null.</summary>
    public int? AtcField => IsAtc && int.TryParse(Position.AsSpan(4), out var id) ? id : null;
    public double? Mhz => Khz > 0 ? Khz / 1000.0 : null;
    /// <summary>"Overlord (GCI) 251.000".</summary>
    public string Describe => Display + (IsGci ? " (GCI)" : IsAtc ? " (ATC)" : "") + (Mhz is { } f ? $" {f:0.000}" : "");
    /// <summary>Two letters for map labels: "OV".</summary>
    public string Initials => new string(Display.Where(char.IsLetterOrDigit).Take(2).ToArray()).ToUpperInvariant();
}

/// <summary>The "flights" message (DLL 0.17): the side's player aircraft, their state and owner,
/// and the handoffs waiting for an answer.</summary>
public sealed record FlightsMessage(
    [property: JsonPropertyName("coalition")] string Coalition,
    [property: JsonPropertyName("flights")] IReadOnlyList<FlightInfo> Flights,
    [property: JsonPropertyName("handoffs")] IReadOnlyList<HandoffInfo> Handoffs);

/// <summary>A player's aircraft. <c>Rid</c>: DCS's runtime object id (what SRS reports as the
/// unitId). <c>State</c>: "ground", "air" or "landed". <c>Field</c>: the airfield it spawned at,
/// took off from or landed at (0: none). <c>Event</c>: the last event (birth, takeoff, land, air,
/// ground). <c>Owner</c>: the controller who has it, or null.</summary>
public sealed record FlightInfo(
    [property: JsonPropertyName("unit")] long Unit,
    [property: JsonPropertyName("rid")] long Rid,
    [property: JsonPropertyName("group")] long Group,
    [property: JsonPropertyName("gname")] string GroupName,
    [property: JsonPropertyName("player")] string Player,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("field")] long Field,
    [property: JsonPropertyName("field_name")] string FieldName,
    [property: JsonPropertyName("since")] double Since,
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("owner")] long? Owner)
{
    public bool OnGround => State != "air";
}

/// <summary>A handoff waiting for the receiver: from and to are controller ids; the package name
/// and frequency (kHz, 0: none) travel with it when a package is handed off.</summary>
public sealed record HandoffInfo(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("from")] long From,
    [property: JsonPropertyName("to")] long To,
    [property: JsonPropertyName("units")] IReadOnlyList<long> Units,
    [property: JsonPropertyName("package")] string Package,
    [property: JsonPropertyName("khz")] long Khz,
    [property: JsonPropertyName("time")] double Time);

/// <summary>The side's controllers, player aircraft and handoffs as the server last sent them,
/// and what changed for this controller: a new pilot spawned with nobody on him, a pilot of
/// mine took off, a handoff offered to me, my handoff answered. Lives on the UI thread.</summary>
public sealed class FlightBoard
{
    private bool _seenFlights;

    /// <summary>This controller's id (the answer to controller_info), null until known.</summary>
    public long? MyId { get; set; }
    /// <summary>The server sends flights (DLL 0.17 or later).</summary>
    public bool Available => _seenFlights;
    public IReadOnlyList<ControllerInfo> Controllers { get; private set; } = [];
    public IReadOnlyList<FlightInfo> Flights { get; private set; } = [];
    public IReadOnlyList<HandoffInfo> Handoffs { get; private set; } = [];

    public event Action? Changed;
    /// <summary>A pilot spawned on the ground with no controller on him (not raised for the
    /// first list after connecting).</summary>
    public event Action<FlightInfo>? Spawned;
    /// <summary>A pilot this controller has took off.</summary>
    public event Action<FlightInfo>? TookOff;
    /// <summary>Another controller hands this controller some pilots.</summary>
    public event Action<HandoffInfo>? Offered;
    /// <summary>A handoff of this controller's is gone: true when the receiver took the pilots.</summary>
    public event Action<HandoffInfo, bool>? Answered;

    public ControllerInfo? Controller(long? id) => id is { } i ? Controllers.FirstOrDefault(c => c.Id == i) : null;
    public FlightInfo? Flight(long unit) => Flights.FirstOrDefault(f => f.Unit == unit);
    public bool Mine(FlightInfo f) => MyId is { } me && f.Owner == me;
    /// <summary>Who has a flight, for lists: "me", a callsign, or "".</summary>
    public string OwnerName(FlightInfo f) => f.Owner is null ? "" : Mine(f) ? "me" : Controller(f.Owner)?.Display ?? $"#{f.Owner}";
    /// <summary>The other controllers of the side.</summary>
    public IEnumerable<ControllerInfo> Others => Controllers.Where(c => c.Id != MyId);
    /// <summary>A handoff waiting with this unit in it.</summary>
    public HandoffInfo? HandoffOf(long unit) => Handoffs.FirstOrDefault(h => h.Units.Contains(unit));

    public void ApplyControllers(ControllersMessage m)
    {
        Controllers = m.Controllers;
        Changed?.Invoke();
    }

    public void ApplyFlights(FlightsMessage m)
    {
        var before = Flights.ToDictionary(f => f.Unit);
        var handoffsBefore = Handoffs;
        var first = !_seenFlights;
        Flights = m.Flights;
        Handoffs = m.Handoffs;
        _seenFlights = true;
        if (!first)
        {
            foreach (var f in m.Flights)
            {
                before.TryGetValue(f.Unit, out var old);
                if (f.Event == "birth" && f.Owner is null && (old is null || old.Since != f.Since)) Spawned?.Invoke(f);
                if (Mine(f) && f.Event == "takeoff" && old is { State: not "air" }) TookOff?.Invoke(f);
            }
            foreach (var h in m.Handoffs)
                if (h.To == MyId && handoffsBefore.All(o => o.Id != h.Id)) Offered?.Invoke(h);
            foreach (var h in handoffsBefore)
                if (h.From == MyId && m.Handoffs.All(n => n.Id != h.Id))
                    Answered?.Invoke(h, h.Units.Any(u => Flight(u)?.Owner == h.To));
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        _seenFlights = false;
        MyId = null;
        Controllers = [];
        Flights = [];
        Handoffs = [];
        Changed?.Invoke();
    }
}

/// <summary>The rule the server (DLL 0.17) applies to callsigns: at most 24 UTF-8 bytes; ASCII
/// letters, digits, space and - / . ' : (other scripts' letters pass as they are).</summary>
public static class Callsigns
{
    public const int MaxBytes = 24;

    public static bool IsValid(string s) =>
        System.Text.Encoding.UTF8.GetByteCount(s) <= MaxBytes
        && s.All(c => c >= 0x80 || char.IsAsciiLetterOrDigit(c) || " -/.':".Contains(c));
}

/// <summary>Areas of operations: polygons of lat/lon corners, sent as "lat,lon;lat,lon;…", several
/// split by "|".</summary>
public static class Areas
{
    /// <summary>Whether a flag string ("0110", one per area) has area <paramref name="index"/> set.</summary>
    public static bool Flag(string flags, int index) => index >= 0 && index < flags.Length && flags[index] == '1';

    /// <summary>A flag string with area <paramref name="index"/> set or cleared, as long as <paramref name="count"/> areas.</summary>
    public static string SetFlag(string flags, int index, bool on, int count)
    {
        var chars = Enumerable.Range(0, count).Select(i => Flag(flags, i) ? '1' : '0').ToArray();
        if (index >= 0 && index < count) chars[index] = on ? '1' : '0';
        var text = new string(chars).TrimEnd('0');
        return text;
    }
    /// <summary>The flags without area <paramref name="index"/> (the areas after it move up).</summary>
    public static string RemoveFlag(string flags, int index) =>
        index >= 0 && index < flags.Length ? flags.Remove(index, 1).TrimEnd('0') : flags;

    public const int MinPoints = 3, MaxPoints = 40, MaxAreas = 10;

    public static IReadOnlyList<IReadOnlyList<(double Lat, double Lon)>> ParseAll(string text) =>
        text.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(Parse).Where(p => p.Count > 0).Take(MaxAreas).ToList();

    public static string FormatAll(IEnumerable<IReadOnlyList<(double Lat, double Lon)>> areas) =>
        string.Join("|", areas.Where(a => a.Count >= MinPoints).Take(MaxAreas).Select(Format));

    /// <summary>Whether a point lies in any of the areas.</summary>
    public static bool Contains(IReadOnlyList<IReadOnlyList<(double Lat, double Lon)>> areas, double lat, double lon) =>
        areas.Any(a => Contains(a, lat, lon));

    public static IReadOnlyList<(double Lat, double Lon)> Parse(string text)
    {
        var points = new List<(double, double)>();
        foreach (var pair in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = pair.Split(',');
            if (p.Length == 2
                && double.TryParse(p[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lat)
                && double.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lon)
                && lat is >= -90 and <= 90 && lon is >= -180 and <= 180)
                points.Add((lat, lon));
        }
        return points.Count >= MinPoints ? points : [];
    }

    public static string Format(IEnumerable<(double Lat, double Lon)> points) =>
        string.Join(";", points.Take(MaxPoints).Select(p => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{p.Lat:0.#####},{p.Lon:0.#####}")));

    /// <summary>Whether a point lies inside the polygon (even-odd rule on lat/lon; areas are small
    /// enough for that).</summary>
    public static bool Contains(IReadOnlyList<(double Lat, double Lon)> poly, double lat, double lon)
    {
        if (poly.Count < MinPoints) return false;
        var inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var (yi, xi) = poly[i];
            var (yj, xj) = poly[j];
            if ((yi > lat) != (yj > lat) && lon < (xj - xi) * (lat - yi) / (yj - yi) + xi) inside = !inside;
        }
        return inside;
    }
}

/// <summary>The colours a controller can mark their jets with (picked at login), apart from the
/// identity and package colours.</summary>
public static class OwnerColors
{
    public static readonly string[] Choices = ["#FF7A1A", "#FFD84A", "#A57CFF", "#FF7AA8", "#1FB89A", "#7EC8FF", "#7CFFB2", "#F0F0F0"];

    public static (byte R, byte G, byte B)? Parse(string? hex) =>
        hex is { Length: 7 } && hex[0] == '#' && int.TryParse(hex.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out var v)
            ? ((byte)(v >> 16), (byte)(v >> 8), (byte)v) : null;
}
