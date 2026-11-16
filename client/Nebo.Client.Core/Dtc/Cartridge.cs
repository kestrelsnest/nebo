using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nebo.Client.Core.Dtc;

/// <summary>A DCS data cartridge (.dtc): <c>{ "type", "name", "data": { sections… } }</c>, kept as
/// the JSON tree DCS wrote, so every key survives a load and save, also the ones the editor does
/// not know. Sections are edited through typed views on this tree (added per section); the
/// tables the avionics read (CMDS/RWR/HARM "Avionics" tables) are compiled by DCS's own code in
/// the pilot hook before the file is written, so they never go stale here.</summary>
public sealed class Cartridge
{
    public const string HornetType = "FA-18C_hornet";
    public const string ViperType = "F-16C_50";
    public const string TomcatType = "F-14BU";

    public JsonObject Root { get; }

    private Cartridge(JsonObject root) => Root = root;

    /// <summary>DCS unit type the cartridge is for ("FA-18C_hornet").</summary>
    public string Type => Root["type"]?.GetValue<string>() ?? "";

    public string Name
    {
        get => Root["name"]?.GetValue<string>() ?? "";
        set => Root["name"] = value;
    }

    public JsonObject Data => Root["data"] as JsonObject ?? throw new InvalidDataException("cartridge has no data");

    /// <summary>Theatre the map-bound sections (waypoints, routes, SA points) were made for.</summary>
    public string Terrain
    {
        get => Data["terrain"]?.GetValue<string>() ?? "";
        set
        {
            Data["terrain"] = value;
            if (Data["WYPT"] is JsonObject w) w["terrain"] = value;
            if (Data["MPD"] is JsonObject m) m["terrain"] = value;   // F-16C
        }
    }

    public static Cartridge Parse(string json)
    {
        if (JsonNode.Parse(json) is not JsonObject root || root["data"] is not JsonObject)
            throw new InvalidDataException("not a DCS data cartridge");
        return new Cartridge(root);
    }

    public static Cartridge Load(string path) => Parse(File.ReadAllText(path));

    /// <summary>The empty cartridge DCS's DTC manager starts from (tools/DtcDefs, DCS's own defaults).</summary>
    public static Cartridge NewDefault(string type, string terrain)
    {
        var c = Parse(Resource($"{type}.default.json") ?? throw new NotSupportedException($"no cartridge format for {type}"));
        c.Terrain = terrain;
        return c;
    }

    public static bool IsSupported(string type) => Resource($"{type}.default.json") is not null;

    public string ToJson(bool indented = false) => Root.ToJsonString(new JsonSerializerOptions { WriteIndented = indented });

    public void Save(string path) => File.WriteAllText(path, ToJson());

    public Cartridge Clone() => new((JsonObject)Root.DeepClone());

    /// <summary>A table made from the controller's own DCS install (DtcData).</summary>
    internal static string? Resource(string name) => DtcData.Read(name);
}

/// <summary>The definitions DCS's DTC manager works with for one aircraft: limits, enums, threat
/// ring types, CMDS programs, threat lists (tools/DtcDefs, from the module's _defs files).</summary>
public sealed class DtcDefs
{
    private static readonly Dictionary<string, DtcDefs> Cache = [];

    public JsonObject Root { get; }

    private DtcDefs(JsonObject root) => Root = root;

    public static DtcDefs For(string type)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(type, out var d)) return d;
            var json = Cartridge.Resource($"{type}.defs.json") ?? throw new NotSupportedException($"no definitions for {type}");
            return Cache[type] = new DtcDefs((JsonObject)JsonNode.Parse(json)!);
        }
    }

    /// <summary>A value by path through the _defs files, e.g. "NAV_SETTINGS_defs/NAV_SETTINGS_LIMITS/WAYPOINT_MAX".</summary>
    public JsonNode? this[string path]
    {
        get
        {
            JsonNode? n = Root;
            foreach (var part in path.Split('/'))
                n = n is JsonObject o ? o[part] : null;
            return n;
        }
    }

    public double Number(string path) => this[path]?.GetValue<double>() ?? throw new KeyNotFoundException(path);
}

/// <summary>The "pilots" message: the coalition's players who chose this side, slot or not
/// (dynamic slots: the aircraft appears in the picture only at spawn).</summary>
public sealed record PilotsMessage(
    [property: System.Text.Json.Serialization.JsonPropertyName("coalition")] string Coalition,
    [property: System.Text.Json.Serialization.JsonPropertyName("pilots")] IReadOnlyList<PilotInfo> Pilots);

/// <summary>A player on the side: DCS player id (push recipient), name, slot ("" before spawn
/// with dynamic slots) and its aircraft type ("" when not known yet).</summary>
public sealed record PilotInfo(
    [property: System.Text.Json.Serialization.JsonPropertyName("id")] long Id,
    [property: System.Text.Json.Serialization.JsonPropertyName("player")] string Player,
    [property: System.Text.Json.Serialization.JsonPropertyName("slot")] string Slot,
    [property: System.Text.Json.Serialization.JsonPropertyName("type")] string Type);
