namespace Nebo.Client.Core;

/// <summary>How NEBO uses the controller's ten SRS radios ("channels"): 4–6 are ATC channels
/// (picking up a pilot tunes one to their airfield's tower; 6 is also the ATC tab's), 7–10 belong
/// to packages 1–4, 1–3 are free.</summary>
public static class ChannelPlan
{
    public const int Count = 10, FirstAtc = 4, Atc = 6, FirstPackage = 7;

    /// <summary>The channel kept for package <paramref name="number"/> (1–4).</summary>
    public static int ForPackage(int number) => FirstPackage + number - 1;

    /// <summary>The package a channel belongs to, or 0.</summary>
    public static int PackageOf(int channel) => channel >= FirstPackage && channel <= Count ? channel - FirstPackage + 1 : 0;

    /// <summary>One of the ATC channels (4–6).</summary>
    public static bool IsAtc(int channel) => channel >= FirstAtc && channel <= Atc;

    /// <summary>"ATC", "P1"… or "" for a free channel.</summary>
    public static string Role(int channel) => IsAtc(channel) ? "ATC" : PackageOf(channel) is > 0 and var p ? $"P{p}" : "";

    /// <summary>Channels the controller may use for anything (1–3).</summary>
    public static IEnumerable<int> Free => Enumerable.Range(1, Count).Where(c => !IsAtc(c) && PackageOf(c) == 0);

    /// <summary>The ATC channel for a tower frequency: the one already on it, else an empty one,
    /// else the one used longest ago (<paramref name="lastUsed"/>: never used counts as oldest).</summary>
    public static int ChooseAtc(Func<int, double?> tunedMhz, double mhz, IReadOnlyDictionary<int, DateTime> lastUsed)
    {
        var atc = Enumerable.Range(FirstAtc, Atc - FirstAtc + 1).ToList();
        foreach (var ch in atc)
            if (tunedMhz(ch) is { } f && Math.Abs(f - mhz) < 0.0005) return ch;
        foreach (var ch in atc)
            if (tunedMhz(ch) is null) return ch;
        return atc.OrderBy(ch => lastUsed.TryGetValue(ch, out var t) ? t : DateTime.MinValue).First();
    }
}
