using System.IO;
using System.Media;
using Nebo.Client.Core;

namespace Nebo.Client;

/// <summary>Missile launches at the side's players: short alarms per launch (Settings.LaunchAlarm:
/// on/off, volume, how many), one after the other (never over each other, however many come at
/// once), and the jump key that centres the GCI maps on the newest launch's target, then the
/// older ones.</summary>
public static class LaunchAlarm
{
    /// <summary>At most this many alarms wait: a salvo does not ring for half a minute.</summary>
    private const int MaxQueued = 8;

    private static LaunchAlarmSettings Settings => AppState.Settings.LaunchAlarm;
    private static (double Volume, byte[] Wav) _wav = (-1, []);
    private static readonly LaunchCycle Cycle = new();
    private static readonly object Gate = new();
    private static int _queued;
    private static bool _playing;

    /// <summary>Maps that centre on a launch (GCI maps on screen); true when one did.</summary>
    public static event Func<Launch, bool>? JumpRequested;

    public static void Start() => AppState.Store.Launched += _ =>
    {
        if (Settings.Sound) Enqueue(Math.Clamp(Settings.Repeats, 1, 3));
    };

    /// <summary>One alarm at the current volume, whatever the settings (the menu's Test sound).</summary>
    public static void Test() => Enqueue(1);

    /// <summary>The jump key: false (key left to others) when nothing is in flight at a player.</summary>
    public static bool Jump()
    {
        if (Cycle.Next(AppState.Store.Launches, DateTime.UtcNow) is not { } launch) return false;
        lock (Gate) _queued = 0;  // seen: the alarms still waiting are dropped
        AppState.Store.Select(launch.Target);
        var moved = false;
        if (JumpRequested is { } handlers)
            foreach (var h in handlers.GetInvocationList().Cast<Func<Launch, bool>>()) moved |= h(launch);
        return moved;
    }

    private static void Enqueue(int alarms)
    {
        lock (Gate)
        {
            _queued = Math.Min(MaxQueued, _queued + alarms);
            if (_playing) return;
            _playing = true;
        }
        Task.Run(() =>
        {
            try
            {
                while (true)
                {
                    lock (Gate)
                    {
                        if (_queued == 0) { _playing = false; return; }
                        _queued--;
                    }
                    // Per alarm: a volume changed in the menu applies to the next one.
                    var volume = Math.Clamp(Settings.Volume, 0.05, 1);
                    var wav = _wav;
                    if (wav.Volume != volume) _wav = wav = (volume, MakeWav(volume));
                    using var player = new SoundPlayer(new MemoryStream(wav.Wav));
                    player.Load();
                    player.PlaySync();
                }
            }
            catch (Exception) { lock (Gate) { _queued = 0; _playing = false; } }  // no audio device: the banner still shows
        });
    }

    /// <summary>One alarm: a fast high-low warble (0.42 s) and a short gap, 16-bit mono PCM.</summary>
    private static byte[] MakeWav(double loudness)
    {
        const int rate = 22050;
        var samples = new List<short>();
        void Tone(double hz, double seconds, double volume)
        {
            var n = (int)(rate * seconds);
            for (var i = 0; i < n; i++)
            {
                var edge = Math.Min(1, Math.Min(i, n - i) / (rate * 0.004));  // no clicks
                samples.Add((short)(Math.Sin(2 * Math.PI * hz * i / rate) * volume * edge * short.MaxValue));
            }
        }
        for (var k = 0; k < 3; k++)
        {
            Tone(1400, 0.07, loudness);
            Tone(950, 0.07, loudness);
        }
        Tone(0, 0.18, 0);
        return Wav(samples, rate);
    }

    /// <summary>16-bit mono PCM samples as a WAV file.</summary>
    internal static byte[] Wav(List<short> samples, int rate)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + samples.Count * 2);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1);
        w.Write((short)1);
        w.Write(rate);
        w.Write(rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(samples.Count * 2);
        foreach (var s in samples) w.Write(s);
        w.Flush();
        return ms.ToArray();
    }
}
