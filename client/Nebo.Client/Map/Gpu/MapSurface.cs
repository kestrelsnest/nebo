using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace Nebo.Client.Map.Gpu;

/// <summary>What a layer draws for: the view (DIPs) and the device pixels per DIP.</summary>
public readonly record struct MapFrame(MapView View, float PixelsPerDip);

/// <summary>One map layer drawn with Skia into the map's surface, bottom-up.</summary>
public abstract class MapLayer
{
    private bool _visible = true;

    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible == value) return;
            _visible = value;
            Invalidate();
        }
    }

    public MapSurface? Surface { get; internal set; }

    private float _opacity = 1;

    /// <summary>The whole layer drawn faded (0–1): a map filter's stage (zones, airfields).</summary>
    public float Opacity
    {
        get => _opacity;
        set
        {
            if (_opacity == value) return;
            _opacity = value;
            Invalidate();
        }
    }

    /// <summary>Asks for a redraw; safe from any thread.</summary>
    public void Invalidate() => Surface?.Invalidate();

    public abstract void Draw(SKCanvas canvas, in MapFrame frame);
}

/// <summary>A map drawn by Skia on the GPU (OpenGL through SKGLElement), or by Skia on the CPU
/// when OpenGL is unavailable (or NEBO_RENDERER=cpu). Draws only when something changed — at most
/// once per display frame — and every frame while a smooth zoom runs. Left-drag pans, the
/// wheel zooms around the pointer.</summary>
public sealed class MapSurface : Decorator
{
    private readonly FrameworkElement _element;
    private bool _queued;
    private bool _dragging;
    private Point _last;
    private long _lastTick;

    public MapNavigator Navigator { get; } = new();
    public IReadOnlyList<MapLayer> Layers => _layers;
    private readonly List<MapLayer> _layers = [];
    public SKColor BackColor { get; set; } = new(0x0A, 0x14, 0x1C);
    public bool UsesGpu { get; }

    /// <summary>Frames painted and the CPU time spent painting them (layers + GPU submit).</summary>
    public (int Frames, double TotalMs, double MaxMs) Stats => _stats;
    private (int Frames, double TotalMs, double MaxMs) _stats;

    /// <summary>Gaps between consecutive painted frames while the map keeps moving (paints less
    /// than 250 ms apart): the longest, and how many exceeded 25 ms (a visibly missed frame).</summary>
    public (double MaxGapMs, int Over25, int Over50) Gaps => _gaps;
    private (double MaxGapMs, int Over25, int Over50) _gaps;
    private long _lastPaint;

    public void ResetStats()
    {
        _stats = default;
        _gaps = default;
        _lastPaint = 0;
        _statsStart = Stopwatch.GetTimestamp();
        GapLog.Clear();
    }

    public MapSurface()
    {
        Focusable = false;
        ClipToBounds = true;
        var renderer = Environment.GetEnvironmentVariable("NEBO_RENDERER");
        UsesGpu = renderer != "cpu";
        if (renderer == "hwnd")
        {
            try
            {
                var host = new HwndGlHost { Paint = Paint };
                host.Mouse += OnHostMouse;
                _element = host;
                _host = host;
            }
            catch (Exception ex)
            {
                Log("Native OpenGL window unavailable: " + ex.Message);
            }
        }
        if (UsesGpu && _element is null)
        {
            try
            {
                var gl = new SKGLElement();
                gl.PaintSurface += (_, e) => Paint(e.Surface, e.BackendRenderTarget.Width);
                _element = gl;
                _gl = gl;
            }
            catch (Exception ex)
            {
                Log("OpenGL unavailable, drawing on the CPU: " + ex.Message);
                UsesGpu = false;
            }
        }
        if (!UsesGpu || _element is null)
        {
            var cpu = new SKElement();
            cpu.PaintSurface += (_, e) => Paint(e.Surface, e.Info.Width);
            _element = cpu;
        }
        Child = _element;
        Navigator.Changed += Invalidate;
        Loaded += (_, _) => { Surfaces++; FrameModeChanged += Invalidate; Invalidate(); };
        Unloaded += (_, _) => { Surfaces--; FrameModeChanged -= Invalidate; };
        SizeChanged += (_, _) => Navigator.SetSize(ActualWidth, ActualHeight);
    }

    public void Add(MapLayer layer)
    {
        layer.Surface = this;
        _layers.Add(layer);
        Invalidate();
    }

    /// <summary>Redraw at the next display frame (coalesced); safe from any thread.</summary>
    public void Invalidate()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(Invalidate);
            return;
        }
        if (_queued) return;
        _queued = true;
        _lastTick = Stopwatch.GetTimestamp();
        // Asked to draw after a quiet spell: the time since the last paint was idle, not a stall.
        if (_lastPaint != 0 && Stopwatch.GetElapsedTime(_lastPaint, _lastTick).TotalMilliseconds > 20) _resumed = true;
        // Mouse or keyboard input waiting: the next frame is asked for at Background priority,
        // after the input. (A smooth zoom asking for frame after frame ahead of input kept
        // Windows from handing NEBO the wheel until the zoom ran out; the notches then arrived
        // together, 0.4–1.1 s late.) Otherwise at once: going through Background every time
        // missed every other display refresh (80 of 164 fps).
        if ((GetQueueStatus(QS_INPUT) >> 16) != 0)
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, RequestFrame);
        else
            RequestFrame();
    }

    private const uint QS_INPUT = 0x0407;  // QS_MOUSE | QS_KEY | QS_RAWINPUT

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetQueueStatus(uint flags);

    private void RequestFrame()
    {
        if (_host is not null) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Render, () => OnRendering(null, EventArgs.Empty));
        else CompositionTarget.Rendering += OnRendering;
    }

    /// <summary>NEBO_RENDERER=hwnd: the map's own window, which presents itself (no WPF render
    /// thread); frames are paced by its SwapBuffers waiting for the display.</summary>
    private readonly HwndGlHost? _host;

    private void OnHostMouse(HwndGlHost.MouseKind kind, double px, double py, int delta)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var p = new Point(px / dpi.DpiScaleX, py / dpi.DpiScaleY);
        switch (kind)
        {
            case HwndGlHost.MouseKind.LeftDown:
                _dragging = true;
                _last = p;
                break;
            case HwndGlHost.MouseKind.LeftUp:
                _dragging = false;
                break;
            case HwndGlHost.MouseKind.Move when _dragging:
                if (_inputAt == 0) _inputAt = Stopwatch.GetTimestamp();
                Navigator.PanBy(p.X - _last.X, p.Y - _last.Y);
                _last = p;
                break;
            case HwndGlHost.MouseKind.Wheel:
                WheelLate(_host!.WheelLateMs, delta);
                Wheel(delta / 120.0, p.X, p.Y);
                break;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        _queued = false;
        var now = Stopwatch.GetTimestamp();
        _ticksSincePaint++;
        if (_lastTickAny != 0) _longestTick = Math.Max(_longestTick, Stopwatch.GetElapsedTime(_lastTickAny, now).TotalMilliseconds);
        _lastTickAny = now;
        var dt = Math.Min(0.05, (now - _lastTick) / (double)Stopwatch.Frequency);
        _lastTick = now;
        if (Navigator.IsAnimating)
        {
            Navigator.Animate(dt);  // raises Changed → queued again for the next frame
            if (!Navigator.IsAnimating) Invalidate();
        }
        if (_host is not null) _host.Render();
        else _element.InvalidateVisual();
        if (DrawsContinuously) Invalidate();  // Settings → General → Map frame rate
    }

    private void Paint(SKSurface surface, int pixelWidth)
    {
        var start = Stopwatch.GetTimestamp();
        var scale = ActualWidth > 0 ? (float)(pixelWidth / ActualWidth) : 1f;
        DrawAll(surface.Canvas, scale);
        var drawn = Stopwatch.GetTimestamp();
        surface.Flush();
        var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var flushMs = Stopwatch.GetElapsedTime(drawn).TotalMilliseconds;
        _stats = (_stats.Frames + 1, _stats.TotalMs + ms, Math.Max(_stats.MaxMs, ms));
        AllPaints++;
        AllPaintMs += ms;
        var gc = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        var moving = Navigator.IsAnimating || _dragging;
        TrimGpu(moving);
        // Slow-frame log: a slow paint, a long wait between frames of a running zoom or drag
        // (also when the UI thread was blocked, which the gap statistics below leave out), or a
        // wheel notch that took long to reach the screen.
        var slowOp = UiOps.TakeLongest();
        var uiReply = UiOps.TakeMaxReply();
        // stall: a smooth zoom ran through both frames and still missed frames between them;
        // input: a wheel notch or a drag step that took long to reach the screen. (Idle time
        // between notches, or a held button that does not move, is neither.)
        if (ms > SlowPaintMs && moving) SlowFrame("paint", ms, flushMs, null, gc, slowOp, uiReply);
        else if (_lastPaint != 0 && _animAtPaint && Navigator.IsAnimating && Stopwatch.GetElapsedTime(_lastPaint, start).TotalMilliseconds is var wait and > SlowGapMs)
            SlowFrame("stall", ms, flushMs, wait, gc, slowOp, uiReply);
        else if (_inputAt != 0 && Stopwatch.GetElapsedTime(_inputAt, start).TotalMilliseconds is var lag and > SlowGapMs)
            SlowFrame("input", ms, flushMs, lag, gc, slowOp, uiReply);
        _inputAt = 0;
        _movingAtPaint = moving;
        // Frame rate of each smooth zoom (longer than 0.3 s), into the slow-frame log.
        if (Navigator.IsAnimating)
        {
            if (!_animAtPaint) { _animStart = start; _animFrames = 0; }
            _animFrames++;
        }
        else if (_animAtPaint && Stopwatch.GetElapsedTime(_animStart, start).TotalSeconds is var secs and > 0.3)
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} zoom  {_animFrames / secs:F0} fps ({_animFrames} frames in {secs * 1000:F0} ms), {(_host is not null ? "hwnd" : "wpf")}";
            ThreadPool.QueueUserWorkItem(_ => AppendSlow(line));
        }
        _animAtPaint = Navigator.IsAnimating;
        _paintsAtPaint = AllPaints;
        _paintMsAtPaint = AllPaintMs;
        if (_lastPaint != 0)
        {
            var gap = Stopwatch.GetElapsedTime(_lastPaint, start).TotalMilliseconds;
            if (gap < 250 && !_resumed)
            {
                _gaps = (Math.Max(_gaps.MaxGapMs, gap), _gaps.Over25 + (gap > 25 ? 1 : 0), _gaps.Over50 + (gap > 50 ? 1 : 0));
                // Where a long gap came from: frames asked for (Rendering ticks) but not painted,
                // or the UI thread itself late (ticks far apart), and collections in between.
                if (gap > 40 && GapLog.Count < 40)
                    GapLog.Add($"gap {gap:F0} ms at {Stopwatch.GetElapsedTime(_statsStart, start).TotalMilliseconds:F0} ms, animating {Navigator.IsAnimating}: ticks {_ticksSincePaint} (longest tick gap {_longestTick:F0} ms), " +
                               $"GC gen0/1/2 +{gc.Item1 - _gcAtPaint.Item1}/{gc.Item2 - _gcAtPaint.Item2}/{gc.Item3 - _gcAtPaint.Item3}, paint {ms:F1} ms");
            }
        }
        _lastPaint = start;
        _resumed = false;
        _gcAtPaint = gc;
        _ticksSincePaint = 0;
        _longestTick = 0;
    }

    /// <summary>Diagnosis of long gaps between painted frames (tests).</summary>
    public List<string> GapLog { get; } = [];
    private (int, int, int) _gcAtPaint;
    private int _ticksSincePaint;
    private double _longestTick;
    private long _lastTickAny;
    private long _statsStart;
    private bool _resumed;

    /// <summary>Smooth zoom by wheel notches around a point (mouse wheel, tests).</summary>
    public void Wheel(double notches, double x, double y)
    {
        if (_inputAt == 0) _inputAt = Stopwatch.GetTimestamp();
        Navigator.WheelZoom(notches, x, y);
        Invalidate();
    }

    private void DrawAll(SKCanvas canvas, float scale)
    {
        canvas.Clear(BackColor);
        _frameLayers.Clear();
        if (!Navigator.IsValid) return;
        var frame = new MapFrame(Navigator.View, scale);
        canvas.Save();
        canvas.Scale(scale);
        foreach (var layer in _layers)
        {
            if (!layer.Visible) continue;
            var save = canvas.Save();
            try
            {
                using var perf = RenderStats.Measure(layer.GetType().Name);
                var layerStart = Stopwatch.GetTimestamp();
                using var timed = new LayerTimer(_frameLayers, layer.GetType().Name, layerStart);
                if (layer.Opacity < 1)
                {
                    using var fade = new SKPaint { Color = SKColors.White.WithAlpha((byte)(255 * Math.Clamp(layer.Opacity, 0, 1))) };
                    canvas.SaveLayer(fade);  // restored by RestoreToCount below
                }
                layer.Draw(canvas, frame);
                if (GpuSync && _gl?.GRContext is { } gr)
                {
                    gr.Flush();
                    gr.Submit(true);  // wait for the GPU: the layer's time includes its GPU work
                }
            }
            catch (Exception ex)
            {
                Log($"{layer.GetType().Name}: {ex}");  // one broken layer must not blank the map
            }
            canvas.RestoreToCount(save);
        }
        canvas.Restore();
    }

    /// <summary>The map drawn on the CPU at a device scale (snapshots: an OpenGL surface is not
    /// in WPF's RenderTargetBitmap).</summary>
    public SKImage RenderToImage(float scale)
    {
        int w = Math.Max(1, (int)Math.Round(ActualWidth * scale)), h = Math.Max(1, (int)Math.Round(ActualHeight * scale));
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        DrawAll(surface.Canvas, scale);
        return surface.Snapshot();
    }

    // ---- Mouse: left-drag pans, wheel zooms around the pointer ------------------------------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _dragging = true;
        _last = e.GetPosition(this);
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag();
            return;
        }
        var p = e.GetPosition(this);
        if (_inputAt == 0) _inputAt = Stopwatch.GetTimestamp();
        Navigator.PanBy(p.X - _last.X, p.Y - _last.Y);
        _last = p;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        EndDrag();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _dragging = false;
    }

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var p = e.GetPosition(this);
        WheelLate(unchecked(Environment.TickCount - e.Timestamp), e.Delta);
        Wheel(e.Delta / 120.0, p.X, p.Y);
        e.Handled = true;
    }

    // ---- Slow frames ------------------------------------------------------------------------

    private const double SlowPaintMs = 50, SlowGapMs = 60;

    // ---- Frame rate setting ---------------------------------------------------------------------

    /// <summary>Raised when Settings → General → Map frame rate changes: every map re-evaluates.</summary>
    public static event Action? FrameModeChanged;

    public static void OnFrameModeChanged() => FrameModeChanged?.Invoke();

    /// <summary>Draw the next display frame even if nothing changed: "always", or "hover" while
    /// the mouse is on this map.</summary>
    private bool DrawsContinuously => IsLoaded && IsVisible && AppState.Settings.MapFrames switch
    {
        "always" => true,
        "hover" => IsMouseOver,
        _ => false,
    };

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        if (AppState.Settings.MapFrames == "hover") Invalidate();
    }

    /// <summary>A wheel notch that reached NEBO late (Windows' time stamp of the mouse message →
    /// now): the zoom runs out, then the late notches arrive together and it jumps.</summary>
    private void WheelLate(int lateMs, int delta)
    {
        _wheels++;
        _maxWheelLate = Math.Max(_maxWheelLate, lateMs);
        if (lateMs <= SlowGapMs || Interlocked.Increment(ref _slowLogged) > SlowLogMax) return;
        var line = $"{DateTime.Now:HH:mm:ss.fff} wheel notch late {lateMs} ms (delta {delta}), UI thread answered within {UiOps.PeekMaxReply():F0} ms";
        ThreadPool.QueueUserWorkItem(_ => AppendSlow(line));
    }

    private int _wheels, _maxWheelLate;

    // ---- GPU memory ---------------------------------------------------------------------------

    /// <summary>Skia's GPU cache (textures of tiles, path masks and atlases made while zooming):
    /// capped at 384 MB (a 128 MB cap was too small: it kept rebuilding the same textures while
    /// zooming); kept between zooms.</summary>
    private const long GpuCacheBytes = 384L * 1024 * 1024;
    /// <summary>Diagnosis (NEBO_GPU_SYNC=1): wait for the GPU after each layer, so the slow-frame
    /// log's layer times include the GPU's work, not only the CPU's.</summary>
    private static readonly bool GpuSync = Environment.GetEnvironmentVariable("NEBO_GPU_SYNC") == "1";
    private SKGLElement? _gl;
    private bool _gpuCapped;

    private void TrimGpu(bool moving)
    {
        if ((_gl?.GRContext ?? _host?.GRContext) is not { } gr) return;
        try
        {
            if (!_gpuCapped)
            {
                gr.SetResourceCacheLimit(GpuCacheBytes);
                _gpuCapped = true;
            }
        }
        catch (Exception ex)
        {
            Log("GPU cache: " + ex.Message);
            _gl = null;
        }
    }

    private string GpuCacheText()
    {
        try
        {
            if ((_gl?.GRContext ?? _host?.GRContext) is not { } gr) return "";
            gr.GetResourceCacheUsage(out var count, out var bytes);
            return $", gpu cache {count} items {bytes / 1048576} MB";
        }
        catch (Exception)
        {
            return "";
        }
    }
    private long _inputAt;
    private bool _movingAtPaint, _animAtPaint;
    private long _animStart;
    private int _animFrames;
    /// <summary>Paints of every map surface (all on the UI thread): the slow-frame log tells how
    /// often the other maps drew while this one waited.</summary>
    private static long AllPaints;
    private static double AllPaintMs;
    private long _paintsAtPaint;
    private double _paintMsAtPaint;
    private static int Surfaces;
    private const int SlowLogMax = 300;
    private static int _slowLogged;
    private static readonly object SlowLogLock = new();
    /// <summary>This frame's layer times (DrawAll), for the slow-frame log.</summary>
    private readonly List<(string Layer, double Ms)> _frameLayers = [];

    private readonly struct LayerTimer(List<(string, double)> list, string name, long start) : IDisposable
    {
        public void Dispose() => list.Add((name, Stopwatch.GetElapsedTime(start).TotalMilliseconds));
    }

    /// <summary>A frame that took long to paint, or a long wait between frames, while the map
    /// was zooming or being dragged → one line in %LocalAppData%\NEBO\slow-frames.log (at most
    /// 300 a session): the slowest layers, the GPU submit, collections, memory. Written off the
    /// UI thread.</summary>
    private void SlowFrame(string kind, double paintMs, double flushMs, double? gapMs, (int, int, int) gc, (double Ms, string? Name) slowOp, double uiReply)
    {
        if (Interlocked.Increment(ref _slowLogged) > SlowLogMax) return;
        var others = AllPaints - _paintsAtPaint - 1;
        var othersMs = AllPaintMs - _paintMsAtPaint - paintMs;
        var layers = string.Join(", ", _frameLayers.OrderByDescending(l => l.Ms).Take(4).Select(l => $"{l.Layer} {l.Ms:F1}"));
        var view = Navigator.View;
        var line = $"{DateTime.Now:HH:mm:ss.fff} {kind,-5} paint {paintMs:F1} ms (submit {flushMs:F1})" +
                   (gapMs is { } g ? $", gap {g:F0} ms, ticks {_ticksSincePaint} (longest {_longestTick:F0})" : "") +
                   $", {(_host is not null ? "hwnd" : UsesGpu ? "gpu" : "cpu")} {view.Width:F0}x{view.Height:F0} @ {view.UnitsPerDip:F0} m/dip, {(_dragging ? "drag" : "zoom")}" +
                   $", GC +{gc.Item1 - _gcAtPaint.Item1}/{gc.Item2 - _gcAtPaint.Item2}/{gc.Item3 - _gcAtPaint.Item3}" +
                   $", UI thread answered within {uiReply:F0} ms" +
                   $", other maps painted {others}x {othersMs:F0} ms (maps open {Surfaces})" +
                   $", heap {GC.GetTotalMemory(false) / 1048576} MB{GpuCacheText()}" +
                   (slowOp.Name is { } op ? $", longest UI job {slowOp.Ms:F0} ms {op}" : "") + $" | {layers}";
        ThreadPool.QueueUserWorkItem(_ =>
        {
            using var p = Process.GetCurrentProcess();
            AppendSlow($"{line}, working set {p.WorkingSet64 / 1048576} MB, private {p.PrivateMemorySize64 / 1048576} MB");
        });
    }

    /// <summary>One line into slow-frames.log (worker thread).</summary>
    private static void AppendSlow(string line)
    {
        try
        {
            lock (SlowLogLock)
            {
                Directory.CreateDirectory(AppPaths.Local);
                File.AppendAllText(Path.Combine(AppPaths.Local, "slow-frames.log"), line + Environment.NewLine);
            }
        }
        catch (IOException)
        {
            // Logging must never break drawing.
        }
    }

    // ---- Errors -----------------------------------------------------------------------------

    private static int _logged;

    /// <summary>Appends to %LocalAppData%\NEBO\render-errors.log (at most 20 lines a session);
    /// also used by the airfield views so a drawing fault never takes the app down.</summary>
    internal static void Log(string text)
    {
        if (Interlocked.Increment(ref _logged) > 20) return;
        AppLog.Write("render: " + text);
        try
        {
            var dir = AppPaths.Local;
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "render-errors.log"), $"{DateTime.Now:s} {text}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Logging must never break drawing.
        }
    }
}
