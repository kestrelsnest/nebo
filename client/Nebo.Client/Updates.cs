using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace Nebo.Client;

/// <summary>The new-version notice: at most once a day (Settings → General) the client asks GitHub
/// for the newest NEBO release. When it is newer than this client, the main window shows a bar;
/// its "Download and install" fetches NEBO-Setup and starts it (the setup closes and updates the
/// client). Nothing is downloaded or installed without that click.</summary>
public static class Updates
{
    private const string LatestRelease = "https://api.github.com/repos/kestrelsnest/nebo/releases/latest";

    /// <param name="SetupUrl">The release's NEBO-Setup .exe, or "" when it has none.</param>
    public sealed record Release(string Version, string SetupUrl, string PageUrl);

    /// <summary>The newer release, or null (none, checked today, switched off, offline).</summary>
    public static async Task<Release?> CheckAsync()
    {
        var settings = AppState.Settings;
        if (!settings.CheckForUpdates || Dev.DebugSnapshot.Active || DateTime.UtcNow - settings.LastUpdateCheck < TimeSpan.FromDays(1))
            return null;
        settings.LastUpdateCheck = DateTime.UtcNow;
        try
        {
            using var http = Client();
            using var doc = JsonDocument.Parse(await http.GetStringAsync(LatestRelease));
            var root = doc.RootElement;
            var version = root.GetProperty("tag_name").GetString()?.TrimStart('v') ?? "";
            if (!IsNewer(version, AboutWindow.Version)) return null;
            var setup = root.GetProperty("assets").EnumerateArray()
                .Select(a => (Name: a.GetProperty("name").GetString() ?? "", Url: a.GetProperty("browser_download_url").GetString() ?? ""))
                .FirstOrDefault(a => a.Name.StartsWith("NEBO-Setup", StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            return new Release(version, setup.Url ?? "", root.GetProperty("html_url").GetString() ?? "");
        }
        catch (Exception)
        {
            return null;  // offline, no release yet, GitHub's rate limit: tomorrow again
        }
    }

    /// <summary>"0.16.1" is newer than "0.16.0"; anything that is not a plain version is not.</summary>
    public static bool IsNewer(string candidate, string current) =>
        Version.TryParse(candidate, out var a) && Version.TryParse(current, out var b) && a > b;

    /// <summary>Downloads the setup into %TEMP% and starts it; the caller then closes the client.</summary>
    public static async Task DownloadAndStartAsync(Release release, IProgress<string> status)
    {
        var path = Path.Combine(Path.GetTempPath(), $"NEBO-Setup-{release.Version}.exe");
        using (var http = Client())
        using (var response = await http.GetAsync(release.SetupUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using var source = await response.Content.ReadAsStreamAsync();
            await using var file = File.Create(path);
            var buffer = new byte[1 << 16];
            long done = 0;
            for (int n; (n = await source.ReadAsync(buffer)) > 0;)
            {
                await file.WriteAsync(buffer.AsMemory(0, n));
                done += n;
                status.Report(total > 0 ? $"Downloading NEBO {release.Version}… {done * 100 / total} %" : $"Downloading NEBO {release.Version}…");
            }
        }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static HttpClient Client()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(Map.TileBaseMaps.UserAgent);  // GitHub asks for one
        return http;
    }
}
