using System.Text.Json.Nodes;

namespace Nebo.Client.Core.Dtc;

/// <summary>COMM1 (UHF) / COMM2 (VHF) of an F-16C cartridge (data.COMM.COMM1|2: Channel_1–20,
/// freq MHz, modulation 1 AM / 2 FM), with the rules of DCS's COMM1.lua / COMM2.lua: COMM1
/// 225–399.975 AM; COMM2 FM 30–87.975 and AM 116–155.975, a frequency in the gap going to the
/// nearer edge, the modulation following the band. Editing a radio clears its mirror_COMM flag
/// (with it set, DCS takes the mission's presets).</summary>
public sealed class ViperComm(Cartridge cartridge)
{
    public const int Channels = 20;
    public const int Am = 1, Fm = 2;

    private JsonObject Comm => cartridge.Data["COMM"] as JsonObject ?? (JsonObject)(cartridge.Data["COMM"] = new JsonObject());

    private JsonObject Radio(int radio) => Comm[$"COMM{radio}"] as JsonObject ?? (JsonObject)(Comm[$"COMM{radio}"] = new JsonObject());

    public bool Mirror(int radio) => Comm[$"mirror_COMM{radio}"]?.GetValue<bool>() == true;
    public void SetMirror(int radio, bool on) => Comm[$"mirror_COMM{radio}"] = on;

    public JsonObject? Channel(int radio, int channel) => Radio(radio)[$"Channel_{channel}"] as JsonObject;

    /// <summary>Sets a channel; the frequency snapped into the radio's bands, the modulation from the band.</summary>
    public void Set(int radio, int channel, double mhz)
    {
        if (channel is < 1 or > Channels) return;
        var (f, m) = radio == 1 ? (Math.Round(Math.Clamp(mhz, 225.0, 399.975), 3), Am) : Vhf(mhz);
        Radio(radio)[$"Channel_{channel}"] = new JsonObject { ["freq"] = f, ["modulation"] = m };
        SetMirror(radio, false);
    }

    /// <summary>COMM2: onFocus_COMM2_sbChannelFreq.</summary>
    public static (double Mhz, int Modulation) Vhf(double mhz)
    {
        const double minFm = 30.0, maxFm = 87.975, minAm = 116.0, maxAm = 155.975;
        mhz = Math.Round(mhz, 3);
        if (mhz < minFm) return (minFm, Fm);
        if (mhz <= maxFm) return (mhz, Fm);
        if (mhz < minAm) return mhz < (minAm - maxFm) / 2 + maxFm ? (maxFm, Fm) : (minAm, Am);
        return (Math.Min(mhz, maxAm), Am);
    }
}

/// <summary>The F-16C's countermeasures, RWR and ROE (data.MPD.CMDS, data.ELINT.RWR,
/// data.MPD.ROE) with the limits of DCS's dialogs (MPD\CMDS.lua, ELINT\RWR\RWR.lua, MPD\ROE.lua):
/// programs MAN1–6, AUTO1–3, BYP of burst / salvo quantity 0–99 and interval 0–10 s (bursts to
/// the ms, salvos to 10 ms); bingo quantities 0–99; delay between programs 0–10 s; each threat's
/// AUTO program (1 none, 2–4 AUTO 1–3) and threshold; RWR tables HIGH / LOW with each emitter's
/// priority (a ranking 1…n), display, search and unknown flags; ROE sovereignty per aircraft
/// group (1 friendly, 2 hostile, 3 unknown). <see cref="Compile"/> rebuilds the tables the
/// avionics read as the dialog does on every change (make_RWR_table, makeCMDSTable).</summary>
public sealed class ViperEw(Cartridge cartridge)
{
    public static readonly string[] Programs = ["MAN1", "MAN2", "MAN3", "MAN4", "MAN5", "MAN6", "AUTO1", "AUTO2", "AUTO3", "BYP"];
    public static readonly string[] Expendables = ["Chaff", "Flare", "Other1", "Other2"];
    public static readonly string[] ThreatCategories = ["Air", "Ground", "Naval", "Other"];
    public static readonly string[] RwrTables = ["HIGH", "LOW"];
    /// <summary>A threat's AUTO program: 1 none, 2–4 AUTO 1–3 (CMDS_defs autoPrograms).</summary>
    public static readonly int[] AutoPrograms = [1, 2, 3, 4];
    public const int Friendly = 1, Hostile = 2, Unknown = 3;

    private static JsonObject Child(JsonObject parent, string key) =>
        parent[key] as JsonObject ?? (JsonObject)(parent[key] = new JsonObject());

    private JsonObject Mpd => Child(cartridge.Data, "MPD");
    private JsonObject Cmds => Child(Mpd, "CMDS");
    private JsonObject CmdsPrograms => Child(Cmds, "CMDSPrograms");
    private JsonObject RwrSection => Child(Child(cartridge.Data, "ELINT"), "RWR");
    private JsonObject Roe => Child(Mpd, "ROE");

    private static JsonArray ThreatsBase => (JsonArray)DtcDefs.For(Cartridge.ViperType)["threats_base"]!;

    // ---- CMDS -----------------------------------------------------------------------------------

    public JsonObject Program(string name) => Child(Child(Cmds, "CMDSProgramSettings"), name);

    /// <summary>One expendable of a program: burst quantity / interval, salvo quantity / interval.</summary>
    public void SetProgram(string program, string expendable, int burstQty, double burstInterval, int salvoQty, double salvoInterval)
    {
        var e = Child(Program(program), expendable);
        e["BurstQuantity"] = Math.Clamp(burstQty, 0, 99);
        e["BurstInterval"] = Math.Round(Math.Clamp(burstInterval, 0, 10), 3);
        e["SalvoQuantity"] = Math.Clamp(salvoQty, 0, 99);
        e["SalvoInterval"] = Math.Round(Math.Clamp(salvoInterval, 0, 10), 2);
    }

    public JsonObject Bingo => Child(Cmds, "CMDSBingoSettings");

    public void SetBingo(int chaff, int flares, int other1, int other2, bool fdbk, bool reqctr, bool bingo)
    {
        var b = Bingo;
        b["ChaffNum"] = Math.Clamp(chaff, 0, 99);
        b["FlaresNum"] = Math.Clamp(flares, 0, 99);
        b["Other1Num"] = Math.Clamp(other1, 0, 99);
        b["Other2Num"] = Math.Clamp(other2, 0, 99);
        b["FDBK"] = fdbk;
        b["REQCTR"] = reqctr;
        b["BINGO"] = bingo;
    }

    /// <summary>Seconds between programs (0–10).</summary>
    public int Delay
    {
        get => (int)(HornetNav.Number(CmdsPrograms["delayBetweenPrograms"]) ?? 0);
        set => CmdsPrograms["delayBetweenPrograms"] = Math.Clamp(value, 0, 10);
    }

    /// <summary>Every threat of the CMDS table: its category, group name and row (program, thresholds).</summary>
    public IReadOnlyList<(string Category, string Group, JsonObject Row)> CmdsThreats =>
        ThreatCategories.SelectMany(cat => (CmdsPrograms[cat] as JsonObject ?? []).Where(kv => kv.Value is JsonObject)
            .Select(kv => (cat, kv.Key, (JsonObject)kv.Value!))).OrderBy(t => t.cat).ThenBy(t => t.Key).ToList();

    /// <summary>A threat's AUTO program and threshold; the threshold must be one the row offers.</summary>
    public void SetCmdsThreat(string category, string group, int program, int thresholdIndex)
    {
        if (CmdsPrograms[category]?[group] is not JsonObject row) return;
        row["program"] = AutoPrograms.Contains(program) ? program : AutoPrograms[0];
        if ((row["thresholds"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(t => HornetNav.Number(t["index"]) == thresholdIndex) is { } th)
            row["default_threshold"] = th.DeepClone();
    }

    // ---- RWR ------------------------------------------------------------------------------------

    /// <summary>A table's emitters by priority.</summary>
    public IReadOnlyList<(string Group, JsonObject Row)> Rwr(string table) =>
        (RwrSection[table] as JsonObject ?? []).Where(kv => kv.Value is JsonObject).Select(kv => (kv.Key, (JsonObject)kv.Value!))
            .OrderBy(t => HornetNav.Number(t.Item2["PRI"]) ?? 999).ToList();

    public void SetRwr(string table, string group, bool display, bool search, bool unknown)
    {
        if (RwrSection[table]?[group] is not JsonObject row) return;
        row["display"] = display;
        row["search"] = search;
        row["unknown"] = unknown;
    }

    /// <summary>Moves an emitter to a place in the ranking (1 = first); the rest close up, as the
    /// dialog's up / down buttons keep the priorities 1…n.</summary>
    public void MoveRwr(string table, string group, int priority)
    {
        var rows = Rwr(table).ToList();
        var i = rows.FindIndex(r => r.Group == group);
        if (i < 0) return;
        var row = rows[i];
        rows.RemoveAt(i);
        rows.Insert(Math.Clamp(priority - 1, 0, rows.Count), row);
        for (var k = 0; k < rows.Count; k++) rows[k].Row["PRI"] = k + 1;
    }

    // ---- ROE ------------------------------------------------------------------------------------

    public IReadOnlyList<JsonObject> RoeList => (Roe["List"] as JsonArray ?? []).OfType<JsonObject>().ToList();

    public JsonObject RoeSettings => Child(Roe, "Settings");

    public void SetSovereignty(string group, int sovereignty)
    {
        if (RoeList.FirstOrDefault(r => (string?)r["group_name"] == group) is { } r)
            r["sovereignty"] = Math.Clamp(sovereignty, Friendly, Unknown);
    }

    /// <summary>ROE after the picture: an aircraft group with a type of the own side FRIENDLY, one
    /// with a type seen on the other side HOSTILE, the rest UNKNOWN. Returns how many are friendly
    /// and hostile.</summary>
    public (int Friendly, int Hostile) MarkFriendly(IReadOnlySet<string> ownTypes, IReadOnlySet<string> otherTypes)
    {
        int friends = 0, hostiles = 0;
        foreach (var r in RoeList)
        {
            var types = GroupTypes(r);
            var s = types.Overlaps(ownTypes) ? Friendly : types.Overlaps(otherTypes) ? Hostile : Unknown;
            r["sovereignty"] = s;
            if (s == Friendly) friends++;
            if (s == Hostile) hostiles++;
        }
        return (friends, hostiles);
    }

    /// <summary>A ROE group's DCS unit types: its own threats' types and the threat base's (wsTypes resolved).</summary>
    private static HashSet<string> GroupTypes(JsonObject roe)
    {
        var types = (roe["threats"] as JsonArray ?? []).OfType<JsonObject>().Select(t => (string?)t["unit_type"] ?? "")
            .Where(t => t.Length > 0).ToHashSet();
        if (HornetEw.ThreatUnits.TryGetValue((string?)roe["group_name"] ?? "", out var known)) types.UnionWith(known);
        return types;
    }

    // ---- What the avionics read --------------------------------------------------------------------

    /// <summary>RWR_Avionics_Threat_Table and CMDS_Avionics_Threat_Table, rebuilt from the settings
    /// in the threat base's order as make_RWR_table / makeCMDSTable do.</summary>
    public void Compile()
    {
        var threats = ThreatsBase.OfType<JsonObject>().ToList();

        JsonArray RwrTable(string table)
        {
            var settings = RwrSection[table] as JsonObject ?? [];
            var rows = threats.Select(t => (t, s: settings[(string?)t["group_name"] ?? ""] as JsonObject)).Where(x => x.s is not null)
                .Select(x => new JsonObject
                {
                    ["group_name"] = x.t["group_name"]?.DeepClone(), ["hint"] = x.t["hint"]?.DeepClone(), ["threats"] = x.t["threats"]?.DeepClone(),
                    ["PRI"] = x.s!["PRI"]?.DeepClone(), ["display"] = x.s["display"]?.DeepClone(),
                    ["search"] = x.s["search"]?.DeepClone(), ["unknown"] = x.s["unknown"]?.DeepClone(),
                })
                .OrderBy(r => HornetNav.Number(r["PRI"]) ?? 0);
            return [.. rows];
        }
        RwrSection["RWR_Avionics_Threat_Table"] = new JsonObject { ["HIGH"] = RwrTable("HIGH"), ["LOW"] = RwrTable("LOW") };

        var search = new JsonObject { ["label"] = "SEARCH", ["index"] = 1 };
        var cmds = new JsonArray();
        foreach (var t in threats)
        {
            var name = (string?)t["group_name"] ?? "";
            var s = ThreatCategories.Select(c => CmdsPrograms[c]?[name] as JsonObject).FirstOrDefault(x => x is not null);
            cmds.Add(new JsonObject
            {
                ["group_name"] = name,
                ["program"] = s?["program"]?.DeepClone() ?? AutoPrograms[0],
                ["thresholds"] = s?["thresholds"]?.DeepClone() ?? new JsonArray(search.DeepClone()),
                ["default_threshold"] = s?["default_threshold"]?.DeepClone() ?? search.DeepClone(),
                ["hint"] = t["hint"]?.DeepClone(),
                ["threats"] = t["threats"]?.DeepClone(),
            });
        }
        CmdsPrograms["CMDS_Avionics_Threat_Table"] = cmds;
    }
}
