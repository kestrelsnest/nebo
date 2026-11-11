using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Nebo.Client.Core;

public sealed class AuthenticationException(string message) : Exception(message);

/// <summary>A controller's connection to a NEBO server.</summary>
public sealed class NeboConnection : IAsyncDisposable
{
    public const int DefaultPort = 10309;
    private const string ClientName = "NEBO Client 0.1";

    private readonly TcpClient _tcp;
    private readonly StreamReader _reader;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public string Coalition { get; }
    /// <summary>The Discord name the server verified (login 2 with Discord), else null.</summary>
    public string? User { get; private init; }

    private NeboConnection(TcpClient tcp, StreamReader reader, string coalition)
    {
        _tcp = tcp;
        _reader = reader;
        Coalition = coalition;
    }

    /// <summary>Sends one JSON object as a line (commands). Safe from several callers at once.</summary>
    public async Task SendAsync(object message, CancellationToken ct = default)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message) + "\n");
        await _sendLock.WaitAsync(ct);
        try
        {
            await _tcp.GetStream().WriteAsync(bytes, ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Connects with a fixed password, taking the first side offered (tools, tests).</summary>
    public static Task<NeboConnection> ConnectAsync(string host, int port, string password, CancellationToken ct = default) =>
        ConnectAsync(host, port, new LoginOptions(() => password, (sides, _) => Task.FromResult<string?>(sides.FirstOrDefault())), ct);

    /// <summary>Login 2 (server 0.10+): hello → Discord (when the server asks: the Discord app's
    /// Authorize popup) → proof of the password (HMAC of the server's challenge; the password is
    /// read only now and never sent) → side choice. An older server answers the hello with a
    /// bare auth_fail: then the old login, where the password decides the side.</summary>
    public static async Task<NeboConnection> ConnectAsync(string host, int port, LoginOptions login, CancellationToken ct = default)
    {
        var tcp = await OpenAsync(host, port, ct);
        try
        {
            var stream = tcp.GetStream();
            await stream.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "hello", client = ClientName, version = 2 }) + "\n"), ct);
            var reader = new StreamReader(stream, new UTF8Encoding(false));
            using var hello = await ReadReplyAsync(reader, ct);
            var h = hello.RootElement;
            if (Str(h, "type") == "auth_fail" && Str(h, "reason") is null)
            {
                tcp.Dispose();
                return await ConnectOldAsync(host, port, login.Password() ?? "", ct);  // a 0.9 server
            }
            if (Str(h, "type") != "hello") throw Refused(h);
            var nonce = Str(h, "nonce") ?? "";

            if (Str(h, "discord") is { Length: > 0 } discordApp)
            {
                login.Progress?.Invoke("Discord: click Authorize in your Discord app…");
                var code = await DiscordRpc.AuthorizeAsync(discordApp, ct);
                await SendLineAsync(stream, new { type = "discord", code }, ct);
                login.Progress?.Invoke("Checking your Discord roles…");
                using var stage = await ReadReplyAsync(reader, ct);
                if (Str(stage.RootElement, "type") != "auth_stage") throw Refused(stage.RootElement);
            }

            // Decrypted only now, after the Discord check passed; only the proof goes out.
            var password = login.Password() ?? "";
            var proof = Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(nonce)));
            await SendLineAsync(stream, new { type = "proof", proof }, ct);
            using var ok = await ReadReplyAsync(reader, ct);
            var o = ok.RootElement;
            if (Str(o, "type") != "auth_ok") throw Refused(o);
            var user = Str(o, "user");
            var sides = o.TryGetProperty("sides", out var s) && s.ValueKind == JsonValueKind.Array
                ? s.EnumerateArray().Select(e => e.GetString() ?? "").Where(x => x.Length > 0).ToList() : [];

            var side = await login.ChooseSide(sides, user) ?? throw new AuthenticationException("No side chosen.");
            await SendLineAsync(stream, new { type = "side", coalition = side }, ct);
            using var sideOk = await ReadReplyAsync(reader, ct);
            if (Str(sideOk.RootElement, "type") != "side_ok") throw Refused(sideOk.RootElement);
            return new NeboConnection(tcp, reader, Str(sideOk.RootElement, "coalition") ?? side) { User = user };
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private static async Task<TcpClient> OpenAsync(string host, int port, CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await tcp.ConnectAsync(host, port, timeout.Token);
            return tcp;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private static async Task<JsonDocument> ReadReplyAsync(StreamReader reader, CancellationToken ct) =>
        JsonDocument.Parse(await reader.ReadLineAsync(ct) ?? throw new AuthenticationException("Server closed the connection."));

    private static Task SendLineAsync(Stream stream, object message, CancellationToken ct) =>
        stream.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message) + "\n"), ct).AsTask();

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static AuthenticationException Refused(JsonElement reply) =>
        new(RefusalText(Str(reply, "reason")) ?? "Wrong password.");

    /// <summary>The login of servers before 0.10: the password decides the coalition.</summary>
    private static async Task<NeboConnection> ConnectOldAsync(string host, int port, string password, CancellationToken ct)
    {
        var tcp = await OpenAsync(host, port, ct);
        try
        {
            var stream = tcp.GetStream();
            var auth = JsonSerializer.Serialize(new AuthRequest(password, ClientName)) + "\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(auth), ct);

            var reader = new StreamReader(stream, new UTF8Encoding(false));
            var line = await reader.ReadLineAsync(ct) ?? throw new AuthenticationException("Server closed the connection.");
            var reply = JsonSerializer.Deserialize<Message>(line);
            if (reply?.Type != "auth_ok" || reply.Coalition is null)
                throw new AuthenticationException(RefusalText(reply?.Reason) ?? "Wrong password.");
            return new NeboConnection(tcp, reader, reply.Coalition);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>Yields each picture the server sends until the connection closes.</summary>
    public async IAsyncEnumerable<Picture> ReadPicturesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var m in ReadMessagesAsync(ct))
            if (m is Picture p) yield return p;
    }

    /// <summary>Yields every known server message (<see cref="Picture"/>, <see cref="Theatre"/>,
    /// <see cref="TerrainMessage"/>, <see cref="FootholdState"/>) until the connection closes;
    /// unknown types are skipped.</summary>
    public async IAsyncEnumerable<object> ReadMessagesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        while (await _reader.ReadLineAsync(ct) is { } line)
        {
            using var doc = JsonDocument.Parse(line);
            if (!doc.RootElement.TryGetProperty("type", out var type)) continue;
            object? message = type.GetString() switch
            {
                "picture" => doc.RootElement.Deserialize<Picture>(),
                "theatre" => doc.RootElement.Deserialize<Theatre>(),
                "terrain" => doc.RootElement.Deserialize<TerrainMessage>(),
                "foothold" => doc.RootElement.Deserialize<FootholdState>(),
                "airfields" => doc.RootElement.Deserialize<AirfieldsMessage>(),
                "weather" => doc.RootElement.Deserialize<WeatherMessage>(),
                "unit_types" => doc.RootElement.Deserialize<UnitTypesMessage>(),
                "command_result" => doc.RootElement.Deserialize<CommandResult>(),
                "lso" => doc.RootElement.Deserialize<LsoMessage>(),
                "marks" => doc.RootElement.Deserialize<MarksMessage>(),
                "ids" => doc.RootElement.Deserialize<IdsMessage>(),
                "threats" => doc.RootElement.Deserialize<ThreatsMessage>(),
                "nctr" => doc.RootElement.Deserialize<NctrMessage>(),
                "pilots" => doc.RootElement.Deserialize<Dtc.PilotsMessage>(),
                "designations" => doc.RootElement.Deserialize<DesignationsMessage>(),
                "dtc_live" => System.Text.Json.Nodes.JsonNode.Parse(doc.RootElement.GetRawText()) as System.Text.Json.Nodes.JsonObject,
                "closed" => doc.RootElement.Deserialize<ClosedMessage>(),
                _ => null,
            };
            if (message is not null) yield return message;
        }
    }

    /// <summary>What a refused login or a closed connection means, for the status bar.</summary>
    public static string? RefusalText(string? reason) => reason switch
    {
        "flying" => "Refused: your address is in a cockpit on this server (controllers don't fly).",
        "role" => "Refused: your Discord account has no controller role on this server's Discord.",
        "discord" => "Refused: Discord did not confirm the login (try again, and click Authorize in Discord).",
        "discord_down" => "Refused: the server could not reach Discord. Try again in a minute.",
        "password" => "Wrong server password.",
        "update" => "This server needs NEBO 0.10 or later (Discord login): update the client.",
        "side" => "Refused: that side is not open to you.",
        "closed" => "This server has no controller logins set up.",
        null => null,
        _ => $"Refused by the server ({reason}).",
    };

    public ValueTask DisposeAsync()
    {
        _reader.Dispose();
        _tcp.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>How a login asks for what it needs: the server password (read only once Discord has
/// passed), the side to join among those the server offers (with the verified Discord name, if
/// any; null = cancel), and progress lines for the status bar.</summary>
public sealed record LoginOptions(
    Func<string?> Password,
    Func<IReadOnlyList<string>, string?, Task<string?>> ChooseSide,
    Action<string>? Progress = null);
