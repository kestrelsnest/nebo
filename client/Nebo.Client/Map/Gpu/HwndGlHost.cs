using System.Runtime.InteropServices;
using System.Windows.Interop;
using SkiaSharp;

namespace Nebo.Client.Map.Gpu;

/// <summary>Test renderer (NEBO_RENDERER=hwnd): Skia on OpenGL in a native child window that
/// presents itself (SwapBuffers), bypassing WPF's render thread and D3DImage. WPF elements cannot
/// draw over it (airspace), and WPF gets no mouse input from it: the window forwards the mouse to
/// <see cref="Mouse"/>.</summary>
public sealed class HwndGlHost : HwndHost
{
    public enum MouseKind { Move, LeftDown, LeftUp, Wheel }

    /// <summary>Mouse in the window: kind, position in device pixels, wheel delta (120 a notch).</summary>
    public event Action<MouseKind, double, double, int>? Mouse;

    /// <summary>Draws a frame: the surface and its width in device pixels.</summary>
    public Action<SKSurface, int>? Paint { get; set; }

    public GRContext? GRContext => _gr;

    /// <summary>How long the last wheel message waited in the queue (Windows' time stamp → now).</summary>
    public int WheelLateMs { get; private set; }

    private IntPtr _hwnd, _hdc, _hglrc;
    private GRContext? _gr;
    private GRBackendRenderTarget? _target;
    private SKSurface? _surface;
    private int _w, _h;
    private static ushort _class;

    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        if (_class == 0)
        {
            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                style = CS_OWNDC | CS_HREDRAW | CS_VREDRAW,
                lpfnWndProc = GetProcAddress(GetModuleHandle("user32.dll"), "DefWindowProcW"),
                hInstance = GetModuleHandle(null),
                lpszClassName = "NeboGlMap",
            };
            _class = RegisterClassEx(ref wc);
        }
        _hwnd = CreateWindowEx(0, "NeboGlMap", "", WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
            0, 0, 1, 1, parent.Handle, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        _hdc = GetDC(_hwnd);
        var pfd = new PIXELFORMATDESCRIPTOR
        {
            nSize = (ushort)Marshal.SizeOf<PIXELFORMATDESCRIPTOR>(),
            nVersion = 1,
            dwFlags = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER,
            cColorBits = 32,
            cAlphaBits = 8,
            cStencilBits = 8,
        };
        var format = ChoosePixelFormat(_hdc, ref pfd);
        SetPixelFormat(_hdc, format, ref pfd);
        _hglrc = wglCreateContext(_hdc);
        wglMakeCurrent(_hdc, _hglrc);
        var swap = wglGetProcAddress("wglSwapIntervalEXT");
        if (swap != IntPtr.Zero) Marshal.GetDelegateForFunctionPointer<SwapInterval>(swap)(1);
        _gr = GRContext.CreateGl(GRGlInterface.Create());
        return new HandleRef(this, _hwnd);
    }

    private delegate bool SwapInterval(int interval);

    /// <summary>Draws and presents a frame now (waits for the display's next refresh).</summary>
    public void Render()
    {
        if (_gr is null || Paint is null || _hwnd == IntPtr.Zero) return;
        GetClientRect(_hwnd, out var rc);
        int w = Math.Max(1, rc.Right - rc.Left), h = Math.Max(1, rc.Bottom - rc.Top);
        wglMakeCurrent(_hdc, _hglrc);
        if (_surface is null || w != _w || h != _h)
        {
            _surface?.Dispose();
            _target?.Dispose();
            _target = new GRBackendRenderTarget(w, h, 0, 8, new GRGlFramebufferInfo(0, 0x8058 /* GL_RGBA8 */));
            _surface = SKSurface.Create(_gr, _target, GRSurfaceOrigin.BottomLeft, SKColorType.Rgba8888);
            (_w, _h) = (w, h);
        }
        Paint(_surface, w);
        SwapBuffers(_hdc);
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_ERASEBKGND:
                handled = true;
                return 1;
            case WM_PAINT:
                ValidateRect(hwnd, IntPtr.Zero);
                Render();
                handled = true;
                return IntPtr.Zero;
            case WM_MOUSEMOVE:
                Mouse?.Invoke(MouseKind.Move, X(lParam), Y(lParam), 0);
                break;
            case WM_LBUTTONDOWN:
                SetCapture(hwnd);
                Mouse?.Invoke(MouseKind.LeftDown, X(lParam), Y(lParam), 0);
                break;
            case WM_LBUTTONUP:
                ReleaseCapture();
                Mouse?.Invoke(MouseKind.LeftUp, X(lParam), Y(lParam), 0);
                break;
            case WM_MOUSEWHEEL:
                var pt = new POINT { X = X(lParam), Y = Y(lParam) };  // screen coordinates
                ScreenToClient(hwnd, ref pt);
                WheelLateMs = unchecked(Environment.TickCount - GetMessageTime());
                Mouse?.Invoke(MouseKind.Wheel, pt.X, pt.Y, (short)((long)wParam >> 16));
                handled = true;
                return IntPtr.Zero;
        }
        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    private static int X(IntPtr l) => (short)((long)l & 0xFFFF);
    private static int Y(IntPtr l) => (short)(((long)l >> 16) & 0xFFFF);

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        wglMakeCurrent(_hdc, _hglrc);
        _surface?.Dispose();
        _target?.Dispose();
        _gr?.Dispose();
        _surface = null;
        _gr = null;
        wglMakeCurrent(IntPtr.Zero, IntPtr.Zero);
        wglDeleteContext(_hglrc);
        ReleaseDC(hwnd.Handle, _hdc);
        DestroyWindow(hwnd.Handle);
        _hwnd = IntPtr.Zero;
    }

    // ---- Win32 / WGL ----------------------------------------------------------------------------

    private const int WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_CLIPSIBLINGS = 0x04000000, WS_CLIPCHILDREN = 0x02000000;
    private const int CS_OWNDC = 0x20, CS_HREDRAW = 0x2, CS_VREDRAW = 0x1;
    private const uint PFD_DRAW_TO_WINDOW = 4, PFD_SUPPORT_OPENGL = 0x20, PFD_DOUBLEBUFFER = 1;
    private const int WM_PAINT = 0x0F, WM_ERASEBKGND = 0x14, WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_MOUSEWHEEL = 0x20A;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public int style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PIXELFORMATDESCRIPTOR
    {
        public ushort nSize, nVersion;
        public uint dwFlags;
        public byte iPixelType, cColorBits, cRedBits, cRedShift, cGreenBits, cGreenShift, cBlueBits, cBlueShift,
            cAlphaBits, cAlphaShift, cAccumBits, cAccumRedBits, cAccumGreenBits, cAccumBlueBits, cAccumAlphaBits,
            cDepthBits, cStencilBits, cAuxBuffers, iLayerType, bReserved;
        public uint dwLayerMask, dwVisibleMask, dwDamageMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateWindowEx(int exStyle, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out RECT rc);
    [DllImport("user32.dll")] private static extern bool ValidateRect(IntPtr hwnd, IntPtr rc);
    [DllImport("user32.dll")] private static extern IntPtr SetCapture(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hwnd, ref POINT p);
    [DllImport("user32.dll")] private static extern int GetMessageTime();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("gdi32.dll")] private static extern int ChoosePixelFormat(IntPtr hdc, ref PIXELFORMATDESCRIPTOR pfd);
    [DllImport("gdi32.dll")] private static extern bool SetPixelFormat(IntPtr hdc, int format, ref PIXELFORMATDESCRIPTOR pfd);
    [DllImport("gdi32.dll")] private static extern bool SwapBuffers(IntPtr hdc);
    [DllImport("opengl32.dll")] private static extern IntPtr wglCreateContext(IntPtr hdc);
    [DllImport("opengl32.dll")] private static extern bool wglMakeCurrent(IntPtr hdc, IntPtr hglrc);
    [DllImport("opengl32.dll")] private static extern bool wglDeleteContext(IntPtr hglrc);
    [DllImport("opengl32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr wglGetProcAddress(string name);
}
