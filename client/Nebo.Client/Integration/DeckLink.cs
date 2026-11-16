using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using Nebo.Client.Core;
using Nebo.Client.Map;
using Nebo.Client.Panels;
using Nebo.Client.Srs;

namespace Nebo.Client.Integration;

/// <summary>The NEBO Stream Deck plugin's link to the client: a WebSocket on
/// <c>http://localhost:{DeckPort}/deck/</c> (this PC only). The client sends its state — the eight
/// marker engagement lines (range, alert, level), the ten SRS channels (frequency, role, selected,
/// transmitting now), the GCI maps' label level, label position and filters, the workspace and
/// players-only — when it changes (checked 4× a second, at least once a second), and takes
/// commands: fit, transmit, labels, labelPos, workspace, filter, playersOnly.</summary>
public static class DeckLink
{
    private static readonly ConcurrentDictionary<WebSocket, SemaphoreSlim> Clients = new();
    private static string _last = "";
    private static DateTime _lastSent;

    public static void Start()
    {
        if (Dev.DebugSnapshot.Active) return;  // a snapshot must not take the port from the real client
        var prefix = $"http://localhost:{AppState.Settings.DeckPort}/deck/";
        var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        try { listener.Start(); }
        catch (HttpListenerException) { return; }  // the port is taken (another client running): no deck link
        _ = Task.Run(() => AcceptLoop(listener));
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => Publish();
        timer.Start();
    }

    private static async Task AcceptLoop(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); }
            catch (Exception) { return; }
            if (!ctx.Request.IsWebSocketRequest || !IPAddress.IsLoopback(ctx.Request.RemoteEndPoint.Address))
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.Close();
                continue;
            }
            _ = Task.Run(() => Serve(ctx));
        }
    }

    private static async Task Serve(HttpListenerContext ctx)
    {
        WebSocket socket;
        try { socket = (await ctx.AcceptWebSocketAsync(null)).WebSocket; }
        catch (Exception) { return; }
        var gate = new SemaphoreSlim(1, 1);
        Clients[socket] = gate;
        _last = "";  // the newcomer gets the state at once
        var buffer = new byte[4096];
        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var text = new StringBuilder();
                WebSocketReceiveResult r;
                do
                {
                    r = await socket.ReceiveAsync(buffer, CancellationToken.None);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    text.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
                } while (!r.EndOfMessage);
                var line = text.ToString();
                _ = Application.Current.Dispatcher.BeginInvoke(() => Command(line));
            }
        }
        catch (Exception) { }
        finally
        {
            Clients.TryRemove(socket, out _);
            socket.Dispose();
        }
    }

    // ---- State -------------------------------------------------------------------------------

    private static IEnumerable<MapPanel> GciMaps() =>
        WorkspaceView.All.Where(v => v.Preset == MainWindow.GciPreset).SelectMany(v => v.Maps).Where(m => !m.IsAirfieldView);

    private static void Publish()
    {
        if (Clients.IsEmpty) return;
        string json;
        try { json = State().ToJsonString(); }
        catch (Exception) { return; }
        if (json == _last && DateTime.UtcNow - _lastSent < TimeSpan.FromSeconds(1)) return;
        _last = json;
        _lastSent = DateTime.UtcNow;
        var bytes = Encoding.UTF8.GetBytes(json);
        foreach (var (socket, gate) in Clients)
            _ = Task.Run(async () =>
            {
                if (!await gate.WaitAsync(0)) return;  // still sending the last one: skip this
                try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
                catch (Exception) { }
                finally { gate.Release(); }
            });
    }

    private static JsonObject State()
    {
        var store = AppState.Store;
        var rows = AppState.Engagements.Rows(store);
        var engagements = new JsonArray();
        for (int n = 1; n <= EngagementBook.Markers; n++)
        {
            var row = rows.FirstOrDefault(r => r.Line?.Marker == n);
            engagements.Add(row is null
                ? new JsonObject { ["n"] = n, ["active"] = false }
                : new JsonObject
                {
                    ["n"] = n, ["active"] = true, ["range"] = Math.Round(row.RangeNm, 1), ["alert"] = row.Line!.AlertNm,
                    ["level"] = EngagementBook.Level(row.RangeNm, row.Line.AlertNm), ["inside"] = row.Inside,
                    ["from"] = row.From, ["to"] = row.To,
                });
        }
        var channels = new JsonArray();
        for (int ch = 1; ch <= ChannelPlan.Count; ch++)
        {
            var package = ChannelPlan.PackageOf(ch);
            channels.Add(new JsonObject
            {
                ["ch"] = ch,
                ["mhz"] = SrsChannels.Mhz(ch),
                ["role"] = package > 0 ? $"P{package}" : ChannelPlan.IsAtc(ch) ? "ATC" : SrsChannels.RadioName(ch) ?? "",
                ["color"] = package > 0 ? Hex(PackageColors.Sk(package)) : ChannelPlan.IsAtc(ch) ? "#4CB864" : null,
                ["selected"] = SrsChannels.Transmitting == ch,
                ["sending"] = SrsChannels.SendingNow == ch,
            });
        }
        var map = GciMaps().FirstOrDefault();
        var filters = new JsonObject();
        foreach (var f in MapPanel.DeckFilters) filters[f] = map?.FilterLevel(f) ?? 0;  // 0 off, 1-3 = 30/60/100 %
        return new JsonObject
        {
            ["type"] = "state",
            ["connected"] = AppState.Connection.IsConnected,
            ["engagements"] = engagements,
            ["channels"] = channels,
            ["srsLive"] = SrsChannels.IsLive,
            ["labels"] = (map?.Labels ?? LabelLevel.Full).ToString(),
            ["labelPos"] = (map?.LabelPos ?? TagPlacement.Above).ToString(),
            ["workspace"] = (Application.Current.MainWindow as MainWindow)?.CurrentPreset ?? MainWindow.GciPreset,
            ["filters"] = filters,
            ["playersOnly"] = AppState.PlayersOnly,
        };
    }

    private static string Hex(SkiaSharp.SKColor c) => $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}";

    // ---- Commands ----------------------------------------------------------------------------

    private static void Command(string line)
    {
        JsonObject? cmd;
        try { cmd = JsonNode.Parse(line) as JsonObject; }
        catch (JsonException) { return; }
        if (cmd?["cmd"]?.GetValue<string>() is not { } name) return;
        switch (name)
        {
            case "fit" when cmd["n"]?.GetValue<int>() is { } n:
                if (AppState.Engagements.WithMarker(n) is { } line2) (AppState.ActiveMap ?? GciMaps().FirstOrDefault())?.FitEngagement(line2);
                break;
            case "transmit" when cmd["ch"]?.GetValue<int>() is { } ch and >= 1 and <= ChannelPlan.Count:
                SrsChannels.Transmit(ch);
                break;
            case "labels":
            {
                var next = GciMaps().FirstOrDefault()?.Labels switch
                {
                    LabelLevel.Full => LabelLevel.Short,
                    LabelLevel.Short => LabelLevel.None,
                    _ => LabelLevel.Full,
                };
                foreach (var m in GciMaps()) m.SetLabels(next);
                break;
            }
            case "labelPos":
            {
                var next = GciMaps().FirstOrDefault()?.LabelPos == TagPlacement.Above ? TagPlacement.Vector : TagPlacement.Above;
                foreach (var m in GciMaps()) m.SetLabelPos(next);
                break;
            }
            case "workspace":
                (Application.Current.MainWindow as MainWindow)?.SwitchGciAtc();
                break;
            case "filter" when cmd["name"]?.GetValue<string>() is { } filter && MapPanel.DeckFilters.Contains(filter):
            {
                // Every GCI map takes the first one's next stage, so they stay alike.
                var next = ((GciMaps().FirstOrDefault()?.FilterLevel(filter) ?? 0) + 1) % 4;
                foreach (var m in GciMaps()) m.SetFilterLevel(filter, next);
                break;
            }
            case "playersOnly":
                AppState.PlayersOnly = cmd["on"]?.GetValue<bool>() ?? !AppState.PlayersOnly;
                break;
        }
        _last = "";  // show the result at once
        Publish();
    }
}
