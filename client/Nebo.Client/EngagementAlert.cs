using System.IO;
using System.Media;
using System.Windows.Threading;
using Nebo.Client.Core;

namespace Nebo.Client;

/// <summary>Engagement lines reaching their alert distance (Engagements panel): checked once a
/// second whether or not the panel is open (lines whose contact is lost are dropped then), a short quiet warning for each line that closed to it
/// (three short ticks, unlike the launch alarm's warble; half its loudness).</summary>
public static class EngagementAlert
{
    private static (double Volume, byte[] Wav) _wav = (-1, []);
    private static readonly object Gate = new();
    private static int _queued;
    private static bool _playing;

    /// <summary>Lines whose alert just fired (the panel flashes them).</summary>
    public static event Action<IReadOnlyList<EngagementLine>>? Fired;

    public static void Start()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            var book = AppState.Engagements;
            if (book.Lines.Count == 0) return;
            book.DropLost(AppState.Store);  // a contact nobody holds any more: its engagement is over
            var fired = book.CheckAlerts(book.Rows(AppState.Store));
            if (fired.Count == 0) return;
            Fired?.Invoke(fired);
            Enqueue(fired.Count);
        };
        timer.Start();
    }

    /// <summary>One warning, whatever is in the list (a test).</summary>
    public static void Test() => Enqueue(1);

    private static void Enqueue(int warnings)
    {
        lock (Gate)
        {
            _queued = Math.Min(4, _queued + warnings);
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
                    var volume = Math.Clamp(AppState.Settings.LaunchAlarm.Volume, 0.05, 1);
                    var wav = _wav;
                    if (wav.Volume != volume) _wav = wav = (volume, MakeWav(volume));
                    using var player = new SoundPlayer(new MemoryStream(wav.Wav));
                    player.Load();
                    player.PlaySync();
                }
            }
            catch (Exception) { lock (Gate) { _queued = 0; _playing = false; } }  // no audio device: the row still shows it
        });
    }

    /// <summary>A short, quiet warning: three ticks (1.5 kHz, 30 ms each, 60 ms apart), half the
    /// launch alarm's loudness (the user's pick of ten samples, "triple tick").</summary>
    private static byte[] MakeWav(double loudness)
    {
        const int rate = 22050;
        var samples = new List<short>();
        void Tick()
        {
            var n = (int)(rate * 0.03);
            for (var i = 0; i < n; i++)
            {
                var edge = Math.Min(1, Math.Min(i, n - i) / (rate * 0.005));  // no clicks
                samples.Add((short)(Math.Sin(2 * Math.PI * 1500 * i / rate) * loudness * 0.5 * edge * short.MaxValue));
            }
        }
        for (var k = 0; k < 3; k++)
        {
            if (k > 0) samples.AddRange(new short[(int)(rate * 0.06)]);
            Tick();
        }
        samples.AddRange(new short[(int)(rate * 0.3)]);
        return LaunchAlarm.Wav(samples, rate);
    }
}
