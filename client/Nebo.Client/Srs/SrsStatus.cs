namespace Nebo.Client.Srs;

/// <summary>What NEBO's SRS connections are doing: the full lines (Settings → SRS) and one line for
/// the main window's status bar.</summary>
public static class SrsStatus
{
    public static string Lines()
    {
        var running = SrsApp.Running;
        var app = running
            ? (SrsChannels.IsLive ? "SRS app connected: channels read live from it." : "SRS app running, not connected (click Connect in its window, External AWACS Mode): channels as NEBO set them.")
            : SrsListen.Instance.AppStatus is { Length: > 0 } s ? s : "SRS app not running.";
        var bridge = SrsBridge.Instance;
        return string.Join("\n", new[] { SrsListen.Instance.Status, app, SrsAutoConnect.Status, bridge.CommandStatus, bridge.PositionStatus, bridge.LineOfSightStatus }
            .Where(t => t is { Length: > 0 }));
    }

    /// <summary>"SRS: app connected · listening · radio at Overlord1-1 (AWACS)".</summary>
    public static string Short()
    {
        var app = !SrsApp.Running ? "app not running" : SrsChannels.IsLive ? "app connected" : "app not connected";
        var listen = SrsListen.Instance.Connected ? "listening" : "not listening";
        return $"SRS: {app} · {listen} · radio at {RadioPosition.Describe()}";
    }
}
