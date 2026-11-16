using System.Diagnostics;
using System.Windows.Automation;

namespace Nebo.Client.Srs;

/// <summary>Logs the controller's own SRS app (SR-ClientRadio 2.x) into External AWACS Mode by
/// driving its window through UI Automation, since SRS has no command line or setting for it:
/// server address, Connect, EAM password and name, Connect External AWACS Mode. Runs once per NEBO
/// connection, off the UI thread (SrsSettings.AutoEam). The states are read from which boxes
/// SRS disables (address box: connected; EAM password box: EAM connected), not from button
/// texts, so SRS's language does not matter. An SRS connected to another server is left alone.
/// Control names are those of SRS 2.4 (MainWindow.xaml); if a later SRS renames them, this
/// stops with a message and the controller clicks EAM by hand as before.</summary>
public static class SrsAutoConnect
{
    private const string ProcessName = "SR-ClientRadio";
    private static int _running;

    /// <summary>The last result, for the SRS panel.</summary>
    public static string Status { get; private set; } = "";
    public static event Action? Changed;

    /// <summary>Starts one attempt unless one runs; host and port are the SRS server's.</summary>
    public static void Start(string host, int port, string password, string name)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1) return;
        _ = Task.Run(() =>
        {
            try { Report(Run(host, port, password, name)); }
            catch (Exception ex) { Report($"SRS auto-connect stopped: {ex.Message} Click External AWACS Mode in SRS yourself."); }
            finally { Interlocked.Exchange(ref _running, 0); }
        });
    }

    private static void Report(string status)
    {
        Status = status;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Changed?.Invoke());
    }

    private static string Run(string host, int port, string password, string name)
    {
        var target = $"{host}:{port}";
        Report("SRS auto-connect: waiting for the SRS window…");
        var window = WaitFor(FindWindow, TimeSpan.FromSeconds(30));
        if (window is null) return "SRS auto-connect: no SRS window (is the SRS app running?).";

        // WPF shows only the open tab's controls: open the one holding the address box (General),
        // and the controller's tab again afterwards.
        var tabs = window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem))
            .Cast<AutomationElement>().ToList();
        var openTab = tabs.FirstOrDefault(t => t.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sp) && ((SelectionItemPattern)sp).Current.IsSelected);
        try
        {
            return Connect(window, tabs, target, host, port, password, name);
        }
        finally
        {
            if (openTab is not null && openTab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sp)) ((SelectionItemPattern)sp).Select();
        }
    }

    private static string Connect(AutomationElement window, List<AutomationElement> tabs, string target, string host, int port, string password, string name)
    {
        var address = ById(window, "ServerIp");
        foreach (var tab in tabs)
        {
            if (address is not null) break;
            if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sp)) ((SelectionItemPattern)sp).Select();
            address = WaitFor(() => ById(window, "ServerIp"), TimeSpan.FromSeconds(1));
        }
        var startStop = address is null ? null : ById(window, "StartStop");
        if (address is null || startStop is null) return Unknown("ServerIp / StartStop");

        if (address.Current.IsEnabled)
        {
            // Not connected: the address, then Connect. The address box hands its text to SRS when
            // it loses focus, so focus moves to the Connect button first.
            SetValue(address, target);
            startStop.SetFocus();
            Invoke(startStop);
            Report($"SRS auto-connect: connecting SRS to {target}…");
            if (WaitFor(() => address.Current.IsEnabled ? null : address, TimeSpan.FromSeconds(20)) is null)
                return $"SRS auto-connect: SRS did not connect to {target}.";
        }
        else if (!SameServer(Value(address), host, port))
        {
            return $"SRS is connected to {Value(address)}, not {target}: disconnect it there for EAM to connect automatically.";
        }

        var expander = WaitFor(() => ById(window, "EAMExpander"), TimeSpan.FromSeconds(5));
        if (expander is null) return Unknown("EAMExpander");
        if (expander.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ep) &&
            ((ExpandCollapsePattern)ep).Current.ExpandCollapseState != ExpandCollapseState.Expanded)
            ((ExpandCollapsePattern)ep).Expand();

        var pw = WaitFor(() => ById(window, "ExternalAWACSModePassword"), TimeSpan.FromSeconds(5));
        var nameBox = ById(window, "ExternalAWACSModeName");
        var connect = ById(window, "ConnectExternalAWACSMode");
        if (pw is null || nameBox is null || connect is null) return Unknown("EAM password / name / connect");
        if (!pw.Current.IsEnabled) return "SRS app connected in External AWACS Mode.";

        // EAM can be pressed once SRS has the server's settings (the server allows EAM).
        if (WaitFor(() => connect.Current.IsEnabled ? connect : null, TimeSpan.FromSeconds(15)) is null)
            return "SRS auto-connect: the SRS server does not offer External AWACS Mode.";
        SetValue(pw, password);
        if (Value(nameBox).Trim().Length == 0) SetValue(nameBox, name);
        else nameBox.SetFocus();  // takes focus off the password box: SRS reads it on LostFocus
        connect.SetFocus();
        Invoke(connect);
        Report("SRS auto-connect: connecting External AWACS Mode…");
        return WaitFor(() => pw.Current.IsEnabled ? null : pw, TimeSpan.FromSeconds(15)) is not null
            ? "SRS app connected in External AWACS Mode (automatically)."
            : "SRS auto-connect: External AWACS Mode did not connect (wrong EAM password?).";
    }

    private static string Unknown(string what) =>
        $"SRS auto-connect: this SRS version has no {what}; click External AWACS Mode in SRS yourself.";

    private static bool SameServer(string current, string host, int port)
    {
        var c = current.Trim();
        return c.Equals($"{host}:{port}", StringComparison.OrdinalIgnoreCase) ||
               (port == 5002 && c.Equals(host, StringComparison.OrdinalIgnoreCase));  // SRS's default port
    }

    private static AutomationElement? FindWindow()
    {
        foreach (var p in Process.GetProcessesByName(ProcessName))
        {
            p.Refresh();
            if (p.MainWindowHandle != IntPtr.Zero) return AutomationElement.FromHandle(p.MainWindowHandle);
        }
        return null;
    }

    private static AutomationElement? ById(AutomationElement root, string id) =>
        root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id));

    private static string Value(AutomationElement e) =>
        e.TryGetCurrentPattern(ValuePattern.Pattern, out var vp) ? ((ValuePattern)vp).Current.Value ?? "" : "";

    private static void SetValue(AutomationElement e, string value)
    {
        e.SetFocus();
        ((ValuePattern)e.GetCurrentPattern(ValuePattern.Pattern)).SetValue(value);
    }

    private static void Invoke(AutomationElement e) =>
        ((InvokePattern)e.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    private static T? WaitFor<T>(Func<T?> probe, TimeSpan timeout) where T : class
    {
        var until = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                if (probe() is { } found) return found;
            }
            catch (ElementNotAvailableException) { /* window rebuilding: try again */ }
            if (DateTime.UtcNow >= until) return null;
            Thread.Sleep(250);
        }
    }
}
