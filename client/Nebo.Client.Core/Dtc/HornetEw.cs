using System.Text.Json.Nodes;

namespace Nebo.Client.Core.Dtc;

/// <summary>The F/A-18C's electronic warfare sections, with the limits of DCS's DTC dialogs:
/// HARM (data.HARM: settings with the ids of HARM_SETTINGS_defs, and each radar's TOO class),
/// CMDS (data.ALR67.CMDS: programs MAN 1–6, AUTO 1–3, BYP with chaff quantity / repeat 0–99 and
/// interval 0.25–5 s in 0.25 s steps, flare and other quantities 0–99, the delay between
/// programs 0–10 s; each threat's AUTO program and threshold) and RWR (data.ALR67.RWR: per
/// table NORM / AI / AAA / UNK / FRND each emitter's priority, display, friend, jammer flags).
/// The "Avionics" tables the jet reads are rebuilt from these by DCS's own code in the pilot hook.</summary>
public sealed class HornetEw(Cartridge cartridge)
{
    public static readonly string[] Programs = ["MAN_1", "MAN_2", "MAN_3", "MAN_4", "MAN_5", "MAN_6", "AUTO_1", "AUTO_2", "AUTO_3", "BYP"];
    public static readonly string[] ThreatCategories = ["Air", "Ground", "Naval", "Other"];
    public static readonly string[] RwrTables = ["NORM", "AI", "AAA", "UNK", "FRND"];
    /// <summary>A threat's AUTO program: 0 none, 7–9 AUTO 1–3 (CMDS_defs autoPrograms).</summary>
    public static readonly int[] AutoPrograms = [0, 7, 8, 9];

    private static JsonObject Child(JsonObject parent, string key) =>
        parent[key] as JsonObject ?? (JsonObject)(parent[key] = new JsonObject());

    // ---- HARM -----------------------------------------------------------------------------------

    public JsonObject HarmSettings => Child(Child(cartridge.Data, "HARM"), "SETTINGS");

    /// <summary>Each radar's TOO class (HARM_TOO_Class ids), by name.</summary>
    public IReadOnlyList<(string Radar, int Class)> TooClasses =>
        Child(Child(cartridge.Data, "HARM"), "TOO_CLASS").Select(kv => (kv.Key, (int)(HornetNav.Number(kv.Value) ?? 0))).OrderBy(t => t.Key).ToList();

    public void SetTooClass(string radar, int cls) => Child(Child(cartridge.Data, "HARM"), "TOO_CLASS")[radar] = cls;

    // ---- CMDS -----------------------------------------------------------------------------------

    private JsonObject Cmds => Child(Child(cartridge.Data, "ALR67"), "CMDS");
    private JsonObject ProgramSettings => Child(Cmds, "CMDSProgramSettings");

    public JsonObject Program(string name) => Child(ProgramSettings, name);

    public void SetProgram(string name, int chaffQty, int chaffRepeat, double chaffInterval, int flareQty, int other1Qty, int other2Qty)
    {
        var p = Program(name);
        var chaff = Child(p, "Chaff");
        chaff["Quantity"] = Math.Clamp(chaffQty, 0, 99);
        chaff["Repeat"] = Math.Clamp(chaffRepeat, 0, 99);
        chaff["Interval"] = Interval(chaffInterval);
        Child(p, "Flare")["Quantity"] = Math.Clamp(flareQty, 0, 99);
        Child(p, "Other1")["Quantity"] = Math.Clamp(other1Qty, 0, 99);
        Child(p, "Other2")["Quantity"] = Math.Clamp(other2Qty, 0, 99);
    }

    /// <summary>As check_and_set_interval: 0.25–5 s, to the nearest 0.25 s.</summary>
    public static double Interval(double s) => Math.Floor(Math.Clamp(s, 0.25, 5.0) / 0.25 + 0.5) * 0.25;

    /// <summary>Seconds between programs (0–10).</summary>
    public int Delay
    {
        get => (int)(HornetNav.Number(ProgramSettings["delay_between_programs"]) ?? 0);
        set => ProgramSettings["delay_between_programs"] = Math.Clamp(value, 0, 10);
    }

    /// <summary>Every threat of the CMDS table: its category, group name and row (program, thresholds).</summary>
    public IReadOnlyList<(string Category, string Group, JsonObject Row)> CmdsThreats
    {
        get
        {
            var table = Child(Cmds, "CMDS_Threat_table");
            return ThreatCategories.SelectMany(cat => (table[cat] as JsonObject ?? []).Where(kv => kv.Value is JsonObject)
                .Select(kv => (cat, kv.Key, (JsonObject)kv.Value!))).OrderBy(t => t.cat).ThenBy(t => t.Key).ToList();
        }
    }

    /// <summary>A threat's AUTO program and threshold; the threshold must be one the row offers.</summary>
    public void SetCmdsThreat(string category, string group, int program, int thresholdIndex)
    {
        if (Child(Cmds, "CMDS_Threat_table")[category]?[group] is not JsonObject row) return;
        row["program"] = AutoPrograms.Contains(program) ? program : 0;
        if ((row["thresholds"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(t => HornetNav.Number(t["index"]) == thresholdIndex) is { } th)
            row["default_threshold"] = th.DeepClone();
    }

    // ---- RWR ------------------------------------------------------------------------------------

    public IReadOnlyList<(string Group, JsonObject Row)> Rwr(string table) =>
        (Child(Child(cartridge.Data, "ALR67"), "RWR")[table] as JsonObject ?? [])
            .Where(kv => kv.Value is JsonObject).Select(kv => (kv.Key, (JsonObject)kv.Value!))
            .OrderBy(t => HornetNav.Number(t.Item2["PRI"]) ?? 999).ToList();

    public void SetRwr(string table, string group, int priority, bool display, bool friend, bool jammer)
    {
        if (Child(Child(cartridge.Data, "ALR67"), "RWR")[table]?[group] is not JsonObject row) return;
        row["PRI"] = Math.Max(1, priority);
        row["display"] = display;
        row["friend"] = friend;
        row["aspj_xmit"] = jammer;
    }

    // ---- Friendly by side -----------------------------------------------------------------------

    /// <summary>HARM TOO classes in hostile / friendly pairs (HARM_TOO_Class: H1 7 ↔ F1 5, H2 8 ↔
    /// F2 6, HAA 10 ↔ FAA 9, HN 4 ↔ FN 3, HS 12 ↔ FS 11).</summary>
    private static readonly (int Hostile, int Friendly)[] TooPairs = [(7, 5), (8, 6), (10, 9), (4, 3), (12, 11)];

    private static readonly object UnitsLock = new();
    private static Dictionary<string, List<string>> _byWsType = [];
    private static Dictionary<string, IReadOnlySet<string>>? _threatUnits;

    /// <summary>Each threat group's DCS unit types (RWR rows and HARM radars are these groups), from
    /// the threat base of the Hornet's definitions: a group's threats name a unit type, or only a
    /// wsType, which the server's unit_types message (DCS's unit database, sent at login) turns
    /// into type names. Until it has come, groups known only by wsType have no types.</summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> ThreatUnits
    {
        get
        {
            lock (UnitsLock) return _threatUnits ??= BuildThreatUnits(_byWsType);
        }
    }

    /// <summary>DCS's unit types by wsType (four levels), from the server's unit_types message.</summary>
    public static void SetUnitTypes(IEnumerable<(string Type, int[] WsType)> types)
    {
        var by = new Dictionary<string, List<string>>();
        foreach (var (type, ws) in types)
            if (ws.Length == 4)
            {
                var key = string.Join(",", ws);
                if (!by.TryGetValue(key, out var list)) by[key] = list = [];
                list.Add(type);
            }
        lock (UnitsLock)
        {
            _byWsType = by;
            _threatUnits = null;
        }
    }

    private static Dictionary<string, IReadOnlySet<string>> BuildThreatUnits(Dictionary<string, List<string>> byWsType)
    {
        JsonArray groups;
        try { groups = DtcDefs.For(Cartridge.HornetType)["threats_base"] as JsonArray ?? []; }
        catch (NotSupportedException) { return []; }  // no tables (no DCS on this PC)
        var result = new Dictionary<string, IReadOnlySet<string>>();
        foreach (var g in groups.OfType<JsonObject>())
        {
            var types = new HashSet<string>();
            foreach (var t in (g["threats"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if ((string?)t["unit_type"] is { Length: > 0 } name) types.Add(name);
                else if (t["wstype"] is JsonArray { Count: 4 } ws
                         && byWsType.TryGetValue(string.Join(",", ws.Select(v => (int?)v ?? 0)), out var found))
                    types.UnionWith(found);
            }
            result[(string?)g["group_name"] ?? ""] = types;
        }
        return result;
    }

    /// <summary>Marks the side's own systems friendly and every other one hostile: an RWR row's
    /// friend flag (all tables) and a HARM radar's TOO class (moved within its hostile / friendly
    /// pair) follow whether any of the group's unit types is among <paramref name="ownTypes"/>.
    /// Returns how many RWR groups and HARM radars are now friendly.</summary>
    public (int RwrFriendly, int HarmFriendly) MarkFriendly(IReadOnlySet<string> ownTypes)
    {
        bool Own(string group) => ThreatUnits.TryGetValue(group, out var types) && types.Overlaps(ownTypes);
        var rwrFriends = new HashSet<string>();
        foreach (var table in RwrTables)
            foreach (var (group, row) in Rwr(table))
            {
                var own = Own(group);
                row["friend"] = own;
                if (own) rwrFriends.Add(group);
            }
        var harm = 0;
        foreach (var (radar, cls) in TooClasses)
        {
            var own = Own(radar);
            var pair = TooPairs.FirstOrDefault(p => p.Hostile == cls || p.Friendly == cls);
            if (pair == default) continue;  // unknown class: left as it is
            SetTooClass(radar, own ? pair.Friendly : pair.Hostile);
            if (own) harm++;
        }
        return (rwrFriends.Count, harm);
    }
}
