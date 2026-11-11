using System.IO;

namespace Nebo.Client;

/// <summary>NEBO's folders: settings and layouts in %AppData%\NEBO (or NEBO_HOME: separate
/// profiles, tests); caches, DCS Map tiles, captured maps and logs in %LocalAppData%\NEBO.</summary>
public static class AppPaths
{
    private static readonly string? Home = Environment.GetEnvironmentVariable("NEBO_HOME") is { Length: > 0 } h ? h : null;

    /// <summary>NEBO_HOME is set: a separate profile.</summary>
    public static bool CustomHome => Home is not null;
    public static string Settings { get; } = Home ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NEBO");
    public static string Local { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEBO");
    public static string Cache { get; } = Path.Combine(Local, "cache");

    /// <summary>Coming from 0.15.x (code name DCB): %AppData%\DCB (settings, layouts) and
    /// %LocalAppData%\DCB (captured maps, caches) become the NEBO folders, once, when the NEBO
    /// ones do not exist yet. Skipped for a separate profile.</summary>
    public static void MigrateFromDcb()
    {
        if (CustomHome) return;
        foreach (var root in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
        {
            var baseDir = Environment.GetFolderPath(root);
            string old = Path.Combine(baseDir, "DCB"), now = Path.Combine(baseDir, "NEBO");
            if (!Directory.Exists(old) || Directory.Exists(now)) continue;
            try { Directory.Move(old, now); }
            catch (IOException) { }                  // in use (an old client still open): keep both
            catch (UnauthorizedAccessException) { }
        }
    }
}
