using System.Diagnostics;
using System.Reflection;
using System.Windows.Threading;

namespace Nebo.Client;

/// <summary>The longest job the UI thread ran (timers, bindings, message handlers, input) since the
/// last map frame, with the method that ran: the slow-frame log names what held a zoom up.</summary>
public static class UiOps
{
    private static readonly FieldInfo? MethodField = typeof(DispatcherOperation).GetField("_method", BindingFlags.Instance | BindingFlags.NonPublic);
    private static long _started;
    private static DispatcherOperation? _current;
    private static (double Ms, string? Name) _longest;

    /// <summary>How late the UI thread answered a top-priority call (posted every 15 ms from a
    /// background thread) since the last map frame: high while the UI thread is busy in any way
    /// (input handlers and window messages included), low when it waited on WPF's render thread.</summary>
    private static double _maxReplyMs;

    public static double PeekMaxReply() => _maxReplyMs;

    public static double TakeMaxReply()
    {
        var r = _maxReplyMs;
        _maxReplyMs = 0;
        return r;
    }

    private static void Watch(Dispatcher dispatcher)
    {
        var thread = new Thread(() =>
        {
            while (!dispatcher.HasShutdownStarted)
            {
                var posted = Stopwatch.GetTimestamp();
                var op = dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
                {
                    var ms = Stopwatch.GetElapsedTime(posted).TotalMilliseconds;
                    if (ms > _maxReplyMs) _maxReplyMs = ms;
                });
                op.Wait(TimeSpan.FromSeconds(5));
                Thread.Sleep(15);
            }
        }) { IsBackground = true, Name = "UI reply probe", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    /// <summary>Hooks the UI thread's dispatcher (call once, on the UI thread).</summary>
    public static void Start(Dispatcher dispatcher)
    {
        Watch(dispatcher);
        dispatcher.Hooks.OperationStarted += (_, e) => { _current = e.Operation; _started = Stopwatch.GetTimestamp(); };
        dispatcher.Hooks.OperationCompleted += (_, e) => Done(e.Operation);
        dispatcher.Hooks.OperationAborted += (_, e) => Done(e.Operation);
    }

    private static void Done(DispatcherOperation op)
    {
        if (op != _current || _started == 0) return;
        var ms = Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
        _started = 0;
        if (ms <= _longest.Ms || ms < 8) return;
        string name;
        try
        {
            name = MethodField?.GetValue(op) is Delegate d ? $"{d.Method.DeclaringType?.Name}.{d.Method.Name}" : op.Priority.ToString();
        }
        catch (Exception)
        {
            name = op.Priority.ToString();
        }
        _longest = (ms, $"{name} ({op.Priority})");
    }

    /// <summary>The longest job since the last call (then forgotten).</summary>
    public static (double Ms, string? Name) TakeLongest()
    {
        var l = _longest;
        _longest = default;
        return l;
    }
}

/// <summary>Development aid: time spent in each map layer's OnRender (read by the snapshot zoom test).</summary>
public static class RenderStats
{
    private static readonly Dictionary<string, (int Count, double TotalMs, double MaxMs)> Stats = [];

    public readonly struct Scope(string name) : IDisposable
    {
        private readonly long _start = Stopwatch.GetTimestamp();

        public void Dispose()
        {
            var ms = Stopwatch.GetElapsedTime(_start).TotalMilliseconds;
            lock (Stats)
            {
                Stats.TryGetValue(name, out var s);
                Stats[name] = (s.Count + 1, s.TotalMs + ms, Math.Max(s.MaxMs, ms));
            }
        }
    }

    public static Scope Measure(string name) => new(name);

    /// <summary>Records a duration measured elsewhere (part of a larger step).</summary>
    public static void Add(string name, double ms)
    {
        lock (Stats)
        {
            Stats.TryGetValue(name, out var s);
            Stats[name] = (s.Count + 1, s.TotalMs + ms, Math.Max(s.MaxMs, ms));
        }
    }

    private static readonly Dictionary<string, int> Counters = [];

    /// <summary>Counts an event (e.g. a frame drawn with tiles still missing).</summary>
    public static void Count(string name)
    {
        lock (Counters) Counters[name] = Counters.GetValueOrDefault(name) + 1;
    }

    public static void Reset()
    {
        lock (Stats) Stats.Clear();
        lock (Counters) Counters.Clear();
    }

    public static IEnumerable<string> CounterReport()
    {
        lock (Counters) return Counters.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} = {kv.Value}").ToList();
    }

    public static IEnumerable<string> Report()
    {
        lock (Stats)
            return Stats.OrderByDescending(kv => kv.Value.TotalMs)
                .Select(kv => $"{kv.Key,-14} renders={kv.Value.Count,4} avg={kv.Value.TotalMs / kv.Value.Count,7:F2} ms max={kv.Value.MaxMs,7:F2} ms total={kv.Value.TotalMs,8:F0} ms")
                .ToList();
    }
}
