using System.Text.Json.Serialization;

namespace Nebo.Client.Core;

/// <summary>The "chat" message (DLL 0.17): the side's last lines of players' chat, mission
/// announcements, players joining and leaving, and what the side's controllers told pilots.</summary>
public sealed record ChatMessage(
    [property: JsonPropertyName("coalition")] string Coalition,
    [property: JsonPropertyName("lines")] IReadOnlyList<ChatLine> Lines);

/// <summary>One line. <c>Kind</c>: "chat" (all chat), "side" (side chat), "mission" (the
/// mission's on-screen text), "join", "leave", "nebo" (a controller's text to pilots).
/// <c>To</c>: "all", "blue", "red" or a player's name. <c>Time</c>: Unix seconds (UTC).</summary>
public sealed record ChatLine(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("time")] long Time,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("to")] string To,
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("text")] string Text)
{
    public DateTime LocalTime => Time > 0 ? DateTimeOffset.FromUnixTimeSeconds(Time).LocalDateTime : DateTime.Now;
    public bool IsPlayerChat => Kind is "chat" or "side";
    public bool IsJoin => Kind is "join" or "leave";
}

/// <summary>The lines this client has seen (newest last, at most <see cref="Max"/>). The server
/// sends its whole recent log each time; <see cref="Apply"/> keeps the new ones.</summary>
public sealed class ChatLog
{
    public const int Max = 500;
    private readonly List<ChatLine> _lines = [];
    private long _lastId;

    public IReadOnlyList<ChatLine> Lines => _lines;
    public event Action? Changed;

    /// <summary>Adds the lines not seen yet. <c>History</c>: the first message after connecting
    /// (the server's backlog, not news).</summary>
    public (IReadOnlyList<ChatLine> Added, bool History) Apply(ChatMessage m)
    {
        var history = _lastId == 0;
        var added = m.Lines.Where(l => l.Id > _lastId).OrderBy(l => l.Id).ToList();
        if (added.Count == 0) return ([], history);
        _lines.AddRange(added);
        if (_lines.Count > Max) _lines.RemoveRange(0, _lines.Count - Max);
        _lastId = added[^1].Id;
        Changed?.Invoke();
        return (added, history);
    }

    public void Clear()
    {
        _lastId = 0;
        if (_lines.Count == 0) return;
        _lines.Clear();
        Changed?.Invoke();
    }

    private static readonly string[] ControllerWords = ["nebo", "gci", "atc", "awacs", "controller", "magic"];

    /// <summary>Whether a player's chat line speaks to controllers: it has the controller's
    /// callsign or a word like GCI, ATC, AWACS, NEBO (whole words, any case).</summary>
    public static bool ForControllers(ChatLine l, string? callsign)
    {
        if (!l.IsPlayerChat) return false;
        var words = SplitWords(l.Text);
        if (!string.IsNullOrWhiteSpace(callsign))
        {
            var own = SplitWords(callsign);
            if (own.Count > 0 && ContainsRun(words, own)) return true;
        }
        return words.Any(w => ControllerWords.Contains(w));
    }

    private static List<string> SplitWords(string s) =>
        [.. s.ToLowerInvariant().Split(s.Where(c => !char.IsLetterOrDigit(c)).Distinct().ToArray(), StringSplitOptions.RemoveEmptyEntries)];

    private static bool ContainsRun(List<string> words, List<string> run)
    {
        for (var i = 0; i + run.Count <= words.Count; i++)
            if (run.Select((w, k) => words[i + k] == w).All(x => x)) return true;
        return false;
    }
}

/// <summary>When a pilot enters another controller's area: fed every few seconds with the other
/// controller whose area holds the pilot (null when in none, or also in one of ours), it answers
/// that controller once on the way in. Leaving (or entering ours) and coming back asks again.</summary>
public sealed class AreaEntries
{
    private readonly Dictionary<long, long?> _last = [];

    /// <summary>The controller to offer the pilot to now, or null.</summary>
    public long? Entered(long unit, long? otherArea)
    {
        _last.TryGetValue(unit, out var was);
        _last[unit] = otherArea;
        return otherArea is not null && otherArea != was ? otherArea : null;
    }

    /// <summary>Forgets pilots no longer flying for us.</summary>
    public void Keep(IEnumerable<long> units)
    {
        var keep = units.ToHashSet();
        foreach (var u in _last.Keys.Where(u => !keep.Contains(u)).ToList()) _last.Remove(u);
    }
}
