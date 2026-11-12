using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Nebo.Client;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        AppPaths.MigrateFromDcb();  // before anything reads settings
        AppLog.Start();
        // Crashes go to nebo.log with their stack; what happens next is unchanged.
        DispatcherUnhandledException += (_, a) => AppLog.Error("crash (UI thread)", a.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, a) => AppLog.Write($"ERROR crash: {a.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, a) => AppLog.Error("unobserved task", a.Exception);
        // Diagnosis: NEBO_WPF=software draws the windows without the GPU (with NEBO_RENDERER=cpu).
        if (Environment.GetEnvironmentVariable("NEBO_WPF") == "software")
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        base.OnStartup(e);
        Themes.ThemeManager.Initialize();
        LaunchAlarm.Start();
        EngagementAlert.Start();
        FlightDesk.Start();
        UiOps.Start(Dispatcher);
        TableColumns.Start();
        Srs.SrsChannels.Start();
        Integration.DeckLink.Start();
        // Data cartridge tables come from the controller's own DCS install (made on first use).
        Core.Dtc.DtcData.Configure(Map.Charts.DcsInstall.Find, AppPaths.Settings);
        // The bullseye key works in every window: the main one and floated map windows alike.
        EventManager.RegisterClassHandler(typeof(Window), Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnKey));
        EventManager.RegisterClassHandler(typeof(Window), Keyboard.PreviewKeyUpEvent, new KeyEventHandler(OnKey));
        // Actions bound to mouse buttons (middle, 4, 5…) work in every window as keys do.
        EventManager.RegisterClassHandler(typeof(Window), Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnMouse));
        EventManager.RegisterClassHandler(typeof(Window), Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(OnMouse));
        Deactivated += (_, _) => { if (!AppState.Settings.BullseyeToggle) AppState.SetBullsKeyHeld(false); };  // key released while elsewhere
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Stop($"code {e.ApplicationExitCode}");
        base.OnExit(e);
    }

    private static bool Typing => Keyboard.FocusedElement is TextBoxBase or PasswordBox;

    private static int? EngagementMarkerKey(Key k) => k switch
    {
        >= Key.D1 and <= Key.D8 => k - Key.D1 + 1,
        >= Key.NumPad1 and <= Key.NumPad8 => k - Key.NumPad1 + 1,
        _ => null,
    };

    private static void OnKey(object sender, KeyEventArgs e)
    {
        // Alt released after an Alt+click on a map: not a menu request.
        if (!e.IsDown && e.Key == Key.System && e.SystemKey is Key.LeftAlt or Key.RightAlt && AppState.AltClicked)
        {
            AppState.AltClicked = false;
            e.Handled = true;
            return;
        }
        if (Input.KeyCapture.Active) return;  // the key bindings window is listening
        var down = e.IsDown && !e.IsRepeat && !Typing;
        // The RWR scope on the map under the pointer (MapPanel decides on which unit).
        if (down && Input.Bindings.Get(Input.Bindings.Rwr).Matches(e))
        {
            if (AppState.RequestRwrClock()) e.Handled = true;
            return;
        }
        // NCTR: drag an area on the map under the pointer.
        if (down && Input.Bindings.Get(Input.Bindings.Nctr).Matches(e))
        {
            if (AppState.RequestNctr()) e.Handled = true;
            return;
        }
        if (down && Input.Bindings.Get(Input.Bindings.PointInfo) is { IsMouse: false } point && point.Matches(e))
        {
            if (AppState.RequestPointInfo()) e.Handled = true;
            return;
        }
        if (down && Input.Bindings.Get(Input.Bindings.DtcMode) is { IsMouse: false } dtc && dtc.Matches(e))
        {
            if (ToggleDtcMode()) e.Handled = true;
            return;
        }
        // 1–8: the engagement line carrying that marker, both ends in view on the map last used
        // (no line with it: the key does what it does). The Stream Deck buttons send these.
        if (down && Keyboard.Modifiers == ModifierKeys.None && EngagementMarkerKey(e.Key) is { } marker
            && AppState.Engagements.WithMarker(marker) is { } line && AppState.ActiveMap is { } map)
        {
            map.FitEngagement(line);
            e.Handled = true;
            return;
        }
        // A missile launch at a player: centre on it (nothing in flight: the key does what it does).
        if (down && Input.Bindings.Get(Input.Bindings.LaunchJump) is { IsMouse: false } jump && jump.Matches(e))
        {
            if (LaunchAlarm.Jump()) e.Handled = true;
            return;
        }
        var bulls = Input.Bindings.Get(Input.Bindings.Bullseye);
        if (e.IsDown ? bulls.Matches(e) && !Typing : bulls.ReleasedBy(e))
        {
            Bullseye(e.IsDown, e.IsRepeat);
            e.Handled = true;
        }
    }

    private static void OnMouse(object sender, MouseButtonEventArgs e)
    {
        if (Input.KeyCapture.Active) return;
        var down = e.ButtonState == MouseButtonState.Pressed;
        if (down && Input.Bindings.Get(Input.Bindings.Rwr) is { IsMouse: true } rwr && rwr.Matches(e))
        {
            if (AppState.RequestRwrClock()) e.Handled = true;
            return;
        }
        if (down && Input.Bindings.Get(Input.Bindings.Nctr) is { IsMouse: true } nctr && nctr.Matches(e))
        {
            if (AppState.RequestNctr()) e.Handled = true;
            return;
        }
        if (down && Input.Bindings.Get(Input.Bindings.DtcMode) is { IsMouse: true } dtc && dtc.Matches(e))
        {
            if (ToggleDtcMode()) e.Handled = true;
            return;
        }
        if (down && Input.Bindings.Get(Input.Bindings.LaunchJump) is { IsMouse: true } jump && jump.Matches(e))
        {
            if (LaunchAlarm.Jump()) e.Handled = true;
            return;
        }
        if (Input.Bindings.Get(Input.Bindings.Bullseye) is { IsMouse: true } bulls && (down ? bulls.Matches(e) : bulls.ReleasedBy(e)))
        {
            Bullseye(down, false);
            e.Handled = true;
        }
    }

    /// <summary>The DTC map's right-click: add ↔ delete, while a DTC editor is open.</summary>
    private static bool ToggleDtcMode()
    {
        if (!Panels.DtcPanel.IsOpen) return false;
        Dtc.DtcSession.Instance.SetDeleteMode(!Dtc.DtcSession.Instance.DeleteMode);
        return true;
    }

    /// <summary>Hold: shown while held. Toggle: each press switches it.</summary>
    private static void Bullseye(bool down, bool repeat)
    {
        if (AppState.Settings.BullseyeToggle)
        {
            if (down && !repeat) AppState.SetBullsKeyHeld(!AppState.BullsKeyHeld);
        }
        else AppState.SetBullsKeyHeld(down);
    }
}
