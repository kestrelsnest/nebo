using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Nebo.StreamDeck;

/// <summary>The NEBO Stream Deck plugin. Stream Deck starts it with
/// <c>-port N -pluginUUID U -registerEvent E -info J</c>; it registers on Stream Deck's WebSocket,
/// connects to the NEBO client's deck link (ws://localhost:47810/deck/, retried every 2 s),
/// draws every key from the client's state (SVG through setImage, only when a key's picture
/// changes) and sends the keys' commands back. Built-in libraries only.</summary>
public static class Program
{
    public const string Prefix = "com.kestrelsnest.nebo.";

    private sealed record KeyContext(string Action, JsonObject Settings);

    private static readonly ConcurrentDictionary<string, KeyContext> Keys = new();
    private static readonly ConcurrentDictionary<string, string> Shown = new();
    private static ClientWebSocket? _deck;
    private static ClientWebSocket? _app;
    private static readonly SemaphoreSlim DeckSend = new(1, 1), AppSend = new(1, 1);
    private static JsonObject? _state;
    private static int _phase;

    // A short debug log next to the exe (bin/plugin.log in the plugin folder), restarted each launch.
    private static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "plugin.log");
    private static readonly object LogGate = new();
    private static int _logged;

    private static void Log(string line)
    {
        if (Interlocked.Increment(ref _logged) > 400) return;  // enough to see what happens
        lock (LogGate) File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
    }

    public static async Task Main(string[] args)
    {
        string? Arg(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        try { File.Delete(LogPath); } catch (Exception) { }
        Log($"start: {string.Join(' ', args.Take(6))}");
        if (Arg("-port") is not { } port || Arg("-pluginUUID") is not { } uuid || Arg("-registerEvent") is not { } register) return;
        var port2 = int.TryParse(Environment.GetEnvironmentVariable("NEBO_DECK_PORT"), out var p) ? p : 47810;

        _deck = new ClientWebSocket();
        await _deck.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), CancellationToken.None);
        await Send(_deck, DeckSend, new JsonObject { ["event"] = register, ["uuid"] = uuid });

        _ = Task.Run(() => AppLoop(port2));
        _ = Task.Run(AnimationLoop);
        await DeckLoop();  // ends when Stream Deck closes the plugin
    }

    // ---- Stream Deck -------------------------------------------------------------------------

    private static async Task DeckLoop()
    {
        var buffer = new byte[65536];
        while (_deck!.State == WebSocketState.Open)
        {
            var text = new StringBuilder();
            WebSocketReceiveResult r;
            try
            {
                do
                {
                    r = await _deck.ReceiveAsync(buffer, CancellationToken.None);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    text.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
                } while (!r.EndOfMessage);
            }
            catch (WebSocketException) { return; }
            if (JsonNode.Parse(text.ToString()) is not JsonObject e) continue;
            var ev = e["event"]?.GetValue<string>();
            var context = e["context"]?.GetValue<string>();
            var action = e["action"]?.GetValue<string>() ?? "";
            var settings = e["payload"]?["settings"] as JsonObject ?? new JsonObject();
            if (ev is not ("keyDown" or "keyUp")) Log($"event {ev} {action} {context} {settings.ToJsonString()}");
            else Log($"event {ev} {action}");
            switch (ev)
            {
                case "willAppear":
                case "didReceiveSettings":
                    if (context is null) break;
                    Keys[context] = new KeyContext(action, (JsonObject)settings.DeepClone());
                    Shown.TryRemove(context, out _);
                    await Draw(context);
                    break;
                case "willDisappear":
                    if (context is null) break;
                    Keys.TryRemove(context, out _);
                    Shown.TryRemove(context, out _);
                    break;
                case "keyDown" when context is not null && Keys.TryGetValue(context, out var k):
                    await Press(k, down: true);
                    break;
                case "keyUp" when context is not null && Keys.TryGetValue(context, out var k2):
                    await Press(k2, down: false);
                    break;
            }
        }
    }

    private static int Setting(KeyContext k, string name, int fallback) =>
        k.Settings[name] is JsonValue v && (v.TryGetValue<int>(out var i) || (v.TryGetValue<string>(out var s) && int.TryParse(s, out i))) ? i : fallback;

    private static string SettingText(KeyContext k, string name, string fallback) =>
        k.Settings[name] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : fallback;

    private static async Task Press(KeyContext k, bool down)
    {
        var kind = k.Action.StartsWith(Prefix, StringComparison.Ordinal) ? k.Action[Prefix.Length..] : k.Action;
        JsonObject? cmd = (kind, down) switch
        {
            ("engagement", true) => new JsonObject { ["cmd"] = "fit", ["n"] = Setting(k, "n", 1) },
            ("channel", true) => new JsonObject { ["cmd"] = "transmit", ["ch"] = Setting(k, "ch", 1) },
            ("labels", true) => new JsonObject { ["cmd"] = "labels" },
            ("labelpos", true) => new JsonObject { ["cmd"] = "labelPos" },
            ("workspace", true) => new JsonObject { ["cmd"] = "workspace" },
            ("filter", true) => new JsonObject { ["cmd"] = "filter", ["name"] = SettingText(k, "name", "ground") },
            ("playersonly", _) => new JsonObject { ["cmd"] = "playersOnly", ["on"] = down },  // held: on while pressed
            _ => null,
        };
        if (cmd is not null && _app is { State: WebSocketState.Open } app) await Send(app, AppSend, cmd);
    }

    private static async Task Draw(string context)
    {
        if (!Keys.TryGetValue(context, out var k)) return;
        var kind = k.Action.StartsWith(Prefix, StringComparison.Ordinal) ? k.Action[Prefix.Length..] : k.Action;
        var svg = KeyArt.Render(kind, n: Setting(k, kind == "channel" ? "ch" : "n", 1), name: SettingText(k, "name", "ground"), _state, _phase);
        if (Shown.TryGetValue(context, out var old) && old == svg) return;
        Shown[context] = svg;
        await Send(_deck!, DeckSend, new JsonObject
        {
            ["event"] = "setImage",
            ["context"] = context,
            ["payload"] = new JsonObject { ["image"] = "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg)), ["target"] = 0 },
        });
        Log($"setImage {kind} {context} ({svg.Length} chars)");
    }

    private static async Task DrawAll()
    {
        foreach (var context in Keys.Keys) await Draw(context);
    }

    /// <summary>Blinking and the transmit pulse: a new phase four times a second.</summary>
    private static async Task AnimationLoop()
    {
        while (true)
        {
            await Task.Delay(250);
            _phase = (_phase + 1) % 4;
            try { await DrawAll(); } catch (Exception) { }
        }
    }

    // ---- The NEBO client --------------------------------------------------------------

    private static async Task AppLoop(int port)
    {
        var buffer = new byte[65536];
        while (true)
        {
            var app = new ClientWebSocket();
            try
            {
                await app.ConnectAsync(new Uri($"ws://localhost:{port}/deck/"), CancellationToken.None);
                _app = app;
                Log("client link connected");
                while (app.State == WebSocketState.Open)
                {
                    var text = new StringBuilder();
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await app.ReceiveAsync(buffer, CancellationToken.None);
                        if (r.MessageType == WebSocketMessageType.Close) break;
                        text.Append(Encoding.UTF8.GetString(buffer, 0, r.Count));
                    } while (!r.EndOfMessage);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    if (JsonNode.Parse(text.ToString()) is JsonObject s && s["type"]?.GetValue<string>() == "state")
                    {
                        _state = s;
                        await DrawAll();
                    }
                }
            }
            catch (Exception ex) { Log($"client link: {ex.GetType().Name} {ex.Message}"); }
            _app = null;
            _state = null;  // keys show "NEBO offline"
            try { await DrawAll(); } catch (Exception) { }
            app.Dispose();
            await Task.Delay(2000);
        }
    }

    private static async Task Send(ClientWebSocket socket, SemaphoreSlim gate, JsonObject message)
    {
        var bytes = Encoding.UTF8.GetBytes(message.ToJsonString());
        await gate.WaitAsync();
        try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
        catch (Exception) { }
        finally { gate.Release(); }
    }
}
