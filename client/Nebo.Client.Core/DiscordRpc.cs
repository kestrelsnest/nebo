using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Nebo.Client.Core;

/// <summary>The Discord app on this PC, through its local pipe (discord-ipc-N): asks it to
/// authorize an application, which shows Discord's own popup, and returns the one-time code the
/// NEBO server swaps for the user's identity and roles. Nothing here is trusted by the server.</summary>
public static class DiscordRpc
{
    private static readonly string[] Scopes = ["identify", "guilds", "guilds.members.read"];

    public static async Task<string> AuthorizeAsync(string clientId, CancellationToken ct)
    {
        await using var pipe = await OpenAsync(ct);
        await SendAsync(pipe, 0, new { v = 1, client_id = clientId }, ct);
        using (var ready = await ReadAsync(pipe, ct))
        {
            if (Str(ready.RootElement, "evt") != "READY")
                throw new AuthenticationException("Discord refused NEBO: " + Message(ready.RootElement));
        }
        var nonce = Guid.NewGuid().ToString();
        await SendAsync(pipe, 1, new { cmd = "AUTHORIZE", nonce, args = new { client_id = clientId, scopes = Scopes } }, ct);
        while (true)
        {
            using var reply = await ReadAsync(pipe, ct);
            var root = reply.RootElement;
            if (Str(root, "nonce") != nonce) continue;  // other events
            if (Str(root, "evt") == "ERROR" || !root.TryGetProperty("data", out var data) || !data.TryGetProperty("code", out var code))
                throw new AuthenticationException("Discord: " + Message(root));
            return code.GetString() ?? throw new AuthenticationException("Discord sent no code.");
        }
    }

    private static async Task<NamedPipeClientStream> OpenAsync(CancellationToken ct)
    {
        for (var i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(200, ct);
                return pipe;
            }
            catch (TimeoutException) { await pipe.DisposeAsync(); }
            catch (IOException) { await pipe.DisposeAsync(); }
        }
        throw new AuthenticationException("This server logs in with Discord: start the Discord app and connect again.");
    }

    private static async Task SendAsync(Stream pipe, int op, object payload, CancellationToken ct)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(payload);
        var frame = new byte[8 + data.Length];
        BitConverter.TryWriteBytes(frame.AsSpan(0, 4), op);
        BitConverter.TryWriteBytes(frame.AsSpan(4, 4), data.Length);
        data.CopyTo(frame, 8);
        await pipe.WriteAsync(frame, ct);
    }

    private static async Task<JsonDocument> ReadAsync(Stream pipe, CancellationToken ct)
    {
        var header = new byte[8];
        await pipe.ReadExactlyAsync(header, ct);
        var length = BitConverter.ToInt32(header, 4);
        if (length is < 0 or > 1 << 20) throw new AuthenticationException("Discord sent a malformed reply.");
        var body = new byte[length];
        await pipe.ReadExactlyAsync(body, ct);
        return JsonDocument.Parse(body);
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Message(JsonElement root) =>
        root.TryGetProperty("data", out var d) && Str(d, "message") is { } m ? m : "no answer";
}
