using System.Windows.Input;

namespace Nebo.Client.Input;

/// <summary>A key or mouse button with modifiers, as the controller binds it: "B", "Ctrl+B",
/// "Mouse:Middle", "Mouse:XButton1", "Alt+Mouse:Left". Plain left button is never accepted for an
/// action (it selects and pans the map).</summary>
public sealed record Gesture(Key? Key, MouseButton? Button, ModifierKeys Modifiers)
{
    public static Gesture? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var mods = ModifierKeys.None;
        foreach (var m in parts[..^1])
        {
            if (!Enum.TryParse<ModifierKeys>(m == "Ctrl" ? "Control" : m, true, out var mod)) return null;
            mods |= mod;
        }
        var last = parts[^1];
        if (last.StartsWith("Mouse:", StringComparison.OrdinalIgnoreCase))
            return Enum.TryParse<MouseButton>(last[6..], true, out var b) ? new Gesture(null, b, mods) : null;
        return Enum.TryParse<Key>(last, true, out var k) ? new Gesture(k, null, mods) : null;
    }

    public override string ToString()
    {
        var mods = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) mods.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) mods.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) mods.Add("Alt");
        mods.Add(Key is { } k ? k.ToString() : $"Mouse:{Button}");
        return string.Join("+", mods);
    }

    /// <summary>How it reads in the settings window: "Alt + left click", "Mouse button 4".</summary>
    public string Display => (Modifiers == ModifierKeys.None ? "" : ToString()[..ToString().LastIndexOf('+')].Replace("+", " + ") + " + ")
        + (Key is { } k ? k.ToString() : Button switch
        {
            MouseButton.Left => "left click", MouseButton.Right => "right button", MouseButton.Middle => "middle button",
            MouseButton.XButton1 => "mouse button 4", MouseButton.XButton2 => "mouse button 5", _ => Button.ToString() ?? "",
        });

    public bool IsMouse => Button is not null;

    public bool Matches(KeyEventArgs e)
    {
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        return Key == key && Keyboard.Modifiers == Modifiers;
    }

    public bool Matches(MouseButtonEventArgs e) => Button == e.ChangedButton && Keyboard.Modifiers == Modifiers;

    /// <summary>Released: only the key or button counts (modifiers may already be up).</summary>
    public bool ReleasedBy(KeyEventArgs e) => Key == (e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key);

    public bool ReleasedBy(MouseButtonEventArgs e) => Button == e.ChangedButton;
}

/// <summary>The bindable actions, their defaults and the controller's choices (settings.json).</summary>
public static class Bindings
{
    public const string Bullseye = "Bullseye", Rwr = "RwrScope", Nctr = "Nctr", PointInfo = "PointInfo", Ruler = "Ruler", DtcMode = "DtcMode", LaunchJump = "LaunchJump";

    public static readonly IReadOnlyList<(string Action, string Name, string Default, string Hint)> All =
    [
        (Bullseye, "Bullseye call at the pointer", "B", "hold or toggle (below)"),
        (Rwr, "RWR scope", "R", "over an own unit, or the selected one"),
        (Nctr, "NCTR area", "N", "then drag a box on the map"),
        (PointInfo, "Point coordinates", "Alt+Mouse:Left", "DDM, DMS, MGRS, bullseye, elevation"),
        (Ruler, "Ruler (drag)", "Mouse:Right", "a mouse button; right-click still opens the menu"),
        (DtcMode, "DTC: add / delete points", "D", "switches what a right-click on the DTC map does"),
        (LaunchJump, "Jump to a missile launch", "Space", "at a player: the newest, then the older ones"),
    ];

    private static AppSettings S => AppState.Settings;

    public static Gesture Get(string action)
    {
        if (S.Bindings.TryGetValue(action, out var text) && Gesture.Parse(text) is { } g) return g;
        // Older settings files kept the three keys on their own.
        var legacy = action switch { Bullseye => S.BullseyeKey, Rwr => S.RwrKey, Nctr => S.NctrKey, _ => null };
        return Gesture.Parse(legacy) ?? Gesture.Parse(All.First(a => a.Action == action).Default)!;
    }

    public static void Set(string action, Gesture gesture) => S.Bindings[action] = gesture.ToString();

    public static void ResetAll()
    {
        S.Bindings.Clear();
        S.BullseyeKey = "B";
        S.RwrKey = "R";
        S.NctrKey = "N";
    }
}
