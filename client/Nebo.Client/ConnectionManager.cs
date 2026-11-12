using System.Collections.Concurrent;
using System.Windows.Threading;
using Nebo.Client.Core;

namespace Nebo.Client;

/// <summary>Owns the one server connection: feeds pictures into the shared store on the UI
/// thread, and sends controller commands, matching each answer to its command.</summary>
public sealed class ConnectionManager(PictureStore store, Dispatcher dispatcher)
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);

    private CancellationTokenSource? _session;
    private NeboConnection? _connection;
    private long _nextCommandId;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<CommandResult>> _waiting = new();

    public bool IsConnected => _session is not null;
    public string Status { get; private set; } = "Not connected";
    public event Action? StatusChanged;
    /// <summary>Raised on the UI thread once a login succeeded (the controller's info goes out then).</summary>
    public event Action? Connected;
    /// <summary>The callsign this login gave (announced to pilots by DLL 0.17).</summary>
    public string? Callsign { get; private set; }
    /// <summary>The colour this controller's jets are marked in ("#rrggbb").</summary>
    public string? Color { get; private set; }
    /// <summary>The saved server this connection was made from (keeps the area of operations).</summary>
    public ServerProfile? Profile { get; private set; }

    public async Task ConnectAsync(string host, int port, LoginOptions login, ServerProfile? profile = null)
    {
        Disconnect();
        Callsign = string.IsNullOrWhiteSpace(login.Callsign) ? null : login.Callsign.Trim();
        Color = OwnerColors.Parse(login.Color) is null ? null : login.Color;
        Profile = profile;
        var session = _session = new CancellationTokenSource();
        var seen = new HashSet<string>();
        var terrainCache = new TerrainCache(System.IO.Path.Combine(AppPaths.Cache, "terrain"));
        login = login with { TerrainHave = terrainCache.Have() };
        SetStatus($"Connecting to {host}:{port}…");
        try
        {
            var withProgress = login with { Progress = line => dispatcher.BeginInvoke(() => SetStatus($"{host}:{port}: {line}")) };
            await using var conn = await NeboConnection.ConnectAsync(host, port, withProgress, session.Token);
            _connection = conn;
            SetStatus($"Connected to {host}:{port} as {conn.Coalition}{(conn.User is { } u ? $" ({u})" : "")}");
            _ = dispatcher.BeginInvoke(() => Connected?.Invoke());
            string? closed = null;
            await foreach (var message in conn.ReadMessagesAsync(session.Token))
            {
                if (message is not CommandResult && seen.Add(KindOf(message))) AppLog.Write($"first {KindOf(message)}{Describe(message)}");
                switch (message)
                {
                    case ClosedMessage c:
                        closed = NeboConnection.RefusalText(c.Reason ?? "closed");
                        break;
                    case Picture picture:
                        await dispatcher.InvokeAsync(() =>
                        {
                            // Everything the UI thread does for one picture, panels reacting included.
                            using var perf = RenderStats.Measure("UI: picture");
                            store.Apply(conn.Coalition, picture, DateTime.UtcNow);
                        });
                        break;
                    case Theatre theatre:
                        await dispatcher.InvokeAsync(() => store.SetTheatre(theatre));
                        break;
                    case AirfieldsMessage airfields:
                        await dispatcher.InvokeAsync(() => store.SetAirfields(airfields.Airfields));
                        break;
                    case FootholdState foothold:
                        await dispatcher.InvokeAsync(() => store.SetFoothold(foothold));
                        break;
                    case WeatherMessage weather:
                        await dispatcher.InvokeAsync(() => store.SetWeather(weather));
                        break;
                    case UnitTypesMessage unitTypes:
                        Core.Dtc.HornetEw.SetUnitTypes(unitTypes.Types.Select(kv => (kv.Key, kv.Value)));
                        break;
                    case LsoMessage lso:
                        await dispatcher.InvokeAsync(() =>
                        {
                            using var perf = RenderStats.Measure("UI: lso frame");
                            store.ApplyLso(lso);
                        });
                        break;
                    case NctrMessage nctr:
                        await dispatcher.InvokeAsync(() => store.ApplyNctr(nctr));
                        break;
                    case Core.Dtc.PilotsMessage pilots:
                        await dispatcher.InvokeAsync(() => store.ApplyPilots(pilots));
                        break;
                    case DesignationsMessage designations:
                        await dispatcher.InvokeAsync(() => store.ApplyDesignations(designations, DateTime.UtcNow));
                        break;
                    case System.Text.Json.Nodes.JsonObject live when (string?)live["type"] == "dtc_live":
                        await dispatcher.InvokeAsync(() => store.ApplyDtcLive(live));
                        break;
                    case IdsMessage ids:
                        await dispatcher.InvokeAsync(() => store.ApplyIds(ids));
                        break;
                    case ThreatsMessage threats:
                        await dispatcher.InvokeAsync(() =>
                        {
                            AppState.Board.ToLatLon ??= (x, z) => AppState.Store.Theatre?.ToLatLon(x, z);
                            AppState.Board.ApplyShared(threats.Threats);
                        });
                        break;
                    case ControllersMessage controllers:
                        await dispatcher.InvokeAsync(() => AppState.Flights.ApplyControllers(controllers));
                        break;
                    case FlightsMessage flights:
                        await dispatcher.InvokeAsync(() => AppState.Flights.ApplyFlights(flights));
                        break;
                    case ChatMessage chat:
                        await dispatcher.InvokeAsync(() => ChatDesk.Apply(chat));
                        break;
                    case MarksMessage marks:
                        await dispatcher.InvokeAsync(() => AppState.Board.ApplyMarks(marks.Marks, DateTime.UtcNow));
                        break;
                    case CommandResult result:
                        if (_waiting.TryRemove(result.Id, out var waiter)) waiter.TrySetResult(result);
                        break;
                    case TerrainMessage terrain:
                        var grid = await Task.Run(() =>  // megabytes: off the UI thread
                        {
                            var g = terrain.Decode();
                            terrainCache.Save(terrain);
                            return g;
                        }, session.Token);
                        await dispatcher.InvokeAsync(() => store.SetTerrain(grid));
                        break;
                    case TerrainCachedMessage kept:
                        var keptGrid = await Task.Run(() => terrainCache.Load(kept.Hash)?.Decode(), session.Token);
                        if (keptGrid is null) AppLog.Write($"terrain {kept.Hash}: not in the cache any more (reconnect to download it)");
                        else await dispatcher.InvokeAsync(() => store.SetTerrain(keptGrid));
                        break;
                }
            }
            SetStatus(closed ?? "Server closed the connection");
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested)
        {
            SetStatus("Disconnected");
        }
        catch (AuthenticationException ex)
        {
            SetStatus(ex.Message);
        }
        catch (Exception ex)
        {
            AppLog.Error($"connection to {host}:{port}", ex);
            SetStatus($"Connection failed: {ex.Message}");
        }
        finally
        {
            if (_session == session)
            {
                _session = null;
                _connection = null;
            }
            FailWaiting("connection closed");
            session.Dispose();
            StatusChanged?.Invoke();
        }
    }

    /// <summary>Sends a command (e.g. "message_unit" with its fields) and waits for the
    /// server's answer; a failed result when not connected or no answer comes in time.</summary>
    public async Task<CommandResult> SendCommandAsync(string name, IReadOnlyDictionary<string, object> fields)
    {
        if (_connection is not { } conn) return new CommandResult(0, false, "not connected");
        var id = Interlocked.Increment(ref _nextCommandId);
        var message = new Dictionary<string, object> { ["type"] = "command", ["id"] = id, ["name"] = name };
        foreach (var (k, v) in fields) message[k] = v;
        var waiter = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[id] = waiter;
        try
        {
            await conn.SendAsync(message);
        }
        catch (Exception ex)
        {
            _waiting.TryRemove(id, out _);
            AppLog.Write($"command {name} not sent: {ex.Message}");
            return new CommandResult(id, false, "not sent: " + ex.Message);
        }
        var done = await Task.WhenAny(waiter.Task, Task.Delay(CommandTimeout));
        CommandResult result;
        if (done == waiter.Task) result = waiter.Task.Result;
        else
        {
            _waiting.TryRemove(id, out _);
            result = new CommandResult(id, false, "no answer from the server");
        }
        if (!result.Ok) AppLog.Write($"command {name} failed: {result.Error}");
        return result;
    }

    /// <summary>For the log: whether this controller's DCS install has the server's terrain,
    /// which the DCS Map and the Scope map's rivers and roads are drawn from.</summary>
    private static string LocalTerrain(string id)
    {
        try
        {
            if (Map.Charts.DcsInstall.Find() is not { } dcs) return "DCS install not found: no DCS Map, no rivers or roads";
            var dir = System.IO.Path.Combine(dcs, "Mods", "terrains", id);
            return System.IO.Directory.Exists(dir) ? $"terrain installed in {dcs}" : $"terrain NOT installed in {dcs}: no DCS Map, no rivers or roads";
        }
        catch (Exception e) { return "terrain check failed: " + e.Message; }
    }

    /// <summary>A message's kind for the log: the JSON "type" of a raw one, else its class.</summary>
    private static string KindOf(object message) =>
        message is System.Text.Json.Nodes.JsonObject j ? (string?)j["type"] ?? "json" : message.GetType().Name;

    private static string Describe(object message) => message switch
    {
        Theatre t => $" ({t.Id}; {LocalTerrain(t.Id)})",
        FootholdState f => $" ({f.Zones.Count} zones)",
        AirfieldsMessage a => $" ({a.Airfields.Count} airfields)",
        TerrainMessage g => g.Enc is null ? $" ({g.Id}, plain)" : $" ({g.Id}, compressed {g.Data?.Length / 1024} KB, {g.Hash})",
        TerrainCachedMessage k => $" ({k.Hash}, from cache)",
        _ => "",
    };

    private void FailWaiting(string reason)
    {
        foreach (var id in _waiting.Keys)
            if (_waiting.TryRemove(id, out var w)) w.TrySetResult(new CommandResult(id, false, reason));
    }

    public void Disconnect()
    {
        _session?.Cancel();
        _session = null;
        _connection = null;
        store.Clear();
        AppState.Board.ClearMarks();  // the next connection resends them
        AppState.Flights.Clear();
        ChatDesk.Clear();
    }

    private void SetStatus(string status)
    {
        AppLog.Write(status);
        Status = status;
        StatusChanged?.Invoke();
    }
}
