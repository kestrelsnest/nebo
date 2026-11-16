using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Nebo.Client.Core.Dtc;

/// <summary>The cartridge tables (each aircraft's default cartridge and editor definitions) made
/// from the controller's own DCS install by DCS's own DTC code: the generator scripts of
/// tools/DtcDefs, built into the client, run with DCS's bin\luae.exe from the DCS folder. The
/// tables are Eagle Dynamics' data, so NEBO never ships them. They are made on first use
/// (~0.2 s for all aircraft) and kept per DCS version under the cache folder, so a DCS update
/// makes them again. Without DCS (or without an aircraft's DTC files) that aircraft has none.</summary>
public static partial class DtcData
{
    /// <summary>Bump when the generator scripts change, so tables made by older ones are not used.</summary>
    public const int GeneratorVersion = 1;

    private static readonly (string Type, string Script)[] Generators =
        [(Cartridge.HornetType, "hornet"), (Cartridge.ViperType, "viper"), (Cartridge.TomcatType, "tomcat")];

    private static readonly object Lock = new();
    private static Func<string?> _dcs = () => null;
    private static string _cacheRoot = Path.GetTempPath();
    private static string? _dir;          // this DCS version's tables, once looked up
    private static string? _dirFor;       // the DCS folder _dir was made from

    /// <summary>Why there are no tables (no DCS, generator failed), for the DTC tab; null when fine.</summary>
    public static string? Problem { get; private set; }

    /// <summary>Where the DCS install is (asked again on every look-up, so a new choice in Map
    /// data applies) and the folder the tables are kept under (&lt;cacheRoot&gt;\dtc-defs).</summary>
    public static void Configure(Func<string?> dcsRoot, string cacheRoot)
    {
        lock (Lock)
        {
            _dcs = dcsRoot;
            _cacheRoot = cacheRoot;
            _dir = _dirFor = null;
        }
    }

    /// <summary>A table file ("FA-18C_hornet.defs.json"), or null when it cannot be had.</summary>
    public static string? Read(string name)
    {
        var dir = Folder();
        if (dir is null) return null;
        var path = Path.Combine(dir, name);
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (IOException) { return null; }
    }

    private static string? Folder()
    {
        lock (Lock)
        {
            var dcs = _dcs();
            if (dcs is null || !DcsLocator.IsDcsFolder(dcs))
            {
                Problem = "The data cartridge needs DCS World on this PC: its aircraft tables are read from your own install (Window → Map data).";
                return null;
            }
            if (_dir is not null && string.Equals(_dirFor, dcs, StringComparison.OrdinalIgnoreCase)) return _dir;
            var dir = Path.Combine(_cacheRoot, "dtc-defs", $"{Safe(DcsVersion(dcs))}-g{GeneratorVersion}");
            if (!File.Exists(Path.Combine(dir, "done")))
            {
                var error = Make(dcs, dir);
                if (error is not null)
                {
                    Problem = "The data cartridge tables could not be made from DCS: " + error;
                    return null;
                }
            }
            Problem = null;
            _dir = dir;
            _dirFor = dcs;
            return dir;
        }
    }

    /// <summary>The install's version (autoupdate.cfg "version"), else DCS.exe's date.</summary>
    public static string DcsVersion(string dcs)
    {
        try
        {
            var cfg = Path.Combine(dcs, "autoupdate.cfg");
            if (File.Exists(cfg) && VersionRegex().Match(File.ReadAllText(cfg)) is { Success: true } m) return m.Groups[1].Value;
            var exe = new[] { "bin", "bin-mt" }.Select(b => Path.Combine(dcs, b, "DCS.exe")).FirstOrDefault(File.Exists);
            if (exe is not null) return File.GetLastWriteTimeUtc(exe).ToString("yyyyMMddHHmmss");
        }
        catch (IOException) { }
        return "unknown";
    }

    /// <summary>Runs each aircraft's generator into a fresh folder, then moves it into place.
    /// An aircraft whose DTC files are missing is skipped; returns an error only when none worked.</summary>
    private static string? Make(string dcs, string dir)
    {
        var luae = new[] { "bin", "bin-mt" }.Select(b => Path.Combine(dcs, b, "luae.exe")).FirstOrDefault(File.Exists);
        if (luae is null) return "bin\\luae.exe not found in " + dcs;
        var work = dir + ".new";
        try
        {
            if (Directory.Exists(work)) Directory.Delete(work, true);
            var scripts = Path.Combine(work, "gen");
            Directory.CreateDirectory(scripts);
            foreach (var file in new[] { "common", "hornet", "viper", "tomcat" })
                File.WriteAllText(Path.Combine(scripts, file + ".lua"), Script(file));
            var errors = new List<string>();
            foreach (var (type, script) in Generators)
            {
                var psi = new ProcessStartInfo(luae)
                {
                    WorkingDirectory = dcs,  // the DTC files load their parts by "./CoreMods/..." paths
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                psi.ArgumentList.Add(Path.Combine(scripts, script + ".lua"));
                psi.ArgumentList.Add(work);
                using var p = Process.Start(psi)!;
                var stderr = p.StandardError.ReadToEndAsync();
                p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(30_000)) { p.Kill(); errors.Add(type + ": timed out"); continue; }
                if (p.ExitCode != 0 || !File.Exists(Path.Combine(work, type + ".defs.json")))
                    errors.Add($"{type}: {stderr.Result.Trim().Split('\n').LastOrDefault() ?? $"exit {p.ExitCode}"}");
            }
            Directory.Delete(scripts, true);
            if (errors.Count == Generators.Length) return string.Join("; ", errors);
            File.WriteAllText(Path.Combine(work, "done"), string.Join("\n", errors));
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.Move(work, dir);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return e.Message;
        }
    }

    private static string Script(string name)
    {
        using var s = typeof(DtcData).Assembly.GetManifestResourceStream("DtcGen." + name + ".lua")
                      ?? throw new InvalidOperationException("generator script missing: " + name);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    private static string Safe(string s) => InvalidChars().Replace(s, "_");

    [GeneratedRegex("\"version\"\\s*:\\s*\"([^\"]+)\"")]
    private static partial Regex VersionRegex();

    [GeneratedRegex("[^A-Za-z0-9._-]")]
    private static partial Regex InvalidChars();
}
