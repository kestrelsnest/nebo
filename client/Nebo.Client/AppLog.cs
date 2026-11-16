using System.IO;
using System.Reflection;

namespace Nebo.Client;

/// <summary>The client's log, %LocalAppData%\NEBO\nebo.log: start and exit, connections (every
/// status line), the first message of each kind per connection, failed commands, render errors
/// and crashes. Each start moves the last one to nebo-previous.log; a session stops writing at
/// <see cref="MaxBytes"/>. Never passwords, never the text of commands.</summary>
public static class AppLog
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private static readonly object Gate = new();
    private static StreamWriter? _writer;
    private static long _written;
    private static bool _full;

    public static string Path { get; } = System.IO.Path.Combine(AppPaths.Local, "nebo.log");

    public static void Start()
    {
        lock (Gate)
        {
            if (_writer is not null) return;
            try
            {
                Directory.CreateDirectory(AppPaths.Local);
                var previous = System.IO.Path.Combine(AppPaths.Local, "nebo-previous.log");
                if (File.Exists(Path)) File.Move(Path, previous, overwrite: true);
                _writer = new StreamWriter(new FileStream(Path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _writer = null;  // a second client has it open, or no rights: no log, no fuss
                return;
            }
        }
        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        Write($"NEBO {version.Split('+')[0]} started, {Environment.OSVersion.VersionString}, .NET {Environment.Version}, " +
              $"renderer {Environment.GetEnvironmentVariable("NEBO_RENDERER") ?? "gpu"}" +
              (AppPaths.CustomHome ? $", profile {AppPaths.Settings}" : ""));
    }

    public static void Write(string line)
    {
        lock (Gate)
        {
            if (_writer is null || _full) return;
            var text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}";
            try
            {
                if (_written + text.Length > MaxBytes)
                {
                    _writer.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} log full ({MaxBytes / 1024 / 1024} MB): nothing more this session");
                    _full = true;
                    return;
                }
                _writer.WriteLine(text);
                _written += text.Length + 2;
            }
            catch (IOException) { }
        }
    }

    public static void Error(string what, Exception e) => Write($"ERROR {what}: {e}");

    public static void Stop(string why)
    {
        Write($"NEBO exiting ({why})");
        lock (Gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
