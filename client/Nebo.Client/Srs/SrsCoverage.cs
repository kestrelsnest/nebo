using System.Windows.Threading;
using Nebo.Client.Core;

namespace Nebo.Client.Srs;

/// <summary>Where aircraft at the chosen altitude hear the controller's radio (the SRS position):
/// recomputed off the UI thread when the position moves more than 300 m or 100 m in height, the
/// altitude setting changes or the terrain arrives; checked every 2 s (a carrier or AWACS moves).
/// Maps draw <see cref="Coverage"/> in light blue.</summary>
public sealed class SrsCoverage
{
    public static SrsCoverage Instance { get; } = new();

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private string _key = "";
    private bool _busy;

    public RadioCoverage? Coverage { get; private set; }
    public event Action? Changed;

    private SrsCoverage()
    {
        _timer.Tick += (_, _) => Update();
        _timer.Start();
    }

    private static SrsSettings Settings => AppState.Settings.Srs;

    /// <summary>Check now (a setting changed).</summary>
    public void Update()
    {
        var store = AppState.Store;
        if (!Settings.ShowCoverage || store.Theatre is not { } theatre || SrsBridge.Instance.Position() is not { } p)
        {
            Set(null, "");
            return;
        }
        var (x, z) = theatre.FromLatLon(p.Lat, p.Lon);
        var antenna = p.Alt + 10;  // a ground radio's mast; an AWACS's own altitude is barely changed
        var target = Settings.CoverageAltFt * 0.3048;
        var grid = store.Terrain;
        var key = $"{Math.Round(x / 300)},{Math.Round(z / 300)},{Math.Round(antenna / 100)},{target:0},{grid?.Id}";
        if (key == _key || _busy) return;
        _busy = true;
        Task.Run(() => RadioCoverage.Compute(grid, x, z, antenna, target)).ContinueWith(t =>
        {
            _busy = false;
            if (t.IsCompletedSuccessfully) Set(t.Result, key);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Set(RadioCoverage? coverage, string key)
    {
        if (coverage is null && Coverage is null) return;
        Coverage = coverage;
        _key = key;
        Changed?.Invoke();
    }
}
