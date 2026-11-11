namespace Nebo.Client.Core;

/// <summary>The controller's own short tags on objects ("LEAD", "NO SRS", "BINGO"): up to
/// <see cref="MaxLength"/> characters, shown in the object's data block on every map. This client
/// only. A player's tag belongs to the player (their name), so it follows them through respawns
/// and is kept between sessions; any other object's belongs to its unit for the session (unit
/// ids start over with every mission).</summary>
public sealed class ObjectTags
{
    public const int MaxLength = 10;

    private readonly Dictionary<string, string> _tags = [];
    public event Action? Changed;

    /// <summary>The key a unit's tag is kept under: "p:&lt;player&gt;" for an own player (contacts
    /// carry no names), else "u:&lt;unit id&gt;".</summary>
    public static string KeyOf(Unit u) => !u.Detected && u.Player is { Length: > 0 } p ? "p:" + p : "u:" + u.Id;

    public string? Get(Unit u) => _tags.TryGetValue(KeyOf(u), out var t) ? t : null;

    /// <summary>Sets or (empty / null) clears a unit's tag; longer text is cut to MaxLength.</summary>
    public void Set(Unit u, string? text)
    {
        var key = KeyOf(u);
        var tag = (text ?? "").Trim();
        if (tag.Length > MaxLength) tag = tag[..MaxLength];
        if (tag.Length == 0 ? !_tags.Remove(key) : _tags.TryGetValue(key, out var old) && old == tag) return;
        if (tag.Length > 0) _tags[key] = tag;
        Changed?.Invoke();
    }

    /// <summary>The tags kept between sessions: players' only.</summary>
    public Dictionary<string, string> Persistent() => _tags.Where(kv => kv.Key.StartsWith("p:", StringComparison.Ordinal)).ToDictionary();

    /// <summary>Players' tags from the settings (anything else is ignored).</summary>
    public void Load(IReadOnlyDictionary<string, string>? saved)
    {
        foreach (var (k, v) in saved ?? new Dictionary<string, string>())
            if (k.StartsWith("p:", StringComparison.Ordinal) && v.Trim() is { Length: > 0 } t) _tags[k] = t.Length > MaxLength ? t[..MaxLength] : t;
    }
}
