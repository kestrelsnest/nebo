using System.Windows.Media;
using Nebo.Client.Core;

namespace Nebo.Client;

/// <summary>The server's chat log for this controller (DLL 0.17 "chat" channel) and the tray lines
/// it raises: players connecting and leaving, and chat that speaks to controllers (your callsign,
/// GCI, ATC, AWACS, NEBO). The rest is in the Chat panel only. Lives on the UI thread.</summary>
public static class ChatDesk
{
    public static ChatLog Log { get; } = new();

    private static readonly Color JoinColor = Color.FromRgb(0x8A, 0x96, 0xA8);
    private static readonly Color ChatColor = Color.FromRgb(0x7C, 0xD9, 0x7C);

    public static void Apply(ChatMessage m)
    {
        var (added, history) = Log.Apply(m);
        if (history) return;  // the server's backlog after connecting: no tray
        var callsign = AppState.Connection.Callsign;
        foreach (var l in added)
        {
            if (l.IsJoin)
                MessageTray.Info(l.Kind == "join" ? $"{l.From} connected" : $"{l.From} left the server", accent: JoinColor);
            else if (ChatLog.ForControllers(l, callsign))
                MessageTray.Info($"{l.From}: {l.Text}", l.Kind == "side" ? "Side chat" : "All chat", ChatColor);
        }
    }

    public static void Clear() => Log.Clear();
}
