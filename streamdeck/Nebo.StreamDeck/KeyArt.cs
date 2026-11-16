using System.Globalization;
using System.Text.Json.Nodes;

namespace Nebo.StreamDeck;

/// <summary>Key pictures (144×144 SVG) from the client's state. Each group has its own colour:
/// engagements red-brown, SRS channels blue, map controls grey, Players only the player-plate
/// blue. Phase (0–3, 4 Hz) drives the blink of an engagement inside its alert and the pulse of the
/// channel being transmitted on.</summary>
public static class KeyArt
{
    private const string Font = "Segoe UI, Arial, sans-serif";
    private const string EngBack = "#2A1414", SrsBack = "#0D1B2E", MapBack = "#1C2228", PlayerOff = "#10305A";
    private const string Far = "#1E7B3C", Near = "#C58A10", Inside = "#C62828", InsideDim = "#5E1414";
    private const string LitDim = "#173A5E", LitMid = "#23568C";
    private const string Lit = "#2E6FB5",PlayerOn = "#5AA0FF", Selected = "#FF7A2F", Sending = "#FF3D3D", SendingDim = "#8E1A1A";

    public static string Render(string kind, int n, string name, JsonObject? state, int phase)
    {
        if (state is null) return Offline(kind, n, name);
        return kind switch
        {
            "engagement" => Engagement(n, state, phase),
            "channel" => Channel(n, state, phase),
            "labels" => Value(MapBack, "LABELS", Str(state["labels"]) ?? "?", false),
            "labelpos" => Value(MapBack, "LABEL POS", Str(state["labelPos"]) ?? "?", false),
            "workspace" => Value(MapBack, "WORKSPACE", Str(state["workspace"]) ?? "?", false, big: true),
            "filter" => Filter(name, state),
            "playersonly" => PlayersOnly(state),
            _ => Value(MapBack, kind.ToUpperInvariant(), "", false),
        };
    }

    private static string Engagement(int n, JsonObject state, int phase)
    {
        var e = (state["engagements"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(x => Int(x["n"]) == n);
        if (e is null || e["active"]?.GetValue<bool>() != true)
            return Frame(EngBack, Text(72, 92, n.ToString(CultureInfo.InvariantCulture), 64, "#6E4A4A", bold: true, anchor: "middle"));
        var level = Str(e["level"]) ?? "far";
        var back = level switch { "inside" => phase % 2 == 0 ? Inside : InsideDim, "near" => Near, _ => Far };
        var range = e["range"]?.GetValue<double>() ?? 0;
        var alert = e["alert"] is JsonValue a && a.TryGetValue<double>(out var al) ? $"⚑ {al:0.#}" : "";
        var to = Short(Str(e["to"]) ?? "", 12);
        return Frame(back,
            Text(10, 38, n.ToString(CultureInfo.InvariantCulture), 34, "#FFFFFF", bold: true),
            Text(134, 34, alert, 18, "#FFFFFF", anchor: "end"),
            Text(72, 90, range.ToString(range < 100 ? "0.0" : "0", CultureInfo.InvariantCulture), 44, "#FFFFFF", bold: true, anchor: "middle"),
            Text(72, 112, "NM", 16, "#FFFFFFCC", anchor: "middle"),
            Text(72, 134, to, 16, "#FFFFFF", anchor: "middle"));
    }

    private static string Channel(int ch, JsonObject state, int phase)
    {
        var c = (state["channels"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(x => Int(x["ch"]) == ch);
        var mhz = c?["mhz"] is JsonValue m && m.TryGetValue<double>(out var f) ? f.ToString("0.000", CultureInfo.InvariantCulture) : "—";
        var sending = c?["sending"]?.GetValue<bool>() == true;
        var selected = c?["selected"]?.GetValue<bool>() == true;
        var role = Str(c?["role"]) ?? "";
        var color = Str(c?["color"]);
        var back = sending ? (phase % 2 == 0 ? Sending : SendingDim) : SrsBack;
        var parts = new List<string>
        {
            Text(10, 34, $"CH{ch}", 26, "#FFFFFF", bold: true),
            Text(72, 88, mhz, mhz.Length > 7 ? 26 : 30, "#FFFFFF", bold: true, anchor: "middle"),
        };
        if (role.Length > 0)
        {
            var label = Esc(Short(role, 9));
            parts.Add(color is not null
                ? $"<rect x=\"14\" y=\"108\" width=\"116\" height=\"26\" rx=\"5\" fill=\"{color}\"/><text x=\"72\" y=\"128\" font-family=\"{Font}\" font-size=\"18\" font-weight=\"bold\" fill=\"#061018\" text-anchor=\"middle\">{label}</text>"
                : Text(72, 128, Short(role, 12), 17, "#9FB4C8", anchor: "middle"));
        }
        if (sending) parts.Add(Text(134, 34, "TX", 22, "#FFFFFF", bold: true, anchor: "end"));
        var svg = Frame(back, [.. parts]);
        return selected ? svg.Replace("</svg>", $"<rect x=\"4\" y=\"4\" width=\"136\" height=\"136\" rx=\"14\" fill=\"none\" stroke=\"{Selected}\" stroke-width=\"8\"/></svg>") : svg;
    }

    private static readonly Dictionary<string, string> FilterNames = new()
    {
        ["ground"] = "GROUND", ["landed"] = "LANDED", ["rings"] = "THREAT RINGS", ["marks"] = "F10 MARKS",
        ["tgp"] = "TGP", ["zones"] = "ZONES", ["airfields"] = "AIRFIELDS",
    };

    private static string Filter(string name, JsonObject state)
    {
        // Level 0 off, 1-3 = 30/60/100 % (an older client sends true/false).
        var level = state["filters"]?[name] is JsonValue v ? v.TryGetValue<int>(out var l) ? l : v.TryGetValue<bool>(out var b) && b ? 3 : 0 : 0;
        string[] backs = [MapBack, LitDim, LitMid, Lit];
        string[] values = ["off", "30%", "60%", "100%"];
        level = Math.Clamp(level, 0, 3);
        return Value(backs[level], FilterNames.GetValueOrDefault(name, name.ToUpperInvariant()), values[level], level > 0);
    }

    private static string PlayersOnly(JsonObject state)
    {
        var on = state["playersOnly"]?.GetValue<bool>() == true;
        return Frame(on ? PlayerOn : PlayerOff,
            Text(72, 62, "PLAYERS", 26, on ? "#061018" : "#FFFFFF", bold: true, anchor: "middle"),
            Text(72, 92, "ONLY", 26, on ? "#061018" : "#FFFFFF", bold: true, anchor: "middle"),
            Text(72, 124, "hold", 16, on ? "#061018" : "#9FB4C8", anchor: "middle"));
    }

    private static string Value(string back, string title, string value, bool lit, bool big = false) =>
        Frame(back,
            Text(72, 40, title, title.Length > 9 ? 17 : 20, lit ? "#FFFFFF" : "#9FB4C8", bold: true, anchor: "middle"),
            Text(72, big ? 104 : 96, value, big ? 44 : 32, "#FFFFFF", bold: true, anchor: "middle"));

    private static string Offline(string kind, int n, string name)
    {
        var back = kind switch { "engagement" => EngBack, "channel" => SrsBack, "playersonly" => PlayerOff, _ => MapBack };
        var title = kind switch
        {
            "engagement" => n.ToString(CultureInfo.InvariantCulture),
            "channel" => $"CH{n}",
            "filter" => FilterNames.GetValueOrDefault(name, name.ToUpperInvariant()),
            "labelpos" => "LABEL POS",
            "playersonly" => "PLAYERS",
            _ => kind.ToUpperInvariant(),
        };
        return Frame(back,
            Text(72, 62, title, title.Length > 9 ? 17 : 26, "#7A8794", bold: true, anchor: "middle"),
            Text(72, 102, "NEBO offline", 16, "#7A8794", anchor: "middle"));
    }

    private static string Frame(string back, params string[] content) =>
        $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"144\" height=\"144\" viewBox=\"0 0 144 144\"><rect width=\"144\" height=\"144\" rx=\"16\" fill=\"{back}\"/>{string.Concat(content)}</svg>";

    private static string Text(double x, double y, string text, double size, string fill, bool bold = false, string anchor = "start") =>
        text.Length == 0 ? "" :
        $"<text x=\"{x.ToString(CultureInfo.InvariantCulture)}\" y=\"{y.ToString(CultureInfo.InvariantCulture)}\" font-family=\"{Font}\" font-size=\"{size.ToString(CultureInfo.InvariantCulture)}\"{(bold ? " font-weight=\"bold\"" : "")} fill=\"{fill}\" text-anchor=\"{anchor}\">{Esc(text)}</text>";

    private static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string Short(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : -1;
}
